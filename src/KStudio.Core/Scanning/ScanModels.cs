using System.Globalization;
using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Windows;

namespace KStudio.Core.Scanning;

/// <summary>Who a scan belongs to. Opaque: KStudio decides who an owner is (a visitor, a session), and the engine only counts per key.</summary>
/// <param name="Key">The owner's key. It is never logged.</param>
internal sealed record KafkaScanOwner(string Key);

/// <summary>
/// Limits a profile or a visitor lowers for one scan (decision 9.4): only a lower value is accepted, and the engine applies the minimum.
/// The process-wide limits (concurrent scans, the byte rate, the owner's hourly budget) are the host's and cannot be lowered per scan.
/// </summary>
internal sealed record KafkaScanLowering
{
    /// <summary>A lower <c>MaxBytesPerScan</c>.</summary>
    public long? MaxBytesPerScan { get; init; }

    /// <summary>A lower <c>MaxRecordsPerScan</c>.</summary>
    public long? MaxRecordsPerScan { get; init; }

    /// <summary>A lower <c>MaxScanDuration</c>.</summary>
    public TimeSpan? MaxScanDuration { get; init; }

    /// <summary>A lower <c>MaxMatches</c>.</summary>
    public int? MaxMatches { get; init; }

    /// <summary>A lower <c>MaxMatchBytes</c>.</summary>
    public int? MaxMatchBytes { get; init; }

    /// <summary>A lower <c>ConfirmAboveBytes</c>.</summary>
    public long? ConfirmAboveBytes { get; init; }

    /// <summary>A lower <c>ConfirmAboveRecords</c>.</summary>
    public long? ConfirmAboveRecords { get; init; }

    /// <summary>
    /// The host's limits lowered by this. A value above the host's throws, because nothing raises a limit, and so does a value below the
    /// lowest decision 9.4 allows (a negative duration would otherwise fail later, with a scan slot already taken).
    /// </summary>
    /// <param name="host">The host's limits (a copy the caller owns).</param>
    public KafkaScanLimits Apply(KafkaScanLimits host)
    {
        const long kib = 1024;
        const long mib = 1024 * kib;

        host.MaxBytesPerScan = Lowered(MaxBytesPerScan, host.MaxBytesPerScan, mib, nameof(MaxBytesPerScan));
        host.MaxRecordsPerScan = Lowered(MaxRecordsPerScan, host.MaxRecordsPerScan, 1_000L, nameof(MaxRecordsPerScan));
        host.MaxScanDuration = Lowered(MaxScanDuration, host.MaxScanDuration, TimeSpan.FromSeconds(5), nameof(MaxScanDuration));
        host.MaxMatches = Lowered(MaxMatches, host.MaxMatches, 1, nameof(MaxMatches));
        host.MaxMatchBytes = Lowered(MaxMatchBytes, host.MaxMatchBytes, (int) kib, nameof(MaxMatchBytes));
        host.ConfirmAboveBytes = Lowered(ConfirmAboveBytes, host.ConfirmAboveBytes, 0L, nameof(ConfirmAboveBytes));
        host.ConfirmAboveRecords = Lowered(ConfirmAboveRecords, host.ConfirmAboveRecords, 0L, nameof(ConfirmAboveRecords));
        return host;
    }

    private static T Lowered<T>(T? wanted, T host, T minimum, string name)
        where T : struct, IComparable<T>
    {
        if (wanted is not { } value)
        {
            return host;
        }

        if (value.CompareTo(host) > 0)
        {
            throw new ArgumentException($"{name} cannot be raised above the host's limit; a profile or a visitor can only lower a limit.", nameof(wanted));
        }

        if (value.CompareTo(minimum) < 0)
        {
            throw new ArgumentException($"{name} is below the lowest value the engine accepts ({minimum}).", nameof(wanted));
        }

        return value;
    }
}

