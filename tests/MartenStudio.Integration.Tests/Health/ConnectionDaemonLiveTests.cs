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
/// small pool and a large one, and C04 beside a HotCold daemon - whose leader, at the Marten floor, sits idle in a
/// transaction for as long as it leads, and must never be raised for it.
/// </summary>
public class ConnectionDaemonLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<IHost> StartAsync(string connectionString, DaemonMode mode)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMarten(options =>
            {
                SampleStore.Configure(options, connectionString);
                options.Projections.LeadershipPollingTime = 1_000;
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

    /// <summary>The first Marten whose HotCold leader holds its lock for the session rather than in a transaction.</summary>
    private static readonly Version LeadershipLockIsTheSessions = new(9, 41);

    /// <summary>
    /// The HotCold leader. At Marten 9.39 and 9.40 (<c>Events.UseAdvisoryLockTransaction</c> on by default) it holds
    /// its leadership with <c>pg_try_advisory_xact_lock</c> in a transaction it never ends - with a snapshot, measured -
    /// and re-polls its locks about once a second, so it is <c>idle in transaction</c> for a second at a time: C04's own
    /// read lists it, holding Marten's daemon lock, and judges it by its real idle time, which is never a minute. From
    /// 9.41 the lock is the session's and the leader is merely idle, so C04 never lists it. Each branch proves its own
    /// half against the leader it found; neither is judged at a shifted clock, because a session that changes every
    /// second has no idle time to project.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("C04")]
    public async Task The_HotCold_daemons_leadership_session_is_never_raised_as_idle_in_a_transaction()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c04_hotcold");

        using IHost host = await StartAsync(database, DaemonMode.HotCold);

        try
        {
            SessionLockLabels labels = SessionLockLabels.For(host.Services.GetRequiredService<IDocumentStore>(), "default", null);
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

            // The real gatherer's read, of every idle-in-transaction session rather than only those idle for a second: the
            // leader re-polls about once a second, so the check's own floor would rarely catch it at all.
            IdleInTransactionCheck check = new();

            Task<IdleInTransactionFacts> ReadFromZeroAsync() =>
                studio.GatherAsync(static (context, token) => IdleInTransactionCheck.ReadAsync(context, TimeSpan.Zero, token));
            Version marten = MartenRuntime.Current.VersionOf(RuntimePackage.Marten) ?? throw new InvalidOperationException("No Marten version.");

            // The leader's own state, as a superuser sees it, for the half each branch proves.
            string leaderStates = await LiveWait.ScalarAsync<string>(
                database,
                "select string_agg(distinct a.state, ',') from pg_stat_activity a where a.pid in (select l.pid from pg_locks l"
                + " where l.locktype = 'advisory' and l.granted and ((l.classid::int8 << 32) | l.objid::int8) = any(@ids))",
                ("ids", ids)) ?? string.Empty;

            if (marten < LeadershipLockIsTheSessions)
            {
                // "active" too, when the read lands on one of its polls.
                leaderStates.Should().Contain("idle in transaction", "at 9.39 and 9.40 the leadership lock is transaction-scoped");

                // C04's own read must list it, holding Marten's daemon lock; and judged at that read, it is not raised,
                // because it has been idle for a second at most.
                IdleInTransactionFacts? seen = null;

                await LiveWait.UntilAsync(
                    async () =>
                    {
                        IdleInTransactionFacts facts = await ReadFromZeroAsync();
                        seen = facts.Sessions.Any(static x => x.HoldsDaemonLock) ? facts : null;
                        return seen is not null;
                    },
                    TimeSpan.FromSeconds(30),
                    "C04's read to list the HotCold leader, idle in its transaction with Marten's daemon lock");

                IdleSession leader = seen!.Sessions.First(static x => x.HoldsDaemonLock);
                leader.State.Should().Be("idle in transaction");
                leader.HasTransactionId.Should().BeFalse("the leader writes nothing in its leadership transaction");
                leader.IdleAtRead.Should().BeLessThan(ConnectionThresholds.IdleInTransactionWarning,
                    "it re-polls its locks about once a second, so it is never idle for a minute");

                HealthVerdict verdict = check.Evaluate(seen, HealthEvaluation.At(seen.ReadAt));
                verdict.Findings.Should().BeEmpty("the session C04 read was idle for a second, not a minute");
            }
            else
            {
                leaderStates.Should().NotBeEmpty("the leader holds one of Marten's daemon locks").And.NotContain("in transaction",
                    "from 9.41 the leadership lock is the session's and leaves no transaction open");

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
