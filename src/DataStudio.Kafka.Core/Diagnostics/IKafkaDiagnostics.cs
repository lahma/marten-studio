namespace DataStudio.Kafka;

/// <summary>
/// What the Kafka engine reads about a cluster, a topic and a consumer group, for the host's own code (AGENTS.md D50): the same reads the Kafka
/// module's pages make, through the same pooled clients and single-flight caches, returned as results and never served by the studio.
/// Registered by <c>AddKafka()</c>; resolve it from the container.
/// </summary>
/// <remarks>
/// The API answers host code, which already holds the cluster's credentials, so it asks none of the visitor's policies or capabilities (D35):
/// it reads what the application's principal may read, nothing more. A read never writes (D37): no group is joined, no offset committed, no
/// topic created. A refusal, an unknown topic or a cluster that does not answer is a <see cref="KafkaReadProblem" /> on the result, never an
/// exception; only an unknown cluster key and a canceled call throw. Every answer may be up to the shell's refresh interval old.
/// </remarks>
public interface IKafkaDiagnostics
{
    /// <summary>The keys of the registered clusters, in registration order. No I/O beyond running the cluster sources once (D43).</summary>
    IReadOnlyList<string> Clusters { get; }

    /// <summary>Whether the cluster answers, with how many brokers, and whether it lists each of <paramref name="topics" />.</summary>
    /// <param name="cluster">The cluster's key, one of <see cref="Clusters" />.</param>
    /// <param name="topics">The topics to look for in the cluster's metadata; none to ask only whether the cluster answers.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    /// <exception cref="ArgumentException"><paramref name="cluster" /> is not a registered cluster.</exception>
    Task<KafkaClusterStatus> GetClusterAsync(string cluster, IReadOnlyList<string>? topics = null, CancellationToken cancellationToken = default);

    /// <summary>A topic's partitions with their first and end offsets, and its retention and cleanup policy.</summary>
    /// <param name="cluster">The cluster's key, one of <see cref="Clusters" />.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    /// <exception cref="ArgumentException"><paramref name="cluster" /> is not a registered cluster, or <paramref name="topic" /> is blank.</exception>
    Task<KafkaTopicStatus> GetTopicAsync(string cluster, string topic, CancellationToken cancellationToken = default);

    /// <summary>
    /// A consumer group's state and members with their assignments, and its committed offset, end offset and lag on every partition a member
    /// is assigned or the group has committed to on <paramref name="topics" />.
    /// </summary>
    /// <param name="cluster">The cluster's key, one of <see cref="Clusters" />.</param>
    /// <param name="groupId">The group.</param>
    /// <param name="topics">
    /// The topics whose committed offsets are read, besides the topics the members are assigned. The engine never lists every group or every
    /// committed offset of a cluster (D37), so a group with no member shows its position only on the topics named here.
    /// </param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    /// <exception cref="ArgumentException"><paramref name="cluster" /> is not a registered cluster, or <paramref name="groupId" /> is blank.</exception>
    Task<KafkaGroupStatus> GetGroupAsync(string cluster, string groupId, IReadOnlyList<string> topics, CancellationToken cancellationToken = default);
}
