using System.Globalization;

using MartenStudio.Services.Health.Checks;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Projections;

/// <summary>Which way a shard's lag is going, over the advisor's recent samples.</summary>
internal enum LagDirection
{
    /// <summary>Fewer than two samples a minute apart: no rate yet.</summary>
    Unknown,

    /// <summary>Nothing to process.</summary>
    CaughtUp,

    /// <summary>The lag is shrinking.</summary>
    Draining,

    /// <summary>The lag has not changed.</summary>
    Steady,

    /// <summary>The lag grew at each of the last two steps between samples: not a single burst.</summary>
    Growing,
}

/// <summary>One shard's lag over the advisor's history.</summary>
/// <param name="ShardName">The shard.</param>
/// <param name="Direction">Which way the lag is going.</param>
/// <param name="Lag">The lag at the latest sample (highest sequence handed out minus position), or <see langword="null" /> with no sample.</param>
/// <param name="MeasuredAt">When the latest sample was read.</param>
/// <param name="ProcessedPerSecond">How fast the shard's position moved over the window, in events per second.</param>
/// <param name="AppendedPerSecond">How fast the sequence moved over the window.</param>
/// <param name="LagPerSecond">How fast the lag changed: positive when growing.</param>
/// <param name="CaughtUpIn">At this rate, when it will be caught up - only while draining.</param>
/// <param name="Watched">The span the rates were measured over.</param>
/// <param name="Samples">How many samples the window held.</param>
/// <param name="WarningThreshold">The shard's own warning threshold, when its row carries one.</param>
/// <param name="CriticalThreshold">The shard's own critical threshold, likewise.</param>
internal sealed record ShardTrend(
    string ShardName,
    LagDirection Direction,
    long? Lag,
    DateTimeOffset? MeasuredAt,
    double? ProcessedPerSecond,
    double? AppendedPerSecond,
    double? LagPerSecond,
    TimeSpan? CaughtUpIn,
    TimeSpan Watched,
    int Samples,
    long? WarningThreshold,
    long? CriticalThreshold)
{
    /// <summary>The warning threshold that applies: the row's own, or the studio's 1,000.</summary>
    public long EffectiveWarning => WarningThreshold ?? ProjectionLagThresholds.Amber;

    /// <summary>The critical threshold that applies: the row's own, or the studio's 100,000.</summary>
    public long EffectiveCritical => CriticalThreshold ?? ProjectionLagThresholds.Red;

    /// <summary>Whether the high-water mark moved over the window: whether anything new reached the shards at all.</summary>
    public bool MarkMoved { get; init; }

    /// <summary>
    /// Why this trend says nothing about the shard's speed right now (<see cref="ProjectionTrend.Judged" />): a pause,
    /// a rebuild, an agent that is not running, a shard or a mark that has not moved. No arrow is drawn and no D04 is
    /// raised for it; the reason is what is said instead.
    /// </summary>
    public string? WithheldBecause { get; init; }

    /// <summary>"draining at 120 ev/s, caught up in ~3 min", or why there is nothing to say yet.</summary>
    public string Describe() => WithheldBecause ?? Direction switch
    {
        LagDirection.Unknown when Samples < 2 => "no trend yet: the advisor needs two samples a minute apart",
        LagDirection.Unknown => "no trend yet: watched for " + DaemonCheckConfiguration.Format(Watched),
        LagDirection.CaughtUp => "caught up",
        LagDirection.Draining when CaughtUpIn is { } eta =>
            "draining at " + Rate(-(LagPerSecond ?? 0)) + ", caught up in ~" + DaemonCheckConfiguration.Format(eta),
        LagDirection.Draining => "draining",
        LagDirection.Steady => "steady at " + DaemonCheckConfiguration.Number(Lag ?? 0) + " behind, processing " + Rate(ProcessedPerSecond ?? 0),
        LagDirection.Growing => "growing by " + Rate(LagPerSecond ?? 0) + " (processing " + Rate(ProcessedPerSecond ?? 0)
            + ", " + Rate(AppendedPerSecond ?? 0) + " arriving)",
        _ => string.Empty,
    };

    /// <summary>An arrow for a table cell - none for a trend that is withheld.</summary>
    public string Arrow => WithheldBecause is not null ? string.Empty : Direction switch
    {
        LagDirection.Growing => "↑",
        LagDirection.Draining => "↓",
        LagDirection.Steady => "→",
        LagDirection.CaughtUp => "✓",
        _ => string.Empty,
    };

    /// <summary>Events per second, for a person.</summary>
    internal static string Rate(double perSecond) =>
        (perSecond >= 10 ? perSecond.ToString("N0", CultureInfo.InvariantCulture) : perSecond.ToString("0.#", CultureInfo.InvariantCulture)) + " ev/s";
}

