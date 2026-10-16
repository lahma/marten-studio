using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using static MartenStudio.Tests.Health.HighWaterPinnedCheckTests;

namespace MartenStudio.Tests.Health;

/// <summary>
/// D03's judgement: a shard that has not moved, with the mark ahead of it for the whole window, while the daemon is
/// alive - the window calibrated by the shard's own pages, and the 9.43 rule on what the HighWaterMark row's age means,
/// at each side of it, changing what is believed and not only what is said.
/// </summary>
public class ShardStalledCheckTests
{
    private const string Shard = "DailySales:All";

    private static readonly MartenRuntime Before943 =
        MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = "9.42.0" });

    private static readonly MartenRuntime From943 =
        MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = "9.43.0" });

    private static readonly DaemonInProcess HealthyHere =
        DaemonInProcess.NotHosted with { Hosting = DaemonHostingState.Hosted, IsRunning = true };

    /// <summary>The mark at 200, the sequence at 210; the mark's row written <paramref name="markAge" /> ago, the fence <paramref name="fenceAge" /> ago.</summary>
    private static HighWaterReading Reading(TimeSpan markAge, TimeSpan? fenceAge = null) => new(
        Now,
        HighestSequence: 210,
        Mark: 200,
        MarkUpdated: Now - markAge,
        StuckMark: null,
        StuckSince: null,
        FenceMark: 190,
        FenceUpdated: Now - (fenceAge ?? markAge),
        LatestWrite: Now - markAge,
        NextCommitted: 201);

    /// <summary>A sample <paramref name="ago" /> before now, with the shard at <paramref name="position" /> and the mark at <paramref name="mark" />.</summary>
    private static HealthSample SampleAt(TimeSpan ago, double position, double mark) => new(
        Now - ago,
        new Dictionary<string, double>
        {
            [DaemonSeries.ShardPosition(Shard)] = position,
            [DaemonSeries.HighWaterMark] = mark,
            [DaemonSeries.HighestSequence] = mark + 10,
        });

    private static HealthSample Current(double position = 100) => SampleAt(TimeSpan.Zero, position, 200);

    /// <summary>The advisor saw the mark ahead of the still shard six minutes ago - past the five-minute floor.</summary>
    private static HealthSample AheadSixMinutesAgo => SampleAt(TimeSpan.FromMinutes(6), 100, 150);

    private static HealthVerdict Evaluate(
        HighWaterReading reading,
        DaemonShardRow row,
        IReadOnlyList<HealthSample> samples,
        DaemonInProcess? inProcess = null,
        MartenRuntime? runtime = null,
        DaemonCheckConfiguration? configuration = null)
    {
        ShardStalledFacts facts = new(configuration ?? DaemonConfiguration(shards: [Shard]), reading, [row], inProcess ?? HealthyHere);
        return new ShardStalledCheck().Evaluate(facts, HealthEvaluation.At(Now, runtime ?? From943, new HealthHistoryView(samples)));
    }

    private static DaemonShardRow StillSince(TimeSpan ago, long position = 100) => new(Shard, position, Now - ago);

    [Fact]
    public void A_shard_still_for_the_whole_window_with_the_mark_ahead_and_the_daemon_alive_is_a_warning()
    {
        HealthFinding finding = Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(10)), [AheadSixMinutesAgo, Current()])
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D03");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be(Shard);
        finding.Evidence.Should().Contain(new HealthEvidence("Position", "100"));
        finding.Evidence.Should().Contain(new HealthEvidence("High-water mark", "200"));
        finding.Evidence.Should().Contain(x => x.Label == "Quiet window" && x.Value.StartsWith("5 min (max of 5 x", StringComparison.Ordinal));
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    /// <summary>The H2 review's follow-up: a page of a slow projection can take minutes. Five is the least a shard is given.</summary>
    [Fact]
    public void A_shard_still_for_less_than_the_five_minute_floor_is_quiet()
    {
        Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(4)), [AheadSixMinutesAgo, Current()])
            .Findings.Should().BeEmpty();
    }

    /// <summary>
    /// Calibrated by the shard's own cadence: the advisor once saw it hold still for four minutes with work to do and
    /// then move, so its window is twelve - ten minutes still is a slow page, thirteen is a stall.
    /// </summary>
    [Theory]
    [InlineData(10, false)]
    [InlineData(13, true)]
    public void The_window_is_three_times_the_longest_page_the_advisor_saw_the_shard_take(int stillMinutes, bool fires)
    {
        HealthSample[] samples =
        [
            SampleAt(TimeSpan.FromMinutes(40), 50, 100),
            SampleAt(TimeSpan.FromMinutes(38), 50, 110),
            SampleAt(TimeSpan.FromMinutes(36), 80, 120),
            SampleAt(TimeSpan.FromMinutes(14), 100, 150),
            Current(),
        ];

        HealthVerdict verdict = Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(stillMinutes)), samples);

        if (fires)
        {
            verdict.Findings.Single().Evidence.Should().Contain(x => x.Label == "Quiet window"
                && x.Value.StartsWith("12 min (3 x this shard's typical page as the advisor saw it, 4 min", StringComparison.Ordinal));
        }
        else
        {
            verdict.Findings.Should().BeEmpty();
        }
    }

    /// <summary>
    /// The H2 re-review: calibrated on the median, not the longest - one slow page among quick ones does not move the
    /// window. Pages of 2, 2 and 4.5 minutes give a six-minute window, where the longest would have given 13.5.
    /// </summary>
    [Fact]
    public void The_window_is_calibrated_on_the_median_page_not_the_longest()
    {
        HealthSample[] samples =
        [
            SampleAt(TimeSpan.FromMinutes(50), 10, 100),
            SampleAt(TimeSpan.FromMinutes(48), 20, 100),
            SampleAt(TimeSpan.FromMinutes(46), 30, 100),
            SampleAt(TimeSpan.FromMinutes(41.5), 40, 100),
            SampleAt(TimeSpan.FromMinutes(8), 100, 150),
            Current(),
        ];

        HealthFinding finding = Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(7)), samples).Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Quiet window"
            && x.Value.StartsWith("6 min (3 x this shard's typical page as the advisor saw it, 2 min", StringComparison.Ordinal));
    }

    /// <summary>
    /// A run longer than the window in force when it ended was a stall, a pause or an outage, not a page: it is dropped,
    /// so a past bad half-hour does not blind D03 for the next one.
    /// </summary>
    [Fact]
    public void A_run_longer_than_the_window_then_in_force_is_not_a_page()
    {
        HealthSample[] samples =
        [
            SampleAt(TimeSpan.FromMinutes(60), 50, 100),
            SampleAt(TimeSpan.FromMinutes(55), 50, 100),
            SampleAt(TimeSpan.FromMinutes(50), 50, 100),
            SampleAt(TimeSpan.FromMinutes(45), 50, 100),
            SampleAt(TimeSpan.FromMinutes(40), 50, 100),
            SampleAt(TimeSpan.FromMinutes(35), 50, 100),
            SampleAt(TimeSpan.FromMinutes(30), 50, 100),
            SampleAt(TimeSpan.FromMinutes(25), 80, 120),
            AheadSixMinutesAgo,
            Current(),
        ];

        HealthFinding finding = Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(7)), samples).Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Quiet window" && x.Value.StartsWith("5 min (max of 5 x", StringComparison.Ordinal),
            "the 35-minute still run was longer than the five-minute window in force, so it was no page");
    }

    /// <summary>The window is never longer than the history can watch whole: 20 minutes, and half the ring's span.</summary>
    [Fact]
    public void The_window_is_capped_at_twenty_minutes_and_half_the_historys_span()
    {
        DaemonThresholds.ShardQuietCeilingFor(TimeSpan.FromMinutes(1)).Should().Be(TimeSpan.FromMinutes(20), "60 samples a minute apart span an hour");
        DaemonThresholds.ShardQuietCeilingFor(TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromMinutes(15));

        DaemonThresholds.ShardQuiet(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(7)).Should().Be(TimeSpan.FromMinutes(20));
        DaemonThresholds.ShardQuiet(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(4)).Should().Be(TimeSpan.FromMinutes(5),
            "a history ten minutes long can watch five minutes whole, not twelve");
        DaemonThresholds.ShardQuiet(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)).Should().Be(TimeSpan.FromMinutes(5));
    }

    /// <summary>A mark that moved a second ago gives a healthy shard no time: the advisor must have seen it ahead for the window.</summary>
    [Fact]
    public void A_mark_the_advisor_has_not_seen_ahead_for_the_window_is_only_watched()
    {
        HealthVerdict verdict = Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(10)), [Current()]);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("only after it has seen the mark ahead of it for 5 min");
    }

    [Fact]
    public void A_mark_that_was_not_ahead_at_the_older_sample_is_quiet()
    {
        Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(10)), [SampleAt(TimeSpan.FromMinutes(6), 100, 100), Current()])
            .Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_shard_that_moved_since_the_older_sample_is_quiet()
    {
        Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(10)), [SampleAt(TimeSpan.FromMinutes(6), 90, 150), Current()])
            .Findings.Should().BeEmpty();
    }

    // ---- The 9.43 rule: what the HighWaterMark row's age means ------------------------------------------------------

    private static HealthVerdict WithAStaleMarkRow(MartenRuntime runtime) => Evaluate(
        Reading(TimeSpan.FromMinutes(30)),
        StillSince(TimeSpan.FromMinutes(30)),
        [SampleAt(TimeSpan.FromMinutes(6), 100, 200), Current()],
        inProcess: DaemonInProcess.NotHosted,
        runtime: runtime);

    /// <summary>From 9.43 the agent restamps its row on every poll (#5541): an old row is a stopped agent - D02's question.</summary>
    [Fact]
    public void From_9_43_an_old_mark_row_means_the_agent_stopped_and_D03_leaves_it_to_D02()
    {
        HealthVerdict verdict = WithAStaleMarkRow(From943);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("from Marten 9.43 a running one does on every poll").And.Contain("D02");
    }

    /// <summary>Below 9.43 the row freezes on an idle store: an old one says nothing, and the check says it cannot tell.</summary>
    [Fact]
    public void Below_9_43_an_old_mark_row_says_nothing_and_the_check_says_it_cannot_tell()
    {
        HealthVerdict verdict = WithAStaleMarkRow(Before943);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("below 9.43").And.Contain("cannot tell");
    }

    /// <summary>
    /// The rule changes what is believed, not only the wording (H2 review): an old mark row beside a fresh fence and a
    /// mark the advisor saw move is a dead agent from 9.43 - the fence and the history are older news from it - and a
    /// live one below, where the fence and the history are all the evidence there is.
    /// </summary>
    [Theory]
    [InlineData("9.42.0", true)]
    [InlineData("9.43.0", false)]
    public void An_old_mark_row_beside_a_fresh_fence_is_believed_alive_only_below_9_43(string marten, bool fires)
    {
        MartenRuntime runtime = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = marten });

        HealthVerdict verdict = Evaluate(
            Reading(TimeSpan.FromMinutes(30), fenceAge: TimeSpan.FromSeconds(3)),
            StillSince(TimeSpan.FromMinutes(10)),
            [SampleAt(TimeSpan.FromMinutes(7), 100, 120), AheadSixMinutesAgo, Current()],
            inProcess: DaemonInProcess.NotHosted,
            runtime: runtime);

        if (fires)
        {
            verdict.Findings.Single().Evidence.Should().Contain(x => x.Label == "Daemon alive because" && x.Value.Contains("allocation fence", StringComparison.Ordinal));
        }
        else
        {
            verdict.Findings.Should().BeEmpty();
            verdict.Note.Should().Contain("has not restamped its mark");
        }
    }

    /// <summary>A recent write of the mark's row is a live agent at both sides of 9.43.</summary>
    [Theory]
    [InlineData("9.42.0")]
    [InlineData("9.43.0")]
    public void A_recently_written_mark_row_is_a_live_agent_at_both_sides(string marten)
    {
        MartenRuntime runtime = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = marten });

        HealthFinding finding = Evaluate(
                Reading(TimeSpan.FromSeconds(5)),
                StillSince(TimeSpan.FromMinutes(10)),
                [AheadSixMinutesAgo, Current()],
                inProcess: DaemonInProcess.NotHosted,
                runtime: runtime)
            .Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Daemon alive because" && x.Value.StartsWith("the high-water agent", StringComparison.Ordinal));
        finding.Caveat.Should().Match(marten == "9.42.0" ? "*Below Marten 9.43*this host runs Marten 9.42.0*" : "*");
    }

    // ---- Not a heartbeat --------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>heartbeat</c> is written at status changes (JasperFx documents it so), and is never read as liveness - K02's
    /// rule since H1-fix. A Running row with a heartbeat days old, on a shard that is otherwise fine, is nobody's finding.
    /// </summary>
    [Fact]
    public void An_old_heartbeat_is_never_read_as_a_stall_or_as_liveness()
    {
        DaemonShardRow row = StillSince(TimeSpan.FromSeconds(10)) with { AgentStatus = "Running", Heartbeat = Now - TimeSpan.FromDays(3) };

        Evaluate(Reading(TimeSpan.FromSeconds(1)), row, [Current()], configuration: DaemonConfiguration(shards: [Shard], extended: true))
            .Findings.Should().BeEmpty();
    }

    // ---- What is somebody else's to report ------------------------------------------------------------------------

    [Fact]
    public void A_paused_shard_is_D05s()
    {
        DaemonShardRow paused = StillSince(TimeSpan.FromMinutes(10)) with { AgentStatus = "Paused" };

        Evaluate(
                Reading(TimeSpan.FromSeconds(1)),
                paused,
                [AheadSixMinutesAgo, Current()],
                configuration: DaemonConfiguration(shards: [Shard], extended: true))
            .Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_shard_being_rebuilt_is_moving_in_its_replay()
    {
        DaemonInProcess rebuilding = HealthyHere with { Rebuilding = [Shard] };

        Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(10)), [AheadSixMinutesAgo, Current()], rebuilding)
            .Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_shard_the_studio_is_rebuilding_is_moving_in_its_replay()
    {
        DaemonInProcess rebuilding = HealthyHere with { StudioRebuilds = ["DailySales"] };

        Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(10)), [AheadSixMinutesAgo, Current()], rebuilding)
            .Findings.Should().BeEmpty();
    }

    /// <summary>A finding names a projection, so it carries what that projection writes: one of a hidden type is dropped.</summary>
    [Fact]
    public void A_finding_carries_the_document_types_its_projection_writes()
    {
        DaemonCheckConfiguration configuration = DaemonConfiguration(shards: [Shard]) with
        {
            PublishedTypes = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase) { ["DailySales"] = [typeof(Uri)] },
        };

        Evaluate(Reading(TimeSpan.FromSeconds(1)), StillSince(TimeSpan.FromMinutes(10)), [AheadSixMinutesAgo, Current()], configuration: configuration)
            .Findings.Single().DocumentTypes.Should().Equal(typeof(Uri));
    }

    [Fact]
    public void It_samples_every_registered_shards_position_and_a_missing_row_as_zero()
    {
        ShardStalledFacts facts = new(
            DaemonConfiguration(shards: ["DailySales:All", "ShipmentTracker:All"]),
            Reading(TimeSpan.Zero),
            [new DaemonShardRow("DailySales:All", 42, Now, WarningThreshold: 10)],
            HealthyHere);

        new ShardStalledCheck().Sample(facts).Should().BeEquivalentTo(new[]
        {
            new HealthSamplePart(DaemonSeries.ShardPosition("DailySales:All"), 42),
            new HealthSamplePart(DaemonSeries.ShardWarningThreshold("DailySales:All"), 10),
            new HealthSamplePart(DaemonSeries.ShardPosition("ShipmentTracker:All"), 0),
        });
    }
}
