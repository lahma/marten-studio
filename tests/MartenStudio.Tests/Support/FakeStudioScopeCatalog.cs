using JasperFx.Descriptors;

using MartenStudio.Services;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Support;

/// <summary>
/// The listings a component test says the header should be offered, without a Marten store anywhere.
/// </summary>
/// <remarks>
/// This is the whole reason <see cref="IStudioScopeCatalog" /> is a seam: the real one builds stores,
/// asks Marten for databases and may query for tenants, none of which a test about a <c>select</c>
/// element has any business doing.
/// </remarks>
internal sealed class FakeStudioScopeCatalog : IStudioScopeCatalog
{
    /// <summary>The stores the picker is offered, in order.</summary>
    public List<StoreListing> Stores { get; } = [];

    /// <summary>The stores whose databases could not be listed (1.2): known, and not selectable.</summary>
    public List<StoreListing> Unlistable { get; } = [];

    /// <summary>The databases of each store, by store key.</summary>
    public Dictionary<string, List<DatabaseListing>> Databases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="DescribeAsync" /> answers, by store key.</summary>
    public Dictionary<string, StoreScopeFacts> Facts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="DescribeAsync" /> answers for one database, by store key and database identity; it wins over <see cref="Facts" />.</summary>
    public Dictionary<(string StoreKey, string DatabaseId), StoreScopeFacts> DatabaseFacts { get; } = new();

    /// <summary>Every <see cref="DescribeAsync" /> call, in order: the store and the database it was asked about.</summary>
    public List<(string StoreKey, string DatabaseId)> DescribeCalls { get; } = [];

    /// <summary>A gate held shut on one database's <see cref="DescribeAsync" />, by store key and database identity, which holds only that one.</summary>
    public Dictionary<(string StoreKey, string DatabaseId), TaskCompletionSource> DescribeGates { get; } = new();

    /// <summary>The databases whose <see cref="DescribeAsync" /> throws, by store key and database identity.</summary>
    public HashSet<(string StoreKey, string DatabaseId)> DescribeThrows { get; } = new();

    /// <summary>How many times the refresh button asked for everything again.</summary>
    public int InvalidateCount { get; private set; }

    /// <summary>
    /// A gate a test can hold shut to keep <see cref="DescribeAsync" /> pending - which is what
    /// <see cref="StudioState.SetScopeAsync" /> awaits when the database changes, so it is how a test holds a
    /// scope change half way, the way a real catalog that queries for tenants does.
    /// </summary>
    public TaskCompletionSource? DescribeGate { get; set; }

    /// <summary>Adds one available store with the databases named.</summary>
    public FakeStudioScopeCatalog WithStore(
        string key,
        string displayName,
        DatabaseCardinality cardinality = DatabaseCardinality.Single,
        params string[] databaseIdentities)
    {
        Stores.Add(new StoreListing(key, displayName, StoreAvailability.Available));
        Databases[key] = [.. databaseIdentities.Select(x => new DatabaseListing(x, x, "localhost"))];
        Facts[key] = new StoreScopeFacts(MartenStudioSource.Map(cardinality), ShowTenantSelector: false, TenantList.Unavailable, null);
        return this;
    }

    /// <summary>Adds a store that built and whose databases could not be listed.</summary>
    public FakeStudioScopeCatalog WithUnlistableStore(string key, string displayName)
    {
        Unlistable.Add(new StoreListing(key, displayName, StoreAvailability.Available));
        return this;
    }

    /// <summary>Adds a store the application registered that will not build.</summary>
    public FakeStudioScopeCatalog WithUnavailableStore(string key, string displayName, string message)
    {
        Stores.Add(new StoreListing(key, displayName, StoreAvailability.Unavailable(message, DateTimeOffset.UnixEpoch)));
        Databases[key] = [];
        Facts[key] = StoreScopeFacts.Unavailable(message);
        return this;
    }