/// <summary>One topic of a scan whose topics are searched with different predicates and grants (the timeline, D42).</summary>
/// <param name="Topic">The topic.</param>
/// <param name="PredicateText">The search for this topic.</param>
/// <param name="Grant">What this topic is read under: it includes <see cref="KafkaReadGrant.Envelope" /> and is included in the request's grant, which is the most any topic may read.</param>
internal sealed record KafkaTopicQuery(KafkaTopicName Topic, string PredicateText, KafkaReadGrant Grant);

/// <summary>What to scan (decision 9.1).</summary>
/// <param name="Cluster">The cluster's key.</param>
/// <param name="Topics">The topics, already authorized by the host studio.</param>
/// <param name="Window">The half-open time window.</param>
/// <param name="PredicateText">What the visitor typed; empty for every record in the window.</param>
/// <param name="Grant">What the visitor may read.</param>
/// <param name="Isolation">Whether uncommitted records are included.</param>
/// <param name="CreateTimeMargin">The margin past the window's end for <c>CreateTime</c> topics; the engine's default when <see langword="null" />.</param>
/// <param name="ContinueFrom">The cursor of an earlier scan: this one reads exactly the offsets that one left.</param>
/// <param name="Lower">Limits lowered for this scan.</param>
/// <param name="DisablePruning">Read every partition even where a verified partitioner would allow reading fewer ("Search all partitions", D-PAYLOAD decision 7.6).</param>
/// <param name="PerTopic">A predicate and a grant for each topic (D42): <paramref name="PredicateText" /> is then <see langword="null" /> and <paramref name="Topics" /> are exactly these topics, in order. One scan still: one consumer, one slot, one estimate and one byte budget.</param>
internal sealed record KafkaScanRequest(
    string Cluster,
    IReadOnlyList<KafkaTopicName> Topics,
    KafkaWindow Window,
    string? PredicateText,
    KafkaReadGrant Grant,
    KafkaIsolation Isolation = KafkaIsolation.Committed,
    TimeSpan? CreateTimeMargin = null,
    KafkaScanCursor? ContinueFrom = null,
    KafkaScanLowering? Lower = null,
    bool DisablePruning = false,
    IReadOnlyList<KafkaTopicQuery>? PerTopic = null);

/// <summary>Why a scan stopped (decision 9.5): exactly one.</summary>
internal enum KafkaScanStopReason
{
    /// <summary>Every partition finished.</summary>
    Completed,

    /// <summary><c>MaxMatches</c> were kept.</summary>
    MatchLimit,

    /// <summary><c>MaxBytesPerScan</c> were read.</summary>
    ByteBudget,

    /// <summary><c>MaxRecordsPerScan</c> were read.</summary>
    RecordBudget,

    /// <summary>The owner's rolling hourly bytes ran out.</summary>
    OwnerBudget,

    /// <summary><c>MaxScanDuration</c> elapsed.</summary>
    Deadline,

    /// <summary>The caller stopped it.</summary>
    Cancelled,

    /// <summary>The handle was disposed while the scan ran (the circuit is gone).</summary>
    CallerGone,

    /// <summary>The host is stopping.</summary>
    Shutdown,

    /// <summary>Every partition refused <c>READ</c>.</summary>
    NotAuthorized,

    /// <summary>The cluster stopped answering for <c>PartitionStallTimeout</c> with no partition progressing.</summary>
    Unavailable,

    /// <summary>A fatal client error or an unexpected exception.</summary>
    Failed,
}

/// <summary>How a partition's part of a scan ended (decision 9.5).</summary>
internal enum PartitionEndState
{
    /// <summary>The partition's range was read to its end.</summary>
    EndOfWindow,

    /// <summary>The log ended before the window's end.</summary>
    EndOfLog,

    /// <summary>An open transaction holds later offsets: the partition stops at the last stable offset.</summary>
    StoppedAtLso,

    /// <summary>There was nothing to read.</summary>
    Empty,

    /// <summary>The broker refused <c>READ</c>.</summary>
    NotAuthorized,

    /// <summary>Offsets were deleted and the partition could not go on past them.</summary>
    OffsetGap,

