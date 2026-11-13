using System.Globalization;

using KStudio.Core.Decoding;
using KStudio.Core.Scanning;

namespace KStudio.Services;

/// <summary>
/// What the live tail page says about how a tail runs, pauses and ends, and about what it did not cover (D-M5 decision 3.7), word for word, in one place:
/// the page draws these sentences and never builds one of its own, and a test walks every stop reason.
/// </summary>
internal static class KafkaTailText
{
    /// <summary>The most partitions the "behind" chips name before the rest are counted.</summary>
    public const int MaxBehindChips = 8;

    /// <summary>The line that says a tail started, for a screen reader.</summary>
    public const string StartedLine = "Live tail started.";

    /// <summary>A reading time: <c>3:05</c>, or <c>1:02:03</c> from the hour.</summary>
    /// <param name="span">The time.</param>
    public static string Elapsed(TimeSpan span) =>
        span.TotalHours >= 1
            ? ((int) span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Why a segment that ended without the tail ending ended, or <see langword="null" /> when the tail is over (the visitor stopped it, or it cannot go
    /// on). <c>Unavailable</c> is a pause too: the cursor is exact, so Resume goes on from it once the cluster answers.
    /// </summary>
    /// <param name="reason">The segment's stop reason.</param>
    /// <param name="requested">Why the page asked the segment to pause, when it did: the visitor, or the tab being hidden.</param>
    public static KafkaTailPause? PauseOf(KafkaScanStopReason reason, KafkaTailPause? requested) => reason switch
    {
        KafkaScanStopReason.Paused => requested ?? KafkaTailPause.You,
        KafkaScanStopReason.Deadline => KafkaTailPause.Time,
        KafkaScanStopReason.ByteBudget => KafkaTailPause.Bytes,
        KafkaScanStopReason.RecordBudget => KafkaTailPause.Records,
        KafkaScanStopReason.OwnerBudget => KafkaTailPause.Allowance,
        KafkaScanStopReason.Unavailable => KafkaTailPause.Unavailable,
        _ => null,
    };

    /// <summary>The reason in the line <c>Paused: …</c>.</summary>
    /// <param name="pause">Why the segment paused.</param>
    /// <param name="limits">The limits the segment ran under.</param>
    /// <param name="spent">For <see cref="KafkaTailPause.Allowance" />: the allowance that ran out, the process's or the cluster's own.</param>
    public static string PausedBecause(KafkaTailPause pause, KafkaScanLimits limits, KafkaSpentBudget? spent)
    {
        ArgumentNullException.ThrowIfNull(limits);

        return pause switch
        {
            KafkaTailPause.You => "you paused it",
            KafkaTailPause.TabHidden => "the tab was hidden; it resumes when you come back",
            KafkaTailPause.Time => "it read for " + KafkaStudioText.SpanText(limits.TailPauseAfter),
            KafkaTailPause.Bytes => $"it read KafkaStudioOptions.Limits.MaxBytesPerScan ({KafkaDisplay.Bytes(limits.MaxBytesPerScan)}) in one stretch",
            KafkaTailPause.Records => $"it read KafkaStudioOptions.Limits.MaxRecordsPerScan ({KafkaDisplay.Count(limits.MaxRecordsPerScan, "record")}) in one stretch",
            KafkaTailPause.Allowance => KafkaStudioText.Allowance(spent) + " is spent",
            KafkaTailPause.Unavailable => "the cluster stopped answering",
            _ => pause.ToString(),
        };
    }

    /// <summary>The line of a paused tail.</summary>
    /// <param name="pause">Why the segment paused.</param>
    /// <param name="limits">The limits the segment ran under.</param>
    /// <param name="spent">The allowance that ran out, for <see cref="KafkaTailPause.Allowance" />.</param>
    /// <param name="shown">How many records the page shows.</param>
    public static string PausedLine(KafkaTailPause pause, KafkaScanLimits limits, KafkaSpentBudget? spent, int shown) =>
        $"Paused: {PausedBecause(pause, limits, spent)}. {KafkaDisplay.Count(shown, "record")} shown.";

    /// <summary>The line of a tail that is over: the records stay and Start begins a new one.</summary>
    /// <param name="reason">The stop reason.</param>
    public static string Ended(KafkaScanStopReason reason) => reason switch
    {
        KafkaScanStopReason.Cancelled => "Stopped by you.",
        KafkaScanStopReason.Shutdown => "Stopped: the application is shutting down.",
        KafkaScanStopReason.NotAuthorized => "Nothing was read: every partition refused READ.",
        KafkaScanStopReason.Failed => KafkaStudioText.TailFailed,
        _ => "The live tail stopped.",
    };

    /// <summary>The sentence for a start the engine or the gate refused; a tail's busy owner is told what holds the place.</summary>
    /// <param name="refusal">Why.</param>
    /// <param name="spent">The allowance that has no bytes left, when the refusal is <c>OwnerBudgetSpent</c>.</param>
    public static string Refusal(KafkaScanRefusal refusal, KafkaSpentBudget? spent) => refusal switch
    {
        KafkaScanRefusal.OwnerBusy => "Another search or live tail of yours is running, in this tab or another. Stop it, or wait for it to finish.",
        KafkaScanRefusal.ProcessBusy => "Two searches or live tails are already running in this application. Try again when one has finished.",
        _ => KafkaStudioText.Refusal(refusal, spent),
    };

    /// <summary>The live line: what the tail follows and what it has read.</summary>
    /// <param name="topics">The topics.</param>
    /// <param name="fromEnd">Whether the running segment started at the end of each partition, and not at a cursor.</param>
    /// <param name="records">Records read.</param>
    /// <param name="bytes">Bytes read.</param>
    /// <param name="matches">Matches found.</param>
    /// <param name="elapsed">Time spent reading.</param>
    /// <param name="pausesAfter">How long a segment reads before it pauses itself.</param>
    public static string Running(IReadOnlyList<string> topics, bool fromEnd, long records, long bytes, long matches, TimeSpan elapsed, TimeSpan pausesAfter) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Live: reading {string.Join(", ", topics)} {(fromEnd ? "from the end" : "on from where it paused")} · {KafkaDisplay.Count(records, "record")}, {KafkaDisplay.Bytes(bytes)} read · {KafkaDisplay.Count(matches, "match", "matches")} · {Elapsed(elapsed)}, pauses itself after {KafkaStudioText.SpanText(pausesAfter)}");

    /// <summary>The chip of the partitions that have reported their end and wait for the next record.</summary>
    /// <param name="partitions">How many.</param>
    public static string LiveChip(int partitions) => string.Create(CultureInfo.InvariantCulture, $"{partitions} live");

    /// <summary>The chip of a partition that is still reading what was produced before.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="partition">The partition.</param>
    public static string BehindChip(string topic, int partition) => string.Create(CultureInfo.InvariantCulture, $"{topic}/{partition} behind");

    /// <summary>The chip for the partitions past <see cref="MaxBehindChips" />.</summary>
    /// <param name="partitions">How many were left out.</param>
    public static string MoreBehind(int partitions) => string.Create(CultureInfo.InvariantCulture, $"and {partitions} more behind");

    /// <summary>The line for matches that arrived and were evicted before the page showed them.</summary>
    /// <param name="skipped">How many.</param>
    /// <param name="capacity">What the ring keeps.</param>
    public static string Faster(long skipped, int capacity) =>
        $"{KafkaDisplay.Count(skipped, "match", "matches")} arrived faster than they could be shown; the newest {KafkaDisplay.Number(capacity)} are kept.";

    /// <summary>The line after a jump to now.</summary>
    /// <param name="offsets">The offsets the jump skipped.</param>
    public static string Jumped(long offsets) => $"Skipped {KafkaDisplay.Count(offsets, "offset")} produced while paused.";

    /// <summary>The caveat for records lost to retention while the tail was not reading.</summary>
    /// <param name="gap">The gap.</param>
    public static string GapSentence(KafkaScanGap gap)
    {
        ArgumentNullException.ThrowIfNull(gap);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{KafkaDisplay.Count(gap.Lost, "offset")} of {gap.Topic}/{gap.Partition} ({gap.FromOffset}–{gap.ToOffset}) {KafkaDisplay.Agree(gap.Lost, "was", "were")} removed by retention before the tail could read them.");
    }

    /// <summary>
    /// "What this tail does not cover": every sentence that could make the records shown look more complete than they are, over the whole session. Empty
    /// only when nothing was left out.
    /// </summary>
    /// <param name="session">The tail session.</param>
    public static IReadOnlyList<string> Caveats(KafkaTailSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        CultureInfo c = CultureInfo.InvariantCulture;
        List<string> caveats = [];

        foreach (string note in session.Notes)
        {
            Add(caveats, note);
        }

        foreach (KafkaScanGap gap in session.Gaps)
        {
            Add(caveats, GapSentence(gap));
        }

        foreach (KeyValuePair<KafkaUnknownReason, long> pair in session.UndecidableByReason.Where(static x => x.Value > 0))
        {
            Add(caveats, string.Create(c, $"{pair.Value:N0} {(pair.Value == 1 ? "record" : "records")} {KafkaUnknownReasonText.Describe(pair.Key, one: pair.Value == 1)}"));
        }

        if (session.NoTimestamp > 0)
        {
            Add(caveats, string.Create(c, $"{KafkaDisplay.Count(session.NoTimestamp, "record")} had no timestamp; a tail judges {KafkaDisplay.Agree(session.NoTimestamp, "it", "them")} all the same."));
        }

        foreach (string refusal in session.TopicRefusals)
        {
            Add(caveats, refusal);
        }

        if (session.Jumps > 0)
        {
            Add(caveats, string.Create(c, $"{KafkaDisplay.Count(session.JumpedOffsets, "offset")} produced while the tail was paused {KafkaDisplay.Agree(session.JumpedOffsets, "was", "were")} skipped by jumping to now."));
        }

        return caveats;

        static void Add(List<string> into, string sentence)
        {
            if (!into.Contains(sentence, StringComparer.Ordinal))
            {
                into.Add(sentence);
            }
        }
    }
}
