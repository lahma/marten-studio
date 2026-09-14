using MartenStudio.Services;
using MartenStudio.Services.Configuration;

namespace MartenStudio.Tests.Configuration;

/// <summary>
/// What the Configuration page is given, said outright by a test.
/// </summary>
internal sealed class FakeConfigurationService : IConfigurationService
{
    /// <summary>What <see cref="DescribeAsync" /> answers.</summary>
    public StudioConfiguration Configuration { get; set; } = Sample();

    /// <summary>What <see cref="DescribeAsync" /> throws, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many times the page has asked, so a reload can be told from the first load.</summary>
    public int Loads { get; private set; }

    public Task<StudioConfiguration> DescribeAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Loads++;
        return Failure is null
            ? Task.FromResult(Configuration)
            : Task.FromException<StudioConfiguration>(Failure);
    }

    /// <summary>A store with one document type, enough cards to render every section.</summary>
    public static StudioConfiguration Sample() => new(
        new StoreConfiguration(
            "default",
            "Default",
            [
                new ConfigurationValue("Document schema", "studio_sample", "StoreOptions.DatabaseSchemaName"),
                new ConfigurationValue("Auto-create", "CreateOrUpdate", "StoreOptions.AutoCreateSchemaObjects"),
            ],
            [new ConfiguredDatabase("localhost.marten", "PostgreSQL", "localhost", 5432, "marten", "studio_sample", ["acme"])],
            null),
        new EventStoreConfiguration(
            [new ConfigurationValue("Stream identity", "Guid", "StoreOptions.Events.StreamIdentity")],
            [new ConfigurationValue("Headers", "no", "StoreOptions.Events.MetadataConfig.HeadersEnabled")],
            [new ConfigurationValue("Async mode", "Solo", "StoreOptions.Events.Daemon.AsyncMode")],
            4,
            2),
        [
            new DocumentTypeConfiguration(
                "customer",
                "Customer",
                "MartenStudio.SampleDomain.Documents.Customer",
                "studio_sample.mt_doc_customer",
                [new ConfigurationValue("Delete style", "Remove", "StoreOptions.Schema.For<T>().SoftDeleted()")],
                [new DuplicatedFieldConfiguration("Email", "email", "varchar")],
                [new ConfiguredIndex("mt_doc_customer_idx_email", "CREATE UNIQUE INDEX mt_doc_customer_idx_email ON studio_sample.mt_doc_customer (email);")],
                [new ConfiguredForeignKey("fk", "customer_id", "studio_sample.mt_doc_customer", "id", "NoAction")],
                [new ConfiguredSubClass("vip_customer", "VipCustomer")],
                ["id", "data", "mt_last_modified"]),
            new DocumentTypeConfiguration(
                "order",
                "Order",
                "MartenStudio.SampleDomain.Documents.Order",
                "studio_sample.mt_doc_order",
                [new ConfigurationValue("Delete style", "SoftDelete", "StoreOptions.Schema.For<T>().SoftDeleted()")],
                [],
                [],
                [],
                [],
                ["id", "data", "mt_deleted"]),
        ],
        "17.2");
}
