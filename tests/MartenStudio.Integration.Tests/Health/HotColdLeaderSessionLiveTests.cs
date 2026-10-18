using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.Services.Database;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The HotCold leader's own session with its leadership lock transaction-scoped, where it holds the lock in a transaction
/// it never ends, as C04 and C05 read it - with its lock monitor (<c>Events.UseMonitoredAdvisoryLock</c>) on, the default,
/// and off.
/// </summary>
/// <remarks>
/// <para>
/// Measured at 9.39 (JasperFx 2.74, Weasel 9.33, DistributedLock.Postgres 1.3.0) before these were written. With the
/// monitor on, the leader is <c>idle in transaction</c> from the moment it takes its lock until its next leadership poll,
/// and from then on <c>active</c> in the monitor's <c>pg_sleep($1)</c>, inside the same transaction: one sleep started
/// five seconds after the lock at the default <c>LeadershipPollingTime</c>, and the next 59.9 s after that. With the
/// monitor off it is <c>idle in transaction</c> for as long as it leads, and its snapshot (<c>backend_xmin</c>) never
/// moves.
/// </para>
/// <para>
/// <b>At every Marten.</b> From 9.41 the lock is the session's by default (<c>Events.UseAdvisoryLockTransaction</c> is
/// false, #5526), but the setting is still there - <c>EventGraph.cs:290</c> at V9.50.2, read by
/// <c>ProjectionCoordinator.buildLockFactory</c> into Weasel's <c>AdvisoryLockOptions.TransactionalLockEnabled</c> - and it
/// is what a host behind a transaction-mode pooler keeps. So both tests set it, rather than skip the versions whose default
/// is the other one, and each writes what it measured to the test output with the Marten it ran on.
/// </para>
/// </remarks>
public class HotColdLeaderSessionLiveTests(PostgresFixture postgres)
{
    /// <summary>The leadership lock in a transaction: 9.39's and 9.40's default, a setting from 9.41.</summary>
    private static void TransactionScoped(StoreOptions options) => options.Events.UseAdvisoryLockTransaction = true;

    /// <summary>
    /// C05 beside the leader with its lock monitor on: the monitor's statement is the only long one the leader runs, C05's
    /// own read lists it - so the quiet below is C05 looking at it, not missing it - and it is younger than a minute. And
    /// it never gets older: the monitor ends it and begins the next within about a minute, inside the same transaction, so
    /// no statement of the leader's reaches C05's five minutes, however long it leads.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("C05")]
    public async Task With_the_lock_monitor_on_the_leaders_sleep_never_runs_for_C05s_five_minutes()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c05_hotcold");

        using IHost host = await ConnectionDaemonLiveTests.StartAsync(database, DaemonMode.HotCold, TransactionScoped);

