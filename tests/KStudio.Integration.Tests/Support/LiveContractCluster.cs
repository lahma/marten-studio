using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Wire;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Support;

/// <summary>
/// The contract cluster over a real broker (decision 12.3): seeding is test code, as <c>admin</c>, and every timestamp is in the past (F7,
/// Kafka 4.0 refuses one an hour ahead). Topics are named <c>ks.{label}.{runId}.{n}</c> and deleted when the cluster is disposed, so a reused
/// broker does not collect them. The engine under test reads as the principal asked for; the seeding is always <c>admin</c>.
/// </summary>
internal sealed class LiveContractCluster : IContractCluster
{
    private readonly KafkaFixture fixture;
    private readonly IAdminClient admin;
    private readonly IProducer<byte[], byte[]> producer;
    private readonly List<string> topics = [];
    private int counter;

    private LiveContractCluster(KafkaFixture fixture, EngineHarness engine)
    {
        this.fixture = fixture;
        Engine = engine;
        admin = new AdminClientBuilder(fixture.Client("admin")).Build();
        producer = new ProducerBuilder<byte[], byte[]>(new ProducerConfig(fixture.Client("admin")) { LingerMs = 5, Acks = Acks.All }).Build();
    }

    public EngineHarness Engine { get; }

    public IKafkaWire Wire => Engine.Wire;

    /// <summary>The broker the cluster seeds, for a test that needs a client of its own.</summary>
    public KafkaFixture Fixture => fixture;

    /// <summary>Opens the cluster for a principal; skips the test when there is no Docker (and fails on CI).</summary>
    /// <param name="fixture">The broker.</param>
    /// <param name="user">The principal the engine reads as.</param>
    /// <param name="requestTimeout">The engine's request timeout; the default when <see langword="null" />.</param>
    public static LiveContractCluster Open(KafkaFixture fixture, string user = "admin", TimeSpan? requestTimeout = null)
    {
        Assert.SkipUnless(DockerAvailability.IsAvailable, DockerAvailability.SkipReason);
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!fixture.Started)
        {
            throw new InvalidOperationException("The Kafka broker of " + fixture.Image + " is not running.");
        }

