using MartenStudio.Services.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// M2's arithmetic at its edges: the arrow, the stall and a running rebuild's estimate over the projections screen's own
/// samples - no samples, a flat line, a saw, a reset, a rebuild restarted, and a gap in the sequence and in the watching.
/// </summary>
public class ShardLagAnalysisTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The default spacing: five seconds, so a run breaks at a step of more than thirty.</summary>
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MaxStep = new ShardLagContext(Spacing).MaxStep;

    /// <summary>Samples five seconds apart: (position, high water) each.</summary>
    private static LagPoint[] Every5s(params (long Position, long HighWater)[] samples) =>
        [.. samples.Select(static (x, i) => new LagPoint(Start + TimeSpan.FromSeconds(5 * i), x.Position, x.HighWater))];

    private static LagPoint At(int seconds, long position, long highWater) => new(Start + TimeSpan.FromSeconds(seconds), position, highWater);

    // ---- No samples ----------------------------------------------------------------------------------------------------

    [Fact]
    public void With_no_samples_there_is_no_trend_no_stall_and_no_estimate()
    {
        ShardLagAnalysis.TrailingRun([], MaxStep).Should().BeEmpty();
        ShardLagAnalysis.Trend([]).Should().Be(LagTrend.None);
        ShardLagAnalysis.Stall([]).Should().BeNull();

        RebuildEta eta = ShardLagAnalysis.Eta([]);
        eta.HasEnoughSamples.Should().BeFalse();
        eta.ProgressSamples.Should().Be(0);
        eta.Remaining.Should().BeNull("never a guess");

        ShardLagHistory history = ShardLagHistory.Build("Orders:All", [], new ShardLagContext(Spacing, Rebuilding: true));
        history.HasTrend.Should().BeFalse("the page falls back to the advisor's arrow");
        history.Arrow.Should().BeEmpty();
        history.RebuildSentence().Should().Be("Rebuild: not enough samples yet - 0 of the 3 showing progress an estimate needs.");
    }

    [Fact]
    public void Fewer_samples_than_a_trend_needs_say_how_many_more()
    {
        LagTrend trend = ShardLagAnalysis.Trend(Every5s((100, 600), (200, 700), (300, 800)));

        trend.Direction.Should().Be(LagDirection.Unknown);
        trend.Samples.Should().Be(3);
        trend.LagPerSecond.Should().BeNull();

        ShardLagHistory.Build("Orders:All", Every5s((100, 600), (200, 700), (300, 800)), new ShardLagContext(Spacing))
            .Summary().Should().Be("500 behind; no trend yet: 3 of the 4 samples a trend needs.");
    }

    // ---- A flat line ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_flat_line_at_the_high_water_is_caught_up()
    {
        LagPoint[] flat = Every5s((500, 500), (500, 500), (500, 500), (500, 500), (500, 500));

        ShardLagAnalysis.Trend(flat).Direction.Should().Be(LagDirection.CaughtUp);
        ShardLagAnalysis.Stall(flat).Should().BeNull("there was nothing to process");

        ShardLagHistory history = ShardLagHistory.Build("Orders:All", flat, new ShardLagContext(Spacing));
        history.Arrow.Should().Be("✓");
        history.Summary().Should().Be("Caught up.");
    }

    /// <summary>Behind, and neither the shard nor the high water moving: nothing arrived, so it is steady, not stalled.</summary>
    [Fact]
    public void A_flat_line_behind_a_high_water_that_does_not_move_is_steady_and_not_stalled()
    {
        LagPoint[] flat = Every5s((100, 600), (100, 600), (100, 600), (100, 600), (100, 600), (100, 600));

        LagTrend trend = ShardLagAnalysis.Trend(flat);
        trend.Direction.Should().Be(LagDirection.Steady);
        trend.LagPerSecond.Should().Be(0);

        ShardLagAnalysis.Stall(flat).Should().BeNull("the high water did not rise while it stood still");
        ShardLagAnalysis.IsStalledStep(flat[0], flat[1]).Should().BeFalse();
    }

    /// <summary>
    /// A shard that keeps up commits a page at a time, so its lag is a saw: rising between pages and dropping at each one.
    /// Two of its steps rising in a row is not falling behind.
    /// </summary>
    [Fact]
    public void A_saw_from_page_at_a_time_commits_is_steady()
    {
        LagPoint[] saw = Every5s((0, 100), (0, 500), (500, 600), (500, 1_000), (1_000, 1_100), (1_000, 1_500), (1_500, 1_600), (1_500, 2_000));

        ShardLagAnalysis.Trend(saw).Direction.Should().Be(LagDirection.Steady);
    }

    // ---- Falling behind, catching up, stalled ----------------------------------------------------------------------

    [Fact]
    public void A_lag_that_grows_while_the_shard_moves_is_falling_behind()
    {
        // Processing 20 ev/s, 40 arriving: the lag grows by 20 ev/s.
        LagPoint[] behind = Every5s((0, 200), (100, 400), (200, 600), (300, 800), (400, 1_000), (500, 1_200));

        LagTrend trend = ShardLagAnalysis.Trend(behind);

        trend.Direction.Should().Be(LagDirection.Growing);
        trend.LagPerSecond.Should().BeApproximately(20, 0.001);
        trend.ProcessedPerSecond.Should().BeApproximately(20, 0.001);
        trend.AppendedPerSecond.Should().BeApproximately(40, 0.001);
        trend.Watched.Should().Be(TimeSpan.FromSeconds(25));
        ShardLagAnalysis.Stall(behind).Should().BeNull("it moved at every step");

        ShardLagHistory history = ShardLagHistory.Build("Orders:All", behind, new ShardLagContext(Spacing));
        history.Arrow.Should().Be("↑");
        history.Summary().Should().Be("700 behind, falling behind by 20 ev/s over the last 25 s.");
    }

    [Fact]
    public void A_lag_that_shrinks_is_catching_up_with_a_time_to_catch_up()
    {
        // Nothing arriving, 100 processed every 5 s: 20 ev/s, 400 left.
        LagPoint[] draining = Every5s((0, 900), (100, 900), (200, 900), (300, 900), (400, 900), (500, 900));

        LagTrend trend = ShardLagAnalysis.Trend(draining);

        trend.Direction.Should().Be(LagDirection.Draining);
        trend.LagPerSecond.Should().BeApproximately(-20, 0.001);
        trend.CaughtUpIn.Should().Be(TimeSpan.FromSeconds(20));

        ShardLagHistory.Build("Orders:All", draining, new ShardLagContext(Spacing)).Arrow.Should().Be("↓");
    }

    /// <summary>A lag that grew and has started to shrink is no longer falling behind, whatever the line through all of it says.</summary>
    [Fact]
    public void A_lag_that_rose_and_is_now_falling_is_not_falling_behind()
    {
        LagPoint[] turned = Every5s((0, 100), (0, 400), (0, 700), (0, 1_000), (400, 1_000), (700, 1_000), (900, 1_000), (950, 1_000));

        ShardLagAnalysis.Trend(turned).Direction.Should().NotBe(LagDirection.Growing);
    }

    [Fact]
    public void A_position_standing_still_while_the_high_water_rises_is_stalled_and_falling_behind()
    {
        LagPoint[] stalled = Every5s((300, 400), (400, 500), (400, 700), (400, 900), (400, 1_100), (400, 1_300));

        LagStall stall = ShardLagAnalysis.Stall(stalled)!;
        stall.Samples.Should().Be(5);
        stall.For.Should().Be(TimeSpan.FromSeconds(20));
        stall.HighWaterRose.Should().Be(800);

        ShardLagAnalysis.IsStalledStep(stalled[1], stalled[2]).Should().BeTrue();
        ShardLagAnalysis.IsStalledStep(stalled[0], stalled[1]).Should().BeFalse("it moved");

        ShardLagHistory history = ShardLagHistory.Build("Orders:All", stalled, new ShardLagContext(Spacing));
        history.Trend.Direction.Should().Be(LagDirection.Growing, "a stall is not a reason to hide the arrow - the chart says why");
        history.WithheldBecause.Should().BeNull();
        history.StallSentence().Should().Be("Stalled: not moved for 20 s while the high water rose by 800.");
    }

    [Fact]
    public void Two_samples_standing_still_are_not_yet_a_stall()
    {
        ShardLagAnalysis.Stall(Every5s((100, 200), (300, 400), (300, 600))).Should().BeNull();
        ShardLagAnalysis.Stall(Every5s((300, 400), (300, 600), (300, 800))).Should().NotBeNull();
    }

    // ---- A reset, and a rebuild restarted ---------------------------------------------------------------------------

    /// <summary>A position that goes backwards starts the shard again: nothing before it is in the run, or in the trend.</summary>
    [Fact]
    public void A_reset_starts_a_new_run()
    {
        LagPoint[] reset = Every5s((900, 1_000), (950, 1_000), (1_000, 1_000), (0, 1_000), (200, 1_000), (400, 1_000), (600, 1_000));

        IReadOnlyList<LagPoint> run = ShardLagAnalysis.TrailingRun(reset, MaxStep);

        run.Should().HaveCount(4);
        run[0].Position.Should().Be(0);
        ShardLagAnalysis.IsReset(reset[2], reset[3]).Should().BeTrue();

        LagTrend trend = ShardLagAnalysis.Trend(run);
        trend.Direction.Should().Be(LagDirection.Draining, "the replay since the reset is catching up");
        trend.ProcessedPerSecond.Should().BeApproximately(40, 0.001, "the rate is the new run's alone");
    }

    [Fact]
    public void A_rebuilds_estimate_counts_only_samples_since_it_restarted()
    {
        // First attempt: to 600, then started again from 0 (a reset), now moving 100 every 5 s.
        LagPoint[] restarted = Every5s((0, 2_000), (300, 2_000), (600, 2_000), (0, 2_000), (100, 2_000), (200, 2_000), (300, 2_000));

        RebuildEta eta = ShardLagAnalysis.Eta(ShardLagAnalysis.TrailingRun(restarted, MaxStep));

        eta.HasEnoughSamples.Should().BeTrue();
        eta.ProgressSamples.Should().Be(3);
        eta.PerSecond.Should().BeApproximately(20, 0.001, "300 over 15 s since the restart, not the first attempt's 60 ev/s");
        eta.Left.Should().Be(1_700);
        eta.Remaining.Should().Be(TimeSpan.FromSeconds(85));
        eta.Window.Should().Be(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// A rebuild Marten Studio started is known by its start, which cuts the run even where no reset shows - a shard that
    /// was low already.
    /// </summary>
    [Fact]
    public void A_rebuild_started_by_the_studio_counts_no_sample_from_before_it_started()
    {
        LagPoint[] points = Every5s((0, 1_000), (0, 1_000), (0, 1_000), (100, 1_000), (200, 1_000), (300, 1_000));
        DateTimeOffset startedAt = Start + TimeSpan.FromSeconds(12);

        IReadOnlyList<LagPoint> run = ShardLagAnalysis.TrailingRun(points, MaxStep, startedAt);

        run.Should().HaveCount(3);
        ShardLagAnalysis.Eta(run).HasEnoughSamples.Should().BeFalse("two steps forward since it started, not three");

        ShardLagAnalysis.TrailingRun(points, MaxStep, Start + TimeSpan.FromMinutes(1)).Should().BeEmpty(
            "no sample yet since a rebuild that started after the last one");
    }

    // ---- The estimate never guesses -------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void A_rebuild_with_fewer_than_three_samples_showing_progress_has_no_estimate(int samples, int progress)
    {
        LagPoint[] points = [.. Enumerable.Range(0, samples).Select(i => new LagPoint(Start + TimeSpan.FromSeconds(5 * i), 100L * (i + 1), 5_000))];

        RebuildEta eta = ShardLagAnalysis.Eta(points);

        eta.ProgressSamples.Should().Be(progress);
        eta.HasEnoughSamples.Should().BeFalse();
        eta.Remaining.Should().BeNull();

        ShardLagHistory history = ShardLagHistory.Build("Orders:All", points, new ShardLagContext(Spacing, Rebuilding: true));
        history.RebuildSentence().Should().Be(
            "Rebuild: not enough samples yet - " + progress + " of the 3 showing progress an estimate needs.");
    }

    [Fact]
    public void Three_samples_showing_progress_give_an_estimate_with_its_window()
    {
        LagPoint[] points = Every5s((100, 5_000), (200, 5_000), (300, 5_000), (400, 5_000));

        ShardLagHistory history = ShardLagHistory.Build("Orders:All", points, new ShardLagContext(Spacing, Rebuilding: true));

        history.Rebuild!.HasEnoughSamples.Should().BeTrue();
        history.Rebuild.Remaining.Should().Be(TimeSpan.FromSeconds(4_600 / 20.0));
        history.RebuildSentence().Should().Be("Rebuild: about 3 min left - 4,600 events at 20 ev/s, from the last 15 s.");
    }

    [Fact]
    public void A_rebuild_that_has_stopped_moving_has_no_estimate()
    {
        LagPoint[] points = Every5s((100, 5_000), (200, 5_000), (300, 5_000), (400, 5_000), (400, 5_000), (400, 5_000));

        RebuildEta eta = ShardLagAnalysis.Eta(points);

        eta.HasEnoughSamples.Should().BeTrue();
        eta.Remaining.Should().BeNull("a rate from before it stopped is not its speed now");
        eta.StillFor.Should().Be(3);

        ShardLagHistory.Build("Orders:All", points, new ShardLagContext(Spacing, Rebuilding: true))
            .RebuildSentence().Should().Be("Rebuild: no estimate - the position has not moved over the last 3 samples.");
    }

    [Fact]
    public void A_rebuild_that_caught_up_says_so()
    {
        LagPoint[] points = Every5s((100, 400), (200, 400), (300, 400), (400, 400));

        ShardLagHistory.Build("Orders:All", points, new ShardLagContext(Spacing, Rebuilding: true))
            .RebuildSentence().Should().Be("Rebuild: caught up with the high water.");
    }

    [Fact]
    public void The_estimate_reads_only_the_last_five_minutes()
    {
        // Slow for ten minutes, then fast for the last four: the speed now is the fast one.
        List<LagPoint> points = [];
        for (int i = 0; i <= 20; i++)
        {
            points.Add(At(30 * i, 10L * i, 100_000));
        }

        for (int i = 1; i <= 8; i++)
        {
            points.Add(At(600 + (30 * i), 200 + (3_000L * i), 100_000));
        }

        RebuildEta eta = ShardLagAnalysis.Eta(ShardLagAnalysis.TrailingRun(points, TimeSpan.FromMinutes(1)));

        eta.Window.Should().BeLessThanOrEqualTo(ShardLagAnalysis.EtaWindow);
        eta.PerSecond.Should().BeGreaterThan(50, "the old crawl is outside the window");
    }

    // ---- Gaps: in the sequence, and in the watching --------------------------------------------------------------

    /// <summary>
    /// The high water jumps over a range of numbers no event holds, and the shard follows it at its next page: the lag spikes
    /// for one sample. That is not falling behind, and it is not a stall the caption names.
    /// </summary>
    [Fact]
    public void A_sequence_gap_the_shard_follows_is_not_falling_behind()
    {
        LagPoint[] gap = Every5s((900, 1_000), (1_000, 1_000), (1_000, 101_000), (101_000, 101_100), (101_100, 101_200), (101_200, 101_300));

        ShardLagAnalysis.Trend(gap).Direction.Should().NotBe(LagDirection.Growing);
        ShardLagAnalysis.Stall(gap).Should().BeNull();
        ShardLagAnalysis.TrailingRun(gap, MaxStep).Should().HaveCount(gap.Length, "a jump forward is not a reset");
    }

    [Fact]
    public void A_sequence_gap_during_a_rebuild_gives_a_finite_estimate_that_counts_it_as_progress()
    {
        LagPoint[] gap = Every5s((100, 200_000), (200, 200_000), (100_200, 200_000), (100_300, 200_000));

        RebuildEta eta = ShardLagAnalysis.Eta(gap);

        eta.HasEnoughSamples.Should().BeTrue();
        eta.Remaining.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThan(TimeSpan.FromMinutes(1));
        eta.Left.Should().Be(99_700);
    }

    /// <summary>Nobody watched for a minute - a hidden tab, a closed page: the run, the trend and the estimate start after it.</summary>
    [Fact]
    public void A_gap_in_the_watching_breaks_the_run()
    {
        LagPoint[] points =
        [
            At(0, 0, 1_000), At(5, 100, 1_000), At(10, 200, 1_000), At(15, 300, 1_000),
            At(75, 600, 1_000), At(80, 700, 1_000), At(85, 800, 1_000),
        ];

        ShardLagAnalysis.IsGap(points[3], points[4], MaxStep).Should().BeTrue();
        IReadOnlyList<LagPoint> run = ShardLagAnalysis.TrailingRun(points, MaxStep);
        run.Should().HaveCount(3);

        ShardLagAnalysis.Trend(run).Direction.Should().Be(LagDirection.Unknown, "three samples since the gap");
        ShardLagAnalysis.Eta(run).ProgressSamples.Should().Be(2);

        ShardLagAnalysis.Runs(points, MaxStep, atResets: false).Should().HaveCount(2);
    }

    // ---- What is withheld ------------------------------------------------------------------------------------------

    [Fact]
    public void The_arrow_is_withheld_for_the_studios_own_pause_and_an_agent_that_is_not_running_and_never_for_a_rebuild()
    {
        LagPoint[] growing = Every5s((100, 200), (100, 400), (100, 600), (100, 800));

        ShardLagHistory paused = ShardLagHistory.Build("Orders:All", growing, new ShardLagContext(Spacing, PausedByStudio: true));
        paused.Arrow.Should().BeEmpty();
        paused.HasTrend.Should().BeTrue("the page says why rather than falling back");
        paused.Summary().Should().Be(
            "700 behind; not judged: Marten Studio paused the projection coordinator, and every lag grows until it is resumed.");

        ShardLagHistory.Build("Orders:All", growing, new ShardLagContext(Spacing, AgentStatusHere: "Paused"))
            .WithheldBecause.Should().Be("not judged: its agent here is Paused (D05)");
        ShardLagHistory.Build("Orders:All", growing, new ShardLagContext(Spacing, AgentStatusHere: "Running"))
            .WithheldBecause.Should().BeNull();
        ShardLagHistory.Build("Orders:All", growing, new ShardLagContext(Spacing, Rebuilding: true, PausedByStudio: true, AgentStatusHere: "Stopped"))
            .WithheldBecause.Should().BeNull("catching up is what a rebuild does, and its estimate is the point");
    }

    // ---- Thresholds ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Thresholds_are_said_only_where_the_host_wrote_them()
    {
        LagPoint[] points = Every5s((100, 200));

        ShardLagHistory.Build("Orders:All", points, new ShardLagContext(Spacing)).ThresholdSentence().Should().BeNull();

        ShardLagHistory both = ShardLagHistory.Build("Orders:All", points, new ShardLagContext(Spacing, WarningThreshold: 50, CriticalThreshold: 5_000));
        both.EffectiveWarning.Should().Be(50);
        both.ThresholdSentence().Should().Be(
            "Thresholds: warning above 50 (its progression row's), critical above 5,000 (its progression row's).");

        ShardLagHistory.Build("Orders:All", points, new ShardLagContext(Spacing, WarningThreshold: 50))
            .ThresholdSentence().Should().Be("Thresholds: warning above 50 (its progression row's), critical above 100,000 (the studio's).");
    }

    [Fact]
    public void The_chart_window_is_the_history_capacity_at_its_spacing()
    {
        ShardLagContext context = new(Spacing);

        context.DisplayWindow.Should().Be(TimeSpan.FromMinutes(10));
        context.MaxStep.Should().Be(TimeSpan.FromSeconds(30), "four of the spacing, at least half a minute");
        new ShardLagContext(TimeSpan.FromMinutes(1)).MaxStep.Should().Be(TimeSpan.FromMinutes(4));

        // A sample older than the window is not drawn, nor read.
        LagPoint[] points = [At(-3_600, 0, 10), .. Every5s((10, 10), (10, 10))];
        ShardLagHistory.Build("Orders:All", points, context).Points.Should().HaveCount(2);
    }
}
