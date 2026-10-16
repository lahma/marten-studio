using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using static MartenStudio.Tests.Health.HighWaterPinnedCheckTests;

namespace MartenStudio.Tests.Health;

/// <summary>D04's judgement and the trend arithmetic it shares with the projections screen and the rebuild dialog.</summary>
public class ProjectionFallingBehindCheckTests
{
    private const string Shard = "DailySales:All";

    /// <summary>One sample <paramref name="ago" /> before now; the mark at <paramref name="mark" />, or with the ceiling when unsaid.</summary>
    private static HealthSample At(TimeSpan ago, double position, double highest, double? warning = null, double? mark = null)
    {
        Dictionary<string, double> values = new()
        {
            [DaemonSeries.ShardPosition(Shard)] = position,
            [DaemonSeries.HighestSequence] = highest,
            [DaemonSeries.HighWaterMark] = mark ?? highest,
        };

        if (warning is { } w)
        {
            values[DaemonSeries.ShardWarningThreshold(Shard)] = w;
        }

        return new HealthSample(Now - ago, values);
    }

    /// <summary>A lag that grows from 2,000 to 3,800 over four minutes while the shard moves and the mark moves.</summary>
    private static readonly HealthSample[] Growing =
    [
        At(TimeSpan.FromMinutes(4), 1_000, 3_000),
        At(TimeSpan.FromMinutes(2), 1_100, 4_000),
        At(TimeSpan.Zero, 1_200, 5_000),
    ];

    private static HealthVerdict Evaluate(params HealthSample[] samples) => EvaluateWith(DaemonInProcess.NotHosted, null, samples);

    private static HealthVerdict EvaluateWith(DaemonInProcess inProcess, DaemonCheckConfiguration? configuration, params HealthSample[] samples) =>
        new ProjectionFallingBehindCheck().Evaluate(
            new ProjectionFallingBehindFacts(configuration ?? DaemonConfiguration(shards: [Shard]), inProcess),
            HealthEvaluation.At(Now, history: new HealthHistoryView(samples)));

