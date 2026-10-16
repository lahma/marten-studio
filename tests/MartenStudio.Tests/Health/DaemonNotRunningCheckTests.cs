using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using static MartenStudio.Tests.Health.HighWaterPinnedCheckTests;

namespace MartenStudio.Tests.Health;

/// <summary>D02's judgement: every one of the three kinds of evidence has to agree, and every stand-down holds.</summary>
public class DaemonNotRunningCheckTests
{
    /// <summary>Events up to 500, a mark at 500 nothing pinned, nothing written for <paramref name="quiet" />.</summary>
    private static HighWaterReading Quiet(TimeSpan? quiet) => new(
        Now,
        HighestSequence: 500,
        Mark: 500,
        MarkUpdated: quiet is { } q ? Now - q : null,
        StuckMark: null,
        StuckSince: null,
        FenceMark: null,
        FenceUpdated: null,
        LatestWrite: quiet is { } w ? Now - w : null,
        NextCommitted: null);

    private static readonly IReadOnlyList<DaemonShardRow> Behind =
    [
        new DaemonShardRow("DailySales:All", 120, Now - TimeSpan.FromHours(1)),
        new DaemonShardRow("ShipmentTracker:All", 500, Now - TimeSpan.FromHours(1)),
    ];

    private static readonly MartenRuntime Before943 =
        MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = "9.42.0" });

    private static readonly MartenRuntime From943 =
        MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = "9.43.0" });

    private static HealthVerdict Evaluate(
        HighWaterReading reading,
        IReadOnlyList<DaemonShardRow>? shards = null,
        DaemonInProcess? inProcess = null,
        IReadOnlyList<DaemonLockHolder>? holders = null,
        MartenRuntime? runtime = null) =>
        new DaemonNotRunningCheck().Evaluate(
            new DaemonNotRunningFacts(DaemonConfiguration(), reading, shards ?? Behind, inProcess ?? DaemonInProcess.NotHosted, holders ?? []),
            HealthEvaluation.At(Now, runtime ?? Before943));

    [Fact]
    public void Work_no_lock_holder_and_a_silent_table_is_critical()
    {
        HealthFinding finding = Evaluate(Quiet(TimeSpan.FromMinutes(10))).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D02");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be("no-daemon");
        finding.Title.Should().Contain("380 events behind");
        finding.Evidence.Should().Contain(x => x.Label == "Leadership locks held" && x.Value.StartsWith("none of this store's 3 lock ids", StringComparison.Ordinal));
        finding.Remedy!.Code.Should().Contain("AddAsyncDaemon(DaemonMode.HotCold)");
    }

    /// <summary>A shard with no row has processed nothing: a never-started daemon is the plainest case of all.</summary>
    [Fact]
    public void A_daemon_that_never_ran_has_no_rows_and_fires()
    {
        HealthFinding finding = Evaluate(Quiet(null) with { Mark = null }, shards: []).Findings.Single();

        finding.Title.Should().Contain("500 events behind");
        finding.Evidence.Should().Contain(new HealthEvidence("Last write to mt_event_progression", "never"));
    }

    /// <summary>2 x SlowPollingTime + 1 min: with the default 1 s, 62 s.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(62)]
    public void A_write_within_the_window_is_quiet(int seconds)
    {
        Evaluate(Quiet(TimeSpan.FromSeconds(seconds))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Just_past_the_window_fires()
    {
        Evaluate(Quiet(TimeSpan.FromSeconds(63))).Findings.Should().ContainSingle();
    }

    [Fact]
    public void Nothing_behind_is_quiet()
    {
        IReadOnlyList<DaemonShardRow> caughtUp =
        [
            new DaemonShardRow("DailySales:All", 500, Now - TimeSpan.FromHours(1)),
            new DaemonShardRow("ShipmentTracker:All", 500, Now - TimeSpan.FromHours(1)),
        ];

        Evaluate(Quiet(TimeSpan.FromHours(1)), caughtUp).Findings.Should().BeEmpty();
    }

    /// <summary>"Never for a host whose daemon runs in this process and is healthy."</summary>
    [Fact]
    public void A_healthy_daemon_in_this_process_is_quiet_whatever_the_table_says()
    {
        DaemonInProcess healthy = DaemonInProcess.NotHosted with { Hosting = DaemonHostingState.Hosted, IsRunning = true };

        HealthVerdict verdict = Evaluate(Quiet(TimeSpan.FromHours(1)), inProcess: healthy);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("hosted in this process");
    }

    [Fact]
    public void The_studios_own_pause_is_quiet()
    {
        DaemonInProcess paused = DaemonInProcess.NotHosted with
        {
            PausedByStudio = new StudioDaemonPause("default", "operator", Now, "localhost.marten", null),
        };

        Evaluate(Quiet(TimeSpan.FromHours(1)), inProcess: paused).Findings.Should().BeEmpty();
    }

    /// <summary>A mark pinned at 500 behind a gap, with 505 committed; nothing written for an hour unless the mark row says otherwise.</summary>
    private static HighWaterReading PinnedQuiet(TimeSpan? markRowAge = null) => Quiet(TimeSpan.FromHours(1)) with
    {
        HighestSequence = 520,
        StuckMark = 500,
        StuckSince = Now - TimeSpan.FromHours(1),
        NextCommitted = 505,
        MarkUpdated = Now - (markRowAge ?? TimeSpan.FromHours(1)),
        LatestWrite = Now - (markRowAge ?? TimeSpan.FromHours(1)),
    };

    /// <summary>Below Marten 9.43 a daemon pinned behind a gap writes nothing at all: its silence is D01's, not D02's.</summary>
    [Fact]
    public void Below_9_43_a_mark_pinned_behind_a_gap_stands_D02_down()
    {
        HealthVerdict verdict = Evaluate(PinnedQuiet(), runtime: Before943);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("D01").And.Contain("Below Marten 9.43");
    }

    /// <summary>
    /// From 9.43 a running daemon restamps the mark's row even while pinned (#5541): a pin whose row has gone quiet is a
    /// daemon that died pinned, and D02 says so - otherwise D01 stays Critical for ever and nothing says why.
    /// </summary>
    [Fact]
    public void From_9_43_a_pin_whose_mark_row_went_quiet_is_a_dead_daemon()
    {
        HealthFinding finding = Evaluate(PinnedQuiet(), runtime: From943).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be("no-daemon");
    }

    [Fact]
    public void From_9_43_a_pin_whose_mark_row_is_still_restamped_stands_D02_down()
    {
        HealthVerdict verdict = Evaluate(PinnedQuiet(markRowAge: TimeSpan.FromSeconds(2)), runtime: From943);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("restamped it 2 s ago");
    }

    /// <summary>
    /// The H2 review's follow-up: a HotCold node just restarted holds no lock and has written nothing yet. While its
    /// process is younger than the quiet window, it is starting, not dead.
    /// </summary>
    [Fact]
    public void A_daemon_hosted_here_in_a_process_younger_than_the_window_is_starting_not_dead()
    {
        DaemonInProcess starting = DaemonInProcess.NotHosted with
        {
            Hosting = DaemonHostingState.Hosted,
            IsRunning = false,
            ProcessStartedAt = Now - TimeSpan.FromSeconds(20),
        };

        HealthVerdict verdict = Evaluate(Quiet(TimeSpan.FromMinutes(10)), inProcess: starting);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("which started 20 s ago").And.Contain("to take the leadership");
    }

    [Fact]
    public void A_daemon_hosted_here_in_an_old_process_that_is_not_running_is_reported()
    {
        DaemonInProcess stopped = DaemonInProcess.NotHosted with
        {
            Hosting = DaemonHostingState.Hosted,
            IsRunning = false,
            ProcessStartedAt = Now - TimeSpan.FromHours(2),
        };

        Evaluate(Quiet(TimeSpan.FromMinutes(10)), inProcess: stopped).Findings.Single().Severity.Should().Be(HealthSeverity.Critical);
    }

    /// <summary>A process start says nothing about a daemon that is not hosted here.</summary>
    [Fact]
    public void A_young_process_that_hosts_no_daemon_is_no_reason_to_stand_down()
    {
        DaemonInProcess none = DaemonInProcess.NotHosted with { ProcessStartedAt = Now - TimeSpan.FromSeconds(5) };

        Evaluate(Quiet(TimeSpan.FromMinutes(10)), inProcess: none).Findings.Should().ContainSingle();
    }

    /// <summary>
    /// The other finding: a session holds the leadership lock and nothing is written. Who it is sits behind the
    /// activity gate, label by ordinal, the pid in the gated value.
    /// </summary>
    [Fact]
    public void A_held_lock_with_nothing_written_is_a_warning_naming_the_holder_only_behind_the_activity_gate()
    {
        DaemonLockHolder holder = new(4242, 4444, "app", "billing-api", "10.0.0.5", Now - TimeSpan.FromDays(1), "idle in transaction");

        HealthFinding finding = Evaluate(Quiet(TimeSpan.FromMinutes(10)), holders: [holder]).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be("lock-held-idle");
        finding.Title.Should().NotContain("4242");

        HealthEvidence named = finding.Evidence.Single(static x => x.Label == "Lock holder 1");
        named.Gate.Should().Be(HealthGate.Activity);
        named.Value.Should().Contain("pid 4242").And.Contain("billing-api").And.Contain("10.0.0.5");

        finding.Evidence.Where(static x => x.Gate == HealthGate.None).Select(static x => x.Value)
            .Should().NotContain(x => x.Contains("4242", StringComparison.Ordinal), "a pid names a session");
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_store_with_no_async_work_is_quiet()
    {
        DaemonNotRunningFacts facts = new(DaemonConfiguration(shards: []), Quiet(TimeSpan.FromHours(1)), [], DaemonInProcess.NotHosted, []);

        new DaemonNotRunningCheck().Evaluate(facts, HealthEvaluation.At(Now)).Findings.Should().BeEmpty();
    }

    /// <summary>The lag is the screen's: against the highest sequence handed out, the furthest-behind shard.</summary>
    [Fact]
    public void The_lag_is_the_furthest_behind_registered_shard_and_a_missing_row_is_zero()
    {
        new DaemonNotRunningFacts(DaemonConfiguration(), Quiet(null), Behind, DaemonInProcess.NotHosted, null).Lag.Should().Be(380);
        new DaemonNotRunningFacts(DaemonConfiguration(), Quiet(null), [Behind[1]], DaemonInProcess.NotHosted, null).Lag.Should().Be(500);
    }
}
