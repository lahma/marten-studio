using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using JasperFx.Descriptors;

using Marten;
using Marten.Storage;

using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PostgresStudio.Services;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="IStudioSourceProvider" />: the application's Marten stores (<see cref="MartenStoreRegistry" />),
/// their databases (<c>Storage.AllDatabases()</c>, D27's rules unchanged) and their tenants (<see cref="TenantDiscovery" />).
/// </summary>
/// <remarks>
/// Scoped, like the tenant discovery it holds: a circuit's cached tenants and its refresh button are its own. The store
/// registry underneath is the process-wide singleton, with its ten-second failure cache and its throttled 9210.
/// </remarks>
internal sealed class MartenStudioSourceProvider : IStudioSourceProvider
{
    private readonly IOptions<MartenStudioOptions> options;
    private readonly MartenStoreRegistry registry;
    private readonly TenantDiscovery tenantDiscovery;
    private readonly IServiceProvider services;
    private readonly ILogger<MartenStudioSourceProvider> logger;
    private readonly StudioLogThrottle? throttle;

    /// <remarks>
    /// <paramref name="throttle" /> is optional so that a test can build a provider by hand; the container always
    /// supplies it, and without one every failure is simply a Warning (<see cref="StudioLogThrottle.LevelOrWarning" />).
    /// </remarks>
    public MartenStudioSourceProvider(
        IOptions<MartenStudioOptions> options,
        MartenStoreRegistry registry,
        TenantDiscovery tenantDiscovery,
        IServiceProvider services,
        ILogger<MartenStudioSourceProvider> logger,
        StudioLogThrottle? throttle = null)
    {
        this.options = options;
        this.registry = registry;
        this.tenantDiscovery = tenantDiscovery;
        this.services = services;
        this.logger = logger;
        this.throttle = throttle;
    }

    /// <inheritdoc />
    public IReadOnlyList<StudioSourceRegistration> Registrations() =>
        [.. registry.Registrations(options).Select(static x => x.AsSource())];

    /// <inheritdoc />
    public StudioSourceRegistration? Find(string? key) => registry.Find(key, options)?.AsSource();

    /// <inheritdoc />
    public StoreAvailability TryResolve(StudioSourceRegistration registration, [NotNullWhen(true)] out IStudioSource? source)
    {
        ArgumentNullException.ThrowIfNull(registration);

        source = null;

        // The registration this provider answered carries the key; the store's service type is the registry's.
        if (registry.Find(registration.Key, options) is not { } marten)
        {
            return StoreAvailability.Unavailable("No Marten store is registered under '" + registration.Key + "'.", DateTimeOffset.UtcNow);
        }

        StoreAvailability availability = registry.TryResolve(marten, services, out IDocumentStore? store);
        if (availability.IsAvailable && store is not null)
        {
            source = new MartenStudioSource(marten, store, tenantDiscovery);
        }

        return availability;
    }

    /// <inheritdoc />
    public void Invalidate() => tenantDiscovery.Invalidate();

    /// <inheritdoc />
    public long Generation => tenantDiscovery.Generation;

