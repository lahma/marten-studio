using System.Globalization;

using MartenStudio.Services.Health.Checks;

namespace MartenStudio.Services.Projections;

/// <summary>
/// One sample of one shard, as a read of the projections screen saw it: where the shard stood and the highest sequence
/// handed out, at the moment of the read.
/// </summary>
/// <param name="At">When the read finished, by this process's clock.</param>
/// <param name="Position">The shard's position: its progression row's <c>last_seq_id</c>.</param>
/// <param name="HighWater">The highest event sequence, which the screen measures every lag against.</param>
internal readonly record struct LagPoint(DateTimeOffset At, long Position, long HighWater)
{
    /// <summary>How far behind the shard was: never negative.</summary>
    public long Lag => Math.Max(0, HighWater - Position);
}

/// <summary>Which way a shard's lag went over the last samples of its trailing run (<see cref="ShardLagAnalysis.Trend" />).</summary>
/// <param name="Direction">Catching up (<see cref="LagDirection.Draining" />), falling behind (<see cref="LagDirection.Growing" />), steady, caught up, or not known yet.</param>
/// <param name="Samples">How many samples the trend read.</param>
/// <param name="Watched">The span from the first of them to the last.</param>
/// <param name="Lag">The latest lag, or <see langword="null" /> with no sample.</param>
/// <param name="ProcessedPerSecond">How fast the position moved over the samples, in events per second.</param>
/// <param name="AppendedPerSecond">How fast the high water moved over them.</param>
/// <param name="LagPerSecond">The lag's fitted change, in events per second: positive while it grows.</param>
/// <param name="CaughtUpIn">At that rate, when the shard will be caught up - only while it is catching up.</param>
internal sealed record LagTrend(
    LagDirection Direction,
    int Samples,
    TimeSpan Watched,
    long? Lag,
    double? ProcessedPerSecond,
    double? AppendedPerSecond,
    double? LagPerSecond,
    TimeSpan? CaughtUpIn)
{
    /// <summary>No sample at all.</summary>
    public static LagTrend None { get; } = new(LagDirection.Unknown, 0, TimeSpan.Zero, null, null, null, null, null);
}

/// <summary>A shard whose position has stood still at the end of its run while the high water rose.</summary>
/// <param name="For">From the first sample that saw it where it is to the latest.</param>
/// <param name="HighWaterRose">How far the high water moved meanwhile.</param>
/// <param name="Samples">How many samples saw it there.</param>
internal sealed record LagStall(TimeSpan For, long HighWaterRose, int Samples);

/// <summary>A running rebuild's estimate, or why there is none (<see cref="ShardLagAnalysis.Eta" />).</summary>
/// <param name="ProgressSamples">How many samples in the window moved past the one before them.</param>
/// <param name="Window">The span the rate was measured over: the first sample of the window to the latest.</param>
/// <param name="Left">Events still to replay at the latest sample: the high water minus the position.</param>
/// <param name="PerSecond">The measured rate, in events per second, or <see langword="null" /> without an estimate.</param>
/// <param name="Remaining">Events left divided by that rate, or <see langword="null" /> without an estimate.</param>
/// <param name="StillFor">How many trailing samples saw the position stand still, when that is why there is no estimate.</param>
internal sealed record RebuildEta(int ProgressSamples, TimeSpan Window, long Left, double? PerSecond, TimeSpan? Remaining, int StillFor = 0)
{
    /// <summary>Whether there are enough samples showing progress to divide by.</summary>
    public bool HasEnoughSamples => ProgressSamples >= ShardLagAnalysis.EtaProgressSamples;
}

