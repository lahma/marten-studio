using System.Buffers.Binary;

namespace KStudio;

/// <summary>
/// How a producer picks a record's partition from its key (AGENTS.md D39). Declaring one is a claim: the engine samples the window before
/// it trusts it, and one record that disagrees turns pruning off for the topic.
/// </summary>
public abstract class KafkaPartitioner
{
    /// <summary>Names the partitioner.</summary>
    /// <param name="name">The name verdicts use.</param>
    protected KafkaPartitioner(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The name verdicts use: murmur2, crc32, fnv1a.</summary>
    public string Name { get; }

    /// <summary>The Java clients' partitioner for keyed records, librdkafka's murmur2 and murmur2_random, and Dekaf's.</summary>
    public static KafkaPartitioner Murmur2 { get; } = new Murmur2Partitioner();

    /// <summary>librdkafka's consistent and consistent_random (Confluent.Kafka's default): CRC32 of a non-empty key.</summary>
    public static KafkaPartitioner Crc32 { get; } = new Crc32Partitioner();

    /// <summary>librdkafka's fnv1a and fnv1a_random, and Sarama's hash partitioner.</summary>
    public static KafkaPartitioner Fnv1a { get; } = new Fnv1aPartitioner();

    /// <summary>The partition of a key; called only for a key <see cref="IsDeterministic" /> accepts.</summary>
    /// <param name="key">The key's bytes.</param>
    /// <param name="partitionCount">How many partitions the topic has.</param>
    /// <returns>The partition, from 0 to <paramref name="partitionCount" /> minus 1.</returns>
    public abstract int Partition(ReadOnlySpan<byte> key, int partitionCount);

    /// <summary>Whether the key alone decides the partition (false where the producer picks one at random).</summary>
    /// <param name="key">The key's bytes.</param>
    /// <returns><see langword="true" /> unless the producer picks the partition of this key at random.</returns>
    public virtual bool IsDeterministic(ReadOnlySpan<byte> key) => true;

    private sealed class Murmur2Partitioner() : KafkaPartitioner("murmur2")
    {
        // murmur2_random randomises only a null key; an empty key is hashed (librdkafka v2.14.0, src/rdkafka_msg.c).
        public override int Partition(ReadOnlySpan<byte> key, int partitionCount) =>
            (int) ((Murmur2Hash.Hash(key) & 0x7fffffffu) % (uint) partitionCount);
    }

    private sealed class Crc32Partitioner() : KafkaPartitioner("crc32")
    {
        public override int Partition(ReadOnlySpan<byte> key, int partitionCount) =>
            (int) (Crc32Hash.Hash(key) % (uint) partitionCount);

        // consistent_random randomises an empty key as well as a null one (librdkafka v2.14.0, src/rdkafka_msg.c).
        public override bool IsDeterministic(ReadOnlySpan<byte> key) => !key.IsEmpty;
    }

    private sealed class Fnv1aPartitioner() : KafkaPartitioner("fnv1a")
    {
        public override int Partition(ReadOnlySpan<byte> key, int partitionCount) =>
            (int) (Fnv1aHash.Hash(key) % (uint) partitionCount);
    }
}

/// <summary>MurmurHash2 as Kafka's <c>Utils.murmur2</c> and librdkafka's <c>rd_murmur2</c> write it (hand-written: no package, hard rule 1).</summary>
internal static class Murmur2Hash
{
    /// <summary>The 32-bit hash of <paramref name="data" />; the partition masks the sign bit.</summary>
    /// <param name="data">The key's bytes.</param>
    public static uint Hash(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            const uint seed = 0x9747b28c;
            const uint m = 0x5bd1e995;
            const int r = 24;

            uint h = seed ^ (uint) data.Length;
            int i = 0;

            for (; i + 4 <= data.Length; i += 4)
            {
                uint k = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
                k *= m;
                k ^= k >> r;
                k *= m;
                h *= m;
                h ^= k;
            }

            switch (data.Length - i)
            {
                case 3:
                    h ^= (uint) data[i + 2] << 16;
                    goto case 2;
                case 2:
                    h ^= (uint) data[i + 1] << 8;
                    goto case 1;
                case 1:
                    h ^= data[i];
                    h *= m;
                    break;
            }

            h ^= h >> 13;
            h *= m;
            h ^= h >> 15;
            return h;
        }
    }
}

/// <summary>CRC-32/ISO-HDLC (zlib's), as librdkafka's <c>rd_crc32</c>: reflected polynomial 0xEDB88320, initial and final xor 0xFFFFFFFF.</summary>
internal static class Crc32Hash
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>The CRC of <paramref name="data" />.</summary>
    /// <param name="data">The key's bytes.</param>
    public static uint Hash(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>The table entry for a byte, for the test that compares the table with librdkafka's.</summary>
    /// <param name="index">The byte.</param>
    public static uint Entry(int index) => Table[index];

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint c = i;

            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}

/// <summary>
/// 32-bit FNV-1a as librdkafka's <c>rd_fnv1a</c> writes it: the hash is taken as a signed 32-bit value and made positive by negation, to
/// match Sarama's default hash partitioner (<c>int.MinValue</c> stays itself).
/// </summary>
internal static class Fnv1aHash
{
    /// <summary>The hash of <paramref name="data" />, as librdkafka returns it.</summary>
    /// <param name="data">The key's bytes.</param>
    public static uint Hash(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            int h = (int) 0x811C9DC5u;

            foreach (byte b in data)
            {
                h ^= b;
                h *= 0x01000193;
            }

            if (h < 0)
            {
                h = -h;
            }

            return (uint) h;
        }
    }
}
