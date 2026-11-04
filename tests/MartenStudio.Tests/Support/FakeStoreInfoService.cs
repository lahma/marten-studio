using JasperFx.Descriptors;

using MartenStudio.Services;
using MartenStudio.Services.Projections;

namespace MartenStudio.Tests.Support;

/// <summary>
/// The store facts the Overview and Stores pages render, said outright by a test.
/// </summary>
internal sealed class FakeStoreInfoService : IStoreInfoService
{
    /// <summary>The stores the page is given.</summary>
    public List<StoreOverview> Stores { get; } = [];

    /// <summary>What <see cref="GetOverviewAsync" /> throws, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many times the page has asked, so a Retry can be told apart from the first load.</summary>
    public int Loads { get; private set; }

    /// <summary>The store key of each ask, in order (<see langword="null" /> for every store).</summary>
    public List<string?> Keys { get; } = [];

    /// <summary>
    /// When set, <see cref="GetOverviewAsync" /> waits for it before answering, so a test can see a page between its ask and
    /// its answer.
    /// </summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>
    /// Reads for a store key that wait for the task completion source before answering, so a test can hold the read of one store while
    /// the visitor moves to another.
    /// </summary>
    public Dictionary<string, TaskCompletionSource> Holds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a store with one database and the facts the tiles show.</summary>
    public FakeStoreInfoService WithStore(
        string key = "default",
        string displayName = "Default",
        int documentTypes = 7,
        string? postgresVersion = "17.2",
        string databaseIdentity = "localhost.marten",
        string? databaseError = null,
        DatabaseCardinality cardinality = DatabaseCardinality.Single)
    {
        Stores.Add(new StoreOverview(
            key,
            displayName,
            IsAvailable: true,
            UnavailableMessage: null,
            cardinality,
            documentTypes,
            postgresVersion,
            new EventStoreFacts("Guid", "Quick", "Single", "studio_sample_events", 4),
            [new DatabaseOverview(databaseIdentity, "marten", "localhost", ["studio_sample", "studio_sample_events"], databaseError)],
            new StoreTenancy(StoreTenancyKind.Single, "One database, no tenants"),
            new StoreDaemonHosting(StoreDaemonHostingKind.NotHostedHere),
            ConjoinedDocumentTypes: 0,
            TenantPartitionedEvents: false));

        return this;
    }

    /// <summary>Adds a store with these databases (<c>identity</c> is also the database's name) and nothing else of note.</summary>
    public FakeStoreInfoService WithDatabases(string key, string displayName, params string[] identities)
    {
        Stores.Add(new StoreOverview(
            key,
            displayName,
            IsAvailable: true,
            UnavailableMessage: null,
            identities.Length > 1 ? DatabaseCardinality.StaticMultiple : DatabaseCardinality.Single,
            DocumentTypeCount: 3,
            PostgresVersion: "17.2",
            new EventStoreFacts("Guid", "Quick", "Single", "studio_sample_events", 4),
            [.. identities.Select(static x => new DatabaseOverview(x, x, "localhost", ["studio_sample"], null))],
            new StoreTenancy(StoreTenancyKind.Static, "A database per tenant, configured"),
            new StoreDaemonHosting(StoreDaemonHostingKind.RegisteredHere, JasperFx.Events.Daemon.DaemonMode.Solo),
            ConjoinedDocumentTypes: 2,
            TenantPartitionedEvents: true));

        return this;
    }

    /// <summary>Adds a store the application registered that will not build.</summary>
    public FakeStoreInfoService WithUnavailableStore(string key, string displayName, string message)
    {
        Stores.Add(new StoreOverview(
            key,
            displayName,
            IsAvailable: false,
            message,
            DatabaseCardinality.Single,
            DocumentTypeCount: 0,
            PostgresVersion: null,
            Events: null,
            Databases: [],
            StoreTenancy.Unknown,
            new StoreDaemonHosting(StoreDaemonHostingKind.NotHostedHere),
            ConjoinedDocumentTypes: 0,
            TenantPartitionedEvents: false));

        return this;
    }

    public async Task<StudioOverview> GetOverviewAsync(string? storeKey = null, CancellationToken cancellationToken = default)
    {
        Loads++;
        Keys.Add(storeKey);

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (storeKey is not null && Holds.TryGetValue(storeKey, out TaskCompletionSource? hold))
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return new StudioOverview(storeKey is null
            ? [.. Stores]
            : [.. Stores.Where(x => string.Equals(x.Key, storeKey, StringComparison.OrdinalIgnoreCase))]);
    }
}
