using Confluent.Kafka;

using KStudio.Core.Clusters;

using KStudioWolverine.Tests.Support;

using Wolverine;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;

namespace KStudioWolverine.Tests.Discovery;

/// <summary>
/// D43, decision 5.9: the adapter writes nothing. Every configuration it touches is byte for byte unchanged afterwards, nothing is indexed (the
/// indexer of <c>Topics</c> creates an endpoint), none of Wolverine's builder callbacks is run or replaced, and the configurations it hands over
/// are copies. The forbidden calls themselves are held on the IL by <c>ForbiddenWolverineCallsTests</c>.
/// </summary>
public class WolverineNeverWritesTests
{
    private static string[] Snapshot(KafkaTransport transport) =>
    [
        .. WolverineRig.Pairs(transport.ConsumerConfig).Select(static x => "consumer:" + x),
        .. WolverineRig.Pairs(transport.ProducerConfig).Select(static x => "producer:" + x),
        .. WolverineRig.Pairs(transport.AdminClientConfig).Select(static x => "admin:" + x),
        .. transport.Topics.OrderBy(static x => x.TopicName, StringComparer.Ordinal).SelectMany(static topic => new[]
        {
            topic.TopicName + "|consumer:" + (topic.ConsumerConfig is null ? "none" : string.Join(',', WolverineRig.Pairs(topic.ConsumerConfig))),
            topic.TopicName + "|producer:" + (topic.ProducerConfig is null ? "none" : string.Join(',', WolverineRig.Pairs(topic.ProducerConfig))),
            topic.TopicName + "|dlq:" + topic.NativeDeadLetterQueueEnabled,
        }),
    ];

    private static (WolverineOptions Options, KafkaTransport Parent) Application()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport parent = WolverineRig.Default(options, "parent:9092");
        parent.ConsumerConfig.Set("sasl.password", "hunter2");
        parent.ConsumerConfig.GroupId = null;

        // A topic with its own consumer configuration that omits the bootstrap servers and the group, which GetEffectiveConsumerConfig would fill in.
        KafkaTopic partial = WolverineRig.Listen(parent, "partial.v1");
        WolverineRig.OwnConsumerConfig(partial, new ConsumerConfig { AutoOffsetReset = AutoOffsetReset.Earliest });
        WolverineRig.OwnProducerConfig(partial, new ProducerConfig { Acks = Acks.All });
        WolverineRig.Send<string>(parent, "out.v1").NativeDeadLetterQueueEnabled = true;
        options.ListenToKafkaTopics("group-a.v1", "group-b.v1");

        return (options, parent);
    }

    [Fact]
    public void Every_configuration_pair_of_every_transport_and_topic_is_unchanged_after_discovery()
    {
        (WolverineOptions options, KafkaTransport parent) = Application();
        string[] before = Snapshot(parent);

        _ = WolverineRig.Contribute(options);

        Snapshot(parent).Should().Equal(before);
        parent.ConsumerConfig.GroupId.Should().BeNull("the group is read from the service name, never written into the configuration");
        parent.Topics["partial.v1"].ConsumerConfig!.BootstrapServers.Should().BeNull("the topic's own configuration is not completed from the parent's");
        parent.Topics["partial.v1"].ConsumerConfig!.GroupId.Should().BeNull();
    }

    [Fact]
    public void Nothing_is_indexed_so_no_endpoint_is_created()
    {
        (WolverineOptions options, KafkaTransport parent) = Application();
        string[] topicsBefore = [.. parent.Topics.Select(static x => x.TopicName)];
        int endpointsBefore = parent.Endpoints().Count();

        _ = WolverineRig.Contribute(options);

        parent.Topics.Select(static x => x.TopicName).Should().Equal(topicsBefore);
        parent.Endpoints().Count().Should().Be(endpointsBefore);
    }

    [Fact]
    public void The_three_builder_callbacks_are_neither_run_nor_replaced()
    {
        WolverineOptions options = WolverineRig.Options();
        int calls = 0;
        Action<ProducerBuilder<string, byte[]>> producers = _ => calls++;
        Action<ConsumerBuilder<string, byte[]>> consumers = _ => calls++;
        Action<AdminClientBuilder> admins = _ => calls++;
        options.UseKafka("broker:9092").ConfigureProducerBuilders(producers).ConfigureConsumerBuilders(consumers).ConfigureAdminClientBuilders(admins);
        KafkaTransport transport = options.Transports.OfType<KafkaTransport>().Single();
        WolverineRig.Listen(transport, "a.v1");

        _ = WolverineRig.Contribute(options, static o => o.Cluster(static (_, b) => b.DisplayName("x")));
        _ = WolverineRig.Registry(options);

        calls.Should().Be(0);
        transport.ConfigureProducerBuilders.Should().BeSameAs(producers);
        transport.ConfigureConsumerBuilders.Should().BeSameAs(consumers);
        transport.ConfigureAdminClientBuilders.Should().BeSameAs(admins);
    }

    [Fact]
    public void The_configurations_handed_over_are_copies_that_cannot_write_back()
    {
        (WolverineOptions options, KafkaTransport parent) = Application();
        string[] before = Snapshot(parent);

        KafkaContributedCluster cluster = WolverineRig.Contribute(options).Clusters.Single();
        cluster.ConsumerConfig.BootstrapServers = "attacker:9092";
        cluster.AdminConfig.BootstrapServers = "attacker:9092";
        cluster.ConsumerConfig.Set("sasl.password", "changed");

        Snapshot(parent).Should().Equal(before);
        parent.ConsumerConfig.BootstrapServers.Should().Be("parent:9092");
    }

    [Fact]
    public void A_tenants_child_transport_is_not_written_either()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport parent = WolverineRig.Default(options, "parent:9092");
        WolverineRig.Listen(parent, "a.v1");
        options.UseKafka("parent:9092").AddTenant("acme", "tenant:9092");
        WolverineRig.ConnectTenants(options, parent);
        KafkaTransport tenant = WolverineTenantProbeTests.TenantTransports(parent).Single();
        string[] before =
        [
            .. WolverineRig.Pairs(tenant.ConsumerConfig), .. WolverineRig.Pairs(tenant.ProducerConfig), .. WolverineRig.Pairs(tenant.AdminClientConfig),
        ];

        KafkaContributedCluster contributed = WolverineRig.Contribute(options).Clusters.Single(static x => x.TenantId == "acme");
        contributed.ConsumerConfig.BootstrapServers = "attacker:9092";

        string[] after =
        [
            .. WolverineRig.Pairs(tenant.ConsumerConfig), .. WolverineRig.Pairs(tenant.ProducerConfig), .. WolverineRig.Pairs(tenant.AdminClientConfig),
        ];
        after.Should().Equal(before);
    }

    [Fact]
    public void Discovery_twice_gives_the_same_answer_because_it_changes_nothing()
    {
        (WolverineOptions options, _) = Application();

        KafkaClusterContribution first = WolverineRig.Contribute(options);
        KafkaClusterContribution second = WolverineRig.Contribute(options);

        second.Clusters.Single().Topics.Should().BeEquivalentTo(first.Clusters.Single().Topics);
        second.Notes.Should().BeEquivalentTo(first.Notes);
    }
}