    /// <inheritdoc />
    /// <remarks>
    /// Every store, every database its tenancy already holds (<see cref="HeldDatabases" />, which never builds one), in
    /// registration order. A store that will not build, or whose held databases cannot be listed, is one
    /// <see cref="StudioSweepTargetOutcome.SourceNotReached" /> - the latter logged as 9213 under the same throttle key
    /// as the scope selector's and the Overview's. A store whose tenancy is one database is a one-database source.
    /// </remarks>
    public async IAsyncEnumerable<StudioListedDatabase> ListUnattendedAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (MartenStoreRegistration registration in registry.Registrations(options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            StudioSourceRegistration source = registration.AsSource();
            (IDocumentStore? store, HeldDatabaseList? listed) = await HeldAsync(registration).ConfigureAwait(false);

            if (store is null || listed is null)
            {
                // Logged already: 9210 for a store that will not build, 9213 for one whose databases could not be listed.
                yield return new StudioListedDatabase(source, StudioSweepTargetOutcome.SourceNotReached);
                continue;
            }

            if (listed.Unnamed > 0)
            {
                yield return new StudioListedDatabase(source, StudioSweepTargetOutcome.Unnamed, Count: listed.Unnamed);
            }

            if (listed.Databases.Count == 0 && listed.Unnamed == 0)
            {
                yield return new StudioListedDatabase(source, StudioSweepTargetOutcome.SourceNotEnumerated, Count: 0, Detail: listed.Shape.ToString());
                continue;
            }

            bool single = MartenStudioSource.Map(SafeCardinality(store)) == StudioDatabaseCardinality.Single;

            foreach (IMartenDatabase database in listed.Databases)
            {
                yield return new StudioListedDatabase(source, StudioSweepTargetOutcome.Database, database.Id.Identity) { SingleDatabaseSource = single };
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The store's held databases, never <c>AllDatabases()</c> on a tenancy that builds (D27, hard rule 14): a database the
    /// tenancy does not hold yet is "not found", as it is to the export. An empty id is the first held database - for a
    /// configured tenancy the same first database the visitor's resolver takes.
    /// </remarks>
    public async ValueTask<StudioUnattendedResolution> ResolveUnattendedAsync(string sourceKey, string databaseId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        ArgumentNullException.ThrowIfNull(databaseId);

        if (registry.Find(sourceKey, options) is not { } registration)
        {
            return StudioUnattendedResolution.NotFound("No Marten store is registered under '" + sourceKey + "'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        (IDocumentStore? store, HeldDatabaseList? listed) = await HeldAsync(registration).ConfigureAwait(false);

        if (store is null || listed is null)
        {
            return StudioUnattendedResolution.NotReached(
                "Marten store '" + registration.Key + "' could not be built, or its databases listed; the server log says why (event 9210 or 9213).");
        }

        IMartenDatabase? database = databaseId.Length == 0
            ? (listed.Databases.Count > 0 ? listed.Databases[0] : null)
            : listed.Databases.FirstOrDefault(x => string.Equals(x.Id.Identity, databaseId, StringComparison.OrdinalIgnoreCase));

        return database is null
            ? StudioUnattendedResolution.NotFound(
                "Marten store '" + registration.Key + "' holds no database '" + databaseId + "' in this process: its tenancy is never asked to build one.")
            : StudioUnattendedResolution.Of(
                new ResolvedScope(new StudioScope(registration.Key, database.Id.Identity, null), registration, store, database));
    }

    /// <summary>The store and the databases its tenancy holds, or nulls when it will not build or cannot say (both logged).</summary>
    private async Task<(IDocumentStore? Store, HeldDatabaseList? Listed)> HeldAsync(MartenStoreRegistration registration)
    {
        StoreAvailability availability = registry.TryResolve(registration, services, out IDocumentStore? store);

        if (!availability.IsAvailable || store is null)
        {
            // TryResolve has logged why, throttled (9210).
            return (null, null);
        }

        try
        {
            return (store, await HeldDatabases.ListAsync(store).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogLevel level = StudioLogThrottle.LevelOrWarning(
                throttle, "Store.Databases", registration.Key, null, StudioLogThrottle.KindOf(exception));
            logger.StoreDatabasesUnreadable(level, exception, registration.Key);
            return (store, null);
        }
    }

    /// <summary>The tenancy's cardinality, or <c>DynamicMultiple</c> - the cautious answer - when it cannot say.</summary>
    private static DatabaseCardinality SafeCardinality(IDocumentStore store)
    {
        try
        {
            return store.Options.Tenancy.Cardinality;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DatabaseCardinality.DynamicMultiple;
        }
    }
}

/// <summary>One Marten store that resolved, as the shared resolver and catalog ask about it.</summary>
internal sealed class MartenStudioSource : IStudioSource
{
    private readonly TenantDiscovery tenants;

    /// <param name="registration">The store's registration.</param>
    /// <param name="store">The built store.</param>
    /// <param name="tenants">The circuit's tenant discovery.</param>
    public MartenStudioSource(MartenStoreRegistration registration, IDocumentStore store, TenantDiscovery tenants)
    {
        Marten = registration;
        Store = store;
        this.tenants = tenants;
        Registration = registration.AsSource();
    }

    /// <inheritdoc />
    public StudioSourceRegistration Registration { get; }

    /// <summary>The Marten registration.</summary>
    public MartenStoreRegistration Marten { get; }

    /// <summary>The built store.</summary>
    public IDocumentStore Store { get; }

    /// <inheritdoc />
    /// <remarks>JasperFx's <c>None</c> reads as <see cref="StudioDatabaseCardinality.Single" />: neither draws a database picker.</remarks>
    public StudioDatabaseCardinality Cardinality => Map(Store.Options.Tenancy.Cardinality);

    /// <inheritdoc />
    public bool ShowsTenantSelector => tenants.IsTenantScopeRelevant(Store);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IStudioDatabase>> DatabasesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IMartenDatabase> databases = await Store.Storage.AllDatabases().ConfigureAwait(false);
        return [.. databases.Select(MartenStudioDatabase.Of)];
    }

    /// <inheritdoc />
    public ValueTask<TenantList> DiscoverTenantsAsync(IStudioDatabase database, IReadOnlyList<IStudioDatabase> all, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(all);

        return tenants.DiscoverAsync(Marten.Key, Store, MartenStudioDatabase.Unwrap(database), Unwrap(all), cancellationToken);
    }

    /// <inheritdoc />
    /// <returns>A <see cref="ResolvedScope" />.</returns>
    public async ValueTask<ResolvedSource> CompleteAsync(
        StudioScope scope,
        IStudioDatabase database,
        IReadOnlyList<IStudioDatabase> all,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(all);

        IMartenDatabase matched = MartenStudioDatabase.Unwrap(database);

        // 5. The tenant, which is only ever one the store could actually answer for - asked with the list
        //    just read, so that a database's same-identity twins answer for their tenants too.
        if (scope.TenantId is { } tenantId)
        {
            IReadOnlyList<IMartenDatabase> databases = Unwrap(all);
            TenantList listed = await tenants
                .DiscoverAsync(Marten.Key, Store, matched, databases, cancellationToken)
                .ConfigureAwait(false);

            if (!TenantList.IsAcceptable(listed, scope.TenantId))
            {
                // The visitor's values escaped and bounded (StudioLogValue.Cap): the message is logged and audited whole.
                throw new KeyNotFoundException(
                    $"'{StudioLogValue.Cap(scope.TenantId)}' is not a tenant of database '{StudioLogValue.Cap(scope.DatabaseId)}' in Marten store '{Marten.Key}'.");
            }

            // 6. And the instance that tenant was built from, when the database has twins.
            matched = StudioScopeResolver.InstanceFor(databases, matched, tenantId);
        }

        return new ResolvedScope(scope, Marten, Store, matched);
    }

    /// <summary>The shared cardinality of JasperFx's.</summary>
    internal static StudioDatabaseCardinality Map(DatabaseCardinality cardinality) => cardinality switch
    {
        DatabaseCardinality.StaticMultiple => StudioDatabaseCardinality.StaticMultiple,
        DatabaseCardinality.DynamicMultiple => StudioDatabaseCardinality.DynamicMultiple,
        _ => StudioDatabaseCardinality.Single,
    };

    private static IReadOnlyList<IMartenDatabase> Unwrap(IReadOnlyList<IStudioDatabase> all) =>
        [.. all.Select(MartenStudioDatabase.Unwrap)];
}
