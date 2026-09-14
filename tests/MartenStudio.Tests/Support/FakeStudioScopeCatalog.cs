using JasperFx.Descriptors;

using MartenStudio.Services;

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

    /// <summary>The databases of each store, by store key.</summary>
    public Dictionary<string, List<DatabaseListing>> Databases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="DescribeAsync" /> answers, by store key.</summary>
    public Dictionary<string, StoreScopeFacts> Facts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times the refresh button asked for everything again.</summary>
    public int InvalidateCount { get; private set; }

    /// <summary>Adds one available store with the databases named.</summary>
    public FakeStudioScopeCatalog WithStore(
        string key,
        string displayName,
        DatabaseCardinality cardinality = DatabaseCardinality.Single,
        params string[] databaseIdentities)
    {
        Stores.Add(new StoreListing(key, displayName, StoreAvailability.Available));
        Databases[key] = [.. databaseIdentities.Select(x => new DatabaseListing(x, x, "localhost"))];
        Facts[key] = new StoreScopeFacts(cardinality, ShowTenantSelector: false, TenantList.Unavailable, null);
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

    /// <summary>Turns the tenant selector on for one store and says what it offers.</summary>
    public FakeStudioScopeCatalog WithTenants(string key, TenantList tenants)
    {
        StoreScopeFacts existing = Facts.TryGetValue(key, out StoreScopeFacts? facts)
            ? facts
            : StoreScopeFacts.None;

        Facts[key] = existing with { ShowTenantSelector = true, Tenants = tenants };
        return this;
    }

    public Task<IReadOnlyList<StoreListing>> ListStoresAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StoreListing>>(Stores);

    public Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DatabaseListing>>(
            Databases.TryGetValue(storeKey, out List<DatabaseListing>? databases) ? databases : []);

    public Task<StoreScopeFacts> DescribeAsync(string storeKey, string databaseId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Facts.TryGetValue(storeKey, out StoreScopeFacts? facts) ? facts : StoreScopeFacts.None);

    public void Invalidate() => InvalidateCount++;
}
