using System.Globalization;

using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Services.Tenants;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Tenancy;

/// <summary>
/// 1.2-SCOPE-2: the Tenants overview against a live server, one fact per tenancy shape. Each reads the page the way the circuit does
/// - one <c>GetAsync</c> through the real service, with <c>ViewDatabaseActivity</c> on - and then proves what the page's contract
/// says: the estimates are the catalog's statistics (never a count), the placement is what the catalog and the enumeration say, and a
/// full read changes nothing on the server (<see cref="ServerSnapshot" />, taken after the seeding's ANALYZE and again after the read).
/// </summary>
public class TenantOverviewLiveTests(PostgresFixture postgres)
{
    private const string Prefix = "scope2_tenants_";

    private const string Acme = "acme";

    private const string Globex = "globex";

    private const string Initech = "initech";

    private const string Schema = "app";

    private const string Events = "app_events";

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_store_whose_events_are_not_kept_per_tenant_says_so_once_and_shows_a_dash_for_every_event_column()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "single_events"], Token);
        string database = owned[0];

        void Configure(StoreOptions options)
        {
            options.Connection(database);
            Documents(options);

            // Conjoined documents, and the event store left as it is: single-tenanted.
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await WriteAsync(seed, Acme, 5);
            await WriteAsync(seed, Globex, 3);
        }

        await TenancyDatabases.ExecuteAsync(database, $"analyze {Schema}.mt_doc_tenancy_note;", Token);

        await using TenancyStudio studio = TenancyStudio.Start(Configure, studio: Activity);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        TenantOverview overview = await circuit.ServiceProvider.GetRequiredService<ITenantOverviewService>()
            .GetAsync(Scope, new TenantOverviewRequest(), Token);

        overview.EventsNotPerTenant.Should().BeTrue();
        overview.Rows.Should().OnlyContain(static x => x.Events.Text == "—" && x.Streams.Text == "—" && x.LastEvent.Text == "—");
        overview.Rows.Select(static x => x.Events.Title).Distinct().Should().Equal("The event store is not kept per tenant.");
    }

    [PostgresFact]
    public async Task Tenants_in_shared_tables_get_statistics_estimates_and_no_last_event()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "shared"], Token);
        string database = owned[0];

        void Configure(StoreOptions options)
        {
            options.Connection(database);
            Documents(options);
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await WriteAsync(seed, Acme, 1_000);
            await WriteAsync(seed, Globex, 300);
            await WriteAsync(seed, Initech, 5);
        }

        // Only one value in the most-common list: acme is singled out, the others fall below it.
        await TenancyDatabases.ExecuteAsync(
            database,
            $"alter table {Schema}.mt_doc_tenancy_note alter column tenant_id set statistics 1; analyze {Schema}.mt_doc_tenancy_note; analyze {Events}.mt_events; analyze {Events}.mt_streams;",
            Token);

        ServerSnapshot before = await SnapshotAsync(owned);

        await using TenancyStudio studio = TenancyStudio.Start(Configure, studio: Activity);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        ITenantOverviewService service = circuit.ServiceProvider.GetRequiredService<ITenantOverviewService>();

        TenantOverview overview = await service.GetAsync(Scope, new TenantOverviewRequest(), Token);

        overview.VolumesShown.Should().BeTrue();
        overview.Rows.Select(static x => x.TenantId).Should().Equal(Acme, Globex, Initech);
        overview.Rows.Should().OnlyContain(static x => x.Placement == TenantPlacement.SharedTables && x.ReadProblem == null);

        TenantRow acme = overview.Rows[0];
        Estimate(acme.Documents).Should().BeInRange(800, 1_200, "the statistics single acme out: within 20% of its 1,000 documents");
        acme.Documents.Text.Should().NotEndWith("+");
        Estimate(acme.Events).Should().BeInRange(800, 1_200, "events are conjoined here, and each write appended one");
        acme.Streams.Text.Should().StartWith("~");

        overview.Rows[1].Documents.Text.Should().Be("—", "globex falls below the one value the statistics keep");
        overview.Rows[1].Documents.Title.Should().Contain("fewer rows than the column statistics single out");

        overview.Rows.Should().OnlyContain(static x => x.LastEvent.Text == "not read", "no index finds one tenant's newest event in shared tables");
        overview.ProjectionsAreShared.Should().BeTrue();
        overview.HasProjectionsColumn.Should().BeFalse();

        // The first read of the activity-gated columns is one Activity entry; the next is none.
        StudioActionLogService log = studio.Services.GetRequiredService<StudioActionLogService>();
        log.GetLatest().Count(static x => x.Action == TenantOverviewService.AuditAction).Should().Be(1);
        _ = await service.GetAsync(Scope, new TenantOverviewRequest { Page = 0 }, Token);
        log.GetLatest().Count(static x => x.Action == TenantOverviewService.AuditAction).Should().Be(1, "once per circuit per database");

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned));
    }

    [PostgresFact]
    public async Task Tenant_partitioned_tables_give_partition_estimates_a_last_event_each_and_a_projections_cell()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "partitioned"], Token);
        string database = owned[0];

        void Configure(StoreOptions options)
        {
            options.Connection(database);
            Documents(options);

            // What UseTenantPartitionedEvents requires: conjoined event tenancy and a quick append mode.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            options.Policies.AllDocumentsAreMultiTenanted();

            // The documents too: one list partition per registered tenant, in a schema of its own.
            options.Policies.PartitionMultiTenantedDocumentsUsingMartenManagement("tenant_partitions");
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await seed.Advanced.AddMartenManagedTenantsAsync(Token, Acme, Globex, Initech);
            await WriteAsync(seed, Acme, 400);
            await WriteAsync(seed, Globex, 40);
        }

        await TenancyDatabases.ExecuteAsync(database, $"analyze {Schema}.mt_doc_tenancy_note; analyze {Events}.mt_events; analyze {Events}.mt_streams;", Token);

        ServerSnapshot before = await SnapshotAsync(owned);

        await using TenancyStudio studio = TenancyStudio.Start(Configure, studio: Activity);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        TenantOverview overview = await circuit.ServiceProvider.GetRequiredService<ITenantOverviewService>()
            .GetAsync(Scope, new TenantOverviewRequest(), Token);

        overview.Rows.Select(static x => x.TenantId).Should().Equal(Acme, Globex, Initech);
        overview.Rows.Should().OnlyContain(static x => x.Placement == TenantPlacement.OwnPartition && x.ReadProblem == null,
            "Marten-managed partitions: each registered tenant holds one");

        Estimate(overview.Rows[0].Documents).Should().BeInRange(320, 480, "acme's partition, after ANALYZE");
        Estimate(overview.Rows[1].Documents).Should().BeInRange(32, 48);
        overview.Rows[2].Documents.Text.Should().Be("~0", "initech holds a partition and no row; ");

        overview.Rows[0].LastEvent.Text.Should().StartWith("#");
        overview.Rows[0].LastEvent.At.Should().NotBeNull();
        overview.Rows[2].LastEvent.Text.Should().Be("—");
        overview.Rows[2].LastEvent.Title.Should().Be("no event yet");

        overview.HasProjectionsColumn.Should().BeTrue("Marten keeps progress per tenant here");
        overview.Rows.Should().OnlyContain(static x => x.Projections != null && x.Projections.Text == "no progress yet", "the daemon has not run");

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned));
    }

    [PostgresFact]
    public async Task A_database_per_tenant_reports_whole_table_estimates_and_each_rows_own_last_event()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "static_a", Prefix + "static_b"], Token);
        string first = owned[0];
        string second = owned[1];

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(first, "scope2-a").ForTenants(Acme);
                tenancy.AddMultipleTenantDatabase(second, "scope2-b").ForTenants(Globex);
            });

            Documents(options);
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await WriteAsync(seed, Acme, 120);
            await WriteAsync(seed, Globex, 30);
        }

        foreach (string database in new[] { first, second })
        {
            await TenancyDatabases.ExecuteAsync(database, $"analyze {Schema}.mt_doc_tenancy_note; analyze {Events}.mt_events; analyze {Events}.mt_streams;", Token);
        }

        ServerSnapshot before = await SnapshotAsync(owned);

        await using TenancyStudio studio = TenancyStudio.Start(Configure, studio: Activity);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        TenantOverview overview = await circuit.ServiceProvider.GetRequiredService<ITenantOverviewService>()
            .GetAsync(Scope, new TenantOverviewRequest(), Token);

        overview.Rows.Select(static x => x.TenantId).Should().Equal(Acme, Globex);
        overview.Rows.Should().OnlyContain(static x => x.Placement == TenantPlacement.OwnDatabase && x.ReadProblem == null);
        overview.Rows.Select(static x => x.DatabaseId).Distinct().Should().HaveCount(2, "each row is read from its own database");

        TenantRow acme = overview.Rows[0];
        TenantRow globex = overview.Rows[1];
        acme.Documents.Title.Should().Be("every row of this database");
        Estimate(acme.Documents).Should().BeInRange(96, 144);
        Estimate(globex.Documents).Should().BeInRange(24, 36);
        acme.LastEvent.Text.Should().StartWith("#");
        globex.LastEvent.Text.Should().StartWith("#");
        acme.Projections!.Text.Should().Be("no progress yet");

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned));
    }

    [PostgresFact]
    public async Task A_master_table_tenancy_places_each_tenant_in_its_database_with_no_enumeration_of_the_pages_own()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "master", Prefix + "master_globex"], Token);
        string master = owned[0];
        string globexDatabase = owned[1];

        const string tenants = "tenants";
        string lookupTable = PostgresStudio.Internal.Sql.SqlIdentifier.Qualify(tenants, "mt_tenant_databases");

        void Configure(StoreOptions options)
        {
            options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
            {
                tenancy.ConnectionString = master;
                tenancy.SchemaName = tenants;
            });

            Documents(options);
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            _ = await seed.Storage.AllDatabases();

            var tenancy = (MasterTableTenancy) seed.Options.Tenancy;
            await tenancy.AddDatabaseRecordAsync(Acme, master);
            await tenancy.AddDatabaseRecordAsync(Globex, globexDatabase);

            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await WriteAsync(seed, Acme, 50);
            await WriteAsync(seed, Globex, 20);
        }

        ServerSnapshot before = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, lookupTable, Token);
        before.Rows.Should().HaveCount(2, "the premise: two registered tenants, and the rows carry their xmin");

        await using TenancyStudio studio = TenancyStudio.Start(Configure, studio: Activity);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        TenantOverview overview = await circuit.ServiceProvider.GetRequiredService<ITenantOverviewService>()
            .GetAsync(Scope, new TenantOverviewRequest(), Token);

        overview.Rows.Select(static x => x.TenantId).Should().Equal(Acme, Globex);
        overview.Rows.Select(static x => x.DatabaseId).Distinct().Should().HaveCount(2, "one database each, placed from the enumeration the resolver made");
        overview.Rows.Should().OnlyContain(static x => x.Placement == TenantPlacement.OwnDatabase);

        // The lookup table's rows carry their xmin: a second read of the master table could not have written, and a write would show.
        before.ShouldBeUnchangedIn(await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, lookupTable, Token));
    }

    /// <summary>
    /// F2: more tenants than the listing holds, discovered through tier 3 (the tenant columns), say so - the 200-tenant sentence was
    /// never set for a store with one database, because only the union's size was counted.
    /// </summary>
    [PostgresFact]
    public async Task More_than_two_hundred_tenants_found_in_the_tables_set_the_truncation_flag()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "many"], Token);
        string database = owned[0];

        void Configure(StoreOptions options)
        {
            options.Connection(database);
            Documents(options);
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            for (int i = 0; i < 205; i++)
            {
                await WriteAsync(seed, "t" + i.ToString("000", CultureInfo.InvariantCulture), 1);
            }
        }

        await using TenancyStudio studio = TenancyStudio.Start(Configure, studio: Activity);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        TenantOverview overview = await circuit.ServiceProvider.GetRequiredService<ITenantOverviewService>()
            .GetAsync(Scope, new TenantOverviewRequest(), Token);

        overview.ListTruncated.Should().BeTrue("tier 3 stops at 200 and says there are more");
        overview.Rows.Should().HaveCount(25);
        overview.HasNext.Should().BeTrue();
    }

    private static void Activity(MartenStudioOptions options) =>
        options.Capabilities = new MartenStudioCapabilities { ViewDatabaseActivity = true };

    private static void Documents(StoreOptions options)
    {
        options.DatabaseSchemaName = Schema;
        options.Events.DatabaseSchemaName = Events;
        options.Schema.For<TenancyNote>().MultiTenanted();
        options.Events.AddEventType<TenancyNoteWritten>();
    }

    private static async Task WriteAsync(DocumentStore store, string tenantId, int documents)
    {
        await using IDocumentSession session = store.LightweightSession(tenantId);

        for (int i = 0; i < documents; i++)
        {
            session.Store(new TenancyNote { Id = Guid.NewGuid(), Text = tenantId + i.ToString(CultureInfo.InvariantCulture) });
            session.Events.StartStream(Guid.NewGuid(), new TenancyNoteWritten(tenantId));
        }

        await session.SaveChangesAsync(Token);
    }

    private static Task<ServerSnapshot> SnapshotAsync(OwnedDatabases owned) =>
        ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, lookupTable: null, Token);

    /// <summary>The number in a "~N" or "~N+" cell.</summary>
    private static double Estimate(TenantCell cell)
    {
        cell.Text.Should().StartWith("~", "a number, not a dash: " + cell.Title);
        return double.Parse(cell.Text.TrimStart('~').TrimEnd('+'), NumberStyles.Number, CultureInfo.InvariantCulture);
    }
}
