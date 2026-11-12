using System.Globalization;
using System.Text;

using KStudio.Tests.Scanning;

namespace KStudio.Tests.Partitioning;

/// <summary>Topics produced the way a producer with a known partitioner would, on the fake broker.</summary>
internal static class PartitionedData
{
    /// <summary>The bytes of a record's value in <see cref="Fill" />; the key <c>k-0042</c> is 6 bytes, so a record is 106.</summary>
    public const int ValueBytes = 100;

    public static string Key(int number) => "k-" + number.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>Produces <paramref name="count" /> keyed records to the partitions <paramref name="partitioner" /> maps their keys to, one per "second" in groups of six.</summary>
    public static void Fill(ScanFixture fixture, string topic, int partitions, KafkaPartitioner partitioner, int count)
    {
        for (int i = 0; i < count; i++)
        {
            string key = Key(i);
            int partition = partitioner.Partition(Encoding.UTF8.GetBytes(key), partitions);
            fixture.Produce(topic, partition, i / 6, key, new string('x', ValueBytes));
        }
    }

    /// <summary>Keys that map to each partition, <paramref name="perPartition" /> each, found by trying key after key.</summary>
    public static Dictionary<int, List<string>> KeysByPartition(KafkaPartitioner partitioner, int partitions, int perPartition)
    {
        Dictionary<int, List<string>> keys = Enumerable.Range(0, partitions).ToDictionary(static x => x, static _ => new List<string>());

        for (int i = 0; keys.Values.Any(x => x.Count < perPartition); i++)
        {
            string key = "probe-" + i.ToString(CultureInfo.InvariantCulture);
            List<string> bucket = keys[partitioner.Partition(Encoding.UTF8.GetBytes(key), partitions)];

            if (bucket.Count < perPartition)
            {
                bucket.Add(key);
            }
        }

        return keys;
    }
}
