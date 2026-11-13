using System.Globalization;

using KStudio.Core.Index;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;

namespace KStudio.Services;

/// <summary>
/// What the search page says about how a search ended and about what it did not cover (D-UI decisions 11 and 13), word for word, in one place:
/// the page draws these sentences and never builds one of its own, and a test walks every stop reason.
/// </summary>
internal static class KafkaSearchText
{
    /// <summary>The button beside a result the host's record index narrowed: the same search, without the index.</summary>
    public const string SearchWholeWindow = "Search the whole window";

    /// <summary>The form's checkbox, shown only where the host registered a record index.</summary>
    public const string IgnoreRecordIndex = "Ignore the host's record index";

    /// <summary>The line on the Settings page: the registered index's type, or <c>none</c>.</summary>
    /// <param name="name">The index's type name, or <see langword="null" /> when none is registered.</param>
    public static string RecordIndexLine(string? name) => "Record index: " + (name ?? "none");

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
            // A scan the host's record index narrowed (AGENTS.md D48) read the ranges it named, not the window: neither sentence may say otherwise.
            KafkaScanStopReason.Completed when result.Index is { IsNarrowed: true } index && result.Matches > 0 => string.Create(
                CultureInfo.InvariantCulture,
                $"Read every offset in the ranges the host's record index named ({KafkaIndexNarrowing.Offsets(index.OffsetsRead)}): {KafkaDisplay.Count(result.Records, "record")}, {KafkaDisplay.Bytes(result.Bytes)}, {KafkaDisplay.Count(result.Matches, "match", "matches")}. The rest of the window was not read."),
            KafkaScanStopReason.Completed when result.Index is { IsNarrowed: true } index => index.NoMatch(result.Records),
            KafkaScanStopReason.Completed when result.Matches > 0 => string.Create(
                CultureInfo.InvariantCulture,
                $"Read every offset in the window: {KafkaDisplay.Count(result.Records, "record")}, {KafkaDisplay.Bytes(result.Bytes)}, {KafkaDisplay.Count(result.Matches, "match", "matches")}."),
            KafkaScanStopReason.Completed => string.Create(
                CultureInfo.InvariantCulture,
                $"No match in the {KafkaDisplay.Count(result.Records, "record")} read from {KafkaDisplay.Count(result.Partitions.Count, "partition")}."),
            KafkaScanStopReason.MatchLimit => string.Create(
                CultureInfo.InvariantCulture,
                $"Stopped at {KafkaDisplay.Count(limits.MaxMatches, "match", "matches")}, the most a search keeps here. Continue reads on from where it stopped."),
            KafkaScanStopReason.ByteBudget =>
                $"Stopped after reading {KafkaDisplay.Bytes(result.Bytes)}, this search's budget. Continue reads on from where it stopped.",
            KafkaScanStopReason.RecordBudget => string.Create(
                CultureInfo.InvariantCulture,
                $"Stopped after {KafkaDisplay.Count(result.Records, "record")}, this search's budget. Continue reads on from where it stopped."),
            KafkaScanStopReason.OwnerBudget =>
                $"Stopped: you have read {KafkaStudioText.Allowance(result.SpentBudget)}. Continue reads on from where it stopped, once the allowance frees up.",
            KafkaScanStopReason.Deadline =>
                $"Stopped after {WindowNote.Span(limits.MaxScanDuration)}, the longest a search runs. Continue reads on from where it stopped.",
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