        try
        {
            long[] ids = DaemonLockIds(host);
            await using ConnectionsLiveHost studio = await ConnectionsLiveHost.CreateAsync(
                database, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

            if (ConnectionDaemonLiveTests.LeaderKeepsItsTransactionWithAKeepalive)
            {
                // Weasel 9.45: no monitor statement at all - the leader runs nothing long for C05 to judge, and what keeps its
                // snapshot about a minute old is DistributedLock's keepalive, inside the leadership transaction. Measured here.
                await LiveWait.UntilAsync(
                    async () => await LiveWait.ScalarAsync<bool>(
                        database,
                        "select count(*) > 0 and bool_and(a.query like '%DistributedLock connection keepalive%' and a.xact_start < a.query_start)"
                        + " from " + ConnectionDaemonLiveTests.LeaderSessions,
                        ("ids", ids)),
                    TimeSpan.FromSeconds(120),
                    "DistributedLock's keepalive to run inside every leadership transaction");

                double keptAfter = await LiveWait.ScalarAsync<double>(
                    database,
                    "select max(extract(epoch from a.query_start - a.xact_start))::float8 from " + ConnectionDaemonLiveTests.LeaderSessions,
                    ("ids", ids));
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Marten {ConnectionDaemonLiveTests.Marten}: no lock monitor statement; the keepalive refreshed the leader's snapshot {keptAfter:0.0} s into its leadership transaction.");
                keptAfter.Should().BeLessThan(75, "the keepalive runs about once a minute");

                HealthReport quiet = await studio.ReportAsync();
                quiet.Findings.Should().NotContain(static x => x.Code == "C05");
                ConnectionsLiveHost.Status(quiet, "C05").Outcome.Should().Be(HealthCheckOutcome.Checked, ConnectionsLiveHost.Status(quiet, "C05").Problem);
                return;
            }

            await ConnectionDaemonLiveTests.WaitForTheLeaderInItsMonitorAsync(database, ids);

            int[] leaders = await LeaderPidsAsync(database, ids);
            LongRunningStatementCheck check = new();
            LongRunningFacts? seen = null;

            // C05 reads statements running for at least its one-second floor: read until it lists one of the leader's.
            await LiveWait.UntilAsync(
                async () =>
                {
                    LongRunningFacts facts = await studio.GatherAsync(check);
                    seen = facts.Statements.Any(x => leaders.Contains(x.ProcessId)) ? facts : null;
                    return seen is not null;
                },
                TimeSpan.FromSeconds(30),
                "C05's read to list the HotCold leader's lock-monitor statement");

            RunningStatement statement = seen!.Statements.First(x => leaders.Contains(x.ProcessId));
            seen.Statements.Where(x => leaders.Contains(x.ProcessId)).Should().AllSatisfy(static x =>
                x.RunningAtRead.Should().BeLessThan(TimeSpan.FromSeconds(61), "the lock monitor sleeps a minute at a time"));
            check.Evaluate(seen, HealthEvaluation.At(seen.ReadAt)).Findings.Should().BeEmpty("a statement under a minute old is not five minutes old");

            // Its next sleep begins once this one has run its minute, in the same transaction: the statement C05 judges is
            // never older than that.
            // timestamptz, which Npgsql reads as a UTC DateTime.
            DateTime started = await LiveWait.ScalarAsync<DateTime>(
                database, "select a.query_start from pg_catalog.pg_stat_activity a where a.pid = @pid", ("pid", statement.ProcessId));
            DateTime transaction = await LiveWait.ScalarAsync<DateTime>(
                database, "select a.xact_start from pg_catalog.pg_stat_activity a where a.pid = @pid", ("pid", statement.ProcessId));

            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select coalesce(bool_and(a.state = 'active' and a.query like '%pg_sleep%' and a.query_start > @started and a.xact_start = @transaction), false)"
                    + " from pg_catalog.pg_stat_activity a where a.pid = @pid",
                    ("pid", statement.ProcessId),
                    ("started", started),
                    ("transaction", transaction)),
                TimeSpan.FromSeconds(90),
                "the lock monitor's next sleep to begin, in the same leadership transaction");

            DateTime next = await LiveWait.ScalarAsync<DateTime>(
                database, "select a.query_start from pg_catalog.pg_stat_activity a where a.pid = @pid", ("pid", statement.ProcessId));
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Marten {ConnectionDaemonLiveTests.Marten}: the leader's sleep ran {(next - started).TotalSeconds:0.0} s before the next began, in the same transaction.");
            (next - started).Should().BeLessThan(ConnectionThresholds.LongRunningWarning, "the monitor re-issues its one-minute sleep, so no statement of the leader's lives for five minutes");

            HealthReport report = await studio.ReportAsync();
            report.Findings.Should().NotContain(static x => x.Code == "C05");
            ConnectionsLiveHost.Status(report, "C05").Outcome.Should().Be(HealthCheckOutcome.Checked, ConnectionsLiveHost.Status(report, "C05").Problem);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// C04 beside the leader with its lock monitor off: nothing uses the leader's connection once it holds its lock, so it
    /// sits idle in its leadership transaction - with a snapshot, which holds back vacuum - for as long as it leads. After a
    /// real minute of that, C04's read lists it holding Marten's daemon lock - inside the leader's bound, so not raised - and,
    /// judged past <c>ConnectionThresholds.DaemonLeaderIdleBound</c>, raises it. This is the anti-vacuity half of
    /// <see cref="ConnectionDaemonLiveTests.The_HotCold_daemons_leadership_session_is_never_raised_as_idle_in_a_transaction" />:
    /// C04 sees this leader, and is quiet there only because the monitor keeps it working.
    /// </summary>
    [PostgresFact]
    [HealthFires("C04")]
    public async Task With_the_lock_monitor_off_C04_raises_the_HotCold_leader_after_a_minute()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c04_hotcold_unmonitored");

