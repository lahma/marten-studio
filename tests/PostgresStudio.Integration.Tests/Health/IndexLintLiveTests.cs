using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Integration.Tests.Health;

/// <summary>
/// W5-B1's checks on a plain database, through Postgres Studio's whole-database table source: T15 (a foreign key no index
/// covers), T19 (partitions against the lock table), T20 (range partitions running out), T25 (a bloated index) and T26 (an
/// integer key running out) each made to fire with plain SQL in the test's own schema, and all of them quiet on healthy
/// tables - with a sweep that locks nothing it lists and writes nothing.
/// </summary>
/// <remarks>
/// The shared fixture is <c>postgres:17-alpine</c> with the server's defaults - <c>max_locks_per_transaction = 64</c>,
/// <c>max_connections = 100</c> - which is what T19's numbers below are. Every write ends with a forced statistics flush.
/// T25's test alone has a database of its own: its <c>VACUUM</c> must remove the deleted rows' index entries, and any session
/// of the same database with an older snapshot keeps them.
/// </remarks>
public class IndexLintLiveTests(PostgresFixture postgres)
{
    private static readonly string[] LintCodes = ["T15", "T19", "T20", "T25", "T26"];

    /// <summary>The studio's clock: 2026-10-26, so a monthly range that ended on 2026-10-01 has run out.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthFires("T15")]
    public async Task A_foreign_key_on_a_table_of_8_MiB_with_no_index_leading_with_it_is_T15_with_a_concurrent_index()
    {
        const string schema = "ps_lint_t15";
        await using StudioHost host = await HostAsync(schema);

        await ExecuteAsync(
            "create table ps_lint_t15.parent (id int primary key)",
            "create table ps_lint_t15.child (id int primary key, parent_id int not null references ps_lint_t15.parent (id), note text not null)",
            "insert into ps_lint_t15.parent select g from generate_series(1, 1000) g",
            "insert into ps_lint_t15.child select g, 1 + g % 1000, 'note ' || g from generate_series(1, 250000) g");

        // ANALYZE sets relpages, which the check sizes the table by.
        await ExecuteAsync("analyze ps_lint_t15.child", "analyze ps_lint_t15.parent");

        HealthReportFinding finding = OnTable(await ReportAsync(host), "T15", schema + ".child").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Subject.Should().Be(schema + ".child.child_parent_id_fkey");
        finding.Evidence.Should().Contain(new HealthEvidence("References", schema + ".parent (id)"));
        finding.Finding.GatedSchemas.Should().BeEmpty("both ends are in the browsable schema");
        finding.Finding.Remedy!.Code.Should().Be("CREATE INDEX CONCURRENTLY ON \"ps_lint_t15\".\"child\" (\"parent_id\");");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    [HealthFires("T19")]
    public async Task Two_hundred_partitions_of_two_indexes_each_are_more_locks_than_max_locks_per_transaction_and_T19()
    {
        const string schema = "ps_lint_t19";
        await using StudioHost host = await HostAsync(schema);

        await ExecuteAsync(
            "create table ps_lint_t19.measures (k int not null, a int, b int) partition by list (k)",
            "create index measures_by_a on ps_lint_t19.measures (a)",
            "create index measures_by_b on ps_lint_t19.measures (b)",
            "do $$ begin for i in 1..200 loop execute pg_catalog.format('create table ps_lint_t19.measures_%s partition of ps_lint_t19.measures for values in (%s)', i, i); end loop; end $$");

        HealthReportFinding finding = OnTable(await ReportAsync(host), "T19", schema + ".measures").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Info, "603 locks are past 64 a transaction, and far from the 6,400 the server holds");
        finding.Finding.Variant.Should().Be("per-transaction");
        finding.Evidence.Should().Contain(static x => x.Label == "Locks one unpruned query takes" && x.Value.EndsWith("= 603", StringComparison.Ordinal),
            "1 + 200 partitions + 400 partition indexes + 2 partitioned indexes (16+)");
        finding.Finding.Remedy!.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        finding.Finding.Remedy.Code.Should().EndWith("max_locks_per_transaction = 1024");
    }

