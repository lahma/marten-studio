using KStudio.Core.Clusters;
using KStudio.Core.Wire;

namespace KStudio.Core.Catalog;

/// <summary>
/// The catalog and overview reads (decision 5.1): clusters with no I/O, a cluster's topics, one topic's overview and the cluster's
/// description. Every field of a result is a <see cref="KafkaField{T}" />, a value or the error that withheld it, so one refusal never
/// blanks a page.
/// </summary>
internal interface IKafkaCatalog
{
    /// <summary>The registered clusters. No I/O.</summary>
    IReadOnlyList<KafkaClusterInfo> Clusters { get; }

    /// <summary>The topics this principal may describe, internal ones apart.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    Task<KafkaField<KafkaTopicList>> ListTopicsAsync(string cluster, CancellationToken cancellationToken);

    /// <summary>
    /// The offset spans of one page of topics (the home): partitions come from the cached metadata and the offsets from two cached reads,
    /// whatever the page holds. A topic whose offsets were refused is withheld alone.
    /// </summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="topics">At most <see cref="KafkaCatalog.MaxSpanTopics" /> topics (the home's page size); <see cref="ArgumentException" /> above.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    Task<KafkaField<IReadOnlyList<KafkaTopicSpan>>> ListTopicSpansAsync(string cluster, IReadOnlyList<KafkaTopicName> topics, CancellationToken cancellationToken);

    /// <summary>A topic's overview; the field is withheld with <c>NotFound</c> when metadata does not list the topic.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    Task<KafkaField<KafkaTopicOverview>> GetTopicOverviewAsync(string cluster, KafkaTopicName topic, CancellationToken cancellationToken);

