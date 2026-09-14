using JasperFx.Descriptors;

using MartenStudio.Services;

namespace MartenStudio.Tests.Support;

/// <summary>
/// The store facts the Overview page renders, said outright by a test.
/// </summary>
internal sealed class FakeStoreInfoService : IStoreInfoService
{
    /// <summary>The stores the page is given.</summary>
    public List<StoreOverview> Stores { get; } = [];

    /// <summary>What <see cref="GetOverviewAsync" /> throws, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many times the page has asked, so a Retry can be told apart from the first load.</summary>
    public int Loads { get; private set; }

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
            [new DatabaseOverview(databaseIdentity, "marten", "localhost", ["studio_sample", "studio_sample_events"], databaseError)]));

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
            Databases: []));

        return this;
    }

    public Task<StudioOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        Loads++;
        return Failure is null
            ? Task.FromResult(new StudioOverview(Stores))
            : Task.FromException<StudioOverview>(Failure);
    }
}