/// <summary>
/// The arithmetic the projections screen's lag chart, its arrow and a running rebuild's estimate are made of: pure, over
/// the screen's own samples (<see cref="ProjectionLagHistory" />), and never a read of anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>A run.</b> Every number here is about the samples since the last break in them - the newest sample back to the
/// first <em>reset</em> (the position went backwards: a rebuild, a rewind, a progression correction) or the first
/// <em>gap</em> (two samples further apart than nobody-was-watching allows, <see cref="ShardLagContext.MaxStep" />). A rate
/// measured across a reset is the old run's and the new one's averaged, and one measured across a gap is a guess about
/// minutes nobody watched.
/// </para>
/// <para>
/// <b>The arrow reads a fit, not two points.</b> The screen samples as often as it refreshes, and a shard that keeps up
/// commits its progress a page at a time, so its lag is a saw: it rises between pages and drops at each one. Two points,
/// or "it grew at each of the last two steps", would read that saw as falling behind half the time. So the lag of the
/// last <see cref="TrendSamples" /> samples is fitted with a straight line, and the change the line makes over them must
/// be more than twice what the samples scatter about it (and at least one event) before it is called growing or
/// shrinking - and the lag at the end must agree with the line, so a lag that rose and has started to fall is not
/// still "falling behind". Everything else is steady.
/// </para>
/// <para>
/// <b>A rebuild's estimate never guesses.</b> Events left divided by the rate measured over the rebuild's own samples -
/// at most the last <see cref="EtaWindow" /> of them - and only once <see cref="EtaProgressSamples" /> of those samples
/// moved past the one before them. A position that has stood still for <see cref="StallSamples" /> samples at the end gives
/// no estimate either: a rate from before it stopped is not this rebuild's speed now.
/// </para>
/// </remarks>
internal static class ShardLagAnalysis
{
    /// <summary>How many of a run's latest samples the arrow reads.</summary>
    public const int TrendSamples = 8;

    /// <summary>The fewest samples the arrow is drawn from.</summary>
    public const int MinimumTrendSamples = 4;

    /// <summary>The fewest trailing samples, standing still with the high water rising, that make a shard "stalled".</summary>
    public const int StallSamples = 3;

    /// <summary>How many samples must show progress - each past the one before it - before a rebuild has an estimate.</summary>
    public const int EtaProgressSamples = 3;

    /// <summary>The most recent span a rebuild's rate is measured over: its speed now, not its average since it began.</summary>
    public static readonly TimeSpan EtaWindow = TimeSpan.FromMinutes(5);

    /// <summary>The longest an estimate is allowed to say.</summary>
    private static readonly double LongestSeconds = TimeSpan.FromDays(365).TotalSeconds;

