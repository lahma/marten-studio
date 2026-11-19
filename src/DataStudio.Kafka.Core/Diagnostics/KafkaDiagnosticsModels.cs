namespace DataStudio.Kafka;

/// <summary>What kind of problem kept a read of <see cref="IKafkaDiagnostics" /> from its answer. Values are explicit and may gain members (D50).</summary>
public enum KafkaReadOutcome
{
    /// <summary>The read answered.</summary>
    Read = 0,

    /// <summary>The broker refused the application's principal by an ACL; <see cref="KafkaReadProblem.Sentence" /> says what to grant.</summary>
    NotAuthorized = 1,

    /// <summary>The topic or group is not there, or the principal may not describe it: the broker says the same for both.</summary>
    NotFound = 2,

    /// <summary>The cluster did not answer in time, or answered that it cannot now (no leader, no coordinator).</summary>
    Unavailable = 3,

    /// <summary>The cluster refused the application's credentials.</summary>
    AuthenticationFailed = 4,

    /// <summary>The broker has no version of the API the read needs.</summary>
    Unsupported = 5,

    /// <summary>Anything else.</summary>
    Failed = 6,
}

/// <summary>Why a read, or one part of it, has no answer.</summary>
public sealed record KafkaReadProblem
{
    /// <summary>The kind of problem.</summary>
    public KafkaReadOutcome Outcome { get; init; }

    /// <summary>The client's error code name (<c>GroupAuthorizationFailed</c>, <c>NotCoordinatorForGroup</c>), or the kind of the error when it has none.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>The studio's sentence for it, in English: "Not available: needs <c>DESCRIBE</c> on group <c>orders</c>". A host words its own from <see cref="Outcome" /> and <see cref="Code" />.</summary>
    public string Sentence { get; init; } = string.Empty;

    /// <summary>The topic the problem is about, when it is about one (a topic's offsets, a group's offsets on one topic); otherwise <see langword="null" />.</summary>
    public string? Topic { get; init; }
}

/// <summary>A partition of a topic.</summary>
public sealed record KafkaTopicPartition
{
    /// <summary>The topic.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>The partition's number.</summary>
    public int Partition { get; init; }
}

/// <summary>Whether a cluster answers, and whether it lists the topics a host asked about (<see cref="IKafkaDiagnostics.GetClusterAsync" />).</summary>
public sealed record KafkaClusterStatus
{
    /// <summary>The cluster's key.</summary>
    public string Cluster { get; init; } = string.Empty;

    /// <summary>When the answer was read from the broker, or from the cache that answered it.</summary>
    public DateTimeOffset ReadAt { get; init; }

    /// <summary>Why the cluster's metadata could not be read; <see langword="null" /> when the cluster answered.</summary>
    public KafkaReadProblem? Problem { get; init; }

    /// <summary>How many brokers the cluster's metadata names; 0 when it was not read.</summary>
    public int BrokerCount { get; init; }

    /// <summary>Each topic the host asked about, in the order asked, with whether the metadata lists it.</summary>
    public IReadOnlyList<KafkaTopicPresence> Topics { get; init; } = [];
}

/// <summary>Whether a cluster's metadata lists a topic.</summary>
public sealed record KafkaTopicPresence
{
    /// <summary>The topic.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>Whether the metadata lists the topic: it exists and the application's principal may describe it.</summary>
    public bool Listed { get; init; }

    /// <summary>The error the metadata carries for a listed topic (a partition without a leader, say); <see langword="null" /> when it carries none.</summary>
    public KafkaReadProblem? Problem { get; init; }
}

/// <summary>A topic's partitions, offsets and retention (<see cref="IKafkaDiagnostics.GetTopicAsync" />).</summary>
public sealed record KafkaTopicStatus
{
    /// <summary>The cluster's key.</summary>
    public string Cluster { get; init; } = string.Empty;

    /// <summary>The topic.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>When the answer was read.</summary>
    public DateTimeOffset ReadAt { get; init; }

    /// <summary>Why the topic could not be read at all: the metadata failed, or does not list the topic (<see cref="KafkaReadOutcome.NotFound" />).</summary>
    public KafkaReadProblem? Problem { get; init; }

    /// <summary>The topic's partitions, in partition order.</summary>
    public IReadOnlyList<KafkaPartitionOffsets> Partitions { get; init; } = [];

    /// <summary>Why the first or end offsets could not be read for the whole topic; a single partition's problem is on that partition.</summary>
    public KafkaReadProblem? OffsetsProblem { get; init; }

    /// <summary><c>retention.ms</c>; -1 keeps records without a time limit. <see langword="null" /> when the configuration was not read.</summary>
    public long? RetentionMs { get; init; }

    /// <summary><c>retention.bytes</c>; -1 keeps records without a size limit. <see langword="null" /> when the configuration was not read.</summary>
    public long? RetentionBytes { get; init; }

    /// <summary><c>cleanup.policy</c>: <c>delete</c>, <c>compact</c> or both; <see langword="null" /> when the configuration was not read.</summary>
    public string? CleanupPolicy { get; init; }

