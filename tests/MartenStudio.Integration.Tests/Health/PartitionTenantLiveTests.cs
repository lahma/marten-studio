using Marten;

using MartenStudio.Integration.Tests.Database;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>A document Marten partitions by fixed ranges of a number, for X4's T20 live test.</summary>
public sealed class X4LedgerDoc
{
    public Guid Id { get; set; }

    public long Number { get; set; }
}

/// <summary>A document Marten partitions by fixed ranges of a moment, for X4's T20 live test.</summary>
public sealed class X4ReadingDoc
{
    public Guid Id { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>A conjoined document whose rows one tenant dominates, for T18.</summary>
public sealed class X4SkewDoc
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// X4 on a live Marten store, each test in a database of its own (FLK-T25's lesson): T20 over a static <c>ByRange()</c> - an
/// integer range whose rows reach its newest range and a time range a month from its end, each given the <c>.AddRange(...)</c>
/// line its declaration takes - and T18 over a conjoined table one tenant holds 70% of, read from <c>pg_stats</c> and named
/// only past the activity gate and that tenant's store policy; over a table Marten partitions by tenant, read from its
/// partitions behind somebody's <c>ACCESS EXCLUSIVE</c> on them; quiet over balanced tenants; and not checked for a
/// <c>SqlConsoleRole</c> that may not read <c>tenant_id</c>.
/// </summary>
/// <remarks>
/// Run at both Marten ends (<c>-p:MartenStudioMartenVersion</c>): <c>ByRange()</c>, <c>AddRange&lt;T&gt;</c>,
/// <c>MultiTenantedWithPartitioning</c> and <c>BulkInsertAsync(tenantId, ...)</c> are the same at 9.39.0 and 9.54.2.
/// </remarks>
public class PartitionTenantLiveTests(PostgresFixture postgres)
{
    /// <summary>The studio's clock: 2026-10-26, so a range ending 2026-11-01 is within one width of now.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] Tenants = ["acme", "globex", "initech", "umbrella", "hooli", "soylent", "wonka"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <remarks>
    /// Not marked <c>[HealthFires("T20")]</c>: T20 is a shared check, proven in <c>PostgresStudio.Integration.Tests</c>
    /// (<c>HealthLiveTestConventionTests.ProvenInPostgresStudioSuite</c>); this is the proof of its Marten remedy.
    /// </remarks>
    [PostgresFact]
    public async Task A_static_ByRange_whose_rows_reach_its_newest_range_or_whose_time_runs_out_is_given_the_AddRange_line()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "x4_marten_range");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database,
            store: static o =>
            {
                o.Schema.For<X4LedgerDoc>().PartitionOn(x => x.Number, x => x.ByRange().AddRange("p0", 0L, 1000L).AddRange("p1000", 1000L, 2000L));
                o.Schema.For<X4ReadingDoc>().PartitionOn(x => x.At, x => x.ByRange().AddRange(
                    "p202610", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
            },
            clock: new ManualClock(Start),
            sample: false);
        await host.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        X4LedgerDoc[] numbered = [.. Enumerable.Range(0, 1_500).Select(static i => new X4LedgerDoc { Id = Guid.NewGuid(), Number = i })];
        await host.Store.BulkInsertAsync(numbered, cancellation: Token);
        string ledger = TableOf<X4LedgerDoc>(host.Store);
        string readings = TableOf<X4ReadingDoc>(host.Store);
        await AnalyzeAsync(host, ledger, numbered.Length);

        HealthReport report = await host.ReportAsync();

        HealthReportFinding rows = About(report, "T20", ledger).Should().ContainSingle().Subject;
        rows.Severity.Should().Be(HealthSeverity.Warning, "an integer key has no clock, and Weasel always gives a ByRange table a DEFAULT partition");
        rows.Finding.Variant.Should().Be(RangePartitionsCheck.RunningOutVariant);
        rows.Finding.Title.Should().EndWith("reach its newest range partition, which ends at 2,000");
        rows.Finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Configuration);
        rows.Finding.Remedy.Code.Should().StartWith("// opts.Schema.For<X4LedgerDoc>() declares this table's ranges with ByRange()")
            .And.EndWith(".AddRange(\"p2000\", 2000L, 3000L)");

        HealthReportFinding time = About(report, "T20", readings).Should().ContainSingle().Subject;
        time.Severity.Should().Be(HealthSeverity.Warning, "the DEFAULT partition catches November's rows");
        time.Finding.Remedy!.Code.Should().EndWith(
            ".AddRange(\"p20261101\", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero))");

        // The line is what Weasel itself would write: declaring it creates the partition, and the next apply finds no drift.
        await using ConnectionsLiveHost extended = await ConnectionsLiveHost.CreateAsync(
            database,
            store: static o => o.Schema.For<X4LedgerDoc>().PartitionOn(x => x.Number, x => x.ByRange()
                .AddRange("p0", 0L, 1000L).AddRange("p1000", 1000L, 2000L).AddRange("p2000", 2000L, 3000L)),
            sample: false);
        await extended.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        (await ScalarAsync(extended, "select pg_catalog.count(*) from pg_catalog.pg_class where relname = '" + ledger.Split('.')[1].Trim('"') + "_p2000'"))
            .Should().Be(1, "the remedy's AddRange line made the next range");
    }