    /// <summary>
    /// The samples since the last break: from the newest back to the first reset or gap, oldest first - and none older than
    /// <paramref name="notBefore" /> when it is given (a rebuild's start).
    /// </summary>
    public static IReadOnlyList<LagPoint> TrailingRun(IReadOnlyList<LagPoint> points, TimeSpan maxStep, DateTimeOffset? notBefore = null)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return [];
        }

        int start = points.Count - 1;

        while (start > 0 && !IsBreak(points[start - 1], points[start], maxStep) && (notBefore is not { } from || points[start - 1].At >= from))
        {
            start--;
        }

        if (notBefore is { } since && points[start].At < since)
        {
            return [];
        }

        return [.. points.Skip(start)];
    }

    /// <summary>
    /// The samples cut into runs, oldest first: a new run begins at every gap, and - when <paramref name="atResets" /> - at
    /// every reset. What the chart draws as separate lines.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<LagPoint>> Runs(IReadOnlyList<LagPoint> points, TimeSpan maxStep, bool atResets)
    {
        ArgumentNullException.ThrowIfNull(points);

        List<IReadOnlyList<LagPoint>> runs = [];
        List<LagPoint> current = [];

        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0 && (IsGap(points[i - 1], points[i], maxStep) || (atResets && IsReset(points[i - 1], points[i]))))
            {
                runs.Add(current);
                current = [];
            }

            current.Add(points[i]);
        }

        if (current.Count > 0)
        {
            runs.Add(current);
        }

        return runs;
    }

    /// <summary>Whether the step from <paramref name="before" /> to <paramref name="after" /> breaks a run.</summary>
    public static bool IsBreak(LagPoint before, LagPoint after, TimeSpan maxStep) => IsGap(before, after, maxStep) || IsReset(before, after);

    /// <summary>Nobody watched between the two: they are further apart than <paramref name="maxStep" />, or out of order.</summary>
    public static bool IsGap(LagPoint before, LagPoint after, TimeSpan maxStep) => after.At - before.At > maxStep || after.At <= before.At;

    /// <summary>The position went backwards: a rebuild, a rewind or a correction started the shard again.</summary>
    public static bool IsReset(LagPoint before, LagPoint after) => after.Position < before.Position;

    /// <summary>The position did not move between the two while the high water did, and the shard had work to do.</summary>
    public static bool IsStalledStep(LagPoint before, LagPoint after) =>
        after.Position == before.Position && after.HighWater > before.HighWater && after.Position < after.HighWater;

    /// <summary>Which way the lag went over the last <see cref="TrendSamples" /> samples of <paramref name="run" />.</summary>
    /// <param name="run">One run (<see cref="TrailingRun" />), oldest first.</param>
    public static LagTrend Trend(IReadOnlyList<LagPoint> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Count == 0)
        {
            return LagTrend.None;
        }

        int take = Math.Min(run.Count, TrendSamples);
        LagPoint[] window = [.. run.Skip(run.Count - take)];

        LagPoint first = window[0];
        LagPoint last = window[^1];
        long lag = last.Lag;
        TimeSpan watched = last.At - first.At;

        if (window.Length < MinimumTrendSamples || watched <= TimeSpan.Zero)
        {
            return new LagTrend(lag == 0 ? LagDirection.CaughtUp : LagDirection.Unknown, window.Length, watched, lag, null, null, null, null);
        }

        double seconds = watched.TotalSeconds;
        double processed = Math.Max(0, (last.Position - first.Position) / seconds);
        double appended = Math.Max(0, (last.HighWater - first.HighWater) / seconds);
        (double slope, double scatter) = Fit(window);

        double change = slope * seconds;
        double margin = Math.Max(2 * scatter, 1);

        LagDirection direction =
            lag == 0 ? LagDirection.CaughtUp
            : change > margin && last.Lag > first.Lag ? LagDirection.Growing
            : change < -margin && last.Lag < first.Lag ? LagDirection.Draining
            : LagDirection.Steady;

        TimeSpan? caughtUpIn = direction == LagDirection.Draining && slope < 0
            ? TimeSpan.FromSeconds(Math.Min(lag / -slope, LongestSeconds))
            : null;

        return new LagTrend(direction, window.Length, watched, lag, processed, appended, slope, caughtUpIn);
    }

    /// <summary>
    /// The run's trailing stall: the samples at its end that saw the position where it is now, when there are at least
    /// <see cref="StallSamples" /> of them, the high water rose across them and the shard has work to do.
    /// </summary>
    public static LagStall? Stall(IReadOnlyList<LagPoint> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Count < StallSamples)
        {
            return null;
        }

        LagPoint last = run[^1];
        int first = run.Count - 1;

        while (first > 0 && run[first - 1].Position == last.Position)
        {
            first--;
        }

        int samples = run.Count - first;
        long rose = last.HighWater - run[first].HighWater;

        return samples >= StallSamples && rose > 0 && last.Position < last.HighWater
            ? new LagStall(last.At - run[first].At, rose, samples)
            : null;
    }

    /// <summary>A running rebuild's estimate over <paramref name="run" />: the rebuild's own samples, oldest first.</summary>
    /// <param name="run">The run since the rebuild began (<see cref="TrailingRun" /> with its start).</param>
    public static RebuildEta Eta(IReadOnlyList<LagPoint> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Count == 0)
        {
            return new RebuildEta(0, TimeSpan.Zero, 0, null, null);
        }

        LagPoint last = run[^1];
        LagPoint[] window = [.. run.Where(x => x.At >= last.At - EtaWindow)];

        int progress = 0;
        for (int i = 1; i < window.Length; i++)
        {
            if (window[i].Position > window[i - 1].Position)
            {
                progress++;
            }
        }

        TimeSpan span = last.At - window[0].At;

        if (progress < EtaProgressSamples || span <= TimeSpan.Zero)
        {
            return new RebuildEta(progress, span, last.Lag, null, null);
        }

        int still = 1;
        while (still < window.Length && window[^(still + 1)].Position == last.Position)
        {
            still++;
        }

        if (still >= StallSamples && last.Lag > 0)
        {
            return new RebuildEta(progress, span, last.Lag, null, null, still);
        }

        double rate = (last.Position - window[0].Position) / span.TotalSeconds;

        if (last.Lag == 0 || rate <= 0)
        {
            return new RebuildEta(progress, span, last.Lag, rate > 0 ? rate : null, last.Lag == 0 ? TimeSpan.Zero : null);
        }

        return new RebuildEta(progress, span, last.Lag, rate, TimeSpan.FromSeconds(Math.Min(last.Lag / rate, LongestSeconds)));
    }

    /// <summary>The lag's least-squares slope in events per second, and the samples' scatter about the line.</summary>
    private static (double Slope, double Scatter) Fit(LagPoint[] window)
    {
        DateTimeOffset origin = window[0].At;
        int n = window.Length;

        double meanX = 0;
        double meanY = 0;

        foreach (LagPoint point in window)
        {
            meanX += (point.At - origin).TotalSeconds;
            meanY += point.Lag;
        }

        meanX /= n;
        meanY /= n;

        double sxx = 0;
        double sxy = 0;

        foreach (LagPoint point in window)
        {
            double dx = (point.At - origin).TotalSeconds - meanX;
            sxx += dx * dx;
            sxy += dx * (point.Lag - meanY);
        }

        if (sxx <= 0)
        {
            return (0, 0);
        }

        double slope = sxy / sxx;
        double squares = 0;

        foreach (LagPoint point in window)
        {
            double fitted = meanY + (slope * ((point.At - origin).TotalSeconds - meanX));
            double residual = point.Lag - fitted;
            squares += residual * residual;
        }

        return (slope, n > 2 ? Math.Sqrt(squares / (n - 2)) : 0);
    }
}

