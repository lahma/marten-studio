using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace KStudio.Sample.Kafka;

/// <summary>
/// What the seeder needs of a cluster, in the shape of the producer and admin client it is built on, so that a recording fake stands in for
/// them in the fast tests (D-UI decision 18). This is the <i>sample's</i> producer and consumer, never KStudio's: KStudio reads and never writes.
/// </summary>
internal interface IKafkaSeedTarget
{
    /// <summary>Creates the topics, and returns once every one of them exists. A topic that already exists is not an error.</summary>
    /// <param name="topics">The topics.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task CreateTopicsAsync(IReadOnlyList<SeedTopic> topics, CancellationToken cancellationToken);

    /// <summary>Produces the records in order and returns once every one of them is acknowledged.</summary>
    /// <param name="records">The records.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task ProduceAsync(IReadOnlyList<SeedRecord> records, CancellationToken cancellationToken);

    /// <summary>Commits the offsets of the sample application's own consumer group.</summary>
    /// <param name="commits">The offsets.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task CommitAsync(IReadOnlyList<SeedCommit> commits, CancellationToken cancellationToken);
}

/// <summary>
/// Puts a <see cref="KafkaSeedPlan" /> on a cluster: the topics, then the records, then the sample application's committed offsets, and the
/// marker record last - so that a seed that stopped halfway leaves no marker, and the judge refuses the cluster rather than trusting it.
/// </summary>
internal static class KafkaSeeder
{
    /// <summary>Seeds <paramref name="target" /> with <paramref name="plan" />.</summary>
    /// <param name="target">The cluster.</param>
    /// <param name="plan">What to put on it.</param>
    /// <param name="cancellationToken">Cancels the seeding.</param>
    public static async Task SeedAsync(IKafkaSeedTarget target, KafkaSeedPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);

        await target.CreateTopicsAsync(plan.Topics, cancellationToken).ConfigureAwait(false);

        // The plan ends with the marker; everything else goes first, and the group's offsets before the marker too.
        SeedRecord marker = plan.Records[^1];
        await target.ProduceAsync([.. plan.Records.Take(plan.Records.Count - 1)], cancellationToken).ConfigureAwait(false);
        await target.CommitAsync(plan.Commits, cancellationToken).ConfigureAwait(false);
        await target.ProduceAsync([marker], cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The real cluster: Confluent's admin client, a producer and a consumer that commits and reads nothing.</summary>
/// <param name="config">The cluster's client configuration (its bootstrap servers).</param>
internal sealed class ConfluentSeedTarget(ClientConfig config) : IKafkaSeedTarget
{
    /// <inheritdoc />
    public async Task CreateTopicsAsync(IReadOnlyList<SeedTopic> topics, CancellationToken cancellationToken)
    {
        using IAdminClient admin = new AdminClientBuilder(config).Build();

        try
        {
            await admin.CreateTopicsAsync(
                topics.Select(static topic => new TopicSpecification
                {
                    Name = topic.Name,
                    NumPartitions = topic.Partitions,

                    // The broker's default replication factor: the throwaway cluster has one broker, and a cluster the host configured has its own.
                    ReplicationFactor = -1,
                    Configs = topic.Config?.ToDictionary(static x => x.Key, static x => x.Value),
                })).ConfigureAwait(false);
        }
        catch (CreateTopicsException exception) when (exception.Results.All(static x => x.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // A topic that exists is the answer a reused cluster gives.
        }

        // A created topic is visible to a producer once the metadata says so; a poll of a condition, not a delay.
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<string> present = [.. admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Where(static x => x.Error.Code == ErrorCode.NoError).Select(static x => x.Topic)];

            if (topics.All(topic => present.Contains(topic.Name)))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException("KStudio sample: the topics it created are not visible in the cluster's metadata after 60 seconds.");
            }

            await Task.Yield();
        }
    }

    /// <inheritdoc />
    public Task ProduceAsync(IReadOnlyList<SeedRecord> records, CancellationToken cancellationToken)
    {
        using IProducer<string?, byte[]?> producer = new ProducerBuilder<string?, byte[]?>(
            new ProducerConfig(config) { Acks = Acks.All, LingerMs = 10, EnableIdempotence = true }).Build();

        Error? failure = null;
        int pending = 0;

        foreach (SeedRecord record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Headers headers = [];
            foreach (SeedHeader header in record.Headers)
            {
                headers.Add(header.Name, header.Value);
            }

            Message<string?, byte[]?> message = new()
            {
                Key = record.Key,
                Value = record.Value,
                Headers = headers,
                Timestamp = new Timestamp(record.Timestamp),
            };

            Interlocked.Increment(ref pending);

            while (true)
            {
                try
                {
                    producer.Produce(
                        new TopicPartition(record.Topic, record.Partition),
                        message,
                        report =>
                        {
                            if (report.Error.IsError)
                            {
                                Interlocked.CompareExchange(ref failure, report.Error, null);
                            }

                            Interlocked.Decrement(ref pending);
                        });
                    break;
                }
                catch (ProduceException<string?, byte[]?> exception) when (exception.Error.Code == ErrorCode.Local_QueueFull)
                {
                    producer.Poll(TimeSpan.FromMilliseconds(100));
                }
            }
        }

        producer.Flush(cancellationToken);

        if (Volatile.Read(ref failure) is { } error)
        {
            throw new InvalidOperationException("KStudio sample: producing the seed failed: " + error.Reason);
        }

        if (Volatile.Read(ref pending) != 0)
        {
            throw new InvalidOperationException("KStudio sample: the seed was not fully acknowledged.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CommitAsync(IReadOnlyList<SeedCommit> commits, CancellationToken cancellationToken)
    {
        foreach (IGrouping<string, SeedCommit> group in commits.GroupBy(static x => x.Group, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The sample application's own consumer group commits what it has handled, without joining the group: an offset commit needs no assignment.
            using IConsumer<Ignore, Ignore> consumer = new ConsumerBuilder<Ignore, Ignore>(
                new ConsumerConfig(config) { GroupId = group.Key, EnableAutoCommit = false }).Build();

            consumer.Commit(group.Select(static x => new TopicPartitionOffset(x.Topic, x.Partition, x.Offset)));
        }

        return Task.CompletedTask;
    }
}
