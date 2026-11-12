using System.Text;

using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Tests.Scanning;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Partitioning;

/// <summary>
/// Verification by sampling (D-PAYLOAD decision 7.4): a declared partitioner is a claim, proved over the window before a key search reads
/// fewer partitions. Every outcome, the read from the start of each range, the quota, the owner's budget and the 10-minute negative cache.
/// </summary>
public class PartitionerVerificationTests
{
    private const int Partitions = 6;

    private static ScanFixture Fixture(KafkaPartitioner? declared, out string topic, int partitions = Partitions, int records = 4_000, Action<KafkaEngineOptions>? configure = null)
    {
        ScanFixture fixture = ScanFixture.Create(options => configure?.Invoke(options));
        topic = fixture.Topic("pruning", partitions);

        if (declared is not null)
        {
            fixture.Declare(topic, t => t.Partitioner(declared));
        }

        PartitionedData.Fill(fixture, topic, partitions, declared ?? KafkaPartitioner.Murmur2, records);
        return fixture;
    }

    private static async Task<KafkaTopicPruning> PruningOf(ScanFixture fixture, string topic, string predicate = "key = 'k-0042'")
    {
        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic], predicate, toSeconds: 2_000));
        plan.Errors.Should().BeEmpty();
        plan.Pruning.Should().HaveCount(1);
        return plan.Pruning[0];
    }

    public static TheoryData<string> Partitioners => new() { "murmur2", "crc32", "fnv1a" };

    private static KafkaPartitioner Named(string name) =>
        name switch
        {
            "murmur2" => KafkaPartitioner.Murmur2,
            "crc32" => KafkaPartitioner.Crc32,
            _ => KafkaPartitioner.Fnv1a,
        };

    [Theory]
    [MemberData(nameof(Partitioners))]
    public async Task A_topic_produced_with_each_partitioner_verifies_on_a_thousand_records(string name)
    {
        await using ScanFixture fixture = Fixture(Named(name), out string topic);

        KafkaTopicPruning pruning = await PruningOf(fixture, topic);

        // 4,000 offsets: a target of 1,000 over six ranges is 167 each.
        pruning.Check.Should().Be(new PartitionerCheck.Verified(6 * 167));
        pruning.IsPruned.Should().BeTrue();
        pruning.Partitioner.Should().Be(name);
        pruning.PartitionCount.Should().Be(Partitions);
        pruning.Partitions.Should().Equal(Named(name).Partition(Encoding.UTF8.GetBytes("k-0042"), Partitions));
    }

    [Fact]
    public async Task The_sample_is_read_from_the_start_of_every_non_empty_range_with_explicit_offsets()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);

        await PruningOf(fixture, topic);

        // The first consumer of the plan is the verification's (the size sample for the estimate comes after it).
        FakeScanReaderView reader = new(fixture.Readers[0]);
        reader.Starts.OrderBy(static x => x.Partition).Select(static x => (x.Partition, x.Offset)).Should().Equal(Enumerable.Range(0, Partitions).Select(static p => (p, 0L)));
        reader.Closed.Should().Be(1);
        fixture.Readers[0].Options.Limits.QueueKilobytes.Should().Be(1_024, "the sample's consumer is small, like the size sampler's");
    }

    [Fact]
    public async Task Each_range_stops_at_its_quota_so_a_fast_partition_cannot_use_up_the_sample()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);

        await PruningOf(fixture, topic);

        FakeScanReaderView reader = new(fixture.Readers[0]);
        reader.PausedPartitions.Should().BeEquivalentTo(Enumerable.Range(0, Partitions), "every range reached its quota of 167 and was paused");
    }

    [Fact]
    public async Task One_record_in_a_partition_its_key_does_not_map_to_is_a_mismatch_with_its_offset()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic, records: 0);
        Dictionary<int, List<string>> keys = PartitionedData.KeysByPartition(KafkaPartitioner.Murmur2, Partitions, 700);

        // The record that disagrees is the first one in partition 2: its key belongs in partition 5.
        fixture.Produce(topic, 2, 0, keys[5][0], "stray");

        foreach ((int partition, List<string> list) in keys)
        {
            foreach (string key in list.Skip(partition == 5 ? 1 : 0))
            {
                fixture.Produce(topic, partition, 1 + (list.IndexOf(key) / 6), key, "v");
            }
        }

        KafkaTopicPruning pruning = await PruningOf(fixture, topic, $"key = '{keys[5][3]}'");

        pruning.Check.Should().Be(new PartitionerCheck.Mismatch(2, 0, 5));
        pruning.IsPruned.Should().BeFalse();
        pruning.Partitions.Should().BeEmpty();
        pruning.NotPrunedReason.Should().Be("record 2/0 is in partition 2, and murmur2 maps its key to partition 5");
        pruning.NotPrunedReason.Should().NotContain(keys[5][0], "the key is never shown");
    }

    [Fact]
    public async Task A_mismatch_on_a_redacted_key_does_not_say_which_partition_the_key_maps_to()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic, records: 0, configure: null);
        Dictionary<int, List<string>> keys = PartitionedData.KeysByPartition(KafkaPartitioner.Murmur2, Partitions, 700);
        fixture.Declare(topic, t => t.Partitioner(KafkaPartitioner.Murmur2).Redact(r => r.Key().Searchable()));
        fixture.Produce(topic, 2, 0, keys[5][0], "stray");

        foreach ((int partition, List<string> list) in keys)
        {
            foreach (string key in list.Skip(partition == 5 ? 1 : 0))
            {
                fixture.Produce(topic, partition, 1 + (list.IndexOf(key) / 6), key, "v");
            }
        }

        KafkaTopicPruning pruning = await PruningOf(fixture, topic, $"key = '{keys[5][3]}'");

        pruning.NotPrunedReason.Should().Be("record 2/0 is in partition 2, and murmur2 maps its key to another partition");
    }

    [Fact]
    public async Task Null_keys_are_skipped_and_the_others_still_verify()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic, records: 0);

        for (int i = 0; i < 4_000; i++)
        {
            string? key = i % 2 == 0 ? PartitionedData.Key(i) : null;
            int partition = key is null ? i % Partitions : KafkaPartitioner.Murmur2.Partition(Encoding.UTF8.GetBytes(key), Partitions);
            fixture.Produce(topic, partition, i / 6, key, "v");
        }

        KafkaTopicPruning pruning = await PruningOf(fixture, topic, "key = 'k-0042'");

        PartitionerCheck.Verified verified = pruning.Check.Should().BeOfType<PartitionerCheck.Verified>().Subject;
        verified.Checked.Should().BeInRange(100, (6 * 167) - 1, "a quota of records is read from every range, and the null-keyed ones are not checked");
        pruning.IsPruned.Should().BeTrue();
    }

    [Fact]
    public async Task Fewer_than_a_hundred_keyed_records_is_too_few_to_trust_and_says_how_many()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic, records: 0);

        // 600 offsets, a tenth of them keyed: the quota of 25 per range reads about 150 records and about 15 are keyed.
        for (int i = 0; i < 600; i++)
        {
            string? key = i % 10 == 0 ? PartitionedData.Key(i) : null;
            int partition = key is null ? i % Partitions : KafkaPartitioner.Murmur2.Partition(Encoding.UTF8.GetBytes(key), Partitions);
            fixture.Produce(topic, partition, i / 6, key, "v");
        }

        KafkaTopicPruning pruning = await PruningOf(fixture, topic, "key = 'k-0040'");

        PartitionerCheck.TooFewKeys few = pruning.Check.Should().BeOfType<PartitionerCheck.TooFewKeys>().Subject;
        few.Checked.Should().BeInRange(1, 99);
        pruning.IsPruned.Should().BeFalse();
        pruning.NotPrunedReason.Should().Be($"only {few.Checked:N0} keyed records could be checked; at least 100 are needed");
    }

    [Fact]
    public async Task A_range_that_gave_no_keyed_record_turns_pruning_off_even_when_the_others_gave_plenty()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic, records: 0);
        Dictionary<int, List<string>> keys = PartitionedData.KeysByPartition(KafkaPartitioner.Murmur2, Partitions, 800);

        foreach (int partition in Enumerable.Range(0, Partitions))
        {
            for (int i = 0; i < 800; i++)
            {
                fixture.Produce(topic, partition, i / 6, partition == 5 ? null : keys[partition][i], "v");
            }
        }

        KafkaTopicPruning pruning = await PruningOf(fixture, topic, $"key = '{keys[1][0]}'");

        pruning.Check.Should().Be(new PartitionerCheck.RangeWithoutKeys(5));
        pruning.IsPruned.Should().BeFalse();
        pruning.NotPrunedReason.Should().Be("partition 5 gave no keyed record to check");
    }

    [Fact]
    public async Task A_sample_the_broker_refuses_is_not_readable_and_is_logged_by_kind_only()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);
        fixture.Cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, topic);

        KafkaTopicPruning pruning = await PruningOf(fixture, topic);

        pruning.Check.Should().Be(new PartitionerCheck.NotReadable());
        pruning.IsPruned.Should().BeFalse();
        pruning.NotPrunedReason.Should().Be("the sample could not be read");

        CapturedLogEntry[] logged = [.. fixture.Logs.Entries.Where(static x => x.EventId.Id == 9676)];
        logged.Should().ContainSingle().Which.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Debug);
        logged[0].Message.Should().Contain("NotAuthorized").And.NotContain("k-0042");
    }

    [Fact]
    public async Task A_sample_that_runs_out_of_time_before_a_hundred_records_is_not_readable()
    {
        // The first consumer (the verification's) delivers nothing and the clock then moves past the sample's five seconds.
        ScriptedReader silent = new();
        ScanFixture? self = null;
        int opened = 0;
        silent.EnqueueAction(() => self!.Clock.Advance(PartitionerVerifier.MaxDuration + TimeSpan.FromSeconds(1)));
        self = ScanFixture.Create(wrap: wire => new ScriptedWire(wire, options => Interlocked.Increment(ref opened) == 1 ? silent : wire.OpenReader(options)));
        await using ScanFixture fixture = self;
        string topic = fixture.Topic("pruning", Partitions);
        fixture.Declare(topic, t => t.Partitioner(KafkaPartitioner.Murmur2));
        PartitionedData.Fill(fixture, topic, Partitions, KafkaPartitioner.Murmur2, 1_200);

        KafkaTopicPruning pruning = await PruningOf(fixture, topic);

        pruning.Check.Should().Be(new PartitionerCheck.NotReadable());
        pruning.NotPrunedReason.Should().Be("the sample could not be read");
        fixture.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9676).Which.Message.Should().Contain("Deadline");
    }

    [Fact]
    public async Task A_mismatch_is_kept_for_ten_minutes_and_the_window_is_not_sampled_again_until_then()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic, records: 0);
        Dictionary<int, List<string>> keys = PartitionedData.KeysByPartition(KafkaPartitioner.Murmur2, Partitions, 700);
        fixture.Produce(topic, 0, 0, keys[1][0], "stray");

        foreach ((int partition, List<string> list) in keys)
        {
            foreach (string key in list.Skip(partition == 1 ? 1 : 0))
            {
                fixture.Produce(topic, partition, 1 + (list.IndexOf(key) / 6), key, "v");
            }
        }

        string predicate = $"key = '{keys[1][5]}'";

        (await PruningOf(fixture, topic, predicate)).Check.Should().Be(new PartitionerCheck.Mismatch(0, 0, 1));
        int readers = fixture.Readers.Count;

        (await PruningOf(fixture, topic, predicate)).Check.Should().Be(new PartitionerCheck.Mismatch(0, 0, 1), "the answer is kept");
        fixture.Readers.Should().HaveCount(readers, "no consumer was opened: neither the verification nor the size sample ran again");

        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        await PruningOf(fixture, topic, predicate);
        fixture.Readers.Should().HaveCount(readers, "nine minutes later it is still kept");

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        (await PruningOf(fixture, topic, predicate)).Check.Should().Be(new PartitionerCheck.Mismatch(0, 0, 1));
        fixture.Readers.Count.Should().BeGreaterThan(readers, "after ten minutes the window is sampled again");
    }

    [Fact]
    public async Task A_proof_is_never_reused_so_a_second_pruned_plan_samples_again()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);

        await PruningOf(fixture, topic, "key = 'k-0042'");
        int readers = fixture.Readers.Count;
        await PruningOf(fixture, topic, "key = 'k-0043'");

        fixture.Readers.Count.Should().BeGreaterThan(readers, "partitions added since, or a producer that changed, would make an earlier proof wrong");
    }

    [Fact]
    public async Task The_sampled_bytes_count_against_the_owners_budget()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);
        OwnerByteBudget budget = fixture.Provider.GetRequiredService<OwnerByteBudget>();
        long before = budget.Remaining(fixture.Owner.Key);

        await PruningOf(fixture, topic);

        long spent = before - budget.Remaining(fixture.Owner.Key);
        long verification = 6 * 167 * (6 + PartitionedData.ValueBytes);
        spent.Should().BeGreaterThanOrEqualTo(verification, "1,002 records of 106 bytes were read by the verification");
        spent.Should().BeLessThanOrEqualTo(verification + (RecordSizeSampler.MaxRecords * (6 + PartitionedData.ValueBytes)), "and at most fifty more by the size sample");
    }

    [Fact]
    public async Task The_verification_never_reads_more_than_four_mebibytes()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("pruning", 2);
        fixture.Declare(topic, t => t.Partitioner(KafkaPartitioner.Murmur2));

        // 800 offsets of 30 KiB: a target of 200 records would be 6 MiB, so the read stops at the byte bound after about 140.
        for (int i = 0; i < 800; i++)
        {
            string key = PartitionedData.Key(i);
            fixture.Produce(topic, KafkaPartitioner.Murmur2.Partition(Encoding.UTF8.GetBytes(key), 2), i / 6, key, new string('x', 30 * 1024));
        }

        OwnerByteBudget budget = fixture.Provider.GetRequiredService<OwnerByteBudget>();
        long before = budget.Remaining(fixture.Owner.Key);

        KafkaTopicPruning pruning = await PruningOf(fixture, topic);

        pruning.Check.Should().BeOfType<PartitionerCheck.Verified>("the byte bound still allowed a hundred keyed records");
        long spent = before - budget.Remaining(fixture.Owner.Key);
        spent.Should().BeLessThan(PartitionerVerifier.MaxBytes + RecordSizeSampler.MaxBytes + (3L * 30 * 1024) + 1024, "the verification stopped at four mebibytes, the size sample at one");
        spent.Should().BeGreaterThanOrEqualTo(PartitionerVerifier.MaxBytes, "it did read up to its bound");
    }

    /// <summary>A reader view for the assertions above.</summary>
    private sealed class FakeScanReaderView(Support.FakeKafka.FakeScanReader reader)
    {
        public IReadOnlyList<WirePartitionOffset> Starts => [.. reader.Assigns.SelectMany(static x => x)];

        public int Closed => reader.CloseCount;

        public IReadOnlyList<int> PausedPartitions => [.. reader.PausedLog.Select(static x => x.Partition)];
    }
}
