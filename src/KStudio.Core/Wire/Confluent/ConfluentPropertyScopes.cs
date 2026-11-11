namespace KStudio.Core.Wire.Confluent;

/// <summary>
/// Which configuration keys belong to which role, so that the engine hands each client only keys it understands (decision 2.1).
/// Without this librdkafka logs "is a consumer property and will be ignored" for each such key on the admin client.
/// </summary>
/// <remarks>
/// The two lists are hand-written; <c>ConfluentPropertyScopesTests</c> proves them equal to what Confluent's own
/// <c>ConsumerConfig</c> and <c>ProducerConfig</c> declare, at the floor and at the latest tested version. The few keys that only a
/// newer Confluent declares are listed in <see cref="NewerThanTheFloor" />: filtering them out at the floor too is harmless.
/// </remarks>
internal static class ConfluentPropertyScopes
{
    /// <summary>Keys <c>ConsumerConfig</c> declares and a producer or admin client does not understand.</summary>
    public static IReadOnlySet<string> ConsumerOnly { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "auto.commit.interval.ms",
        "auto.offset.reset",
        "check.crcs",
        "coordinator.query.interval.ms",
        "dotnet.consumer.consume.result.fields",
        "enable.auto.commit",
        "enable.auto.offset.store",
        "enable.partition.eof",
        "fetch.error.backoff.ms",
        "fetch.max.bytes",
        "fetch.min.bytes",
        "fetch.queue.backoff.ms",
        "fetch.wait.max.ms",
        "group.id",
        "group.instance.id",
        "group.protocol",
        "group.protocol.type",
        "group.remote.assignor",
        "heartbeat.interval.ms",
        "isolation.level",
        "max.partition.fetch.bytes",
        "max.poll.interval.ms",
        "max.poll.records",
        "partition.assignment.strategy",
        "queued.max.messages.kbytes",
        "queued.min.messages",
        "session.timeout.ms",
        "share.acknowledgement.mode",
    };

    /// <summary>Keys <c>ProducerConfig</c> declares and a consumer or admin client does not understand.</summary>
    public static IReadOnlySet<string> ProducerOnly { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "batch.num.messages",
        "batch.size",
        "compression.level",
        "compression.type",
        "dotnet.producer.delivery.report.fields",
        "dotnet.producer.enable.background.poll",
        "dotnet.producer.enable.delivery.reports",
        "enable.gapless.guarantee",
        "enable.idempotence",
        "linger.ms",
        "message.send.max.retries",
        "message.timeout.ms",
        "partitioner",
        "queue.buffering.backpressure.threshold",
        "queue.buffering.max.kbytes",
        "queue.buffering.max.messages",
        "request.timeout.ms",
        "sticky.partitioning.linger.ms",
        "transaction.timeout.ms",
        "transactional.id",
    };

    /// <summary>
    /// Keys in <see cref="ConsumerOnly" /> that Confluent.Kafka 2.14.0 (the floor) does not declare and a later version does: the test
    /// allows these, and only these, to be in the list without being declared.
    /// </summary>
    public static IReadOnlySet<string> NewerThanTheFloor { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "max.poll.records",
        "share.acknowledgement.mode",
    };

    /// <summary>The scan consumer's keys: the host's clone minus producer-only keys.</summary>
    /// <param name="clone">The clone.</param>
    public static IReadOnlyDictionary<string, string> ForConsumer(IReadOnlyDictionary<string, string> clone) =>
        Without(clone, ProducerOnly, null);

    /// <summary>The admin client's keys: the host's clone minus consumer-only and producer-only keys.</summary>
    /// <param name="clone">The clone.</param>
    public static IReadOnlyDictionary<string, string> ForAdmin(IReadOnlyDictionary<string, string> clone) =>
        Without(clone, ConsumerOnly, ProducerOnly);

    private static Dictionary<string, string> Without(
        IReadOnlyDictionary<string, string> clone,
        IReadOnlySet<string> first,
        IReadOnlySet<string>? second)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> pair in clone)
        {
            if (!first.Contains(pair.Key) && (second is null || !second.Contains(pair.Key)))
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }
}
