using JasperFx.Descriptors;

using Marten;
using Marten.Storage;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>One store as the picker offers it.</summary>
internal sealed record StoreListing(string Key, string DisplayName, StoreAvailability Availability);

/// <summary>One database as the picker offers it. The key is Marten's identity, never a connection string.</summary>
internal sealed record DatabaseListing(string Identity, string Name, string Server);

/// <summary>
/// What the header needs to know about one store and database beyond the listings: how many databases
/// there could be, whether a tenant selector means anything, and which tenants it would offer.
/// </summary>
internal sealed record StoreScopeFacts(
    DatabaseCardinality Cardinality,
    bool ShowTenantSelector,
    TenantList Tenants,
    string? UnavailableMessage)
{
    /// <summary>What a store nobody could build looks like.</summary>
    public static StoreScopeFacts Unavailable(string? message) =>
        new(DatabaseCardinality.Single, ShowTenantSelector: false, TenantList.Unavailable, message);

    /// <summary>What no store at all looks like.</summary>
    public static StoreScopeFacts None { get; } =
        new(DatabaseCardinality.Single, ShowTenantSelector: false, TenantList.Unavailable, null);
}

/// <summary>
/// Where <see cref="StudioState" /> gets its authorization-filtered listings.
/// </summary>
/// <remarks>
/// A seam rather than a method on the state object, for two reasons. The listings are IO - they build
/// stores, ask Marten for databases and may query for tenants - and the state is what a circuit holds;
/// keeping them apart means a component test can say what the header should show without a Postgres
/// anywhere near it.
/// </remarks>
internal interface IStudioScopeCatalog
{
    /// <summary>Every store the visitor may see, unavailable ones included and marked.</summary>
    Task<IReadOnlyList<StoreListing>> ListStoresAsync(CancellationToken cancellationToken = default);

    /// <summary>Every database of one store the visitor may see.</summary>
    Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default);

    /// <summary>The facts the header renders about one store and database.</summary>
    Task<StoreScopeFacts> DescribeAsync(string storeKey, string databaseId, CancellationToken cancellationToken = default);

    /// <summary>Forgets whatever was cached, for the refresh button.</summary>
    void Invalidate();
}

/// <summary>
/// The real catalog: the registry, the store authorization policy and tenant discovery, in that order.
/// </summary>
/// <remarks>
/// Every listing is filtered before it is returned, which is what makes it the set
/// <see cref="StudioState.SetScopeAsync" /> validates against. A store whose every database the visitor
/// may not see is not listed at all - not listed as empty, not listed as denied.
/// </remarks>
internal sealed class StudioScopeCatalog : IStudioScopeCatalog
{
    private readonly IOptions<MartenStudioOptions> options;
    private readonly MartenStoreRegistry registry;
    private readonly StudioAuthorization authorization;
    private readonly TenantDiscovery tenantDiscovery;
    private readonly IServiceProvider provider;
    private readonly ILogger<StudioScopeCatalog> logger;

    public StudioScopeCatalog(
        IOptions<MartenStudioOptions> options,
        MartenStoreRegistry registry,
        StudioAuthorization authorization,
        TenantDiscovery tenantDiscovery,
        IServiceProvider provider,
        ILogger<StudioScopeCatalog> logger)
    {
        this.options = options;
        this.registry = registry;
        this.authorization = authorization;
        this.tenantDiscovery = tenantDiscovery;
        this.provider = provider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoreListing>> ListStoresAsync(CancellationToken cancellationToken = default)
    {
        List<StoreListing> stores = [];

        foreach (MartenStoreRegistration registration in registry.Registrations(options))
        {
            StoreAvailability availability = registry.TryResolve(registration, provider, out IDocumentStore? store);
            if (!availability.IsAvailable || store is null)
            {
                // A store the application registered but that will not build is offered and greyed out
                // rather than omitted - but only to someone the policy would let reach it. The resource
                // carries an empty database identity because there is no database to name yet.
                if (await authorization.IsAuthorizedAsync(new StudioScope(registration.Key, string.Empty, null), null, cancellationToken).ConfigureAwait(false))
                {
                    stores.Add(new StoreListing(registration.Key, registration.DisplayName, availability));
                }

                continue;
            }

            IReadOnlyList<DatabaseListing> databases = await ListDatabasesAsync(registration.Key, store, cancellationToken).ConfigureAwait(false);
            if (databases.Count > 0)
            {
                stores.Add(new StoreListing(registration.Key, registration.DisplayName, availability));
            }
        }

        return stores;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        MartenStoreRegistration? registration = registry.Find(storeKey, options);
        if (registration is null)
        {
            return [];
        }

        StoreAvailability availability = registry.TryResolve(registration, provider, out IDocumentStore? store);
        return availability.IsAvailable && store is not null
            ? await ListDatabasesAsync(registration.Key, store, cancellationToken).ConfigureAwait(false)
            : [];
    }

    /// <inheritdoc />
    public async Task<StoreScopeFacts> DescribeAsync(string storeKey, string databaseId, CancellationToken cancellationToken = default)
    {
        MartenStoreRegistration? registration = registry.Find(storeKey, options);
        if (registration is null)
        {
            return StoreScopeFacts.None;
        }

        StoreAvailability availability = registry.TryResolve(registration, provider, out IDocumentStore? store);
        if (!availability.IsAvailable || store is null)
        {
            return StoreScopeFacts.Unavailable(availability.Message);
        }

        DatabaseCardinality cardinality = store.Options.Tenancy.Cardinality;
        bool showTenants = tenantDiscovery.IsTenantScopeRelevant(store);
        if (!showTenants || string.IsNullOrEmpty(databaseId))
        {
            return new StoreScopeFacts(cardinality, showTenants, TenantList.Unavailable, null);
        }

        try
        {
            foreach (IMartenDatabase database in await store.Storage.AllDatabases().ConfigureAwait(false))
            {
                if (string.Equals(database.Id.Identity, databaseId, StringComparison.OrdinalIgnoreCase))
                {
                    TenantList tenants = await tenantDiscovery
                        .DiscoverAsync(registration.Key, store, database, cancellationToken)
                        .ConfigureAwait(false);

                    return new StoreScopeFacts(cardinality, showTenants, tenants, null);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not discover the tenants of store {StoreKey}", storeKey);
        }

        return new StoreScopeFacts(cardinality, showTenants, TenantList.Unavailable, null);
    }

    /// <inheritdoc />
    public void Invalidate() => tenantDiscovery.Invalidate();

    private async Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(
        string storeKey,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        List<DatabaseListing> listings = [];
        try
        {
            foreach (IMartenDatabase database in await store.Storage.AllDatabases().ConfigureAwait(false))
            {
                listings.Add(new DatabaseListing(database.Id.Identity, database.Id.Name, database.Id.Server));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not list the databases of store {StoreKey}", storeKey);
            return [];
        }

        return await authorization
            .FilterAsync(listings, x => new StudioScope(storeKey, x.Identity, null), cancellationToken)
            .ConfigureAwait(false);
    }
}