    /// <summary>The cluster's id, controller, brokers and the operations this principal may perform on it.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    Task<KafkaClusterOverview> GetClusterOverviewAsync(string cluster, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class KafkaCatalog(
    KafkaClusterRegistry registry,
    KafkaWireReads reads,
    KafkaThroughputSamples throughput) : IKafkaCatalog
{
    /// <inheritdoc />
    public IReadOnlyList<KafkaClusterInfo> Clusters
    {
        get
        {
            IReadOnlyList<KafkaClusterDefinition> all = registry.Clusters;

            return
            [
                .. all.Select(x => new KafkaClusterInfo(
                    x.Key,
                    x.DisplayName,
                    x.Environment,
                    x.ConsumerGroups,
                    x.BaseKey,
                    x.TenantId,
                    x.Source,
                    x.Profile,
                    SameServers(x, all))),
            ];
        }
    }

    /// <summary>
    /// The entry keys of the clusters of another base cluster whose bootstrap servers are the same set (#72): configuration compared, nothing connected.
    /// The tenants of one broker-per-tenant cluster are not compared with each other or with their parent, which share a registration by design.
    /// </summary>
    private static string[] SameServers(KafkaClusterDefinition cluster, IReadOnlyList<KafkaClusterDefinition> all) =>
        cluster.ServersKey is null
            ? []
            : [.. all.Where(x => x.ServersKey == cluster.ServersKey && !string.Equals(x.BaseKey, cluster.BaseKey, StringComparison.Ordinal)).Select(static x => x.Key)];

    /// <inheritdoc />
    public async Task<KafkaField<KafkaTopicList>> ListTopicsAsync(string cluster, CancellationToken cancellationToken)
    {
        WireResult<WireMetadata> metadata = await reads.MetadataAsync(cluster, cancellationToken).ConfigureAwait(false);

        if (metadata.Error is { } error)
        {
            return KafkaField<KafkaTopicList>.Withheld(error);
        }

        List<KafkaTopicSummary> topics = [];
        List<KafkaTopicSummary> internalTopics = [];

        foreach (WireTopicInfo topic in metadata.Value.Topics.Where(static x => x.Error is null).OrderBy(static x => x.Name, StringComparer.Ordinal))
        {
            bool isInternal = topic.Name.StartsWith("__", StringComparison.Ordinal);
            (isInternal ? internalTopics : topics).Add(new KafkaTopicSummary(topic.Name, topic.Partitions.Count, isInternal));
        }

        return KafkaField<KafkaTopicList>.Of(new KafkaTopicList(topics, internalTopics));
    }

    /// <summary>The most topics one <see cref="IKafkaCatalog.ListTopicSpansAsync" /> call takes: the home's page size, so the cost of a page is bounded whatever the cluster's size.</summary>
    public const int MaxSpanTopics = 100;

    /// <inheritdoc />
    public async Task<KafkaField<IReadOnlyList<KafkaTopicSpan>>> ListTopicSpansAsync(
        string cluster, IReadOnlyList<KafkaTopicName> topics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);

        if (topics.Count > MaxSpanTopics)
        {
            throw new ArgumentException($"At most {MaxSpanTopics} topics can be asked for at once: one page of the home's list.", nameof(topics));
        }

        WireResult<WireMetadata> metadata = await reads.MetadataAsync(cluster, cancellationToken).ConfigureAwait(false);

        if (metadata.Error is { } error)
        {
            return KafkaField<IReadOnlyList<KafkaTopicSpan>>.Withheld(error);
        }

        Dictionary<string, WireTopicInfo> listed = metadata.Value.Topics
            .Where(static x => x.Error is null)
            .GroupBy(static x => x.Name, StringComparer.Ordinal)
            .ToDictionary(static x => x.Key, static x => x.First(), StringComparer.Ordinal);

        List<WirePartition> partitions = [];

        foreach (string name in topics.Select(static x => x.Value).Distinct(StringComparer.Ordinal))
        {
            if (listed.TryGetValue(name, out WireTopicInfo? info))
            {
                partitions.AddRange(info.Partitions.Select(x => new WirePartition(name, x.Partition)));
            }
        }

        WireResult<IReadOnlyList<WireOffset>> low = WireResult<IReadOnlyList<WireOffset>>.Ok([]);
        WireResult<IReadOnlyList<WireOffset>> high = low;

        if (partitions.Count > 0)
        {
            // Earliest and Latest are two calls (one spec per call, F6), each for every partition of the page, run together.
            Task<WireResult<IReadOnlyList<WireOffset>>> earliest = reads.OffsetsForTopicsAsync(cluster, partitions, WireOffsetSpec.Earliest, WireIsolation.ReadUncommitted, cancellationToken);
            Task<WireResult<IReadOnlyList<WireOffset>>> latest = reads.OffsetsForTopicsAsync(cluster, partitions, WireOffsetSpec.Latest, WireIsolation.ReadUncommitted, cancellationToken);
            await Task.WhenAll(earliest, latest).ConfigureAwait(false);
            low = earliest.Result;
            high = latest.Result;
        }

        List<KafkaTopicSpan> spans = [];

        foreach (KafkaTopicName topic in topics)
        {
            if (!listed.TryGetValue(topic.Value, out WireTopicInfo? info))
            {
                // Not listed: missing, or this principal may not describe it - the same thing to a visitor.
                spans.Add(new KafkaTopicSpan(topic.Value, KafkaField<long>.Withheld(new WireError.NotFound(KafkaResourceType.Topic, topic.Value))));
                continue;
            }

            long total = 0;
            WireError? refused = null;

            foreach (WirePartitionInfo partition in info.Partitions)
            {
                WirePartition key = new(topic.Value, partition.Partition);
                KafkaField<long> start = Offset(low, key);
                KafkaField<long> end = Offset(high, key);

                if ((start.Error ?? end.Error) is { } partitionError)
                {
                    refused = partitionError;
                    break;
                }

                total += Math.Max(0, end.Value - start.Value);
            }

            spans.Add(new KafkaTopicSpan(topic.Value, refused is null ? KafkaField<long>.Of(total) : KafkaField<long>.Withheld(refused)));
        }

        return KafkaField<IReadOnlyList<KafkaTopicSpan>>.Of(spans);
    }

    /// <inheritdoc />
    public async Task<KafkaField<KafkaTopicOverview>> GetTopicOverviewAsync(
        string cluster, KafkaTopicName topic, CancellationToken cancellationToken)
    {
        WireResult<WireMetadata> metadata = await reads.MetadataAsync(cluster, cancellationToken).ConfigureAwait(false);

        if (metadata.Error is { } error)
        {
            return KafkaField<KafkaTopicOverview>.Withheld(error);
        }

        WireTopicInfo? info = metadata.Value.Topics.FirstOrDefault(x => x.Name == topic.Value && x.Error is null);

        if (info is null)
        {
            // A topic metadata does not list is missing, or this principal may not describe it: the same thing to a visitor.
            return KafkaField<KafkaTopicOverview>.Withheld(new WireError.NotFound(KafkaResourceType.Topic, topic.Value));
        }

        List<WirePartition> partitions = [.. info.Partitions.Select(x => new WirePartition(topic.Value, x.Partition))];
        KafkaClusterDefinition definition = registry.Get(cluster);

        // The five list-offsets calls of one overview run together, each batching every partition of the topic (decision 5.2).
        Task<WireResult<IReadOnlyList<WireOffset>>> earliest = Offsets(WireOffsetSpec.Earliest, WireIsolation.ReadUncommitted);
        Task<WireResult<IReadOnlyList<WireOffset>>> latest = Offsets(WireOffsetSpec.Latest, WireIsolation.ReadUncommitted);
        Task<WireResult<IReadOnlyList<WireOffset>>> stable = Offsets(WireOffsetSpec.Latest, WireIsolation.ReadCommitted);
        Task<WireResult<IReadOnlyList<WireOffset>>> first = Offsets(WireOffsetSpec.ForTimestamp(0), WireIsolation.ReadUncommitted);
        Task<WireResult<IReadOnlyList<WireOffset>>> highest = Offsets(WireOffsetSpec.MaxTimestamp, WireIsolation.ReadUncommitted);
        Task<WireResult<IReadOnlyList<WireTopicDescription>>> operations = reads.TopicOperationsAsync(cluster, topic, cancellationToken);
        Task<WireResult<WireTopicConfig>> config = reads.ConfigAsync(cluster, topic, cancellationToken);
        // The groups the host declared for the cluster and the one a source paired with this topic's listener: never every group of the cluster (#73).
        Task<KafkaGroupOverview>[] groups = [.. definition.GroupsOf(topic.Value).Select(x => GroupAsync(cluster, x.Group, x.ClusterWide, topic.Value, partitions, cancellationToken))];

        await Task.WhenAll(earliest, latest, stable, first, highest, operations, config).ConfigureAwait(false);
        KafkaGroupOverview[] groupOverviews = await Task.WhenAll(groups).ConfigureAwait(false);

        List<KafkaPartitionOverview> overviews = [];

        foreach (WirePartitionInfo partition in info.Partitions)
        {
            WirePartition key = new(topic.Value, partition.Partition);

            overviews.Add(new KafkaPartitionOverview(
                partition.Partition,
                partition.Leader,
                partition.Replicas,
                partition.InSyncReplicas,
                Offset(earliest.Result, key),
                Offset(latest.Result, key),
                Offset(stable.Result, key),
                Timestamp(first.Result, key),
                Timestamp(highest.Result, key)));
        }

        long? span = overviews.Any(static x => x.OffsetSpan is not null) ? overviews.Sum(static x => x.OffsetSpan ?? 0) : null;
        bool? open = overviews.Any(static x => x.OpenTransaction is not null) ? overviews.Any(static x => x.OpenTransaction == true) : null;

        if (overviews.All(static x => x.HighWatermark.IsAvailable))
        {
            throughput.Record(cluster, topic.Value, overviews.Sum(static x => x.HighWatermark.Value));
        }

        return KafkaField<KafkaTopicOverview>.Of(new KafkaTopicOverview(
            cluster,
            topic.Value,
            topic.Value.StartsWith("__", StringComparison.Ordinal),
            overviews,
            Operations(operations.Result),
            config.Result.Error is { } configError
                ? KafkaField<KafkaTopicConfigView>.Withheld(configError)
                : KafkaField<KafkaTopicConfigView>.Of(KafkaTopicConfigView.From(config.Result.Value)),
            span,
            open,
            throughput.Rate(cluster, topic.Value),
            groupOverviews));

        Task<WireResult<IReadOnlyList<WireOffset>>> Offsets(WireOffsetSpec spec, WireIsolation isolation) =>
            reads.OffsetsAsync(cluster, topic.Value, partitions, spec, isolation, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KafkaClusterOverview> GetClusterOverviewAsync(string cluster, CancellationToken cancellationToken)
    {
        WireResult<WireClusterDescription> description = await reads.ClusterAsync(cluster, cancellationToken).ConfigureAwait(false);

        return new KafkaClusterOverview(
            cluster,
            description.Error is { } error
                ? KafkaField<WireClusterDescription>.Withheld(error)
                : KafkaField<WireClusterDescription>.Of(description.Value));
    }

    private async Task<KafkaGroupOverview> GroupAsync(
        string cluster, string group, bool clusterWide, string topic, IReadOnlyList<WirePartition> partitions, CancellationToken cancellationToken)
    {
        Task<WireResult<WireGroupDescription>> state = reads.GroupAsync(cluster, group, cancellationToken);
        Task<WireResult<IReadOnlyList<WireGroupOffset>>> committed = reads.GroupOffsetsAsync(cluster, group, topic, partitions, cancellationToken);
        Task<WireResult<IReadOnlyList<WireOffset>>> high = reads.OffsetsAsync(
            cluster, topic, partitions, WireOffsetSpec.Latest, WireIsolation.ReadUncommitted, cancellationToken);

        await Task.WhenAll(state, committed, high).ConfigureAwait(false);

        KafkaField<WireGroupDescription> stateField = state.Result.Error is { } stateError
            ? KafkaField<WireGroupDescription>.Withheld(stateError)
            : KafkaField<WireGroupDescription>.Of(state.Result.Value);

        if (committed.Result.Error is { } error)
        {
            return new KafkaGroupOverview(group, stateField, KafkaField<IReadOnlyList<KafkaGroupLag>>.Withheld(error), clusterWide);
        }

        List<KafkaGroupLag> lag = [];

        foreach (WireGroupOffset offset in committed.Result.Value)
        {
            if (offset.Error is { } offsetError)
            {
                lag.Add(new KafkaGroupLag(offset.Partition, KafkaField<long?>.Withheld(offsetError), null));
                continue;
            }

            KafkaField<long> highWatermark = Offset(high.Result, offset.Partition);
            long? behind = highWatermark.IsAvailable && offset.Committed is { } position ? Math.Max(0, highWatermark.Value - position) : null;
            lag.Add(new KafkaGroupLag(offset.Partition, KafkaField<long?>.Of(offset.Committed), behind));
        }

        return new KafkaGroupOverview(group, stateField, KafkaField<IReadOnlyList<KafkaGroupLag>>.Of(lag), clusterWide);
    }

    private static KafkaField<long> Offset(WireResult<IReadOnlyList<WireOffset>> result, WirePartition partition)
    {
        if (result.Error is { } error)
        {
            return KafkaField<long>.Withheld(error);
        }

        WireOffset? offset = result.Value.FirstOrDefault(x => x.Partition == partition);

        if (offset is null)
        {
            return KafkaField<long>.Withheld(new WireError.NotFound(KafkaResourceType.Topic, partition.Topic));
        }

        return offset.Error is { } offsetError ? KafkaField<long>.Withheld(offsetError) : KafkaField<long>.Of(Math.Max(0, offset.Offset));
    }

    private static KafkaField<KafkaTimestampAt?> Timestamp(WireResult<IReadOnlyList<WireOffset>> result, WirePartition partition)
    {
        if (result.Error is { } error)
        {
            return KafkaField<KafkaTimestampAt?>.Withheld(error);
        }

        WireOffset? offset = result.Value.FirstOrDefault(x => x.Partition == partition);

        if (offset is null)
        {
            return KafkaField<KafkaTimestampAt?>.Withheld(new WireError.NotFound(KafkaResourceType.Topic, partition.Topic));
        }

        if (offset.Error is { } offsetError)
        {
            return KafkaField<KafkaTimestampAt?>.Withheld(offsetError);
        }

        // An empty partition answers -1/-1 with no error: no record, shown as "empty", never as 1970.
        return KafkaField<KafkaTimestampAt?>.Of(
            offset.Found && offset.TimestampMs is { } at
                ? new KafkaTimestampAt(offset.Offset, DateTimeOffset.FromUnixTimeMilliseconds(at))
                : null);
    }

    private static KafkaField<IReadOnlyList<string>> Operations(WireResult<IReadOnlyList<WireTopicDescription>> result)
    {
        if (result.Error is { } error)
        {
            return KafkaField<IReadOnlyList<string>>.Withheld(error);
        }

        WireTopicDescription? description = result.Value.Count > 0 ? result.Value[0] : null;

        if (description is null)
        {
            return KafkaField<IReadOnlyList<string>>.Of([]);
        }

        return description.Error is { } descriptionError
            ? KafkaField<IReadOnlyList<string>>.Withheld(descriptionError)
            : KafkaField<IReadOnlyList<string>>.Of(description.AuthorizedOperations);
    }
}
