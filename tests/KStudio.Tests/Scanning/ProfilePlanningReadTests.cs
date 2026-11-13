using KStudio.Core.Clusters;
using KStudio.Core.Scanning;
using KStudio.Tests.Contract;
using KStudio.Tests.Partitioning;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The reads a plan makes before a scan (the record-size sample and the partitioner verification) are the owner's reads too (AGENTS.md D41, D47): on
/// a profiled cluster they count against the cluster's own allowance as well as the process's, and a verification is refused when either is spent.
/// </summary>
public class ProfilePlanningReadTests
{
    private const long MiB = 1024 * 1024;
    private const int Partitions = 6;

    private static ScanFixture Production(out string topic)
    {
        ScanFixture fixture = ScanFixture.Create(cluster: static c => c.Profile(KafkaProfile.Production));
        topic = fixture.Topic("planning", Partitions);
        fixture.Declare(topic, static t => t.Partitioner(KafkaPartitioner.Murmur2));
        PartitionedData.Fill(fixture, topic, Partitions, KafkaPartitioner.Murmur2, 4_000);
        return fixture;
    }

    private static KafkaClusterCost Cost(ScanFixture fixture) =>
        fixture.Provider.GetRequiredService<KafkaClusterCostControls>().For(fixture.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey));

    private static Task<KafkaScanPlan> PlanAsync(ScanFixture fixture, string topic, string predicate) =>
        fixture.PlanAsync(ScanFixture.Request([topic], predicate, toSeconds: 2_000));

    [Fact]
    public async Task A_key_search_on_a_cluster_whose_own_allowance_is_spent_does_no_verification_read_though_the_process_has_room()
    {
        await using ScanFixture fixture = Production(out string topic);
        KafkaClusterCost cost = Cost(fixture);
        OwnerByteBudget process = fixture.Provider.GetRequiredService<OwnerByteBudget>();

        cost.OwnerBudget!.Add(fixture.Owner.Key, 512 * MiB);
        process.Remaining(fixture.Owner.Key).Should().BeGreaterThan(0, "the process's own allowance has room");

        KafkaScanPlan plan = await PlanAsync(fixture, topic, "key = 'k-0042'");

        plan.Errors.Should().BeEmpty();
        plan.Pruning.Should().ContainSingle().Which.Check.Should().Be(new PartitionerCheck.NotReadable());
        plan.Pruning[0].IsPruned.Should().BeFalse();
        fixture.Readers.Should().HaveCount(1, "only the size sample read: the verification opened no consumer");
        fixture.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9676).Which.Message.Should().Contain("OwnerBudget");
    }

    [Fact]
    public async Task A_verification_never_reads_more_than_the_clusters_allowance_has_left()
    {
        await using ScanFixture fixture = Production(out string topic);
        KafkaClusterCost cost = Cost(fixture);

        // 6,000 bytes are fewer than a hundred records of 106 bytes: the sample stops at the cluster's bound and claims nothing.
        cost.OwnerBudget!.Add(fixture.Owner.Key, (512 * MiB) - 6_000);

        KafkaScanPlan plan = await PlanAsync(fixture, topic, "key = 'k-0042'");

        plan.Pruning.Should().ContainSingle().Which.IsPruned.Should().BeFalse();
        fixture.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9676).Which.Message.Should().Contain("ByteLimit");
    }

    [Fact]
    public async Task The_size_sample_and_the_verification_bytes_land_in_the_clusters_allowance_as_well_as_the_processs()
    {
        await using ScanFixture fixture = Production(out string topic);
        KafkaClusterCost cost = Cost(fixture);
        OwnerByteBudget process = fixture.Provider.GetRequiredService<OwnerByteBudget>();

        // A search that fixes no key: only the size sample reads.
        await PlanAsync(fixture, topic, "header.kind = 'x'");

        long sampled = (2 * 1024 * MiB) - process.Remaining(fixture.Owner.Key);
        sampled.Should().BeGreaterThan(0, "the sample read records");
        ((512 * MiB) - cost.OwnerBudget!.Remaining(fixture.Owner.Key)).Should().Be(sampled, "the same bytes, counted on both");

        // A key search now reads the verification too (the size sample is remembered for ten minutes, so these are the verification's bytes).
        long beforeProcess = process.Remaining(fixture.Owner.Key);
        long beforeCluster = cost.OwnerBudget.Remaining(fixture.Owner.Key);

        KafkaScanPlan plan = await PlanAsync(fixture, topic, "key = 'k-0042'");

        plan.Pruning[0].IsPruned.Should().BeTrue();
        long verification = beforeProcess - process.Remaining(fixture.Owner.Key);
        verification.Should().BeGreaterThanOrEqualTo(6 * 167 * (6 + PartitionedData.ValueBytes), "1,002 records were read by the verification");
        (beforeCluster - cost.OwnerBudget.Remaining(fixture.Owner.Key)).Should().Be(verification);
    }

    [Fact]
    public async Task A_cluster_with_no_profile_has_no_allowance_of_its_own_to_charge()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("plain", Partitions);
        fixture.Declare(topic, static t => t.Partitioner(KafkaPartitioner.Murmur2));
        PartitionedData.Fill(fixture, topic, Partitions, KafkaPartitioner.Murmur2, 4_000);

        KafkaScanPlan plan = await PlanAsync(fixture, topic, "key = 'k-0042'");

        plan.Pruning[0].IsPruned.Should().BeTrue();
        Cost(fixture).Should().BeSameAs(KafkaClusterCost.None);
    }
}
