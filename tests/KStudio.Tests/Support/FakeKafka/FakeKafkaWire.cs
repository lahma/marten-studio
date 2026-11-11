using KStudio.Core.Clusters;
using KStudio.Core.Wire;

namespace KStudio.Tests.Support.FakeKafka;

/// <summary>Builds <see cref="FakeKafkaWire" />s over one fake cluster (decision 3.5): what the fast suite puts first in the container.</summary>
internal sealed class FakeKafkaWireFactory(FakeKafkaCluster cluster) : IKafkaWireFactory
{
    public FakeKafkaCluster Cluster { get; } = cluster;

    public IKafkaWire Create(KafkaClusterDefinition definition) => new FakeKafkaWire(Cluster, definition.Key);
}

/// <summary>A wire over the in-memory cluster: Kafka's semantics as the contract proves them, errors as values.</summary>
internal sealed class FakeKafkaWire(FakeKafkaCluster cluster, string clusterKey) : IKafkaWire
{
    private static readonly string[] AllOperations = ["Describe", "Read", "DescribeConfigs"];

    public string ClusterKey { get; } = clusterKey;

    public FakeKafkaCluster Cluster { get; } = cluster;

    /// <summary>The readers this wire opened, for the tests that assert how they were used.</summary>
    public List<FakeScanReader> Readers { get; } = [];

    public Task<WireResult<WireMetadata>> GetMetadataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cluster.Record(new FakeCall(WireCall.Metadata, [], null, null, string.Empty));

        if (Cluster.TakeFailure(WireCall.Metadata) is { } failure)
        {
            return Task.FromResult(WireResult<WireMetadata>.Fail(failure));
        }

        WireTopicInfo[] topics =
        [
            .. Cluster.AllTopics()
                .Where(topic => !Cluster.IsDenied(KafkaAclOperation.Describe, KafkaResourceType.Topic, topic.Name))
                .Select(topic => new WireTopicInfo(topic.Name, Partitions(topic), null)),
        ];