    [Fact]
    public void A_lag_past_the_threshold_and_still_growing_is_a_warning()
    {
        HealthFinding finding = Evaluate(Growing).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D04");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be(Shard);
        finding.Title.Should().Contain("3,800 events behind");
        finding.Evidence.Should().Contain(x => x.Label == "Warning threshold" && x.Value.StartsWith("1,000 (the studio's default", StringComparison.Ordinal));
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    /// <summary>High and draining is not a finding: it is the ETA the status row and the screen show.</summary>
    [Fact]
    public void A_high_lag_that_is_draining_is_quiet_and_says_when_it_catches_up()
    {
        HealthVerdict verdict = Evaluate(
            At(TimeSpan.FromMinutes(2), 1_000, 7_000),
            At(TimeSpan.Zero, 4_000, 7_000));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(Shard + ": draining at 25 ev/s, caught up in ~2 min");
    }

    /// <summary>Grew, and is now shrinking: not "still growing".</summary>
    [Fact]
    public void A_lag_that_grew_and_turned_is_not_still_growing()
    {
        Evaluate(
                At(TimeSpan.FromMinutes(4), 1_000, 3_000),
                At(TimeSpan.FromMinutes(2), 1_000, 6_000),
                At(TimeSpan.Zero, 2_500, 6_200))
            .Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_growing_lag_under_the_threshold_is_quiet()
    {
        Evaluate(At(TimeSpan.FromMinutes(2), 100, 300), At(TimeSpan.Zero, 100, 900)).Findings.Should().BeEmpty();
    }

    /// <summary>A row's own threshold wins - Marten writes none (#5173), a host may.</summary>
    [Fact]
    public void A_rows_own_warning_threshold_wins()
    {
        HealthFinding finding = Evaluate(
                At(TimeSpan.FromMinutes(2), 100, 300, warning: 50),
                At(TimeSpan.Zero, 150, 900, warning: 50))
            .Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Warning threshold" && x.Value == "50 (warning_behind_threshold)");
    }

    // ---- The stand-downs: a growing lag that is somebody else's finding (H2 review) -----------------------------

    /// <summary>The studio's own coordinator pause grows every lag, by design: not one shard is "falling behind".</summary>
    [Fact]
    public void Nothing_falls_behind_while_the_studio_has_paused_the_coordinator()
    {
        DaemonInProcess paused = DaemonInProcess.NotHosted with
        {
            PausedByStudio = new StudioDaemonPause("default", "operator", Now, "localhost.marten", null),
        };

        HealthVerdict verdict = EvaluateWith(paused, null, Growing);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(Shard + ": not judged: Marten Studio paused the projection coordinator");
    }

    /// <summary>A rebuild starts the shard again from zero: its lag jumps, and that is the rebuild, not a slow shard.</summary>
    [Fact]
    public void A_shard_known_to_be_rebuilding_is_not_judged()
    {
        DaemonInProcess rebuilding = DaemonInProcess.NotHosted with { StudioRebuilds = ["DailySales"] };

        EvaluateWith(rebuilding, null, Growing).Findings.Should().BeEmpty();
    }

    /// <summary>A rebuild nobody told the studio about drops the position: a shard that went backwards has not moved forward.</summary>
    [Fact]
    public void A_position_that_fell_back_to_the_start_is_not_judged()
    {
        HealthVerdict verdict = Evaluate(
            At(TimeSpan.FromMinutes(2), 4_000, 5_000),
            At(TimeSpan.Zero, 0, 5_500));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("not judged: the shard has not moved while the advisor watched");
    }

    [Fact]
    public void A_shard_whose_agent_here_is_not_running_is_D05s()
    {
        DaemonInProcess paused = DaemonInProcess.NotHosted with
        {
            Hosting = DaemonHostingState.Hosted,
            IsRunning = true,
            Agents = [new DaemonAgentFact(Shard, "Paused", "ApplyEvent", "boom", Now)],
        };

        HealthVerdict verdict = EvaluateWith(paused, null, Growing);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("its agent here is Paused (D05)");
    }

    /// <summary>A shard that stood still while events arrived is not slow: it is stalled (D03) or nothing runs it (D02).</summary>
    [Fact]
    public void A_shard_that_did_not_move_is_not_falling_behind()
    {
        HealthVerdict verdict = Evaluate(
            At(TimeSpan.FromMinutes(4), 1_000, 3_000),
            At(TimeSpan.Zero, 1_000, 5_000));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("which is about whether it runs (D02, D03), not its speed");
    }

    /// <summary>A mark held behind a gap grows the ceiling's lag with nothing reaching any shard: that is D01.</summary>
    [Fact]
    public void A_lag_that_grew_while_the_mark_stood_still_is_D01s()
    {
        HealthVerdict verdict = Evaluate(
            At(TimeSpan.FromMinutes(4), 1_000, 3_000, mark: 1_100),
            At(TimeSpan.Zero, 1_100, 5_000, mark: 1_100));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("the high-water mark has not moved").And.Contain("D01");
    }

    /// <summary>A finding names a projection, so it carries what it writes; a hidden one is not even named in a note.</summary>
    [Fact]
    public void A_projection_of_a_hidden_type_is_carried_by_type_and_left_out_of_the_notes()
    {
        DaemonCheckConfiguration hidden = DaemonConfiguration(shards: [Shard]) with
        {
            PublishedTypes = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase) { ["DailySales"] = [typeof(Uri)] },
            HiddenProjections = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DailySales" },
        };

        EvaluateWith(DaemonInProcess.NotHosted, hidden, Growing).Findings.Single().DocumentTypes.Should().Equal(typeof(Uri));

        HealthVerdict draining = EvaluateWith(DaemonInProcess.NotHosted, hidden, At(TimeSpan.FromMinutes(2), 1_000, 7_000), At(TimeSpan.Zero, 4_000, 7_000));
        draining.Findings.Should().BeEmpty();
        (draining.Note ?? string.Empty).Should().NotContain("DailySales");
    }

    /// <summary>Two samples a minute apart is the least the advisor believes.</summary>
    [Fact]
    public void Samples_under_a_minute_apart_are_no_trend()
    {
        Evaluate(At(TimeSpan.FromSeconds(30), 1_000, 3_000), At(TimeSpan.Zero, 1_000, 9_000)).Findings.Should().BeEmpty();
    }

    /// <summary>A sample from an older sweep is not "now": a sweep in which D01 or D03 could not read says nothing.</summary>
    [Fact]
    public void No_sample_from_this_sweep_is_no_judgement()
    {
        HealthVerdict verdict = Evaluate(At(TimeSpan.FromMinutes(4), 1_000, 3_000), At(TimeSpan.FromMinutes(2), 1_000, 9_000));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("No shard positions were read in this sweep");
    }

    // ---- ProjectionTrend --------------------------------------------------------------------------------------

    [Fact]
    public void The_trend_is_measured_over_the_window_not_the_last_step()
    {
        ShardTrend trend = ProjectionTrend.For(
            new HealthHistoryView([At(TimeSpan.FromMinutes(5), 0, 6_000), At(TimeSpan.FromMinutes(1), 3_000, 6_000), At(TimeSpan.Zero, 3_000, 6_000)]),
            Shard);

        trend.Direction.Should().Be(LagDirection.Draining);
        trend.ProcessedPerSecond.Should().BeApproximately(10, 0.001, "3,000 events over five minutes, whatever the last minute did");
        trend.Lag.Should().Be(3_000);
    }

    [Fact]
    public void A_caught_up_shard_is_caught_up_with_one_sample()
    {
        ProjectionTrend.For(new HealthHistoryView([At(TimeSpan.Zero, 500, 500)]), Shard).Direction.Should().Be(LagDirection.CaughtUp);
    }

    [Fact]
    public void No_samples_is_unknown_and_says_so()
    {
        ShardTrend trend = ProjectionTrend.For(HealthHistoryView.Empty, Shard);

        trend.Direction.Should().Be(LagDirection.Unknown);
        trend.Lag.Should().BeNull();
        trend.Describe().Should().Contain("no trend yet");
    }

    [Fact]
    public void The_rebuild_estimate_divides_by_the_measured_throughput_and_says_what_it_measured()
    {
        HealthHistoryView history = new([At(TimeSpan.FromMinutes(5), 0, 6_000), At(TimeSpan.Zero, 3_000, 6_000)]);

        double? throughput = ProjectionTrend.Throughput(history, [Shard]);
        throughput.Should().BeApproximately(10, 0.001);

        ProjectionTrend.EstimateRebuild(36_000, throughput, history).Should().StartWith("About 1 h 0 min, at the 10 ev/s");
    }

    /// <summary>D03's calibration: the longest still run with work that ended in a move; a gap in the watching measures nothing.</summary>
    [Fact]
    public void The_longest_page_is_a_still_run_with_work_that_ended_in_a_move()
    {
        HealthHistoryView history = new(
        [
            At(TimeSpan.FromMinutes(30), 10, 100),
            At(TimeSpan.FromMinutes(27), 10, 100),
            At(TimeSpan.FromMinutes(26), 60, 100),
            At(TimeSpan.FromMinutes(12), 60, 100),
            At(TimeSpan.FromMinutes(1), 90, 100),
            At(TimeSpan.Zero, 90, 100),
        ]);

        ProjectionTrend.LongestPage(history, Shard).Should().Be(TimeSpan.FromMinutes(4),
            "30 to 26 minutes ago is a page; 26 to 1 crosses a fourteen-minute and an eleven-minute gap, which nobody watched");
    }

    [Fact]
    public void A_shard_that_was_caught_up_while_still_took_no_page()
    {
        ProjectionTrend.LongestPage(new HealthHistoryView([At(TimeSpan.FromMinutes(4), 100, 100), At(TimeSpan.Zero, 200, 200)]), Shard)
            .Should().BeNull("still with nothing to do is idle, not a page");
    }

    [Fact]
    public void Before_two_samples_the_rebuild_estimate_is_an_honest_no()
    {
        ProjectionTrend.EstimateRebuild(36_000, null, HealthHistoryView.Empty).Should().StartWith("No estimate yet");
        ProjectionTrend.EstimateRebuild(0, null, HealthHistoryView.Empty).Should().Be("Nothing to replay.");
    }
}
