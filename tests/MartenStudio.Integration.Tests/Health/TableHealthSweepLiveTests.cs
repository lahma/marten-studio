using System.Globalization;

using JasperFx;

using MartenStudio.SampleDomain.Documents;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// Acceptance 2: the tables-and-indexes checks lock nothing they list and create nothing - measured with H1's
/// <see cref="HealthSweepProbe" /> from a second connection while each statement's transaction is still open.
/// </summary>
public class TableHealthSweepLiveTests(PostgresFixture postgres)
{
    private const int Tenants = 40;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A tenant-partitioned document table with forty partitions, one of which another session holds in
    /// <c>ACCESS EXCLUSIVE</c> mode for the whole sweep - Marten adding a tenant, say. Every table check still
    /// runs to the end, no statement holds a lock on any partition (or on any user table but <c>mt_hilo</c>,
    /// whose one-row read is T11's and is named), and the schema is exactly as it was.
    /// </summary>
    [PostgresFact]
    public async Task A_sweep_over_a_tenant_partitioned_table_locks_no_partition_and_creates_nothing()
    {
        const string schema = "health_tables_partitions";
        HealthSweepProbe probe = new(postgres);

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            schema,
            store: static o => o.Schema.For<HealthTenantThing>().MultiTenantedWithPartitioning(x =>
            {
                var list = x.ByList();
                for (int i = 0; i < Tenants; i++)
                {
                    string tenant = "tenant" + i.ToString("00", CultureInfo.InvariantCulture);
                    list.AddPartition(tenant, tenant);
                }
            }),
            services: services => services.AddSingleton<IHealthStatementObserver>(probe));
        await ApplyAsync(host.Store);

        string parent = host.Store.Options.FindOrResolveDocumentType(typeof(HealthTenantThing)).TableName.Name;
        (await PartitionCountAsync(schema, parent)).Should().BeGreaterThanOrEqualTo(Tenants, "the store really is tenant-partitioned");

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events");

        await using NpgsqlConnection migration = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
        await using (NpgsqlCommand hold = new("lock table " + SqlIdentifier.Qualify(schema, parent + "_tenant00") + " in access exclusive mode", migration, holding))
        {
            await hold.ExecuteNonQueryAsync(Token);
        }

        HealthReport report = await ReportAsync(host);

        await holding.RollbackAsync(Token);

        foreach (HealthCheckStatus status in report.Checks.Where(static x => x.Code.StartsWith('T')))
        {
            status.Outcome.Should().Be(HealthCheckOutcome.Checked,
                "{0} reads only the catalog, so a lock on a partition holds it up not at all: {1}", status.Code, status.Problem);
        }

        probe.StatementsObserved.Should().BePositive("so 'no lock' is not 'nothing ran'");
        probe.BeyondCatalogReads.Should().NotContain(x => x.Relation != null && x.Relation.StartsWith(parent + "_", StringComparison.Ordinal),
            "no check, of this packet or another, opens a partition: a list locks nothing it lists (D27)");

        IReadOnlyList<HeldLock> ours = TableCheckLocks(report, probe);
        ours.Should().OnlyContain(
            static x => OnTableOrItsIndexes(x, "mt_hilo") && x.Statement.Contains("mt_hilo", StringComparison.Ordinal),
            "the only user table a regular table check reads a row of is mt_hilo, for T11, and only once the catalog says it exists");
        probe.Locks.Where(x => TableStatements(report).Contains(x.Statement)).Should().Contain(static x => x.IsCatalogRead,
            "the table checks' statements were observed, so the empty set above is not vacuous");

