using System.Collections.Concurrent;
using System.Text;

using Confluent.Kafka;

using KStudio.Integration.Tests.Support;

namespace KStudio.Integration.Tests.Partitioning;

/// <summary>
/// The ground truth the known-answer vectors cannot be (D-PAYLOAD decision 7.7): a real Confluent.Kafka producer, with one of librdkafka's
/// own partitioners, writes 2,000 keys to a real broker, and every partition the broker reports equals what <see cref="KafkaPartitioner" />
/// computes for the same key, at two partition counts. The producer belongs to this harness and never to the engine (hard rule 17).
/// </summary>
public abstract class PartitionerParityLiveTests(KafkaFixture fixture)
{
    private const int KeyCount = 2_000;

    /// <summary>ASCII, non-ASCII, binary, one byte and long keys; empty only where the partitioner decides it (<paramref name="emptyKey" />).</summary>
    private static byte[][] Keys(bool emptyKey)
    {
        Random random = new(20261112);
        List<byte[]> keys = [];

        for (int i = 0; i < 600; i++)
        {
            keys.Add(Encoding.UTF8.GetBytes($"key-{i:D5}"));
        }

        for (int i = 0; i < 400; i++)
        {
            keys.Add(Encoding.UTF8.GetBytes($"ünï-č-{i}-日本語-😀"));
        }

        for (int i = 0; i < 500; i++)
        {
            byte[] binary = new byte[1 + random.Next(40)];
            random.NextBytes(binary);
            keys.Add(binary);
        }

        for (int i = 0; i < 256; i++)
        {
            keys.Add([(byte) i]);
        }

        while (keys.Count < KeyCount - 1)
        {
            byte[] longKey = new byte[200 + random.Next(800)];
            random.NextBytes(longKey);
            keys.Add(longKey);
        }

        keys.Add(emptyKey ? [] : Encoding.UTF8.GetBytes("the-last-key"));
        return [.. keys];
    }

    private async Task AssertParityAsync(string producerPartitioner, KafkaPartitioner partitioner, bool emptyKey)
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        byte[][] keys = Keys(emptyKey);

        foreach (int partitions in new[] { 6, 7 })
        {
            string topic = admin.NewTopic($"parity-{partitioner.Name}-{partitions}");
            await admin.CreateTopicAsync(topic, partitions);

            ProducerConfig config = new(fixture.Client("admin")) { LingerMs = 5, Acks = Acks.All };

            // By its librdkafka name: Confluent.Kafka's Partitioner enum has no Fnv1a member, and the setting is the same string.
            config.Set("partitioner", producerPartitioner);
            ConcurrentBag<(int Index, int Partition)> delivered = [];
            int failures = 0;

            using (IProducer<byte[], byte[]> producer = new ProducerBuilder<byte[], byte[]>(config).Build())
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    int index = i;
                    producer.Produce(
                        topic,
                        new Message<byte[], byte[]> { Key = keys[i], Value = Encoding.ASCII.GetBytes("v") },
                        report =>
                        {
                            if (report.Error.IsError)
                            {
                                Interlocked.Increment(ref failures);
                            }
                            else
                            {
                                delivered.Add((index, report.Partition.Value));
                            }
                        });
                }

                producer.Flush(TimeSpan.FromSeconds(60));
            }

            failures.Should().Be(0);
            delivered.Should().HaveCount(keys.Length);

            foreach ((int index, int partition) in delivered)
            {
                partitioner.IsDeterministic(keys[index]).Should().BeTrue($"key {index} was produced for the comparison");
                partitioner.Partition(keys[index], partitions).Should().Be(
                    partition,
                    $"{partitioner.Name} over {partitions} partitions must agree with librdkafka for key {index} ({Convert.ToHexString(keys[index])[..Math.Min(32, keys[index].Length * 2)]}...)");
            }
        }
    }

    [Fact]
    public Task Murmur2_agrees_with_librdkafkas_murmur2_for_every_key_at_six_and_seven_partitions() =>
        AssertParityAsync("murmur2", KafkaPartitioner.Murmur2, emptyKey: true);

    [Fact]
    public Task Crc32_agrees_with_librdkafkas_consistent_for_every_key_at_six_and_seven_partitions() =>
        AssertParityAsync("consistent", KafkaPartitioner.Crc32, emptyKey: false);

    [Fact]
    public Task Fnv1a_agrees_with_librdkafkas_fnv1a_for_every_key_at_six_and_seven_partitions() =>
        AssertParityAsync("fnv1a", KafkaPartitioner.Fnv1a, emptyKey: true);
}

/// <summary><see cref="PartitionerParityLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class PartitionerParityLiveTests39(Kafka39Fixture fixture) : PartitionerParityLiveTests(fixture);

/// <summary><see cref="PartitionerParityLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class PartitionerParityLiveTests4(Kafka4Fixture fixture) : PartitionerParityLiveTests(fixture);