    [PostgresFact]
    [HealthFires("T18")]
    public async Task One_tenant_holding_most_of_a_conjoined_table_is_named_only_past_the_activity_gate_and_its_store_policy()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "x4_marten_skew");
        string table;

        await using (ConnectionsLiveHost seed = await ConnectionsLiveHost.CreateAsync(database, store: Conjoined, sample: false))
        {
            await seed.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            table = await SeedAsync(seed, ("acme", 84_000), ("globex", 6_000), ("initech", 6_000), ("umbrella", 6_000), ("hooli", 6_000), ("soylent", 6_000), ("wonka", 6_000));
        }

        // Closed: ViewDatabaseActivity off. The finding names a tenant, so it is counted and never described.
        await using (ConnectionsLiveHost closed = await ConnectionsLiveHost.CreateAsync(database, store: Conjoined, sample: false))
        {
            HealthReport report = await closed.ReportAsync();

            report.Findings.Should().NotContain(static x => x.Code == "T18");
            ConnectionsLiveHost.Status(report, "T18").FindingsWithheld.Should().BeTrue("a withheld finding is never a \"nothing found\"");
            Everything(report).Should().NotContain("acme");
        }

        // Activity on, but the store policy does not show this visitor acme.
        await using (ConnectionsLiveHost refused = await ConnectionsLiveHost.CreateAsync(
            database,
            store: Conjoined,
            studio: static o =>
            {
                o.Capabilities.ViewDatabaseActivity = true;
                o.StoreAuthorizationPolicy = "store";
            },
            services: static s => s.AddSingleton<IAuthorizationService>(new ResourcePolicy(static r => r.TenantId != "acme")),
            sample: false))
        {
            HealthReport report = await refused.ReportAsync();

            report.Findings.Should().NotContain(static x => x.Code == "T18");
            report.WithheldReason.Should().Contain("names a tenant the store policy does not show you");
            Everything(report).Should().NotContain("acme");
        }

        // Activity on, and the policy shows acme: the finding, tenant and all.
        await using ConnectionsLiveHost open = await ConnectionsLiveHost.CreateAsync(
            database,
            store: Conjoined,
            studio: static o => o.Capabilities.ViewDatabaseActivity = true,
            sample: false);

        HealthReportFinding finding = (await open.ReportAsync()).Findings.Should().ContainSingle(static x => x.Code == "T18").Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Finding.Subject.Should().Be(table);
        finding.Finding.TenantId.Should().Be("acme");
        finding.Finding.Title.Should().StartWith("One tenant holds ").And.EndWith(" of the X4SkewDoc documents").And.NotContain("acme");
        finding.Evidence.Should().Contain(new HealthEvidence("Tenant", "acme"));
        Share(finding).Should().BeInRange(0.65, 0.75, "84,000 of 120,000 rows, as ANALYZE's sample of 30,000 measures it");
        finding.Evidence.Should().Contain(static x => x.Label == "Measured from" && x.Value.StartsWith("pg_stats' most common values of tenant_id", StringComparison.Ordinal));
    }

    [PostgresFact]
    [HealthFires("T18")]
    public async Task A_table_Marten_partitions_by_tenant_is_judged_by_its_partitions_behind_a_held_lock_and_never_lists_them()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "x4_marten_tenant_partitions");
        HealthSweepProbe probe = new(database);

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database,
            store: static o => o.Schema.For<X4SkewDoc>().MultiTenantedWithPartitioning(static x =>
            {
                var list = x.ByList();

                foreach (string tenant in Tenants)
                {
                    list.AddPartition(tenant, tenant);
                }
            }),
            studio: static o => o.Capabilities.ViewDatabaseActivity = true,
            services: s => s.AddSingleton<IHealthStatementObserver>(probe),
            sample: false);
        await host.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        string table = await SeedAsync(host, ("acme", 84_000), ("globex", 6_000), ("initech", 6_000), ("umbrella", 6_000), ("hooli", 6_000), ("soylent", 6_000), ("wonka", 6_000));
        string parent = table.Split('.')[1];
        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(database, "public");

        await using NpgsqlConnection migration = await host.OpenAsync();
        await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
        await using (NpgsqlCommand hold = new(
            "lock table " + SqlIdentifier.Qualify("public", parent) + ", " + SqlIdentifier.Qualify("public", parent + "_acme") + " in access exclusive mode",
            migration,
            holding))
        {
            await hold.ExecuteNonQueryAsync(Token);
        }

        HealthReport report = await host.ReportAsync();

        await holding.RollbackAsync(Token);

        HealthCheckStatus status = ConnectionsLiveHost.Status(report, "T18");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, "T18 reads the catalog, so a lock on the table and a partition holds it up not at all: {0}", status.Problem);

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "T18").Subject;
        finding.Finding.TenantId.Should().Be("acme", "read from the largest partition's bound, FOR VALUES IN ('acme')");
        finding.Evidence.Should().Contain(new HealthEvidence("Its rows", "~84,000 of ~120,000 (70%)"), "each partition's own rows, as ANALYZE counted them")
            .And.Contain(new HealthEvidence("Tenants", "7 tenant partitions"));
        finding.Finding.WhyItMatters.Should().Contain("partitioned by tenant");
        Everything(report).Should().NotContainAny(Tenants.Select(x => parent + "_" + x), "a tenant partition is never named: the names are the tenant list (D27)");

        probe.StatementsObserved.Should().BePositive();
        probe.BeyondCatalogReads.Should().NotContain(x => x.Relation != null && x.Relation.StartsWith(parent, StringComparison.Ordinal),
            "no statement opens the table or a partition of it: a list locks nothing it lists (D27)");
        (await HealthSweepProbe.SnapshotAsync(database, "public")).Should().BeEquivalentTo(before, "a sweep writes nothing");
    }

    [PostgresFact]
    [HealthQuiet("T18")]
    public async Task Tenants_of_a_size_are_quiet_and_the_row_names_none_of_them()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "x4_marten_balanced");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database,
            store: Conjoined,
            studio: static o => o.Capabilities.ViewDatabaseActivity = true,
            sample: false);
        await host.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await SeedAsync(host, [.. Tenants.Select(static x => (x, 20_000))]);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "T18");
        HealthCheckStatus status = ConnectionsLiveHost.Status(report, "T18");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.FindingsWithheld.Should().BeFalse("quiet because it looked, not because it kept a finding back");
        status.Note.Should().StartWith("The largest tenant holds ").And.NotContainAny(Tenants);
    }

    [PostgresFact]
    public async Task A_console_role_that_may_not_read_tenant_id_is_told_which_grant_T18_needs()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "x4_marten_narrow");
        await using (NpgsqlConnection admin = new(database))
        {
            await admin.OpenAsync(Token);
            await using NpgsqlCommand role = new(
                "do $$ begin if not exists (select from pg_catalog.pg_roles where rolname = 'x4_narrow') then create role x4_narrow nologin; end if; end $$",
                admin);
            await role.ExecuteNonQueryAsync(Token);
        }

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database,
            store: Conjoined,
            studio: static o => o.SqlConsoleRole = "x4_narrow",
            sample: false);
        await host.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        string table = await SeedAsync(host, ("acme", 84_000), ("globex", 6_000), ("initech", 6_000), ("umbrella", 6_000), ("hooli", 6_000), ("soylent", 6_000), ("wonka", 6_000));

        HealthCheckStatus status = ConnectionsLiveHost.Status(await host.ReportAsync(), "T18");

        status.Outcome.Should().Be(HealthCheckOutcome.Unavailable, "pg_stats shows tenant_id only to a role that may SELECT it, never to pg_monitor");
        (status.Problem ?? status.Note).Should().Contain("Not checked: this needs SELECT on column tenant_id of " + table + " for role x4_narrow.")
            .And.NotContain("acme");
    }

    private static void Conjoined(StoreOptions options) => options.Schema.For<X4SkewDoc>().MultiTenanted();

    /// <summary>Bulk-inserts each tenant's documents, analyses the table, and returns it as <c>schema.table</c>.</summary>
    private static async Task<string> SeedAsync(ConnectionsLiveHost host, params (string Tenant, int Rows)[] tenants)
    {
        foreach ((string tenant, int rows) in tenants)
        {
            X4SkewDoc[] documents = [.. Enumerable.Range(0, rows).Select(i => new X4SkewDoc { Id = Guid.NewGuid(), Name = tenant + i })];
            await host.Store.BulkInsertAsync(tenant, documents, cancellation: Token);
        }

        var name = host.Store.Options.FindOrResolveDocumentType(typeof(X4SkewDoc)).TableName;
        string table = SqlIdentifier.Qualify(name.Schema, name.Name);
        await AnalyzeAsync(host, table, tenants.Sum(static x => x.Rows));
        return name.Schema + "." + name.Name;
    }

    /// <summary>
    /// Analyses <paramref name="table" /> once the store's own sessions have reported its <paramref name="rows" /> inserts to
    /// the server - waited on, never slept on. A pooled session reports when idle, within ten seconds; an ANALYZE before that
    /// counts the rows, and the report then adds them again, so <c>n_live_tup</c> would read double.
    /// </summary>
    private static async Task AnalyzeAsync(ConnectionsLiveHost host, string table, long rows)
    {
        string counted =
            "select coalesce(pg_catalog.sum(pg_catalog.pg_stat_get_live_tuples(c.oid)), 0)::int8 from pg_catalog.pg_class c "
            + "where c.oid = '" + table + "'::pg_catalog.regclass "
            + "or c.oid in (select i.inhrelid from pg_catalog.pg_inherits i where i.inhparent = '" + table + "'::pg_catalog.regclass)";

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(250));

        while (await ScalarAsync(host, counted) < rows)
        {
            await timer.WaitForNextTickAsync(deadline.Token);
        }

        await ExecuteAsync(host, "analyze " + table);
    }

    private static string TableOf<T>(IDocumentStore store)
    {
        var name = store.Options.FindOrResolveDocumentType(typeof(T)).TableName;
        return name.Schema + "." + name.Name;
    }

    private static IReadOnlyList<HealthReportFinding> About(HealthReport report, string code, string tableKey) =>
        [.. report.Findings.Where(x => x.Code == code && x.Finding.Subject == tableKey)];

    /// <summary>Every sentence a report shows, for a test that holds a name out of all of it.</summary>
    private static string Everything(HealthReport report) =>
        string.Join(
            "\n",
            report.Findings.SelectMany(static x => new[] { x.Finding.Title, x.Finding.WhyItMatters, x.Finding.Subject ?? string.Empty, x.Finding.Caveat ?? string.Empty }
                .Concat(x.Evidence.Select(static e => e.Value)))
                .Concat(report.Checks.Select(static x => (x.Note ?? string.Empty) + " " + (x.Problem ?? string.Empty))));

    /// <summary>The share a T18 finding's "Its rows" evidence states, as a fraction.</summary>
    private static double Share(HealthReportFinding finding)
    {
        string value = finding.Evidence.Single(static x => x.Label == "Its rows").Value;
        string percent = value[(value.LastIndexOf('(') + 1)..value.LastIndexOf('%')];
        return double.Parse(percent, System.Globalization.CultureInfo.InvariantCulture) / 100;
    }

    private static async Task ExecuteAsync(ConnectionsLiveHost host, string sql)
    {
        await using NpgsqlConnection connection = await host.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<long> ScalarAsync(ConnectionsLiveHost host, string sql)
    {
        await using NpgsqlConnection connection = await host.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }
}