    [PostgresFact]
    [HealthFires("T20")]
    public async Task A_monthly_range_that_ended_with_no_default_is_a_critical_T20_and_rows_in_a_default_a_warning()
    {
        const string schema = "ps_lint_t20";
        await using StudioHost host = await HostAsync(schema);

        await ExecuteAsync(
            "create table ps_lint_t20.events (at timestamptz not null, v int) partition by range (at)",
            "create table ps_lint_t20.events_2026_08 partition of ps_lint_t20.events for values from ('2026-08-01 00:00+00') to ('2026-09-01 00:00+00')",
            "create table ps_lint_t20.events_2026_09 partition of ps_lint_t20.events for values from ('2026-09-01 00:00+00') to ('2026-10-01 00:00+00')",
            "create table ps_lint_t20.readings (at date not null, v int) partition by range (at)",
            "create table ps_lint_t20.readings_2026_12 partition of ps_lint_t20.readings for values from ('2026-12-01') to ('2027-01-01')",
            "create table ps_lint_t20.readings_default partition of ps_lint_t20.readings default",
            "insert into ps_lint_t20.readings select date '2027-02-01' + g % 20, g from generate_series(1, 500) g");
        await ExecuteAsync("analyze ps_lint_t20.readings");

        HealthReport report = await ReportAsync(host);

        HealthReportFinding runningOut = OnTable(report, "T20", schema + ".events").Should().ContainSingle().Subject;
        runningOut.Severity.Should().Be(HealthSeverity.Critical, "no DEFAULT partition: an insert for October fails");
        runningOut.Finding.Variant.Should().Be(RangePartitionsCheck.RunningOutVariant);
        runningOut.Finding.Title.Should().Contain("ran out at 2026-10-01 00:00 UTC");
        runningOut.Finding.Remedy!.Code.Should().Be(
            "CREATE TABLE \"ps_lint_t20\".\"events_p20261001\" PARTITION OF \"ps_lint_t20\".\"events\" FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00');",
            "a calendar month on from the newest bound, read from the catalog without opening a partition");

        HealthReportFinding defaults = OnTable(report, "T20", schema + ".readings").Should().ContainSingle().Subject;
        defaults.Finding.Variant.Should().Be(RangePartitionsCheck.DefaultRowsVariant, "the newest bound is 67 days ahead, but 500 rows found no range");
        defaults.Severity.Should().Be(HealthSeverity.Warning);
        defaults.Finding.Remedy.Should().BeNull();
    }

    [PostgresFact]
    [HealthFires("T25")]
    public async Task A_primary_key_index_left_at_its_size_by_deleting_almost_every_row_is_a_T25_warning_with_a_concurrent_reindex()
    {
        const string schema = "ps_lint_t25";

        // A database of its own, not a schema of the shared one. VACUUM removes a deleted row's index entries only once no
        // session of the table's database can still see the row (PostgreSQL 14 and later count that database's sessions
        // alone), and in the shared database other classes run beside this one, some holding a snapshot or a transaction id
        // - an ACCESS EXCLUSIVE lock takes one - for seconds. A VACUUM in that window removes nothing and leaves the index's
        // catalog row as the empty table's CREATE left it, one page and no entries, so T25 is rightly quiet (FLK-T25).
        string database = await postgres.CreateDatabaseAsync(schema, Token);
        await ExecuteInAsync(database, "create schema ps_lint_t25");
        await using StudioHost host = Host(database, schema);

        // 3.5 million int4 entries make a pkey of about 75 MiB (22.6 bytes an entry, measured on 17); deleting 39 rows in 40
        // and vacuuming leaves the pages and 87,500 entries - some 900 bytes an entry, past the issue's 640. Autovacuum is
        // off for the table, so the VACUUM below is the only one: an autovacuum worker that came for the insert would hold
        // pages and the table's lock while it ran.
        await ExecuteInAsync(
            database,
            "create table ps_lint_t25.ledger (id int primary key) with (autovacuum_enabled = false)",
            "insert into ps_lint_t25.ledger select g from generate_series(1, 3500000) g");
        await ExecuteInAsync(database, "delete from ps_lint_t25.ledger where id % 40 <> 0");

        // FREEZE, which makes the VACUUM aggressive and so a VACUUM that waits for a page's cleanup lock instead of skipping
        // the page. A plain VACUUM skips a heap page another backend has pinned for that instant - the checkpointer or the
        // background writer writing out the pages the delete just dirtied, on a CI disk - and the deleted rows on it stay in
        // the index: "tuples missed: 220 dead from 1 pages not removed due to cleanup lock contention" for each page, and a
        // pkey that read 100,060 here, 12,560 past the live rows' 87,500 (issue #27). Reproduced and measured on 17 with one
        // pinned page: a plain VACUUM leaves its 220, VACUUM (FREEZE) waits for the pin and removes every one.
        await ExecuteInAsync(database, "vacuum (freeze) ps_lint_t25.ledger");

        // The precondition, read where T25 reads it: the index's own reltuples, which only a VACUUM that removed entries writes.
        // With every page cleaned it is the 87,500 live rows' alone. T25's line is some 122,800.
        (await ScalarInAsync(database, "select c.reltuples::int8 from pg_catalog.pg_class c where c.oid = 'ps_lint_t25.ledger_pkey'::pg_catalog.regclass"))
            .Should().BeInRange(87_500, 100_000, "VACUUM removes the deleted rows' index entries once no session of this database can still see the rows");

        HealthReportFinding finding = OnTable(await ReportAsync(host), "T25", schema + ".ledger").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Variant.Should().Be(BloatedIndexCheck.IssueRuleVariant);
        finding.Finding.Subject.Should().Be(schema + ".ledger.ledger_pkey");
        finding.Finding.Remedy!.Code.Should().Be("REINDEX INDEX CONCURRENTLY \"ps_lint_t25\".\"ledger_pkey\";");
    }

