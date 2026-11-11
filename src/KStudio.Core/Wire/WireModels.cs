namespace KStudio.Core.Wire;

/// <summary>A topic's partition.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition's number.</param>
internal readonly record struct WirePartition(string Topic, int Partition)
{
    /// <inheritdoc />
    public override string ToString() => Topic + "[" + Partition + "]";
}

/// <summary>A position in a partition. An assign carries only explicit, non-negative offsets (decision 3.1).</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition's number.</param>
/// <param name="Offset">The offset.</param>
internal readonly record struct WirePartitionOffset(string Topic, int Partition, long Offset)
{
    /// <summary>The partition.</summary>
    public WirePartition Key => new(Topic, Partition);
}

/// <summary>The isolation level a read uses.</summary>
internal enum WireIsolation
{
    /// <summary>Only committed records; the end is the last stable offset.</summary>
    ReadCommitted,

    /// <summary>Every record, including those of open and aborted transactions; the end is the high watermark.</summary>
    ReadUncommitted,
}

/// <summary>The kinds of offset a list-offsets call can ask for.</summary>
internal enum WireOffsetSpecKind
{
    /// <summary>The log start.</summary>
    Earliest,

    /// <summary>The high watermark, or the last stable offset under <see cref="WireIsolation.ReadCommitted" />.</summary>
    Latest,

    /// <summary>The offset and timestamp of the record with the highest timestamp.</summary>
    MaxTimestamp,

    /// <summary>The earliest offset whose timestamp is at or after a time.</summary>
    ForTimestamp,
}

/// <summary>What to look up: one kind per call, never two for one partition (F6).</summary>
/// <param name="Kind">The kind.</param>
/// <param name="TimestampMs">The time for <see cref="WireOffsetSpecKind.ForTimestamp" />, in Unix milliseconds.</param>
internal readonly record struct WireOffsetSpec(WireOffsetSpecKind Kind, long TimestampMs = 0)
{
    /// <summary>The log start.</summary>
    public static WireOffsetSpec Earliest { get; } = new(WireOffsetSpecKind.Earliest);

    /// <summary>The end of the readable log.</summary>
    public static WireOffsetSpec Latest { get; } = new(WireOffsetSpecKind.Latest);

    /// <summary>The record with the highest timestamp.</summary>
    public static WireOffsetSpec MaxTimestamp { get; } = new(WireOffsetSpecKind.MaxTimestamp);

    /// <summary>The earliest record at or after <paramref name="unixMs" />.</summary>
    /// <param name="unixMs">The time in Unix milliseconds.</param>
    public static WireOffsetSpec ForTimestamp(long unixMs) => new(WireOffsetSpecKind.ForTimestamp, unixMs);
}

/// <summary>One partition's answer to a list-offsets call.</summary>
/// <param name="Partition">The partition.</param>
/// <param name="Offset">The offset, or -1 when no record qualifies (an empty partition, or none at or after the time).</param>
/// <param name="TimestampMs">The record's timestamp, or <see langword="null" /> when the answer carries none (Earliest and Latest never do).</param>
/// <param name="LeaderEpoch">The leader epoch, when known.</param>
/// <param name="Error">Why this partition has no answer, when it has none.</param>
internal sealed record WireOffset(WirePartition Partition, long Offset, long? TimestampMs, int? LeaderEpoch, WireError? Error)
{
    /// <summary>Whether the partition answered with an offset of a record.</summary>
    public bool Found => Error is null && Offset >= 0;
}

/// <summary>What a reader needs to be built.</summary>
/// <param name="ScanId">The scan's id; the group id derives from it.</param>
/// <param name="Isolation">The isolation level.</param>
/// <param name="Limits">The queue and fetch sizes.</param>
internal sealed record WireReaderOptions(Guid ScanId, WireIsolation Isolation, KStudio.KafkaScanLimits Limits);

/// <summary>A broker or node.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Host">Its host.</param>
/// <param name="Port">Its port.</param>
internal sealed record WireNode(int Id, string Host, int Port);