    /// <summary>The partition made no progress for <c>PartitionStallTimeout</c>.</summary>
    Stalled,

    /// <summary>The scan stopped before the partition finished.</summary>
    NotReached,
}

/// <summary>Why a start was refused (decision 9.7). A refusal is a value, never a queue.</summary>
internal enum KafkaScanRefusal
{
    /// <summary>The process runs its most concurrent scans.</summary>
    ProcessBusy,

    /// <summary>The owner runs its most concurrent scans.</summary>
    OwnerBusy,

    /// <summary>The owner has read its bytes for the hour.</summary>
    OwnerBudgetSpent,

    /// <summary>The scan is above the confirmation threshold and the confirmation did not travel with the call.</summary>
    NeedsConfirmation,

    /// <summary>The plan has errors.</summary>
    PredicateInvalid,

    /// <summary>The host is stopping.</summary>
    ShuttingDown,

    /// <summary>The cluster's profile forbids this read: a live tail on a <c>Production</c> cluster, even when the capability is granted (D47).</summary>
    ProfileForbids,
}

/// <summary>What <c>Start</c> returns: a handle, or the reason there is none.</summary>
internal readonly struct KafkaScanStart
{
    private KafkaScanStart(KafkaScanHandle? handle, KafkaScanRefusal? refusal)
    {
        Handle = handle;
        Refusal = refusal;
    }

    /// <summary>The running (or already complete) scan; <see langword="null" /> when refused.</summary>
    public KafkaScanHandle? Handle { get; }

    /// <summary>Why it was refused.</summary>
    public KafkaScanRefusal? Refusal { get; }

    /// <summary>Whether a scan started.</summary>
    public bool Started => Handle is not null;

    /// <summary>A started scan.</summary>
    /// <param name="handle">The handle.</param>
    public static KafkaScanStart Of(KafkaScanHandle handle) => new(handle, null);

    /// <summary>A refusal.</summary>
    /// <param name="refusal">Why.</param>
    public static KafkaScanStart Refused(KafkaScanRefusal refusal) => new(null, refusal);
}

/// <summary>The engine's estimate before it reads (decision 9.10).</summary>
/// <param name="Offsets">The offsets in the ranges: "about", an upper bound, since offsets are not records.</param>
/// <param name="MeanRecordBytes">The sampled mean record size, when sampling gave one.</param>
/// <param name="Bytes">Offsets times the mean; <see langword="null" /> when the size is unknown.</param>
/// <param name="Partitions">How many partitions would be read.</param>
/// <param name="NeedsConfirmation">Whether the scan is above the confirmation threshold.</param>
/// <param name="Basis">How the estimate was made, in words.</param>
internal sealed record KafkaScanEstimate(long Offsets, long? MeanRecordBytes, long? Bytes, int Partitions, bool NeedsConfirmation, string Basis);

/// <summary>Everything a scan needs, worked out before anything is read: the resolved window, the predicate per topic, the estimate.</summary>
internal sealed class KafkaScanPlan
{
    /// <summary>Creates a plan.</summary>
    /// <param name="request">The request.</param>
    /// <param name="window">The resolved window; <see langword="null" /> when the request was refused before it was resolved.</param>
    /// <param name="ranges">The ranges to read: the window's, or the cursor's.</param>
    /// <param name="topics">The compiled topic contexts, by topic.</param>
    /// <param name="errors">What is wrong with the search text.</param>
    /// <param name="verdicts">How each clause runs on each topic.</param>
    /// <param name="estimate">The estimate.</param>
    /// <param name="limits">The limits this scan runs under.</param>
    /// <param name="auditText">The canonical predicate with redacted literals hidden.</param>
    /// <param name="pruning">What each topic's partitioner claim came to (decision 7.4); empty when pruning was not asked of any topic.</param>
    public KafkaScanPlan(
        KafkaScanRequest request,
        ResolvedWindow? window,
        IReadOnlyList<PartitionRange> ranges,
        IReadOnlyDictionary<string, TopicScanContext> topics,
        IReadOnlyList<PredicateError> errors,
        IReadOnlyList<ClauseVerdict> verdicts,
        KafkaScanEstimate estimate,
        KafkaScanLimits limits,
        string auditText,
        IReadOnlyList<KafkaTopicPruning>? pruning = null)
    {
        Request = request;
        Window = window;
        Ranges = ranges;
        Topics = topics;
        Errors = errors;
        Verdicts = verdicts;
        Estimate = estimate;
        Limits = limits;
        AuditText = auditText;
        Pruning = pruning ?? [];
    }

