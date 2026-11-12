using System.Text;

using KStudio.Core.Predicates;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Core.Windows;
using KStudio.Tests.Scanning;

namespace KStudio.Tests.Partitioning;

/// <summary>
/// A verified partitioner in a scan (D-PAYLOAD decisions 7.5 and 7.6): the plan's ranges, estimate and cursor cover only the pruned
/// partitions, every verdict and note sentence word for word, <c>DisablePruning</c>, a continued scan and a pruned negative.
/// </summary>
public class PrunedScanTests
{
    private const int Partitions = 6;
    private const string Verified = "verified on 1,002 records";

    private static ScanFixture Fixture(KafkaPartitioner? declared, out string topic, int records = 4_000, Action<KafkaTopicBuilder>? configure = null)
    {
        ScanFixture fixture = ScanFixture.Create();
        topic = fixture.Topic("pruned", Partitions);

        if (declared is not null || configure is not null)
        {
            fixture.Declare(topic, t =>
            {
                if (declared is not null)
                {
                    t.Partitioner(declared);
                }

                configure?.Invoke(t);
            });
        }

        PartitionedData.Fill(fixture, topic, Partitions, declared ?? KafkaPartitioner.Murmur2, records);
        return fixture;
    }

    private static int PartitionOf(string key, KafkaPartitioner? partitioner = null) =>
        (partitioner ?? KafkaPartitioner.Murmur2).Partition(Encoding.UTF8.GetBytes(key), Partitions);

    private static KafkaScanRequest Request(string topic, string predicate, KafkaScanCursor? cursor = null, KafkaScanLowering? lower = null) =>
        ScanFixture.Request([topic], predicate, toSeconds: 2_000, cursor: cursor, lower: lower);

    private static string KeyVerdict(KafkaScanPlan plan, string topic) =>
        plan.Verdicts.Single(x => x.Topic == topic && x.Field == "key").Description;

