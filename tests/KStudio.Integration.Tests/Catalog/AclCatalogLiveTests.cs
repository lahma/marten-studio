using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

using Microsoft.Extensions.Logging;

namespace KStudio.Integration.Tests.Catalog;

/// <summary>
/// The catalog as principals with different ACLs (decisions 5.2 and 5.4, SPIKE S1): <c>reader</c> has READ on <c>ks.</c> and on the
/// <c>kstudio-</c> groups, <c>configreader</c> adds DESCRIBE_CONFIGS, <c>nogroup</c> has no group ACL. A refusal is a value with its sentence that
/// never blanks the page, and it is logged once at Debug.
/// </summary>
public abstract class AclCatalogLiveTests(KafkaFixture fixture)
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A topic with three records on partition 0 and a topic outside the prefix every restricted user must never see.</summary>
    private async Task<(LiveContractCluster Admin, string Topic, string Other)> SeedAsync(string label)
    {
        LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic(label);
        string other = $"other.{fixture.RunId}.{label}";

        await admin.CreateTopicAsync(topic, 2);
        await admin.CreateTopicAsync(other, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 3).Select(i => new ContractRecord("k" + i, "v" + i, T0 + (i * 1000L)))]);
        return (admin, topic, other);
    }

    private EngineHarness As(string user, Action<KafkaEngineOptions>? configure = null) =>
        EngineHarness.ForClient(fixture.Client(user), configure: configure);

    [Fact]
    public async Task A_restricted_principal_lists_only_the_topics_its_acls_allow()
    {
        (LiveContractCluster admin, string topic, string other) = await SeedAsync("list");
        await using LiveContractCluster seeded = admin;
        await using EngineHarness engine = As("reader");

        KafkaTopicList list = (await engine.Catalog.ListTopicsAsync(EngineHarness.ClusterKey, Token)).Value;

        list.Topics.Select(static x => x.Name).Should().Contain(topic);
        list.Topics.Concat(list.InternalTopics).Select(static x => x.Name).Should().NotContain(other).And.OnlyContain(static x => x.StartsWith("ks.", StringComparison.Ordinal));

        KafkaField<KafkaTopicOverview> hidden = await engine.Catalog.GetTopicOverviewAsync(EngineHarness.ClusterKey, new KafkaTopicName(other), Token);
        hidden.Error.Should().Be(new WireError.NotFound(KafkaResourceType.Topic, other), "a topic the principal may not describe is absent");
    }

    [Fact]
    public async Task READ_alone_gives_offsets_and_timestamps_and_refuses_the_configs_as_a_value_with_what_to_grant()
    {
        (LiveContractCluster admin, string topic, _) = await SeedAsync("reader");
        await using LiveContractCluster seeded = admin;
        await using EngineHarness engine = As("reader");

        KafkaTopicOverview overview = (await engine.Catalog.GetTopicOverviewAsync(EngineHarness.ClusterKey, new KafkaTopicName(topic), Token)).Value;

        KafkaPartitionOverview p0 = overview.Partitions[0];
        (p0.LowWatermark.Value, p0.HighWatermark.Value, p0.LastStableOffset.Value).Should().Be((0L, 3L, 3L));
        p0.FirstTimestamp.Value.Should().Be(new KafkaTimestampAt(0, DateTimeOffset.FromUnixTimeMilliseconds(T0)));
        p0.HighestTimestamp.Value.Should().Be(new KafkaTimestampAt(2, DateTimeOffset.FromUnixTimeMilliseconds(T0 + 2000)), "the highest timestamp, with its offset");
        overview.Partitions[1].FirstTimestamp.Value.Should().BeNull("an empty partition has no first record");
        overview.OffsetSpan.Should().Be(3);

        overview.Config.IsAvailable.Should().BeFalse();
        overview.Config.Error.Should().Be(new WireError.NotAuthorized(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, topic));
        KafkaAccessText.Describe(overview.Config.Error!).Should().Be($"Not available: needs `DESCRIBE_CONFIGS` on topic `{topic}`");
        overview.AuthorizedOperations.IsAvailable.Should().BeTrue("one refusal never blanks the rest of the page");
    }

    [Fact]
    public async Task READ_plus_DESCRIBE_CONFIGS_reads_the_configs()
    {
        (LiveContractCluster admin, string topic, _) = await SeedAsync("configreader");
        await using LiveContractCluster seeded = admin;
        await using EngineHarness engine = As("configreader");

        KafkaTopicOverview overview = (await engine.Catalog.GetTopicOverviewAsync(EngineHarness.ClusterKey, new KafkaTopicName(topic), Token)).Value;

        overview.Config.IsAvailable.Should().BeTrue();
        overview.Config.Value.RetentionMs.Should().BePositive();
        overview.Config.Value.TimestampType.Should().Be("CreateTime");
        overview.Config.Value.IsCompacted.Should().BeFalse();
    }

    [Fact]
    public async Task The_cluster_is_described_to_a_restricted_principal_too()
    {
        // SPIKE S1.5 measured DescribeCluster as allowed for every principal here, so no cluster refusal can be provoked on a live broker;
        // the refusal as a value is proved on the fake (CatalogReadsTests).
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        await using EngineHarness engine = As("reader");

        KafkaClusterOverview overview = await engine.Catalog.GetClusterOverviewAsync(EngineHarness.ClusterKey, Token);

        overview.Description.IsAvailable.Should().BeTrue();
        overview.Description.Value.ClusterId.Should().NotBeNullOrWhiteSpace();
        overview.Description.Value.Nodes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Without_a_group_acl_a_declared_groups_state_and_lag_are_values_that_say_what_to_grant()
    {
        (LiveContractCluster admin, string topic, _) = await SeedAsync("nogroup");
        await using LiveContractCluster seeded = admin;
        string group = $"kstudio-nogroup-{fixture.RunId}";
        await using EngineHarness engine = As("nogroup", options => options.AddCluster("groups", fixture.Client("nogroup"), c => c.ConsumerGroup(group)));

        KafkaTopicOverview overview = (await engine.Catalog.GetTopicOverviewAsync("groups", new KafkaTopicName(topic), Token)).Value;

        KafkaGroupOverview groupOverview = overview.Groups.Single();
        groupOverview.State.Error.Should().Be(new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Group, group));
        groupOverview.Lag.Error.Should().Be(new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Group, group));
        KafkaAccessText.Describe(groupOverview.Lag.Error!).Should().Be($"Not available: needs `DESCRIBE` on group `{group}`");
        overview.Partitions[0].HighWatermark.Value.Should().Be(3, "the topic's own fields still load");
    }

    [Fact]
    public async Task With_a_group_acl_a_declared_groups_lag_is_the_high_watermark_minus_the_committed_offset()
    {
        (LiveContractCluster admin, string topic, _) = await SeedAsync("grouplag");
        await using LiveContractCluster seeded = admin;
        string group = $"kstudio-lag-{fixture.RunId}";
        await admin.CommitGroupOffsetAsync(group, topic, 0, 2);
        await using EngineHarness engine = As("reader", options => options.AddCluster("groups", fixture.Client("reader"), c => c.ConsumerGroup(group)));

        KafkaTopicOverview overview = (await engine.Catalog.GetTopicOverviewAsync("groups", new KafkaTopicName(topic), Token)).Value;

        KafkaGroupOverview groupOverview = overview.Groups.Single();
        groupOverview.State.IsAvailable.Should().BeTrue();
        KafkaGroupLag p0 = groupOverview.Lag.Value.Single(x => x.Partition.Partition == 0);
        (p0.Committed.Value, p0.Lag).Should().Be((2L, 1L));
        groupOverview.Lag.Value.Single(x => x.Partition.Partition == 1).Committed.Value.Should().BeNull("the group has committed nothing on the empty partition");
    }

    [Fact]
    public async Task A_refusal_is_logged_once_at_debug_and_never_as_a_warning()
    {
        (LiveContractCluster admin, string topic, _) = await SeedAsync("logonce");
        await using LiveContractCluster seeded = admin;
        await using EngineHarness engine = As("reader");

        await engine.Catalog.GetTopicOverviewAsync(EngineHarness.ClusterKey, new KafkaTopicName(topic), Token);
        await engine.Catalog.GetTopicOverviewAsync(EngineHarness.ClusterKey, new KafkaTopicName(topic), Token);

        MartenStudio.Tests.Support.CapturedLogEntry refusal = engine.Logs.Entries.Single(static x => x.EventId.Id == 9652);
        refusal.Level.Should().Be(LogLevel.Debug);
        refusal.Message.Should().Contain("DESCRIBE_CONFIGS").And.Contain(topic);
        engine.Logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_wrong_password_is_an_authentication_failure_not_an_unreachable_cluster()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        await using EngineHarness engine = EngineHarness.ForClient(
            KafkaFixture.ClientFor(fixture.Bootstrap, "reader", "not-the-password"),
            configure: options => options.RequestTimeout = TimeSpan.FromSeconds(3));

        KafkaField<KafkaTopicList> topics = await engine.Catalog.ListTopicsAsync(EngineHarness.ClusterKey, Token);

        topics.IsAvailable.Should().BeFalse();
        topics.Error.Should().BeOfType<WireError.AuthenticationFailed>();
        KafkaAccessText.Describe(topics.Error!).Should().Contain("refused KStudio's credentials");
    }
}

/// <summary><see cref="AclCatalogLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class AclCatalogLiveTests39(Kafka39Fixture fixture) : AclCatalogLiveTests(fixture);

/// <summary><see cref="AclCatalogLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class AclCatalogLiveTests4(Kafka4Fixture fixture) : AclCatalogLiveTests(fixture);
