using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Core.Windows;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Catalog;

/// <summary>
/// AGENTS.md hard rule 17 on the broker, for metadata and window resolution: asking about a topic that does not exist, as a principal that
/// <b>may create topics</b> on a broker that <b>auto-creates</b> them, finds it still absent. SPIKE S1.6: a default admin client's single-topic
/// metadata request creates the topic; the engine's admin client, built with <c>allow.auto.create.topics=false</c>, does not.
/// </summary>
/// <remarks>
/// Measured 2026-11-10 on 3.9.2 and 4.4.0 (REVIEW-ENG-A 2): of the admin calls the engine makes, <b>only</b> the single-topic metadata request
/// creates a topic, even for a default client; the engine never makes it (<c>ForbiddenConfluentCallsTests</c>), and the admin override is the
/// second line behind that fence. Every wire call about a missing topic answers a value and creates nothing: that is
/// <c>WireContract.Every_wire_call_about_a_missing_topic_answers_not_found_and_creates_nothing</c>, which runs on both images as <c>admin</c>.
/// </remarks>
public abstract class ReadNeverWritesMetadataLiveTests(KafkaFixture fixture)
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_control_proves_this_broker_would_have_created_the_topic_for_a_default_admin_client()
    {
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("control");

        using IAdminClient plain = new AdminClientBuilder(fixture.Client("admin")).Build();
        plain.GetMetadata(topic, TimeSpan.FromSeconds(10));

        TopicsExist(fixture, topic).Should().BeTrue("a default admin client's GetMetadata(topic) creates the topic here: the tests below mean something");
        await DeleteAsync(topic);
    }

    [Fact]
    public async Task The_control_shows_that_of_the_admin_calls_the_engine_makes_none_creates_a_topic_for_a_default_admin_client()
    {
        // Measured, not assumed: if a later broker or librdkafka made one of these create the topic, this fails and the engine's
        // admin override stops being a second line and becomes the only one.
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        using IAdminClient plain = new AdminClientBuilder(fixture.Client("admin")).Build();

        string described = cluster.NewTopic("ctl-describe");
        await SwallowAsync(() => plain.DescribeTopicsAsync(TopicCollection.OfTopicNames([described])));
        TopicsExist(fixture, described).Should().BeFalse("DescribeTopics does not create");

        string configured = cluster.NewTopic("ctl-config");
        await SwallowAsync(() => plain.DescribeConfigsAsync([new ConfigResource { Type = ResourceType.Topic, Name = configured }]));
        TopicsExist(fixture, configured).Should().BeFalse("DescribeConfigs does not create");

        string listed = cluster.NewTopic("ctl-offsets");
        await SwallowAsync(() => plain.ListOffsetsAsync([new TopicPartitionOffsetSpec { TopicPartition = new TopicPartition(listed, 0), OffsetSpec = OffsetSpec.Latest() }]));
        TopicsExist(fixture, listed).Should().BeFalse("ListOffsets does not create");

        string grouped = cluster.NewTopic("ctl-group");
        await SwallowAsync(() => plain.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions("kstudio-ctl-" + fixture.RunId, [new TopicPartition(grouped, 0)])]));
        TopicsExist(fixture, grouped).Should().BeFalse("ListConsumerGroupOffsets does not create");
    }

    [Fact]
    public async Task The_control_proves_a_default_consumer_that_subscribes_would_create_the_topic_which_is_why_the_engine_never_subscribes()
    {
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("ctl-subscribe");

        ConsumerConfig config = new(fixture.Client("admin")) { GroupId = "kstudio-ctl-" + fixture.RunId, AllowAutoCreateTopics = true, AutoOffsetReset = AutoOffsetReset.Earliest };

        await Task.Factory.StartNew(
            () =>
            {
                using IConsumer<byte[], byte[]> consumer = new ConsumerBuilder<byte[], byte[]>(config).Build();
                consumer.Subscribe(topic);
                DateTime deadline = DateTime.UtcNow.AddSeconds(30);

                while (DateTime.UtcNow < deadline && !TopicsExist(fixture, topic))
                {
                    consumer.Consume(TimeSpan.FromMilliseconds(250));
                }

                consumer.Close();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        TopicsExist(fixture, topic).Should().BeTrue("a subscribing consumer's metadata request auto-creates the topic here");
        await DeleteAsync(topic);
    }

    [Fact]
    public async Task Every_wire_call_the_engine_makes_about_a_missing_topic_creates_nothing_through_the_engines_own_wire()
    {
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("wire");
        IKafkaWire wire = cluster.Wire;

        await wire.DescribeTopicsAsync([new KafkaTopicName(topic)], Token);
        await wire.DescribeTopicConfigAsync(new KafkaTopicName(topic), Token);
        await wire.ListOffsetsAsync([new WirePartition(topic, 0)], WireOffsetSpec.Latest, WireIsolation.ReadCommitted, Token);
        await wire.ListGroupOffsetsAsync("kstudio-absent-" + fixture.RunId, [new WirePartition(topic, 0)], Token);
        await wire.GetMetadataAsync(Token);

        TopicsExist(fixture, topic).Should().BeFalse();
    }

    [Fact]
    public async Task An_overview_of_a_missing_topic_creates_nothing()
    {
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("missing");

        KafkaField<KafkaTopicOverview> overview = await cluster.Engine.Catalog.GetTopicOverviewAsync(EngineHarness.ClusterKey, new KafkaTopicName(topic), Token);
        KafkaField<KafkaTopicList> list = await cluster.Engine.Catalog.ListTopicsAsync(EngineHarness.ClusterKey, Token);
        KafkaClusterOverview description = await cluster.Engine.Catalog.GetClusterOverviewAsync(EngineHarness.ClusterKey, Token);

        overview.Error.Should().Be(new WireError.NotFound(KafkaResourceType.Topic, topic));
        list.Value.Topics.Select(static x => x.Name).Should().NotContain(topic);
        description.Description.IsAvailable.Should().BeTrue();
        TopicsExist(fixture, topic).Should().BeFalse("the engine's metadata reads never create a topic");
    }

    [Fact]
    public async Task Resolving_a_window_over_a_missing_topic_creates_nothing_and_says_it_is_not_found()
    {
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("window");

        ResolvedWindow resolved = await cluster.Engine.Resolver.ResolveAsync(
            EngineHarness.ClusterKey,
            [new KafkaTopicName(topic)],
            new KafkaWindow(DateTimeOffset.FromUnixTimeMilliseconds(T0), DateTimeOffset.FromUnixTimeMilliseconds(T0 + 60_000)),
            KafkaIsolation.Committed,
            null,
            Token);

        resolved.Topics.Single().Refusal.Should().Be(new WireError.NotFound(KafkaResourceType.Topic, topic));
        resolved.Ranges.Should().BeEmpty();
        TopicsExist(fixture, topic).Should().BeFalse();
    }

    [Fact]
    public async Task A_reader_assigned_a_missing_topic_creates_nothing_because_an_assign_never_does()
    {
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("reader");

        await Task.Factory.StartNew(
            () =>
            {
                using IWireScanReader reader = cluster.Wire.OpenReader(new WireReaderOptions(Guid.NewGuid(), WireIsolation.ReadCommitted, new KafkaScanLimits()));
                reader.Assign([new WirePartitionOffset(topic, 0, 0)]);

                // A consumer assigned a missing topic reports nothing at all (SPIKE S1.6): a few reads, then the proof it created nothing.
                for (int i = 0; i < 20; i++)
                {
                    reader.Read(TimeSpan.FromMilliseconds(100)).Should().BeSameAs(WireReadOutcome.Idle);
                }

                reader.Close();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        TopicsExist(fixture, topic).Should().BeFalse("the scan consumer is built with allow.auto.create.topics=false");
    }

    private static bool TopicsExist(KafkaFixture fixture, string topic)
    {
        using IAdminClient check = new AdminClientBuilder(new AdminClientConfig(fixture.Client("admin")) { AllowAutoCreateTopics = false }).Build();
        return check.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Any(x => x.Topic == topic && x.Error.Code == ErrorCode.NoError);
    }

    private static async Task SwallowAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (KafkaException)
        {
            // The call about a missing topic fails; what matters is that it did not create it.
        }
    }

    private async Task DeleteAsync(string topic)
    {
        using IAdminClient admin = new AdminClientBuilder(fixture.Client("admin")).Build();
        await admin.DeleteTopicsAsync([topic]);
    }
}

/// <summary><see cref="ReadNeverWritesMetadataLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class ReadNeverWritesMetadataLiveTests39(Kafka39Fixture fixture) : ReadNeverWritesMetadataLiveTests(fixture);

/// <summary><see cref="ReadNeverWritesMetadataLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class ReadNeverWritesMetadataLiveTests4(Kafka4Fixture fixture) : ReadNeverWritesMetadataLiveTests(fixture);