        return new LiveContractCluster(
            fixture,
            EngineHarness.ForClient(
                fixture.Client(user),
                configure: requestTimeout is { } timeout ? options => options.RequestTimeout = timeout : null));
    }

    public string NewTopic(string label) =>
        $"ks.{label}.{fixture.RunId}.{Interlocked.Increment(ref counter)}";

    public async Task CreateTopicAsync(string topic, int partitions, IReadOnlyDictionary<string, string>? config = null)
    {
        await admin.CreateTopicsAsync(
        [
            new TopicSpecification
            {
                Name = topic,
                NumPartitions = partitions,
                ReplicationFactor = 1,
                Configs = config is null ? null : new Dictionary<string, string>(config),
            },
        ]);

        lock (topics)
        {
            topics.Add(topic);
        }

        // CreateTopics returns before every broker serves the topic's metadata: a DescribeConfigs straight after can answer "unknown topic"
        // (seen once on 3.9.2). Wait, bounded, until the topic is listed (a condition, not a delay).
        SpinWait.SpinUntil(() => TopicExists(fixture, topic), TimeSpan.FromSeconds(30)).Should().BeTrue("the broker lists the topic that was just created");
    }

    public Task<bool> TopicExistsAsync(string topic) => Task.FromResult(TopicExists(fixture, topic));

    /// <summary>Whether the broker lists the topic, asked by an admin client that does not auto-create (all topics, never a single one).</summary>
    /// <param name="fixture">The broker.</param>
    /// <param name="topic">The topic.</param>
    public static bool TopicExists(KafkaFixture fixture, string topic)
    {
        using IAdminClient check = new AdminClientBuilder(new AdminClientConfig(fixture.Client("admin")) { AllowAutoCreateTopics = false }).Build();
        return check.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Any(x => x.Topic == topic && x.Error.Code == ErrorCode.NoError);
    }

    public Task ProduceAsync(string topic, int partition, IReadOnlyList<ContractRecord> records)
    {
        foreach (ContractRecord record in records)
        {
            producer.Produce(new TopicPartition(topic, partition), ToMessage(record));
        }

        producer.Flush(TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task<IContractTransaction> BeginTransactionAsync(string topic, int partition, IReadOnlyList<ContractRecord> records)
    {
        ProducerConfig config = new(fixture.Client("admin"))
        {
            TransactionalId = $"ks-test-{fixture.RunId}-{Interlocked.Increment(ref counter)}",
            TransactionTimeoutMs = 300_000,
            EnableIdempotence = true,
        };

        IProducer<byte[], byte[]> transactional = new ProducerBuilder<byte[], byte[]>(config).Build();

        try
        {
            transactional.InitTransactions(TimeSpan.FromSeconds(30));
            transactional.BeginTransaction();

            foreach (ContractRecord record in records)
            {
                transactional.Produce(new TopicPartition(topic, partition), ToMessage(record));
            }

            transactional.Flush(TimeSpan.FromSeconds(30));
            return Task.FromResult<IContractTransaction>(new LiveTransaction(transactional, () => SettledAsync(topic, partition)));
        }
        catch
        {
            transactional.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits until the transaction's markers are written: committing or aborting returns once the coordinator has decided, and the markers reach
    /// the partitions a moment later, so until then the last stable offset still holds the transaction's records back. A wait on a condition (the
    /// last stable offset reaching the high watermark), bounded at 30 seconds.
    /// </summary>
    private async Task SettledAsync(string topic, int partition)
    {
        WirePartition target = new(topic, partition);
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            long stable = (await Wire.ListOffsetsAsync([target], WireOffsetSpec.Latest, WireIsolation.ReadCommitted, CancellationToken.None)).Value[0].Offset;
            long high = (await Wire.ListOffsetsAsync([target], WireOffsetSpec.Latest, WireIsolation.ReadUncommitted, CancellationToken.None)).Value[0].Offset;

            if (stable == high)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The transaction on {topic}[{partition}] did not settle: the last stable offset is {stable} and the high watermark {high}.");
            }

            await Task.Yield();
        }
    }

    public async Task DeleteRecordsAsync(string topic, int partition, long before) =>
        await admin.DeleteRecordsAsync([new TopicPartitionOffset(topic, partition, before)]);

    public async Task<AppendBatch> ProduceAppendTimeAsync(string topic, int partition, int count, long afterMs)
    {
        TopicPartition target = new(topic, partition);
        DeliveryResult<byte[], byte[]> result;
        long first;

        // Records go one at a time until the broker's own clock reads later than afterMs: a poll of a condition, not an assumption that the
        // test's clock matches the broker's.
        do
        {
            result = await producer.ProduceAsync(target, new Message<byte[], byte[]> { Key = [1], Value = [1] });
            first = result.Offset.Value;
        }
        while (result.Timestamp.UnixTimestampMs <= afterMs);

        long max = result.Timestamp.UnixTimestampMs;
        long end = result.Offset.Value + 1;

        for (int i = 1; i < count; i++)
        {
            result = await producer.ProduceAsync(target, new Message<byte[], byte[]> { Key = [1], Value = [1] });
            max = Math.Max(max, result.Timestamp.UnixTimestampMs);
            end = result.Offset.Value + 1;
        }

        return new AppendBatch(first, end, max);
    }

    /// <summary>Commits a group's offset for a partition, as the host's own consumer would have.</summary>
    /// <param name="group">The group.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="offset">The offset to commit.</param>
    public void CommitGroupOffset(string group, string topic, int partition, long offset)
    {
        ConsumerConfig config = new(fixture.Client("admin")) { GroupId = group, EnableAutoCommit = false };
        using IConsumer<byte[], byte[]> consumer = new ConsumerBuilder<byte[], byte[]>(config).Build();
        consumer.Commit([new TopicPartitionOffset(topic, partition, offset)]);
        consumer.Close();
    }

    public async ValueTask DisposeAsync()
    {
        producer.Dispose();

        string[] created;

        lock (topics)
        {
            created = [.. topics];
        }

        if (created.Length > 0)
        {
            try
            {
                await admin.DeleteTopicsAsync(created);
            }
            catch (DeleteTopicsException)
            {
                // A topic already gone is not a failure of the test that used it.
            }
        }

        admin.Dispose();
        await Engine.DisposeAsync();
    }

    private static Message<byte[], byte[]> ToMessage(ContractRecord record)
    {
        Headers headers = [];

        foreach (KeyValuePair<string, string> header in record.Headers ?? [])
        {
            headers.Add(header.Key, System.Text.Encoding.UTF8.GetBytes(header.Value));
        }

        return new Message<byte[], byte[]>
        {
            Key = record.Key is null ? null! : System.Text.Encoding.UTF8.GetBytes(record.Key),
            Value = record.Value is null ? null! : System.Text.Encoding.UTF8.GetBytes(record.Value),
            Headers = headers,
            Timestamp = new Timestamp(record.TimestampMs, TimestampType.CreateTime),
        };
    }

    private sealed class LiveTransaction(IProducer<byte[], byte[]> producer, Func<Task> settled) : IContractTransaction
    {
        private bool ended;

        public async Task CommitAsync()
        {
            ended = true;
            producer.CommitTransaction();
            await settled();
        }

        public async Task AbortAsync()
        {
            ended = true;
            producer.AbortTransaction();
            await settled();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!ended)
                {
                    producer.AbortTransaction();
                }
            }
            catch (KafkaException)
            {
                // The broker aborts it at the transaction timeout.
            }

            producer.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