        using IHost host = await ConnectionDaemonLiveTests.StartAsync(
            database,
            DaemonMode.HotCold,
            static options =>
            {
                TransactionScoped(options);
                options.Events.UseMonitoredAdvisoryLock = false;
            });

        try
        {
            long[] ids = DaemonLockIds(host);
            await using ConnectionsLiveHost studio = await ConnectionsLiveHost.CreateAsync(
                database, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select count(*) > 0 and bool_and(a.state = 'idle in transaction' and pg_catalog.now() - a.state_change > interval '62 seconds')"
                    + " from " + ConnectionDaemonLiveTests.LeaderSessions,
                    ("ids", ids)),
                TimeSpan.FromSeconds(120),
                "the HotCold leader to have sat idle in its leadership transaction for a minute");

            int[] leaders = await LeaderPidsAsync(database, ids);
            IdleInTransactionCheck check = new();
            IdleInTransactionFacts facts = await studio.GatherAsync(check);

            // One session per daemon lock the leader holds, and C04's read lists every one of them.
            IdleSession[] listed = [.. facts.Sessions.Where(x => leaders.Contains(x.ProcessId))];
            listed.Select(static x => x.ProcessId).Should().BeEquivalentTo(leaders, "C04's own read lists every session of the leader's");
            listed.Should().AllSatisfy(static leader =>
            {
                leader.HoldsDaemonLock.Should().BeTrue("the session holds one of Marten's daemon locks");
                leader.State.Should().Be("idle in transaction");
                leader.HasTransactionId.Should().BeFalse("the leader writes nothing in its leadership transaction");
                leader.HasSnapshot.Should().BeTrue("its snapshot is never refreshed with the monitor off, which is what holds back vacuum");
                leader.IdleAtRead.Should().BeGreaterThanOrEqualTo(ConnectionThresholds.IdleInTransactionWarning);
            });

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Marten {ConnectionDaemonLiveTests.Marten}: {listed.Length} leader session(s), idle in the leadership transaction with a snapshot for "
                + string.Join(", ", listed.Select(static x => x.IdleAtRead.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s")) + ".");

            // Recognised by its daemon lock: a minute idle is inside the leader's bound, and is not raised.
            check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt)).Findings.Should().BeEmpty(
                "a leader idle in its transaction for a minute is inside ConnectionThresholds.DaemonLeaderIdleBound");

            // With the monitor off nothing refreshes it, so had nothing changed it would pass the bound - and be raised.
            HealthFinding finding = check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt + ConnectionThresholds.DaemonLeaderIdleBound))
                .Findings.Should().ContainSingle().Subject;
            finding.Code.Should().Be("C04");
            finding.Severity.Should().Be(HealthSeverity.Warning, "past the bound, not ten minutes");
            string longest = finding.Evidence.Single(static x => x.Label == "Session 1 (longest)").Value;
            leaders.Should().Contain(pid => longest.StartsWith("pid " + pid + ",", StringComparison.Ordinal), "the longest is one of the leader's sessions, named by pid");

            HealthReport report = await studio.ReportAsync();
            report.Findings.Should().NotContain(static x => x.Code == "C04", "the sweep, a minute in, is inside the leader's bound too");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static long[] DaemonLockIds(IHost host) =>
        [.. SessionLockLabels.For(host.Services.GetRequiredService<IDocumentStore>(), "default", null).DaemonLockIds];

    private static async Task<int[]> LeaderPidsAsync(string database, long[] ids) =>
        await LiveWait.ScalarAsync<int[]>(database, "select array_agg(a.pid) from " + ConnectionDaemonLiveTests.LeaderSessions, ("ids", ids))
        ?? throw new InvalidOperationException("No session holds one of the store's daemon locks.");
}