/// <summary>
/// Lag trends and throughput from <see cref="HealthHistory" />'s samples: pure arithmetic over what the advisor
/// recorded, shared by D04, the projections screen and the rebuild dialog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only what a sweep recorded.</b> The samples come from the health advisor's regular sweep - D01 writes the
/// sequence and the mark, D03 every shard's position - which runs only while the Health page or the Overview is
/// open. Nothing here reads the database, and nothing here asks for a sweep: a screen that finds no samples says
/// "no trend yet" and means it.
/// </para>
/// <para>
/// <b>Rates over the window, not the last step</b> (<see cref="HealthTrend.PerSecond" />'s rule): one slow sweep must
/// not make a steady drain look stalled. "Growing" is the opposite case and needs more than a rate: the lag must have
/// grown at each of the last two steps, so arrivals outpaced processing twice running. A burst of appends lands in one
/// step, however large, and a shard that keeps up drains it at the next - so a single burst is never "growing", and a
/// lag that grew and is now draining is not reported as still growing.
/// </para>
/// </remarks>
internal static class ProjectionTrend
{
    /// <summary>The shard's trend, over the samples within <paramref name="window" /> of the latest one.</summary>
    public static ShardTrend For(HealthHistoryView history, string shard, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(shard);

        TimeSpan span = window ?? DaemonThresholds.TrendWindow;
        string positionSeries = DaemonSeries.ShardPosition(shard);

        List<(DateTimeOffset At, double Position, double Highest, double? Mark)> points = [];
        long? warning = null;
        long? critical = null;

        foreach (HealthSample sample in history.Samples)
        {
            if (sample.Values.TryGetValue(positionSeries, out double position)
                && sample.Values.TryGetValue(DaemonSeries.HighestSequence, out double highest))
            {
                points.Add((sample.At, position, highest,
                    sample.Values.TryGetValue(DaemonSeries.HighWaterMark, out double mark) ? mark : null));
            }

            if (sample.Values.TryGetValue(DaemonSeries.ShardWarningThreshold(shard), out double w))
            {
                warning = (long) w;
            }

            if (sample.Values.TryGetValue(DaemonSeries.ShardCriticalThreshold(shard), out double c))
            {
                critical = (long) c;
            }
        }

        if (points.Count == 0)
        {
            return new ShardTrend(shard, LagDirection.Unknown, null, null, null, null, null, null, TimeSpan.Zero, 0, warning, critical);
        }

        (DateTimeOffset latestAt, double latestPosition, double latestHighest, double? latestMark) = points[^1];
        points.RemoveAll(x => x.At < latestAt - span);

        long lag = LagOf(latestPosition, latestHighest);
        (DateTimeOffset firstAt, double firstPosition, double firstHighest, double? firstMark) = points[0];
        TimeSpan watched = latestAt - firstAt;
        bool markMoved = latestMark is { } to && firstMark is { } from && to > from;

        if (points.Count < 2 || watched < DaemonThresholds.MinimumWatch)
        {
            return new ShardTrend(
                shard, lag == 0 ? LagDirection.CaughtUp : LagDirection.Unknown, lag, latestAt,
                null, null, null, null, watched, points.Count, warning, critical) { MarkMoved = markMoved };
        }

        double seconds = watched.TotalSeconds;
        double processed = Math.Max(0, (latestPosition - firstPosition) / seconds);
        double appended = Math.Max(0, (latestHighest - firstHighest) / seconds);
        long firstLag = LagOf(firstPosition, firstHighest);
        double lagRate = (lag - firstLag) / seconds;

        LagDirection direction =
            lag == 0 ? LagDirection.CaughtUp
            : GrewAtEachOfTheLastTwoSteps(points) ? LagDirection.Growing
            : lag < firstLag ? LagDirection.Draining
            : LagDirection.Steady;

        TimeSpan? eta = direction == LagDirection.Draining && lagRate < 0
            ? TimeSpan.FromSeconds(Math.Min(lag / -lagRate, TimeSpan.FromDays(365).TotalSeconds))
            : null;

        return new ShardTrend(shard, direction, lag, latestAt, processed, appended, lagRate, eta, watched, points.Count, warning, critical)
        {
            MarkMoved = markMoved,
        };
    }

