using System.Globalization;

using KStudio.Core.Scanning;
using KStudio.Core.Wire;

namespace KStudio.Core.Windows;

/// <summary>A half-open time window <c>[From, To)</c> (decision 6): both UTC, and <c>From &lt; To</c>.</summary>
/// <param name="From">The start, inclusive.</param>
/// <param name="To">The end, exclusive.</param>
internal readonly record struct KafkaWindow(DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>What is wrong with this window, or <see langword="null" />.</summary>
    public string? Problem =>
        From.Offset != TimeSpan.Zero || To.Offset != TimeSpan.Zero ? "The window's times must be UTC."
        : From >= To ? "The window must start before it ends."
        : null;
}

/// <summary>Which records a window may include.</summary>
internal enum KafkaIsolation
{
    /// <summary>Only committed records: a partition stops at its last stable offset.</summary>
    Committed,

    /// <summary>Uncommitted records too: a record may belong to a transaction that is later aborted.</summary>
    IncludeUncommitted,
}

/// <summary>What a topic's timestamps mean.</summary>
internal enum KafkaTimestampKind
{
    /// <summary>The producer's time; records may be out of order, so the end gets a margin.</summary>
    CreateTime,

    /// <summary>The broker's time; monotonic per partition, so the end gets no margin.</summary>
    LogAppendTime,

    /// <summary>The topic's configuration could not be read; treated as <see cref="CreateTime" />, so never fewer records are read.</summary>
    Unknown,
}

/// <summary>
/// Something a visitor must be told about a window (decision 6): each is a value with its wording, so that "no match" is never said
/// without what could make it untrue.
/// </summary>
internal abstract record WindowNote
{
    private WindowNote()
    {
    }

    /// <summary>The sentence a visitor reads.</summary>
    public abstract string Text { get; }

    /// <summary>Retention or <c>DeleteRecords</c> removed the head of the log before the window's start.</summary>
    /// <param name="Topic">The topic.</param>
    /// <param name="Partition">The partition.</param>
    /// <param name="EarliestRetained">The log start.</param>
    /// <param name="EarliestTimestamp">The timestamp of the first retained record, when known.</param>
    public sealed record RetentionGap(string Topic, int Partition, long EarliestRetained, DateTimeOffset? EarliestTimestamp) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => string.Create(
            CultureInfo.InvariantCulture,
            $"Records before {(EarliestTimestamp is { } at ? Time(at) : "offset " + EarliestRetained.ToString(CultureInfo.InvariantCulture))} are gone (retention): the earliest retained record is offset {EarliestRetained}");
    }

    /// <summary>A key search reads only the partitions its key maps to, after sampling the window proved the topic's partitioner (D-PAYLOAD decision 7.5).</summary>
    /// <param name="Topic">The topic.</param>
    /// <param name="Partitions">The partitions read.</param>
    /// <param name="Partitioner">The partitioner's name.</param>
    /// <param name="Checked">How many keyed records the sample checked.</param>
    public sealed record PartitionsPruned(string Topic, IReadOnlyList<int> Partitions, string Partitioner, int Checked) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => string.Create(
            CultureInfo.InvariantCulture,
            $"{(Partitions.Count == 1 ? "Only partition" : "Only partitions")} {KafkaTopicPruning.List(Partitions)} of {Topic} {(Partitions.Count == 1 ? "is" : "are")} read: {Partitioner} maps the key to {(Partitions.Count == 1 ? "it" : "them")}, verified on {Checked:N0} records of this window.");
    }

    /// <summary>A partition stops at its last stable offset because an open transaction holds later offsets.</summary>
    /// <param name="Topic">The topic.</param>
    /// <param name="Partition">The partition.</param>
    /// <param name="Lso">The last stable offset.</param>
    /// <param name="HighWatermark">The high watermark.</param>
    public sealed record StoppedAtLso(string Topic, int Partition, long Lso, long HighWatermark) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => string.Create(
            CultureInfo.InvariantCulture,
            $"Stopped at offset {Lso}: an open transaction holds offsets up to {HighWatermark}. Include uncommitted records to read past it.");
    }

    /// <summary>The window includes uncommitted records.</summary>
    public sealed record IncludesUncommitted : WindowNote
    {
        /// <inheritdoc />
        public override string Text => "Includes uncommitted records: a record may belong to a transaction that is later aborted";
    }

    /// <summary>A <c>CreateTime</c> window ends a margin past its end, so a record stamped earlier than the margin allows may be missed.</summary>
    /// <param name="Topic">The topic.</param>
    /// <param name="Partition">The partition.</param>
    /// <param name="Margin">The margin.</param>
    /// <param name="EndOffset">The end offset of the partition's range.</param>
    public sealed record CreateTimeMargin(string Topic, int Partition, TimeSpan Margin, long EndOffset) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => Margin > TimeSpan.Zero
            ? string.Create(CultureInfo.InvariantCulture, $"A record stamped more than {Span(Margin)} before a later one is not found after offset {EndOffset}")
            : string.Create(CultureInfo.InvariantCulture, $"No margin is read past the window's end: a record stamped earlier than one before offset {EndOffset} but appended after it is not found");
    }

    /// <summary>The partitions a window reads are the ones its topics had when it was resolved.</summary>
    /// <param name="ResolvedAt">When the window was resolved.</param>
    public sealed record PartitionsFixed(DateTimeOffset ResolvedAt) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => "A partition added after " + Time(ResolvedAt) + " is not read: a window reads the partitions its topics had when it was resolved";
    }

    /// <summary>A partition the paused tail's cursor does not name: it was added after the tail started, so a resumed tail reads it from its end (D-M5 decision 3.1).</summary>
    /// <param name="Topic">The topic.</param>
    /// <param name="Partition">The partition.</param>
    public sealed record PartitionAddedToTail(string Topic, int Partition) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => string.Create(
            CultureInfo.InvariantCulture,
            $"Partition {Partition} of {Topic} was added after the tail started; it is read from its end.");
    }

    /// <summary>A paused tail's cursor lies past the end of the partition (recreated or truncated): the resumed tail reads it from its end, and says so (D-M5 decision 3.1).</summary>
    /// <param name="Topic">The topic.</param>
    /// <param name="Partition">The partition.</param>
    /// <param name="HighWatermark">Where it is read from.</param>
    public sealed record TailPositionPastEnd(string Topic, int Partition, long HighWatermark) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => string.Create(
            CultureInfo.InvariantCulture,
            $"Partition {Partition} of {Topic} is shorter than where the tail paused (recreated or truncated); it is read from offset {HighWatermark}.");
    }

    /// <summary>The topic is compacted.</summary>
    /// <param name="Topic">The topic.</param>
    public sealed record CompactedTopic(string Topic) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => "Compacted topic: a time-window search can miss superseded keys and sees tombstones";
    }

    /// <summary>The window reaches into tiered storage.</summary>
    /// <param name="Topic">The topic.</param>
    public sealed record TieredStorage(string Topic) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => "Older records are in tiered storage: reading them is slower and may cost more";
    }

    /// <summary>The topic's configuration could not be read, so its timestamp type is assumed.</summary>
    /// <param name="Topic">The topic.</param>
    /// <param name="Error">Why.</param>
    public sealed record ConfigNotReadable(string Topic, WireError Error) : WindowNote
    {
        /// <inheritdoc />
        public override string Text =>
            Catalog.KafkaAccessText.Describe(Error) + ". The topic's timestamp type is assumed to be CreateTime, so a margin is read past the window's end";
    }

    /// <summary>The broker does not support something the window would like to ask.</summary>
    /// <param name="Api">What.</param>
    public sealed record ApiUnsupported(string Api) : WindowNote
    {
        /// <inheritdoc />
        public override string Text => "This broker does not support " + Api + ", so the window does not use it";
    }

    /// <summary>A time as a note writes it.</summary>
    /// <param name="at">The time.</param>
    internal static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A span as a note writes it: <c>2 min</c>, <c>30 s</c>, <c>1 h</c>.</summary>
    /// <param name="span">The span.</param>
    internal static string Span(TimeSpan span) =>
        span.TotalHours >= 1 && span.Ticks % TimeSpan.TicksPerHour == 0 ? span.TotalHours.ToString("0", CultureInfo.InvariantCulture) + " h"
        : span.TotalMinutes >= 1 && span.Ticks % TimeSpan.TicksPerMinute == 0 ? span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min"
        : span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";
}

