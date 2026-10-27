using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Maintenance;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Integration.Tests.Health;

/// <summary>
/// X4 on a plain database: T20's ranges against now and against their data - a monthly range one width from its end, one
/// three months ahead, a default partition with rows, an integer range whose rows reach its newest partition - T19's capacity
/// Warning on a server with a small lock table, and the sweep behind somebody's <c>ACCESS EXCLUSIVE</c> on a partitioned table
/// and a partition, which locks nothing it lists and writes nothing.
/// </summary>
/// <remarks>
/// Every test has a database of its own (FLK-T25's lesson): the catalog's row estimates and the statistics counters these
/// checks read are the database's, and another class's sessions in a shared one are not this test's to wait for. Every write
/// ends with an <c>ANALYZE</c> or a forced statistics flush.
/// </remarks>
public class PartitionHeadroomLiveTests(PostgresFixture postgres)
{
    private const string Schema = "x4";

    /// <summary>The studio's clock: 2026-10-26, so October's partition is the one now is in.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthFires("T20")]
    public async Task A_monthly_range_one_width_from_its_end_with_no_default_is_critical()
    {
        string database = await DatabaseAsync("x4_t20_month");
        await ExecuteInAsync(
            database,
            "create table x4.events (at timestamptz not null, v int) partition by range (at)",
            "create table x4.events_2026_09 partition of x4.events for values from ('2026-09-01 00:00+00') to ('2026-10-01 00:00+00')",
            "create table x4.events_2026_10 partition of x4.events for values from ('2026-10-01 00:00+00') to ('2026-11-01 00:00+00')",
            "insert into x4.events select '2026-10-20 00:00+00'::timestamptz + (g || ' minutes')::interval, g from pg_catalog.generate_series(1, 300) g");
        await ExecuteInAsync(database, "analyze x4.events");
        await using StudioHost host = Host(database);

        HealthReportFinding finding = About(await ReportAsync(host), "T20", "x4.events").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Critical, "the newest partition ends in six days, and no DEFAULT takes November's rows");
        finding.Finding.Variant.Should().Be(RangePartitionsCheck.RunningOutVariant);
        finding.Finding.Title.Should().Contain("runs out at 2026-11-01 00:00 UTC");
        finding.Finding.Remedy!.Code.Should().Be(
            "CREATE TABLE \"x4\".\"events_p20261101\" PARTITION OF \"x4\".\"events\" FOR VALUES FROM ('2026-11-01 00:00:00+00') TO ('2026-12-01 00:00:00+00');",
            "the bounds were parsed by the studio from Postgres' own text, and the next month written from them");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    [HealthFires("T20")]
    public async Task An_integer_range_whose_rows_reach_its_newest_partition_and_one_with_rows_in_its_default_are_warnings()
    {
        string database = await DatabaseAsync("x4_t20_integer");
        await ExecuteInAsync(
            database,
            "create table x4.ledger (id bigint not null, note text) partition by range (id)",
            "create table x4.ledger_p0 partition of x4.ledger for values from (0) to (1000)",
            "create table x4.ledger_p1000 partition of x4.ledger for values from (1000) to (2000)",
            "create table x4.ledger_p2000 partition of x4.ledger for values from (2000) to (3000)",
            "insert into x4.ledger select g, 'n' || g from pg_catalog.generate_series(1, 2500) g",
            "create table x4.tickets (id int not null) partition by range (id)",
            "create table x4.tickets_p0 partition of x4.tickets for values from (0) to (1000)",
            "create table x4.tickets_default partition of x4.tickets default",
            "insert into x4.tickets select g from pg_catalog.generate_series(1, 1200) g");

        // Analysed from a session of its own once the inserting one's counters have reached the server: an ANALYZE in the
        // inserting session, before its counters are flushed, has them added again on top of its own count.
        await ExecuteInAsync(database, "analyze x4.ledger", "analyze x4.tickets");
        await using StudioHost host = Host(database);

        HealthReport report = await ReportAsync(host);

        HealthReportFinding ledger = About(report, "T20", "x4.ledger").Should().ContainSingle().Subject;
        ledger.Severity.Should().Be(HealthSeverity.Warning, "an integer key has no clock: never Critical");
        ledger.Finding.Variant.Should().Be(RangePartitionsCheck.RunningOutVariant);
        ledger.Finding.Title.Should().Be("The rows of x4.ledger reach its newest range partition, which ends at 3,000");
        ledger.Evidence.Should().Contain(new HealthEvidence("Empty partitions above the rows", "0"));
        ledger.Finding.Remedy!.Code.Should().Be(
            "CREATE TABLE \"x4\".\"ledger_p3000\" PARTITION OF \"x4\".\"ledger\" FOR VALUES FROM (3000) TO (4000);",
            "the newest partition's width on, from bounds Postgres wrote bare and quoted");

        IReadOnlyList<HealthReportFinding> tickets = About(report, "T20", "x4.tickets");
        tickets.Should().HaveCount(2);
        tickets.Should().ContainSingle(static x => x.Finding.Variant == RangePartitionsCheck.DefaultRowsVariant)
            .Which.Finding.Title.Should().Be("The DEFAULT partition of x4.tickets holds about 201 rows");
        tickets.Should().ContainSingle(static x => x.Finding.Variant == RangePartitionsCheck.RunningOutVariant)
            .Which.Finding.WhyItMatters.Should().Contain("land in the DEFAULT partition");
    }