    /// <summary>Says which tenants one database of a store lists, as the policy filtered them: the tenant selector is on for it.</summary>
    public FakeStudioScopeCatalog WithDatabaseTenants(string key, string databaseId, TenantList tenants)
    {
        StoreScopeFacts existing = Facts.TryGetValue(key, out StoreScopeFacts? facts) ? facts : StoreScopeFacts.None;
        DatabaseFacts[(key, databaseId)] = existing with { ShowTenantSelector = true, Tenants = tenants };
        return this;
    }

    /// <summary>Turns the tenant selector on for one store and says what it offers.</summary>
    public FakeStudioScopeCatalog WithTenants(string key, TenantList tenants)
    {
        StoreScopeFacts existing = Facts.TryGetValue(key, out StoreScopeFacts? facts)
            ? facts
            : StoreScopeFacts.None;

        Facts[key] = existing with { ShowTenantSelector = true, Tenants = tenants };
        return this;
    }

    public Task<StoreListings> ListStoresAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new StoreListings([.. Stores], [.. Unlistable]));

    public Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DatabaseListing>>(
            Databases.TryGetValue(storeKey, out List<DatabaseListing>? databases) ? databases : []);

    /// <summary>The stores whose databases could not be enumerated: <see cref="TryListDatabasesAsync" /> says it failed.</summary>
    public HashSet<string> EnumerationFails { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The stores whose listing throws, as a policy handler that throws does.</summary>
    public HashSet<string> ListingThrows { get; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task<StoreDatabaseListing> TryListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        if (ListingThrows.Contains(storeKey))
        {
            throw new InvalidOperationException("the policy handler threw");
        }

        return new StoreDatabaseListing(
            EnumerationFails.Contains(storeKey) ? [] : await ListDatabasesAsync(storeKey, cancellationToken).ConfigureAwait(false),
            EnumerationFails.Contains(storeKey));
    }

    public async Task<StoreScopeFacts> DescribeAsync(string storeKey, string databaseId, CancellationToken cancellationToken = default)
    {
        if (DescribeGate is { } gate)
        {
            await gate.Task.Bounded();
        }

        DescribeCalls.Add((storeKey, databaseId));

        if (DescribeGates.TryGetValue((storeKey, databaseId), out TaskCompletionSource? own))
        {
            await own.Task.Bounded();
        }

        if (DescribeThrows.Contains((storeKey, databaseId)))
        {
            throw new InvalidOperationException("the database's listing threw");
        }

        if (DatabaseFacts.TryGetValue((storeKey, databaseId), out StoreScopeFacts? forDatabase))
        {
            return forDatabase;
        }

        return Facts.TryGetValue(storeKey, out StoreScopeFacts? facts) ? facts : StoreScopeFacts.None;
    }

    /// <summary>What <see cref="PlaceDeclaredTenantAsync" /> answers for a tenant of a store, by store key and tenant; a tenant not named is not declared.</summary>
    public Dictionary<(string StoreKey, string TenantId), DeclaredTenantPlacement> Placements { get; } = new();

    /// <summary>Every <see cref="PlaceDeclaredTenantAsync" /> call, in order.</summary>
    public List<(string StoreKey, IReadOnlyList<string> DatabaseIds, string TenantId)> PlaceCalls { get; } = [];

    /// <summary>Says the store declares where its tenant is: placed in a database, or (with <see langword="null" />) in none the visitor may see.</summary>
    public FakeStudioScopeCatalog WithPlacement(string storeKey, string tenantId, string? databaseId, StoreScopeFacts? facts = null)
    {
        Placements[(storeKey, tenantId)] = databaseId is null
            ? DeclaredTenantPlacement.NotPlaced
            : new DeclaredTenantPlacement(true, new TenantDatabaseHit(databaseId, facts ?? StoreScopeFacts.None));
        return this;
    }

    public Task<DeclaredTenantPlacement> PlaceDeclaredTenantAsync(
        string storeKey,
        IReadOnlyList<string> databaseIds,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        PlaceCalls.Add((storeKey, databaseIds, tenantId));
        return Task.FromResult(Placements.TryGetValue((storeKey, tenantId), out DeclaredTenantPlacement? placement) ? placement : DeclaredTenantPlacement.NotDeclared);
    }

    public void Invalidate() => InvalidateCount++;
}
