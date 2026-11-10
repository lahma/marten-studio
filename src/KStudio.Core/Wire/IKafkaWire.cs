using KStudio.Core.Clusters;

namespace KStudio.Core.Wire;

/// <summary>
/// Every broker call KStudio makes (AGENTS.md D36), one per cluster. It exists so that the fast suite can run the engine on a
/// hand-written fake broker, not for a second client; no Confluent type crosses it and errors are values. It has no produce,
/// commit, delete or alter method, so neither the Confluent driver nor the fake can be asked for one (D37).
/// </summary>
internal interface IKafkaWire
{
    /// <summary>The cluster's key.</summary>
    string ClusterKey { get; }

    /// <summary>Every topic this principal may DESCRIBE (metadata for all topics; the broker omits the others).</summary>
    /// <param name="cancellationToken">Abandons the wait, not the call.</param>
    Task<WireResult<WireMetadata>> GetMetadataAsync(CancellationToken cancellationToken);

    /// <summary>Partitions, replicas, ISR, whether a topic is internal and the authorized operations of the named topics.</summary>
    /// <param name="topics">The topics.</param>
    /// <param name="cancellationToken">Abandons the wait, not the call.</param>
    Task<WireResult<IReadOnlyList<WireTopicDescription>>> DescribeTopicsAsync(
        IReadOnlyList<KafkaTopicName> topics, CancellationToken cancellationToken);

    /// <summary>The cluster id, controller, brokers and the operations this principal may perform on the cluster.</summary>
    /// <param name="cancellationToken">Abandons the wait, not the call.</param>
    Task<WireResult<WireClusterDescription>> DescribeClusterAsync(CancellationToken cancellationToken);

    /// <summary>One spec kind per call; every partition at most once (F6).</summary>
    /// <param name="partitions">The partitions.</param>
    /// <param name="spec">What to look up.</param>
    /// <param name="isolation">The isolation level: it decides whether <c>Latest</c> is the high watermark or the last stable offset.</param>
    /// <param name="cancellationToken">Abandons the wait, not the call.</param>
    Task<WireResult<IReadOnlyList<WireOffset>>> ListOffsetsAsync(
        IReadOnlyList<WirePartition> partitions,
        WireOffsetSpec spec,
        WireIsolation isolation,
        CancellationToken cancellationToken);

    /// <summary>The entries of a topic's configuration the engine reads.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="cancellationToken">Abandons the wait, not the call.</param>
    Task<WireResult<WireTopicConfig>> DescribeTopicConfigAsync(KafkaTopicName topic, CancellationToken cancellationToken);

    /// <summary>A declared group's committed offsets for some partitions.</summary>
    /// <param name="groupId">The group.</param>
    /// <param name="partitions">The partitions.</param>
    /// <param name="cancellationToken">Abandons the wait, not the call.</param>
    Task<WireResult<IReadOnlyList<WireGroupOffset>>> ListGroupOffsetsAsync(
        string groupId, IReadOnlyList<WirePartition> partitions, CancellationToken cancellationToken);

    /// <summary>A declared group's state and member count.</summary>
    /// <param name="groupId">The group.</param>
    /// <param name="cancellationToken">Abandons the wait, not the call.</param>
    Task<WireResult<WireGroupDescription>> DescribeGroupAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>Builds one consumer. Every member of the reader is called from one thread, the scan's.</summary>
    /// <param name="options">The scan's id, isolation and limits.</param>
    IWireScanReader OpenReader(WireReaderOptions options);
}

/// <summary>
/// One consumer, used from one thread (the scan's). It never subscribes, commits or stores an offset (D37).
/// </summary>
internal interface IWireScanReader : IDisposable
{
    /// <summary>Starts reading the partitions at explicit offsets (F10): never a stored or committed position.</summary>
    /// <param name="start">The partitions with their first offset each.</param>
    void Assign(IReadOnlyList<WirePartitionOffset> start);

    /// <summary>Stops delivering records of partitions that are finished.</summary>
    /// <param name="partitions">The partitions.</param>
    void Pause(IReadOnlyList<WirePartition> partitions);

    /// <summary>Resumes delivery.</summary>
    /// <param name="partitions">The partitions.</param>
    void Resume(IReadOnlyList<WirePartition> partitions);

    /// <summary>
    /// Moves one partition to a new position after an out-of-range failure. It is an unassign followed by an assign, never a seek:
    /// a seek on a partition in that state throws (SPIKE S3d).
    /// </summary>
    /// <param name="position">The partition and the offset to continue at.</param>
    void Reposition(WirePartitionOffset position);

    /// <summary>Waits at most <paramref name="timeout" /> for the next outcome.</summary>
    /// <param name="timeout">How long to wait.</param>
    WireReadOutcome Read(TimeSpan timeout);

    /// <summary>Closes the consumer; it commits nothing. Then disposes.</summary>
    void Close();
}

/// <summary>Builds the wire of a cluster. The fast suite's implementation is the fake broker.</summary>
internal interface IKafkaWireFactory
{
    /// <summary>Creates the wire of one cluster; it connects to nothing until a call is made.</summary>
    /// <param name="cluster">The cluster.</param>
    IKafkaWire Create(KafkaClusterDefinition cluster);
}
