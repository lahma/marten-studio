using Confluent.Kafka;

namespace KStudio.Integration.Tests.Support;

/// <summary>
/// The tests' own reading of a group's committed offsets, with a consumer that carries the group's id and is never subscribed (an OffsetFetch
/// and nothing else). It is never the engine's path, and it is not an admin client's <c>ListConsumerGroupOffsets</c> either: on an admin handle
/// librdkafka 2.16.0 and earlier crash the process when the coordinator answers <c>NOT_COORDINATOR</c> (issue #5040, fixed by PR #5611), which a
/// pristine broker does just after it creates <c>__consumer_offsets</c>.
/// </summary>
internal static class LiveGroupOffsets
{
    /// <summary>The offsets the group has committed for the partitions; a partition with none is absent.</summary>
    /// <param name="fixture">The broker.</param>
    /// <param name="groupId">The group.</param>
    /// <param name="partitions">The partitions to ask about.</param>
    public static IReadOnlyList<TopicPartitionOffset> Committed(KafkaFixture fixture, string groupId, IReadOnlyList<TopicPartition> partitions)
    {
        ConsumerConfig config = new(fixture.Client("admin")) { GroupId = groupId, EnableAutoCommit = false };
        using IConsumer<byte[], byte[]> consumer = new ConsumerBuilder<byte[], byte[]>(config).Build();

        return [.. consumer.Committed(partitions, TimeSpan.FromSeconds(30)).Where(static x => x.Offset.Value >= 0)];
    }

    /// <summary>The offsets the group has committed on any topic a host could see (the broker's own <c>__</c> topics are not asked about).</summary>
    /// <param name="fixture">The broker.</param>
    /// <param name="groupId">The group.</param>
    public static IReadOnlyList<TopicPartitionOffset> CommittedAnywhere(KafkaFixture fixture, string groupId)
    {
        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig(fixture.Client("admin")) { AllowAutoCreateTopics = false }).Build();

        TopicPartition[] all =
        [
            .. admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics
                .Where(static x => !x.Topic.StartsWith("__", StringComparison.Ordinal))
                .SelectMany(static x => x.Partitions.Select(p => new TopicPartition(x.Topic, p.PartitionId))),
        ];

        return all.Length == 0 ? [] : Committed(fixture, groupId, all);
    }
}
