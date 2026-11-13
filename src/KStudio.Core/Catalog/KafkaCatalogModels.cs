using KStudio.Core.Wire;

namespace KStudio.Core.Catalog;

/// <summary>
/// A field of an overview: a value, or the error that withheld it, so that one refusal never blanks a page (decision 5.1).
/// </summary>
/// <typeparam name="T">What the field holds.</typeparam>
internal readonly struct KafkaField<T>
{
    private readonly T? value;

    private KafkaField(T? value, WireError? error)
    {
        this.value = value;
        Error = error;
    }

    /// <summary>Why the field has no value, when it has none.</summary>
    public WireError? Error { get; }

    /// <summary>Whether the field has its value.</summary>
    public bool IsAvailable => Error is null;

    /// <summary>The value; throws when the field was withheld.</summary>
    public T Value => Error is null ? value! : throw new InvalidOperationException("The field was withheld: " + Error.Code + ".");

    /// <summary>The value, or <paramref name="fallback" /> when the field was withheld.</summary>
    /// <param name="fallback">What to return instead.</param>
    public T? ValueOr(T? fallback) => Error is null ? value : fallback;

    /// <summary>A field with its value.</summary>
    /// <param name="value">The value.</param>
    public static KafkaField<T> Of(T value) => new(value, null);

    /// <summary>A field the error withheld.</summary>
    /// <param name="error">Why.</param>
    public static KafkaField<T> Withheld(WireError error) => new(default, error);
}

/// <summary>A registered cluster, as a page lists it. Read with no I/O.</summary>
/// <param name="Key">The entry key: the cluster's key, or <c>{key}/{tenant}</c> for one tenant of a broker-per-tenant cluster.</param>
/// <param name="DisplayName">The name shown.</param>
/// <param name="Environment">The environment label.</param>
/// <param name="ConsumerGroups">The groups the host declared for the cluster, then the groups a source found its listeners using.</param>
/// <param name="BaseKey">The cluster's key without a tenant.</param>
/// <param name="TenantId">The tenant this entry is a cluster of, or <see langword="null" />.</param>
/// <param name="Source">The source that contributed the cluster, or <see langword="null" /> for a host cluster.</param>
/// <param name="Profile">The profile the host set with <c>KafkaClusterBuilder.Profile</c>, or <see langword="null" /> for none (never inferred: D47).</param>
/// <param name="SameServersAs">
/// The entry keys of the other clusters, of another base cluster, whose bootstrap servers are the same set as this cluster's (#72): probably one cluster
/// registered twice. Compared from configuration alone, never by connecting; the servers themselves are not in the record.
/// </param>
/// <param name="CanBeLeftOut">Whether the source that contributed this cluster lets the host leave it out of discovery. This cluster's own fact only: a page counts the clusters the visitor may see, never one it was refused (D35).</param>
internal sealed record KafkaClusterInfo(
    string Key,
    string DisplayName,
    string? Environment,
    IReadOnlyList<string> ConsumerGroups,
    string BaseKey,
    string? TenantId,
    string? Source,
    KafkaProfile? Profile = null,
    IReadOnlyList<string>? SameServersAs = null,
    bool CanBeLeftOut = false);

/// <summary>A topic in the list.</summary>
/// <param name="Name">The name.</param>
/// <param name="PartitionCount">How many partitions.</param>
/// <param name="IsInternal">Whether the name starts with <c>__</c>.</param>
internal sealed record KafkaTopicSummary(string Name, int PartitionCount, bool IsInternal);

/// <summary>The topics this principal may describe; internal topics are listed apart.</summary>
/// <param name="Topics">The topics.</param>
/// <param name="InternalTopics">The topics whose names start with <c>__</c>.</param>
internal sealed record KafkaTopicList(IReadOnlyList<KafkaTopicSummary> Topics, IReadOnlyList<KafkaTopicSummary> InternalTopics);

