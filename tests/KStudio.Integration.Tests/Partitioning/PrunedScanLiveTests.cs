using System.Text;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Predicates;
using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;

namespace KStudio.Integration.Tests.Partitioning;

/// <summary>
/// A verified partitioner against real brokers (D-PAYLOAD decision 7.7): a topic a real murmur2 producer wrote is pruned to the one
/// partition its key maps to and the record is found there; a round-robin topic whose host claims murmur2 is caught by the sample; and a
/// topic whose partitions were raised after the first records were written is caught by a window that covers both.
/// </summary>
public abstract class PrunedScanLiveTests(KafkaFixture fixture)
{
    private const int Partitions = 6;
    private const int Records = 2_000;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Key(int number) => "k-" + number.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    private static void Declare(KafkaEngineOptions options, string topic) => options.Topic(topic, t => t.Partitioner(KafkaPartitioner.Murmur2));

    private ProducerConfig Producer() => new(fixture.Client("admin")) { Partitioner = Confluent.Kafka.Partitioner.Murmur2, LingerMs = 5, Acks = Acks.All };

    private void ProduceKeyed(string topic, int from, int to)
    {
        // A producer made now has the topic's current partition count: one made before a partition was added would not know it.
        using IProducer<byte[], byte[]> producer = new ProducerBuilder<byte[], byte[]>(Producer()).Build();

        for (int i = from; i < to; i++)
        {
            producer.Produce(topic, new Message<byte[], byte[]> { Key = Encoding.UTF8.GetBytes(Key(i)), Value = Encoding.ASCII.GetBytes("v" + i) });
        }

        producer.Flush(TimeSpan.FromSeconds(60)).Should().Be(0);
    }

    [Fact]
    public async Task A_murmur2_topic_is_a_point_lookup_that_reads_one_partition_and_finds_the_record()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("pruned-point");
        await admin.CreateTopicAsync(topic, Partitions);
        ProduceKeyed(topic, 0, Records);
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", o => Declare(o, topic));

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(LiveScanEngine.Request([topic], "key = 'k-0042'"), Token);

        int partition = KafkaPartitioner.Murmur2.Partition(Encoding.UTF8.GetBytes(Key(42)), Partitions);
        plan.Verdicts.Single().Kind.Should().Be(ClauseVerdictKind.PointLookup);
        plan.Verdicts.Single().Description.Should().StartWith($"one partition: murmur2 maps the key to partition {partition} of {Partitions} (verified on ");
        plan.Ranges.Should().ContainSingle().Which.Partition.Should().Be(partition);
        result.Partitions.Should().ContainSingle("the progress lists one partition");
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        matches.Should().ContainSingle().Which.Partition.Should().Be(partition);
        result.PruningSentences.Should().ContainSingle().Which.Should().StartWith($"This search read only partition {partition} of {Partitions} of {topic} (murmur2, verified on ");
    }

    [Fact]
    public async Task A_round_robin_topic_that_claims_murmur2_is_caught_by_the_sample_and_every_partition_is_read()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("pruned-roundrobin");
        await admin.CreateTopicAsync(topic, Partitions);

        // Round-robin by record number: record i goes to partition i % 6, whatever its key hashes to.
        for (int partition = 0; partition < Partitions; partition++)
        {
            await admin.ProduceAsync(topic, partition, [.. Enumerable.Range(0, Records / Partitions).Select(i => LiveRecords.At(i, Key((i * Partitions) + partition), "v"))]);
        }

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", o => Declare(o, topic));

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(LiveScanEngine.Request([topic], "key = 'k-0042'"), Token);

        plan.Pruning.Single().Check.Should().BeOfType<PartitionerCheck.Mismatch>();
        plan.Verdicts.Single().Description.Should().Contain("; not pruned: record ").And.Contain(" is in partition ").And.Contain(", and murmur2 maps its key to partition ");
        plan.Ranges.Should().HaveCount(Partitions);
        result.Partitions.Should().HaveCount(Partitions);
        matches.Should().ContainSingle("the record is found, because every partition was read");
    }

    [Fact]
    public async Task A_window_that_covers_records_written_before_the_partitions_were_raised_is_a_mismatch()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("pruned-raised");
        await admin.CreateTopicAsync(topic, 3);
        ProduceKeyed(topic, 0, 600);

        using (IAdminClient client = new AdminClientBuilder(fixture.Client("admin")).Build())
        {
            await client.CreatePartitionsAsync([new PartitionsSpecification { Topic = topic, IncreaseTo = Partitions }]);
            SpinWait.SpinUntil(
                () => client.GetMetadata(topic, TimeSpan.FromSeconds(10)).Topics.Any(x => x.Topic == topic && x.Partitions.Count == Partitions),
                TimeSpan.FromSeconds(60)).Should().BeTrue("the broker serves six partitions");
        }

        ProduceKeyed(topic, 600, 1_200);
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", o => Declare(o, topic));

        KafkaScanPlan plan = await engine.PlanAsync(LiveScanEngine.Request([topic], "key = 'k-0042'"), Token);

        plan.Pruning.Single().Check.Should().BeOfType<PartitionerCheck.Mismatch>("the first records of partitions 0 to 2 were mapped over three partitions, not six");
        plan.Pruning.Single().IsPruned.Should().BeFalse();
        plan.Ranges.Should().HaveCount(Partitions);
    }
}

/// <summary><see cref="PrunedScanLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class PrunedScanLiveTests39(Kafka39Fixture fixture) : PrunedScanLiveTests(fixture);

/// <summary><see cref="PrunedScanLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class PrunedScanLiveTests4(Kafka4Fixture fixture) : PrunedScanLiveTests(fixture);
