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

        return Facts.TryGetValue(storeKey, out StoreScopeFacts? facts) ? facts : StoreScopeFacts.None;
    }

    public void Invalidate() => InvalidateCount++;
}
