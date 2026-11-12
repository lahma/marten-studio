using System.Globalization;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Wire;

using Microsoft.Extensions.Logging;

namespace KStudio.Core.Catalog;

/// <summary>
/// The wire's reads, single-flight and cached per key (decision 5.3), with every refusal logged once at Debug (decision 5.4). The
/// catalog and the window resolver both read through here, so a page and a scan share one answer.
/// </summary>
/// <remarks>
/// The cache key is <c>{cluster}|…</c> and nothing about the visitor: KStudio asks its cluster policy before it calls, and the engine
/// answers only what the broker allows this application's principal.
/// </remarks>
internal sealed class KafkaWireReads(
    KafkaWires wires,
    KafkaSnapshotCache cache,
    IKafkaEngineSettings settings,
    KStudioLoggers loggers,
    KafkaLogOnce once)
{
    /// <summary>How long configs, authorized operations and the cluster description are reused.</summary>
    public static readonly TimeSpan SlowTimeToLive = TimeSpan.FromMinutes(1);

    private TimeSpan Refresh => settings.CatalogTimeToLive;

    /// <summary>Metadata for all topics.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task<WireResult<WireMetadata>> MetadataAsync(string cluster, CancellationToken cancellationToken) =>
        cache.GetAsync(
            cluster + "|metadata",
            Refresh,
            async ct => Observe(cluster, WireCall.Metadata, await wires.For(cluster).GetMetadataAsync(ct).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>
    /// Metadata for all topics, read now: the cached answer is dropped first. A scan's partitions are fixed when its window is resolved,
    /// so the window must not be built on an answer up to a refresh interval old (a partition added in between would never be read).
    /// </summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task<WireResult<WireMetadata>> FreshMetadataAsync(string cluster, CancellationToken cancellationToken)
    {
        cache.Invalidate(cluster + "|metadata");
        return MetadataAsync(cluster, cancellationToken);
    }

    /// <summary>One kind of offset for some partitions of a topic.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="partitions">The partitions; none twice.</param>
    /// <param name="spec">What to look up.</param>
    /// <param name="isolation">The isolation level.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task<WireResult<IReadOnlyList<WireOffset>>> OffsetsAsync(
        string cluster,
        string topic,
        IReadOnlyList<WirePartition> partitions,
        WireOffsetSpec spec,
        WireIsolation isolation,
        CancellationToken cancellationToken) =>
        cache.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"{cluster}|offsets|{topic}|{SpecText(spec)}|{isolation}"),
            Refresh,
            async ct => Observe(cluster, WireCall.ListOffsets, await wires.For(cluster).ListOffsetsAsync(partitions, spec, isolation, ct).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>
    /// One kind of offset for the partitions of several topics, in one call (the home's page of spans): single-flight and cached for the same
    /// set of topics, so two callers within a refresh interval cost one read.
    /// </summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="partitions">The partitions of every topic; none twice.</param>
    /// <param name="spec">What to look up.</param>
    /// <param name="isolation">The isolation level.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task<WireResult<IReadOnlyList<WireOffset>>> OffsetsForTopicsAsync(
        string cluster,
        IReadOnlyList<WirePartition> partitions,
        WireOffsetSpec spec,
        WireIsolation isolation,
        CancellationToken cancellationToken)
    {
        string topics = string.Join(',', partitions.Select(static x => x.Topic).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        return cache.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"{cluster}|offsets|[{topics}]|{SpecText(spec)}|{isolation}"),
            Refresh,
            async ct => Observe(cluster, WireCall.ListOffsets, await wires.For(cluster).ListOffsetsAsync(partitions, spec, isolation, ct).ConfigureAwait(false)),
            cancellationToken);
    }

    /// <summary>A topic's configuration entries.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task<WireResult<WireTopicConfig>> ConfigAsync(string cluster, KafkaTopicName topic, CancellationToken cancellationToken) =>
        cache.GetAsync(
            $"{cluster}|config|{topic}",
            SlowTimeToLive,
            async ct => Observe(cluster, WireCall.DescribeConfigs, await wires.For(cluster).DescribeTopicConfigAsync(topic, ct).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>A topic with the operations this principal may perform on it.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task<WireResult<IReadOnlyList<WireTopicDescription>>> TopicOperationsAsync(
        string cluster, KafkaTopicName topic, CancellationToken cancellationToken) =>
        cache.GetAsync(
            $"{cluster}|ops|{topic}",
            SlowTimeToLive,
            async ct => Observe(cluster, WireCall.DescribeTopics, await wires.For(cluster).DescribeTopicsAsync([topic], ct).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>The cluster description.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task<WireResult<WireClusterDescription>> ClusterAsync(string cluster, CancellationToken cancellationToken) =>
        cache.GetAsync(
            cluster + "|cluster",
            SlowTimeToLive,
            async ct => Observe(cluster, WireCall.DescribeCluster, await wires.For(cluster).DescribeClusterAsync(ct).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>A declared group's committed offsets for a topic's partitions.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="groupId">The group.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="partitions">The topic's partitions.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task<WireResult<IReadOnlyList<WireGroupOffset>>> GroupOffsetsAsync(
        string cluster,
        string groupId,
        string topic,
        IReadOnlyList<WirePartition> partitions,
        CancellationToken cancellationToken) =>
        cache.GetAsync(
            $"{cluster}|group|{groupId}|{topic}",
            Refresh,
            async ct => Observe(cluster, WireCall.GroupOffsets, await wires.For(cluster).ListGroupOffsetsAsync(groupId, partitions, ct).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>A declared group's state.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="groupId">The group.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task<WireResult<WireGroupDescription>> GroupAsync(string cluster, string groupId, CancellationToken cancellationToken) =>
        cache.GetAsync(
            $"{cluster}|groupstate|{groupId}",
            Refresh,
            async ct => Observe(cluster, WireCall.DescribeGroup, await wires.For(cluster).DescribeGroupAsync(groupId, ct).ConfigureAwait(false)),
            cancellationToken);

    /// <summary>
    /// Logs what a wire result says, once per window per key: a refusal by an ACL at Debug (9652), an API the broker lacks at Debug
    /// (9653), and a cluster that cannot be reached at Warning the first time and Debug afterwards (9654). A refusal is an expected
    /// state and is never a warning.
    /// </summary>
    /// <typeparam name="T">What the call returned.</typeparam>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="call">The call.</param>
    /// <param name="result">The result, returned unchanged.</param>
    public WireResult<T> Observe<T>(string cluster, WireCall call, WireResult<T> result)
    {
        if (result.Error is { } error)
        {
            Observe(cluster, call, error);
        }

        return result;
    }

    /// <summary>Logs one error as <see cref="Observe{T}" /> does.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="call">The call.</param>
    /// <param name="error">The error.</param>
    public void Observe(string cluster, WireCall call, WireError error)
    {
        ILogger logger = loggers.Catalog;

        switch (error)
        {
            case WireError.NotAuthorized refusal:
                {
                    string resource = KafkaLogValue.Cap(refusal.ResourceName);
                    string operation = KafkaAccess.OperationName(refusal.Operation);
                    string resourceType = KafkaAccess.ResourceTypeName(refusal.ResourceType);
                    string callName = call.ToString();

                    if (once.ShouldLog($"{cluster}|{call}|{refusal.ResourceType}|{resource}"))
                    {
                        KStudioLog.AccessRefused(logger, cluster, callName, operation, resourceType, resource);
                    }

                    break;
                }

            case WireError.Unsupported unsupported:
                if (once.ShouldLog($"{cluster}|unsupported|{unsupported.Api}"))
                {
                    KStudioLog.ApiUnsupported(logger, cluster, unsupported.Api);
                }

                break;

            case WireError.Unavailable:
                {
                    // An authentication failure is logged once by the client's own error handler (9655).
                    LogLevel level = KafkaLogOnce.WarningOrDebug(once, $"{cluster}|unavailable");
                    string callName = call.ToString();
                    string code = error.Code;
                    KStudioLog.ClusterUnavailable(logger, level, cluster, callName, code);
                    break;
                }
        }
    }

    private static string SpecText(WireOffsetSpec spec) => spec.Kind switch
    {
        WireOffsetSpecKind.Earliest => "earliest",
        WireOffsetSpecKind.Latest => "latest",
        WireOffsetSpecKind.MaxTimestamp => "max",
        _ => "ts" + spec.TimestampMs.ToString(CultureInfo.InvariantCulture),
    };
}