/// <summary>One topic's offset span on the home: the sum over its partitions of high minus low, shown as <c>~N</c> and never called a count.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="OffsetSpan">The span; withheld alone when the broker refused this topic's offsets or metadata does not list it.</param>
internal sealed record KafkaTopicSpan(string Topic, KafkaField<long> OffsetSpan);

/// <summary>A record's offset and timestamp.</summary>
/// <param name="Offset">The offset.</param>
/// <param name="Timestamp">The timestamp.</param>
internal sealed record KafkaTimestampAt(long Offset, DateTimeOffset Timestamp);

/// <summary>One partition of a topic overview.</summary>
/// <param name="Partition">The partition's number.</param>
/// <param name="Leader">The leader's broker id.</param>
/// <param name="Replicas">The replicas.</param>
/// <param name="InSyncReplicas">The in-sync replicas.</param>
/// <param name="LowWatermark">The log start.</param>
/// <param name="HighWatermark">The high watermark.</param>
/// <param name="LastStableOffset">The last stable offset.</param>
/// <param name="FirstTimestamp">The first record's offset and timestamp; a value of <see langword="null" /> is an empty partition.</param>
/// <param name="HighestTimestamp">The record with the <b>highest</b> timestamp, not the last record's.</param>
internal sealed record KafkaPartitionOverview(
    int Partition,
    int Leader,
    IReadOnlyList<int> Replicas,
    IReadOnlyList<int> InSyncReplicas,
    KafkaField<long> LowWatermark,
    KafkaField<long> HighWatermark,
    KafkaField<long> LastStableOffset,
    KafkaField<KafkaTimestampAt?> FirstTimestamp,
    KafkaField<KafkaTimestampAt?> HighestTimestamp)
{
    /// <summary>
    /// High minus low: an <b>offset span</b>, shown as <c>~N</c> and never called a count. Compaction and control records leave gaps
    /// (SPIKE S3.3: it overstated the live records of a compacted topic by 97 %).
    /// </summary>
    public long? OffsetSpan => LowWatermark.IsAvailable && HighWatermark.IsAvailable ? HighWatermark.Value - LowWatermark.Value : null;

    /// <summary>Whether an open transaction holds offsets back: the last stable offset is below the high watermark.</summary>
    public bool? OpenTransaction => LastStableOffset.IsAvailable && HighWatermark.IsAvailable ? LastStableOffset.Value < HighWatermark.Value : null;
}

/// <summary>The topic configuration the engine reads.</summary>
/// <param name="RetentionMs"><c>retention.ms</c>; -1 is unlimited.</param>
/// <param name="RetentionBytes"><c>retention.bytes</c>; -1 is unlimited.</param>
/// <param name="CleanupPolicy"><c>cleanup.policy</c>.</param>
/// <param name="TimestampType"><c>message.timestamp.type</c>: <c>CreateTime</c> or <c>LogAppendTime</c>.</param>
/// <param name="RemoteStorageEnabled"><c>remote.storage.enable</c> (tiered storage).</param>
/// <param name="LocalRetentionMs"><c>local.retention.ms</c>.</param>
/// <param name="LocalRetentionBytes"><c>local.retention.bytes</c>.</param>
/// <param name="MaxMessageBytes"><c>max.message.bytes</c>.</param>
internal sealed record KafkaTopicConfigView(
    long? RetentionMs,
    long? RetentionBytes,
    string? CleanupPolicy,
    string? TimestampType,
    bool RemoteStorageEnabled,
    long? LocalRetentionMs,
    long? LocalRetentionBytes,
    long? MaxMessageBytes)
{
    /// <summary>Whether <c>cleanup.policy</c> contains <c>compact</c>: a time-window search can miss superseded keys.</summary>
    public bool IsCompacted => CleanupPolicy?.Contains("compact", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>Reads the entries the wire returned.</summary>
    /// <param name="config">The wire's entries.</param>
    public static KafkaTopicConfigView From(WireTopicConfig config) => new(
        Long(config.Get("retention.ms")),
        Long(config.Get("retention.bytes")),
        config.Get("cleanup.policy"),
        config.Get("message.timestamp.type"),
        string.Equals(config.Get("remote.storage.enable"), "true", StringComparison.OrdinalIgnoreCase),
        Long(config.Get("local.retention.ms")),
        Long(config.Get("local.retention.bytes")),
        Long(config.Get("max.message.bytes")));

    private static long? Long(string? text) =>
        long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long value) ? value : null;
}

