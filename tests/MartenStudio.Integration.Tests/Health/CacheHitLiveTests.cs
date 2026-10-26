using Npgsql;

using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// C11 through MartenStudio's own report, over a store's database of each test's own (W5-CAP, issue #8 G5): a million and
/// more blocks read from outside shared buffers fire it once the statistics window has counted a day, and a warm database
/// does not. The Postgres suite proves the same on the version matrix; this is the proof MartenStudio's convention asks of
/// every code, through the sweep, the cache and the per-visitor report a MartenStudio host runs.
/// </summary>
/// <remarks>
/// "A day" is the studio's clock moved to a day and a minute past the window's start, which is read from the server first: a
/// window the server's own reset times measure is lengthened by moving the studio's "now", never by seeding the history.
/// </remarks>
public class CacheHitLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using NpgsqlConnection connection = new(Unpooled(database));
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(Token);
    }

    private static Task<long> CounterAsync(string database, string column) =>
        LiveWait.ScalarAsync<long>(database, "select " + column + " from pg_stat_database where datname = current_database()");

    /// <summary>Moves <paramref name="clock" /> to a day and a minute past the statistics window's start.</summary>
    private static async Task ADayLaterAsync(ConnectionsLiveHost host, ManualClock clock)
    {
        StatisticsReading reading = await host.GatherAsync(StatisticsWindow.ReadAsync);
        DateTimeOffset since = StatisticsWindow.Compute(reading, HealthHistoryView.Empty).Since;

        clock.Advance(since + StatisticsWindow.Day + TimeSpan.FromMinutes(1) - clock.GetUtcNow());
    }

    [PostgresFact]
    [HealthFires("C11")]
    public async Task A_million_blocks_a_day_read_from_outside_shared_buffers_is_a_warning()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c11_fires");

        // About twice the default shared_buffers, written through CREATE TABLE AS's ring: it can never be held in shared buffers
        // whole, so every scan reads much of it again (a table smaller than shared_buffers was measured to end up held there).
        await ExecuteAsync(database, "create table cold as select g::bigint as id, repeat(md5(g::text), 4) as filler from generate_series(1, 1600000) g");
        await ExecuteAsync(database, "vacuum analyze cold");

        for (int batch = 0; batch < 30 && await CounterAsync(database, "blks_read") < 1_200_000; batch++)
        {
            await ExecuteAsync(database, string.Concat(Enumerable.Repeat("select count(*) from cold; ", 20)));
        }

        await LiveWait.UntilAsync(async () => await CounterAsync(database, "blks_read") >= 1_200_000, TimeSpan.FromSeconds(60), "1 200 000 blocks read");

        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, clock: clock);
        await ADayLaterAsync(host, clock);

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "C11").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Evidence.Select(static x => x.Label).Should().Contain(["Cache hit ratio", "Blocks read a day"]);
    }

    [PostgresFact]
    [HealthQuiet("C11")]
    public async Task A_warm_database_read_through_its_index_is_quiet()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c11_quiet");

        await ExecuteAsync(database, "create table warm (id int primary key, v text); insert into warm select g, md5(g::text) from generate_series(1, 1000) g; analyze warm;");
        await ExecuteAsync(database, "do $$ declare x text; begin for i in 1..20000 loop select v into x from warm where id = 1 + i % 1000; end loop; end $$;");
        await LiveWait.UntilAsync(async () => await CounterAsync(database, "blks_hit") >= 40_000, TimeSpan.FromSeconds(60), "the warm reads' hits");

        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, clock: clock);
        await ADayLaterAsync(host, clock);

        HealthReport report = await host.ReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "C11");
        HealthCheckStatus status = ConnectionsLiveHost.Status(report, "C11");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked);
        status.Note.Should().Contain("judged only below 95.0%");
    }
}