    /// <summary>The request.</summary>
    public KafkaScanRequest Request { get; }

    /// <summary>The resolved window and its notes; <see langword="null" /> when the text was invalid.</summary>
    public ResolvedWindow? Window { get; }

    /// <summary>The ranges the scan reads.</summary>
    public IReadOnlyList<PartitionRange> Ranges { get; }

    /// <summary>The compiled topics.</summary>
    public IReadOnlyDictionary<string, TopicScanContext> Topics { get; }

    /// <summary>What is wrong with the text, for the visitor. No message contains a literal.</summary>
    public IReadOnlyList<PredicateError> Errors { get; }

    /// <summary>How each clause runs on each topic.</summary>
    public IReadOnlyList<ClauseVerdict> Verdicts { get; }

    /// <summary>The estimate.</summary>
    public KafkaScanEstimate Estimate { get; }

    /// <summary>The limits this scan runs under: the host's, lowered by the request.</summary>
    public KafkaScanLimits Limits { get; }

    /// <summary>The canonical text of the search, a literal on a redacted field shown as <c>&lt;redacted&gt;</c>: for the audit and for a search link.</summary>
    public string AuditText { get; }

    /// <summary>Whether <c>Start</c> can succeed (before limits and confirmation).</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Whether judging a record can read its value on any topic: the search is audited as one that reads values.</summary>
    public bool ReadsValues => Topics.Values.Any(static x => x.Predicate.ReadsValues && x.Grant.HasFlag(KafkaReadGrant.Payload));

    /// <summary>
    /// What each topic's declared partitioner came to, for the topics it was asked of: pruned (and to which partitions), or why not. The
    /// plan's <see cref="Ranges" /> keep only the pruned partitions of a pruned topic.
    /// </summary>
    public IReadOnlyList<KafkaTopicPruning> Pruning { get; }

    /// <summary>Everything a visitor must be told about the window, the topics and the partitions, the pruned partitions included.</summary>
    public IEnumerable<WindowNote> Notes =>
        (Window?.AllNotes ?? []).Concat(
            Pruning.Where(static x => x.IsPruned).Select(static x => (WindowNote) new WindowNote.PartitionsPruned(x.Topic, x.Partitions, x.Partitioner!, x.Checked)));
}

/// <summary>One partition's progress.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition.</param>
/// <param name="Start">The first offset of its range.</param>
/// <param name="End">One past the last.</param>
/// <param name="Position">The next offset to read.</param>
/// <param name="Percent">How far the partition is, 0 to 100.</param>
/// <param name="Records">Records read.</param>
/// <param name="Bytes">Bytes read.</param>
/// <param name="Matches">Matches found.</param>
/// <param name="Undecidable">Records the search could not decide.</param>
/// <param name="OutsideWindow">Records read whose own timestamp is outside the window.</param>
/// <param name="State">How it ended, when it has.</param>
internal sealed record KafkaPartitionProgress(
    string Topic,
    int Partition,
    long Start,
    long End,
    long Position,
    double Percent,
    long Records,
    long Bytes,
    long Matches,
    long Undecidable,
    long OutsideWindow,
    PartitionEndState? State);