    /// <summary>Why the configuration could not be read (it needs <c>DESCRIBE_CONFIGS</c>, which describing the topic does not).</summary>
    public KafkaReadProblem? ConfigProblem { get; init; }

    /// <summary>
    /// The sum of the partitions' offset spans, or <see langword="null" /> when any is unknown. An offset span is not a record count:
    /// compaction and transaction markers leave gaps.
    /// </summary>
    public long? OffsetSpan
    {
        get
        {
            long total = 0;

            foreach (KafkaPartitionOffsets partition in Partitions)
            {
                if (partition.OffsetSpan is not { } span)
                {
                    return null;
                }

                total += span;
            }

            return Partitions.Count == 0 ? null : total;
        }
    }
}

/// <summary>A partition's first and end offset.</summary>
public sealed record KafkaPartitionOffsets
{
    /// <summary>The partition's number.</summary>
    public int Partition { get; init; }

    /// <summary>The log start: the earliest offset the broker still holds. <see langword="null" /> when it was not read.</summary>
    public long? FirstOffset { get; init; }

    /// <summary>The high watermark: the offset the next record gets. <see langword="null" /> when it was not read.</summary>
    public long? EndOffset { get; init; }

    /// <summary>Why this partition's offsets could not be read.</summary>
    public KafkaReadProblem? Problem { get; init; }

    /// <summary>The end minus the start, or <see langword="null" /> when either is unknown.</summary>
    public long? OffsetSpan => FirstOffset is { } first && EndOffset is { } end ? Math.Max(0, end - first) : null;
}

/// <summary>A consumer group's state, members, positions and lag (<see cref="IKafkaDiagnostics.GetGroupAsync" />).</summary>
public sealed record KafkaGroupStatus
{
    /// <summary>The cluster's key.</summary>
    public string Cluster { get; init; } = string.Empty;

    /// <summary>The group.</summary>
    public string GroupId { get; init; } = string.Empty;

    /// <summary>When the answer was read.</summary>
    public DateTimeOffset ReadAt { get; init; }

    /// <summary>Why the group could not be described; its offsets may still have been read. <see langword="null" /> when it was.</summary>
    public KafkaReadProblem? Problem { get; init; }

    /// <summary>The group's state as Kafka names it (<c>Stable</c>, <c>Empty</c>, <c>PreparingRebalance</c>; <c>Dead</c> for a group Kafka does not know); empty when not described.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>The group's live members, with their assignments.</summary>
    public IReadOnlyList<KafkaGroupMember> Members { get; init; } = [];

    /// <summary>Every partition a member is assigned or the group has committed to on the topics asked about, in topic and partition order.</summary>
    public IReadOnlyList<KafkaGroupPartition> Partitions { get; init; } = [];

    /// <summary>
    /// Why the committed or end offsets of a topic could not be read; a single partition's problem is on that partition, and so is a
    /// first offset that could not be read where the group has committed nothing.
    /// </summary>
    public KafkaReadProblem? OffsetsProblem { get; init; }

    /// <summary>
    /// The sum of the known lags. It is 0 when no lag is known as well as when the group has caught up: <see cref="Partitions" /> and
    /// <see cref="OffsetsProblem" /> tell the two apart.
    /// </summary>
    public long TotalLag
    {
        get
        {
            long total = 0;

            foreach (KafkaGroupPartition partition in Partitions)
            {
                total += partition.Lag ?? 0;
            }

            return total;
        }
    }

