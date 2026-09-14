using JasperFx.Events.Daemon;

using Marten;
using Marten.Storage;

using Microsoft.Extensions.Logging;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Services.Projections;

/// <summary>
/// The running daemon, or the reason there is not one here.
/// </summary>
/// <param name="State">Whether a daemon was reachable.</param>
/// <param name="Daemon">The daemon, when there is one. Never touched when <paramref name="State" /> is not <c>Hosted</c>.</param>
/// <param name="Explanation">Plain words, rendered on the card.</param>
/// <param name="Databases">
/// Every database of this store, by <c>DatabaseId.Identity</c> - the set a coordinator pause would
/// reach. Enumerated on the way to the daemon rather than asked for a second time, because
/// <c>IMartenStorage.AllDatabases()</c> is a query on a master-table tenancy.
/// </param>
internal sealed record DaemonHosting(
    DaemonHostingState State,
    IProjectionDaemon? Daemon,
    string Explanation,
    IReadOnlyList<string>? Databases = null)
{
    /// <summary>The daemon answered.</summary>
    public static DaemonHosting Hosted(IProjectionDaemon daemon, IReadOnlyList<string> databases) =>
        new(DaemonHostingState.Hosted, daemon, "The async daemon is hosted in this process.", databases);

    /// <summary>There is no daemon here, and this is why.</summary>
    public static DaemonHosting NotHosted(string explanation) =>
        new(DaemonHostingState.NotHostedInThisProcess, null, explanation);

    /// <summary>The daemon, or <see langword="null" />.</summary>
    public bool TryGetDaemon(out IProjectionDaemon daemon)
    {
        daemon = Daemon!;
        return State == DaemonHostingState.Hosted && Daemon is not null;
    }
}

/// <summary>
/// Finds the daemon the host is already running, and never starts one.
/// </summary>
/// <remarks>
/// <para>
/// The only supported way to reach a running daemon is the coordinator the host's own
/// <c>AddAsyncDaemon()</c> registered (AGENTS.md hard rule 11).
/// <c>store.BuildProjectionDaemonAsync()</c> would build a <em>second</em> daemon beside it, and the two
/// then contend for the same advisory locks until one of them hangs - which is a hang in the host's
/// application, caused by opening an admin page.
/// </para>
/// <para>
/// The absence of a coordinator is therefore a value and not a failure: an application whose daemon runs
/// in a different process is a supported deployment, and the projections page still renders every
/// progress row out of the database. Only the buttons go away.
/// </para>
/// <para>
/// Which service to ask for depends on how the store was registered, and the two are not
/// interchangeable. <c>AddMarten().AddAsyncDaemon()</c> registers the non-generic
/// <c>Marten.Events.Daemon.Coordination.IProjectionCoordinator</c>;
/// <c>AddMartenStore&lt;T&gt;().AddAsyncDaemon()</c> registers <em>only</em>
/// <c>IProjectionCoordinator&lt;T&gt;</c>, so asking for the non-generic one on an ancillary store's
/// behalf would silently hand back the main store's daemon - or nothing at all. That is the one place
/// this codebase calls <c>MakeGenericType</c>.
/// </para>
/// </remarks>
internal sealed class DaemonAccessor
{
    /// <summary>What the card says when no coordinator is registered for this store.</summary>
    public const string NotRegisteredExplanation =
        "No async daemon is hosted in this process. Marten Studio reads projection progress straight from " +
        "the database, but it cannot start, stop or rebuild anything from here. Add " +
        "AddAsyncDaemon(DaemonMode.Solo) (or HotCold) to this application's AddMarten chain to host one, " +
        "or drive the daemon from the process that already runs it.";

    private readonly IServiceProvider provider;
    private readonly ILogger<DaemonAccessor> logger;

    public DaemonAccessor(IServiceProvider provider, ILogger<DaemonAccessor> logger)
    {
        this.provider = provider;
        this.logger = logger;
    }