        return $"{localFrom.ToString(format, c)}–{localTo.ToString(format, c)} {ZoneLabel(preference, from)} · {from.UtcDateTime.ToString(format, c)}Z–{to.UtcDateTime.ToString(format, c)}Z";
    }

    /// <summary>
    /// How a time zone is named beside a time (#77): an IANA id with the UTC offset at <paramref name="at" /> (<c>Europe/Helsinki (UTC+02:00)</c>), otherwise the
    /// offset alone (<c>UTC+02:00</c>, or <c>UTC</c> at no offset). Never a Windows id such as <c>FLE Standard Time</c>, which reads as a display name it is not.
    /// </summary>
    /// <param name="zone">The zone.</param>
    /// <param name="at">The time the offset is read at.</param>
    public static string ZoneLabel(TimeZoneInfo zone, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(zone);

        TimeSpan offset = zone.GetUtcOffset(at);
        string shown = offset == TimeSpan.Zero
            ? "UTC"
            : "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        return zone.Id.Contains('/', StringComparison.Ordinal) ? zone.Id + " (" + shown + ")" : shown;
    }

    /// <summary>The page's line for a local time the zone's clocks skip, naming the zone as <see cref="ZoneLabel" /> does, never by a Windows id.</summary>
    /// <param name="shown">The time as the visitor typed it.</param>
    /// <param name="local">The time, kind unspecified.</param>
    /// <param name="zone">The zone it was typed in.</param>
    public static string SkippedLocalTime(string shown, DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        // The zone's offset before its clocks skipped this time: the standard offset, read an hour before the gap's start.
        DateTimeOffset at = new DateTimeOffset(DateTime.SpecifyKind(local - zone.BaseUtcOffset, DateTimeKind.Utc)).AddHours(-1);

        return $"{shown} does not exist in {ZoneLabel(zone, at)} (the clocks skip it).";
    }

    /// <summary>
    /// A partition's end state in words, told apart by what was read: a partition the scan stopped inside (a match limit, a budget, a stop) was started and is
    /// <c>stopped before its end</c>; one the scan never reached is <c>not reached</c>. Both are the engine's <see cref="PartitionEndState.NotReached" />.
    /// </summary>
    /// <param name="state">How it ended.</param>
    /// <param name="recordsRead">The records the scan read from it.</param>
    public static string State(PartitionEndState state, long recordsRead) =>
        state == PartitionEndState.NotReached && recordsRead > 0 ? "stopped before its end" : State(state);

    /// <summary>
    /// A partition's end state in a search the host's record index narrowed (AGENTS.md D48): the range is the one the index named, not the window's, and a
    /// partition it named nothing in was not read at all. A search the index did not narrow reads as always.
    /// </summary>
    /// <param name="state">How it ended.</param>
    /// <param name="recordsRead">The records the scan read from it.</param>
    /// <param name="topic">The partition's topic.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="narrowing">What the index did to the plan, or <see langword="null" /> when it was not asked.</param>
    public static string State(PartitionEndState state, long recordsRead, string topic, int partition, KafkaIndexNarrowing? narrowing)
    {
        if (narrowing is not { IsNarrowed: true })
        {
            return State(state, recordsRead);
        }

        return state switch
        {
            PartitionEndState.EndOfWindow => "read to the end of the range the record index named",
            PartitionEndState.Empty when !narrowing.Ranges.Any(x => x.Partition == partition && string.Equals(x.Topic, topic, StringComparison.Ordinal)) =>
                "not read: the record index named nothing here",
            _ => State(state, recordsRead),
        };
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
    /// <param name="leaveOutMissingTopics">Leaves out the sentences that only say a topic does not exist, for a page that draws one "Not found" line per topic (#77).</param>
    public static IReadOnlyList<string> Caveats(KafkaScanResult result, bool leaveOutMissingTopics = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        CultureInfo c = CultureInfo.InvariantCulture;
        List<string> caveats = [];

        foreach (WindowNote note in result.Notes.Where(note => !(leaveOutMissingTopics && IsMissingTopicNote(note))))
        {
            Add(caveats, note.Text);
        }

        // What a pruned search did not read: a "no match" is only about the partitions the key maps to (D-PAYLOAD decision 7.6).
        foreach (string sentence in result.PruningSentences)
        {
            Add(caveats, sentence);
        }

        // What a search narrowed by the host's record index did not read, beside its matches and its negatives alike (AGENTS.md D48).
        if (result.Index?.Caveat is { } narrowed)
        {
            Add(caveats, narrowed);
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
            Add(caveats, $"{partition.Topic}/{partition.Partition}: {State(partition.State, partition.Records)}");
        }

        foreach (KafkaTopicRefusal refusal in result.TopicRefusals.Where(refusal => !(leaveOutMissingTopics && refusal.Missing)))
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

    /// <summary>
    /// Whether a window note only repeats that a topic does not exist: the configuration of a missing topic cannot be read, and its note starts with the
    /// same "Not found" sentence as the topic's refusal. A page that draws one "Not found" line per missing topic leaves these out (#77).
    /// </summary>
    /// <param name="note">The note.</param>
    public static bool IsMissingTopicNote(WindowNote note) =>
        note is WindowNote.ConfigNotReadable { Error: KStudio.Core.Wire.WireError.NotFound };
}
