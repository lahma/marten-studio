using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Database;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The connection checks beside a real async daemon hosted in the process: C09 against the daemon's shards on a
/// small pool and a large one, and C04 beside a HotCold daemon - whose leader, with its lock transaction-scoped (the
/// floor's default, and a setting at every Marten), holds it in a transaction for as long as it leads, and must never be
/// raised for it.
/// </summary>
public class ConnectionDaemonLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A host over the sample store in <paramref name="connectionString" />'s database with a real daemon, started and
    /// waited on until it runs the sample's two async shards. <c>LeadershipPollingTime</c> is one second.
    /// </summary>
    internal static async Task<IHost> StartAsync(string connectionString, DaemonMode mode, Action<StoreOptions>? store = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMarten(options =>
            {
                SampleStore.Configure(options, connectionString);
                options.Projections.LeadershipPollingTime = 1_000;
                store?.Invoke(options);
            })
            .AddAsyncDaemon(mode);

        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, Operator>();
        builder.Services.AddMartenStudio(static options => options.Capabilities = MartenStudioCapabilities.All());

        IHost host = builder.Build();

        await host.Services.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await host.StartAsync(Token);

        Marten.Events.Daemon.Coordination.IProjectionCoordinator coordinator =
            host.Services.GetRequiredService<Marten.Events.Daemon.Coordination.IProjectionCoordinator>();

        await LiveWait.UntilAsync(
            () => Task.FromResult(coordinator.DaemonForMainDatabase().CurrentAgents().Count(static x => x.Status == AgentStatus.Running) >= 2),
            TimeSpan.FromSeconds(60),
            "the daemon to run the sample's two async shards");

        return host;
    }

    private static async Task<HealthReport> ReportAsync(IHost host)
    {
        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();

        return await circuit.ServiceProvider.GetRequiredService<IHealthService>()
            .GetReportAsync(new StudioScope("default", string.Empty, null), HealthReportRequest.Regular, Token);
    }

    [PostgresFact]
    [HealthFires("C09")]
    public async Task A_daemon_whose_shards_fill_the_pool_on_their_own_is_a_C09_warning()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c09_fires");
        string small = new NpgsqlConnectionStringBuilder(database) { MaxPoolSize = 3 }.ConnectionString;

        using IHost host = await StartAsync(small, DaemonMode.Solo);

        try
        {
            HealthReport report = await ReportAsync(host);

            HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "C09").Subject;
            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.Evidence.Should().Contain(new HealthEvidence("Max Pool Size", "3"));
            finding.Evidence.Single(static x => x.Label == "Running shards").Value.Should().Be("2");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [PostgresFact]
    [HealthQuiet("C09")]
    public async Task A_daemon_with_room_in_the_pool_is_quiet()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c09_quiet");

        using IHost host = await StartAsync(database, DaemonMode.Solo);

        try
        {
            HealthReport report = await ReportAsync(host);

            report.Findings.Should().NotContain(static x => x.Code == "C09");
            HealthCheckStatus status = report.Checks.Single(static x => x.Code == "C09");
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
            status.Note.Should().Contain("2 shards running").And.Contain("pool of 100");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Whether a transaction-scoped HotCold leader keeps its transaction with DistributedLock's keepalive rather than its
    /// lock monitor's <c>pg_sleep</c>: Weasel 9.45 (weasel#614, Patched.DistributedLock.Postgres 1.3.3), which Marten 9.50.2
    /// brings. Measured by CL3 at 9.50.2, at <c>LeadershipPollingTime</c> 1 s and 5 s alike: no <c>pg_sleep</c> at all - each
    /// leader session sits <c>idle in transaction</c> with the snapshot of its lock statement until
    /// <c>SELECT 0 /* DistributedLock connection keepalive */</c> runs in the transaction about 61 s after the lock, and again
    /// a minute on. At Weasel 9.33 (Marten 9.39) the monitor's <c>pg_sleep(60)</c> runs from the first leadership poll.
    /// </summary>
    internal static bool LeaderKeepsItsTransactionWithAKeepalive =>
        (MartenRuntime.Current.VersionOf(RuntimePackage.WeaselCore) ?? new Version(0, 0)) >= new Version(9, 45);

    /// <summary>The Marten this run resolved, for the measurements the HotCold tests write to their output.</summary>
    internal static Version Marten => MartenRuntime.Current.VersionOf(RuntimePackage.Marten) ?? throw new InvalidOperationException("No Marten version.");

    /// <summary>
    /// Every session in <em>this</em> database holding one of the store's daemon locks (<c>@ids</c>), as a superuser sees
    /// it in <c>pg_stat_activity</c>. The HotCold leader holds more than one, on a session each.
    /// </summary>
    /// <remarks>
    /// <c>pg_stat_activity</c> and <c>pg_locks</c> are the server's, and every store built from the sample's configuration
    /// computes the same lock ids: a read that is not held to the current database also finds the HotCold leaders of
    /// every other test running on the server at the time. This class's own read once did - its single read of the
    /// leader's state could be another class's leader's - and measured here, a sibling test's unmonitored leader in
    /// another database made this class's leader look idle in transaction for a minute.
    /// </remarks>
    internal const string LeaderSessions =
        "pg_catalog.pg_stat_activity a where a.datname = pg_catalog.current_database() and a.pid in (select l.pid from pg_catalog.pg_locks l"
        + " where l.locktype = 'advisory' and l.granted and ((l.classid::int8 << 32) | l.objid::int8) = any(@ids))";

    /// <summary>
    /// Whether every leader session is running its lock monitor's statement inside the leadership transaction: active in
    /// <c>pg_sleep</c>, in a transaction that began before the statement did.
    /// </summary>
    internal const string LeaderInItsMonitor =
        "select count(*) > 0 and bool_and(a.state = 'active' and a.query like '%pg_sleep%' and a.xact_start < a.query_start)"
        + " from " + LeaderSessions;

    /// <summary>Waits until <see cref="LeaderInItsMonitor" /> holds, and says what the leader's sessions were doing if it never does.</summary>
    internal static async Task WaitForTheLeaderInItsMonitorAsync(string database, long[] ids)
    {
        try
        {
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(database, LeaderInItsMonitor, ("ids", ids)),
                TimeSpan.FromSeconds(60),
                "the HotCold leader's lock monitor to be running its sleep inside the leadership transaction");
        }
        catch (TimeoutException exception)
        {
            string sessions = await LiveWait.ScalarAsync<string>(
                database,
                "select string_agg(format('pid %s %s, xact_start %s, query_start %s: %s', a.pid, a.state, a.xact_start, a.query_start, left(a.query, 80)), '; ')"
                + " from " + LeaderSessions,
                ("ids", ids)) ?? "none";

            throw new TimeoutException(exception.Message + " The sessions holding the store's daemon locks: " + sessions, exception);
        }
    }

    /// <summary>
    /// The HotCold leader, as this class's host runs it - <c>LeadershipPollingTime</c> one second and
    /// <c>Events.UseMonitoredAdvisoryLock</c> at its default, on - with its leadership lock in a transaction and on the
    /// session, at every Marten.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both scopes, on both legs.</b> <c>Events.UseAdvisoryLockTransaction</c> is on by default at Marten 9.39 and 9.40
    /// and off from 9.41 (#5526), and it is a setting at both ends - <c>EventGraph.cs:279</c> at V9.39.0,
    /// <c>EventGraph.cs:290</c> at V9.50.2, read by <c>ProjectionCoordinator.buildLockFactory</c> into Weasel's
    /// <c>AdvisoryLockOptions.TransactionalLockEnabled</c> at both - so each case sets it rather than skipping the version
    /// whose default is the other one. True is what a host behind a transaction-mode pooler keeps; false is T14's and
    /// K04's remedy, given at every version.
    /// </para>
    /// <para>
    /// <b>Transaction-scoped</b>, the leader takes its lock with <c>pg_try_advisory_xact_lock</c> in a transaction it never
    /// ends. Measured at 9.39 (JasperFx 2.74, Weasel 9.33,
    /// DistributedLock.Postgres 1.3.0): it sits <c>idle in transaction</c>, with a snapshot, only until the first leadership
    /// poll after it took the lock - about 0.9 s after the lock was seen at this one-second polling time, about 5 s at the
    /// default <c>LeadershipPollingTime</c> - when <c>HasLock</c> first touches the lock handle's <c>HandleLostToken</c> and
    /// DistributedLock's monitor starts. From then on the leader is <c>active</c> in the monitor's <c>pg_sleep(60)</c>,
    /// inside the same transaction, and the monitor re-issues the sleep the moment it ends, once a minute. So it is never
    /// idle in its transaction for a minute, and C04 never raises it.
    /// </para>
    /// <para>
    /// This test used to read the leader's state once and require <c>idle in transaction</c>, and then require C04's read
    /// to list it idle: both are true only inside that first second, and both failed under load, where the reads came
    /// after the monitor had started (the leader read <c>active</c>, as a <c>fallout Test</c> run recorded). It now waits
    /// for the state the leader keeps for the rest of its leadership - its monitor's statement running in its leadership
    /// transaction, which is also the proof that the lock is transaction-scoped - and proves C04 quiet against it. That C04
    /// does see and raise this leader once it really is idle in its transaction is
    /// <see cref="HotColdLeaderSessionLiveTests.With_the_lock_monitor_off_C04_raises_the_HotCold_leader_after_a_minute" />.
    /// </para>
    /// <para>
    /// <b>Session-scoped</b>, the leader keeps no transaction open, so C04 never lists it. Neither case is judged at a
    /// shifted clock: a session that is not idle has no idle time to project.
    /// </para>
    /// </remarks>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    [HealthQuiet("C04")]
    public async Task The_HotCold_daemons_leadership_session_is_never_raised_as_idle_in_a_transaction(bool transactionScopedLock)
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(
            postgres.ConnectionString, "health_c04_hotcold_" + (transactionScopedLock ? "xact" : "session"));

        using IHost host = await StartAsync(
            database, DaemonMode.HotCold, options => options.Events.UseAdvisoryLockTransaction = transactionScopedLock);

        try
        {
            SessionLockLabels labels = MartenSessionLockLabeler.For(host.Services.GetRequiredService<IDocumentStore>(), "default", null);
            long[] ids = [.. labels.DaemonLockIds];

            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select exists (select 1 from pg_locks l join pg_stat_activity a on a.pid = l.pid"
                    + " where a.datname = current_database() and l.locktype = 'advisory' and l.granted"
                    + " and ((l.classid::int8 << 32) | l.objid::int8) = any(@ids))",
                    ("ids", ids)),
                TimeSpan.FromSeconds(60),
                "the HotCold leader to hold one of Marten's daemon locks");

            await using ConnectionsLiveHost studio = await ConnectionsLiveHost.CreateAsync(database);

            // The real gatherer's read, of every idle-in-transaction session rather than only those idle for a second, so a
            // read that lands between two of the monitor's sleeps lists the leader rather than missing it.
            IdleInTransactionCheck check = new();

            Task<IdleInTransactionFacts> ReadFromZeroAsync() =>
                studio.GatherAsync(static (context, token) => IdleInTransactionCheck.ReadAsync(context, TimeSpan.Zero, token));

            if (transactionScopedLock && LeaderKeepsItsTransactionWithAKeepalive)
            {
                // Weasel 9.45: no monitor statement, so the leader sits idle in its leadership transaction, with a snapshot,
                // between DistributedLock's keepalives. C04's own read lists it, holding the store's daemon lock.
                IdleInTransactionFacts? seen = null;
                await LiveWait.UntilAsync(
                    async () =>
                    {
                        IdleInTransactionFacts facts = await ReadFromZeroAsync();
                        seen = facts.Sessions.Any(static x => x.HoldsDaemonLock && x.HasSnapshot) ? facts : null;
                        return seen is not null;
                    },
                    TimeSpan.FromSeconds(60),
                    "C04's read to list the HotCold leader idle in its leadership transaction, holding its daemon lock");

                IdleSession leader = seen!.Sessions.First(static x => x.HoldsDaemonLock && x.HasSnapshot);
                leader.State.Should().Be("idle in transaction");

                // Judged at the moment it has been idle a minute - the keepalive's own cadence, C04's Warning line - it is
                // recognised by its lock and not raised: that is the Warning C04 gave before.
                TimeSpan toAMinute = ConnectionThresholds.IdleInTransactionWarning + TimeSpan.FromSeconds(1) - leader.IdleAtRead;
                HealthVerdict atAMinute = check.Evaluate(seen, HealthEvaluation.At(seen.ReadAt + toAMinute));
                atAMinute.Findings.Should().BeEmpty("Marten's own leader between keepalives is an expected state, recognised by its lock");
                atAMinute.Note.Should().Contain("holding Marten's own daemon leadership lock");

                // Past the bound, a leader whose keepalive stopped is raised like any other session holding a snapshot.
                check.Evaluate(seen, HealthEvaluation.At(seen.ReadAt + ConnectionThresholds.DaemonLeaderIdleBound))
                    .Findings.Should().ContainSingle("past the bound the leader's snapshot really does hold back vacuum");

                HealthReport report = await studio.ReportAsync();
                report.Findings.Should().NotContain(static x => x.Code == "C04");
            }
            else if (transactionScopedLock)
            {
                // Which is also the proof that the lock is transaction-scoped, at this Marten too: the sleep runs in a
                // transaction that began before it did.
                await WaitForTheLeaderInItsMonitorAsync(database, ids);

                // C04's own read: the leader is working, not idle, inside its transaction. A read that lands in the instant
                // between two sleeps may list it, idle for no time at all.
                IdleInTransactionFacts facts = await ReadFromZeroAsync();

                facts.Sessions.Where(static x => x.HoldsDaemonLock).Should().AllSatisfy(static x =>
                    x.IdleAtRead.Should().BeLessThan(TimeSpan.FromSeconds(1), "the lock monitor re-issues its sleep the moment the last one ends"));
                check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt)).Findings.Should().BeEmpty(
                    "the leader's lock monitor keeps its leadership transaction busy, so it is never idle in it for a minute");
            }
            else
            {
                // The leader's own state, as a superuser sees it.
                string leaderStates = await LiveWait.ScalarAsync<string>(
                    database, "select string_agg(distinct a.state, ',') from " + LeaderSessions, ("ids", ids)) ?? string.Empty;

                leaderStates.Should().NotBeEmpty("the leader holds one of Marten's daemon locks").And.NotContain("in transaction",
                    "a session-scoped leadership lock leaves no transaction open");

                // So C04's read never lists it, even from zero.
                IdleInTransactionFacts facts = await ReadFromZeroAsync();

                facts.Sessions.Should().NotContain(static x => x.HoldsDaemonLock);
                check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt)).Findings.Should().BeEmpty();
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private sealed class Operator : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "test"))));
    }
}