    /// <summary>
    /// The service type whose registration means "this process hosts a daemon for that store".
    /// </summary>
    /// <remarks>
    /// Separated from the resolution so a test can assert the decision without building a store: what
    /// matters here is the shape of the registration, and building one opens a connection.
    /// </remarks>
    /// <param name="registration">The store the studio is about.</param>
    /// <returns>The coordinator service type, or <see langword="null" /> when none could be formed.</returns>
    public static Type? CoordinatorServiceType(MartenStoreRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (registration.ServiceType == typeof(IDocumentStore))
        {
            return typeof(MartenCoordinator);
        }

        try
        {
            return typeof(Marten.Events.Daemon.Coordination.IProjectionCoordinator<>)
                .MakeGenericType(registration.ServiceType);
        }
        catch (ArgumentException)
        {
            // A marker type that does not satisfy the coordinator's constraints cannot have had a
            // coordinator registered for it either, so this is the same answer as "not hosted here".
            return null;
        }
    }

    /// <summary>
    /// The daemon for the database in <paramref name="scope" />, or the reason there is not one.
    /// </summary>
    /// <param name="scope">An already-resolved scope: the authorization has happened before this is called.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async ValueTask<DaemonHosting> ForScopeAsync(ResolvedScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        MartenCoordinator? coordinator = ResolveCoordinator(scope.Registration);
        if (coordinator is null)
        {
            return DaemonHosting.NotHosted(NotRegisteredExplanation);
        }

