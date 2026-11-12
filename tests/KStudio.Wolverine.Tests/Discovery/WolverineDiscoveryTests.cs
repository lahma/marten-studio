using System.Text.Json;

using Confluent.Kafka;

using KStudio;
using KStudio.Core.Clusters;
using KStudio.Internal;

using KStudioWolverine.Tests.Support;

using Wolverine;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Interop;
using Wolverine.Runtime.Routing;

namespace KStudioWolverine.Tests.Discovery;

/// <summary>
/// D43, decision 5: what a Wolverine application's Kafka configuration becomes in KStudio, over a <see cref="WolverineOptions" /> built in the test.
/// Wolverine's fluent configuration of an endpoint is deferred until its runtime compiles it, so the topics are shaped through the endpoints' own
/// public members, which is what the adapter reads.
/// </summary>
public class WolverineDiscoveryTests
{
    private static KafkaDiscoveredTopic Topic(KafkaContributedCluster cluster, string name) => cluster.Topics.Single(x => x.Name == name);

    [Fact]
    public void The_default_transport_is_the_wolverine_cluster_with_its_bootstrap_servers_and_display_name()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options, "kafka-1:9092,kafka-2:9092");

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        KafkaContributedCluster cluster = contribution.Clusters.Should().ContainSingle().Which;
        (cluster.Key, cluster.TenantId).Should().Be(("wolverine", null));
        cluster.ConsumerConfig.BootstrapServers.Should().Be("kafka-1:9092,kafka-2:9092");
        cluster.AdminConfig.BootstrapServers.Should().Be("kafka-1:9092,kafka-2:9092");
        cluster.Builder.DisplayNameText.Should().Be("Wolverine");
    }

    [Fact]
    public void A_named_broker_is_its_own_cluster_keyed_with_the_slug_of_its_name()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options);
        WolverineRig.Named(options, "Payments EU!", "pay:9092");

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        contribution.Clusters.Select(static x => x.Key).Should().Equal("wolverine", "wolverine-payments-eu");
        contribution.Clusters[1].Builder.DisplayNameText.Should().Be("Wolverine · Payments EU!");
        contribution.Clusters[1].ConsumerConfig.BootstrapServers.Should().Be("pay:9092");
    }

    [Theory]
    [InlineData("payments", "payments")]
    [InlineData("Payments EU", "payments-eu")]
    [InlineData("  --a__b--  ", "a-b")]
    [InlineData("a---b", "a-b")]
    [InlineData("é", "")]
    [InlineData("ÄB", "b")]
    [InlineData("!!!", "")]
    [InlineData("Order.Service_2", "order-service-2")]
    public void The_slug_is_lower_case_with_other_characters_replaced_collapsed_and_trimmed(string name, string expected)
    {
        WolverineClusterSource.Slug(name).Should().Be(expected);
    }

    [Fact]
    public void A_broker_name_that_leaves_no_slug_is_noted_and_not_listed()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Named(options, "!!!", "x:9092");

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        contribution.Clusters.Should().BeEmpty();
        contribution.Notes.Should().ContainSingle().Which.Text.Should().Contain("makes no cluster key");
    }

    [Fact]
    public void The_cluster_key_is_the_hosts_to_choose_and_a_bad_one_is_refused_when_it_is_set()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options);
        WolverineRig.Named(options, "payments");

        WolverineRig.Contribute(options, static o => o.ClusterKey = "orders").Clusters.Select(static x => x.Key).Should().Equal("orders", "orders-payments");

        Action refuse = () => new KafkaEngineOptions().UseWolverineKafka(static o => o.ClusterKey = "Not A Key");

        refuse.Should().Throw<ArgumentException>().WithMessage("*ClusterKey*");
    }

    [Fact]
    public void A_key_that_a_host_cluster_holds_is_skipped_and_noted_and_the_hosts_cluster_stays()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options);

        KafkaClusterRegistry registry = WolverineRig.Registry(
            options,
            static host => host.AddCluster("wolverine", new ClientConfig { BootstrapServers = "host:9092" }));

        registry.Clusters.Should().ContainSingle().Which.Source.Should().BeNull();
        registry.Notes.Should().ContainSingle().Which.Text.Should().Contain("already registered");
    }

    [Fact]
    public void A_transport_with_no_bootstrap_servers_is_noted_and_not_listed()
    {
        WolverineOptions options = WolverineRig.Options();
        options.UseKafka("temp:9092");
        KafkaTransport transport = options.Transports.OfType<KafkaTransport>().Single();
        transport.ConsumerConfig.BootstrapServers = null;
        transport.AdminClientConfig.BootstrapServers = null;

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        contribution.Clusters.Should().BeEmpty();
        contribution.Notes.Should().ContainSingle().Which.Text.Should().Contain("no bootstrap servers");
    }

    [Fact]
    public void A_transport_that_set_only_one_side_is_read_with_the_others_bootstrap_servers()
    {
        WolverineOptions options = WolverineRig.Options();
        options.UseKafka("temp:9092");
        KafkaTransport transport = options.Transports.OfType<KafkaTransport>().Single();
        transport.AdminClientConfig.BootstrapServers = null;

        KafkaContributedCluster cluster = WolverineRig.Contribute(options).Clusters.Should().ContainSingle().Which;

        cluster.AdminConfig.BootstrapServers.Should().Be("temp:9092");
        transport.AdminClientConfig.BootstrapServers.Should().BeNull("the transport's own configuration is never written");
    }

    [Fact]
    public void Roles_and_message_types_follow_the_listener_and_the_subscriptions()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        KafkaTopic listener = WolverineRig.Listen(transport, "checkout.v1");
        listener.MessageType = typeof(PlaceOrder);
        WolverineRig.Send<OrderPlaced>(transport, "checkout.confirmed.v1");
        KafkaTopic both = WolverineRig.Listen(transport, "echo.v1");
        both.Subscriptions.Add(new Subscription { Scope = RoutingScope.Namespace, Match = "Orders.Events" });
        both.Subscriptions.Add(new Subscription { Scope = RoutingScope.Assembly, Match = "Orders" });
        both.Subscriptions.Add(new Subscription { Scope = RoutingScope.Implements, Match = "Orders.IEvent" });
        both.Subscriptions.Add(new Subscription { Scope = RoutingScope.All });

        KafkaContributedCluster cluster = WolverineRig.Contribute(options).Clusters.Should().ContainSingle().Which;

        Topic(cluster, "checkout.v1").Role.Should().Be(KafkaTopicRole.Listener);
        Topic(cluster, "checkout.v1").MessageTypes.Should().Equal(typeof(PlaceOrder).FullName);
        Topic(cluster, "checkout.confirmed.v1").Role.Should().Be(KafkaTopicRole.Sender);
        Topic(cluster, "checkout.confirmed.v1").MessageTypes.Should().Equal(typeof(OrderPlaced).FullName);
        Topic(cluster, "echo.v1").Role.Should().Be(KafkaTopicRole.ListenerAndSender);
        Topic(cluster, "echo.v1").MessageTypes.Should().Equal("namespace Orders.Events", "assembly Orders", "implements Orders.IEvent", "all messages");
        cluster.Topics.Select(static x => x.Name).Should().Equal("checkout.confirmed.v1", "checkout.v1", "echo.v1");
    }

    [Fact]
    public void At_most_twenty_message_types_are_listed_for_a_topic()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        KafkaTopic sender = transport.Topics["many.v1"];

        for (int i = 0; i < 30; i++)
        {
            sender.Subscriptions.Add(new Subscription { Scope = RoutingScope.Type, Match = "Orders.Type" + i });
        }

        WolverineRig.Contribute(options).Clusters.Single().Topics.Single().MessageTypes.Should().HaveCount(20);
    }

    [Fact]
    public void The_wolverine_topics_marker_and_an_endpoint_nothing_uses_are_not_topics()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        transport.Topics[KafkaTopic.WolverineTopicsName].IsListener = true;
        transport.Topics[KafkaTopic.WolverineTopicsName].Subscriptions.Add(new Subscription { Scope = RoutingScope.All });
        _ = transport.Topics["unused.v1"];
        WolverineRig.Listen(transport, "real.v1");

        WolverineRig.Contribute(options).Clusters.Single().Topics.Select(static x => x.Name).Should().Equal("real.v1");
    }

    [Fact]
    public void A_topic_on_another_cluster_is_skipped_and_noted_by_name_on_the_page_only()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options, "main:9092");
        WolverineRig.Listen(transport, "local.v1");
        KafkaTopic elsewhere = WolverineRig.Listen(transport, "elsewhere.v1");
        WolverineRig.OwnConsumerConfig(elsewhere, new ConsumerConfig { BootstrapServers = "other:9092" });
        KafkaTopic sameCluster = WolverineRig.Listen(transport, "same.v1");
        WolverineRig.OwnConsumerConfig(sameCluster, new ConsumerConfig { BootstrapServers = " main:9092 " });

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        contribution.Clusters.Single().Topics.Select(static x => x.Name).Should().Equal("local.v1", "same.v1");
        KafkaSourceNote note = contribution.Notes.Should().ContainSingle().Which;
        note.Text.Should().Contain("elsewhere.v1").And.Contain("another cluster");
        note.What.Should().NotContain("elsewhere");
        note.Reason.Should().NotContain("elsewhere");
    }

    [Fact]
    public void The_consumer_group_is_the_topics_own_then_the_transports_then_the_service_name()
    {
        WolverineOptions options = WolverineRig.Options("service-name");
        KafkaTransport transport = WolverineRig.Default(options);
        WolverineRig.Listen(transport, "a.v1");
        WolverineRig.OwnConsumerConfig(WolverineRig.Listen(transport, "b.v1"), new ConsumerConfig { GroupId = "topic-group" });

        KafkaContributedCluster fromService = WolverineRig.Contribute(options).Clusters.Single();

        Topic(fromService, "a.v1").ConsumerGroup.Should().Be("service-name");
        Topic(fromService, "b.v1").ConsumerGroup.Should().Be("topic-group");

        transport.ConsumerConfig.GroupId = "transport-group";
        KafkaContributedCluster fromTransport = WolverineRig.Contribute(options).Clusters.Single();

        Topic(fromTransport, "a.v1").ConsumerGroup.Should().Be("transport-group");
        Topic(fromTransport, "b.v1").ConsumerGroup.Should().Be("topic-group");
        fromTransport.Builder.ConsumerGroups.Should().Equal("topic-group", "transport-group");
    }

    [Fact]
    public void A_sender_has_no_consumer_group()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        transport.ConsumerConfig.GroupId = "g";
        WolverineRig.Send<OrderPlaced>(transport, "out.v1");

        KafkaContributedCluster cluster = WolverineRig.Contribute(options).Clusters.Single();

        Topic(cluster, "out.v1").ConsumerGroup.Should().BeNull();
        cluster.Builder.ConsumerGroups.Should().BeEmpty();
    }

    [Fact]
    public void A_topic_group_lists_each_of_its_topics_as_a_listener()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options);
        options.ListenToKafkaTopics("group-a.v1", "group-b.v1");

        KafkaContributedCluster cluster = WolverineRig.Contribute(options).Clusters.Single();

        cluster.Topics.Select(static x => (x.Name, x.Role)).Should().Equal(("group-a.v1", KafkaTopicRole.Listener), ("group-b.v1", KafkaTopicRole.Listener));
    }

    [Fact]
    public void The_native_dead_letter_topic_is_listed_when_any_endpoint_enables_it_and_it_is_not_a_listener()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        WolverineRig.Listen(transport, "checkout.v1").NativeDeadLetterQueueEnabled = true;
        _ = transport.Topics[transport.DeadLetterQueueTopicName];

        KafkaContributedCluster cluster = WolverineRig.Contribute(options).Clusters.Single();

        KafkaDiscoveredTopic dead = Topic(cluster, "wolverine-dead-letter-queue");
        dead.Role.Should().Be(KafkaTopicRole.DeadLetter);
        dead.Description.Should().Be("Wolverine dead letters");
        dead.ConsumerGroup.Should().BeNull("Wolverine never consumes it by default");
        cluster.Builder.ConsumerGroups.Should().Equal("orders-app");
    }

    [Fact]
    public void Without_native_dead_letters_no_dead_letter_topic_is_listed()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Listen(WolverineRig.Default(options), "checkout.v1");

        WolverineRig.Contribute(options).Clusters.Single().Topics.Select(static x => x.Name).Should().Equal("checkout.v1");
    }

    [Fact]
    public void A_custom_dead_letter_topic_name_is_the_one_listed()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        transport.DeadLetterQueueTopicName = "orders.dlq";
        WolverineRig.Listen(transport, "checkout.v1").NativeDeadLetterQueueEnabled = true;

        WolverineRig.Contribute(options).Clusters.Single().Topics.Select(static x => x.Name).Should().Equal("checkout.v1", "orders.dlq");
    }

    [Fact]
    public void A_retry_tier_of_a_discovered_topic_says_so()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        WolverineRig.Listen(transport, "checkout.v1");
        WolverineRig.Listen(transport, "checkout.v1.retry.30s");
        WolverineRig.Listen(transport, "orphan.retry.5m");

        KafkaContributedCluster cluster = WolverineRig.Contribute(options).Clusters.Single();

        Topic(cluster, "checkout.v1.retry.30s").Description.Should().Be("Wolverine retry tier of checkout.v1");
        Topic(cluster, "checkout.v1").Description.Should().Be("Wolverine listener");
        Topic(cluster, "orphan.retry.5m").Description.Should().Be("Wolverine listener", "its source is not a topic found here");
    }

    [Theory]
    [InlineData(null, "crc32")]
    [InlineData("consistent_random", "crc32")]
    [InlineData("murmur2", "murmur2")]
    [InlineData("murmur2_random", "murmur2")]
    [InlineData("fnv1a", "fnv1a")]
    [InlineData("fnv1a_random", "fnv1a")]
    [InlineData("consistent", null)]
    [InlineData("random", null)]
    [InlineData("something-new", null)]
    public void The_partitioner_claim_follows_the_producer_configuration(string? setting, string? claim)
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);

        if (setting is not null)
        {
            transport.ProducerConfig.Set("partitioner", setting);
        }

        WolverineRig.Send<OrderPlaced>(transport, "out.v1");

        WolverineRig.Contribute(options).Clusters.Single().Topics.Single().Partitioner?.Name.Should().Be(claim);
        if (claim is null)
        {
            WolverineRig.Contribute(options).Clusters.Single().Topics.Single().Partitioner.Should().BeNull();
        }
    }

    [Fact]
    public void A_topics_own_producer_configuration_decides_its_partitioner()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        transport.ProducerConfig.Set("partitioner", "murmur2");
        KafkaTopic topic = WolverineRig.Send<OrderPlaced>(transport, "out.v1");
        WolverineRig.OwnProducerConfig(topic, new ProducerConfig { BootstrapServers = "broker:9092" });
        topic.ProducerConfig!.Set("partitioner", "fnv1a");

        WolverineRig.Contribute(options).Clusters.Single().Topics.Single().Partitioner.Should().BeSameAs(KafkaPartitioner.Fnv1a);
    }

    [Fact]
    public void A_topic_serialized_as_CloudEvents_is_declared_so_and_names_its_serializer()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        KafkaTopic cloud = WolverineRig.Listen(transport, "cloud.v1");
        cloud.DefaultSerializer = new CloudEventsMapper(new HandlerGraph(), new JsonSerializerOptions());
        WolverineRig.Listen(transport, "plain.v1");

        KafkaContributedCluster cluster = WolverineRig.Contribute(options).Clusters.Single();

        Topic(cluster, "cloud.v1").CloudEvents.Should().BeTrue();
        Topic(cluster, "cloud.v1").Serializer.Should().Be(nameof(CloudEventsMapper));
        Topic(cluster, "plain.v1").CloudEvents.Should().BeFalse();
        Topic(cluster, "plain.v1").Serializer.Should().BeNull();
    }

    [Fact]
    public void A_CloudEvents_topic_is_decoded_as_CloudEvents_unless_the_host_declared_a_decoder()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        WolverineRig.Listen(transport, "cloud.v1").DefaultSerializer = new CloudEventsMapper(new HandlerGraph(), new JsonSerializerOptions());
        WolverineRig.Listen(transport, "declared.v1").DefaultSerializer = new CloudEventsMapper(new HandlerGraph(), new JsonSerializerOptions());

        KafkaClusterRegistry registry = WolverineRig.Registry(options, static host => host.Topic("declared.v1", static t => t.Value(KafkaDecoders.Json)));

        KafkaClusterDefinition definition = registry.Get("wolverine");
        KafkaDecoders.BuiltInKind(definition.Descriptor("cloud.v1").Settings.DecoderInstance).Should().Be(KafkaBuiltInDecoder.CloudEvents);
        KafkaDecoders.BuiltInKind(definition.Descriptor("declared.v1").Settings.DecoderInstance).Should().Be(KafkaBuiltInDecoder.Json);
    }

    [Fact]
    public void A_host_declaration_keeps_its_redaction_rules_and_discovery_adds_none()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        WolverineRig.Listen(transport, "customers.v1");
        WolverineRig.Listen(transport, "other.v1");

        KafkaClusterRegistry registry = WolverineRig.Registry(
            options,
            static host => host.Topic("customers.v1", static t => t.Redact(static r => r.Header("customer-email"))));

        KafkaClusterDefinition definition = registry.Get("wolverine");
        definition.Descriptor("customers.v1").Settings.Rules!.Declared.Should().ContainSingle(static x => x.Field == "customer-email");
        definition.Descriptor("customers.v1").Discovered.Should().NotBeNull();
        definition.Descriptor("other.v1").Settings.Rules.Should().BeNull("discovery adds no rule: the host declares them");
        definition.Descriptor("other.v1").Declared.Should().BeTrue();
        definition.Descriptor("other.v1").KeyFormatDeclared.Should().BeTrue("Wolverine writes UTF-8 keys, so the format is declared and not assumed");
    }

    [Fact]
    public void A_custom_envelope_mapper_is_said_in_the_description()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);
        KafkaTopic custom = WolverineRig.Listen(transport, "custom.v1");
        custom.EnvelopeMapper = new CustomMapper();

        WolverineRig.Contribute(options).Clusters.Single().Topics.Single().Description.Should().Be(
            "Wolverine listener · custom envelope mapper: header names may differ");
    }

    [Fact]
    public void More_than_500_topics_list_the_first_500_by_name_and_say_so()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);

        for (int i = 0; i < 520; i++)
        {
            WolverineRig.Listen(transport, "topic-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
        }

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        contribution.Clusters.Single().Topics.Should().HaveCount(500);
        contribution.Clusters.Single().Topics[^1].Name.Should().Be("topic-0499");
        contribution.Notes.Should().ContainSingle().Which.Text.Should().Contain("first 500");
    }

    [Fact]
    public void The_hook_is_applied_to_each_clusters_builder_and_is_told_the_broker_name()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options);
        WolverineRig.Named(options, "payments");
        List<string?> brokers = [];

        KafkaClusterContribution contribution = WolverineRig.Contribute(
            options,
            o => o.Cluster((broker, builder) =>
            {
                brokers.Add(broker);
                builder.DisplayName("Mine " + (broker ?? "default")).Environment("production");
                builder.OAuthBearerTokenRefresh(static (_, _) => { });
            }));

        brokers.Should().Equal(null, "payments");
        contribution.Clusters.Select(static x => x.Builder.DisplayNameText).Should().Equal("Mine default", "Mine payments");
        contribution.Clusters.Should().OnlyContain(static x => x.Builder.EnvironmentText == "production" && x.Builder.TokenRefresh != null);
    }

    [Fact]
    public void A_topic_the_hook_declares_replaces_the_discovered_descriptor_field_by_field()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Listen(WolverineRig.Default(options), "checkout.v1").DefaultSerializer = new CloudEventsMapper(new HandlerGraph(), new JsonSerializerOptions());

        KafkaClusterRegistry registry = WolverineRig.Registry(
            options,
            configure: static o => o.Cluster(static (_, b) => b.Topic("checkout.v1", static t => t.Description("Checkout commands").Key(KafkaKeyFormat.Utf8String))));

        KafkaTopicDescriptor descriptor = registry.Get("wolverine").Descriptor("checkout.v1");

        descriptor.Description.Should().Be("Checkout commands");
        KafkaDecoders.BuiltInKind(descriptor.Settings.DecoderInstance).Should().Be(KafkaBuiltInDecoder.CloudEvents, "the host left the decoder open, and Wolverine said CloudEvents");
        descriptor.Discovered.Should().NotBeNull();
    }

    [Fact]
    public void A_hook_that_throws_is_noted_and_the_cluster_is_still_listed()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options);

        KafkaClusterContribution contribution = WolverineRig.Contribute(options, static o => o.Cluster(static (_, _) => throw new InvalidOperationException("secret")));

        contribution.Clusters.Should().ContainSingle();
        KafkaSourceNote note = contribution.Notes.Should().ContainSingle().Which;
        note.Text.Should().Contain("InvalidOperationException").And.NotContain("secret");
    }

    [Fact]
    public void Without_Wolverine_registered_the_source_says_so_and_lists_nothing()
    {
        KafkaClusterContribution contribution = WolverineRig.Contribute(null);

        contribution.Clusters.Should().BeEmpty();
        contribution.Notes.Should().ContainSingle().Which.Text.Should().Contain("not registered");
    }

    [Fact]
    public void A_Wolverine_with_no_Kafka_transport_says_so()
    {
        KafkaClusterContribution contribution = WolverineRig.Contribute(WolverineRig.Options());

        contribution.Clusters.Should().BeEmpty();
        contribution.Notes.Should().ContainSingle().Which.Text.Should().Contain("no Kafka transport");
    }

    [Fact]
    public void At_most_fifty_consumer_groups_are_handed_over_and_the_rest_noted()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport transport = WolverineRig.Default(options);

        for (int i = 0; i < 60; i++)
        {
            string name = "group-topic-" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            WolverineRig.OwnConsumerConfig(WolverineRig.Listen(transport, name), new ConsumerConfig { GroupId = "g" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) });
        }

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        contribution.Clusters.Single().Builder.ConsumerGroups.Should().HaveCount(50);
        contribution.Notes.Should().ContainSingle().Which.Text.Should().Contain("first 50");
        WolverineRig.Registry(options).Clusters.Should().ContainSingle("the cluster passes the registry's own rules");
    }

    [Fact]
    public void The_registration_adds_one_source_and_reads_nothing_until_the_registry_is_built()
    {
        KafkaEngineOptions engine = new();

        engine.UseWolverineKafka().Should().BeSameAs(engine);

        engine.ClusterSources.Should().ContainSingle();
    }

    private sealed record PlaceOrder;

    private sealed record OrderPlaced;

    private sealed class CustomMapper : IKafkaEnvelopeMapper
    {
        public void MapIncomingToEnvelope(Envelope envelope, Message<string, byte[]> incoming)
        {
        }

        public void MapEnvelopeToOutgoing(Envelope envelope, Message<string, byte[]> outgoing)
        {
        }
    }
}