/// <summary>A scan's progress (decision 9.6), copied under the scan thread's lock.</summary>
/// <param name="Partitions">Per partition.</param>
/// <param name="Records">Records read.</param>
/// <param name="Bytes">Bytes read.</param>
/// <param name="Matches">Matches kept.</param>
/// <param name="Undecidable">Records the search could not decide.</param>
/// <param name="OutsideWindow">Records outside the window by their own timestamp.</param>
/// <param name="Elapsed">Time since the scan began.</param>
/// <param name="BytesPerSecond">Bytes read per second so far.</param>
/// <param name="StopReason">Why it stopped, once it has.</param>
/// <param name="Version">Increases with every change, so a batcher can tell nothing changed.</param>
internal sealed record KafkaScanProgress(
    IReadOnlyList<KafkaPartitionProgress> Partitions,
    long Records,
    long Bytes,
    long Matches,
    long Undecidable,
    long OutsideWindow,
    TimeSpan Elapsed,
    double BytesPerSecond,
    KafkaScanStopReason? StopReason,
    long Version);

/// <summary>Records a partition lost to retention while the scan read it (decision 6.6).</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition.</param>
/// <param name="FromOffset">The first offset lost.</param>
/// <param name="ToOffset">The last offset lost.</param>
/// <param name="Lost">How many offsets.</param>
/// <param name="LogStart">The log start the partition reported when the loss was found, when it was read.</param>
internal sealed record KafkaScanGap(string Topic, int Partition, long FromOffset, long ToOffset, long Lost, long? LogStart = null);

/// <summary>One partition's part of a finished scan.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition.</param>
/// <param name="Start">The first offset of its range.</param>
/// <param name="End">One past the last.</param>
/// <param name="Next">The next offset that was not read.</param>
/// <param name="State">How it ended.</param>
/// <param name="Records">Records read.</param>
/// <param name="Bytes">Bytes read.</param>
/// <param name="Matches">Matches found.</param>
/// <param name="Undecidable">Records the search could not decide.</param>
/// <param name="OutsideWindow">Records outside the window by their own timestamp.</param>
internal sealed record KafkaPartitionResult(
    string Topic,
    int Partition,
    long Start,
    long End,
    long Next,
    PartitionEndState State,
    long Records,
    long Bytes,
    long Matches,
    long Undecidable,
    long OutsideWindow);

/// <summary>A topic the scan could not read at all, with why.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Text">The sentence.</param>
internal sealed record KafkaTopicRefusal(string Topic, string Text);

/// <summary>
/// A finished scan: why it stopped, what it read, and everything that could make "no match" untrue (decision 9.5): records lost to
/// retention, records the search could not decide, partitions that stopped early.
/// </summary>
internal sealed record KafkaScanResult(
    Guid ScanId,
    KafkaScanStopReason StopReason,
    TimeSpan Elapsed,
    IReadOnlyList<KafkaPartitionResult> Partitions,
    long Records,
    long Bytes,
    long Matches,
    long Undecidable,
    long OutsideWindow,
    long NoTimestamp,
    int GroupRefusals,
    IReadOnlyDictionary<KafkaUnknownReason, long> UndecidableByReason,
    IReadOnlyList<KafkaScanGap> Gaps,
    IReadOnlyList<WindowNote> Notes,
    IReadOnlyList<KafkaTopicRefusal> TopicRefusals,
    KafkaScanCursor Cursor,
    DecodeCounters Decodes,
    DecodeCounters MatchDecodes)
{
    /// <summary>What each topic's declared partitioner came to for this scan (from the plan); a topic that was pruned was read in some of its partitions only.</summary>
    public IReadOnlyList<KafkaTopicPruning> Pruning { get; init; } = [];

    /// <summary>One sentence per pruned topic for the result's caveats: "This search read only partition 3 of 6 of orders (murmur2, verified on 1,002 records)".</summary>
    public IEnumerable<string> PruningSentences => Pruning.Where(static x => x.IsPruned).Select(static x => x.Caveat!);

    /// <summary>The audit message's suffix for the topics that were pruned (<c>; pruned to partition 3 of orders</c>); empty when none were.</summary>
    public string PruningAuditText => string.Concat(Pruning.Where(static x => x.IsPruned).Select(static x => x.AuditSuffix));

    /// <summary>Sentences saying what the undecidable records could not be, for the line beside a negative.</summary>
    public IEnumerable<string> UndecidableSentences =>
        UndecidableByReason.Where(static x => x.Value > 0).Select(static x => string.Create(CultureInfo.InvariantCulture, $"{x.Value:N0} {(x.Value == 1 ? "record" : "records")} {KafkaUnknownReasonText.Describe(x.Key, one: x.Value == 1)}"));
}

