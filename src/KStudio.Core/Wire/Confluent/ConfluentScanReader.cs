using Confluent.Kafka;

using KStudio.Core.Clusters;

namespace KStudio.Core.Wire.Confluent;

/// <summary>
/// <see cref="IWireScanReader" /> over one Confluent consumer (decisions 3.1, 4.2 and D37). It is assigned explicit offsets only,
/// never subscribes, commits or stores an offset, and is used from one thread, the scan's.
/// </summary>
/// <remarks>
/// SPIKE S1: a principal with no group ACL still reads; its consumer sees one non-fatal <c>GroupAuthorizationFailed</c> from
/// <c>Consume</c>, which this reader returns as a <see cref="WireReadOutcome.Failure" /> for the scan to count and go on. SPIKE S3d:
/// a position deleted mid-scan arrives as <c>Local_AutoOffsetReset</c>, and <see cref="Reposition" /> is an unassign followed by an
/// assign, because a <c>Seek</c> on that partition throws.
/// </remarks>
internal sealed class ConfluentScanReader : IWireScanReader
{
    private readonly IConsumer<byte[], byte[]> consumer;
    private readonly string groupId;
    private bool closed;
    private bool disposed;

    /// <summary>Builds the consumer; nothing is fetched until <see cref="Assign" />.</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="options">The engine options as validated (the group id prefix).</param>
    /// <param name="readerOptions">The scan's id, isolation and limits.</param>
    /// <param name="events">The engine's log and error handlers.</param>
    public ConfluentScanReader(
        KafkaClusterDefinition cluster,
        KafkaEngineSnapshot options,
        WireReaderOptions readerOptions,
        ConfluentClientEvents events)
    {
        ConsumerConfig config = ConfluentClientSettings.BuildConsumerConfig(
            cluster,
            readerOptions.ScanId,
            readerOptions.Isolation,
            readerOptions.Limits,
            options.GroupIdPrefix,
            events.Overridden(cluster, "scan"));

        groupId = config.GroupId;
        string key = cluster.Key;

        // The default deserializers of byte[] are Confluent's ByteArray: a null value stays null, so a tombstone is never an empty array.
        ConsumerBuilder<byte[], byte[]> builder = new ConsumerBuilder<byte[], byte[]>(config)
            .SetLogHandler((_, message) => events.OnLog(key, "scan", message))
            .SetErrorHandler((_, error) => events.OnError(key, "scan", error));

        if (cluster.TokenRefresh is { } refresh)
        {
            builder.SetOAuthBearerTokenRefreshHandler((client, text) => ConfluentWireFactory.RefreshToken(refresh, client, text, key, "scan", events));
        }

        try
        {
            consumer = builder.Build();
        }
        catch (KafkaException exception)
        {
            throw new WireException(ConfluentErrors.Map(exception.Error, WireCall.Fetch));
        }
    }

    /// <summary>The scan's consumer group id, which only the refusal of a group ACL names.</summary>
    internal string GroupId => groupId;

    /// <inheritdoc />
    public void Assign(IReadOnlyList<WirePartitionOffset> start)
    {
        foreach (WirePartitionOffset position in start)
        {
            // A negative offset is Beginning, End or Stored: the last stalls without a group ACL (SPIKE S1) and none is explicit.
            ArgumentOutOfRangeException.ThrowIfNegative(position.Offset, nameof(start));
        }

        Mapped(() => consumer.Assign(start.Select(static x => new TopicPartitionOffset(x.Topic, x.Partition, x.Offset)).ToList()));
    }

    /// <inheritdoc />
    public void Pause(IReadOnlyList<WirePartition> partitions) =>
        Mapped(() => consumer.Pause(partitions.Select(static x => new TopicPartition(x.Topic, x.Partition)).ToList()));

    /// <inheritdoc />
    public void Resume(IReadOnlyList<WirePartition> partitions) =>
        Mapped(() => consumer.Resume(partitions.Select(static x => new TopicPartition(x.Topic, x.Partition)).ToList()));

    /// <inheritdoc />
    public void Reposition(WirePartitionOffset position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position.Offset, nameof(position));

        Mapped(() =>
        {
            consumer.IncrementalUnassign([new TopicPartition(position.Topic, position.Partition)]);
            consumer.IncrementalAssign([new TopicPartitionOffset(position.Topic, position.Partition, position.Offset)]);
        });
    }

    /// <summary>Runs a consumer call and turns a Confluent exception into the engine's <see cref="WireException" />.</summary>
    private static void Mapped(Action call)
    {
        try
        {
            call();
        }
        catch (KafkaException exception)
        {
            throw new WireException(ConfluentErrors.Map(exception.Error, WireCall.Fetch));
        }
    }

    /// <inheritdoc />
    public WireReadOutcome Read(TimeSpan timeout)
    {
        try
        {
            ConsumeResult<byte[], byte[]>? result = consumer.Consume(timeout);

            if (result is null)
            {
                return WireReadOutcome.Idle;
            }

            if (result.IsPartitionEOF)
            {
                return new WireReadOutcome.PartitionEnd(new WirePartition(result.Topic, result.Partition.Value), result.Offset.Value);
            }

            return new WireReadOutcome.Record(ToRecord(result));
        }
        catch (ConsumeException exception)
        {
            return ToFailure(exception);
        }
        catch (KafkaException exception)
        {
            return new WireReadOutcome.Failure(ConfluentErrors.Map(exception.Error, WireCall.Fetch), null);
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        if (closed)
        {
            return;
        }

        closed = true;

        try
        {
            // With auto-commit off, no stored offsets and no subscription this commits nothing and leaves no group (D37).
            consumer.Close();
        }
        catch (KafkaException)
        {
            // The consumer is disposed anyway.
        }
        finally
        {
            Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        consumer.Dispose();
    }

    private static WireRecord ToRecord(ConsumeResult<byte[], byte[]> result)
    {
        Message<byte[], byte[]> message = result.Message;
        List<WireHeader> headers = [];

        if (message.Headers is { } source)
        {
            foreach (IHeader header in source)
            {
                headers.Add(new WireHeader(header.Key, header.GetValueBytes()));
            }
        }

        WireTimestampType type = message.Timestamp.Type switch
        {
            TimestampType.CreateTime => WireTimestampType.CreateTime,
            TimestampType.LogAppendTime => WireTimestampType.LogAppendTime,
            _ => WireTimestampType.NotAvailable,
        };

        return new WireRecord(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            message.Key,
            message.Value,
            [.. headers],
            message.Timestamp.UnixTimestampMs,
            type,
            result.LeaderEpoch);
    }

    private WireReadOutcome.Failure ToFailure(ConsumeException exception)
    {
        TopicPartition? partition = exception.ConsumerRecord?.TopicPartition;
        WirePartition? wirePartition = partition is { Topic.Length: > 0 }
            ? new WirePartition(partition.Topic, partition.Partition.Value)
            : null;

        // A group refusal names the group; everything else names the topic the failure is about.
        string resource = exception.Error.Code == ErrorCode.GroupAuthorizationFailed ? groupId : wirePartition?.Topic ?? string.Empty;

        return new WireReadOutcome.Failure(
            ConfluentErrors.Map(exception.Error, WireCall.Fetch, resource, wirePartition),
            wirePartition);
    }
}
