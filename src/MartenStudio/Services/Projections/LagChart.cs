using System.Globalization;
using System.Text;

namespace MartenStudio.Services.Projections;

/// <summary>
/// What the projections screen's lag chart draws, already in the chart's own coordinates: one SVG polyline's
/// <c>points</c> per line, so the component that renders it only writes attributes.
/// </summary>
/// <param name="HighWater">The high water, one line per run of samples with no gap in it.</param>
/// <param name="Position">The shard's position, one line per run - cut at gaps and at resets.</param>
/// <param name="Stalled">The steps in which the position stood still while the high water rose, each a two-point line.</param>
/// <param name="Warning">The warning threshold as a line - the high water minus it - or none when the lag never came near it.</param>
/// <param name="Critical">The critical threshold likewise.</param>
/// <param name="Bottom">The sequence at the chart's bottom edge.</param>
/// <param name="Top">The sequence at its top edge.</param>
internal sealed record LagChartFigure(
    IReadOnlyList<string> HighWater,
    IReadOnlyList<string> Position,
    IReadOnlyList<string> Stalled,
    IReadOnlyList<string> Warning,
    IReadOnlyList<string> Critical,
    long Bottom,
    long Top)
{
    /// <summary>Nothing to draw.</summary>
    public static LagChartFigure Empty { get; } = new([], [], [], [], [], 0, 0);
}

/// <summary>
/// The lag chart's geometry: pure, from a shard's samples (<see cref="ShardLagHistory" />) to SVG coordinates in a
/// <see cref="Width" /> by <see cref="Height" /> box.
/// </summary>
/// <remarks>
/// <para>
/// <b>Time across, sequence up.</b> The x axis runs from the first sample drawn to the latest; the y axis from the lowest
/// value drawn to the highest, so the gap between the two lines - the lag - is as tall as the chart can make it. A chart
/// whose lines are one on the other is a shard that kept up.
/// </para>
/// <para>
/// <b>Thresholds follow the high water.</b> A threshold is a number of events behind, so its line is the high water minus
/// it, and the position falling below that line is the lag passing it. A line is drawn only once the lag has come within
/// half of its threshold: a chart scaled to show a critical line of 100,000 for a shard 50 events behind would show nothing
/// else. The caption names the thresholds either way.
/// </para>
/// <para>
/// <b>Breaks are drawn as breaks.</b> A gap in the samples (nobody watched) cuts both lines, and a reset (the position went
/// backwards) cuts the position's: a straight line across either would draw a measurement nobody made.
/// </para>
/// </remarks>
internal static class LagChart
{
    /// <summary>The chart's width, in its own units (and CSS pixels).</summary>
    public const int Width = 160;

    /// <summary>Its height.</summary>
    public const int Height = 40;

    /// <summary>The margin the lines keep from the edges, so a stroke on the edge is not cut in half.</summary>
    private const double Pad = 2;

    /// <summary>The figure for one shard's history.</summary>
    public static LagChartFigure Draw(ShardLagHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return Draw(history.Points, history.MaxStep, history.EffectiveWarning, history.EffectiveCritical);
    }

    /// <summary>The figure for <paramref name="points" />, oldest first.</summary>
    /// <param name="points">The samples to draw.</param>
    /// <param name="maxStep">The longest step between two samples of one run.</param>
    /// <param name="warning">The warning threshold that applies, in events behind.</param>
    /// <param name="critical">The critical threshold that applies.</param>
    public static LagChartFigure Draw(IReadOnlyList<LagPoint> points, TimeSpan maxStep, long warning, long critical)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return LagChartFigure.Empty;
        }

        double nearest = points.Max(static x => x.Lag) * 2.0;
        bool drawWarning = warning > 0 && nearest >= warning;
        bool drawCritical = critical > 0 && nearest >= critical;

        long bottom = points.Min(static x => Math.Min(x.Position, x.HighWater));
        long top = points.Max(static x => Math.Max(x.Position, x.HighWater));

        if (drawWarning)
        {
            bottom = Math.Min(bottom, points.Min(x => Below(x, warning)));
        }

        if (drawCritical)
        {
            bottom = Math.Min(bottom, points.Min(x => Below(x, critical)));
        }

        Scale scale = new(points[0].At, (points[^1].At - points[0].At).TotalSeconds, bottom, top);

        IReadOnlyList<IReadOnlyList<LagPoint>> whole = ShardLagAnalysis.Runs(points, maxStep, atResets: false);
        IReadOnlyList<IReadOnlyList<LagPoint>> unbroken = ShardLagAnalysis.Runs(points, maxStep, atResets: true);

        List<string> stalled = [];
        foreach (IReadOnlyList<LagPoint> run in unbroken)
        {
            for (int i = 1; i < run.Count; i++)
            {
                if (ShardLagAnalysis.IsStalledStep(run[i - 1], run[i]))
                {
                    stalled.Add(Pair(scale, run[i - 1], run[i - 1].Position) + " " + Pair(scale, run[i], run[i].Position));
                }
            }
        }

        return new LagChartFigure(
            [.. whole.Select(run => Line(scale, run, static x => x.HighWater))],
            [.. unbroken.Select(run => Line(scale, run, static x => x.Position))],
            stalled,
            drawWarning ? [.. whole.Select(run => Line(scale, run, x => Below(x, warning)))] : [],
            drawCritical ? [.. whole.Select(run => Line(scale, run, x => Below(x, critical)))] : [],
            bottom,
            top);
    }

    /// <summary>The high water minus a threshold, never below zero: where the position crosses the threshold.</summary>
    private static long Below(LagPoint point, long threshold) => Math.Max(0, point.HighWater - threshold);

    /// <summary>
    /// One polyline's points. A run of one sample is the same point twice, which a round line cap draws as a dot - a
    /// polyline of one point draws nothing.
    /// </summary>
    private static string Line(Scale scale, IReadOnlyList<LagPoint> run, Func<LagPoint, long> value)
    {
        StringBuilder points = new();

        foreach (LagPoint point in run)
        {
            if (points.Length > 0)
            {
                points.Append(' ');
            }

            points.Append(Pair(scale, point, value(point)));
        }

        if (run.Count == 1)
        {
            points.Append(' ').Append(Pair(scale, run[0], value(run[0])));
        }

        return points.ToString();
    }

    private static string Pair(Scale scale, LagPoint point, long value) =>
        Number(scale.X(point.At)) + "," + Number(scale.Y(value));

    private static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The mapping from a sample's time and a sequence to the chart's coordinates.</summary>
    private readonly record struct Scale(DateTimeOffset Origin, double Seconds, long Bottom, long Top)
    {
        public double X(DateTimeOffset at) => Seconds <= 0
            ? Width - Pad
            : Pad + ((at - Origin).TotalSeconds / Seconds * (Width - (2 * Pad)));

        public double Y(long value) => Top <= Bottom
            ? Height / 2.0
            : Height - Pad - ((double) (value - Bottom) / (Top - Bottom) * (Height - (2 * Pad)));
    }
}