/// <summary>A partition's leader, replicas and in-sync replicas.</summary>
/// <param name="Partition">The partition's number.</param>
/// <param name="Leader">The leader's broker id, or -1.</param>
/// <param name="Replicas">The replicas' broker ids.</param>
/// <param name="InSyncReplicas">The in-sync replicas' broker ids.</param>
internal sealed record WirePartitionInfo(int Partition, int Leader, IReadOnlyList<int> Replicas, IReadOnlyList<int> InSyncReplicas);

/// <summary>A topic as metadata lists it.</summary>
/// <param name="Name">The topic's name.</param>
/// <param name="Partitions">Its partitions.</param>
/// <param name="Error">A per-topic error, when metadata carried one.</param>
internal sealed record WireTopicInfo(string Name, IReadOnlyList<WirePartitionInfo> Partitions, WireError? Error);

/// <summary>Every topic this principal may describe.</summary>
/// <param name="Topics">The topics.</param>
internal sealed record WireMetadata(IReadOnlyList<WireTopicInfo> Topics);

/// <summary>A topic with the operations this principal may perform on it (KIP-430).</summary>
/// <param name="Name">The topic's name.</param>
/// <param name="IsInternal">Whether Kafka considers the topic internal.</param>
/// <param name="Partitions">Its partitions.</param>
/// <param name="AuthorizedOperations">The names of the operations this principal may perform (<c>Read</c>, <c>Describe</c>, ...).</param>
/// <param name="Error">A per-topic error.</param>
internal sealed record WireTopicDescription(
    string Name,
    bool IsInternal,
    IReadOnlyList<WirePartitionInfo> Partitions,
    IReadOnlyList<string> AuthorizedOperations,
    WireError? Error);

/// <summary>The cluster as describe-cluster returns it.</summary>
/// <param name="ClusterId">The cluster's id.</param>
/// <param name="ControllerId">The controller's broker id, when there is one.</param>
/// <param name="Nodes">The brokers.</param>
/// <param name="AuthorizedOperations">The operations this principal may perform on the cluster.</param>
internal sealed record WireClusterDescription(
    string? ClusterId,
    int? ControllerId,
    IReadOnlyList<WireNode> Nodes,
    IReadOnlyList<string> AuthorizedOperations);

/// <summary>
/// The configuration entries of a topic the engine reads; no other entry is carried, and a sensitive one has no value.
/// </summary>
/// <param name="Topic">The topic.</param>
/// <param name="Entries">Entry name to value; <see langword="null" /> where the value is withheld or unset.</param>
internal sealed record WireTopicConfig(string Topic, IReadOnlyDictionary<string, string?> Entries)
{
    /// <summary>The entries the engine reads (decision 5.2).</summary>
    public static IReadOnlyList<string> KnownKeys { get; } =
    [
        "retention.ms",
        "retention.bytes",
        "cleanup.policy",
        "message.timestamp.type",
        "remote.storage.enable",
        "local.retention.ms",
        "local.retention.bytes",
        "max.message.bytes",
    ];

    /// <summary>One entry's value, or <see langword="null" />.</summary>
    /// <param name="key">The entry's name.</param>
    public string? Get(string key) => Entries.TryGetValue(key, out string? value) ? value : null;
}

/// <summary>A group's committed offset for a partition.</summary>
/// <param name="Partition">The partition.</param>
/// <param name="Committed">The committed offset, or <see langword="null" /> when the group has none.</param>
/// <param name="Error">A per-partition error.</param>
internal sealed record WireGroupOffset(WirePartition Partition, long? Committed, WireError? Error);

/// <summary>A group's state.</summary>
/// <param name="GroupId">The group's id.</param>
/// <param name="State">The state's name (<c>Stable</c>, <c>Empty</c>, ...).</param>
/// <param name="MemberCount">How many members the group has.</param>
/// <param name="PartitionAssignor">The assignor, when known.</param>
internal sealed record WireGroupDescription(string GroupId, string State, int MemberCount, string? PartitionAssignor);

/// <summary>The kind of timestamp a record carries.</summary>
internal enum WireTimestampType
{
    /// <summary>The producer's time.</summary>
    CreateTime,