/// <summary>What <see cref="KafkaScanHandle.ReadBatchesAsync" /> yields, at most four times a second (decision 9.7).</summary>
/// <param name="NewMatches">The matches added since the last batch.</param>
/// <param name="Progress">A copy of the progress.</param>
/// <param name="Final">The result, in the last batch.</param>
internal sealed record KafkaScanBatch(IReadOnlyList<KafkaMatch> NewMatches, KafkaScanProgress Progress, KafkaScanResult? Final);

/// <summary>One partition's place in a cursor.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition.</param>
/// <param name="Next">The next offset to read.</param>
/// <param name="End">One past the last offset of the original range; "continue" does not re-resolve it.</param>
internal sealed record KafkaCursorPosition(string Topic, int Partition, long Next, long End);

/// <summary>
/// Where a scan stopped, exactly (decision 9.9): per partition the next offset and the end of the range. "Continue from here" plans with
/// these as starts and ends, so the two scans together read exactly the offsets one scan would have.
/// </summary>
internal sealed class KafkaScanCursor
{
    /// <summary>The text form is bounded at 4 KB, for the address.</summary>
    public const int MaxTextLength = 4_096;

    private const string Prefix = "v1;";

    /// <summary>Creates a cursor.</summary>
    /// <param name="positions">The partitions that have more to read.</param>
    public KafkaScanCursor(IReadOnlyList<KafkaCursorPosition> positions) => Positions = positions;

    /// <summary>The partitions that have more to read.</summary>
    public IReadOnlyList<KafkaCursorPosition> Positions { get; }

    /// <summary>
    /// What the scan that made this cursor proved about its pruned topics (D-PAYLOAD decision 7): a continued scan keeps the claim and does not
    /// sample again. Held in memory only; the text form carries positions alone, so a cursor read back from text has none.
    /// </summary>
    public IReadOnlyList<KafkaTopicPruning> Pruning { get; init; } = [];

    /// <summary>Whether nothing is left to read.</summary>
    public bool IsEmpty => Positions.Count == 0;

    /// <summary>Writes <c>v1;{topic}:{partition}:{next}:{end},…</c>; false when the text would exceed 4 KB.</summary>
    /// <param name="text">The text.</param>
    public bool TryFormat(out string text)
    {
        StringBuilder builder = new(Prefix);

        for (int i = 0; i < Positions.Count; i++)
        {
            KafkaCursorPosition p = Positions[i];

            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(CultureInfo.InvariantCulture, $"{p.Topic}:{p.Partition}:{p.Next}:{p.End}");
        }

        text = builder.ToString();
        return text.Length <= MaxTextLength;
    }

    /// <summary>Reads the text form; false for anything that is not one.</summary>
    /// <param name="text">The text.</param>
    /// <param name="cursor">The cursor.</param>
    public static bool TryParse(string? text, out KafkaScanCursor cursor)
    {
        cursor = new KafkaScanCursor([]);

        if (text is null || text.Length > MaxTextLength || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        List<KafkaCursorPosition> positions = [];
        HashSet<(string, int)> seen = [];

        foreach (string part in text[Prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = part.Split(':');

            if (fields.Length != 4
                || !KafkaTopicName.IsValid(fields[0])
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int partition)
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long next)
                || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long end)
                || next > end
                || !seen.Add((fields[0], partition)))
            {
                return false;
            }

            positions.Add(new KafkaCursorPosition(fields[0], partition, next, end));
        }

        cursor = new KafkaScanCursor(positions);
        return true;
    }
}
