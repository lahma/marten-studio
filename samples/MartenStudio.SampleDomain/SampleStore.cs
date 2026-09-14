using JasperFx;

using Marten;

using MartenStudio.SampleDomain.Documents;

namespace MartenStudio.SampleDomain;

/// <summary>
/// The demo domain's Marten configuration, shared by the sample host and the integration tests.
/// </summary>
/// <remarks>
/// This project deliberately does not reference MartenStudio (AGENTS.md D17): the studio has to work
/// against a store that knows nothing about it, and a reference here would let that invariant rot without
/// anyone noticing. Everything the studio renders about this store, it learns from <c>StoreOptions</c>.
/// </remarks>
public static class SampleStore
{
    /// <summary>The schema the demo documents live in.</summary>
    public const string DocumentSchema = "studio_sample";

    /// <summary>The schema the demo event store lives in.</summary>
    public const string EventSchema = "studio_sample_events";

    /// <summary>The tenants <see cref="SampleDataSeeder" /> writes invoices for.</summary>
    public static IReadOnlyList<string> TenantIds { get; } = ["acme", "globex"];

    /// <summary>
    /// Configures the demo store: its schemas, its document types and the features that make each of them
    /// interesting to look at in the studio.
    /// </summary>
    public static void Configure(StoreOptions opts, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        opts.Connection(connectionString);

        // Its own schemas, so the demo never collides with whatever else lives in the database and the
        // studio has more than one schema name to report.
        opts.DatabaseSchemaName = DocumentSchema;
        opts.Events.DatabaseSchemaName = EventSchema;

        // A demo database is disposable, so let Marten build everything it needs on first use.
        opts.AutoCreateSchemaObjects = AutoCreate.All;

        opts.Schema.For<Customer>()
            .Duplicate(x => x.Email, configure: static index => index.IsUnique = true);

        opts.Schema.For<Order>()
            .SoftDeleted()
            .ForeignKey<Customer>(x => x.CustomerId)
            .UseOptimisticConcurrency(true);

        opts.Schema.For<Invoice>()
            .MultiTenanted();
    }
}
