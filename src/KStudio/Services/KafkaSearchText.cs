using System.Globalization;

using KStudio.Core.Scanning;
using KStudio.Core.Windows;

namespace KStudio.Services;

/// <summary>
/// What the search page says about how a search ended and about what it did not cover (D-UI decisions 11 and 13), word for word, in one place:
/// the page draws these sentences and never builds one of its own, and a test walks every stop reason.
/// </summary>
internal static class KafkaSearchText
{
    /// <summary>The stop reasons after which "Continue" reads on from the cursor.</summary>
    public static bool CanContinue(KafkaScanStopReason reason) => reason is
        KafkaScanStopReason.MatchLimit
        or KafkaScanStopReason.ByteBudget
        or KafkaScanStopReason.RecordBudget
        or KafkaScanStopReason.Deadline
        or KafkaScanStopReason.Cancelled
        or KafkaScanStopReason.OwnerBudget;

    /// <summary>The result banner: the one sentence that says why the search stopped.</summary>
    /// <param name="result">The finished scan.</param>
    /// <param name="limits">The limits it ran under.</param>
    public static string Stop(KafkaScanResult result, KafkaScanLimits limits)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(limits);

        return result.StopReason switch
        {
            KafkaScanStopReason.Completed when result.Matches > 0 => string.Create(
                CultureInfo.InvariantCulture,
                $"Read every offset in the window: {KafkaDisplay.Count(result.Records, "record")}, {KafkaDisplay.Bytes(result.Bytes)}, {KafkaDisplay.Count(result.Matches, "match", "matches")}."),
            KafkaScanStopReason.Completed => string.Create(
                CultureInfo.InvariantCulture,
                $"No match in the {KafkaDisplay.Count(result.Records, "record")} read from {KafkaDisplay.Count(result.Partitions.Count, "partition")}."),
            KafkaScanStopReason.MatchLimit => string.Create(
                CultureInfo.InvariantCulture,
                $"Stopped at {KafkaDisplay.Count(limits.MaxMatches, "match", "matches")}, the most a search keeps (KafkaStudioOptions.Limits.MaxMatches). Continue reads on from where it stopped."),
            KafkaScanStopReason.ByteBudget =>
                $"Stopped after reading {KafkaDisplay.Bytes(result.Bytes)}, this search's budget (KafkaStudioOptions.Limits.MaxBytesPerScan). Continue reads on from where it stopped.",
            KafkaScanStopReason.RecordBudget => string.Create(
                CultureInfo.InvariantCulture,
                $"Stopped after {KafkaDisplay.Count(result.Records, "record")}, this search's budget (KafkaStudioOptions.Limits.MaxRecordsPerScan). Continue reads on from where it stopped."),
            KafkaScanStopReason.OwnerBudget =>
                "Stopped: you have read your hourly allowance. Continue reads on from where it stopped, once the allowance frees up.",
            KafkaScanStopReason.Deadline =>
                $"Stopped after {WindowNote.Span(limits.MaxScanDuration)}, the longest a search runs (KafkaStudioOptions.Limits.MaxScanDuration). Continue reads on from where it stopped.",
            KafkaScanStopReason.Cancelled => "Stopped by you. Continue reads on from where it stopped.",
            KafkaScanStopReason.Shutdown => "Stopped: the application is shutting down.",
            KafkaScanStopReason.NotAuthorized => "Nothing was read: every partition refused READ.",
            KafkaScanStopReason.Unavailable => "Stopped: the cluster stopped answering.",
            KafkaScanStopReason.Failed => "The search failed. The application's log has the reason.",
            _ => "The search stopped.",
        };
    }

    /// <summary>
    /// The window repeated in both zones, so a visitor never guesses which zone a time is in: <c>10:00–11:00 Europe/Helsinki · 08:00Z–09:00Z</c>. A window
    /// that spans a day or more, or whose ends fall on different dates in either zone, shows the dates as well - 24 h must not read <c>13:48–13:48</c>.
    /// </summary>
    /// <param name="from">The window's start (UTC).</param>
    /// <param name="to">The window's end (UTC).</param>
    /// <param name="preference">The visitor's time zone.</param>
    public static string WindowLine(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo preference)
    {
        ArgumentNullException.ThrowIfNull(preference);

        CultureInfo c = CultureInfo.InvariantCulture;
        DateTimeOffset localFrom = TimeZoneInfo.ConvertTime(from, preference);
        DateTimeOffset localTo = TimeZoneInfo.ConvertTime(to, preference);
        bool dates = to - from >= TimeSpan.FromDays(1) || localFrom.Date != localTo.Date || from.UtcDateTime.Date != to.UtcDateTime.Date;
        string format = dates ? "yyyy-MM-dd HH:mm" : "HH:mm";

        return $"{localFrom.ToString(format, c)}–{localTo.ToString(format, c)} {preference.Id} · {from.UtcDateTime.ToString(format, c)}Z–{to.UtcDateTime.ToString(format, c)}Z";
    }

    /// <summary>A partition's end state in words, for the progress table and the caveats.</summary>
    /// <param name="state">How it ended.</param>
    public static string State(PartitionEndState state) => state switch
    {
        PartitionEndState.EndOfWindow => "read to the window's end",
        PartitionEndState.EndOfLog => "the log ended first",
        PartitionEndState.StoppedAtLso => "stopped at the last stable offset",
        PartitionEndState.Empty => "nothing to read",
        PartitionEndState.NotAuthorized => "READ refused",
        PartitionEndState.OffsetGap => "offsets were deleted",
        PartitionEndState.Stalled => "made no progress",
        PartitionEndState.NotReached => "not reached",
        _ => state.ToString(),
    };

    /// <summary>
    /// "What this result does not cover" (D-UI decision 13): every sentence that could make a negative untrue, in the order the design lists them.
    /// Empty only when nothing was left out - and a "no match" is never drawn without this list beside it.
    /// </summary>
    /// <param name="result">The finished scan.</param>
    public static IReadOnlyList<string> Caveats(KafkaScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        CultureInfo c = CultureInfo.InvariantCulture;
        List<string> caveats = [];

        foreach (WindowNote note in result.Notes)
        {
            Add(caveats, note.Text);
        }

        foreach (KafkaScanGap gap in result.Gaps)
        {
            Add(caveats, string.Create(c, $"{KafkaDisplay.Count(gap.Lost, "offset")} of {gap.Topic}/{gap.Partition} ({gap.FromOffset}–{gap.ToOffset}) {KafkaDisplay.Agree(gap.Lost, "was", "were")} removed by retention while the search read them."));
        }

        foreach (string sentence in result.UndecidableSentences)
        {
            Add(caveats, sentence);
        }

        if (result.OutsideWindow > 0)
        {
            Add(caveats, string.Create(c, $"{KafkaDisplay.Count(result.OutsideWindow, "record")} {KafkaDisplay.Agree(result.OutsideWindow, "was", "were")} read past the window's edges and left out by their own timestamp."));
        }

        if (result.NoTimestamp > 0)
        {
            Add(caveats, string.Create(c, $"{KafkaDisplay.Count(result.NoTimestamp, "record")} had no timestamp and {KafkaDisplay.Agree(result.NoTimestamp, "was", "were")} left out."));
        }

        foreach (KafkaPartitionResult partition in result.Partitions.Where(static x =>
                     x.State is PartitionEndState.NotAuthorized or PartitionEndState.OffsetGap or PartitionEndState.Stalled or PartitionEndState.NotReached))
        {
            Add(caveats, $"{partition.Topic}/{partition.Partition}: {State(partition.State)}");
        }

        foreach (KafkaTopicRefusal refusal in result.TopicRefusals)
        {
            Add(caveats, refusal.Text);
        }

        if (result.GroupRefusals > 0)
        {
            Add(caveats, string.Create(c, $"{KafkaDisplay.Count(result.GroupRefusals, "consumer")} could not use {KafkaDisplay.Agree(result.GroupRefusals, "its", "their")} group (expected without a group ACL; nothing was affected)."));
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
