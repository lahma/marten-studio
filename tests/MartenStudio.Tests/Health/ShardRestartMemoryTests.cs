using System.Globalization;
using System.Reflection;

using JasperFx.Events.Daemon;

using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using static MartenStudio.Tests.Health.HighWaterPinnedCheckTests;

namespace MartenStudio.Tests.Health;

/// <summary>
/// D05 across a restart. The coordinator starts a paused agent again as a new one, which reads Running with no failure until
/// it reaches the failing event again (JasperFx 2.74.0 and 2.87.1), so what an earlier sweep saw is what keeps a shard that
/// keeps failing reported - and progress past where it failed is what ends that.
/// </summary>
public class ShardRestartMemoryTests
{
    private const string Shard = "ShipmentTracker:All";

    /// <summary>The first pause, three seconds before the earlier sweep that saw it.</summary>
    private static readonly DateTimeOffset FirstPause = Now - TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(3);

    /// <summary>The next attempt's pause: the coordinator restarted the shard five seconds after the first, and it failed again.</summary>
    private static readonly DateTimeOffset SecondPause = FirstPause + TimeSpan.FromSeconds(6);

    private static DaemonInProcess Hosted(params DaemonAgentFact[] agents) => DaemonInProcess.NotHosted with
    {
        Hosting = DaemonHostingState.Hosted,
        IsRunning = true,
        Agents = agents,
    };

    private static ShardFailedOrPausedFacts Facts(DaemonInProcess inProcess, IReadOnlyList<DaemonShardRow>? rows = null) =>
        new(DaemonConfiguration(extended: rows is not null), rows, inProcess);

    private static DaemonAgentFact Running(long? position) => new(Shard, "Running", null, null, null, position);

    private static DaemonAgentFact Paused(DateTimeOffset at, long position) =>
        new(Shard, "Paused", "ApplyEvent", "boom", at, position, 41, "item_added");