        return Task.FromResult(WireResult<WireMetadata>.Ok(new WireMetadata(topics)));
    }

    public Task<WireResult<IReadOnlyList<WireTopicDescription>>> DescribeTopicsAsync(
        IReadOnlyList<KafkaTopicName> topics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cluster.Record(new FakeCall(WireCall.DescribeTopics, [], null, null, string.Join(',', topics.Select(static x => x.Value))));

        if (Cluster.TakeFailure(WireCall.DescribeTopics) is { } failure)
        {
            return Task.FromResult(WireResult<IReadOnlyList<WireTopicDescription>>.Fail(failure));
        }

        List<WireTopicDescription> result = [];

        foreach (KafkaTopicName name in topics)
        {
            FakeTopic? topic = Cluster.Topic(name.Value);

            if (topic is null)
            {
                result.Add(new WireTopicDescription(name.Value, false, [], [], new WireError.NotFound(KafkaResourceType.Topic, name.Value)));
            }
            else if (Cluster.IsDenied(KafkaAclOperation.Describe, KafkaResourceType.Topic, name.Value))
            {
                result.Add(new WireTopicDescription(
                    name.Value, false, [], [],
                    new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Topic, name.Value)));
            }
            else
            {
                string[] operations = [.. AllOperations.Where(op => !IsOperationDenied(op, name.Value))];
                result.Add(new WireTopicDescription(name.Value, name.Value.StartsWith("__", StringComparison.Ordinal), Partitions(topic), operations, null));
            }
        }

        return Task.FromResult(WireResult<IReadOnlyList<WireTopicDescription>>.Ok(result));
    }

    public Task<WireResult<WireClusterDescription>> DescribeClusterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cluster.Record(new FakeCall(WireCall.DescribeCluster, [], null, null, string.Empty));

        if (Cluster.TakeFailure(WireCall.DescribeCluster) is { } failure)
        {
            return Task.FromResult(WireResult<WireClusterDescription>.Fail(failure));
        }

        if (Cluster.IsDenied(KafkaAclOperation.Describe, KafkaResourceType.Cluster, "kafka-cluster"))
        {
            return Task.FromResult(WireResult<WireClusterDescription>.Fail(
                new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Cluster, "kafka-cluster")));
        }

        return Task.FromResult(WireResult<WireClusterDescription>.Ok(
            new WireClusterDescription("fake-cluster", 1, [new WireNode(1, "fake", 9092)], ["Describe"])));
    }

    public Task<WireResult<IReadOnlyList<WireOffset>>> ListOffsetsAsync(
        IReadOnlyList<WirePartition> partitions,
        WireOffsetSpec spec,
        WireIsolation isolation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cluster.Record(new FakeCall(WireCall.ListOffsets, [.. partitions], spec, isolation, string.Empty));

        if (Cluster.TakeFailure(WireCall.ListOffsets) is { } failure)
        {
            return Task.FromResult(WireResult<IReadOnlyList<WireOffset>>.Fail(failure));
        }

        // librdkafka refuses a partition twice in one call (F6); so does the fake.
        if (partitions.Distinct().Count() != partitions.Count)
        {
            return Task.FromResult(WireResult<IReadOnlyList<WireOffset>>.Fail(new WireError.Failed("Local_InvalidArg", IsFatal: false)));
        }

        // A broker fails the whole call with Local_NoEnt for a topic it does not know; for one topic that is "not found" (measured on 3.9.2
        // and 4.4.0, REVIEW-ENG-A 2). The same call about several topics is not modelled: the contract does not prove it.
        string[] distinctTopics = [.. partitions.Select(static x => x.Topic).Distinct()];

        if (distinctTopics.Length == 1 && Cluster.Topic(distinctTopics[0]) is null)
        {
            return Task.FromResult(WireResult<IReadOnlyList<WireOffset>>.Fail(new WireError.NotFound(KafkaResourceType.Topic, distinctTopics[0])));
        }

        // librdkafka answers Local_NoEnt for the whole call when no requested partition has a leader; the topic is listed, so it is not
        // "not found" (REVIEW-ENG-A-FIX 1, measured by WireContract on both images).
        if (distinctTopics.Length == 1 && partitions.All(x => x.Partition < 0 || x.Partition >= Cluster.Topic(x.Topic)!.Partitions.Count)
            && !Cluster.IsDenied(KafkaAclOperation.Describe, KafkaResourceType.Topic, distinctTopics[0]))
        {
            return Task.FromResult(WireResult<IReadOnlyList<WireOffset>>.Fail(new WireError.Unavailable("LeaderNotAvailable", Retriable: true)));
        }

        List<WireOffset> result = [];

        lock (Cluster.Gate)
        {
            foreach (WirePartition partition in partitions)
            {
                result.Add(OffsetOf(partition, spec, isolation));
            }
        }

        return Task.FromResult(WireResult<IReadOnlyList<WireOffset>>.Ok(result));
    }

    public Task<WireResult<WireTopicConfig>> DescribeTopicConfigAsync(KafkaTopicName topic, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cluster.Record(new FakeCall(WireCall.DescribeConfigs, [], null, null, topic.Value));

        if (Cluster.TakeFailure(WireCall.DescribeConfigs) is { } failure)
        {
            return Task.FromResult(WireResult<WireTopicConfig>.Fail(failure));
        }

        if (Cluster.IsDenied(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, topic.Value))
        {
            return Task.FromResult(WireResult<WireTopicConfig>.Fail(
                new WireError.NotAuthorized(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, topic.Value)));
        }

        FakeTopic? fake = Cluster.Topic(topic.Value);

        if (fake is null)
        {
            return Task.FromResult(WireResult<WireTopicConfig>.Fail(new WireError.NotFound(KafkaResourceType.Topic, topic.Value)));
        }

        Dictionary<string, string?> entries = new(StringComparer.Ordinal);

        foreach (string key in WireTopicConfig.KnownKeys)
        {
            if (fake.Config.TryGetValue(key, out string? value))
            {
                entries[key] = value;
            }
        }

        return Task.FromResult(WireResult<WireTopicConfig>.Ok(new WireTopicConfig(topic.Value, entries)));
    }

    public Task<WireResult<IReadOnlyList<WireGroupOffset>>> ListGroupOffsetsAsync(
        string groupId, IReadOnlyList<WirePartition> partitions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cluster.Record(new FakeCall(WireCall.GroupOffsets, [.. partitions], null, null, groupId));

        if (Cluster.TakeFailure(WireCall.GroupOffsets) is { } failure)
        {
            return Task.FromResult(WireResult<IReadOnlyList<WireGroupOffset>>.Fail(failure));
        }

        if (Cluster.IsDenied(KafkaAclOperation.Describe, KafkaResourceType.Group, groupId))
        {
            return Task.FromResult(WireResult<IReadOnlyList<WireGroupOffset>>.Fail(
                new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Group, groupId)));
        }

        IReadOnlyList<WireGroupOffset> offsets =
            [.. partitions.Select(x => new WireGroupOffset(x, Cluster.Committed(groupId, x.Topic, x.Partition), null))];
        return Task.FromResult(WireResult<IReadOnlyList<WireGroupOffset>>.Ok(offsets));
    }

    public Task<WireResult<WireGroupDescription>> DescribeGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Cluster.Record(new FakeCall(WireCall.DescribeGroup, [], null, null, groupId));

        if (Cluster.TakeFailure(WireCall.DescribeGroup) is { } failure)
        {
            return Task.FromResult(WireResult<WireGroupDescription>.Fail(failure));
        }

        if (Cluster.IsDenied(KafkaAclOperation.Describe, KafkaResourceType.Group, groupId))
        {
            return Task.FromResult(WireResult<WireGroupDescription>.Fail(
                new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Group, groupId)));
        }

        // An unknown group is "Dead", as Kafka describes it, not an error.
        (string State, int Members)? group = Cluster.Group(groupId);
        return Task.FromResult(WireResult<WireGroupDescription>.Ok(
            new WireGroupDescription(groupId, group?.State ?? "Dead", group?.Members ?? 0, null)));
    }

    public IWireScanReader OpenReader(WireReaderOptions options)
    {
        FakeScanReader reader = new(Cluster, options, "kstudio-" + options.ScanId.ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        lock (Readers)
        {
            Readers.Add(reader);
        }

        return reader;
    }

    private bool IsOperationDenied(string operation, string topic) => operation switch
    {
        "Read" => Cluster.IsDenied(KafkaAclOperation.Read, KafkaResourceType.Topic, topic),
        "DescribeConfigs" => Cluster.IsDenied(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, topic),
        _ => false,
    };

    private static List<WirePartitionInfo> Partitions(FakeTopic topic) =>
        [.. topic.Partitions.Select(static (_, i) => new WirePartitionInfo(i, 1, [1], [1]))];

    /// <summary>One partition's answer (decision 3.5): timestamps are the records' own, control markers carry none, and none found is -1.</summary>
    private WireOffset OffsetOf(WirePartition partition, WireOffsetSpec spec, WireIsolation isolation)
    {
        FakeTopic? topic = Cluster.Topic(partition.Topic);

        if (topic is null || partition.Partition < 0 || partition.Partition >= topic.Partitions.Count)
        {
            return new WireOffset(partition, -1, null, null, new WireError.NotFound(KafkaResourceType.Topic, partition.Topic));
        }

        if (Cluster.IsDenied(KafkaAclOperation.Describe, KafkaResourceType.Topic, partition.Topic))
        {
            return new WireOffset(
                partition, -1, null, null,
                new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Topic, partition.Topic));
        }

        if (Cluster.OffsetFault?.Invoke(partition, spec, isolation) is { } fault)
        {
            return new WireOffset(partition, -1, null, null, fault);
        }

        FakePartition log = topic.Partitions[partition.Partition];
        long limit = isolation == WireIsolation.ReadCommitted ? log.LastStableOffset : log.End;
        long? Stamp(FakeEntry entry) => Cluster.OmitLookupTimestamps ? null : entry.Record!.TimestampMs;

        switch (spec.Kind)
        {
            case WireOffsetSpecKind.Earliest:
                return new WireOffset(partition, log.LogStart, null, 0, null);

            case WireOffsetSpecKind.Latest:
                return new WireOffset(partition, limit, null, 0, null);

            case WireOffsetSpecKind.MaxTimestamp:
                {
                    FakeEntry? best = null;

                    foreach (FakeEntry entry in log.Entries.Where(x => x.Record is not null && x.Offset < limit))
                    {
                        if (best is null || entry.Record!.TimestampMs > best.Record!.TimestampMs)
                        {
                            best = entry;
                        }
                    }

                    return best is null
                        ? new WireOffset(partition, -1, null, null, null)
                        : new WireOffset(partition, best.Offset, Stamp(best), 0, null);
                }

            default:
                {
                    FakeEntry? first = log.Entries.FirstOrDefault(x => x.Record is not null && x.Offset < limit && x.Record.TimestampMs >= spec.TimestampMs);

                    return first is null
                        ? new WireOffset(partition, -1, null, null, null)
                        : new WireOffset(partition, first.Offset, Stamp(first), 0, null);
                }
        }
    }
}
