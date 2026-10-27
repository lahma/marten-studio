using System.Globalization;

using MartenStudio.Services.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>M2's chart geometry: where the lines go, where they break, and when a threshold is drawn at all.</summary>
public class LagChartTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(30);

    private static LagPoint At(int seconds, long position, long highWater) => new(Start + TimeSpan.FromSeconds(seconds), position, highWater);

    /// <summary>A polyline's points, parsed back.</summary>
    private static (double X, double Y)[] Parse(string points) =>
    [
        .. points.Split(' ').Select(static pair =>
        {
            string[] xy = pair.Split(',');
            return (double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture));
        }),
    ];

    [Fact]
    public void No_samples_draw_nothing()
    {
        LagChart.Draw([], MaxStep, 1_000, 100_000).Should().Be(LagChartFigure.Empty);
    }

    [Fact]
    public void Time_runs_across_and_the_sequence_up_within_the_box()
    {
        LagChartFigure figure = LagChart.Draw([At(0, 0, 100), At(10, 50, 100), At(20, 100, 100)], MaxStep, 1_000, 100_000);

        figure.Bottom.Should().Be(0);
        figure.Top.Should().Be(100);

        (double X, double Y)[] position = Parse(figure.Position.Should().ContainSingle().Subject);
        position.Should().HaveCount(3);
        position[0].X.Should().Be(2, "the first sample sits at the left margin");
        position[^1].X.Should().Be(LagChart.Width - 2, "the latest at the right margin");
        position[0].Y.Should().Be(LagChart.Height - 2, "the lowest value sits on the bottom margin");
        position[^1].Y.Should().Be(2, "the highest on the top margin");
        position[1].Y.Should().Be(LagChart.Height / 2.0);

        Parse(figure.HighWater.Should().ContainSingle().Subject).Should().OnlyContain(static x => x.Y == 2);
    }

    [Fact]
    public void A_single_sample_is_drawn_as_a_dot()
    {
        LagChartFigure figure = LagChart.Draw([At(0, 40, 40)], MaxStep, 1_000, 100_000);

        (double X, double Y)[] dot = Parse(figure.Position.Single());
        dot.Should().HaveCount(2, "a polyline of one point draws nothing; the same point twice, with a round cap, is a dot");
        dot[0].Should().Be(dot[1]);
        dot[0].Y.Should().Be(LagChart.Height / 2.0, "a flat chart is drawn across its middle");
    }

    [Fact]
    public void A_gap_breaks_both_lines_and_a_reset_breaks_the_position_only()
    {
        LagPoint[] points =
        [
            At(0, 100, 200), At(5, 200, 300),
            At(65, 300, 400), At(70, 400, 500),
            At(75, 0, 500), At(80, 100, 500),
        ];

        LagChartFigure figure = LagChart.Draw(points, MaxStep, 1_000, 100_000);

        figure.HighWater.Should().HaveCount(2, "nobody watched between 5 s and 65 s");
        figure.Position.Should().HaveCount(3, "and the position went back to zero at 75 s");
    }

    [Fact]
    public void Each_step_standing_still_while_the_high_water_rose_is_a_stalled_segment()
    {
        LagChartFigure figure = LagChart.Draw(
            [At(0, 100, 100), At(5, 100, 300), At(10, 100, 500), At(15, 400, 500), At(20, 400, 500)],
            MaxStep,
            1_000,
            100_000);

        figure.Stalled.Should().HaveCount(2, "0-5 and 5-10; 15-20 stood still with nothing new arriving");
        figure.Stalled.Should().OnlyContain(static x => x.Split(' ').Length == 2);
    }

    /// <summary>
    /// A threshold line is the high water minus it, drawn once the lag has come within half of it - a chart scaled to a
    /// 100,000 line for a shard 50 behind would show nothing else.
    /// </summary>
    [Fact]
    public void A_threshold_is_drawn_once_the_lag_comes_within_half_of_it()
    {
        LagPoint[] near = [At(0, 0, 400), At(5, 100, 700)];

        LagChartFigure figure = LagChart.Draw(near, MaxStep, warning: 1_000, critical: 100_000);
        figure.Warning.Should().ContainSingle("the lag reached 600, more than half of 1,000");
        figure.Critical.Should().BeEmpty();
        figure.Bottom.Should().Be(0, "the line is never drawn below zero");

        LagChart.Draw([At(0, 0, 400)], MaxStep, warning: 1_000, critical: 100_000).Warning.Should().BeEmpty("400 is not within half of 1,000");
        LagChart.Draw([At(0, 0, 60_000)], MaxStep, warning: 1_000, critical: 100_000).Critical.Should().ContainSingle();
    }

    [Fact]
    public void A_threshold_line_follows_the_high_water()
    {
        LagChartFigure figure = LagChart.Draw([At(0, 5_000, 10_000), At(5, 6_000, 11_000)], MaxStep, warning: 2_000, critical: 100_000);

        figure.Bottom.Should().Be(5_000);
        (double X, double Y)[] warning = Parse(figure.Warning.Single());
        (double X, double Y)[] highWater = Parse(figure.HighWater.Single());

        warning.Zip(highWater).Should().OnlyContain(static x => x.First.Y > x.Second.Y, "below the high water, by the threshold");
    }

    [Fact]
    public void Coordinates_are_written_in_the_invariant_culture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");

            // 1 of 7 is a fraction of the height: 32.9, which Finnish would write 32,9.
            LagChartFigure figure = LagChart.Draw([At(0, 0, 7), At(7, 1, 7)], MaxStep, 1_000, 100_000);

            figure.Position.Single().Should().Be("2,38 158,32.9");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_shards_history_draws_with_its_own_thresholds()
    {
        ShardLagHistory history = ShardLagHistory.Build(
            "Orders:All",
            [At(0, 0, 40), At(5, 10, 60)],
            new ShardLagContext(TimeSpan.FromSeconds(5), WarningThreshold: 80));

        LagChart.Draw(history).Warning.Should().ContainSingle("a lag of 50 is within half of the row's own 80");
    }
}
