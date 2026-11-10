using KStudio.Core.Clusters;
using KStudio.Core.Logging;

using Microsoft.Extensions.Logging;

namespace KStudio.Core.Wire.Confluent;

/// <summary>
/// One admin client per cluster, created by the first call that needs it and disposed when it has been idle for the idle timeout
/// (decision 4.1). Nothing is created at startup, and there is no hosted service: the reaper is a timer started with the first client
/// that stops itself when none is left.
/// </summary>
/// <typeparam name="TClient">The client; <c>IAdminClient</c> in production, anything disposable in a test.</typeparam>
internal sealed class KafkaAdminPool<TClient> : IDisposable, IAsyncDisposable
    where TClient : class, IDisposable
{
    private readonly Func<KafkaClusterDefinition, TClient> create;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan idleTimeout;
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private ITimer? reaper;
    private bool disposed;

    /// <summary>Creates a pool; it connects to nothing.</summary>
    /// <param name="create">Builds the client of a cluster.</param>
    /// <param name="timeProvider">The clock the idle time is read from.</param>
    /// <param name="idleTimeout">How long a client may sit unused.</param>
    /// <param name="logger">Where 9650 and 9651 go.</param>
    public KafkaAdminPool(Func<KafkaClusterDefinition, TClient> create, TimeProvider timeProvider, TimeSpan idleTimeout, ILogger logger)
    {
        this.create = create;
        this.timeProvider = timeProvider;
        this.idleTimeout = idleTimeout;
        this.logger = logger;
    }

    /// <summary>How many clients exist right now, for a test.</summary>
    internal int ClientCount
    {
        get
        {
            lock (gate)
            {
                return slots.Count;
            }
        }
    }

    /// <summary>Takes a lease on the cluster's client, creating the client on first use. Dispose the lease when the call is done.</summary>
    /// <param name="cluster">The cluster.</param>
    public Lease Rent(KafkaClusterDefinition cluster)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (!slots.TryGetValue(cluster.Key, out Slot? slot))
            {
                slot = new Slot(create(cluster), timeProvider.GetUtcNow());
                slots[cluster.Key] = slot;
                string key = KafkaLogValue.Cap(cluster.Key);
                KStudioLog.AdminClientCreated(logger, key);
                reaper ??= timeProvider.CreateTimer(Reap, null, idleTimeout / 2, idleTimeout / 2);
            }

            slot.Leases++;
            slot.LastUsed = timeProvider.GetUtcNow();
            return new Lease(this, cluster.Key, slot);
        }
    }

    /// <summary>
    /// Disposes every client and refuses new leases. A failing client does not stop the others and is logged at Debug (9664). Both
    /// interfaces are implemented, so that a synchronous <c>ServiceProvider.Dispose()</c> can dispose the pool as well as an asynchronous one.
    /// </summary>
    public void Dispose()
    {
        List<KeyValuePair<string, Slot>> toDispose;

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            toDispose = [.. slots];
            slots.Clear();
            reaper?.Dispose();
            reaper = null;
        }

        // Disposing an admin client blocks while librdkafka tears down, so it is never done under the lock.
        foreach (KeyValuePair<string, Slot> pair in toDispose)
        {
            DisposeClient(pair.Key, pair.Value);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Reap(object? state)
    {
        // A timer callback that throws ends the process, so nothing escapes this method.
        try
        {
            List<KeyValuePair<string, Slot>> expired = [];
            DateTimeOffset now = timeProvider.GetUtcNow();

            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                foreach (KeyValuePair<string, Slot> pair in slots)
                {
                    if (pair.Value.Leases == 0 && now - pair.Value.LastUsed >= idleTimeout)
                    {
                        expired.Add(pair);
                    }
                }

                foreach (KeyValuePair<string, Slot> pair in expired)
                {
                    slots.Remove(pair.Key);
                }

                if (slots.Count == 0)
                {
                    reaper?.Dispose();
                    reaper = null;
                }
            }

            foreach (KeyValuePair<string, Slot> pair in expired)
            {
                if (DisposeClient(pair.Key, pair.Value))
                {
                    string key = KafkaLogValue.Cap(pair.Key);
                    KStudioLog.AdminClientDisposed(logger, key, (int) (now - pair.Value.LastUsed).TotalSeconds);
                }
            }
        }
        catch (Exception exception)
        {
            KStudioLog.AdminClientDisposeFailed(logger, exception, "pool");
        }
    }

    private bool DisposeClient(string cluster, Slot slot)
    {
        try
        {
            slot.Client.Dispose();
            return true;
        }
        catch (Exception exception)
        {
            string key = KafkaLogValue.Cap(cluster);
            KStudioLog.AdminClientDisposeFailed(logger, exception, key);
            return false;
        }
    }

    private void Release(Slot slot)
    {
        lock (gate)
        {
            slot.Leases--;
            slot.LastUsed = timeProvider.GetUtcNow();
        }
    }

    internal sealed class Slot(TClient client, DateTimeOffset lastUsed)
    {
        public TClient Client { get; } = client;

        public int Leases { get; set; }

        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }

    /// <summary>A call's hold on a cluster's admin client; the client is not disposed while a lease exists.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly KafkaAdminPool<TClient> pool;
        private readonly Slot slot;
        private int released;

        internal Lease(KafkaAdminPool<TClient> pool, string cluster, Slot slot)
        {
            this.pool = pool;
            this.slot = slot;
            Cluster = cluster;
        }

        /// <summary>The cluster's key.</summary>
        public string Cluster { get; }

        /// <summary>The client; valid until the lease is disposed.</summary>
        public TClient Client => slot.Client;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                pool.Release(slot);
            }
        }
    }
}
