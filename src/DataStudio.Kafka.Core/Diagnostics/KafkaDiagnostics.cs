using DataStudio.Kafka.Core.Catalog;
using DataStudio.Kafka.Core.Clusters;
using DataStudio.Kafka.Core.Logging;
using DataStudio.Kafka.Core.Wire;

namespace DataStudio.Kafka.Core.Diagnostics;

/// <summary>
/// <see cref="IKafkaDiagnostics" /> over the engine's own reads (AGENTS.md D50): every call goes through <see cref="KafkaWireReads" />, so it
/// shares the pages' single-flight caches and pooled clients, and the group offsets go through the offsets reader (librdkafka #5040, D36).
/// Nothing here asks a visitor's policy, and nothing is served: the result is the host's.
/// </summary>
/// <param name="registry">The registered clusters.</param>
/// <param name="reads">The engine's cached wire reads.</param>
/// <param name="timeProvider">The clock a result is stamped with.</param>
/// <param name="loggers">Where event 9681 goes.</param>
internal sealed class KafkaDiagnostics(
    KafkaClusterRegistry registry,
    KafkaWireReads reads,
    TimeProvider timeProvider,
    KafkaEngineLoggers loggers) : IKafkaDiagnostics
{
    /// <inheritdoc />
    public IReadOnlyList<string> Clusters => [.. registry.Clusters.Select(static x => x.Key)];

    /// <inheritdoc />
    public async Task<KafkaClusterStatus> GetClusterAsync(string cluster, IReadOnlyList<string>? topics = null, CancellationToken cancellationToken = default)
    {
        Resolve(cluster);

        WireResult<WireMetadata> metadata = await reads.MetadataAsync(cluster, cancellationToken).ConfigureAwait(false);
        KafkaClusterStatus status;

        if (metadata.Error is { } error)
        {
            status = new KafkaClusterStatus
            {
                Cluster = cluster,
                ReadAt = Now(),
                Problem = Problem(error),
                Topics = [.. (topics ?? []).Select(static x => new KafkaTopicPresence { Topic = x })],
            };
        }
        else
        {
            Dictionary<string, WireTopicInfo> listed = Listed(metadata.Value);

            status = new KafkaClusterStatus
            {
                Cluster = cluster,
                ReadAt = Now(),
                BrokerCount = metadata.Value.Brokers.Count,
                Topics =
                [
                    .. (topics ?? []).Select(topic => listed.TryGetValue(topic, out WireTopicInfo? info)
                        ? new KafkaTopicPresence { Topic = topic, Listed = true, Problem = info.Error is { } topicError ? Problem(topicError, topic) : null }
                        : new KafkaTopicPresence { Topic = topic }),
                ],
            };
        }

        Log("cluster", cluster, status.Problem);
        return status;
    }

    /// <inheritdoc />
    public async Task<KafkaTopicStatus> GetTopicAsync(string cluster, string topic, CancellationToken cancellationToken = default)
    {
        Resolve(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

        WireResult<WireMetadata> metadata = await reads.MetadataAsync(cluster, cancellationToken).ConfigureAwait(false);

        if (Partitions(metadata, topic, out WireError? missing) is not { } partitions)
        {
            KafkaTopicStatus unread = new() { Cluster = cluster, Topic = topic, ReadAt = Now(), Problem = Problem(missing!, topic) };
            Log("topic", cluster, unread.Problem);
            return unread;
        }

        Task<WireResult<IReadOnlyList<WireOffset>>> earliest = reads.OffsetsAsync(
            cluster, topic, partitions, WireOffsetSpec.Earliest, WireIsolation.ReadUncommitted, cancellationToken);
        Task<WireResult<IReadOnlyList<WireOffset>>> latest = reads.OffsetsAsync(
            cluster, topic, partitions, WireOffsetSpec.Latest, WireIsolation.ReadUncommitted, cancellationToken);
        Task<WireResult<WireTopicConfig>> config = reads.ConfigAsync(cluster, new KafkaTopicName(topic), cancellationToken);
        await Task.WhenAll(earliest, latest, config).ConfigureAwait(false);

        WireResult<IReadOnlyList<WireOffset>> first = await earliest.ConfigureAwait(false);
        WireResult<IReadOnlyList<WireOffset>> end = await latest.ConfigureAwait(false);
        WireResult<WireTopicConfig> settings = await config.ConfigureAwait(false);
        KafkaTopicConfigView? view = settings.Error is null ? KafkaTopicConfigView.From(settings.Value) : null;

        KafkaTopicStatus status = new()
        {
            Cluster = cluster,
            Topic = topic,
            ReadAt = Now(),
            Partitions =
            [
                .. partitions.Select(partition =>
                {
                    (long? firstOffset, WireError? firstError) = Offset(first, partition);
                    (long? endOffset, WireError? endError) = Offset(end, partition);
                    WireError? partitionError = first.Error is null && end.Error is null ? firstError ?? endError : null;

                    return new KafkaPartitionOffsets
                    {
                        Partition = partition.Partition,
                        FirstOffset = firstOffset,
                        EndOffset = endOffset,
                        Problem = partitionError is null ? null : Problem(partitionError, topic),
                    };
                }),
            ],
            OffsetsProblem = (first.Error ?? end.Error) is { } offsetsError ? Problem(offsetsError, topic) : null,
            RetentionMs = view?.RetentionMs,
            RetentionBytes = view?.RetentionBytes,
            CleanupPolicy = view?.CleanupPolicy,
            ConfigProblem = settings.Error is { } configError ? Problem(configError, topic) : null,
        };

        Log("topic", cluster, status.OffsetsProblem);
        return status;
    }

    /// <inheritdoc />
    public async Task<KafkaGroupStatus> GetGroupAsync(
        string cluster, string groupId, IReadOnlyList<string> topics, CancellationToken cancellationToken = default)
    {
        Resolve(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(topics);

        // The metadata first, as the topic page reads it (D50): a group call made while the cluster is away times out, and a timed-out group
        // call is what librdkafka crashes on when the broker returns (D36). No metadata, no group call.
        WireResult<WireMetadata> metadata = await reads.MetadataAsync(cluster, cancellationToken).ConfigureAwait(false);

        if (metadata.Error is { } metadataError)
        {
            KafkaReadProblem problem = Problem(metadataError);
            Log("group", cluster, problem);

            return new KafkaGroupStatus
            {
                Cluster = cluster,
                GroupId = groupId,
                ReadAt = Now(),
                Problem = problem,
                OffsetsProblem = problem,
            };
        }

        WireResult<WireGroupDescription> description = await reads.GroupAsync(cluster, groupId, cancellationToken).ConfigureAwait(false);

        // A group Kafka does not know is "Dead" with no member, as Kafka itself describes one, never a problem.
        bool unknown = description.Error is WireError.NotFound;
        KafkaGroupMember[] members = description.Error is null ? [.. description.Value.Members.Select(Member)] : [];
        Dictionary<KafkaTopicPartition, string> owners = [];

        foreach (KafkaGroupMember member in members)
        {
            foreach (KafkaTopicPartition partition in member.Assignment)
            {
                owners[partition] = member.ClientId;
            }
        }

        string[] wanted = [.. topics.Concat(owners.Keys.Select(static x => x.Topic)).Where(static x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal)];
        Task<TopicPositions>[] reading = [.. wanted.Select(topic => Positions(cluster, groupId, topic, metadata, cancellationToken))];
        TopicPositions[] positions = await Task.WhenAll(reading).ConfigureAwait(false);

        Dictionary<KafkaTopicPartition, KafkaGroupPartition> rows = [];
        Dictionary<string, KafkaReadProblem> withheld = new(StringComparer.Ordinal);
        KafkaReadProblem? offsetsProblem = null;

        foreach (TopicPositions topic in positions)
        {
            KafkaReadProblem? topicProblem = topic.Error is { } topicError ? Problem(topicError, topic.Topic) : null;
            offsetsProblem ??= topicProblem;

            if (topicProblem is not null)
            {
                withheld[topic.Topic] = topicProblem;
            }

            foreach (KafkaGroupPartition row in topic.Rows)
            {
                KafkaTopicPartition key = new() { Topic = row.Topic, Partition = row.Partition };

                // The union of the assigned partitions and the committed ones: an owned partition is shown even before its first commit.
                // A row of a topic whose offsets were withheld as a whole carries that problem, so whoever judges one partition
                // (PlaceOf) finds it there and does not read a missing offset as "none committed".
                // A first offset that could not be read is a problem only for whoever owns the partition: by itself it lists no row.
                if (owners.TryGetValue(key, out string? owner)
                    || row.CommittedOffset is not null
                    || (row.Problem is not null && !topic.FirstOffsetOnly.Contains(row.Partition)))
                {
                    rows[key] = row with { MemberClientId = owner, Problem = row.Problem ?? topicProblem };
                }
            }
        }

        foreach ((KafkaTopicPartition key, string owner) in owners)
        {
            rows.TryAdd(key, new KafkaGroupPartition
            {
                Topic = key.Topic,
                Partition = key.Partition,
                MemberClientId = owner,
                Problem = withheld.GetValueOrDefault(key.Topic),
            });
        }

        KafkaGroupStatus status = new()
        {
            Cluster = cluster,
            GroupId = groupId,
            ReadAt = Now(),
            Problem = description.Error is { } describeError && !unknown ? Problem(describeError) : null,
            State = description.Error is null ? description.Value.State : unknown ? "Dead" : string.Empty,
            Members = members,
            Partitions = [.. rows.Values.OrderBy(static x => x.Topic, StringComparer.Ordinal).ThenBy(static x => x.Partition)],
            OffsetsProblem = offsetsProblem,
        };

        Log("group", cluster, status.Problem ?? status.OffsetsProblem);
        return status;
    }

    /// <summary>The group's committed offsets and the first and end offsets of every partition of one topic, as rows with no owner yet.</summary>
    private async Task<TopicPositions> Positions(
        string cluster, string groupId, string topic, WireResult<WireMetadata> metadata, CancellationToken cancellationToken)
    {
        if (Partitions(metadata, topic, out WireError? missing) is not { } partitions)
        {
            return new TopicPositions(topic, [], missing, new HashSet<int>());
        }

        Task<WireResult<IReadOnlyList<WireGroupOffset>>> committing = reads.GroupOffsetsAsync(cluster, groupId, topic, partitions, cancellationToken);
        Task<WireResult<IReadOnlyList<WireOffset>>> ending = reads.OffsetsAsync(
            cluster, topic, partitions, WireOffsetSpec.Latest, WireIsolation.ReadUncommitted, cancellationToken);

        // The same cached read the topic's own status makes: a partition with nothing committed is counted from here, not from offset 0.
        Task<WireResult<IReadOnlyList<WireOffset>>> starting = reads.OffsetsAsync(
            cluster, topic, partitions, WireOffsetSpec.Earliest, WireIsolation.ReadUncommitted, cancellationToken);
        await Task.WhenAll(committing, ending, starting).ConfigureAwait(false);

        WireResult<IReadOnlyList<WireGroupOffset>> committed = await committing.ConfigureAwait(false);
        WireResult<IReadOnlyList<WireOffset>> ends = await ending.ConfigureAwait(false);
        WireResult<IReadOnlyList<WireOffset>> starts = await starting.ConfigureAwait(false);

        HashSet<int> firstOffsetOnly = [];

        KafkaGroupPartition[] rows =
        [
            .. partitions.Select(partition =>
            {
                WireGroupOffset? position = committed.Error is null ? committed.Value.FirstOrDefault(x => x.Partition == partition) : null;
                (long? end, WireError? endError) = Offset(ends, partition);
                (long? first, WireError? firstError) = Offset(starts, partition);
                long? at = position?.Error is null ? position?.Committed : null;

                // The first offset matters only where nothing is committed: its failure alone leaves a partition with a commit as it was read.
                WireError? partitionError = committed.Error is null && ends.Error is null
                    ? position?.Error ?? endError ?? (at is null ? firstError : null)
                    : null;

                if (partitionError is not null && ReferenceEquals(partitionError, firstError) && position?.Error is null && endError is null)
                {
                    firstOffsetOnly.Add(partition.Partition);
                }

                return new KafkaGroupPartition
                {
                    Topic = topic,
                    Partition = partition.Partition,
                    CommittedOffset = at,
                    EndOffset = end,
                    FirstOffset = first,
                    Lag = at is { } c && end is { } e ? Math.Max(0, e - c) : null,
                    Problem = partitionError is null ? null : Problem(partitionError, topic),
                };
            }),
        ];

        return new TopicPositions(topic, rows, committed.Error ?? ends.Error, firstOffsetOnly);
    }

    /// <summary>A topic's partitions from the metadata, or <see langword="null" /> with the error that withheld them.</summary>
    private static IReadOnlyList<WirePartition>? Partitions(WireResult<WireMetadata> metadata, string topic, out WireError? error)
    {
        if (metadata.Error is { } metadataError)
        {
            error = metadataError;
            return null;
        }

        WireTopicInfo? info = metadata.Value.Topics.FirstOrDefault(x => string.Equals(x.Name, topic, StringComparison.Ordinal));

        if (info is null || info.Error is not null)
        {
            // A topic the metadata does not list is missing, or the principal may not describe it: Kafka says the same for both.
            error = info?.Error ?? new WireError.NotFound(KafkaResourceType.Topic, topic);
            return null;
        }

        error = null;
        return [.. info.Partitions.OrderBy(static x => x.Partition).Select(x => new WirePartition(topic, x.Partition))];
    }

    private static Dictionary<string, WireTopicInfo> Listed(WireMetadata metadata)
    {
        Dictionary<string, WireTopicInfo> listed = new(StringComparer.Ordinal);

        foreach (WireTopicInfo topic in metadata.Topics)
        {
            listed.TryAdd(topic.Name, topic);
        }

        return listed;
    }

    private static (long? Offset, WireError? Error) Offset(WireResult<IReadOnlyList<WireOffset>> result, WirePartition partition)
    {
        if (result.Error is { } error)
        {
            return (null, error);
        }

        WireOffset? offset = result.Value.FirstOrDefault(x => x.Partition == partition);

        if (offset is null)
        {
            return (null, new WireError.NotFound(KafkaResourceType.Topic, partition.Topic));
        }

        return offset.Error is { } offsetError ? (null, offsetError) : (Math.Max(0, offset.Offset), null);
    }

    private static KafkaGroupMember Member(WireGroupMember member) => new()
    {
        MemberId = member.MemberId,
        ClientId = member.ClientId,
        Host = member.Host,
        Assignment =
        [
            .. member.Assignment
                .OrderBy(static x => x.Topic, StringComparer.Ordinal)
                .ThenBy(static x => x.Partition)
                .Select(static x => new KafkaTopicPartition { Topic = x.Topic, Partition = x.Partition }),
        ],
    };

    /// <summary>A wire error as the API returns it: its kind, its code and the studio's sentence (<see cref="KafkaAccessText" />).</summary>
    /// <param name="error">The error.</param>
    /// <param name="topic">The topic it is about, when it is about one.</param>
    internal static KafkaReadProblem Problem(WireError error, string? topic = null) => new()
    {
        Topic = topic,
        Outcome = error switch
        {
            WireError.NotAuthorized => KafkaReadOutcome.NotAuthorized,
            WireError.NotFound => KafkaReadOutcome.NotFound,
            WireError.Unavailable => KafkaReadOutcome.Unavailable,
            WireError.AuthenticationFailed => KafkaReadOutcome.AuthenticationFailed,
            WireError.Unsupported => KafkaReadOutcome.Unsupported,
            _ => KafkaReadOutcome.Failed,
        },
        Code = error.Code,
        Sentence = KafkaAccessText.Describe(error),
    };

    private void Resolve(string cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (!registry.TryGet(cluster, out _))
        {
            throw new ArgumentException(
                $"No Kafka cluster is registered under the key '{cluster}'. The registered keys are in IKafkaDiagnostics.Clusters.",
                nameof(cluster));
        }
    }

    private DateTimeOffset Now() => timeProvider.GetUtcNow();

    /// <summary>Event 9681, at Debug: which read, which cluster, and the outcome's code only (D50, after D31's 9250).</summary>
    private void Log(string read, string cluster, KafkaReadProblem? problem)
    {
        string key = KafkaLogValue.Cap(cluster);
        string outcome = problem is null ? nameof(KafkaReadOutcome.Read) : KafkaLogValue.Cap(problem.Code);
        KafkaEngineLog.DiagnosticsRead(loggers.Catalog, read, key, outcome);
    }

    /// <summary>
    /// One topic's rows, the error that withheld its committed or end offsets as a whole, and the partitions whose only problem is a
    /// first offset that could not be read.
    /// </summary>
    private sealed record TopicPositions(string Topic, IReadOnlyList<KafkaGroupPartition> Rows, WireError? Error, IReadOnlySet<int> FirstOffsetOnly);
}
