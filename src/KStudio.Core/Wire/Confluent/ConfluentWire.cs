using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;

namespace KStudio.Core.Wire.Confluent;

/// <summary>
/// <see cref="IKafkaWire" /> over Confluent.Kafka (decisions 3 and 4.1): the admin calls run on the cluster's pooled admin client, a
/// caller's cancellation abandons the wait and never the call, and every Confluent exception is mapped to a value here.
/// </summary>
internal sealed class ConfluentWire(
    KafkaClusterDefinition cluster,
    KafkaAdminPool<IAdminClient> pool,
    KafkaEngineOptions options,
    ConfluentClientEvents events,
    KStudioLoggers loggers) : IKafkaWire
{
    /// <inheritdoc />
    public string ClusterKey => cluster.Key;

    /// <inheritdoc />
    public Task<WireResult<WireMetadata>> GetMetadataAsync(CancellationToken cancellationToken) =>
        Admin(
            WireCall.Metadata,
            ClusterKey,
            async (client, _) =>
            {
                // GetMetadata is synchronous in Confluent: it runs on the thread pool, bounded by the request timeout.
                Metadata metadata = await Task.Run(() => client.GetMetadata(options.RequestTimeout), CancellationToken.None).ConfigureAwait(false);

                WireTopicInfo[] topics =
                [
                    .. metadata.Topics.Select(topic => new WireTopicInfo(
                        topic.Topic,
                        [.. topic.Partitions.Select(static x => new WirePartitionInfo(x.PartitionId, x.Leader, x.Replicas, x.InSyncReplicas))],
                        topic.Error.IsError ? ConfluentErrors.Map(topic.Error, WireCall.Metadata, topic.Topic) : null)),
                ];

                return WireResult<WireMetadata>.Ok(new WireMetadata(topics));
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<WireResult<IReadOnlyList<WireTopicDescription>>> DescribeTopicsAsync(
        IReadOnlyList<KafkaTopicName> topics, CancellationToken cancellationToken) =>
        Admin<IReadOnlyList<WireTopicDescription>>(
            WireCall.DescribeTopics,
            topics.Count == 1 ? topics[0].Value : ClusterKey,
            async (client, resource) =>
            {
                DescribeTopicsOptions describeOptions = new() { IncludeAuthorizedOperations = true, RequestTimeout = options.RequestTimeout };
                IEnumerable<TopicDescription> descriptions;

                try
                {
                    DescribeTopicsResult result = await client
                        .DescribeTopicsAsync(TopicCollection.OfTopicNames(topics.Select(static x => x.Value)), describeOptions)
                        .ConfigureAwait(false);
                    descriptions = result.TopicDescriptions;
                }
                catch (DescribeTopicsException exception) when (exception.Results?.TopicDescriptions is not null)
                {
                    descriptions = exception.Results.TopicDescriptions;
                }

                return WireResult<IReadOnlyList<WireTopicDescription>>.Ok([.. descriptions.Select(Describe)]);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<WireResult<WireClusterDescription>> DescribeClusterAsync(CancellationToken cancellationToken) =>
        Admin(
            WireCall.DescribeCluster,
            ClusterKey,
            async (client, _) =>
            {
                DescribeClusterResult result = await client
                    .DescribeClusterAsync(new DescribeClusterOptions { IncludeAuthorizedOperations = true, RequestTimeout = options.RequestTimeout })
                    .ConfigureAwait(false);

                return WireResult<WireClusterDescription>.Ok(new WireClusterDescription(
                    result.ClusterId,
                    result.Controller?.Id,
                    [.. (result.Nodes ?? []).Select(static x => new WireNode(x.Id, x.Host, x.Port))],
                    OperationNames(result.AuthorizedOperations)));
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<WireResult<IReadOnlyList<WireOffset>>> ListOffsetsAsync(
        IReadOnlyList<WirePartition> partitions,
        WireOffsetSpec spec,
        WireIsolation isolation,
        CancellationToken cancellationToken)
    {
        // One partition at most once per call (F6): librdkafka refuses duplicates, and the engine never sends one.
        if (partitions.Distinct().Count() != partitions.Count)
        {
            return WireResult<IReadOnlyList<WireOffset>>.Fail(new WireError.Failed("Local_InvalidArg", IsFatal: false));
        }

        if (partitions.Count == 0)
        {
            return WireResult<IReadOnlyList<WireOffset>>.Ok([]);
        }

        return await Admin<IReadOnlyList<WireOffset>>(
            WireCall.ListOffsets,
            ClusterKey,
            async (client, _) =>
            {
                OffsetSpec offsetSpec = spec.Kind switch
                {
                    WireOffsetSpecKind.Earliest => OffsetSpec.Earliest(),
                    WireOffsetSpecKind.Latest => OffsetSpec.Latest(),
                    WireOffsetSpecKind.MaxTimestamp => OffsetSpec.MaxTimestamp(),
                    _ => OffsetSpec.ForTimestamp(spec.TimestampMs),
                };

                List<TopicPartitionOffsetSpec> requests =
                [
                    .. partitions.Select(x => new TopicPartitionOffsetSpec
                    {
                        TopicPartition = new TopicPartition(x.Topic, x.Partition),
                        OffsetSpec = offsetSpec,
                    }),
                ];

                ListOffsetsOptions listOptions = new()
                {
                    IsolationLevel = isolation == WireIsolation.ReadCommitted ? IsolationLevel.ReadCommitted : IsolationLevel.ReadUncommitted,
                    RequestTimeout = options.RequestTimeout,
                };

                IEnumerable<ListOffsetsResultInfo> infos;

                try
                {
                    ListOffsetsResult result = await client.ListOffsetsAsync(requests, listOptions).ConfigureAwait(false);
                    infos = result.ResultInfos;
                }
                catch (ListOffsetsException exception) when (exception.Result?.ResultInfos is not null)
                {
                    // Per-partition failures arrive with the infos; each becomes that partition's error.
                    infos = exception.Result.ResultInfos;
                }

                return WireResult<IReadOnlyList<WireOffset>>.Ok([.. infos.Select(ToOffset)]);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<WireResult<WireTopicConfig>> DescribeTopicConfigAsync(KafkaTopicName topic, CancellationToken cancellationToken) =>
        Admin(
            WireCall.DescribeConfigs,
            topic.Value,
            async (client, resource) =>
            {
                try
                {
                    List<DescribeConfigsResult> results = await client
                        .DescribeConfigsAsync(
                            [new ConfigResource { Type = ResourceType.Topic, Name = topic.Value }],
                            new DescribeConfigsOptions { RequestTimeout = options.RequestTimeout })
                        .ConfigureAwait(false);

                    Dictionary<string, string?> entries = new(StringComparer.Ordinal);

                    foreach (string key in WireTopicConfig.KnownKeys)
                    {
                        if (results[0].Entries.TryGetValue(key, out ConfigEntryResult? entry))
                        {
                            entries[key] = entry.IsSensitive ? null : entry.Value;
                        }
                    }

                    return WireResult<WireTopicConfig>.Ok(new WireTopicConfig(topic.Value, entries));
                }
                catch (DescribeConfigsException exception) when (exception.Results is { Count: > 0 })
                {
                    Error error = exception.Results.Select(static x => x.Error).FirstOrDefault(static x => x is { IsError: true }) ?? exception.Error;
                    return WireResult<WireTopicConfig>.Fail(ConfluentErrors.Map(error, WireCall.DescribeConfigs, resource));
                }
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<WireResult<IReadOnlyList<WireGroupOffset>>> ListGroupOffsetsAsync(
        string groupId, IReadOnlyList<WirePartition> partitions, CancellationToken cancellationToken) =>
        Admin<IReadOnlyList<WireGroupOffset>>(
            WireCall.GroupOffsets,
            groupId,
            async (client, resource) =>
            {
                try
                {
                    List<ListConsumerGroupOffsetsResult> results = await client
                        .ListConsumerGroupOffsetsAsync(
                            [new ConsumerGroupTopicPartitions(groupId, [.. partitions.Select(static x => new TopicPartition(x.Topic, x.Partition))])],
                            new ListConsumerGroupOffsetsOptions { RequestTimeout = options.RequestTimeout })
                        .ConfigureAwait(false);

                    return WireResult<IReadOnlyList<WireGroupOffset>>.Ok(
                    [
                        .. results.SelectMany(static x => x.Partitions).Select(item => new WireGroupOffset(
                            new WirePartition(item.Topic, item.Partition.Value),
                            item.Offset.Value >= 0 ? item.Offset.Value : null,
                            item.Error.IsError ? ConfluentErrors.Map(item.Error, WireCall.GroupOffsets, resource) : null)),
                    ]);
                }
                catch (ListConsumerGroupOffsetsException exception) when (exception.Results is { Count: > 0 })
                {
                    Error error = exception.Results.Select(static x => x.Error).FirstOrDefault(static x => x is { IsError: true }) ?? exception.Error;
                    return WireResult<IReadOnlyList<WireGroupOffset>>.Fail(ConfluentErrors.Map(error, WireCall.GroupOffsets, resource));
                }
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<WireResult<WireGroupDescription>> DescribeGroupAsync(string groupId, CancellationToken cancellationToken) =>
        Admin(
            WireCall.DescribeGroup,
            groupId,
            async (client, resource) =>
            {
                IEnumerable<ConsumerGroupDescription> descriptions;

                try
                {
                    DescribeConsumerGroupsResult result = await client
                        .DescribeConsumerGroupsAsync([groupId], new DescribeConsumerGroupsOptions { RequestTimeout = options.RequestTimeout })
                        .ConfigureAwait(false);
                    descriptions = result.ConsumerGroupDescriptions;
                }
                catch (DescribeConsumerGroupsException exception) when (exception.Results?.ConsumerGroupDescriptions is not null)
                {
                    descriptions = exception.Results.ConsumerGroupDescriptions;
                }

                ConsumerGroupDescription? description = descriptions.FirstOrDefault();

                if (description is null)
                {
                    return WireResult<WireGroupDescription>.Fail(new WireError.NotFound(KafkaResourceType.Group, groupId));
                }

                if (description.Error is { IsError: true } error)
                {
                    return WireResult<WireGroupDescription>.Fail(ConfluentErrors.Map(error, WireCall.DescribeGroup, resource));
                }

                return WireResult<WireGroupDescription>.Ok(new WireGroupDescription(
                    description.GroupId,
                    description.State.ToString(),
                    description.Members?.Count ?? 0,
                    description.PartitionAssignor));
            },
            cancellationToken);

    /// <inheritdoc />
    public IWireScanReader OpenReader(WireReaderOptions readerOptions) =>
        new ConfluentScanReader(cluster, options, readerOptions, events);

    private static WireTopicDescription Describe(TopicDescription description) => new(
        description.Name,
        description.IsInternal,
        [.. (description.Partitions ?? []).Select(static x => new WirePartitionInfo(
            x.Partition,
            x.Leader?.Id ?? -1,
            [.. (x.Replicas ?? []).Select(static n => n.Id)],
            [.. (x.ISR ?? []).Select(static n => n.Id)]))],
        OperationNames(description.AuthorizedOperations),
        description.Error is { IsError: true } error ? ConfluentErrors.Map(error, WireCall.DescribeTopics, description.Name) : null);

    private static string[] OperationNames(IEnumerable<AclOperation>? operations) =>
        [.. (operations ?? []).Select(static x => x.ToString())];

    private static WireOffset ToOffset(ListOffsetsResultInfo info)
    {
        TopicPartitionOffsetError item = info.TopicPartitionOffsetError;
        WirePartition partition = new(item.Topic, item.Partition.Value);

        if (item.Error.IsError)
        {
            return new WireOffset(partition, -1, null, null, ConfluentErrors.Map(item.Error, WireCall.ListOffsets, item.Topic));
        }

        return new WireOffset(
            partition,
            item.Offset.Value >= 0 ? item.Offset.Value : -1,
            info.Timestamp >= 0 ? info.Timestamp : null,
            item.LeaderEpoch,
            null);
    }

    /// <summary>
    /// Runs one admin call: leases the cluster's client, returns the call's value or the error that withheld it, and lets the caller's
    /// token end the wait without ending the call. The lease is held until the call itself completes.
    /// </summary>
    private async Task<WireResult<T>> Admin<T>(
        WireCall call,
        string resource,
        Func<IAdminClient, string, Task<WireResult<T>>> run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<WireResult<T>> work;
        KafkaAdminPool<IAdminClient>.Lease lease;

        try
        {
            lease = pool.Rent(cluster);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return WireResult<T>.Fail(Classify(ConfluentErrors.Map(exception)));
        }

        try
        {
            work = Guard(call, resource, run, lease.Client);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        _ = work.ContinueWith(
            static (_, state) => ((IDisposable) state!).Dispose(),
            lease,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WireResult<T>> Guard<T>(
        WireCall call,
        string resource,
        Func<IAdminClient, string, Task<WireResult<T>>> run,
        IAdminClient client)
    {
        try
        {
            return await run(client, resource).ConfigureAwait(false);
        }
        catch (KafkaException exception)
        {
            return WireResult<T>.Fail(Classify(ConfluentErrors.Map(exception.Error, call, resource)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string callName = call.ToString();
            KStudioLog.CatalogReadFailed(loggers.Catalog, exception, callName, ClusterKey);
            return WireResult<T>.Fail(new WireError.Failed(exception.GetType().Name, IsFatal: false));
        }
    }

    /// <summary>A timeout after a recorded authentication failure is that failure, not an unreachable cluster.</summary>
    private WireError Classify(WireError error) =>
        error is WireError.Unavailable && events.RecentAuthenticationFailure(ClusterKey) is { } authentication
            ? authentication
            : error;
}
