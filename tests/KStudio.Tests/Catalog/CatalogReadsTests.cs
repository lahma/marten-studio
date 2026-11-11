using System.Text;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Tests.Contract;
using KStudio.Tests.Support.FakeKafka;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Catalog;

/// <summary>
/// Decision 5.2's mapping: every field of an overview comes from its call, a refusal is a value with its sentence that never blanks the rest
/// of the page and logs once at Debug, and N callers cost one wire call (AGENTS.md D36).
/// </summary>
public class CatalogReadsTests
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(Action<KafkaEngineOptions>? configure = null)
        {
            Clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero));
            Cluster = new FakeKafkaCluster(Clock);
            Engine = EngineHarness.ForWire(new FakeKafkaWire(Cluster, EngineHarness.ClusterKey), Clock, TimeSpan.FromSeconds(5), configure);
        }

        public ManualTimeProvider Clock { get; }

        public FakeKafkaCluster Cluster { get; }

        public EngineHarness Engine { get; }

        public IKafkaCatalog Catalog => Engine.Catalog;

        public async Task<KafkaTopicOverview> Overview(string topic) =>
            (await Catalog.GetTopicOverviewAsync(EngineHarness.ClusterKey, new KafkaTopicName(topic), Token)).Value;

        public void Produce(string topic, int partition, int count, long firstSecond = 0)
        {
            for (int i = 0; i < count; i++)
            {
                Cluster.Produce(topic, partition, Encoding.UTF8.GetBytes("k" + i), Encoding.UTF8.GetBytes("v" + i), null, T0 + ((firstSecond + i) * 1000));
            }
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    [Fact]
    public async Task Every_field_of_a_topic_overview_comes_from_its_call()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("orders", 2, new Dictionary<string, string>
        {
            ["retention.ms"] = "604800000",
            ["cleanup.policy"] = "delete",
            ["message.timestamp.type"] = "CreateTime",
        });
        rig.Produce("orders", 0, 5, 10);
        rig.Produce("orders", 1, 0);

        KafkaTopicOverview overview = await rig.Overview("orders");

        overview.Partitions.Should().HaveCount(2);
        KafkaPartitionOverview p0 = overview.Partitions[0];
        p0.Leader.Should().Be(1);
        p0.Replicas.Should().Equal(1);
        p0.InSyncReplicas.Should().Equal(1);
        (p0.LowWatermark.Value, p0.HighWatermark.Value, p0.LastStableOffset.Value).Should().Be((0L, 5L, 5L));
        p0.OffsetSpan.Should().Be(5, "an offset span, shown as ~N, never called a count");
        p0.OpenTransaction.Should().BeFalse();
        p0.FirstTimestamp.Value.Should().Be(new KafkaTimestampAt(0, DateTimeOffset.FromUnixTimeMilliseconds(T0 + 10_000)));
        p0.HighestTimestamp.Value.Should().Be(new KafkaTimestampAt(4, DateTimeOffset.FromUnixTimeMilliseconds(T0 + 14_000)), "the highest timestamp with its offset");

        KafkaPartitionOverview empty = overview.Partitions[1];
        empty.FirstTimestamp.IsAvailable.Should().BeTrue();
        empty.FirstTimestamp.Value.Should().BeNull("an empty partition has no first record: shown as empty, never as 1970");
        empty.HighestTimestamp.Value.Should().BeNull();

        overview.OffsetSpan.Should().Be(5);
        overview.OpenTransaction.Should().BeFalse();
        overview.AuthorizedOperations.Value.Should().Contain(["Describe", "Read"]);
        overview.Config.Value.RetentionMs.Should().Be(604800000);
        overview.Config.Value.IsCompacted.Should().BeFalse();
        overview.Config.Value.TimestampType.Should().Be("CreateTime");
        overview.IsInternal.Should().BeFalse();
    }

    [Fact]
    public async Task An_overview_runs_its_five_list_offsets_calls_each_batching_every_partition_of_the_topic()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("orders", 3);

        await rig.Overview("orders");

        FakeCall[] calls = [.. rig.Cluster.Calls.Where(static x => x.Call == WireCall.ListOffsets && x.Detail.Length == 0)];
        calls.Should().HaveCount(5);
        calls.Select(static x => (x.Spec!.Value.Kind, x.Isolation)).Should().BeEquivalentTo(
        [
            (WireOffsetSpecKind.Earliest, (WireIsolation?) WireIsolation.ReadUncommitted),
            (WireOffsetSpecKind.Latest, WireIsolation.ReadUncommitted),
            (WireOffsetSpecKind.Latest, WireIsolation.ReadCommitted),
            (WireOffsetSpecKind.ForTimestamp, WireIsolation.ReadUncommitted),
            (WireOffsetSpecKind.MaxTimestamp, WireIsolation.ReadUncommitted),
        ]);
        calls.Should().OnlyContain(static x => x.Partitions.Count == 3, "each call batches every partition");
        calls.Select(static x => x.Spec!.Value.Kind == WireOffsetSpecKind.ForTimestamp ? x.Spec.Value.TimestampMs : 0).Should().OnlyContain(static x => x == 0);
    }

    [Fact]
    public async Task An_open_transaction_shows_as_a_last_stable_offset_below_the_high_watermark()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("tx", 1);
        rig.Produce("tx", 0, 3);
        long id = rig.Cluster.BeginTransaction();
        rig.Cluster.Produce("tx", 0, null, Encoding.UTF8.GetBytes("v"), null, T0, id);

        KafkaTopicOverview overview = await rig.Overview("tx");

        overview.OpenTransaction.Should().BeTrue();
        (overview.Partitions[0].LastStableOffset.Value, overview.Partitions[0].HighWatermark.Value).Should().Be((3L, 4L));
    }

    [Fact]
    public async Task A_compacted_topic_and_tiered_storage_are_read_from_the_config()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("table", 1, new Dictionary<string, string>
        {
            ["cleanup.policy"] = "compact,delete",
            ["remote.storage.enable"] = "true",
            ["local.retention.ms"] = "3600000",
        });

        KafkaTopicConfigView config = (await rig.Overview("table")).Config.Value;

        config.IsCompacted.Should().BeTrue();
        config.RemoteStorageEnabled.Should().BeTrue();
        config.LocalRetentionMs.Should().Be(3600000);
    }

    [Fact]
    public async Task A_refused_config_is_a_value_with_its_sentence_and_the_rest_of_the_page_still_loads()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("orders", 1);
        rig.Produce("orders", 0, 2);
        rig.Cluster.Deny(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, "orders");

        KafkaTopicOverview overview = await rig.Overview("orders");

        overview.Config.IsAvailable.Should().BeFalse();
        overview.Config.Error.Should().Be(new WireError.NotAuthorized(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, "orders"));
        KafkaAccessText.Describe(overview.Config.Error!).Should().Be("Not available: needs `DESCRIBE_CONFIGS` on topic `orders`");
        overview.Partitions[0].HighWatermark.Value.Should().Be(2, "one refusal never blanks the page");
        overview.AuthorizedOperations.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task A_refusal_is_logged_once_at_debug_per_ten_minutes_and_never_as_a_warning()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("orders", 1);
        rig.Cluster.Deny(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, "orders");

        await rig.Overview("orders");
        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await rig.Overview("orders");
        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await rig.Overview("orders");

        CapturedLogEntry[] refusals = [.. rig.Engine.Logs.Entries.Where(static x => x.EventId.Id == 9652)];
        refusals.Should().ContainSingle();
        refusals[0].Level.Should().Be(LogLevel.Debug);
        refusals[0].Message.Should().Be($"Kafka cluster {EngineHarness.ClusterKey} refused DescribeConfigs: needs DESCRIBE_CONFIGS on topic orders");
        rig.Engine.Logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Warning);

        rig.Clock.Advance(TimeSpan.FromMinutes(11));
        await rig.Overview("orders");

        rig.Engine.Logs.Entries.Count(static x => x.EventId.Id == 9652).Should().Be(2, "ten minutes later it is logged again");
    }

    [Fact]
    public async Task A_topic_the_principal_may_not_describe_is_absent_and_a_missing_one_is_not_found()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("hidden", 1);
        rig.Cluster.Deny(KafkaAclOperation.Describe, KafkaResourceType.Topic, "hidden");

        KafkaField<KafkaTopicOverview> hidden = await rig.Catalog.GetTopicOverviewAsync(EngineHarness.ClusterKey, new KafkaTopicName("hidden"), Token);
        KafkaField<KafkaTopicOverview> missing = await rig.Catalog.GetTopicOverviewAsync(EngineHarness.ClusterKey, new KafkaTopicName("nope"), Token);
        KafkaTopicList list = (await rig.Catalog.ListTopicsAsync(EngineHarness.ClusterKey, Token)).Value;

        hidden.Error.Should().Be(new WireError.NotFound(KafkaResourceType.Topic, "hidden"));
        missing.Error.Should().Be(new WireError.NotFound(KafkaResourceType.Topic, "nope"));
        list.Topics.Select(static x => x.Name).Should().NotContain("hidden");
    }

    [Fact]
    public async Task The_topic_list_puts_internal_topics_apart_and_counts_partitions()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("b", 3);
        rig.Cluster.CreateTopic("a", 1);
        rig.Cluster.CreateTopic("__consumer_offsets", 50);

        KafkaTopicList list = (await rig.Catalog.ListTopicsAsync(EngineHarness.ClusterKey, Token)).Value;

        list.Topics.Should().Equal(new KafkaTopicSummary("a", 1, false), new KafkaTopicSummary("b", 3, false));
        list.InternalTopics.Should().Equal(new KafkaTopicSummary("__consumer_offsets", 50, true));
    }

    [Fact]
    public async Task A_refused_cluster_description_is_a_value()
    {
        await using Rig rig = new();
        rig.Cluster.Deny(KafkaAclOperation.Describe, KafkaResourceType.Cluster, "kafka-cluster");

        KafkaClusterOverview overview = await rig.Catalog.GetClusterOverviewAsync(EngineHarness.ClusterKey, Token);

        overview.Description.IsAvailable.Should().BeFalse();
        KafkaAccessText.Describe(overview.Description.Error!).Should().Be("Not available: needs `DESCRIBE` on the cluster");
    }

    [Fact]
    public async Task A_cluster_overview_names_the_cluster_and_its_brokers()
    {
        await using Rig rig = new();

        KafkaClusterOverview overview = await rig.Catalog.GetClusterOverviewAsync(EngineHarness.ClusterKey, Token);

        overview.Description.Value.ClusterId.Should().Be("fake-cluster");
        overview.Description.Value.Nodes.Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_overviews_cost_one_wire_call_per_read_and_the_time_to_live_decides_when_it_runs_again()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("orders", 2);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => rig.Overview("orders")));

        rig.Cluster.Calls.Count(static x => x.Call == WireCall.Metadata).Should().Be(1);
        rig.Cluster.Calls.Count(static x => x.Call == WireCall.ListOffsets).Should().Be(5);
        rig.Cluster.Calls.Count(static x => x.Call == WireCall.DescribeConfigs).Should().Be(1);

        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        await rig.Overview("orders");

        rig.Cluster.Calls.Count(static x => x.Call == WireCall.Metadata).Should().Be(2, "past the refresh interval it reads again");
        rig.Cluster.Calls.Count(static x => x.Call == WireCall.DescribeConfigs).Should().Be(1, "configs are reused for a minute");

        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        await rig.Overview("orders");

        rig.Cluster.Calls.Count(static x => x.Call == WireCall.DescribeConfigs).Should().Be(2);
    }

    [Fact]
    public async Task A_declared_groups_state_and_lag_are_read_and_a_refused_group_is_a_value()
    {
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero));
        FakeKafkaCluster cluster = new(clock);
        await using EngineHarness engine = EngineHarness.ForWire(
            new FakeKafkaWire(cluster, EngineHarness.ClusterKey),
            clock,
            TimeSpan.FromSeconds(5),
            options => options.AddCluster("groups", new Confluent.Kafka.ClientConfig { BootstrapServers = "x:1" }, builder => builder.ConsumerGroup("billing").ConsumerGroup("denied-group")));

        cluster.CreateTopic("orders", 1);

        for (int i = 0; i < 10; i++)
        {
            cluster.Produce("orders", 0, null, Encoding.UTF8.GetBytes("v"), null, T0 + (i * 1000));
        }

        cluster.SetGroup("billing", "Stable", 3);
        cluster.Commit("billing", "orders", 0, 4);
        cluster.Deny(KafkaAclOperation.Describe, KafkaResourceType.Group, "denied-group");

        KafkaTopicOverview overview = (await engine.Catalog.GetTopicOverviewAsync("groups", new KafkaTopicName("orders"), Token)).Value;

        KafkaGroupOverview billing = overview.Groups.Single(static x => x.GroupId == "billing");
        billing.State.Value.Should().Be(new WireGroupDescription("billing", "Stable", 3, null));
        KafkaGroupLag lag = billing.Lag.Value.Single();
        (lag.Committed.Value, lag.Lag).Should().Be((4L, 6L));

        KafkaGroupOverview denied = overview.Groups.Single(static x => x.GroupId == "denied-group");
        denied.State.Error.Should().BeOfType<WireError.NotAuthorized>();
        denied.Lag.Error.Should().Be(new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Group, "denied-group"));
        KafkaAccessText.Describe(denied.Lag.Error!).Should().Be("Not available: needs `DESCRIBE` on group `denied-group`");
    }

    [Fact]
    public async Task The_rate_of_new_offsets_comes_from_the_overview_reads_with_no_read_of_its_own()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("orders", 1);
        rig.Produce("orders", 0, 10);

        KafkaTopicOverview first = await rig.Overview("orders");
        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        rig.Produce("orders", 0, 100, 100);
        KafkaTopicOverview second = await rig.Overview("orders");

        first.OffsetsPerSecond.Should().BeNull();
        second.OffsetsPerSecond.Should().BeApproximately(5.0, 0.0001);
    }

    [Fact]
    public async Task Nothing_the_catalog_calls_creates_alters_or_deletes_and_no_single_topic_metadata_request_exists()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("orders", 1);

        await rig.Overview("orders");
        await rig.Catalog.ListTopicsAsync(EngineHarness.ClusterKey, Token);
        await rig.Catalog.GetClusterOverviewAsync(EngineHarness.ClusterKey, Token);

        rig.Cluster.Calls.Select(static x => x.Call).Distinct().Should().BeSubsetOf(
            [WireCall.Metadata, WireCall.DescribeTopics, WireCall.DescribeCluster, WireCall.ListOffsets, WireCall.DescribeConfigs],
            "the wire has no call that writes, and the catalog calls none outside decision 5.2");
        typeof(IKafkaWire).GetMethods().Select(static x => x.Name).Should().NotContain(static x =>
            x.StartsWith("Create", StringComparison.Ordinal) || x.StartsWith("Delete", StringComparison.Ordinal) || x.StartsWith("Alter", StringComparison.Ordinal)
            || x.StartsWith("Produce", StringComparison.Ordinal) || x.StartsWith("Commit", StringComparison.Ordinal));
    }
}