    /// <summary>
    /// The trend as D04 and the projections screen may state it: withheld, with the reason, wherever a growing (or
    /// standing) lag is explained by something other than the shard's speed.
    /// </summary>
    /// <param name="trend">The arithmetic.</param>
    /// <param name="pausedByStudio">Marten Studio's coordinator pause is recorded for the store: every lag grows, by design.</param>
    /// <param name="rebuilding">
    /// The shard's projection is known to be being rebuilt: its position restarted from zero - from Marten 9.52, from its
    /// start floor, which the daemon seeds the progression row at before the replay.
    /// </param>
    /// <param name="agentStatusHere">The status of the shard's agent in the daemon hosted here, when one runs it.</param>
    /// <remarks>
    /// A growth claim also needs the shard to have moved over the window - one that stands still while events arrive is
    /// not slow, it is stalled or not run (D03, D02) - and the mark to have moved, because a mark held behind a gap
    /// grows the handed-out ceiling's lag with nothing reaching any shard (D01). A steady lag needs the shard to have
    /// moved for the same reason. A drain is a fact whatever the mark does, and is stated.
    /// </remarks>
    public static ShardTrend Judged(ShardTrend trend, bool pausedByStudio, bool rebuilding, string? agentStatusHere)
    {
        ArgumentNullException.ThrowIfNull(trend);

        string? reason =
            pausedByStudio ? "not judged: Marten Studio paused the projection coordinator, and every lag grows until it is resumed"
            : rebuilding ? "not judged: being rebuilt, which starts the shard again from the beginning (from Marten 9.52, from its start floor)"
            : agentStatusHere is { Length: > 0 } status && !string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase)
                ? "not judged: its agent here is " + status + " (D05)"
            : trend.Direction is LagDirection.Growing or LagDirection.Steady && trend.ProcessedPerSecond is not > 0
                ? "not judged: the shard has not moved while the advisor watched, which is about whether it runs (D02, D03), not its speed"
            : trend.Direction is LagDirection.Growing or LagDirection.Steady && !trend.MarkMoved
                ? "not judged: the high-water mark has not moved, so nothing new reached the shard (D01)"
            : null;

