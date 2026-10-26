using System.Diagnostics;

using JasperFx.Events.Daemon;

using Marten;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// K08 on a real server (W5-S2): a database whose own settings give the store's sessions a timeout shorter than the HotCold
/// leader may sit idle fires, and one at the defaults is quiet - and, with a real HotCold daemon at whichever Marten this run
/// resolved, what such a timeout does to the leader: the stretch the check judges against, measured.
/// </summary>
/// <remarks>
/// <para>
/// Every timeout is set with <c>ALTER DATABASE … SET</c> on a database of the test's own, so nothing else on the suite's
/// server is subject to it, and the store's sessions see it as <c>source = 'database'</c>.
/// </para>
/// <para>
/// <b>Run at both Marten ends</b> (9.39 and the latest tested): the measurement writes what it saw with the Marten it ran on,
/// and asserts the behaviour K08's stretch assumes for that Weasel - DistributedLock's keepalive about once a minute, the
/// leader idle in between, from Weasel 9.34 (Marten 9.39.1); the upstream DistributedLock's <c>pg_sleep</c> monitor, the leader
/// active, at Marten 9.39.0. Which one runs is read off the loaded DistributedLock (<c>LeaderLockMonitor</c>).
/// </para>
/// </remarks>
public class DaemonTimeoutsLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void HotCold(StoreOptions options)
    {
        options.Projections.AsyncMode = DaemonMode.HotCold;
        options.Events.UseAdvisoryLockTransaction = true;
    }

    [PostgresFact]
    [HealthFires("K08")]
    public async Task K08_fires_for_a_transaction_scoped_leader_whose_database_ends_idle_transactions_with_its_monitor_off()
    {
        string database = await DatabaseAsync("health_k08_fires", "idle_in_transaction_session_timeout = '30s'");

        await using ConnectionsLiveHost studio = await ConnectionsLiveHost.CreateAsync(database, store: static options =>
        {
            HotCold(options);
            options.Events.UseMonitoredAdvisoryLock = false;
        });

        HealthReport report = await studio.ReportAsync();
        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "K08").Subject;

        finding.Finding.Variant.Should().Be("idle-in-transaction");
        finding.Evidence.Should().Contain(x => x.Label == "Set by" && x.Value == "the database's own settings (ALTER DATABASE … SET)");
        finding.Evidence.Should().Contain(x => x.Label == "Setting" && x.Value == "idle_in_transaction_session_timeout = 30000 ms",
            "read as a new session starts with it, never the studio's own transaction's value");
    }

    [PostgresFact]
    [HealthQuiet("K08")]
    public async Task K08_is_quiet_for_a_HotCold_leader_on_a_database_at_the_servers_defaults()
    {
        string database = await DatabaseAsync("health_k08_quiet");

        await using ConnectionsLiveHost studio = await ConnectionsLiveHost.CreateAsync(database, store: HotCold);

        HealthReport report = await studio.ReportAsync();
        report.Findings.Should().NotContain(static x => x.Code == "K08");
        ConnectionsLiveHost.Status(report, "K08").Outcome.Should().Be(HealthCheckOutcome.Checked, ConnectionsLiveHost.Status(report, "K08").Problem);
    }

    /// <summary>
    /// A real HotCold leader, its lock transaction-scoped and monitored, on a database that ends idle transactions after 20 s:
    /// with DistributedLock's keepalive (from Weasel 9.34) the leader sits idle about a minute between keepalives, so the server ends its session and K08 says it
    /// would; below it, the monitor's sleep keeps it active once it has taken the lead, so it keeps its session and K08 is
    /// quiet. Either way the test writes what it measured.
    /// </summary>
    [PostgresFact]
    public async Task An_idle_in_transaction_timeout_ends_the_leader_exactly_where_K08_says_it_would()
    {
        string database = await DatabaseAsync("health_k08_leader", "idle_in_transaction_session_timeout = '20s'");

        using IHost host = await ConnectionDaemonLiveTests.StartAsync(database, DaemonMode.HotCold, static options =>
        {
            options.Events.UseAdvisoryLockTransaction = true;
            options.Events.UseMonitoredAdvisoryLock = true;
        });

        try
        {
            long[] ids = [.. MartenStudio.Services.MartenSessionLockLabeler.For(host.Services.GetRequiredService<IDocumentStore>(), "default", null).DaemonLockIds];
            int first = await LeaderAsync(database, ids);
            Stopwatch clock = Stopwatch.StartNew();
            bool ended;

            // Watch the leader for 75 s: past the 20 s timeout, and past one keepalive.
            try
            {
                await LiveWait.UntilAsync(
                    async () => !await LiveWait.ScalarAsync<bool>(database, "select exists (select 1 from pg_catalog.pg_stat_activity a where a.pid = @pid)", ("pid", first)),
                    TimeSpan.FromSeconds(75),
                    "the first leader's session to end");
                ended = true;
            }
            catch (TimeoutException)
            {
                ended = false;
            }

            TestContext.Current.TestOutputHelper?.WriteLine(
                "Marten {0}: with idle_in_transaction_session_timeout = 20 s the leader's session {1} after {2:0.0} s.",
                ConnectionDaemonLiveTests.Marten, ended ? "was ended" : "survived", clock.Elapsed.TotalSeconds);

            await using ConnectionsLiveHost studio = await ConnectionsLiveHost.CreateAsync(database, store: static options =>
            {
                HotCold(options);
                options.Events.UseMonitoredAdvisoryLock = true;
            });
            bool fires = (await studio.ReportAsync()).Findings.Any(static x => x.Code == "K08");

            ended.Should().Be(ConnectionDaemonLiveTests.LeaderKeepsItsTransactionWithAKeepalive,
                "with the keepalive the leader idles about a minute between keepalives; with the pg_sleep monitor it stays active");
            fires.Should().Be(ended, "K08 raises exactly the timeout that ended the leader here");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The packet's open question, at 9.39's <c>pg_sleep(60)</c> monitor: does a server <c>statement_timeout</c> of 10 s end the
    /// monitor's sleep inside its savepoint harmlessly, or make DistributedLock report the lock lost? Measured: neither -
    /// DistributedLock.Postgres 1.3.0 runs <c>SET LOCAL statement_timeout = 0</c> before the sleep, so the server's timeout
    /// never reaches it, and the leader keeps its lock. So K08 has no <c>statement_timeout</c> variant. Asserted as measured, so
    /// a DistributedLock that changes it says so. With the keepalive (from Weasel 9.34) there is no sleep, and the test says so
    /// and skips.
    /// </summary>
    [PostgresFact]
    public async Task A_statement_timeout_under_the_monitors_sleep_and_the_leaders_lock()
    {
        if (ConnectionDaemonLiveTests.LeaderKeepsItsTransactionWithAKeepalive)
        {
            Assert.Skip("Marten " + ConnectionDaemonLiveTests.Marten + " monitors the lock with DistributedLock's keepalive, which runs no long statement for statement_timeout to cancel.");
        }

        string database = await DatabaseAsync("health_k08_sleep", "statement_timeout = '10s'");

        using IHost host = await ConnectionDaemonLiveTests.StartAsync(database, DaemonMode.HotCold, static options =>
        {
            options.Events.UseAdvisoryLockTransaction = true;
            options.Events.UseMonitoredAdvisoryLock = true;
        });

        try
        {
            long[] ids = [.. MartenStudio.Services.MartenSessionLockLabeler.For(host.Services.GetRequiredService<IDocumentStore>(), "default", null).DaemonLockIds];
            int first = await LeaderAsync(database, ids);

            // Until the first leader's transaction has outlived several 10 s statement timeouts of its sleep - or the leader is gone.
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select not exists (select 1 from pg_catalog.pg_stat_activity a where a.pid = @pid)"
                    + " or exists (select 1 from pg_catalog.pg_stat_activity a where a.pid = @pid and pg_catalog.now() - a.xact_start > interval '40 seconds')",
                    ("pid", first)),
                TimeSpan.FromSeconds(90),
                "the first leader to have led for 40 s, or to be gone");

            int[] now = await LiveWait.ScalarAsync<int[]>(database, "select coalesce(array_agg(a.pid), '{}') from " + ConnectionDaemonLiveTests.LeaderSessions, ("ids", ids)) ?? [];
            string states = await LiveWait.ScalarAsync<string>(database,
                "select coalesce(string_agg(distinct a.state || ':' || left(a.query, 40), ' | '), 'none') from " + ConnectionDaemonLiveTests.LeaderSessions, ("ids", ids)) ?? "none";

            // The sleep asks for a minute. DistributedLock.Postgres 1.3.0 runs SET LOCAL statement_timeout = 0 in the leadership
            // transaction before it (its own SQL strings, read from the assembly), so the server's 10 s never reaches the sleep:
            // 40 s into the leadership the first sleep is still the one running, older than the timeout.
            double sleeping = await LiveWait.ScalarAsync<double>(database,
                "select coalesce(max(extract(epoch from pg_catalog.now() - a.query_start))::float8, -1) from " + ConnectionDaemonLiveTests.LeaderSessions
                + " and a.query like '%pg_sleep%'", ("ids", ids));

            TestContext.Current.TestOutputHelper?.WriteLine(
                "Marten {0}: with statement_timeout = 10 s, 40 s on the first leader {1} holds the lock ({2}); its current sleep began {3:0.0} s ago.",
                ConnectionDaemonLiveTests.Marten, now.Contains(first) ? "still" : "no longer", states, sleeping);

            now.Should().Contain(first, "measured at 9.39: a server statement_timeout under a minute does not cost the leader its lock");
            sleeping.Should().BeGreaterThan(10, "the monitor's sleep runs under the transaction's own statement_timeout = 0, never the server's");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private async Task<string> DatabaseAsync(string name, string? setting = null)
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, name);

        if (setting is not null)
        {
            await using NpgsqlConnection connection = new(postgres.ConnectionString);
            await connection.OpenAsync(Token);
            await using NpgsqlCommand command = new("alter database " + name + " set " + setting, connection);
            await command.ExecuteNonQueryAsync(Token);
        }

        return database;
    }

    private static async Task<int> LeaderAsync(string database, long[] ids)
    {
        await LiveWait.UntilAsync(
            async () => await LiveWait.ScalarAsync<bool>(database, "select exists (select 1 from " + ConnectionDaemonLiveTests.LeaderSessions + ")", ("ids", ids)),
            TimeSpan.FromSeconds(30),
            "a session to hold the store's daemon lock");

        return (await LiveWait.ScalarAsync<int[]>(database, "select array_agg(a.pid) from " + ConnectionDaemonLiveTests.LeaderSessions, ("ids", ids)))![0];
    }
}
