using Confluent.Kafka;

using KStudio.Core.Clusters;

namespace KStudio.Tests.Support;

/// <summary>A cluster source that answers with what a test gave it, as KStudio.Wolverine would.</summary>
internal sealed class FakeClusterSource(KafkaClusterContribution contribution, string name = "Wolverine") : IKafkaClusterSource
{
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public KafkaClusterContribution Contribute() => contribution;

    /// <summary>One contributed cluster over a made-up broker.</summary>
    /// <param name="key">The cluster key, not the entry key.</param>
    /// <param name="tenant">The tenant, or <see langword="null" />.</param>
    /// <param name="topics">The topics the source found.</param>
    public static KafkaContributedCluster Cluster(string key, string? tenant = null, params KafkaDiscoveredTopic[] topics)
    {
        KafkaClusterBuilder builder = new();
        builder.DisplayName("Wolverine");
        return new KafkaContributedCluster(key, tenant, new ConsumerConfig { BootstrapServers = "broker:9092" }, new AdminClientConfig { BootstrapServers = "broker:9092" }, builder, topics);
    }

    /// <summary>A topic a source found, with the facts the topic overview shows.</summary>
    /// <param name="name">The topic.</param>
    /// <param name="role">How the application uses it.</param>
    public static KafkaDiscoveredTopic Topic(string name, KafkaTopicRole role = KafkaTopicRole.Listener) =>
        new(name, role, ["Orders.PlaceOrder", "namespace Orders.Events"], "SystemTextJsonSerializer", "orders-app", false, null, "Wolverine", "Wolverine listener");
}
