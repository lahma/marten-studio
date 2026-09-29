using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Projections;
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

    /// <summary>
    /// An external system runs this store's async projections, and the studio does not ask its
    /// coordinator anything.
    /// </summary>
    public static DaemonHosting ExternallyManaged() =>
        new(DaemonHostingState.ExternallyManaged, null, DaemonAccessor.ExternallyManagedExplanation);

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
/// <b>Three answers that are not failures, and are never logged above Debug.</b> A store whose
/// <c>Projections.AsyncMode</c> is <c>DaemonMode.ExternallyManaged</c> has its async projections run by
/// something else - Wolverine's managed event-subscription distribution sets exactly that - and its
/// coordinator is never asked: Wolverine's <c>DaemonForMainDatabase()</c> and <c>DaemonForDatabase()</c>
/// throw <c>NotSupportedException</c> by design, so asking would turn a supported deployment into a
/// warning every <c>RefreshInterval</c> in every open tab, which is the production report this was
/// written for. A coordinator that throws <c>NotSupportedException</c> from the per-database lookup
/// anyway (a Wolverine release that did not set the mode) is read the same way. And a coordinator whose
/// <em>construction</em> throws <c>ArgumentOutOfRangeException</c> - Wolverine's does, from
/// <c>FindStore</c>, for a store its agent family does not know - is "not hosted here", which is what
/// it is. Only a coordinator that answers with something else - or whose construction fails in some
/// other way - is an anomaly, and that is a Warning once per <see cref="StudioLogThrottle.Window" />
/// (event 9212) rather than once per poll.
/// </para>
/// <para>
/// <b><see cref="ForScopeAsync" /> is the one place that decides.</b> Every daemon control - pause,
/// resume, start and stop agent, restart high water, rebuild - asks it and refuses on
/// <see cref="DaemonHostingState.ExternallyManaged" />, and <see cref="CoordinatorForScope" /> takes its
/// answer rather than forming one of its own. Reading <c>AsyncMode</c> alone was not enough: a Wolverine
/// release from before it set <c>ExternallyManaged</c> leaves the mode at <c>Solo</c>, the page hid
/// the controls because the lookup said "external", and the pause path - which only read the mode -
/// went on to <c>WolverineProjectionCoordinator.PauseAsync</c>, which is <c>StopAllAsync</c> on this node
/// behind Wolverine's back.
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
    /// <summary>
    /// What the card says when no coordinator is registered for a store that does have async work.
    /// </summary>
    /// <remarks>
    /// A statement of fact and not a to-do. A host that runs its daemon in another process - or runs none
    /// here on purpose - is a supported deployment, and a card that told it to add
    /// <c>AddAsyncDaemon</c> was advice nobody asked for, repeated on every page.
    /// </remarks>
    public const string NotRegisteredExplanation =
        "No async daemon is hosted in this process for this store; hosting one takes " +
        "AddAsyncDaemon(DaemonMode.Solo) or HotCold. Projection progress is read from the database, and " +
        "starting, stopping and rebuilding happen in whichever process runs the daemon.";

    /// <summary>What the card says when the store has nothing for a daemon to run.</summary>
    public const string NoAsyncProjectionsExplanation =
        "This store has no asynchronous projections, so there is no daemon to host.";

    /// <summary>
    /// What the card says when an external system - Wolverine's managed distribution, typically - runs
    /// this store's async projections.
    /// </summary>
    public const string ExternallyManagedExplanation =
        "Async projections are run by an external system (for example Wolverine's managed distribution). " +
        "Progress is read from the database. Starting, stopping and rebuilding belong to that system.";

    /// <summary>
    /// What the card says when a coordinator is registered and could not be constructed here.
    /// </summary>
    /// <remarks>
    /// The exception's message is deliberately not repeated: Wolverine's, for one, names every event store
    /// its agent family knows, and a visitor authorized for this store is not owed the names of the
    /// others (D5). It is in the Debug log for whoever administers the process.
    /// </remarks>
    public const string CoordinatorUnavailableExplanation =
        "A projection coordinator is registered for this store, but it could not be created in this " +
        "process, so there is no daemon here to show. Projection progress is read from the database.";

    /// <summary>The throttle key of a coordinator that was asked and answered with a failure.</summary>
    private const string ForScopeSite = "DaemonAccessor.ForScope";

    /// <summary>The throttle key of a coordinator whose construction failed in an unexpected way.</summary>
    private const string ConstructSite = "DaemonAccessor.Construct";

    private readonly IServiceProvider provider;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<DaemonAccessor> logger;

    public DaemonAccessor(IServiceProvider provider, StudioLogThrottle throttle, ILogger<DaemonAccessor> logger)
    {
        this.provider = provider;
        this.throttle = throttle;
        this.logger = logger;
    }

    /// <summary>
    /// Whether an external system runs this store's async projections
    /// (<c>Projections.AsyncMode == DaemonMode.ExternallyManaged</c>).
    /// </summary>
    /// <remarks>
    /// Read through <c>IReadOnlyEventStoreOptions.Daemon</c>, which is the store's own
    /// <c>ProjectionOptions</c> - <c>IReadOnlyStoreOptions</c> has no <c>Projections</c> member.
    /// <c>DaemonMode.ExternallyManaged</c> exists in the JasperFx.Events that Marten 9.31 brings; both
    /// are pinned by <c>MartenApiSurfaceTest</c>. A store whose settings cannot be read is not claimed to
    /// be externally managed: the coordinator path below is the conservative one.
    /// </remarks>
    internal static bool IsExternallyManaged(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return store.Options.Events.Daemon.AsyncMode == DaemonMode.ExternallyManaged;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the store has anything a daemon would run: an async projection or a subscription.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ProjectionGraph.HasAnyAsyncProjections()</c> and not <c>IReadOnlyEventStoreOptions.Projections()</c>:
    /// the latter is <c>Options.Projections.All.OfType&lt;ISubscriptionSource&gt;()</c> and never lists a
    /// subscription registered with <c>Events.Subscribe(...)</c>, which lives in a separate list - verified
    /// against Marten 9.31 and JasperFx.Events 2.60, and pinned by <c>MartenApiSurfaceTest</c>. The
    /// fallback for a settings object of another shape is the projection list, and a store that cannot
    /// be read at all is assumed to have async work, so the card says the informative sentence rather
    /// than claiming there is nothing to host.
    /// </para>
    /// </remarks>
    internal static bool HasAsyncWork(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return store.Options.Events.Daemon is ProjectionOptions projections
                ? projections.HasAnyAsyncProjections()
                : store.Options.Events.Projections().Any(static x => x.Lifecycle == ProjectionLifecycle.Async);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>Why there is no daemon to show, when nothing is registered for this store.</summary>
    internal static string NotRegisteredExplanationFor(IDocumentStore store) =>
        HasAsyncWork(store) ? NotRegisteredExplanation : NoAsyncProjectionsExplanation;

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
    /// <remarks>
    /// <para>
    /// The one place the studio decides whether a store's daemon is hosted here, not hosted here, or run
    /// by an external system - and every daemon control refuses on the last answer, so it has to be the
    /// same answer whatever shape the store has.
    /// </para>
    /// <para>
    /// <b>The per-database lookup is asked first, on every shape of store.</b> It is the call an
    /// externally managing coordinator refuses: Wolverine's <c>DaemonForMainDatabase()</c> and
    /// <c>DaemonForDatabase()</c> throw <c>NotSupportedException</c>, while its <c>AllDaemonsAsync()</c>
    /// answers - with the very daemons its agents run. So a multi-database store whose lookup started
    /// from the set was found "hosted" on a Wolverine release that leaves <c>AsyncMode</c> alone, the
    /// same coordinator on a single-database store was "external", and whether the studio offered to
    /// pause somebody else's projections depended on how many databases they had.
    /// </para>
    /// <para>
    /// <b>Only the lookup itself is read as "external".</b> A <c>NotSupportedException</c> from
    /// enumerating the databases, or from the set the multi-database match falls back to, is not the
    /// deployment answering; it is a coordinator or a tenancy that failed, and it is logged as one
    /// (event 9212, throttled) rather than silently turning the store's controls off.
    /// </para>
    /// </remarks>
    /// <param name="scope">An already-resolved scope: the authorization has happened before this is called.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async ValueTask<DaemonHosting> ForScopeAsync(ResolvedScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string storeKey = scope.Registration.Key;
        string databaseId = scope.Database.Id.Identity;

        // First, and before the coordinator is so much as resolved: an externally managed store's
        // coordinator (Wolverine's) throws from the very calls below, by design.
        if (IsExternallyManaged(scope.Store))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Marten Studio is not asking the projection coordinator of store {StoreKey}: its AsyncMode is ExternallyManaged",
                    storeKey);
            }

            return DaemonHosting.ExternallyManaged();
        }

        MartenCoordinator? coordinator = ResolveCoordinator(scope.Registration, databaseId, out bool constructionFailed);
        if (coordinator is null)
        {
            // A value on the card, polled every RefreshInterval: Debug, and never more.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Marten Studio found no projection coordinator for store {StoreKey} in this process",
                    storeKey);
            }

            return DaemonHosting.NotHosted(
                constructionFailed ? CoordinatorUnavailableExplanation : NotRegisteredExplanationFor(scope.Store));
        }

        try
        {
            IReadOnlyList<IMartenDatabase> databases = await scope.Store.Storage.AllDatabases().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            IProjectionDaemon? daemon = await LookUpDaemonAsync(
                coordinator, scope.Database, databases.Count, storeKey, cancellationToken).ConfigureAwait(false);

            return daemon is null
                ? DaemonHosting.ExternallyManaged()
                : DaemonHosting.Hosted(daemon, [.. databases.Select(static x => x.Id.Identity)]);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A real anomaly, on a path every open Overview, projections screen and navigation badge polls:
            // once per store, database and kind of failure per window at Warning, and at Debug in between.
            LogLevel level = throttle.WarningOrDebug(ForScopeSite, storeKey, databaseId, StudioLogThrottle.KindOf(exception));
            logger.DaemonUnreachable(level, exception, storeKey, databaseId);

            return DaemonHosting.NotHosted(
                $"A daemon coordinator is registered, but it could not answer for this database: {exception.Message}");
        }
    }

    /// <summary>
    /// The coordinator for the store in <paramref name="scope" />, or <see langword="null" /> when this
    /// process hosts none for it - as <paramref name="hosting" /> has already decided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same single resolution path as <see cref="ForScopeAsync" /> - both go through
    /// <see cref="ResolveCoordinator(MartenStoreRegistration, string?, out bool)" />, so an ancillary
    /// store's generic <c>IProjectionCoordinator&lt;T&gt;</c> is found here exactly as it is there and
    /// there is never a second answer to "which coordinator is this store's".
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
    /// <para>
    /// <b>It takes <see cref="ForScopeAsync" />'s answer rather than forming its own.</b> An externally
    /// managed store has no coordinator as far as the studio is concerned, whatever is registered:
    /// pausing or resuming it belongs to the system that runs its projections, and that system's
    /// coordinator is exactly the one whose <c>PauseAsync</c> is <c>StopAllAsync</c> on this node. This
    /// used to read <c>AsyncMode</c> for itself, which is the one signal a Wolverine release predating
    /// <c>ExternallyManaged</c> does not give - so the lookup said "external" and hid the controls, and a
    /// client that drove them anyway reached Wolverine's coordinator here.
    /// </para>
    /// </remarks>
    /// <param name="scope">An already-resolved scope: the authorization has happened before this is called.</param>
    /// <param name="hosting">What <see cref="ForScopeAsync" /> answered for <paramref name="scope" />.</param>
    /// <param name="explanation">
    /// Why there is none, in the words the card uses, when the answer is <see langword="null" />.
    /// </param>
    public MartenCoordinator? CoordinatorForScope(ResolvedScope scope, DaemonHosting hosting, out string explanation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(hosting);

        if (hosting.State == DaemonHostingState.ExternallyManaged)
        {
            explanation = hosting.Explanation;
            return null;
        }

        MartenCoordinator? coordinator = ResolveCoordinator(
            scope.Registration, scope.Database.Id.Identity, out bool constructionFailed);

        explanation = coordinator is not null
            ? string.Empty
            : constructionFailed ? CoordinatorUnavailableExplanation : NotRegisteredExplanationFor(scope.Store);

        return coordinator;
    }

    /// <summary>
    /// The coordinator's daemon for <paramref name="database" />, or <see langword="null" /> when the
    /// coordinator refuses to hand one out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-database call comes first for every shape of store, and it is the only call whose
    /// <c>NotSupportedException</c> is read as "externally managed" - see <see cref="ForScopeAsync" />.
    /// One database is the overwhelmingly common shape, and <c>DaemonForMainDatabase</c> is the call that
    /// works before any database has been resolved by name; a multi-database store is asked
    /// <c>DaemonForDatabase</c> with the tenancy's own name for the database (<see cref="IdentifierOf" />).
    /// </para>
    /// <para>
    /// The multi-database answer is then checked against the database it is supposed to be about, and
    /// only if it is not provably that one is the set asked for (<see cref="MatchInSetAsync" />).
    /// </para>
    /// </remarks>
    private async ValueTask<IProjectionDaemon?> LookUpDaemonAsync(
        MartenCoordinator coordinator,
        IMartenDatabase database,
        int databaseCount,
        string storeKey,
        CancellationToken cancellationToken)
    {
        string identifier = IdentifierOf(database);
        IProjectionDaemon daemon;

        try
        {
            daemon = databaseCount <= 1
                ? coordinator.DaemonForMainDatabase()
                : await coordinator.DaemonForDatabase(identifier).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            // Wolverine's managed distribution, on a release that did not set ExternallyManaged: its
            // coordinator hands out no daemons, and says so with exactly this exception type from exactly
            // this call. That is the deployment answering, not a fault.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    exception,
                    "The projection coordinator of store {StoreKey} does not hand out daemons; Marten Studio treats the store as externally managed",
                    storeKey);
            }

            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (databaseCount <= 1 || IsBuiltAgainst(daemon, database, identifier))
        {
            return daemon;
        }

        return await MatchInSetAsync(coordinator, database, identifier, cancellationToken).ConfigureAwait(false) ?? daemon;
    }

    /// <summary>
    /// Whether <paramref name="daemon" /> was built against <paramref name="database" />.
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
    /// <c>DaemonForDatabase</c> throws <c>UnknownTenantIdException</c> for every multi-database store,
    /// which is why <see cref="LookUpDaemonAsync" /> hands it <see cref="IdentifierOf" /> - and why the
    /// answer is still checked, because a tenancy that resolves tenant ids and database names through the
    /// same call can resolve the wrong one.
    /// </para>
    /// <para>
    /// <c>JasperFxAsyncDaemon</c> takes its <c>Tracker</c> straight off the database
    /// (<c>Tracker = Database.Tracker</c>) and stamps <c>Tracker.DatabaseIdentifier</c> with
    /// <c>Database.Identifier</c>, so the tracker <em>is</em> the identity - verified by decompiling
    /// JasperFx.Events 2.69.3 and Marten 9.35's <c>ProjectionCoordinator</c>. The reference is the
    /// primary match because it cannot be ambiguous; the stamped identifier is the fallback for a
    /// coordinator that hands back a daemon built against a different instance of the same database.
    /// </para>
    /// </remarks>
    private static bool IsBuiltAgainst(IProjectionDaemon daemon, IMartenDatabase database, string identifier) =>
        ReferenceEquals(daemon.Tracker, database.Tracker)
        || string.Equals(daemon.Tracker?.DatabaseIdentifier, identifier, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The daemon the coordinator holds for <paramref name="database" />, found in its whole set, or
    /// <see langword="null" /> when none of them is provably that database's.
    /// </summary>
    /// <remarks>
    /// Only reached when <c>DaemonForDatabase</c> answered with a daemon that is not provably about this
    /// database, and never read as "externally managed" - a coordinator that got that far has already
    /// handed out a daemon, so a <c>NotSupportedException</c> from here is a fault like any other (event
    /// 9212). Reference first, then the stamped identifier, for the reasons on <see cref="IsBuiltAgainst" />.
    /// </remarks>
    private static async ValueTask<IProjectionDaemon?> MatchInSetAsync(
        MartenCoordinator coordinator,
        IMartenDatabase database,
        string identifier,
        CancellationToken cancellationToken)
    {
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

        return null;
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
    /// answer.
    /// </remarks>
    internal MartenCoordinator? ResolveCoordinator(MartenStoreRegistration registration) =>
        ResolveCoordinator(registration, null, out _);

    /// <summary>
    /// The coordinator registered for this store, or <see langword="null" /> and whether one was
    /// registered but would not be constructed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A construction failure is "not hosted here" either way - a daemon that will not build is still a
    /// daemon this process cannot drive, and the card says so - but only one shape of it is expected.
    /// Wolverine's coordinator calls <c>FindStore</c> in its constructor and throws
    /// <c>ArgumentOutOfRangeException("Unknown identity …")</c> for a store its agent family does not
    /// know, and this is resolved on every poll of every open page: that is Debug.
    /// </para>
    /// <para>
    /// Anything else - a missing dependency, a coordinator whose own constructor faulted - is a host
    /// whose daemon is registered and broken, which is exactly what a Debug line would hide. It is event
    /// 9212, throttled like the lookup failures in <see cref="ForScopeAsync" /> (its own throttle site,
    /// because it is a different fault), so it is a Warning once per window rather than once per poll.
    /// </para>
    /// </remarks>
    /// <param name="registration">The store.</param>
    /// <param name="databaseId">The database the caller is about, for the log line; blank when none.</param>
    /// <param name="constructionFailed">Whether a coordinator is registered and could not be built.</param>
    private MartenCoordinator? ResolveCoordinator(
        MartenStoreRegistration registration,
        string? databaseId,
        out bool constructionFailed)
    {
        constructionFailed = false;

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
            constructionFailed = true;

            if (IsUnknownStoreShape(exception))
            {
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        exception,
                        "Marten Studio could not construct the projection coordinator {ServiceType} for store {StoreKey}; it treats the store as having no daemon here",
                        serviceType,
                        registration.Key);
                }

                return null;
            }

            string storeKey = registration.Key;
            string database = databaseId ?? string.Empty;

            LogLevel level = throttle.WarningOrDebug(ConstructSite, storeKey, database, StudioLogThrottle.KindOf(exception));
            logger.DaemonUnreachable(level, exception, storeKey, database);

            return null;
        }
    }

    /// <summary>
    /// Whether a coordinator's construction failed the way Wolverine's does for a store its agent family
    /// does not know: <c>ArgumentOutOfRangeException</c>, as <c>FindStore</c> throws it.
    /// </summary>
    /// <remarks>
    /// Unwrapped from a <see cref="System.Reflection.TargetInvocationException" /> as well, in case a
    /// container activates the coordinator through reflection that wraps what the constructor threw;
    /// Microsoft's own container does not, but the studio does not choose the host's container.
    /// </remarks>
    private static bool IsUnknownStoreShape(Exception exception) =>
        exception is ArgumentOutOfRangeException
        || exception is System.Reflection.TargetInvocationException { InnerException: ArgumentOutOfRangeException };
}
