using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;
using KStudio.Core.Windows;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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

        TopicAppears(fixture, topic).Should().BeTrue("a default admin client's GetMetadata(topic) creates the topic here: the tests below mean something");
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
        // Read by a consumer that carries the group's id and is never subscribed (the tests' own helper, not an admin handle: librdkafka #5040).
        await SwallowAsync(() => Task.Run(() => LiveGroupOffsets.Committed(fixture, "kstudio-ctl-" + fixture.RunId, [new TopicPartition(grouped, 0)])));
        TopicsExist(fixture, grouped).Should().BeFalse("an OffsetFetch does not create");
    }

    [Fact]
    public async Task The_engines_admin_client_does_not_create_a_topic_even_when_asked_about_it_by_name()
    {
        // The engine never makes this request (ForbiddenConfluentCallsTests), so this is the admin override's own test: with
        // allow.auto.create.topics deleted from ConfluentClientSettings.BuildAdminConfig, the topic exists afterwards, as the control above shows.
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("override");

        KafkaEngineOptions options = new();
        options.AddCluster("live", fixture.Client("admin"));
        KafkaClusterDefinition definition = new KafkaClusterRegistry(options).Get("live");
        ConfluentClientEvents events = new(new KStudioLoggers(NullLoggerFactory.Instance), new KafkaLogOnce(TimeProvider.System), TimeProvider.System);

        using (IAdminClient admin = ConfluentWireFactory.BuildAdminClient(definition, events))
        {
            admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        }

        TopicsExist(fixture, topic).Should().BeFalse("the engine's admin client is built with allow.auto.create.topics=false");
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
    public async Task The_lag_read_runs_on_a_consumer_backed_handle_and_that_consumer_joins_no_group_and_commits_nothing()
    {
        // D36: the group-offsets read goes through the offsets reader (a consumer that is never subscribed, with a dependent admin client),
        // because an admin handle has no consumer group and librdkafka up to 2.16.0 crashes on NOT_COORDINATOR (#5040). Hard rule 17 holds for it.
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("lag");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, [LiveRecords.At(0, "k", "v")]);
        // Not "kstudio-lag-{RunId}": AclCatalogLiveTests commits offsets for that name in the same fixture, so the group would be listed whenever it ran first.
        string asked = "kstudio-never-joined-" + Guid.NewGuid().ToString("N")[..8];

        WireResult<IReadOnlyList<WireGroupOffset>> read = await cluster.Wire.ListGroupOffsetsAsync(asked, [new WirePartition(topic, 0)], Token);
        read.IsOk.Should().BeTrue("a group that does not exist has no committed offsets, which is an answer");

        KafkaAdminPool<ConfluentOffsetsReader> readers = cluster.Engine.Provider.GetRequiredService<KafkaAdminPool<ConfluentOffsetsReader>>();
        readers.ClientCount.Should().Be(1, "the read created the cluster's offsets reader");

        string readerGroup;
        using (KafkaAdminPool<ConfluentOffsetsReader>.Lease lease = readers.Rent(cluster.Engine.Provider.GetRequiredService<KafkaClusterRegistry>().Get(EngineHarness.ClusterKey)))
        {
            readerGroup = lease.Client.GroupId;
        }

        readerGroup.Should().StartWith("kstudio-").And.NotBe(asked);

        using IAdminClient client = new AdminClientBuilder(fixture.Client("admin")).Build();
        ListConsumerGroupsResult groups = await client.ListConsumerGroupsAsync();
        IReadOnlyList<string> listed = [.. (groups.Valid ?? []).Select(static x => x.GroupId)];
        listed.Should().NotContain([asked, readerGroup], "neither the asked group nor the reader's own was ever joined");
        listed.Should().NotContain(static x => x.StartsWith("kstudio-", StringComparison.Ordinal) && x.Length == "kstudio-".Length + 32, "no group named for the reader exists");
        LiveGroupOffsets.Committed(fixture, readerGroup, [new TopicPartition(topic, 0)]).Should().BeEmpty("the reader committed nothing");
        TopicAppears(fixture, topic).Should().BeTrue();
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
    public async Task The_control_shows_that_a_consumer_with_auto_create_true_creates_the_missing_topic_it_is_assigned()
    {
        // Measured on 3.9.2 and 4.4.0 (REVIEW-ENG-A-FIX 4): librdkafka's CONFIGURATION.md says allow.auto.create.topics applies "when
        // subscribing to or assigning non-existent topics", and it does: a plain consumer with true that is only assigned the topic creates it.
        // So the scan consumer's false is the only line there, and the next test fails if the engine's override is deleted.
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("ctl-consumer");

        ConsumerConfig config = new(fixture.Client("admin"))
        {
            GroupId = "kstudio-ctl-" + fixture.RunId,
            AllowAutoCreateTopics = true,
            EnableAutoCommit = false,
            EnablePartitionEof = true,
        };

        await Task.Run(
            () =>
            {
                using IConsumer<byte[], byte[]> plain = new ConsumerBuilder<byte[], byte[]>(config).Build();
                plain.Assign([new TopicPartitionOffset(topic, 0, 0)]);

                SpinWait.SpinUntil(
                    () =>
                    {
                        try
                        {
                            plain.Consume(TimeSpan.FromMilliseconds(200));
                        }
                        catch (ConsumeException)
                        {
                            // An unknown-partition or group error is not what is measured; the topic's existence is.
                        }

                        return TopicsExist(fixture, topic);
                    },
                    TimeSpan.FromSeconds(30));
            },
            Token);

        TopicsExist(fixture, topic).Should().BeTrue("measured: a consumer with allow.auto.create.topics=true that is assigned a missing topic creates it on this broker");
        await DeleteAsync(topic);
    }

    [Fact]
    public async Task A_scan_reader_built_from_a_host_config_that_allows_auto_create_still_creates_nothing_because_the_engine_sets_it_false()
    {
        // The host's own consumer settings ask for auto-creation; the engine's override (ConfluentClientSettings.BuildConsumerConfig) turns it
        // off. With the override deleted, the control above shows the topic would exist afterwards.
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("reader");

        Dictionary<string, string> hostSettings = new(fixture.Client("admin")) { ["allow.auto.create.topics"] = "true" };
        ClientConfig host = new(hostSettings);
        await using EngineHarness engine = EngineHarness.ForClient(host);

        await Task.Factory.StartNew(
            () =>
            {
                using IWireScanReader reader = engine.Wire.OpenReader(new WireReaderOptions(Guid.NewGuid(), WireIsolation.ReadCommitted, new KafkaScanLimits()));
                reader.Assign([new WirePartitionOffset(topic, 0, 0)]);

                // A consumer assigned a missing topic reports nothing at all (SPIKE S1.6): reads for as long as the control needed, then the proof.
                for (int i = 0; i < 40; i++)
                {
                    reader.Read(TimeSpan.FromMilliseconds(200)).Should().BeSameAs(WireReadOutcome.Idle);
                }

                reader.Close();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        TopicsExist(fixture, topic).Should().BeFalse("the scan consumer is built with allow.auto.create.topics=false whatever the host's config says");
    }

    /// <summary>
    /// Whether the topic exists within 30 s. A broker creates an auto-created topic asynchronously after the metadata request that asked for
    /// it, so a positive control waits on the condition (never a delay); the negative checks stay immediate.
    /// </summary>
    private static bool TopicAppears(KafkaFixture fixture, string topic)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            if (TopicsExist(fixture, topic))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
        }
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