    /// <summary>
    /// Where the member with <paramref name="clientId" /> stands in the group: it waits for a partition, has read everything it owns, has
    /// records waiting, or is not a member at all. Only the partitions the member owns are judged: a problem on one of them, or an offset of
    /// one that was not read, makes the answer <see cref="KafkaMemberState.Unknown" />, and a problem on any other partition or topic does not.
    /// A partition the member owns with no committed offset, or whose committed offset retention has passed, counts from the partition's
    /// first offset, where a consumer that starts at the earliest record begins: records that have expired are not waiting. (A consumer configured to start at the end has less waiting than
    /// that; the group does not say which it is.) An application tells a quiet consumer from a stuck one with it: a consumer that only
    /// marks progress when a record arrives looks the same idle for both.
    /// </summary>
    /// <param name="clientId">The member's <c>client.id</c>, which must be unique in the group for the answer to name one consumer.</param>
    public KafkaMemberPlace PlaceOf(string clientId)
    {
        ArgumentNullException.ThrowIfNull(clientId);

        KafkaGroupMember? member = null;

        foreach (KafkaGroupMember candidate in Members)
        {
            if (string.Equals(candidate.ClientId, clientId, StringComparison.Ordinal))
            {
                member = candidate;
                break;
            }
        }

        if (member is null)
        {
            return Problem is { } problem
                ? KafkaMemberPlace.Unknown($"consumer group {GroupId} could not be read: {problem.Sentence}")
                : new KafkaMemberPlace
                {
                    State = KafkaMemberState.NotAMember,
                    Detail = $"not a member of consumer group {GroupId} ({State}, {Members.Count} member(s))",
                };
        }

        if (member.Assignment.Count == 0)
        {
            return new KafkaMemberPlace { State = KafkaMemberState.Standby };
        }

        long waiting = 0;

        foreach (KafkaTopicPartition owned in member.Assignment)
        {
            KafkaGroupPartition? row = null;

            foreach (KafkaGroupPartition partition in Partitions)
            {
                if (partition.Partition == owned.Partition && string.Equals(partition.Topic, owned.Topic, StringComparison.Ordinal))
                {
                    row = partition;
                    break;
                }
            }

            if (row?.Problem is { } problem)
            {
                return KafkaMemberPlace.Unknown($"the offsets of {owned.Topic} partition {owned.Partition} could not be read: {problem.Sentence}");
            }

            if (row?.EndOffset is not { } end)
            {
                return KafkaMemberPlace.Unknown($"the end offset of {owned.Topic} partition {owned.Partition} is not known");
            }

            // Nothing committed: from the first record the partition still holds, never from offset 0, which retention may have long passed.
            // A committed offset that retention has passed counts from the first record too: what expired is not waiting.
            long? start = row.CommittedOffset is { } committed
                ? row.FirstOffset is { } first ? Math.Max(committed, first) : committed
                : row.FirstOffset;

            if (start is not { } from)
            {
                return KafkaMemberPlace.Unknown($"the first offset of {owned.Topic} partition {owned.Partition} is not known, and the group has committed none");
            }

            waiting += Math.Max(0, end - from);
        }

        return waiting > 0
            ? new KafkaMemberPlace
            {
                State = KafkaMemberState.Behind,
                RecordsWaiting = waiting,
                Detail = $"{waiting} record(s) waiting on its {member.Assignment.Count} partition(s) in consumer group {GroupId}",
            }
            : new KafkaMemberPlace { State = KafkaMemberState.CaughtUp, RecordsWaiting = 0 };
    }
}

/// <summary>One live member of a consumer group.</summary>
public sealed record KafkaGroupMember
{
    /// <summary>The member id the coordinator gave it.</summary>
    public string MemberId { get; init; } = string.Empty;

    /// <summary>The member's <c>client.id</c>.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>The member's address as the coordinator sees it.</summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>The partitions assigned to it, in topic and partition order; none for a member that stands by.</summary>
    public IReadOnlyList<KafkaTopicPartition> Assignment { get; init; } = [];
}

/// <summary>A consumer group's position on one partition.</summary>
public sealed record KafkaGroupPartition
{
    /// <summary>The topic.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>The partition's number.</summary>
    public int Partition { get; init; }

    /// <summary>The group's committed offset, or <see langword="null" /> when it has committed none or it was not read.</summary>
    public long? CommittedOffset { get; init; }

    /// <summary>The partition's high watermark, or <see langword="null" /> when it was not read.</summary>
    public long? EndOffset { get; init; }

    /// <summary>The offset of the first record the partition still holds (its log start), or <see langword="null" /> when it was not read.</summary>
    public long? FirstOffset { get; init; }

    /// <summary>The end minus the committed offset (0 when the committed offset is past the end), or <see langword="null" /> when either is unknown.</summary>
    public long? Lag { get; init; }

    /// <summary>The <c>client.id</c> of the member assigned this partition, or <see langword="null" /> when none is.</summary>
    public string? MemberClientId { get; init; }

    /// <summary>Why this partition's committed, first or end offset could not be read: the partition's own problem, or its topic's.</summary>
    public KafkaReadProblem? Problem { get; init; }
}

/// <summary>Where a member stands in its consumer group (<see cref="KafkaGroupStatus.PlaceOf" />). Values are explicit and may gain members.</summary>
public enum KafkaMemberState
{
    /// <summary>The group or its offsets could not be read, so the answer is not known.</summary>
    Unknown = 0,

    /// <summary>A member with no partition: the topics have fewer partitions than the group has members, and this one waits for an owner to leave.</summary>
    Standby = 1,

    /// <summary>A member whose every partition has its committed offset at the end: there is nothing to read.</summary>
    CaughtUp = 2,

    /// <summary>A member with records waiting on a partition it owns.</summary>
    Behind = 3,

    /// <summary>The group has no member with this client id: the coordinator removed it, or it never joined.</summary>
    NotAMember = 4,
}

/// <summary>Where a member stands in its consumer group.</summary>
public sealed record KafkaMemberPlace
{
    /// <summary>The member's state.</summary>
    public KafkaMemberState State { get; init; }

    /// <summary>The records waiting on its partitions, for <see cref="KafkaMemberState.Behind" /> and <see cref="KafkaMemberState.CaughtUp" />; otherwise <see langword="null" />.</summary>
    public long? RecordsWaiting { get; init; }

    /// <summary>The same in words, for a health check's data; empty for <see cref="KafkaMemberState.Standby" /> and <see cref="KafkaMemberState.CaughtUp" />.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>Whether the member is working: it stands by, or has read everything it owns.</summary>
    public bool HasNothingToRead => State is KafkaMemberState.Standby or KafkaMemberState.CaughtUp;

    internal static KafkaMemberPlace Unknown(string detail) => new() { State = KafkaMemberState.Unknown, Detail = detail };
}