        IReadOnlySet<string> after = await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events");
        after.Should().BeEquivalentTo(before, "a sweep never runs DDL");
    }

    /// <summary>
    /// Hard rule 14's own trap, kept shut: a store with an <c>int</c>-id type and no <c>mt_hilo</c> yet. T11 looks,
    /// says no HiLo id has been issued, and the table is still not there - because the advisor never touched
    /// <c>database.Sequences</c>, whose first touch creates it.
    /// </summary>
    [PostgresFact]
    public async Task T11_never_creates_mt_hilo_where_it_does_not_exist()
    {
        const string schema = "health_t11_nohilo";

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            schema,
            store: static o =>
            {
                o.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
                o.Schema.For<HealthCounterThing>();
            });

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events");

        HealthReport report = await ReportAsync(host);

        HealthCheckStatus status = HealthLiveHost.Status(report, "T11");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("mt_hilo does not exist");
        status.Statements.Should().ContainSingle("the existence check, and never the read").Which.Sql.Should().Contain("pg_catalog.pg_class");

        (await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events")).Should().BeEquivalentTo(before)
            .And.NotContain("relation:" + schema + ".mt_hilo:r");
        host.Logs.WarningsOrWorse.Should().BeEmpty("an absent mt_hilo is an expected state");
    }

    /// <summary>The deeper sample reads one visible table at a time and leaves nothing behind.</summary>
    [PostgresFact]
    public async Task The_deeper_sample_reads_only_visible_document_tables_and_creates_nothing()
    {
        const string schema = "health_t09_sweep";
        HealthSweepProbe probe = new(postgres);

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            schema,
            store: static o => o.Schema.For<HealthScanThing>(),
            studio: static o => o.IsDocumentTypeVisible = static type => type != typeof(Customer),
            services: services => services.AddSingleton<IHealthStatementObserver>(probe));
        await ApplyAsync(host.Store);

        await InsertAsync(postgres, TableOf<HealthScanThing>(host.Store), 1_200);
        await InsertAsync(postgres, TableOf<Customer>(host.Store), 1_200, "jsonb_build_object('Email', 'c' || g || '@example.invalid')");

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events");

        HealthReport report = await ReportAsync(host, deeper: true);

        HealthCheckStatus status = HealthLiveHost.Status(report, "T09");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Statements.Should().Contain(static x => x.Sql.Contains("tablesample system", StringComparison.Ordinal) && x.Sql.Contains("healthscanthing", StringComparison.Ordinal));
        status.Statements.Should().NotContain(static x => x.Sql.Contains("mt_doc_customer", StringComparison.Ordinal), "a hidden type's table is never sampled");

        TableCheckLocks(report, probe).Should().OnlyContain(
            static x => OnTableOrItsIndexes(x, "mt_hilo") || OnTableOrItsIndexes(x, "mt_doc_healthscanthing"),
            "the sample holds the one table it reads, T11 the one row of mt_hilo, and nothing else");
        probe.BeyondCatalogReads.Should().Contain(static x => x.Relation == "mt_doc_healthscanthing",
            "the sample really read the table, so the probe saw it");

        (await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events")).Should().BeEquivalentTo(before);
    }

    /// <summary>
    /// An <c>AccessShareLock</c> on <paramref name="table" /> or on one of its own indexes - which the planner
    /// opens, with that lock, to plan any statement over the table, so a one-table read holds both. Marten names
    /// a table's indexes after it (<c>pkey_mt_hilo_entity_name</c>).
    /// </summary>
    private static bool OnTableOrItsIndexes(HeldLock held, string table) =>
        held.Mode == "AccessShareLock" && held.Relation is { } relation && relation.Contains(table, StringComparison.Ordinal);

    /// <summary>The statements the table checks ran, as their status rows record them.</summary>
    private static HashSet<string> TableStatements(HealthReport report) =>
        [.. report.Checks.Where(static x => x.Code.StartsWith('T')).SelectMany(static x => x.Statements).Select(static x => x.Sql)];

    /// <summary>
    /// The locks beyond a catalog read that the table checks' own statements held - not another packet's
    /// checks', which read rows of their own (the daemon checks read the progression table).
    /// </summary>
    private static IReadOnlyList<HeldLock> TableCheckLocks(HealthReport report, HealthSweepProbe probe)
    {
        HashSet<string> statements = TableStatements(report);
        return [.. probe.BeyondCatalogReads.Where(x => statements.Contains(x.Statement))];
    }

    private async Task<long> PartitionCountAsync(string schema, string parent)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand count = new(
            "select pg_catalog.count(*) from pg_catalog.pg_inherits i join pg_catalog.pg_class p on p.oid = i.inhparent "
            + "join pg_catalog.pg_namespace n on n.oid = p.relnamespace where n.nspname = @schema and p.relname = @parent",
            connection);
        count.Parameters.AddWithValue("schema", schema);
        count.Parameters.AddWithValue("parent", parent);

        return (long) (await count.ExecuteScalarAsync(Token))!;
    }
}
