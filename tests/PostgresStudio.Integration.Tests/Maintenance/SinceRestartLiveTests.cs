using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Storage;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Integration.Tests.Maintenance;

/// <summary>
/// T08's <c>since-restart</c> on a real PostgreSQL 17 (W5b-int: V1's variant wired to CAP's statistics window), in a database
/// of each test's own: after <c>pg_stat_reset()</c> a table of 50,000 estimated rows has not been analysed since the counters
/// began again, and T08 says so in every sweep - two of them eleven minutes apart on the studio's clock - until a day of
/// statistics has counted.
/// </summary>
/// <remarks>
/// <para>
/// <c>pg_stat_reset()</c> resets this database's counters and its tables' (their <c>last_analyze</c> included) and leaves
/// <c>pg_class.reltuples</c> alone, which is exactly the shape a crash or <c>pg_upgrade</c> leaves: large tables autovacuum
/// knows nothing about since. It moves this database's <c>stats_reset</c>, so the window starts there whatever the server's
/// shared reset times say. The table has <c>autovacuum_enabled = false</c>, so no autoanalyze clears the finding mid-test.
/// </para>
/// <para>
/// "A day" is the studio's clock set past the window's start, read from the server first, as C11's and C08's live tests set
/// it: the window is the server's reset time, and only the studio's "now" moves.
/// </para>
/// </remarks>
public class SinceRestartLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A database of the test's own with <c>public.orders</c> - 50,000 rows, analysed, its inserts and analyze counted - and
    /// then its statistics reset: <c>last_analyze</c> gone, <c>reltuples</c> kept.
    /// </summary>
    private static async Task<string> ResetAsync(MatrixServer server, string name)
    {
        string database = await server.CreateDatabaseAsync(name, Token);
        string app = server.ConnectionString(MatrixRole.App, database);

        await StorageLive.ExecuteAsync(
            app,
            "create table public.orders (id bigint primary key, note text) with (autovacuum_enabled = false); "
            + "insert into public.orders select g, md5(g::text) from generate_series(1, 50000) g;");
        await StorageLive.ExecuteAsync(app, "analyze public.orders");

        // The inserting backend reports its counts as it ends, which can be a moment after the client is gone: wait for them, so
        // none lands after the reset as modifications since an analyze there never was.
        await StorageLive.UntilAsync(
            async () => await StorageLive.ScalarAsync<bool>(
                app,
                "select coalesce((select n_tup_ins >= 50000 and last_analyze is not null from pg_stat_user_tables where relid = 'public.orders'::regclass), false)"),
            TimeSpan.FromSeconds(60),
            "the inserts and the analyze of public.orders to be counted");

        // pg_stat_reset() is a superuser's by default.
        await server.ExecuteAsync("select pg_catalog.pg_stat_reset()", database, Token);

        await StorageLive.UntilAsync(
            async () => await StorageLive.ScalarAsync<bool>(
                app,
                "select coalesce((select last_analyze is null and last_autoanalyze is null and n_mod_since_analyze = 0 from pg_stat_user_tables where relid = 'public.orders'::regclass), false) "
                + "and (select stats_reset is not null from pg_stat_database where datname = current_database())"),
            TimeSpan.FromSeconds(60),
            "pg_stat_reset() to have reset public.orders and stamped the database's stats_reset");

        (await StorageLive.ScalarAsync<long>(app, "select reltuples::int8 from pg_class where oid = 'public.orders'::regclass"))
            .Should().Be(50_000, "a reset leaves the planner's estimate alone - which is why the table still counts as large");

        return database;
    }

    private static HealthCheckResult T08(HealthSweep sweep) => sweep.Results.Single(static x => x.Check.Code == "T08");

    private static HealthFinding? SinceRestart(HealthCheckResult t08) =>
        t08.Findings.SingleOrDefault(static x => x.Variant == StaleStatisticsCheck.SinceRestartVariant);

    [PostgresFact]
    [HealthFires("T08")]
    public async Task After_pg_stat_reset_T08_is_since_restart_in_every_sweep_until_a_day_of_statistics_has_counted()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        string database = await ResetAsync(server, "w5b_t08_since_restart");

        SettableClock clock = new(DateTimeOffset.UtcNow);
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database, services: s => s.AddSingleton<TimeProvider>(clock));

        StatisticsWindow window = await StorageLive.WindowAsync(host);
        window.Reason.Should().NotBe(StatisticsResetReason.None, "pg_stat_reset() moved this database's stats_reset: " + window.BasisText);

        // Two sweeps eleven minutes apart - past T08's PersistFor - each with the finding: it is the window's age that keeps it,
        // not the one sweep that first sees a reset.
        foreach (TimeSpan after in (TimeSpan[]) [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(12)])
        {
            clock.Set(window.Since + after);

            HealthCheckResult t08 = T08(await host.SweepAsync(HealthSweepKind.Regular));
            t08.Outcome.Should().Be(HealthCheckOutcome.Checked, t08.Problem?.Reason);

            HealthFinding? found = SinceRestart(t08);
            found.Should().NotBeNull(after + " after the reset");

            HealthFinding finding = found!;
            finding.Severity.Should().Be(HealthSeverity.Info);
            finding.Subject.Should().BeNull("it is the database's");
            finding.Title.Should().StartWith("Autovacuum's counters began again at ").And.Contain("invisible to it, and to T07");
            finding.Evidence.Should().Contain(static x => x.Label == "The largest of them" && x.Value.Contains("orders", StringComparison.Ordinal));
            finding.Evidence.Should().Contain(static x => x.Label == "Not analysed since" && x.Value.StartsWith("1 table", StringComparison.Ordinal));
        }

        // A day and a minute on, the window covers a day: the checks that judge rates per day judge again, and T08 has no quiet
        // left to explain - though nothing has analysed the table since.
        clock.Set(window.Since + StatisticsWindow.Day + TimeSpan.FromMinutes(1));

        HealthCheckResult later = T08(await host.SweepAsync(HealthSweepKind.Regular));
        later.Outcome.Should().Be(HealthCheckOutcome.Checked, later.Problem?.Reason);
        SinceRestart(later).Should().BeNull("a day of statistics has counted");
    }

    [PostgresFact]
    [HealthQuiet("T08")]
    public async Task A_day_of_statistics_after_pg_stat_reset_T08_is_quiet_though_the_table_is_still_unanalysed_since()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        string database = await ResetAsync(server, "w5b_t08_since_restart_quiet");

        SettableClock clock = new(DateTimeOffset.UtcNow);
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database, services: s => s.AddSingleton<TimeProvider>(clock));

        StatisticsWindow window = await StorageLive.WindowAsync(host);
        window.Reason.Should().NotBe(StatisticsResetReason.None);
        clock.Set(window.Since + StatisticsWindow.Day + TimeSpan.FromMinutes(1));

        HealthCheckResult t08 = T08(await host.SweepAsync(HealthSweepKind.Regular));

        t08.Outcome.Should().Be(HealthCheckOutcome.Checked, t08.Problem?.Reason);
        t08.Findings.Should().BeEmpty("a day has counted since the reset, and nothing else is wrong with public.orders' statistics");

        (await StorageLive.ScalarAsync<bool>(
                server.ConnectionString(MatrixRole.App, database),
                "select last_analyze is null and last_autoanalyze is null from pg_stat_user_tables where relid = 'public.orders'::regclass"))
            .Should().BeTrue("the quiet is the window's length, not an analyze");
    }
}