    [PostgresFact]
    [HealthQuiet("T20")]
    public async Task Three_months_ahead_with_an_empty_default_and_an_integer_range_with_room_are_quiet()
    {
        string database = await DatabaseAsync("x4_t20_quiet");
        await ExecuteInAsync(
            database,
            "create table x4.events (at timestamptz not null, v int) partition by range (at)",
            "create table x4.events_2026_10 partition of x4.events for values from ('2026-10-01 00:00+00') to ('2026-11-01 00:00+00')",
            "create table x4.events_2026_11 partition of x4.events for values from ('2026-11-01 00:00+00') to ('2026-12-01 00:00+00')",
            "create table x4.events_2026_12 partition of x4.events for values from ('2026-12-01 00:00+00') to ('2027-01-01 00:00+00')",
            "create table x4.events_2027_01 partition of x4.events for values from ('2027-01-01 00:00+00') to ('2027-02-01 00:00+00')",
            "create table x4.events_default partition of x4.events default",
            "insert into x4.events select '2026-10-20 00:00+00'::timestamptz + (g || ' minutes')::interval, g from pg_catalog.generate_series(1, 300) g",
            "create table x4.ledger (id int not null) partition by range (id)",
            "create table x4.ledger_p0 partition of x4.ledger for values from (0) to (1000)",
            "create table x4.ledger_p1000 partition of x4.ledger for values from (1000) to (2000)",
            "create table x4.ledger_p2000 partition of x4.ledger for values from (2000) to (3000)",
            "create table x4.ledger_p3000 partition of x4.ledger for values from (3000) to (4000)",
            "insert into x4.ledger select g from pg_catalog.generate_series(1, 900) g");
        await ExecuteInAsync(database, "analyze x4.events", "analyze x4.ledger");
        await using StudioHost host = Host(database);

        HealthReport report = await ReportAsync(host);

        report.Findings.Where(static x => x.Code == "T20").Should().BeEmpty("97 days ahead is three widths, and three empty partitions sit above the ledger's rows");
        HealthCheckStatus status = report.Checks.Single(static x => x.Code == "T20");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().BeNull("it judged both tables, and its bounds were all read");
    }