/// <summary>One partition's range to read (decision 6): <c>[Start, End)</c>, fixed for the whole scan.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition.</param>
/// <param name="Start">The first offset to read: the earliest offset whose timestamp is at or after the window's start. Exact.</param>
/// <param name="End">One past the last offset to read.</param>
/// <param name="LogStart">The log start when the window was resolved.</param>
/// <param name="HighWatermark">The high watermark.</param>
/// <param name="Lso">The last stable offset; <see langword="null" /> when uncommitted records are included.</param>
/// <param name="Notes">What a visitor must be told about this partition.</param>
internal sealed record PartitionRange(
    string Topic,
    int Partition,
    long Start,
    long End,
    long LogStart,
    long HighWatermark,
    long? Lso,
    IReadOnlyList<WindowNote> Notes)
{
    /// <summary>The partition.</summary>
    public WirePartition Key => new(Topic, Partition);

    /// <summary>Whether there is nothing to read.</summary>
    public bool IsEmpty => End <= Start;
}

/// <summary>A topic in a resolved window.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="TimestampKind">What its timestamps mean.</param>
/// <param name="Refusal">Why the topic has no ranges: not listed (<c>NotFound</c>), refused, or the cluster failed.</param>
/// <param name="Notes">What a visitor must be told about the topic.</param>
internal sealed record TopicWindow(string Topic, KafkaTimestampKind TimestampKind, WireError? Refusal, IReadOnlyList<WindowNote> Notes);

/// <summary>A window resolved to offsets, per partition, before anything is read.</summary>
/// <param name="Cluster">The cluster's key.</param>
/// <param name="Window">The window.</param>
/// <param name="Isolation">The isolation.</param>
/// <param name="Margin">The margin past the end for <c>CreateTime</c> topics.</param>
/// <param name="Topics">The topics.</param>
/// <param name="Ranges">The partitions' ranges, for every topic that is not refused.</param>
/// <param name="Notes">What applies to the whole window.</param>
internal sealed record ResolvedWindow(
    string Cluster,
    KafkaWindow Window,
    KafkaIsolation Isolation,
    TimeSpan Margin,
    IReadOnlyList<TopicWindow> Topics,
    IReadOnlyList<PartitionRange> Ranges,
    IReadOnlyList<WindowNote> Notes)
{
    /// <summary>Every note: the window's, each topic's and each partition's.</summary>
    public IEnumerable<WindowNote> AllNotes => Notes.Concat(Topics.SelectMany(static x => x.Notes)).Concat(Ranges.SelectMany(static x => x.Notes));
}
