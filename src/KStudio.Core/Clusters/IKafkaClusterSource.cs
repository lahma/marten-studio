using Confluent.Kafka;

namespace KStudio.Core.Clusters;

/// <summary>
/// A source of clusters other than the host's own <c>AddCluster</c> calls (D43): KStudio.Wolverine reads a Wolverine application's Kafka
/// transports through one. The seam is internal and granted by <c>InternalsVisibleTo</c> to that one assembly, which checks its own build
/// against KStudio.Core's before it uses any of it (D30's pattern).
/// </summary>
/// <remarks>
/// It exists because the public registration is an options delegate that runs at host start with no service provider, possibly before
/// the thing a source reads (Wolverine's runtime) has connected. The registry runs every source once, when it is built, which is the first
/// use of any page or link that needs a cluster.
/// </remarks>
internal interface IKafkaClusterSource
{
    /// <summary>The source's name for notes and logs, for example <c>Wolverine</c>.</summary>
    string Name { get; }

    /// <summary>Called once, when the registry is built. Must not throw: every failure is a note.</summary>
    KafkaClusterContribution Contribute();
}

/// <summary>What a source contributed: clusters, and what it could not list and why.</summary>
/// <param name="Clusters">The clusters, in the order the source found them. A tenant's cluster comes after the cluster it belongs to.</param>
/// <param name="Notes">What was not listed.</param>
/// <param name="Incomplete">
/// Whether the answer will be fuller later: the registry then asks the source <em>once more</em>, on the first access after
/// <paramref name="CompleteWhen" /> has fired, and takes from that answer only the tenant clusters it adds. It never changes or removes an entry
/// already listed, and never asks a third time.
/// </param>
/// <param name="CompleteWhen">
/// The token that fires when the source can complete its answer (Wolverine's <c>ApplicationStarted</c>). The engine cannot see the host's
/// lifetime, so the source hands it over.
/// </param>
/// <param name="IncompleteNote">The sentence the Settings page shows while the answer is incomplete.</param>
internal sealed record KafkaClusterContribution(
    IReadOnlyList<KafkaContributedCluster> Clusters,
    IReadOnlyList<KafkaSourceNote> Notes,
    bool Incomplete = false,
    string? IncompleteNote = null,
    CancellationToken CompleteWhen = default);

/// <summary>
/// One thing a source did not list. <see cref="Text" /> is for the Settings page and may name an item; <see cref="What" /> and
/// <see cref="Reason" /> are for the log (event 9677) and name no tenant, topic or value.
/// </summary>
/// <param name="What">What was not listed, in general terms: <c>a cluster</c>, <c>tenant clusters</c>, <c>a topic</c>.</param>
/// <param name="Reason">Why, in general terms.</param>
/// <param name="Text">The sentence the Settings page shows.</param>
internal sealed record KafkaSourceNote(string What, string Reason, string Text);

/// <summary>
/// One cluster a source found. The configurations are cloned by the registry as <c>AddCluster</c> clones the host's, and never written.
/// </summary>
/// <param name="Key">The cluster's key (not the entry key); a tenant's cluster has its parent's key and a <paramref name="TenantId" />.</param>
/// <param name="TenantId">The tenant this entry is a cluster of, or <see langword="null" />.</param>
/// <param name="ConsumerConfig">The consumer-side configuration.</param>
/// <param name="AdminConfig">The admin-side configuration.</param>
/// <param name="Builder">What is said about the cluster beyond its configuration; the host's hook has already been applied to it.</param>
/// <param name="Topics">The topics the source found.</param>
internal sealed record KafkaContributedCluster(
    string Key,
    string? TenantId,
    ConsumerConfig ConsumerConfig,
    AdminClientConfig AdminConfig,
    KafkaClusterBuilder Builder,
    IReadOnlyList<KafkaDiscoveredTopic> Topics);

/// <summary>What a source found out about one topic. It never replaces what the host declared (decision 5.6).</summary>
/// <param name="Name">The topic's name.</param>
/// <param name="Role">How the source uses it.</param>
/// <param name="MessageTypes">The message types it carries or is sent, as text.</param>
/// <param name="Serializer">The serializer's type name, when the source knows one.</param>
/// <param name="ConsumerGroup">The group the source's listener uses, when it has a listener.</param>
/// <param name="CloudEvents">Whether the source writes the topic's values as structured CloudEvents.</param>
/// <param name="Partitioner">The partitioner the source's producers use, as a claim the engine verifies before it prunes.</param>
/// <param name="Source">The source's name.</param>
/// <param name="Description">A sentence about the topic, or <see langword="null" />.</param>
internal sealed record KafkaDiscoveredTopic(
    string Name,
    KafkaTopicRole Role,
    IReadOnlyList<string> MessageTypes,
    string? Serializer,
    string? ConsumerGroup,
    bool CloudEvents,
    KafkaPartitioner? Partitioner,
    string Source,
    string? Description);

/// <summary>How a source uses a topic.</summary>
internal enum KafkaTopicRole
{
    /// <summary>The source listens to it.</summary>
    Listener,

    /// <summary>The source sends to it.</summary>
    Sender,

    /// <summary>Both.</summary>
    ListenerAndSender,

    /// <summary>The source's dead-letter topic.</summary>
    DeadLetter,
}