    [PostgresFact]
    [HealthFires("T19")]
    public async Task Two_hundred_and_twenty_partitions_on_a_server_of_20_connections_pass_half_the_lock_table_and_T19_warns()
    {
        // max_connections = 20 makes the documented capacity 64 x 20 = 1,280, half of it 640: 220 partitions of two indexes
        // each are 1 + 220 + 440 + 2 = 663 locks for one query that is not pruned.
        await using AutovacuumServer server = await AutovacuumServer.StartAsync(17, "max_connections=20");
        await server.ExecuteAsync(
            "create schema x4",
            "create table x4.measures (k int not null, a int, b int) partition by list (k)",
            "create index measures_by_a on x4.measures (a)",
            "create index measures_by_b on x4.measures (b)",
            "do $$ begin for i in 1..220 loop execute pg_catalog.format('create table x4.measures_%s partition of x4.measures for values in (%s)', i, i); end loop; end $$");

        await using StudioHost host = server.Host(Schema, new ManualClock(Start));
        HealthReportFinding finding = AutovacuumServer.About(await AutovacuumServer.ReportAsync(host), "T19", "x4.measures").Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Variant.Should().Be(PartitionLockBudgetCheck.CapacityVariant);
        finding.Evidence.Should().Contain(static x => x.Label == "Locks one unpruned query takes" && x.Value.EndsWith("= 663", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(static x => x.Label == "Documented capacity" && x.Value.StartsWith("1,280 = ", StringComparison.Ordinal));
        finding.Finding.Remedy!.Code.Should().EndWith("max_locks_per_transaction = 128", "663 / 0.5 / 20 needs 67 a connection, and never less than double 64");
    }

    /// <summary>
    /// D27's rule, held: with somebody's <c>ACCESS EXCLUSIVE</c> on a range-partitioned table, on a partition of it and on an
    /// integer range's partition (<c>lock_timeout</c> is the studio's, which a queued read would run into), T19 and T20 judge
    /// every table all the same - the bounds deparsed with no relation, the rows from the catalog - no statement of the sweep
    /// holds a lock beyond a catalog read, and the schema is as it was.
    /// </summary>
    [PostgresFact]
    public async Task The_partition_reads_return_behind_a_held_access_exclusive_lock_and_write_nothing()
    {
        string database = await DatabaseAsync("x4_locks");
        await ExecuteInAsync(
            database,
            "create table x4.events (at timestamptz not null, v int) partition by range (at)",
            "create index events_by_v on x4.events (v)",
            "create table x4.events_2026_09 partition of x4.events for values from ('2026-09-01 00:00+00') to ('2026-10-01 00:00+00')",
            "create table x4.events_2026_10 partition of x4.events for values from ('2026-10-01 00:00+00') to ('2026-11-01 00:00+00')",
            "create table x4.ledger (id int not null) partition by range (id)",
            "create table x4.ledger_p0 partition of x4.ledger for values from (0) to (1000)",
            "insert into x4.ledger select g from pg_catalog.generate_series(1, 900) g");
        await ExecuteInAsync(database, "analyze x4.ledger");

        HealthSweepProbe probe = new(token => OpenAsync(database, token));
        await using StudioHost host = Host(database, probe);
        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(token => OpenAsync(database, token), Schema);

        await using NpgsqlConnection migration = await OpenAsync(database, Token);
        await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
        await using (NpgsqlCommand hold = new("lock table x4.events, x4.events_2026_10, x4.ledger_p0 in access exclusive mode", migration, holding))
        {
            await hold.ExecuteNonQueryAsync(Token);
        }

        HealthReport report = await ReportAsync(host);

        await holding.RollbackAsync(Token);

        foreach (string code in new[] { "T19", "T20" })
        {
            HealthCheckStatus status = report.Checks.Single(x => x.Code == code);
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} reads only the catalog, so a lock on a partitioned table holds it up not at all: {1}", code, status.Problem);
        }

        About(report, "T20", "x4.events").Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical,
            "the locked table's bounds were read: October's partition is the newest, six days from its end");
        About(report, "T20", "x4.ledger").Should().ContainSingle().Which.Finding.Title.Should().Contain("reach its newest range partition",
            "the locked partition's rows were read from the catalog");

        probe.StatementsObserved.Should().BePositive("so 'no lock' is not 'nothing ran'");
        probe.BeyondCatalogReads.Should().BeEmpty("a list locks nothing it lists (D27)");
        (await HealthSweepProbe.SnapshotAsync(token => OpenAsync(database, token), Schema)).Should().BeEquivalentTo(before, "a sweep writes nothing");
    }

    /// <summary>A database of the test's own, with the <c>x4</c> schema in it, and its connection string.</summary>
    private async Task<string> DatabaseAsync(string name)
    {
        string database = await postgres.CreateDatabaseAsync(name, Token);
        await ExecuteInAsync(database, "create schema " + Schema);
        return database;
    }

    /// <summary>A Postgres Studio host over <paramref name="connectionString" />, whose browsable schema is <c>x4</c>.</summary>
    private static StudioHost Host(string connectionString, IHealthStatementObserver? observer = null) =>
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
            options => options.BrowsableSchemas.Add(Schema));

    private static Task<HealthReport> ReportAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token));

    private static IReadOnlyList<HealthReportFinding> About(HealthReport report, string code, string tableKey) =>
        [.. report.Findings.Where(x => x.Code == code && x.Finding.Subject == tableKey)];

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken token)
    {
        NpgsqlConnection connection = new(connectionString);

        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs statements on one connection of the test's own to <paramref name="connectionString" />, then flushes its statistics.</summary>
    private static async Task ExecuteInAsync(string connectionString, params string[] statements)
    {
        await using NpgsqlConnection connection = await OpenAsync(connectionString, Token);

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlCommand flush = new("select pg_catalog.pg_stat_force_next_flush()", connection);
        await flush.ExecuteNonQueryAsync(Token);
    }
}
