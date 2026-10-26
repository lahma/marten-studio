using JasperFx.Events.Daemon;

using MartenStudio.Services.Health.Checks;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;

namespace MartenStudio.Tests.Health;

/// <summary>K08's evaluator at its edges: each lock scope, monitor on and off, both monitors, and each timeout (W5-S2).</summary>
public class DaemonTimeoutsCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static ServerSettingValue Timeout(string name, int milliseconds, string source = "user") =>
        new(name, milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "ms", source, false, "user", "0");

    private static DaemonTimeoutsFacts Facts(
        bool transaction = true,
        bool monitored = true,
        bool keepalive = true,
        int idleInTransaction = 0,
        int? transactionTimeout = 0,
        int idleSession = 0,
        DaemonMode mode = DaemonMode.HotCold) =>
        new(
            mode,
            transaction,
            monitored,
            TimeSpan.FromSeconds(5),
            keepalive,
            Timeout("idle_in_transaction_session_timeout", idleInTransaction),
            transactionTimeout is { } t ? Timeout("transaction_timeout", t) : null,
            Timeout("idle_session_timeout", idleSession),
            "true - the default on Marten 9.39.0");

    private static HealthVerdict Evaluate(DaemonTimeoutsFacts facts) =>
        new DaemonTimeoutsCheck().Evaluate(facts, HealthEvaluation.At(Now)).ShouldBeWellFormedFor<DaemonTimeoutsCheck>();

    [Theory]
    [InlineData(true, 30_000, true)]
    [InlineData(true, 182_000, true)]
    [InlineData(true, 183_000, false)]
    [InlineData(false, 17_000, true)]
    [InlineData(false, 18_000, false)]
    public void Idle_in_transaction_is_judged_against_three_of_the_leaders_idle_stretches(bool keepalive, int milliseconds, bool fires)
    {
        HealthVerdict verdict = Evaluate(Facts(keepalive: keepalive, idleInTransaction: milliseconds));

        if (!fires)
        {
            verdict.Findings.Should().BeEmpty();
            return;
        }

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Variant.Should().Be("idle-in-transaction");
        finding.Subject.Should().Be("idle_in_transaction_session_timeout");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(x => x.Label == "Set by" && x.Value.Contains("ALTER ROLE", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(x => x.Label == "Leader idle" && x.Value.Contains(keepalive ? "keepalives" : "pg_sleep", StringComparison.Ordinal));
        finding.WhyItMatters.Should().Contain("ALTER ROLE … SET idle_in_transaction_session_timeout = 0");
        finding.Remedy!.Code.Should().Contain("opts.Events.UseAdvisoryLockTransaction = false;");
    }

    [Fact]
    public void With_the_monitor_off_any_idle_in_transaction_timeout_ends_the_leader()
    {
        Evaluate(Facts(monitored: false, idleInTransaction: 3_600_000)).Findings.Should().ContainSingle()
            .Which.Evidence.Should().Contain(x => x.Label == "Leader idle" && x.Value.Contains("for as long as it leads", StringComparison.Ordinal));
    }

    [Fact]
    public void A_transaction_timeout_ends_the_leadership_transaction_whatever_the_monitor()
    {
        HealthVerdict verdict = Evaluate(Facts(transactionTimeout: 3_600_000, idleInTransaction: 3_600_000));

        verdict.Findings.Should().ContainSingle().Which.Variant.Should().Be("transaction-timeout");
    }

    [Fact]
    public void Below_17_there_is_no_transaction_timeout_to_judge()
    {
        Evaluate(Facts(transactionTimeout: null)).Note.Should().Contain("transaction_timeout not shown or not on this server");
    }

    [Theory]
    [InlineData(true, 60_000, true)]
    [InlineData(true, 183_000, false)]
    [InlineData(false, 3_600_000, true)]
    public void A_session_scoped_lock_is_judged_on_idle_session_timeout_and_nothing_else(bool monitored, int milliseconds, bool fires)
    {
        HealthVerdict verdict = Evaluate(Facts(transaction: false, monitored: monitored, idleSession: milliseconds, idleInTransaction: 1_000, transactionTimeout: 1_000));

        if (!fires)
        {
            verdict.Findings.Should().BeEmpty("a session-scoped lock holds no transaction for the other two to end");
            return;
        }

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Variant.Should().Be("idle-session");
        finding.Remedy!.Code.Should().Contain("opts.Events.UseAdvisoryLockTransaction = true;");
    }

    [Theory]
    [InlineData(DaemonMode.Solo)]
    [InlineData(DaemonMode.Disabled)]
    [InlineData(DaemonMode.ExternallyManaged)]
    public void Only_a_HotCold_daemon_is_judged(DaemonMode mode)
    {
        HealthVerdict verdict = Evaluate(Facts(mode: mode, idleInTransaction: 1_000, transactionTimeout: 1_000));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(mode.ToString());
    }

    [Fact]
    public void Quiet_with_the_timeouts_off()
    {
        Evaluate(Facts()).Note.Should().Be(
            "The leadership lock is transaction-scoped; idle_in_transaction_session_timeout is off (0) and transaction_timeout off (0), "
            + "neither of which ends the leader's transaction.");
    }

    // ---- Which lock monitor runs: read off DistributedLock, never a Marten or Weasel version ----------------------------

    [Theory]
    [InlineData("1.3.3+425b94ad7888d7c275f7d3358e2f4cf8844c7206", null, null, true, "DistributedLock.Postgres 1.3.3 (loaded)")]
    [InlineData("1.3.0+306be8b2524f953263acd0876abeabb9581bcf8a", null, "9.45.0", false, "DistributedLock.Postgres 1.3.0 (loaded)")]
    [InlineData(null, "1.3.3.0", null, true, "assembly version 1.3.3.0")]
    [InlineData(null, "1.0.0.0", "9.45.0", false, "assembly version 1.0.0.0")]
    [InlineData(null, null, "9.34.0", true, "Weasel.Postgresql 9.34.0")]
    [InlineData(null, null, "9.33.0", false, "Weasel.Postgresql 9.33.0")]
    [InlineData(null, null, null, true, "taken for the keepalive")]
    public void The_monitor_is_the_loaded_DistributedLocks_then_the_one_Weasel_references_then_Weasels_own_version(
        string? loaded, string? referenced, string? weasel, bool keepalive, string basis)
    {
        LeaderLockMonitor monitor = LeaderLockMonitor.Of(
            loaded,
            referenced is null ? null : Version.Parse(referenced),
            weasel is null ? null : Version.Parse(weasel));

        monitor.Keepalive.Should().Be(keepalive);
        monitor.Basis.Should().Contain(basis);
    }

    /// <summary>
    /// The boundary is a patch release of the floor: Marten 9.39.0 runs Weasel 9.33 and the upstream DistributedLock 1.3.0,
    /// 9.39.1 already Weasel 9.34 and the Patched 1.3.3 (weasel#614) - so this run's own answer follows the Marten it resolved.
    /// </summary>
    [Fact]
    public void This_runs_monitor_is_the_keepalive_exactly_from_Marten_9_39_1()
    {
        Version marten = MartenStudio.Services.Configuration.MartenRuntime.Current.VersionOf(MartenStudio.Services.Configuration.RuntimePackage.Marten)!;

        LeaderLockMonitor.Current.Keepalive.Should().Be(marten >= new Version(9, 39, 1), LeaderLockMonitor.Current.Basis);
    }

    [Fact]
    public void It_is_MartenStudios_own_reads_the_server_and_lists_its_three_variants()
    {
        HealthCheckInfo info = HealthCheckRegistry.Describe(typeof(DaemonTimeoutsCheck));

        info.Area.Should().Be(HealthArea.Configuration);
        info.Tier.Should().Be(HealthCostTier.Catalog);
        info.Gate.Should().Be(HealthGate.Database);
        info.Variants.Should().Equal("idle-in-transaction", "transaction-timeout", "idle-session");
        typeof(DaemonTimeoutsCheck).Assembly.Should().NotBeSameAs(typeof(HealthCheckRegistry).Assembly);
    }
}