/// <summary>
/// The studio's own thresholds that apply to a store's shards, read off its settings, for the projections screen's
/// "Thresholds that apply": the lag colours, D01, D03, D04 and D05's numbers, and how this screen's own samples are kept.
/// </summary>
/// <param name="SlowPollingTime">The store's <c>Projections.SlowPollingTime</c>, which D03's window is a multiple of.</param>
/// <param name="HealthInterval">The advisor's sampling interval (<c>EffectiveHealthInterval</c>).</param>
/// <param name="SampleSpacing">How far apart this screen keeps its samples (<see cref="ProjectionLagHistory.SpacingFor" />).</param>
internal sealed record ProjectionThresholds(TimeSpan SlowPollingTime, TimeSpan HealthInterval, TimeSpan SampleSpacing)
{
    /// <summary>D03's window for a shard the advisor has not yet seen take a page.</summary>
    public TimeSpan ShardQuiet => DaemonThresholds.ShardQuiet(SlowPollingTime, HealthInterval);

    /// <summary>The most D03's window may be, however slow the shard's pages.</summary>
    public TimeSpan ShardQuietCeiling => DaemonThresholds.ShardQuietCeilingFor(HealthInterval);

    /// <summary>How long D05 remembers a shard it saw fail, while it has not moved past where it failed.</summary>
    public TimeSpan RestartMemory => ShardFailedOrPausedCheck.RestartMemoryFor(HealthInterval);

    /// <summary>How far back this screen's chart reaches.</summary>
    public TimeSpan SampleWindow => SampleSpacing * ProjectionLagHistory.Capacity;
}

/// <summary>What a shard's history is judged with, besides its samples.</summary>
/// <param name="Spacing">How far apart the samples are kept (<see cref="ProjectionLagHistory.SpacingFor" />).</param>
/// <param name="Rebuilding">Whether its projection is known to be being rebuilt: by Marten Studio, or by the daemon here.</param>
/// <param name="RebuildStartedAt">When Marten Studio started that rebuild, when it did - no sample before it is the rebuild's.</param>
/// <param name="PausedByStudio">Whether Marten Studio's coordinator pause is recorded for the store.</param>
/// <param name="AgentStatusHere">The status of the shard's agent in the daemon hosted here, when one runs it.</param>
/// <param name="WarningThreshold">The row's own <c>warning_behind_threshold</c>, when the host wrote one.</param>
/// <param name="CriticalThreshold">The row's own <c>critical_behind_threshold</c>, likewise.</param>
internal sealed record ShardLagContext(
    TimeSpan Spacing,
    bool Rebuilding = false,
    DateTimeOffset? RebuildStartedAt = null,
    bool PausedByStudio = false,
    string? AgentStatusHere = null,
    long? WarningThreshold = null,
    long? CriticalThreshold = null)
{
    /// <summary>
    /// The longest step between two samples that is still one run: four of the spacing, and at least half a minute - a poll
    /// that ran late is not a break, a tab that was hidden for a minute is.
    /// </summary>
    public TimeSpan MaxStep => Spacing * 4 > TimeSpan.FromSeconds(30) ? Spacing * 4 : TimeSpan.FromSeconds(30);

    /// <summary>How far back the chart reaches: the history's capacity at its spacing.</summary>
    public TimeSpan DisplayWindow => Spacing * ProjectionLagHistory.Capacity;
}

/// <summary>
/// One shard's history as the projections screen draws it: the samples within the chart's window, the arrow, the stall,
/// a running rebuild's estimate and the thresholds that apply.
/// </summary>
/// <param name="ShardName">The shard.</param>
/// <param name="Points">The samples the chart draws, oldest first.</param>
/// <param name="Trend">The arrow's arithmetic, over the trailing run.</param>
internal sealed record ShardLagHistory(string ShardName, IReadOnlyList<LagPoint> Points, LagTrend Trend)
{
    /// <summary>The trailing stall, when the run ends standing still while the high water rose.</summary>
    public LagStall? Stall { get; init; }

