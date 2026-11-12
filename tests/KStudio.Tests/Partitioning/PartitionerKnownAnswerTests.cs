using System.Text;

using KStudio.Core.Clusters;

namespace KStudio.Tests.Partitioning;

/// <summary>
/// The three hand-written partitioners (D-PAYLOAD decision 7.2) against the known answers of the sources they copy: Apache Kafka's
/// <c>Utils.murmur2</c> test, librdkafka's own unit tests, and the CRC catalogue. The live parity test is the ground truth these vectors
/// cannot be: a real producer's own partitioner on a real broker.
/// </summary>
public class PartitionerKnownAnswerTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private const string Unaligned = "PreAmbleWillBeRemoved,ThePrePartThatIs";

    // librdkafka v2.14.0, src/rdmurmur2.c, unittest_murmur2: java_murmur2_results, the Java client's own answers for these keys.
    public static TheoryData<string, uint> LibrdkafkaMurmur2 => new()
    {
        { "kafka", 0xd067cf64 },
        { "giberish123456789", 0x8f552b0c },
        { "1234", 0x9fc97b14 },
        { "234", 0xe7c009ca },
        { "34", 0x873930da },
        { "4", 0x5a4b5ca1 },
        { Unaligned, 0x78424f1c },
        { Unaligned[1..], 0x4a62b377 },
        { Unaligned[2..], 0xe0e4e09e },
        { Unaligned[3..], 0x62b8b43f },
        { string.Empty, 0x106e08d9 },
    };

    // Apache Kafka 3.9.0, clients/src/test/java/org/apache/kafka/common/utils/UtilsTest.java, testMurmur2 (signed ints, as Java prints them).
    public static TheoryData<string, int> KafkaMurmur2 => new()
    {
        { "21", -973932308 },
        { "foobar", -790332482 },
        { "a-little-bit-long-string", -985981536 },
        { "a-little-bit-longer-string", -1486304829 },
        { "lkjh234lh9fiuh90y23oiuhsafujhadof229phr9h19h89h8", -58897971 },
        { "abc", 479470107 },
    };

    // librdkafka v2.14.0, src/rdfnv1a.c, unittest_fnv1a: golang_hashfnv_results ("acquired via Go's hash/fnv"), in the C file's key order.
    public static TheoryData<string, uint> LibrdkafkaFnv1a => new()
    {
        { "kafka", 0x0d33c4e1 },
        { "giberish123456789", 0x77a58295 },
        { "1234", 0x023bdd03 },
        { "234", 0x2dea3cd2 },
        { "34", 0x740fa83e },
        { "4", 0x310ca263 },
        { Unaligned, 0x65cbd69c },
        { Unaligned[1..], 0x6e49c79a },
        { Unaligned[2..], 0x69eed356 },
        { Unaligned[3..], 0x6abcc023 },
        { string.Empty, 0x7ee3623b },
    };

    // CRC-32/ISO-HDLC check values: "123456789" is the CRC catalogue's check value for the algorithm librdkafka's src/rdcrc32.c states
    // (Width 32, Poly 0x04c11db7, XorIn and XorOut 0xffffffff, reflected in and out); "a" is zlib's crc32("a").
    public static TheoryData<string, uint> Crc32Catalogue => new()
    {
        { "123456789", 0xCBF43926 },
        { string.Empty, 0x00000000 },
        { "a", 0xE8B7BE43 },
    };

    [Theory]
    [MemberData(nameof(LibrdkafkaMurmur2))]
    public void Murmur2_matches_librdkafkas_unit_test(string key, uint expected) => Murmur2Hash.Hash(Bytes(key)).Should().Be(expected);

    [Theory]
    [MemberData(nameof(KafkaMurmur2))]
    public void Murmur2_matches_Apache_Kafkas_Utils_test(string key, int expected) => ((int) Murmur2Hash.Hash(Bytes(key))).Should().Be(expected);

    [Theory]
    [MemberData(nameof(LibrdkafkaFnv1a))]
    public void Fnv1a_matches_librdkafkas_unit_test(string key, uint expected) => Fnv1aHash.Hash(Bytes(key)).Should().Be(expected);

    [Theory]
    [MemberData(nameof(Crc32Catalogue))]
    public void Crc32_matches_the_catalogue_check_values(string key, uint expected) => Crc32Hash.Hash(Bytes(key)).Should().Be(expected);

    [Fact]
    public void The_crc32_table_is_librdkafkas()
    {
        // librdkafka v2.14.0, src/rdcrc32.c, crc_table: the first four entries and the last.
        (Crc32Hash.Entry(0), Crc32Hash.Entry(1), Crc32Hash.Entry(2), Crc32Hash.Entry(3), Crc32Hash.Entry(255))
            .Should().Be((0x00000000u, 0x77073096u, 0xee0e612cu, 0x990951bau, 0x2d02ef8du));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(1_000)]
    public void The_partition_is_the_hash_modulo_the_partition_count_for_every_vector(int partitions)
    {
        foreach (string key in new[] { "kafka", "giberish123456789", "1234", "234", "34", "4", Unaligned, "", "a", "123456789" })
        {
            byte[] bytes = Bytes(key);

            // Murmur2 masks the sign bit first, as Kafka's toPositive and librdkafka's "& 0x7fffffff" do.
            long murmur = Murmur2Hash.Hash(bytes) & 0x7fffffffL;
            KafkaPartitioner.Murmur2.Partition(bytes, partitions).Should().Be((int) (murmur % partitions), $"murmur2 of '{key}' over {partitions}");

            // The other two take the unsigned value, librdkafka's uint32 % int32.
            KafkaPartitioner.Crc32.Partition(bytes, partitions).Should().Be((int) (Crc32Hash.Hash(bytes) % (uint) partitions), $"crc32 of '{key}' over {partitions}");
            KafkaPartitioner.Fnv1a.Partition(bytes, partitions).Should().Be((int) (Fnv1aHash.Hash(bytes) % (uint) partitions), $"fnv1a of '{key}' over {partitions}");

            foreach (KafkaPartitioner partitioner in new[] { KafkaPartitioner.Murmur2, KafkaPartitioner.Crc32, KafkaPartitioner.Fnv1a })
            {
                partitioner.Partition(bytes, partitions).Should().BeInRange(0, partitions - 1);
            }
        }
    }

    [Fact]
    public void A_known_partition_of_the_java_default_partitioner()
    {
        // murmur2("kafka") = 0xd067cf64, so the Java default partitioner puts it in partition (0xd067cf64 & 0x7fffffff) % n.
        KafkaPartitioner.Murmur2.Partition(Bytes("kafka"), 6).Should().Be(0x5067cf64 % 6);
        KafkaPartitioner.Murmur2.Partition(Bytes("kafka"), 12).Should().Be(0x5067cf64 % 12);
    }

    [Fact]
    public void Which_keys_are_deterministic_follows_librdkafkas_partitioners()
    {
        // murmur2_random and fnv1a_random randomise only a null key; consistent_random randomises an empty key as well (src/rdkafka_msg.c).
        KafkaPartitioner.Murmur2.IsDeterministic(ReadOnlySpan<byte>.Empty).Should().BeTrue();
        KafkaPartitioner.Fnv1a.IsDeterministic(ReadOnlySpan<byte>.Empty).Should().BeTrue();
        KafkaPartitioner.Crc32.IsDeterministic(ReadOnlySpan<byte>.Empty).Should().BeFalse();

        foreach (KafkaPartitioner partitioner in new[] { KafkaPartitioner.Murmur2, KafkaPartitioner.Crc32, KafkaPartitioner.Fnv1a })
        {
            partitioner.IsDeterministic(Bytes("k")).Should().BeTrue(partitioner.Name);
        }
    }

    [Fact]
    public void The_built_ins_are_named_for_verdicts()
    {
        (KafkaPartitioner.Murmur2.Name, KafkaPartitioner.Crc32.Name, KafkaPartitioner.Fnv1a.Name).Should().Be(("murmur2", "crc32", "fnv1a"));
    }

    [Fact]
    public void A_host_may_subclass_the_partitioner_and_must_name_it()
    {
        Action blank = () => _ = new Custom(" ");

        blank.Should().Throw<ArgumentException>();
        new Custom("mine").Name.Should().Be("mine");
        new Custom("mine").Partition(Bytes("k"), 4).Should().Be(1);
    }

    [Fact]
    public void The_topic_builder_refuses_a_null_partitioner_and_keeps_the_declared_one_in_its_snapshot()
    {
        KafkaTopicBuilder builder = new();
        Action refuse = () => builder.Partitioner(null!);

        refuse.Should().Throw<ArgumentNullException>();
        builder.PartitionerValue.Should().BeNull();

        builder.Partitioner(KafkaPartitioner.Murmur2).Should().BeSameAs(builder);
        builder.PartitionerValue.Should().BeSameAs(KafkaPartitioner.Murmur2);
        builder.Snapshot().PartitionerValue.Should().BeSameAs(KafkaPartitioner.Murmur2, "the registry keeps a copy that carries the declaration");
        new KafkaTopicDescriptor("t", builder).Partitioner.Should().BeSameAs(KafkaPartitioner.Murmur2);
        new KafkaTopicDescriptor("t", null).Partitioner.Should().BeNull("a topic that declared nothing declared no partitioner");
    }

    private sealed class Custom(string name) : KafkaPartitioner(name)
    {
        public override int Partition(ReadOnlySpan<byte> key, int partitionCount) => 1 % partitionCount;
    }
}