        try
        {
            IReadOnlyList<IMartenDatabase> databases = await scope.Store.Storage.AllDatabases().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // One database is the overwhelmingly common shape, and DaemonForMainDatabase is the call that
            // works before any database has been resolved by name.
            IProjectionDaemon daemon = databases.Count <= 1
                ? coordinator.DaemonForMainDatabase()
                : await DaemonForAsync(coordinator, scope.Database, cancellationToken).ConfigureAwait(false);

            return DaemonHosting.Hosted(daemon, [.. databases.Select(static x => x.Id.Identity)]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Marten Studio could not reach the async daemon of store {StoreKey}, database {DatabaseId}",
                scope.Registration.Key,
                scope.Scope.DatabaseId);

            return DaemonHosting.NotHosted(
                $"A daemon coordinator is registered, but it could not answer for this database: {exception.Message}");
        }
    }

    /// <summary>
    /// The coordinator for the store in <paramref name="scope" />, or <see langword="null" /> when this
    /// process hosts none for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same single resolution path as <see cref="ForScopeAsync" /> - both go through
    /// <see cref="ResolveCoordinator" />, so an ancillary store's generic
    /// <c>IProjectionCoordinator&lt;T&gt;</c> is found here exactly as it is there and there is never a
    /// second answer to "which coordinator is this store's".
    /// </para>
    /// <para>
    /// Exposed separately because pause and resume are operations on the <em>coordinator</em> and not on
    /// a daemon: <c>PauseAsync()</c> stops the leadership runner - the thing that otherwise restarts
    /// every stopped agent within <c>LeadershipPollingTime</c> - and only then stops the daemons. A stop
    /// aimed at one database's daemon cannot do that, which is why the studio's "stop everything" is
    /// this and not <c>IProjectionDaemon.StopAllAsync()</c> (AGENTS.md hard rule 11's sibling problem:
    /// the supported daemon is the coordinator's, so the supported way to stop it is the coordinator's
    /// too).
    /// </para>
    /// </remarks>
    /// <param name="scope">An already-resolved scope: the authorization has happened before this is called.</param>
    public MartenCoordinator? CoordinatorForScope(ResolvedScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return ResolveCoordinator(scope.Registration);
    }

    /// <summary>
    /// The daemon the coordinator already holds for <paramref name="database" />, in a multi-database store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator keys its daemons on <c>IDatabase.Identifier</c> - the tenancy's own name for a
    /// database - and <c>DaemonForDatabase(id)</c> puts that string through
    /// <c>Tenancy.FindOrCreateDatabase</c>, which resolves a <em>tenant id or database identifier</em>.
    /// The studio addresses databases by <c>DatabaseId.Identity</c> instead (<c>server.name</c>, taken
    /// from the connection string by Weasel's <c>PostgresqlDatabase</c>), and the two are not the same
    /// string: <c>StaticMultiTenancy</c> names a database <c>databaseIdentifier ?? "{Database}@{Host}"</c>
    /// and <c>MasterTableTenancy</c> looks tenants up. Handing <c>Identity</c> to
    /// <c>DaemonForDatabase</c> therefore throws <c>UnknownTenantIdException</c> for every
    /// multi-database store, which the catch above would render as "not hosted in this process" -
    /// for a daemon that is running right here.
    /// </para>
    /// <para>
    /// So the daemons are asked for as a set and matched on the database they were built against.
    /// <c>JasperFxAsyncDaemon</c> takes its <c>Tracker</c> straight off the database
    /// (<c>Tracker = Database.Tracker</c>) and stamps <c>Tracker.DatabaseIdentifier</c> with
    /// <c>Database.Identifier</c>, so the tracker <em>is</em> the identity - verified by decompiling
    /// JasperFx.Events 2.69.3 and Marten 9.35's <c>ProjectionCoordinator</c>. The reference is the
    /// primary match because it cannot be ambiguous; the stamped identifier is the fallback for a
    /// coordinator that hands back a daemon built against a different instance of the same database.
    /// </para>
    /// </remarks>
    private static async ValueTask<IProjectionDaemon> DaemonForAsync(
        MartenCoordinator coordinator,
        IMartenDatabase database,
        CancellationToken cancellationToken)
    {
        string identifier = IdentifierOf(database);

        IReadOnlyList<IProjectionDaemon> daemons = await coordinator.AllDaemonsAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (IProjectionDaemon candidate in daemons)
        {
            if (ReferenceEquals(candidate.Tracker, database.Tracker))
            {
                return candidate;
            }
        }

        foreach (IProjectionDaemon candidate in daemons)
        {
            if (string.Equals(candidate.Tracker?.DatabaseIdentifier, identifier, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        // Nothing matched, which means the coordinator has not built a daemon for this database yet.
        // Asking for it by the identifier it would be keyed under is the one call that can create it.
        return await coordinator.DaemonForDatabase(identifier).ConfigureAwait(false);
    }

    /// <summary>
    /// The tenancy's own name for a database, which is the string the coordinator keys daemons on.
    /// </summary>
    /// <remarks>
    /// <c>IDatabase.Identifier</c> is <c>[Obsolete]</c> in favour of the <c>DatabaseId</c> value object,
    /// and for everything the studio does itself the value object is the right handle. This one place
    /// needs the old string because it is what Marten's own coordinator - and
    /// <c>ShardStateTracker.DatabaseIdentifier</c> - are keyed on; <c>Id.Identity</c> is derived from the
    /// connection string and is a different value.
    /// </remarks>
    private static string IdentifierOf(IMartenDatabase database)
    {
#pragma warning disable CS0618 // Type or member is obsolete
        return database.Identifier;
#pragma warning restore CS0618
    }

    /// <summary>
    /// The coordinator registered for this store, or <see langword="null" /> when there is none.
    /// </summary>
    /// <remarks>
    /// <c>GetService</c> and not <c>GetRequiredService</c>: the missing registration <em>is</em> the
    /// answer. A construction failure is treated the same way and logged, because a daemon that will not
    /// build is still a daemon this process cannot drive.
    /// </remarks>
    internal MartenCoordinator? ResolveCoordinator(MartenStoreRegistration registration)
    {
        Type? serviceType = CoordinatorServiceType(registration);
        if (serviceType is null)
        {
            return null;
        }

        try
        {
            return provider.GetService(serviceType) as MartenCoordinator;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Marten Studio could not resolve the projection coordinator {ServiceType} for store {StoreKey}",
                serviceType,
                registration.Key);

            return null;
        }
    }
}