    /// <summary>What an earlier sweep, <paramref name="ago" /> before now, recorded of the shard failed or paused.</summary>
    private static HealthSample SeenFailing(
        TimeSpan ago,
        long position,
        DateTimeOffset? pausedAt = null,
        ShardFailureCategory? category = ShardFailureCategory.ApplyEvent)
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal) { [DaemonSeries.ShardFailedAt(Shard)] = position };

        if (pausedAt is { } paused)
        {
            values[DaemonSeries.ShardPausedAt(Shard)] = paused.ToUnixTimeMilliseconds();
        }

        if (category is { } kind)
        {
            values[DaemonSeries.ShardFailureCategory(Shard)] = (int) kind;
        }

        return new HealthSample(Now - ago, values);
    }

    private static HealthVerdict Evaluate(ShardFailedOrPausedFacts facts, params HealthSample[] samples) =>
        new ShardFailedOrPausedCheck().Evaluate(facts, HealthEvaluation.At(Now, history: new HealthHistoryView(samples)));

    private static string U(DateTimeOffset at) => at.ToString("u", CultureInfo.InvariantCulture);

    // ---- The restart window: the agent reads Running ------------------------------------------------------------------

    [Fact]
    public void An_agent_started_again_short_of_where_it_was_seen_failing_keeps_failing_and_being_restarted()
    {
        HealthFinding finding = Evaluate(Facts(Hosted(Running(40))), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause))
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D05");
        finding.Severity.Should().Be(HealthSeverity.Warning, "the coordinator keeps trying; nothing has given up on the shard");
        finding.Subject.Should().Be(Shard);
        finding.Title.Should().Be("Shard ShipmentTracker:All keeps failing and being restarted");
        finding.WhyItMatters.Should().Contain("Between a restart and the next failure it reads Running");
        finding.Evidence.Should().Contain(new HealthEvidence(
            "Status", "Running (the daemon in this process), started again after a failure and not past it yet"));
        finding.Evidence.Should().Contain(new HealthEvidence(
            "Seen failing before",
            "paused at " + U(FirstPause) + ", at position 40, seen by the advisor's check at " + U(Now - TimeSpan.FromMinutes(1))));
        finding.Evidence.Should().Contain(new HealthEvidence("Position now", "40"));
        finding.Evidence.Should().Contain(new HealthEvidence("Failure", "ApplyEvent"), "how it failed is remembered with where");
        finding.Evidence.Should().NotContain(static x => x.Label == "Paused at", "nothing is paused right now");
        finding.Remedy!.Code.Should().Be("opts.Projections.Errors.SkipApplyErrors = true;");
        finding.Caveat.Should().Contain("until no check has seen it fail, or still standing where it failed, for 10 min");
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    /// <summary><c>ISubscriptionAgent.Position</c> reads 0 until the agent's start command has been processed.</summary>
    [Fact]
    public void A_fresh_agent_that_reads_position_zero_before_its_start_is_processed_is_kept_too()
    {
        Evaluate(Facts(Hosted(Running(0))), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause))
            .Findings.Should().ContainSingle().Which.Title.Should().EndWith("keeps failing and being restarted");
    }

    [Fact]
    public void A_shard_past_where_it_was_seen_failing_has_recovered_and_is_quiet()
    {
        Evaluate(Facts(Hosted(Running(41))), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause))
            .Findings.Should().BeEmpty("its first committed progress past the failure ends it");
    }

    [Fact]
    public void A_running_agent_no_check_saw_failing_is_quiet()
    {
        Evaluate(Facts(Hosted(Running(40)))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_failure_no_check_has_seen_for_longer_than_the_memory_is_forgotten()
    {
        TimeSpan memory = ShardFailedOrPausedCheck.RestartMemoryFor(TimeSpan.FromMinutes(1));
        memory.Should().Be(TimeSpan.FromMinutes(10), "the trailing window, at the default HealthInterval");

        Evaluate(Facts(Hosted(Running(40))), SeenFailing(memory, 40, FirstPause))
            .Findings.Should().ContainSingle("ten minutes ago is still inside the memory");
        Evaluate(Facts(Hosted(Running(40))), SeenFailing(memory + TimeSpan.FromSeconds(1), 40, FirstPause))
            .Findings.Should().BeEmpty();
    }

    /// <summary>
    /// A host that samples once an hour: ten minutes would have forgotten every failure before the next sample came, so the
    /// memory is three intervals there.
    /// </summary>
    [Fact]
    public void At_a_one_hour_HealthInterval_the_memory_is_three_intervals()
    {
        ShardFailedOrPausedCheck.RestartMemoryFor(TimeSpan.FromHours(1)).Should().Be(TimeSpan.FromHours(3));

        ShardFailedOrPausedFacts hourly = Facts(Hosted(Running(40)));
        hourly = hourly with { Configuration = hourly.Configuration with { HealthInterval = TimeSpan.FromHours(1) } };

        Evaluate(hourly, SeenFailing(TimeSpan.FromHours(1), 40, FirstPause))
            .Findings.Should().ContainSingle("the last sample, an hour ago, saw it fail");
        Evaluate(Facts(Hosted(Running(40))), SeenFailing(TimeSpan.FromHours(1), 40, FirstPause))
            .Findings.Should().BeEmpty("at the default interval an hour is past the memory");
        Evaluate(hourly, SeenFailing(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(1), 40, FirstPause))
            .Findings.Should().BeEmpty("three intervals and more is past it at any interval");
    }

    /// <summary>
    /// A later sample that saw the shard still standing exactly where it failed - D03's position, which is what a restarted
    /// shard short of its failure shows - starts the memory again; one that saw it anywhere else does not.
    /// </summary>
    [Fact]
    public void A_later_sample_that_saw_the_shard_still_where_it_failed_renews_the_memory()
    {
        HealthSample failed = SeenFailing(TimeSpan.FromMinutes(20), 40, FirstPause);
        HealthSample stillThere = new(Now - TimeSpan.FromMinutes(2), new Dictionary<string, double> { [DaemonSeries.ShardPosition(Shard)] = 40 });
        HealthSample rewound = new(Now - TimeSpan.FromMinutes(2), new Dictionary<string, double> { [DaemonSeries.ShardPosition(Shard)] = 30 });

        Evaluate(Facts(Hosted(Running(40))), failed).Findings.Should().BeEmpty("twenty minutes is past the memory");
        Evaluate(Facts(Hosted(Running(40))), failed, stillThere)
            .Findings.Should().ContainSingle("two minutes ago a sample saw it still where it failed");
        Evaluate(Facts(Hosted(Running(40))), failed, rewound)
            .Findings.Should().BeEmpty("a sample that saw it elsewhere renews nothing");
    }

    /// <summary>The latest sample is the one judged: a shard that failed further on since is judged by that.</summary>
    [Fact]
    public void The_latest_failure_seen_is_the_one_judged()
    {
        HealthSample[] history = [SeenFailing(TimeSpan.FromMinutes(3), 40), SeenFailing(TimeSpan.FromMinutes(1), 90)];

        Evaluate(Facts(Hosted(Running(90))), history).Findings.Should().ContainSingle("a restart resumes from 90, where it failed last");
        Evaluate(Facts(Hosted(Running(60))), history).Findings.Should().BeEmpty("60 is neither where it failed nor a fresh start");
        Evaluate(Facts(Hosted(Running(91))), history).Findings.Should().BeEmpty();
    }

    /// <summary>
    /// A shard that failed, got past it - a later sample saw its position beyond the failure - and was then restarted for
    /// another reason (a Stop and Start, a leadership change), reading 0 before its start is processed: not this failure.
    /// </summary>
    [Fact]
    public void A_recovered_shard_restarted_for_another_reason_is_not_reported()
    {
        HealthSample recovered = new(Now - TimeSpan.FromMinutes(2), new Dictionary<string, double> { [DaemonSeries.ShardPosition(Shard)] = 55 });

        Evaluate(Facts(Hosted(Running(0))), SeenFailing(TimeSpan.FromMinutes(3), 40, FirstPause), recovered)
            .Findings.Should().BeEmpty("the shard's progress past the failure ended it");
        Evaluate(Facts(Hosted(Running(0))), SeenFailing(TimeSpan.FromMinutes(3), 40, FirstPause))
            .Findings.Should().ContainSingle("without that progress, a fresh agent at 0 is the restart window");
    }

    [Fact]
    public void A_shard_rewound_below_where_it_failed_is_not_reported()
    {
        Evaluate(Facts(Hosted(Running(20))), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause))
            .Findings.Should().BeEmpty("a rewind below the failure is an operator's, not a restart");
    }

    [Fact]
    public void An_agent_whose_position_cannot_be_read_is_not_judged_by_the_memory()
    {
        Evaluate(Facts(Hosted(Running(null))), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_shard_being_rebuilt_is_not_judged_by_the_memory()
    {
        DaemonInProcess rebuilding = Hosted(Running(0)) with { StudioRebuilds = ["ShipmentTracker"] };

        Evaluate(Facts(rebuilding), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_category_the_memory_does_not_hold_leaves_the_finding_without_a_remedy()
    {
        HealthFinding finding = Evaluate(Facts(Hosted(Running(40))), SeenFailing(TimeSpan.FromMinutes(1), 40, category: null))
            .Findings.Single();

        finding.Evidence.Should().NotContain(static x => x.Label == "Failure");
        finding.Evidence.Should().Contain(new HealthEvidence(
            "Seen failing before", "at position 40, seen by the advisor's check at " + U(Now - TimeSpan.FromMinutes(1))));
        finding.Remedy.Should().BeNull();
    }

    // ---- Paused again, by a later pause ------------------------------------------------------------------------------

    [Fact]
    public void A_later_pause_no_further_on_than_an_earlier_one_reads_as_keeps_failing_and_being_restarted()
    {
        HealthFinding finding = Evaluate(Facts(Hosted(Paused(SecondPause, 40))), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("Shard ShipmentTracker:All keeps failing and being restarted");
        finding.WhyItMatters.Should().Contain("it failed again, no further on");
        finding.Evidence.Should().Contain(new HealthEvidence("Status", "Paused (the daemon in this process)"));
        finding.Evidence.Should().Contain(new HealthEvidence("Paused at", U(SecondPause)));
        finding.Evidence.Should().Contain(x => x.Label == "Seen failing before" && x.Value.StartsWith("paused at " + U(FirstPause) + ", at position 40", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(new HealthEvidence("Failed on event", "41 (item_added)"), "the agent here names the event it failed on");
        finding.Evidence.Should().NotContain(static x => x.Label == "Position now", "a paused shard stands where it failed");
        finding.Caveat.Should().Be("Read from the daemon hosted in this process.");
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    /// <summary>CL2's case, at a <c>HealthCheckPollingTime</c> longer than the test: one pause, seen by two checks.</summary>
    [Fact]
    public void The_same_pause_seen_by_two_checks_is_still_paused()
    {
        Evaluate(Facts(Hosted(Paused(FirstPause, 40))), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause))
            .Findings.Single().Title.Should().Be("Shard ShipmentTracker:All is paused");
    }

    [Fact]
    public void A_pause_after_progress_past_an_earlier_failure_is_a_new_one()
    {
        Evaluate(Facts(Hosted(Paused(SecondPause, 60))), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause))
            .Findings.Single().Title.Should().EndWith("is paused");
    }

    /// <summary>The sweep's own sample is in the history its evaluator reads, and is not an earlier attempt.</summary>
    [Fact]
    public void This_sweeps_own_sample_is_not_an_earlier_attempt()
    {
        Evaluate(Facts(Hosted(Paused(SecondPause, 40))), SeenFailing(TimeSpan.Zero, 40, FirstPause))
            .Findings.Single().Title.Should().EndWith("is paused");
    }

    /// <summary>A studio beside a daemon in another process has the row's heartbeat - the time of its last status change.</summary>
    [Fact]
    public void Read_from_the_row_alone_a_later_pause_no_further_on_reads_the_same()
    {
        DaemonShardRow row = new(Shard, 40, Now, SecondPause, "Paused", "boom", "ApplyEvent", 41, "item_added");

        HealthFinding finding = Evaluate(Facts(DaemonInProcess.NotHosted, [row]), SeenFailing(TimeSpan.FromMinutes(1), 40, FirstPause))
            .Findings.Single();

        finding.Title.Should().EndWith("keeps failing and being restarted");
        finding.Evidence.Should().Contain(new HealthEvidence("Status", "Paused (mt_event_progression, as its node last wrote it)"));
        finding.Caveat.Should().StartWith("Read from mt_event_progression");
    }

    // ---- What a sweep records ----------------------------------------------------------------------------------------

    [Fact]
    public void A_sweep_records_where_a_paused_shard_stood_when_its_pause_began_and_how_it_failed()
    {
        IReadOnlyList<HealthSamplePart> parts = [.. new ShardFailedOrPausedCheck().Sample(Facts(Hosted(Paused(FirstPause, 40))))];

        parts.Should().BeEquivalentTo(
        [
            new HealthSamplePart(DaemonSeries.ShardFailedAt(Shard), 40),
            new HealthSamplePart(DaemonSeries.ShardPausedAt(Shard), FirstPause.ToUnixTimeMilliseconds()),
            new HealthSamplePart(DaemonSeries.ShardFailureCategory(Shard), (int) ShardFailureCategory.ApplyEvent),
        ]);
    }

    [Fact]
    public void A_stopped_row_is_recorded_at_its_own_position_with_no_pause()
    {
        DaemonShardRow row = new(Shard, 40, Now, Now, "Stopped", "boom", "ProgressionOutOfOrder");

        IReadOnlyList<HealthSamplePart> parts = [.. new ShardFailedOrPausedCheck().Sample(Facts(DaemonInProcess.NotHosted, [row]))];

        parts.Should().BeEquivalentTo(
        [
            new HealthSamplePart(DaemonSeries.ShardFailedAt(Shard), 40),
            new HealthSamplePart(DaemonSeries.ShardFailureCategory(Shard), (int) ShardFailureCategory.ProgressionOutOfOrder),
        ]);
    }

    [Fact]
    public void A_sweep_records_nothing_for_a_running_shard_a_plain_stop_or_a_rebuild()
    {
        ShardFailedOrPausedCheck check = new();

        check.Sample(Facts(Hosted(Running(40)))).Should().BeEmpty();
        check.Sample(Facts(Hosted(new DaemonAgentFact(Shard, "Stopped", null, null, null, 40)))).Should().BeEmpty("a stop with no failure is somebody's stop");
        check.Sample(Facts(Hosted(Paused(FirstPause, 40)) with { StudioRebuilds = ["ShipmentTracker"] })).Should().BeEmpty("a rebuild stops it on purpose");
        check.Sample(Facts(Hosted(Paused(FirstPause, 40) with { Position = null }))).Should().BeEmpty("there is nothing to judge progress by");
    }

    /// <summary>
    /// D05 judges progress and renewal by D03's positions, and an evaluator reads only the series written at or below its own
    /// gate: were D03's gate ever raised above D05's, D05 would silently stop seeing them.
    /// </summary>
    [Fact]
    public void D05_reads_D03s_positions_so_the_two_share_a_gate() =>
        typeof(ShardStalledCheck).GetCustomAttribute<HealthCheckAttribute>()!.Gate
            .Should().Be(typeof(ShardFailedOrPausedCheck).GetCustomAttribute<HealthCheckAttribute>()!.Gate);

    /// <summary>
    /// Two sweeps, as the sweeper runs them: what the first records is what the second's evaluator reads - at D05's own
    /// gate, which the sweeper stamps on every part it writes.
    /// </summary>
    [Fact]
    public void What_one_sweep_records_is_what_the_next_one_reads_at_D05s_own_gate()
    {
        HealthGate gate = typeof(ShardFailedOrPausedCheck).GetCustomAttribute<HealthCheckAttribute>()!.Gate;
        gate.Should().Be(HealthGate.Database);

        ShardFailedOrPausedCheck check = new();
        List<HealthSamplePart> parts = [.. check.Sample(Facts(Hosted(Paused(FirstPause, 40))))];

        HealthSample first = new(
            Now - TimeSpan.FromMinutes(1),
            parts.ToDictionary(static x => x.Series, static x => x.Value, StringComparer.Ordinal))
        {
            Gates = parts.ToDictionary(static x => x.Series, _ => gate, StringComparer.Ordinal),
        };

        HealthHistoryView seen = new HealthHistoryView([first]).VisibleAt(gate);

        check.Evaluate(Facts(Hosted(Running(40))), HealthEvaluation.At(Now, history: seen))
            .Findings.Should().ContainSingle().Which.Title.Should().EndWith("keeps failing and being restarted");
        check.Evaluate(Facts(Hosted(Running(41))), HealthEvaluation.At(Now, history: seen))
            .Findings.Should().BeEmpty();
    }
}
