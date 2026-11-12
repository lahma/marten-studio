using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Scanning;

/// <summary>
/// A profile in the engine (AGENTS.md D47, D-M5 decisions 4.4 and 4.5): the plan's limits are the host's capped by the cluster's profile, a
/// lowering may only go below that, and a profiled cluster has a byte rate and an hourly allowance of its own on top of the process's.
/// </summary>
public class ProfileEngineTests
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;
    private const long GiB = 1024 * MiB;

    private static void Raised(KafkaEngineOptions options)
    {
        options.Limits.MaxBytesPerScan = 4 * GiB;
        options.Limits.MaxRecordsPerScan = 50_000_000;
        options.Limits.MaxScanDuration = TimeSpan.FromMinutes(20);
        options.Limits.MaxMatches = 5_000;
        options.Limits.ConfirmAboveBytes = 2 * GiB;
        options.Limits.ConfirmAboveRecords = 40_000_000;
        options.Limits.BytesPerSecond = 100 * MiB;
        options.Limits.OwnerBytesPerHour = 50 * GiB;
    }

    private static void Seed(ScanFixture fixture, string topic, int count)
    {
        for (int i = 0; i < count; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v" + i);
        }
    }

    [Theory]
    [InlineData(null, 4L * GiB, 5_000)]
    [InlineData(KafkaProfile.Development, 4L * GiB, 5_000)]
    [InlineData(KafkaProfile.Test, 256L * MiB, 500)]
    [InlineData(KafkaProfile.Production, 64L * MiB, 500)]
    public async Task A_plans_limits_are_the_hosts_capped_by_the_clusters_profile(KafkaProfile? profile, long maxBytes, int maxMatches)
    {
        await using ScanFixture fixture = ScanFixture.Create(Raised, cluster: profile is { } p ? c => c.Profile(p) : null);
        string topic = fixture.Topic("plan");
        Seed(fixture, topic, 3);

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));

        plan.Limits.MaxBytesPerScan.Should().Be(maxBytes);
        plan.Limits.MaxMatches.Should().Be(maxMatches);
        fixture.Scanner.LimitsFor(EngineHarness.ClusterKey).MaxBytesPerScan.Should().Be(maxBytes);
        fixture.Scanner.LimitsFor(EngineHarness.ClusterKey).Validate().Should().BeEmpty();
    }

    [Fact]
    public async Task A_production_plan_carries_the_table()
    {
        await using ScanFixture fixture = ScanFixture.Create(Raised, cluster: static c => c.Profile(KafkaProfile.Production));
        string topic = fixture.Topic("table");
        Seed(fixture, topic, 3);

        KafkaScanLimits limits = (await fixture.PlanAsync(ScanFixture.Request([topic]))).Limits;

        limits.MaxBytesPerScan.Should().Be(64 * MiB);
        limits.MaxRecordsPerScan.Should().Be(1_000_000);
        limits.MaxScanDuration.Should().Be(TimeSpan.FromMinutes(1));
        limits.MaxMatches.Should().Be(500);
        limits.ConfirmAboveBytes.Should().Be(16 * MiB);
        limits.ConfirmAboveRecords.Should().Be(250_000);
        limits.BytesPerSecond.Should().Be(5 * MiB);
        limits.OwnerBytesPerHour.Should().Be(512 * MiB);
    }

    [Fact]
    public async Task A_lowering_goes_below_the_capped_value_and_a_raise_above_it_throws_even_when_the_host_allowed_it()
    {
        await using ScanFixture fixture = ScanFixture.Create(Raised, cluster: static c => c.Profile(KafkaProfile.Production));
        string topic = fixture.Topic("lower");
        Seed(fixture, topic, 3);

        KafkaScanPlan lowered = await fixture.PlanAsync(ScanFixture.Request([topic], lower: new KafkaScanLowering { MaxBytesPerScan = 8 * MiB }));
        lowered.Limits.MaxBytesPerScan.Should().Be(8 * MiB);

        Func<Task> raised = () => fixture.PlanAsync(ScanFixture.Request([topic], lower: new KafkaScanLowering { MaxBytesPerScan = 128 * MiB }));
        (await raised.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("MaxBytesPerScan");
    }

    [Fact]
    public async Task A_host_that_set_nothing_gets_its_own_limits_from_every_plan_and_no_controls_of_a_clusters_own()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("defaults");
        Seed(fixture, topic, 3);

        KafkaScanLimits limits = (await fixture.PlanAsync(ScanFixture.Request([topic]))).Limits;
        KafkaScanLimits host = new();

        foreach (System.Reflection.PropertyInfo property in typeof(KafkaScanLimits).GetProperties().Where(static x => x.CanWrite))
        {
            property.GetValue(limits).Should().Be(property.GetValue(host), property.Name);
        }

        KafkaClusterCostControls costs = fixture.Provider.GetRequiredService<KafkaClusterCostControls>();
        costs.For(fixture.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey)).Should().BeSameAs(KafkaClusterCost.None);
        costs.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_development_cluster_has_no_controls_of_its_own_and_a_test_cluster_has_them_only_where_it_lowers_the_hosts()
    {
        // At the defaults, Test's rate (10 MiB/s) and allowance (2 GiB) equal the host's: nothing is lower, so nothing is allocated.
        await using ScanFixture test = ScanFixture.Create(cluster: static c => c.Profile(KafkaProfile.Test));
        test.Provider.GetRequiredService<KafkaClusterCostControls>().For(test.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey))
            .Should().BeSameAs(KafkaClusterCost.None);

        await using ScanFixture raised = ScanFixture.Create(Raised, cluster: static c => c.Profile(KafkaProfile.Test));
        KafkaClusterCost cost = raised.Provider.GetRequiredService<KafkaClusterCostControls>().For(raised.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey));
        cost.Governor!.BytesPerSecond.Should().Be(10 * MiB);
        cost.OwnerBudget!.BytesPerHour.Should().Be(2 * GiB);

        await using ScanFixture dev = ScanFixture.Create(Raised, cluster: static c => c.Profile(KafkaProfile.Development));
        dev.Provider.GetRequiredService<KafkaClusterCostControls>().For(dev.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey))
            .Should().BeSameAs(KafkaClusterCost.None);
    }

    [Fact]
    public async Task The_controls_of_a_cluster_are_made_once_and_kept()
    {
        await using ScanFixture fixture = ScanFixture.Create(cluster: static c => c.Profile(KafkaProfile.Production));
        KafkaClusterDefinition definition = fixture.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey);
        KafkaClusterCostControls costs = fixture.Provider.GetRequiredService<KafkaClusterCostControls>();

        KafkaClusterCost first = costs.For(definition);
        KafkaClusterCost second = costs.For(definition);

        second.Should().BeSameAs(first, "an allowance and a bucket's debt outlive the scan that started them");
        first.Governor!.BytesPerSecond.Should().Be(5 * MiB);
        first.OwnerBudget!.BytesPerHour.Should().Be(512 * MiB);
        costs.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_production_cluster_refuses_a_start_when_its_own_allowance_is_spent_though_the_process_has_room()
    {
        await using ScanFixture fixture = ScanFixture.Create(cluster: static c => c.Profile(KafkaProfile.Production));
        string topic = fixture.Topic("allowance");
        Seed(fixture, topic, 4);

        KafkaScanOwner owner = new("spender");
        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));
        KafkaClusterCost cost = fixture.Provider.GetRequiredService<KafkaClusterCostControls>().For(fixture.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey));
        OwnerByteBudget process = fixture.Provider.GetRequiredService<OwnerByteBudget>();

        cost.OwnerBudget!.Add(owner.Key, 512 * MiB);

        process.Remaining(owner.Key).Should().Be(2 * GiB, "the process's own allowance has not been touched");
        cost.Remaining(owner.Key, process).Should().Be(0);
        cost.BytesPerHour(process).Should().Be(512 * MiB);
        fixture.Start(plan, confirmed: true, owner: owner).Refusal.Should().Be(KafkaScanRefusal.OwnerBudgetSpent);

        fixture.Clock.Advance(TimeSpan.FromMinutes(61));
        KafkaScanStart again = fixture.Start(plan, confirmed: true, owner: owner);

        again.Started.Should().BeTrue("the hour has rolled on");
        await again.Handle!.DisposeAsync();
    }

    [Fact]
    public async Task A_scan_stops_with_the_owner_budget_when_the_clusters_allowance_runs_out_while_it_reads()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync(cluster: static c => c.Profile(KafkaProfile.Production));
        await using ScanEngineTests.ScriptedScan guard = scan;
        KafkaClusterCost cost = scan.Fixture.Provider.GetRequiredService<KafkaClusterCostControls>().For(scan.Fixture.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey));
        cost.OwnerBudget!.Add(scan.Fixture.Owner.Key, (512 * MiB) - 1);

        scan.Reader.Enqueue(new WireReadOutcome.Record(Record(scan.Topic, 0, 0)));

        KafkaScanResult result = await scan.Handle.Result.Bounded();

        result.StopReason.Should().Be(KafkaScanStopReason.OwnerBudget);
        scan.Fixture.Provider.GetRequiredService<OwnerByteBudget>().Remaining(scan.Fixture.Owner.Key).Should().BeGreaterThan(0, "the process's allowance still has room");
        cost.OwnerBudget.Remaining(scan.Fixture.Owner.Key).Should().Be(0);
    }

    [Fact]
    public async Task A_record_read_is_refused_when_the_clusters_allowance_is_spent_and_asks_the_broker_nothing()
    {
        await using ScanFixture fixture = ScanFixture.Create(cluster: static c => c.Profile(KafkaProfile.Production));
        string topic = fixture.Topic("read");
        Seed(fixture, topic, 5);
        fixture.Provider.GetRequiredService<KafkaClusterCostControls>()
            .For(fixture.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey))
            .OwnerBudget!.Add(fixture.Owner.Key, 512 * MiB);
        int calls = fixture.Cluster.Calls.Count;

        KafkaRecordRead read = await fixture.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), 0, 2, 5, KafkaReadGrant.Envelope, KafkaIsolation.Committed),
            fixture.Owner,
            TestContext.Current.CancellationToken);

        read.Refusal.Should().Be(KafkaScanRefusal.OwnerBudgetSpent);
        fixture.Cluster.Calls.Should().HaveCount(calls);
    }

    // ---- pacing, on a manual clock and a scripted reader: no wall time passes

    /// <summary>A 1 MiB record exactly, so that the bucket's arithmetic is exact: key 1 byte, value 1 MiB - 1.</summary>
    private static WireRecord Record(string topic, int partition, long offset)
    {
        WireRecord record = new(topic, partition, offset, [1], new byte[MiB - 1], [], ScanFixture.T0.ToUnixTimeMilliseconds() + 1_000, WireTimestampType.CreateTime, 0);
        record.Size.Should().Be((int) MiB);
        return record;
    }

    /// <summary>Feeds 1 MiB records and advances the clock one second at a time; returns the MiB read after each second, the first entry being the burst.</summary>
    private static async Task<long[]> ReadPerSecondAsync(
        Action<KafkaClusterBuilder>? cluster,
        Func<ScanEngineTests.ScriptedScan, KafkaByteRateGovernor> waitingGovernor,
        int seconds,
        Action<KafkaEngineOptions>? configure = null)
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync(configure, cluster: cluster);
        await using ScanEngineTests.ScriptedScan guard = scan;

        for (int i = 0; i < 99; i++)
        {
            scan.Reader.Enqueue(new WireReadOutcome.Record(Record(scan.Topic, 0, i)));
        }

        KafkaByteRateGovernor governor = waitingGovernor(scan);
        long[] read = new long[seconds + 1];

        // The bucket starts full: one second of rate is read at once, and one record more, because a bucket at zero still reads.
        ScanFixture.Until(() => governor.Waiters == 1, "the scan thread is in debt after its burst");
        read[0] = scan.Handle.Progress.Bytes / MiB;

        for (int second = 1; second <= seconds; second++)
        {
            long before = scan.Handle.Progress.Records;
            scan.Fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            ScanFixture.Until(() => scan.Handle.Progress.Records > before && governor.Waiters == 1, "the debt is paid, the second's records are read, and the thread is in debt again");
            read[second] = scan.Handle.Progress.Bytes / MiB;
        }

        return read;
    }

    [Fact]
    public async Task A_production_cluster_is_paced_at_five_mebibytes_a_second_by_its_own_bucket()
    {
        KafkaByteRateGovernor? cluster = null;

        long[] read = await ReadPerSecondAsync(
            static c => c.Profile(KafkaProfile.Production),
            scan => cluster ??= scan.Fixture.Provider.GetRequiredService<KafkaClusterCostControls>().For(scan.Fixture.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey)).Governor!,
            10);

        long afterBurst = read[1] - read[0];
        long overTen = read[10] - read[0];

        read[0].Should().Be(6, "five MiB of bucket and one record more");
        overTen.Should().BeInRange(45, 55, "ten seconds at 5 MiB/s, within 10 %");
        afterBurst.Should().BeInRange(4, 6);
    }

    [Fact]
    public async Task A_development_cluster_runs_at_the_hosts_rate()
    {
        KafkaByteRateGovernor? process = null;

        long[] read = await ReadPerSecondAsync(
            static c => c.Profile(KafkaProfile.Development),
            scan => process ??= scan.Fixture.Provider.GetRequiredService<KafkaByteRateGovernor>(),
            5);

        read[0].Should().Be(11, "ten MiB of bucket and one record more");
        (read[5] - read[0]).Should().BeInRange(45, 55, "five seconds at the default 10 MiB/s, within 10 %");
    }

    [Fact]
    public async Task A_cluster_is_paced_by_the_slower_of_its_own_bucket_and_the_process_bucket()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync(cluster: static c => c.Profile(KafkaProfile.Production));
        await using ScanEngineTests.ScriptedScan guard = scan;
        KafkaByteRateGovernor process = scan.Fixture.Provider.GetRequiredService<KafkaByteRateGovernor>();

        // Another cluster's scans have overdrawn the process's bucket by 20 MiB: two seconds of debt, deeper than anything the cluster owes.
        process.Take(30 * MiB);
        ScanFixture.Until(() => process.Waiters == 1, "the scan waits on the process's bucket, the deeper debt, before any record is in its reach");

        for (int i = 0; i < 20; i++)
        {
            scan.Reader.Enqueue(new WireReadOutcome.Record(Record(scan.Topic, 0, i)));
        }

        scan.Handle.Progress.Records.Should().Be(0);

        scan.Fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        scan.Handle.Progress.Records.Should().Be(0, "one second of two has passed");

        scan.Fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        ScanFixture.Until(() => scan.Handle.Progress.Records == 1 && process.Waiters == 1, "the debt is paid, one record is read and the process's bucket owes again");
    }
}