    /// <summary>The estimate of a rebuild that is running, or <see langword="null" /> while none is.</summary>
    public RebuildEta? Rebuild { get; init; }

    /// <summary>
    /// Why the arrow is not drawn although the samples would draw one: a lag that grows by design (Marten Studio's pause)
    /// or because the agent here is not running (D05's). A stall and a rebuild are not reasons - they are what the chart
    /// and the estimate are for.
    /// </summary>
    public string? WithheldBecause { get; init; }

    /// <summary>The row's own warning threshold, when the host wrote one.</summary>
    public long? WarningThreshold { get; init; }

    /// <summary>The row's own critical threshold, when the host wrote one.</summary>
    public long? CriticalThreshold { get; init; }

    /// <summary>The longest step between two samples of one run.</summary>
    public TimeSpan MaxStep { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The warning threshold that applies: the row's own, or the studio's 1,000.</summary>
    public long EffectiveWarning => WarningThreshold ?? ProjectionLagThresholds.Amber;

    /// <summary>The critical threshold that applies: the row's own, or the studio's 100,000.</summary>
    public long EffectiveCritical => CriticalThreshold ?? ProjectionLagThresholds.Red;

    /// <summary>Whether this history says anything about the arrow - a direction, or why there is none to draw.</summary>
    public bool HasTrend => Trend.Direction != LagDirection.Unknown || WithheldBecause is not null;

    /// <summary>The span the chart covers: its first sample to its last.</summary>
    public TimeSpan Span => Points.Count == 0 ? TimeSpan.Zero : Points[^1].At - Points[0].At;

    /// <summary>The arrow for the lag cell and the caption - none while withheld or unknown.</summary>
    public string Arrow => WithheldBecause is not null ? string.Empty : Trend.Direction switch
    {
        LagDirection.Growing => "↑",
        LagDirection.Draining => "↓",
        LagDirection.Steady => "→",
        LagDirection.CaughtUp => "✓",
        _ => string.Empty,
    };

    /// <summary>The direction as a class suffix, the same one the advisor's arrow uses.</summary>
    public string DirectionClass => Trend.Direction.ToString().ToLowerInvariant();

    /// <summary>Builds one shard's history from its samples, oldest first.</summary>
    public static ShardLagHistory Build(string shardName, IReadOnlyList<LagPoint> points, ShardLagContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardName);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(context);

        TimeSpan maxStep = context.MaxStep;
        IReadOnlyList<LagPoint> drawn = points.Count == 0
            ? []
            : [.. points.Where(x => x.At >= points[^1].At - context.DisplayWindow)];

        IReadOnlyList<LagPoint> run = ShardLagAnalysis.TrailingRun(drawn, maxStep);

        string? withheld =
            context.Rebuilding ? null
            : context.PausedByStudio ? "not judged: Marten Studio paused the projection coordinator, and every lag grows until it is resumed"
            : context.AgentStatusHere is { Length: > 0 } status && !string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
                ? "not judged: its agent here is " + status + " (D05)"
            : null;

        return new ShardLagHistory(shardName, drawn, ShardLagAnalysis.Trend(run))
        {
            Stall = ShardLagAnalysis.Stall(run),
            Rebuild = context.Rebuilding ? ShardLagAnalysis.Eta(ShardLagAnalysis.TrailingRun(drawn, maxStep, context.RebuildStartedAt)) : null,
            WithheldBecause = withheld,
            WarningThreshold = context.WarningThreshold,
            CriticalThreshold = context.CriticalThreshold,
            MaxStep = maxStep,
        };
    }

