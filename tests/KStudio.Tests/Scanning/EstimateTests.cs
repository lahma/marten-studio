using System.Text;

using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The estimate before a scan reads (decision 9.10): the offsets in the ranges, the mean record size from a small sample, the confirmation
/// threshold, and the confirmation that the engine - not a dialog - checks at <c>Start</c>.
/// </summary>
public class EstimateTests
{
    private static string Value(int bytes) => new('x', bytes - 2);

    private static ScanFixture Fixture(out string topic, int partitions, int perPartition, int recordBytes, Action<KafkaEngineOptions>? configure = null)
    {
        ScanFixture fixture = ScanFixture.Create(configure);
        topic = fixture.Topic("estimate", partitions);

        for (int p = 0; p < partitions; p++)
        {
            for (int i = 0; i < perPartition; i++)
            {
                fixture.Produce(topic, p, i, "k" + (i % 10), Value(recordBytes));
            }
        }

        return fixture;
    }

    [Fact]
    public async Task The_estimate_is_the_offsets_of_the_ranges_times_the_sampled_mean_record_size()
    {
        await using ScanFixture fixture = Fixture(out string topic, partitions: 2, perPartition: 30, recordBytes: 100);

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));

        KafkaScanEstimate estimate = plan.Estimate;
        (estimate.Offsets, estimate.MeanRecordBytes, estimate.Bytes, estimate.Partitions, estimate.NeedsConfirmation).Should().Be((60L, (long?) 100, (long?) 6_000, 2, false));
        estimate.Basis.Should().Contain("About 60 offsets").And.Contain("an upper bound").And.Contain("about 100 bytes each");
    }

    [Fact]
    public async Task Sampling_is_one_short_consumer_from_the_starts_of_at_most_three_of_the_largest_ranges()
    {
        await using ScanFixture fixture = Fixture(out string topic, partitions: 5, perPartition: 20, recordBytes: 100);

        // Make partition 3 and 4 the largest ranges, then 0.
        fixture.Produce(topic, 3, 99, "k", Value(100));
        fixture.Produce(topic, 4, 98, "k", Value(100));
        fixture.Produce(topic, 4, 99, "k", Value(100));

        await fixture.PlanAsync(ScanFixture.Request([topic]));

        FakeReaderView reader = new(fixture.Readers.Single());
        reader.AssignedPartitions.Should().HaveCount(3).And.Contain([3, 4]);
        reader.AssignedOffsets.Should().OnlyContain(static x => x == 0, "sampling starts at the start of each range");
        reader.Closed.Should().Be(1);
    }

    [Fact]
    public async Task Sampling_reads_at_most_fifty_records_or_a_mebibyte_and_its_bytes_count_against_the_owner()
    {
        await using ScanFixture fixture = Fixture(out string topic, partitions: 1, perPartition: 400, recordBytes: 100);
        OwnerByteBudget budget = fixture.Provider.GetRequiredService<OwnerByteBudget>();
        long before = budget.Remaining(fixture.Owner.Key);

        await fixture.PlanAsync(ScanFixture.Request([topic]));

        before.Should().Be(2L * 1024 * 1024 * 1024);
        (before - budget.Remaining(fixture.Owner.Key)).Should().Be(RecordSizeSampler.MaxRecords * 100, "fifty records of a hundred bytes were read");

        await using ScanFixture big = Fixture(out string bigTopic, partitions: 1, perPartition: 30, recordBytes: 300 * 1024);
        OwnerByteBudget bigBudget = big.Provider.GetRequiredService<OwnerByteBudget>();
        long bigBefore = bigBudget.Remaining(big.Owner.Key);

        await big.PlanAsync(ScanFixture.Request([bigTopic]));

        long read = bigBefore - bigBudget.Remaining(big.Owner.Key);
        read.Should().BeGreaterThanOrEqualTo(RecordSizeSampler.MaxBytes).And.BeLessThan(RecordSizeSampler.MaxBytes + (300 * 1024), "the sample stops at a mebibyte");
    }

    [Fact]
    public async Task A_sample_is_reused_for_ten_minutes_and_taken_again_after_them()
    {
        await using ScanFixture fixture = Fixture(out string topic, partitions: 1, perPartition: 20, recordBytes: 100);

        await fixture.PlanAsync(ScanFixture.Request([topic]));
        await fixture.PlanAsync(ScanFixture.Request([topic], "key = 'k1'"));

        fixture.Readers.Should().HaveCount(1, "the second plan reused the sample");

        fixture.Clock.Advance(TimeSpan.FromMinutes(9));
        await fixture.PlanAsync(ScanFixture.Request([topic]));
        fixture.Readers.Should().HaveCount(1);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.PlanAsync(ScanFixture.Request([topic]));
        fixture.Readers.Should().HaveCount(2, "ten minutes have passed");
    }

    [Fact]
    public async Task A_refused_sample_leaves_the_size_unknown_and_never_invents_one()
    {
        await using ScanFixture fixture = Fixture(out string topic, partitions: 1, perPartition: 20, recordBytes: 100);
        fixture.Cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, topic);

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));

        (plan.Estimate.MeanRecordBytes, plan.Estimate.Bytes).Should().Be(((long?) null, (long?) null));
        plan.Estimate.Offsets.Should().Be(20);
        plan.Estimate.Basis.Should().Contain("record size is unknown");
    }

    [Fact]
    public async Task A_plan_with_nothing_to_read_has_an_empty_estimate_and_samples_nothing()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("quiet");
        fixture.Produce(topic, 0, 100, "k", "v");

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic], toSeconds: 10));

        plan.Estimate.Offsets.Should().Be(0);
        plan.Estimate.NeedsConfirmation.Should().BeFalse();
        fixture.Readers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_scan_above_the_byte_threshold_needs_a_confirmation_and_start_refuses_without_it()
    {
        await using ScanFixture fixture = Fixture(out string topic, partitions: 1, perPartition: 30, recordBytes: 100, configure: static o => o.Limits.ConfirmAboveBytes = 1_000);

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));

        plan.Estimate.NeedsConfirmation.Should().BeTrue("3,000 bytes is above 1,000");
        fixture.Start(plan).Refusal.Should().Be(KafkaScanRefusal.NeedsConfirmation);
        fixture.Start(plan, confirmed: false).Refusal.Should().Be(KafkaScanRefusal.NeedsConfirmation);

        KafkaScanStart confirmed = fixture.Start(plan, confirmed: true);
        confirmed.Started.Should().BeTrue();
        await using KafkaScanHandle handle = confirmed.Handle!;
        (await handle.Result.Bounded()).Records.Should().Be(30);
    }

    [Fact]
    public async Task A_scan_above_the_record_threshold_needs_a_confirmation_even_when_the_size_is_unknown()
    {
        await using ScanFixture fixture = Fixture(out string topic, partitions: 1, perPartition: 30, recordBytes: 100, configure: static o => o.Limits.ConfirmAboveRecords = 10);
        fixture.Cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, topic);

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));

        plan.Estimate.Bytes.Should().BeNull();
        plan.Estimate.NeedsConfirmation.Should().BeTrue("30 offsets is above 10");
        fixture.Start(plan).Refusal.Should().Be(KafkaScanRefusal.NeedsConfirmation);
    }

    [Fact]
    public async Task A_visitor_can_lower_the_confirmation_threshold_for_one_scan()
    {
        await using ScanFixture fixture = Fixture(out string topic, partitions: 1, perPartition: 30, recordBytes: 100);

        KafkaScanPlan lenient = await fixture.PlanAsync(ScanFixture.Request([topic]));
        KafkaScanPlan strict = await fixture.PlanAsync(ScanFixture.Request([topic], lower: new KafkaScanLowering { ConfirmAboveRecords = 5 }));

        lenient.Estimate.NeedsConfirmation.Should().BeFalse();
        strict.Estimate.NeedsConfirmation.Should().BeTrue();
        fixture.Start(lenient).Started.Should().BeTrue();
    }

    [Fact]
    public async Task The_sampler_gives_a_per_topic_mean_so_a_small_topic_does_not_hide_a_large_one()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string small = fixture.Topic("small");
        string large = fixture.Topic("large");

        for (int i = 0; i < 20; i++)
        {
            fixture.Produce(small, 0, i, "kk", Value(10));
            fixture.Produce(large, 0, i, "kk", Value(1_000));
        }

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([small, large]));

        plan.Estimate.Bytes.Should().Be((20 * 10) + (20 * 1_000), "each topic is multiplied by its own mean");
        Encoding.UTF8.GetByteCount(plan.Estimate.Basis).Should().BeGreaterThan(0);
    }

    /// <summary>What a test needs to know of a fake reader.</summary>
    private sealed class FakeReaderView(Support.FakeKafka.FakeScanReader reader)
    {
        public IReadOnlyList<int> AssignedPartitions => [.. reader.Assigns.SelectMany(static x => x).Select(static x => x.Partition)];

        public IReadOnlyList<long> AssignedOffsets => [.. reader.Assigns.SelectMany(static x => x).Select(static x => x.Offset)];

        public int Closed => reader.CloseCount;
    }
}