        return reason is null ? trend : trend with { WithheldBecause = reason };
    }

    /// <summary>
    /// How long a page of <paramref name="shard" /> typically takes, as the advisor saw it: the median of the runs in
    /// which the shard stood still with work to do before it moved on - what D03's window is calibrated against.
    /// </summary>
    /// <param name="history">The advisor's samples.</param>
    /// <param name="shard">The shard.</param>
    /// <param name="windowFor">D03's window for a given typical page - the rule a run is checked against.</param>
    /// <remarks>
    /// <para>
    /// <b>The median, of runs that were pages.</b> A run that lasted longer than D03's window in force when it ended - the
    /// window the median of the runs before it gave - was a stall, a pause, an outage or a rebuild writing nothing, not a
    /// page, and is dropped: counting it would let one bad hour switch the check off for the next. The median then keeps
    /// one odd run from moving the window at all.
    /// </para>
    /// <para>
    /// A run starts at a sample in which the mark is ahead of the shard and ends at the first sample in which the shard
    /// is further on. A run broken by a sample without the shard, or by a step longer than <see cref="MaxSampleStep" />
    /// (nobody watched in between), measures nothing; one still running now is the stall itself and is not counted.
    /// </para>
    /// </remarks>
    public static TimeSpan? TypicalPage(HealthHistoryView history, string shard, Func<TimeSpan?, TimeSpan> windowFor)
    {
        ArgumentNullException.ThrowIfNull(windowFor);

        List<TimeSpan> pages = [];

        foreach (TimeSpan run in CompletedRuns(history, shard))
        {
            if (run <= windowFor(Median(pages)))
            {
                pages.Add(run);
            }
        }

        return Median(pages);
    }

    private static TimeSpan? Median(List<TimeSpan> runs)
    {
        if (runs.Count == 0)
        {
            return null;
        }

        List<TimeSpan> sorted = [.. runs.Order()];
        int middle = sorted.Count / 2;

        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>Every completed still-with-work run of <paramref name="shard" />, oldest first.</summary>
    private static List<TimeSpan> CompletedRuns(HealthHistoryView history, string shard)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(shard);

        string series = DaemonSeries.ShardPosition(shard);
        List<TimeSpan> runs = [];
        DateTimeOffset? runStart = null;
        DateTimeOffset previousAt = DateTimeOffset.MinValue;
        double runPosition = 0;
        bool runHadWork = false;

        foreach (HealthSample sample in history.Samples)
        {
            if (!sample.Values.TryGetValue(series, out double position))
            {
                runStart = null;
                continue;
            }

            bool hasWork = sample.Values.TryGetValue(DaemonSeries.HighWaterMark, out double mark) && mark > position;

            if (runStart is { } started && sample.At - previousAt <= MaxSampleStep)
            {
                if (position > runPosition && runHadWork)
                {
                    runs.Add(sample.At - started);
                }

                if (position != runPosition)
                {
                    runStart = sample.At;
                    runPosition = position;
                    runHadWork = hasWork;
                }
            }
            else
            {
                runStart = sample.At;
                runPosition = position;
                runHadWork = hasWork;
            }

            previousAt = sample.At;
        }

        return runs;
    }

    /// <summary>A step between two samples longer than this is a gap in the watching, not a page.</summary>
    internal static readonly TimeSpan MaxSampleStep = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The fastest any of <paramref name="shards" /> was measured processing events, in events per second, or
    /// <see langword="null" /> when none was seen to move: what the rebuild dialog divides by.
    /// </summary>
    /// <remarks>
    /// Measured while the shards were catching up in continuous mode, which is not the same code path as a rebuild's
    /// replay; the dialog says so beside the number rather than pretending it is a rebuild's rate.
    /// </remarks>
    public static double? Throughput(HealthHistoryView history, IEnumerable<string> shards)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(shards);

        double? best = null;

        foreach (string shard in shards)
        {
            if (For(history, shard).ProcessedPerSecond is { } rate && rate > 0 && (best is null || rate > best))
            {
                best = rate;
            }
        }

        return best;
    }

    /// <summary>"About 12 min at the 140 ev/s ...", or the honest "no estimate yet".</summary>
    public static string EstimateRebuild(long eventsToReplay, double? throughput, HealthHistoryView history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (eventsToReplay <= 0)
        {
            return "Nothing to replay.";
        }

        if (throughput is not { } rate || rate <= 0)
        {
            return history.Samples.Count < 2
                ? "No estimate yet: the health advisor has not watched this database for long enough to measure how fast this projection processes events. It samples while the Health page or the Overview is open."
                : "No estimate yet: the health advisor has not seen this projection's shards process events while it watched.";
        }

        TimeSpan estimate = TimeSpan.FromSeconds(Math.Min(eventsToReplay / rate, TimeSpan.FromDays(365).TotalSeconds));

        return "About " + DaemonCheckConfiguration.Format(estimate) + ", at the " + ShardTrend.Rate(rate)
            + " this projection was measured processing while it caught up. A rebuild replays in batches and is often faster; a busy database makes it slower.";
    }

    private static long LagOf(double position, double highest) => (long) Math.Max(0, highest - position);

    /// <summary>
    /// Whether the lag grew at each of the last two steps between samples: arrivals outpaced processing twice running.
    /// A burst lands in one step; a shard that drains it shrinks the lag at the next, so a burst alone is never growth.
    /// </summary>
    private static bool GrewAtEachOfTheLastTwoSteps(List<(DateTimeOffset At, double Position, double Highest, double? Mark)> points)
    {
        if (points.Count < 3)
        {
            return false;
        }

        long last = LagOf(points[^1].Position, points[^1].Highest);
        long before = LagOf(points[^2].Position, points[^2].Highest);
        long earlier = LagOf(points[^3].Position, points[^3].Highest);

        return last > before && before > earlier;
    }
}