    /// <summary>
    /// The caption's first line, and the text equivalent of the chart: the latest lag, the trend and the span it was read
    /// over - "1,234 behind, catching up over the last 40 s, caught up in ~2 min".
    /// </summary>
    public string Summary()
    {
        long lag = Trend.Lag ?? 0;
        string behind = lag == 0 ? "Caught up" : Number(lag) + " behind";

        if (WithheldBecause is { } withheld)
        {
            return behind + "; " + withheld + ".";
        }

        return Trend.Direction switch
        {
            LagDirection.CaughtUp => behind + ".",
            LagDirection.Draining => behind + ", catching up over the last " + Format(Trend.Watched)
                + (Trend.CaughtUpIn is { } eta ? ", caught up in ~" + Format(eta) : string.Empty) + ".",
            LagDirection.Steady => behind + ", steady over the last " + Format(Trend.Watched) + ".",
            LagDirection.Growing => behind + ", falling behind by " + ShardTrend.Rate(Trend.LagPerSecond ?? 0)
                + " over the last " + Format(Trend.Watched) + ".",
            _ => behind + "; no trend yet: " + Trend.Samples.ToString(CultureInfo.InvariantCulture) + " of the "
                + ShardLagAnalysis.MinimumTrendSamples.ToString(CultureInfo.InvariantCulture) + " samples a trend needs.",
        };
    }

    /// <summary>The arrow's hover text and accessible name.</summary>
    public string Describe() => WithheldBecause ?? Trend.Direction switch
    {
        LagDirection.CaughtUp => "caught up",
        LagDirection.Draining => "catching up: the lag shrank by " + ShardTrend.Rate(-(Trend.LagPerSecond ?? 0)) + " over this page's last "
            + Samples() + (Trend.CaughtUpIn is { } eta ? ", caught up in ~" + Format(eta) : string.Empty),
        LagDirection.Steady => "steady: the lag neither grew nor shrank by more than it wobbles, over this page's last " + Samples(),
        LagDirection.Growing => "falling behind: the lag grew by " + ShardTrend.Rate(Trend.LagPerSecond ?? 0) + " over this page's last " + Samples()
            + " (processing " + ShardTrend.Rate(Trend.ProcessedPerSecond ?? 0) + ", " + ShardTrend.Rate(Trend.AppendedPerSecond ?? 0) + " arriving)",
        _ => "no trend yet",
    };

    /// <summary>"Stalled: not moved for 40 s while the high water rose by 1,200.", or <see langword="null" />.</summary>
    public string? StallSentence() => Stall is { } stall
        ? "Stalled: not moved for " + Format(stall.For) + " while the high water rose by " + Number(stall.HighWaterRose) + "."
        : null;

    /// <summary>A running rebuild's estimate, with the window it was measured over - or why there is none.</summary>
    public string? RebuildSentence() => Rebuild switch
    {
        null => null,
        { HasEnoughSamples: false } eta =>
            "Rebuild: not enough samples yet - " + eta.ProgressSamples.ToString(CultureInfo.InvariantCulture) + " of the "
            + ShardLagAnalysis.EtaProgressSamples.ToString(CultureInfo.InvariantCulture) + " showing progress an estimate needs.",
        { StillFor: > 0 } eta =>
            "Rebuild: no estimate - the position has not moved over the last " + eta.StillFor.ToString(CultureInfo.InvariantCulture) + " samples.",
        { Left: 0 } => "Rebuild: caught up with the high water.",
        { Remaining: { } remaining, PerSecond: { } rate } eta =>
            "Rebuild: about " + Format(remaining) + " left - " + Number(eta.Left) + " events at " + ShardTrend.Rate(rate)
            + ", from the last " + Format(eta.Window) + ".",
        _ => "Rebuild: no estimate.",
    };

    /// <summary>The thresholds the host wrote into the shard's progression row, or <see langword="null" /> when it wrote none.</summary>
    public string? ThresholdSentence()
    {
        if (WarningThreshold is null && CriticalThreshold is null)
        {
            return null;
        }

        return "Thresholds: warning above " + Number(EffectiveWarning) + (WarningThreshold is null ? " (the studio's)" : " (its progression row's)")
            + ", critical above " + Number(EffectiveCritical) + (CriticalThreshold is null ? " (the studio's)" : " (its progression row's)") + ".";
    }

    /// <summary>The chart's title.</summary>
    public string Title() => Points.Count <= 1
        ? ShardName + ": its position against the high water - one sample so far"
        : ShardName + ": its position against the high water over the last " + Format(Span);

    /// <summary>The chart's description: everything the caption says, in one text.</summary>
    public string Description() =>
        string.Join(" ", new[] { Summary(), StallSentence(), RebuildSentence(), ThresholdSentence() }.Where(static x => x is not null));

    private string Samples() => Trend.Samples.ToString(CultureInfo.InvariantCulture) + " samples (" + Format(Trend.Watched) + ")";

    private static string Format(TimeSpan span) => DaemonCheckConfiguration.Format(span);

    private static string Number(long value) => DaemonCheckConfiguration.Number(value);
}