    [PostgresFact]
    [HealthFires("T26")]
    public async Task An_int4_identity_seventy_percent_through_its_range_is_a_T26_warning_that_widens_the_column()
    {
        const string schema = "ps_lint_t26";
        await using StudioHost host = await HostAsync(schema);

        await ExecuteAsync(
            "create table ps_lint_t26.tickets (id int generated by default as identity primary key, note text)",
            "alter table ps_lint_t26.tickets alter column id restart with 1500000000",
            "insert into ps_lint_t26.tickets (note) select 'n' || g from generate_series(1, 1000) g");
        await ExecuteAsync("analyze ps_lint_t26.tickets");

        HealthReportFinding finding = OnTable(await ReportAsync(host), "T26", schema + ".tickets").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning, "1,500,000,999 is 70% of 2,147,483,647");
        finding.Finding.Subject.Should().Be(schema + ".tickets.id");
        finding.Finding.Variant.Should().Be("column");
        finding.Finding.Remedy!.Code.Should().Be("ALTER TABLE \"ps_lint_t26\".\"tickets\" ALTER COLUMN \"id\" TYPE bigint;",
            "an identity column's sequence follows the column's type");
    }

    /// <summary>
    /// A covered foreign key on a table past T15's size, three months of a range ahead with an empty default, a small
    /// partitioned table, a young identity and a dense index: quiet on every W5-B1 check, by checks that looked - and the
    /// sweep that looked locked nothing it listed and wrote nothing.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("T15")]
    [HealthQuiet("T19")]
    [HealthQuiet("T20")]
    [HealthQuiet("T25")]
    [HealthQuiet("T26")]
    public async Task Covered_keys_a_range_kept_ahead_and_young_keys_are_quiet_and_the_sweep_locks_and_writes_nothing()
    {
        const string schema = "ps_lint_quiet";
        HealthSweepProbe probe = new(postgres);
        await using StudioHost host = await HostAsync(schema, probe);

        await ExecuteAsync(
            "create table ps_lint_quiet.parent (id int generated always as identity primary key, name text)",
            "create table ps_lint_quiet.child (id int primary key, parent_id int not null references ps_lint_quiet.parent (id), note text not null)",
            "create index child_by_parent on ps_lint_quiet.child (parent_id)",
            "insert into ps_lint_quiet.parent (name) select 'p' || g from generate_series(1, 1000) g",
            "insert into ps_lint_quiet.child select g, 1 + g % 1000, 'note ' || g from generate_series(1, 250000) g",
            "create table ps_lint_quiet.events (at timestamptz not null, v int) partition by range (at)",
            "create index events_by_v on ps_lint_quiet.events (v)",
            "create table ps_lint_quiet.events_2026_10 partition of ps_lint_quiet.events for values from ('2026-10-01 00:00+00') to ('2026-11-01 00:00+00')",
            "create table ps_lint_quiet.events_2026_11 partition of ps_lint_quiet.events for values from ('2026-11-01 00:00+00') to ('2026-12-01 00:00+00')",
            "create table ps_lint_quiet.events_2026_12 partition of ps_lint_quiet.events for values from ('2026-12-01 00:00+00') to ('2027-01-01 00:00+00')",
            "create table ps_lint_quiet.events_default partition of ps_lint_quiet.events default");

        // T25 judges a dense index past its 64 MiB line - (id) INCLUDE (payload), whose leaf entries carry the payload: counted
        // by the key alone it would read as some six times bloated (W5-B1 review F4 and F6).
        await ExecuteAsync(
            "create table ps_lint_quiet.ledger (id int primary key, payload text not null)",
            "create index ledger_by_id_inc on ps_lint_quiet.ledger (id) include (payload)",
            "insert into ps_lint_quiet.ledger select g, pg_catalog.repeat('p', 90) || g from pg_catalog.generate_series(1, 600000) g");
        await ExecuteAsync("analyze ps_lint_quiet.parent", "analyze ps_lint_quiet.child", "analyze ps_lint_quiet.events", "analyze ps_lint_quiet.ledger");
        (await ScalarAsync("select pg_catalog.pg_relation_size('ps_lint_quiet.ledger_by_id_inc'::pg_catalog.regclass)"))
            .Should().BeGreaterThanOrEqualTo(BloatedIndexCheck.MinIndexBytes, "the dense index is one T25 judges");

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, schema);

        HealthReport report = await ReportAsync(host);

        IReadOnlyList<HealthReportFinding> ours = [.. report.Findings.Where(x =>
            LintCodes.Contains(x.Code) && x.Finding.Subject?.StartsWith(schema + ".", StringComparison.Ordinal) == true)];
        ours.Should().BeEmpty(string.Join(" | ", ours.Select(static x => x.Code + " " + x.Finding.Subject + ": " + x.Finding.Title)));

        foreach (string code in LintCodes)
        {
            HealthCheckStatus status = report.Checks.Single(x => x.Code == code);
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} is quiet because it looked, not because it did not run: {1}", code, status.Problem);
        }

        probe.StatementsObserved.Should().BePositive();
        probe.BeyondCatalogReads.Should().BeEmpty("a sweep locks nothing it lists (D27)");
        (await HealthSweepProbe.SnapshotAsync(postgres, schema)).Should().BeEquivalentTo(before, "a sweep writes nothing");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>A host over the <c>postgres</c> database whose only browsable schema is <paramref name="schema" />, created empty.</summary>
    private async Task<StudioHost> HostAsync(string schema, IHealthStatementObserver? observer = null)
    {
        await postgres.CreateSchemaAsync(schema, Token);
        return Host(postgres.ConnectionString, schema, observer);
    }

    /// <summary>A host over the database <paramref name="connectionString" /> names, whose only browsable schema is <paramref name="schema" />.</summary>
    private static StudioHost Host(string connectionString, string schema, IHealthStatementObserver? observer = null) =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
                services.AddSingleton<TimeProvider>(new ManualClock(Start));

                if (observer is not null)
                {
                    services.AddSingleton(observer);
                }
            },
            options => options.BrowsableSchemas.Add(schema));

    private static Task<HealthReport> ReportAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token));

    private static IReadOnlyList<HealthReportFinding> OnTable(HealthReport report, string code, string tableKey) =>
        [.. report.Findings.Where(x => x.Code == code
            && (x.Finding.Subject == tableKey || x.Finding.Subject?.StartsWith(tableKey + ".", StringComparison.Ordinal) == true))];

    /// <summary>One number the test reads for itself.</summary>
    private Task<long> ScalarAsync(string sql) => ScalarInAsync(postgres.ConnectionString, sql);

    /// <summary>One number the test reads for itself, in the database <paramref name="connectionString" /> names.</summary>
    private static async Task<long> ScalarInAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Runs statements on one connection of the test's own, then flushes its statistics.</summary>
    private Task ExecuteAsync(params string[] statements) => ExecuteInAsync(postgres.ConnectionString, statements);

    /// <summary>Runs statements on one connection of the test's own to the database <paramref name="connectionString" /> names, then flushes its statistics.</summary>
    private static async Task ExecuteInAsync(string connectionString, params string[] statements)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlCommand flush = new("select pg_catalog.pg_stat_force_next_flush()", connection);
        await flush.ExecuteNonQueryAsync(Token);
    }
}