    /// <summary>The broker's time.</summary>
    LogAppendTime,

    /// <summary>The record carries none.</summary>
    NotAvailable,
}

/// <summary>A record header.</summary>
/// <param name="Name">The header's name.</param>
/// <param name="Value">The value, or <see langword="null" />.</param>
internal sealed record WireHeader(string Name, byte[]? Value);

/// <summary>
/// A record as the wire hands it over: it owns copies of its key, value and headers and never leaves KStudio.Core (decision 3.2).
/// </summary>
internal sealed class WireRecord
{
    /// <summary>Creates a record and computes its <see cref="Size" />.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="offset">The offset.</param>
    /// <param name="key">The key; <see langword="null" /> is a null key, an empty array an empty key.</param>
    /// <param name="value">The value; <see langword="null" /> is a tombstone.</param>
    /// <param name="headers">The headers in wire order, duplicates kept.</param>
    /// <param name="timestampMs">The timestamp in Unix milliseconds.</param>
    /// <param name="timestampType">The kind of timestamp.</param>
    /// <param name="leaderEpoch">The leader epoch, when known.</param>
    public WireRecord(
        string topic,
        int partition,
        long offset,
        byte[]? key,
        byte[]? value,
        WireHeader[] headers,
        long timestampMs,
        WireTimestampType timestampType,
        int? leaderEpoch)
    {
        Topic = topic;
        Partition = partition;
        Offset = offset;
        Key = key;
        Value = value;
        Headers = headers;
        TimestampMs = timestampMs;
        TimestampType = timestampType;
        LeaderEpoch = leaderEpoch;

        long size = (key?.Length ?? 0) + (value?.Length ?? 0);

        foreach (WireHeader header in headers)
        {
            size += System.Text.Encoding.UTF8.GetByteCount(header.Name) + (header.Value?.Length ?? 0);
        }

        Size = (int) Math.Min(size, int.MaxValue);
    }

    /// <summary>The topic.</summary>
    public string Topic { get; }

    /// <summary>The partition.</summary>
    public int Partition { get; }

    /// <summary>The offset.</summary>
    public long Offset { get; }

    /// <summary>The key; <see langword="null" /> is a null key.</summary>
    public byte[]? Key { get; }

    /// <summary>The value; <see langword="null" /> is a tombstone.</summary>
    public byte[]? Value { get; }

    /// <summary>The headers, in wire order.</summary>
    public WireHeader[] Headers { get; }

    /// <summary>The timestamp in Unix milliseconds.</summary>
    public long TimestampMs { get; }

    /// <summary>The kind of timestamp.</summary>
    public WireTimestampType TimestampType { get; }

    /// <summary>The leader epoch, when known.</summary>
    public int? LeaderEpoch { get; }

    /// <summary>Key, value and every header name and value, in bytes: what the governor and budgets count.</summary>
    public int Size { get; }
}

/// <summary>What one <see cref="IWireScanReader.Read" /> returned.</summary>
internal abstract record WireReadOutcome
{
    private WireReadOutcome()
    {
    }

    /// <summary>Nothing arrived within the timeout.</summary>
    public static WireReadOutcome Idle { get; } = new IdleOutcome();

    /// <summary>The nothing-arrived outcome.</summary>
    public sealed record IdleOutcome : WireReadOutcome;

    /// <summary>A record.</summary>
    /// <param name="Value">The record.</param>
    public sealed record Record(WireRecord Value) : WireReadOutcome;

    /// <summary>A partition reached the end of what it can read: the high watermark, or the last stable offset under read_committed.</summary>
    /// <param name="Partition">The partition.</param>
    /// <param name="Offset">The offset the end is at.</param>
    public sealed record PartitionEnd(WirePartition Partition, long Offset) : WireReadOutcome;

    /// <summary>A non-fatal or fatal problem the reader reports in place of a record.</summary>
    /// <param name="Error">The error.</param>
    /// <param name="Partition">The partition it concerns, when it names one.</param>
    public sealed record Failure(WireError Error, WirePartition? Partition) : WireReadOutcome;
}
