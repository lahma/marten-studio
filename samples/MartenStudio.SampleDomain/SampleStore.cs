using JasperFx;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.SampleDomain.Documents;
using MartenStudio.SampleDomain.Events;

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

        // --- Events ---
        // Everything below is about the event store, its projections and the async daemon. The document
        // section above is a different packet's; keep the two apart.

        // Correlation, causation and headers are off by default, and a studio that cannot show them
        // cannot show what a real event store looks like. The seeder sets all three on every append.
        opts.Events.MetadataConfig.EnableAll();

        // A projection that throws must produce a dead letter and let the shard carry on, rather than
        // pausing it. ShipmentTracker throws on one event on purpose; without this the demo would come
        // up with a stopped shard instead of the dead letter it is meant to demonstrate.
        opts.Projections.Errors.SkipApplyErrors = true;

        // Three registrations, three lifecycles, so the projections screen has each of them to draw.
        opts.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Inline);
        opts.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async);
        opts.Projections.Add(new ShipmentTracker(), ProjectionLifecycle.Async);
    }
}