/// <summary>A declared group's lag on one partition.</summary>
/// <param name="Partition">The partition.</param>
/// <param name="Committed">The group's committed offset; <see langword="null" /> when it has none.</param>
/// <param name="Lag">High watermark minus committed, when both are known.</param>
internal sealed record KafkaGroupLag(WirePartition Partition, KafkaField<long?> Committed, long? Lag);

/// <summary>A declared group's state and lag on a topic.</summary>
/// <param name="GroupId">The group's id.</param>
/// <param name="State">The group's state and member count.</param>
/// <param name="Lag">The lag per partition; the error that withheld the offsets, otherwise.</param>
/// <param name="ClusterWide">
/// Whether the host declared the group for the whole cluster, so that nothing says it reads this topic. A group a source paired with this topic's listener
/// is not cluster-wide (#73).
/// </param>
internal sealed record KafkaGroupOverview(
    string GroupId,
    KafkaField<WireGroupDescription> State,
    KafkaField<IReadOnlyList<KafkaGroupLag>> Lag,
    bool ClusterWide = false)
{
    /// <summary>
    /// Whether this is a cluster-wide group whose offsets are known and none of them is committed on the topic: it does not read this topic as far as
    /// Kafka says, and a lag of "—" beside the groups that do would be read as "lag unknown".
    /// </summary>
    public bool HasNoCommittedOffset =>
        ClusterWide && Lag.IsAvailable && Lag.Value.All(static x => x.Committed.IsAvailable && x.Committed.Value is null);
}

/// <summary>A topic overview (decision 5.2). Every field that can be refused is a <see cref="KafkaField{T}" />.</summary>
/// <param name="Cluster">The cluster's key.</param>
/// <param name="Name">The topic.</param>
/// <param name="IsInternal">Whether the name starts with <c>__</c>.</param>
/// <param name="Partitions">The partitions.</param>
/// <param name="AuthorizedOperations">What this principal may do with the topic.</param>
/// <param name="Config">Retention, cleanup policy, timestamp type and tiered storage.</param>
/// <param name="OffsetSpan">The sum of the partitions' spans, an estimate shown with <c>~</c>.</param>
/// <param name="OpenTransaction">Whether any partition has an open transaction.</param>
/// <param name="OffsetsPerSecond">The recent rate of the high watermarks, an estimate; <see langword="null" /> until two samples exist.</param>
/// <param name="Groups">The groups read on this topic: the host's cluster-wide ones and the one a source paired with the topic's listener.</param>
/// <param name="ClusterHasGroups">Whether the cluster has any consumer group at all, declared or found by a source (so an empty <paramref name="Groups" /> means none reads this topic).</param>
internal sealed record KafkaTopicOverview(
    string Cluster,
    string Name,
    bool IsInternal,
    IReadOnlyList<KafkaPartitionOverview> Partitions,
    KafkaField<IReadOnlyList<string>> AuthorizedOperations,
    KafkaField<KafkaTopicConfigView> Config,
    long? OffsetSpan,
    bool? OpenTransaction,
    double? OffsetsPerSecond,
    IReadOnlyList<KafkaGroupOverview> Groups,
    bool ClusterHasGroups = false);

/// <summary>A cluster overview.</summary>
/// <param name="Key">The cluster's key.</param>
/// <param name="Description">The cluster id, controller, brokers and operations.</param>
internal sealed record KafkaClusterOverview(string Key, KafkaField<WireClusterDescription> Description);
