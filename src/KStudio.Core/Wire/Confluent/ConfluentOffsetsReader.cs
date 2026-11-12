using Confluent.Kafka;

using KStudio.Core.Clusters;

namespace KStudio.Core.Wire.Confluent;

/// <summary>
/// The handle the group-offsets read runs on: a consumer that is never subscribed or assigned (so it never fetches, and is polled only to
/// serve its events), and an admin client dependent on its handle (D36). An admin client has no consumer group, and librdkafka 2.16.0 and earlier dereference it when an OffsetFetch answers
/// <c>NOT_COORDINATOR</c> or <c>COORDINATOR_NOT_AVAILABLE</c> (issue #5040, fixed by PR #5611): the process dies with SIGSEGV. A
/// consumer's handle has one, so the same request takes the path that handles the answer. Hard rule 17 holds as it does for a scan
/// consumer: the reader commits nothing, joins no group, creates no topic and runs no host delegate.
/// </summary>
internal sealed class ConfluentOffsetsReader : IDisposable
{
    private const int MaxDrainIterations = 8;

    private readonly IConsumer<byte[], byte[]> consumer;
    private readonly IAdminClient admin;
    private readonly Lock drainGate = new();
    private int disposed;

    /// <summary>Builds the consumer and the admin client over its handle; nothing connects until the first call.</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="groupIdPrefix">The group id's prefix.</param>
    /// <param name="events">The engine's log and error handlers.</param>
    public ConfluentOffsetsReader(KafkaClusterDefinition cluster, string groupIdPrefix, ConfluentClientEvents events)
    {
        ConsumerConfig config = ConfluentClientSettings.BuildOffsetsReaderConfig(
            cluster,
            Guid.NewGuid(),
            groupIdPrefix,
            events.Overridden(cluster, "offsets"));

        GroupId = config.GroupId;
        string key = cluster.Key;

        ConsumerBuilder<byte[], byte[]> builder = new ConsumerBuilder<byte[], byte[]>(config)
            .SetLogHandler((_, message) => events.OnLog(key, "offsets", message))
            .SetErrorHandler((_, error) => events.OnError(key, "offsets", error));

        if (cluster.TokenRefresh is { } refresh)
        {
            builder.SetOAuthBearerTokenRefreshHandler((client, text) => ConfluentWireFactory.RefreshToken(refresh, client, text, key, "offsets", events));
        }

        consumer = builder.Build();

        try
        {
            admin = new DependentAdminClientBuilder(consumer.Handle).Build();
        }
        catch
        {
            consumer.Dispose();
            throw;
        }
    }

    /// <summary>The reader's own consumer group id, which no member ever joins and no offset is ever committed for.</summary>
    internal string GroupId { get; }

    /// <summary>The admin client over the consumer's handle: the group-offsets read runs on it, and nothing else does.</summary>
    internal IAdminClient Admin => admin;

    /// <summary>
    /// Serves the events the consumer's queue holds, so that none accumulates between reads and so that the host's OAuth token-refresh hook
    /// runs: librdkafka queues that op on the main queue inside <c>rd_kafka_new</c>, a consumer redirects the main queue to its poll queue,
    /// and the dependent admin client polls only its own result queue, so nothing else would ever serve it. Called before and after each
    /// read. The consumer has no assignment, so each call returns at once.
    /// </summary>
    internal void Drain()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        lock (drainGate)
        {
            for (int i = 0; i < MaxDrainIterations; i++)
            {
                try
                {
                    if (consumer.Consume(TimeSpan.Zero) is null)
                    {
                        return;
                    }
                }
                catch (Exception exception) when (exception is KafkaException or ObjectDisposedException)
                {
                    // A non-fatal error event surfaces as a ConsumeException; serving it is all that was asked.
                }
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        // The dependent admin client first: it uses the consumer's handle. Then the consumer is closed and disposed.
        try
        {
            admin.Dispose();
        }
        finally
        {
            try
            {
                consumer.Close();
            }
            finally
            {
                consumer.Dispose();
            }
        }
    }
}