    [Fact]
    public async Task A_pruned_plan_reads_the_one_partition_the_key_maps_to_and_estimates_only_it()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);
        int partition = PartitionOf("k-0042");

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'k-0042'"));

        plan.Ranges.Should().ContainSingle().Which.Partition.Should().Be(partition);
        plan.Estimate.Partitions.Should().Be(1);
        plan.Estimate.Offsets.Should().Be(fixture.Cluster.Topic(topic)!.Partitions[partition].End, "the estimate covers exactly what is read");
        plan.Pruning.Should().ContainSingle().Which.Partitions.Should().Equal(partition);
    }

    [Fact]
    public async Task A_pruned_scan_finds_what_a_full_scan_finds_and_reads_a_sixth_of_it()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);

        (KafkaScanPlan plan, KafkaScanResult pruned, IReadOnlyList<KafkaMatch> prunedMatches) = await fixture.RunAsync(Request(topic, "key = 'k-0042'"));
        (_, KafkaScanResult full, IReadOnlyList<KafkaMatch> fullMatches) = await fixture.RunAsync(Request(topic, "key = 'k-0042'") with { DisablePruning = true });

        plan.Pruning[0].IsPruned.Should().BeTrue();
        prunedMatches.Select(static x => (x.Partition, x.Offset)).Should().Equal(fullMatches.Select(static x => (x.Partition, x.Offset)));
        pruned.Partitions.Should().HaveCount(1);
        pruned.Records.Should().BeLessThan(full.Records / 3);
        full.Partitions.Should().HaveCount(Partitions);
        full.Pruning.Single().IsPruned.Should().BeFalse();
    }

    [Fact]
    public async Task One_partition_is_a_point_lookup_and_says_what_was_proved()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);
        int partition = PartitionOf("k-0042");

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'k-0042'"));

        ClauseVerdict verdict = plan.Verdicts.Single();
        verdict.Kind.Should().Be(ClauseVerdictKind.PointLookup);
        verdict.Description.Should().Be($"one partition: murmur2 maps the key to partition {partition} of 6 ({Verified})");
    }

    [Fact]
    public async Task Several_partitions_are_listed_and_the_key_is_read_in_each()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);
        Dictionary<int, List<string>> keys = PartitionedData.KeysByPartition(KafkaPartitioner.Murmur2, Partitions, 1);

        foreach (int partition in new[] { 1, 3, 4 })
        {
            fixture.Produce(topic, partition, 700, keys[partition][0], "late");
        }

        string predicate = $"key in ('{keys[4][0]}', '{keys[1][0]}', '{keys[3][0]}')";

        (KafkaScanPlan plan, _, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Request(topic, predicate));

        ClauseVerdict verdict = plan.Verdicts.Single();
        verdict.Kind.Should().Be(ClauseVerdictKind.PartitionPruned);
        verdict.Description.Should().Be($"pruned to partitions 1, 3 and 4 of 6 (murmur2, {Verified})");
        plan.Ranges.Select(static x => x.Partition).Should().Equal(1, 3, 4);
        plan.Notes.Select(static x => x.Text).Should().Contain($"Only partitions 1, 3 and 4 of {topic} are read: murmur2 maps the key to them, verified on 1,002 records of this window.");
        matches.Should().HaveCount(3);
    }

    [Fact]
    public async Task The_plan_says_in_a_note_which_partition_is_read()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);
        int partition = PartitionOf("k-0042");

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'k-0042'"));

        plan.Notes.Select(static x => x.Text).Should().Contain($"Only partition {partition} of {topic} is read: murmur2 maps the key to it, verified on 1,002 records of this window.");
    }

    [Fact]
    public async Task Only_the_clause_that_fixes_the_key_is_pruned_and_a_contains_beside_it_stays_a_scan()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'k-0042' and key ~ 'k-'"));

        plan.Verdicts.Select(static x => x.Kind).Should().BeEquivalentTo([ClauseVerdictKind.PointLookup, ClauseVerdictKind.SlowScan]);
        plan.Verdicts.Single(static x => x.Kind == ClauseVerdictKind.SlowScan).Description.Should().StartWith("slow: each key is formatted as text");
    }

    [Fact]
    public async Task A_topic_that_declares_no_partitioner_says_so_on_its_key_verdict_and_reads_every_partition()
    {
        await using ScanFixture fixture = Fixture(null, out string topic);

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'k-0042'"));

        KeyVerdict(plan, topic).Should().Be("compared as bytes (Utf8String, assumed); not pruned: the topic declares no partitioner");
        plan.Ranges.Should().HaveCount(Partitions);
        plan.Pruning.Single().IsPruned.Should().BeFalse();
        plan.Verdicts.Single().Kind.Should().Be(ClauseVerdictKind.Scan);
    }

    [Fact]
    public async Task A_search_that_does_not_fix_the_key_says_so_and_a_search_with_no_key_clause_says_nothing()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);

        KafkaScanPlan prefix = await fixture.PlanAsync(Request(topic, "key ^= 'k-004'"));
        KeyVerdict(prefix, topic).Should().Be("compared as bytes (Utf8String, assumed); not pruned: the search does not fix the key with = or in");
        prefix.Ranges.Should().HaveCount(Partitions);

        KafkaScanPlan header = await fixture.PlanAsync(Request(topic, "header.h = 'y'"));
        header.Pruning.Should().BeEmpty("there is no key clause to explain");
        header.Verdicts.Single().Description.Should().Be("compared as bytes");
        fixture.Readers.Should().HaveCount(1, "only the first plan's size sample, reused by the second: no verification ran for either");
    }

    [Fact]
    public async Task A_json_string_key_with_characters_serializers_escape_differently_is_not_pruned()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic, configure: t => t.Key(KafkaKeyFormat.JsonString));

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'café'"));

        KeyVerdict(plan, topic).Should().Be(
            "compared as bytes; a key with escapes is decoded (JsonString); not pruned: a JSON-string key with characters other than letters, digits, space and -_.:@ is escaped differently by different serializers");
        plan.Ranges.Should().HaveCount(Partitions);
    }

    [Fact]
    public async Task An_empty_key_is_not_pruned_under_a_partitioner_that_randomises_it()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Crc32, out string topic);

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key in ('k-0042', '')"));

        KeyVerdict(plan, topic).Should().EndWith("; not pruned: crc32 does not decide the partition of an empty key");
        plan.Ranges.Should().HaveCount(Partitions);
    }

    [Fact]
    public async Task A_window_of_fewer_than_four_hundred_offsets_is_read_whole_without_sampling()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic, records: 399);

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'k-0042'"));

        KeyVerdict(plan, topic).Should().EndWith("; not pruned: the window has fewer than 400 offsets, so it is read whole");
        plan.Pruning.Single().Check.Should().BeNull("nothing was sampled");
        fixture.Readers.Should().HaveCount(1, "only the size sample read");

        // At 400 it is worth proving: the quota is a hundred records over six ranges.
        await using ScanFixture exactly = Fixture(KafkaPartitioner.Murmur2, out string other, records: 400);

        (await exactly.PlanAsync(Request(other, "key = 'k-0042'"))).Pruning.Single().Check.Should().BeOfType<PartitionerCheck.Verified>();
    }

    [Fact]
    public async Task A_mismatch_names_the_record_on_the_verdict_and_every_partition_is_read()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic, records: 0);
        Dictionary<int, List<string>> keys = PartitionedData.KeysByPartition(KafkaPartitioner.Murmur2, Partitions, 700);
        fixture.Produce(topic, 2, 0, keys[5][0], "stray");

        foreach ((int partition, List<string> list) in keys)
        {
            foreach (string key in list.Skip(partition == 5 ? 1 : 0))
            {
                fixture.Produce(topic, partition, 1 + (list.IndexOf(key) / 6), key, "v");
            }
        }

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, $"key = '{keys[1][0]}'"));

        KeyVerdict(plan, topic).Should().EndWith("; not pruned: record 2/0 is in partition 2, and murmur2 maps its key to partition 5");
        plan.Ranges.Should().HaveCount(Partitions);
        plan.Notes.Select(static x => x.Text).Should().NotContain(static x => x.StartsWith("Only partition", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sample_the_broker_refuses_says_it_could_not_be_read_on_the_verdict()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);
        fixture.Cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, topic);

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'k-0042'"));

        KeyVerdict(plan, topic).Should().EndWith("; not pruned: the sample could not be read");
    }

    [Fact]
    public async Task Asking_for_every_partition_reads_every_partition_and_samples_nothing()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'k-0042'") with { DisablePruning = true });

        KeyVerdict(plan, topic).Should().EndWith("; not pruned: you asked to search every partition");
        plan.Ranges.Should().HaveCount(Partitions);
        plan.Estimate.Partitions.Should().Be(Partitions);
        plan.Pruning.Single().Check.Should().BeNull();
        fixture.Readers.Should().HaveCount(1, "only the size sample read");
    }

    [Fact]
    public async Task A_pruned_negative_carries_what_it_did_not_read_in_its_result()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);
        int partition = PartitionOf("no-such-key");

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Request(topic, "key = 'no-such-key'"));

        matches.Should().BeEmpty();
        result.Matches.Should().Be(0);
        result.Pruning.Single().IsPruned.Should().BeTrue();
        result.PruningSentences.Should().Equal($"This search read only partition {partition} of 6 of {topic} (murmur2, {Verified}).");
        result.PruningAuditText.Should().Be($"; pruned to partition {partition} of {topic}");
        result.Notes.Select(static x => x.Text).Should().Contain(static x => x.StartsWith("Only partition ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scan_that_was_not_pruned_has_nothing_to_add_to_its_caveats_or_its_audit_text()
    {
        await using ScanFixture fixture = Fixture(null, out string topic);

        (_, KafkaScanResult result, _) = await fixture.RunAsync(Request(topic, "key = 'k-0042'"));

        result.PruningSentences.Should().BeEmpty();
        result.PruningAuditText.Should().BeEmpty();
    }

    [Fact]
    public async Task Continuing_a_pruned_scan_reads_only_the_partitions_that_scan_read()
    {
        await using ScanFixture fixture = Fixture(KafkaPartitioner.Murmur2, out string topic);
        int partition = PartitionOf("dup");

        for (int i = 0; i < 30; i++)
        {
            fixture.Produce(topic, partition, 700 + i, "dup", "again");
        }

        (_, KafkaScanResult first, IReadOnlyList<KafkaMatch> firstMatches) = await fixture.RunAsync(Request(topic, "key = 'dup'", lower: new KafkaScanLowering { MaxMatches = 5 }));

        first.StopReason.Should().Be(KafkaScanStopReason.MatchLimit);
        firstMatches.Should().HaveCount(5);
        first.Cursor.Positions.Should().ContainSingle().Which.Partition.Should().Be(partition, "the cursor holds only the partition that was read");

        (KafkaScanPlan continued, KafkaScanResult second, IReadOnlyList<KafkaMatch> secondMatches) = await fixture.RunAsync(Request(topic, "key = 'dup'", first.Cursor));

        continued.Ranges.Should().ContainSingle().Which.Partition.Should().Be(partition);
        second.StopReason.Should().Be(KafkaScanStopReason.Completed);
        secondMatches.Should().HaveCount(25);
        second.Cursor.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_second_topic_is_judged_on_its_own_declaration()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string pruned = fixture.Topic("pruned", Partitions);
        string whole = fixture.Topic("whole", Partitions);
        fixture.Declare(pruned, t => t.Partitioner(KafkaPartitioner.Murmur2));
        PartitionedData.Fill(fixture, pruned, Partitions, KafkaPartitioner.Murmur2, 4_000);
        PartitionedData.Fill(fixture, whole, Partitions, KafkaPartitioner.Murmur2, 4_000);

        (KafkaScanPlan plan, _, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([pruned, whole], "key = 'k-0042'", toSeconds: 2_000));

        plan.Ranges.Count(x => x.Topic == pruned).Should().Be(1);
        plan.Ranges.Count(x => x.Topic == whole).Should().Be(Partitions);
        plan.Pruning.Select(static x => (x.Topic, x.IsPruned)).Should().Equal((pruned, true), (whole, false));
        plan.Pruning[1].NotPrunedReason.Should().Be("the topic declares no partitioner");
        matches.Select(static x => x.Topic).Should().BeEquivalentTo([pruned, whole], "the key is found on both");
    }

    [Fact]
    public async Task A_host_partitioner_that_throws_claims_nothing_and_is_logged_by_exception_type_only()
    {
        await using ScanFixture fixture = Fixture(null, out string topic, configure: t => t.Partitioner(new Throwing()));

        KafkaScanPlan plan = await fixture.PlanAsync(Request(topic, "key = 'k-0042'"));

        KafkaTopicPruning pruning = plan.Pruning.Single();
        pruning.IsPruned.Should().BeFalse();
        pruning.Check.Should().Be(new PartitionerCheck.NotReadable());
        plan.Ranges.Should().HaveCount(Partitions);
        fixture.Logs.Entries.Where(static x => x.EventId.Id == 9676).Should().ContainSingle().Which.Message.Should().Contain(nameof(InvalidOperationException)).And.NotContain("k-0042");
    }

    [Fact]
    public void The_pruning_texts_list_partitions_the_way_the_notes_say_them()
    {
        KafkaTopicPruning.List([3]).Should().Be("3");
        KafkaTopicPruning.List([1, 3]).Should().Be("1 and 3");
        KafkaTopicPruning.List([1, 3, 4]).Should().Be("1, 3 and 4");
        new WindowNote.PartitionsPruned("t", [2], "fnv1a", 1_234).Text.Should().Be("Only partition 2 of t is read: fnv1a maps the key to it, verified on 1,234 records of this window.");
    }

    private sealed class Throwing() : KafkaPartitioner("throwing")
    {
        public override int Partition(ReadOnlySpan<byte> key, int partitionCount) => throw new InvalidOperationException("host code failed");
    }
}
