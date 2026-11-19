using DataStudio.Kafka.Core.Clusters;
using DataStudio.Kafka.Core.Logging;

using Microsoft.Extensions.Logging;

namespace DataStudio.Kafka.Core.Wire.Confluent;

/// <summary>
/// One client per cluster (the admin client, or the offsets reader: <c>clientKind</c> says which), created by the first call that needs it and disposed when it has been idle for the idle timeout
/// (decision 4.1). Nothing is created at startup, and there is no hosted service: the reaper is a timer started with the first client
/// that stops itself when none is left. A client a call still holds is never destroyed: shutting the pool down retires it, and the last
/// lease released disposes it.
/// </summary>
/// <typeparam name="TClient">The client; <c>IAdminClient</c> in production, anything disposable in a test.</typeparam>
internal sealed class KafkaAdminPool<TClient> : IDisposable, IAsyncDisposable
    where TClient : class, IDisposable
{
    private readonly Func<KafkaClusterDefinition, TClient> create;
    private readonly string clientKind;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan idleTimeout;
    private readonly TimeSpan retiredWait;
    private readonly ILogger logger;
    private readonly Lock gate = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private ITimer? reaper;
    private bool disposed;
    private int retiredPending;
    private TaskCompletionSource? retiredDrained;

    /// <summary>Creates a pool; it connects to nothing.</summary>
    /// <param name="create">Builds the client of a cluster.</param>
    /// <param name="clientKind">What the pool holds, <c>admin</c> or <c>offsets</c>: the log says which of the two pools a line is about.</param>
    /// <param name="timeProvider">The clock the idle time is read from.</param>
    /// <param name="idleTimeout">How long a client may sit unused.</param>
    /// <param name="logger">Where 9650 and 9651 go.</param>
    /// <param name="retiredWait">How long <see cref="DisposeAsync" /> waits for the clients that were leased at shutdown (the request timeout and a second).</param>
    public KafkaAdminPool(
        Func<KafkaClusterDefinition, TClient> create,
        string clientKind,
        TimeProvider timeProvider,
        TimeSpan idleTimeout,
        ILogger logger,
        TimeSpan retiredWait = default)
    {
        this.create = create;
        this.clientKind = clientKind;
        this.timeProvider = timeProvider;
        this.idleTimeout = idleTimeout;
        this.logger = logger;
        this.retiredWait = retiredWait;
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
                slot = new Slot(cluster.Key, create(cluster), timeProvider.GetUtcNow());
                slots[cluster.Key] = slot;
                string key = KafkaLogValue.Cap(cluster.Key);
                KafkaEngineLog.AdminClientCreated(logger, clientKind, key);
                reaper ??= timeProvider.CreateTimer(Reap, null, idleTimeout / 2, idleTimeout / 2);
            }

            slot.Leases++;
            slot.LastUsed = timeProvider.GetUtcNow();
            return new Lease(this, cluster.Key, slot);
        }
    }

    /// <summary>
    /// Refuses new leases and disposes every client no call holds. A client a call still holds is retired instead, and its last lease
    /// disposes it (on a thread-pool thread, never inline), so that no blocking call touches freed native memory. A failing client does
    /// not stop the others and is logged at Debug (9664). Both interfaces are implemented, so that a synchronous
    /// <c>ServiceProvider.Dispose()</c> can dispose the pool as well as an asynchronous one.
    /// </summary>
    public void Dispose()
    {
        List<KeyValuePair<string, Slot>> toDispose = [];

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            foreach (KeyValuePair<string, Slot> pair in slots)
            {
                if (pair.Value.Leases == 0)
                {
                    toDispose.Add(pair);
                }
                else
                {
                    pair.Value.Retired = true;
                    retiredPending++;
                }
            }

            if (retiredPending > 0)
            {
                retiredDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

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

    /// <summary>
    /// Disposes as <see cref="Dispose" /> does, then waits at most the retired wait (the request timeout and a second) for the clients
    /// that were leased; one still leased after that is left to its last lease.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();

        Task? drained;

        lock (gate)
        {
            drained = retiredDrained?.Task;
        }

        if (drained is null || drained.IsCompleted)
        {
            return;
        }

        try
        {
            await drained.WaitAsync(retiredWait, timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A call is still in flight; its lease disposes the client when the call ends.
        }
    }

    /// <summary>The least time between two replacements of one cluster's client for a stale coordinator (D50); a timed-out group call has no cooldown.</summary>
    internal static readonly TimeSpan RetireCooldown = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, DateTimeOffset> retiredAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes the client a lease holds out of the pool, because the group coordinator it looked up is no longer the group's (D50), so that the
    /// next <see cref="Rent" /> builds a new one. librdkafka's admin API keeps a group coordinator it looked up for as long as the client is
    /// used, and every lookup renews it, so a client that was answered <c>NOT_COORDINATOR</c> would keep asking the broker that no longer
    /// coordinates the group until it sat idle. The client is disposed when its last lease is released, as a client leased at shutdown is; one
    /// another call already replaced is left alone, so concurrent failures replace it once. <b>A cluster's client is replaced for this reason at
    /// most once per <see cref="RetireCooldown" />:</b> the answer is a reply, so the broker is there, and a coordinator that keeps answering "not
    /// available" (an election that does not end) would otherwise cost a new client for every failing group at every refresh. A group call that
    /// timed out is the other rule (<see cref="RetireAfterTimeout" />).
    /// </summary>
    /// <param name="lease">The lease whose call was answered with a stale coordinator.</param>
    /// <param name="code">The error code that said so, for the log.</param>
    /// <returns>Whether this call took the client out.</returns>
    public bool RetireStaleCoordinator(Lease lease, string code) => Retire(lease, code, applyCooldown: true);

    /// <summary>
    /// Takes the client a lease holds out of the pool, because a group call on it ended without an answer (<c>Local_TimedOut</c>), <b>every time and with no cooldown</b> (D50). A client whose group request timed out while the broker was away
    /// crashes the whole process (<c>0xC0000005</c>) a few seconds after the broker is back, and the only safe client is a new one; the
    /// cooldown of <see cref="RetireStaleCoordinator" /> would keep the replacement when it times out too, and that one is the one that kills
    /// the process. A cluster that is really down costs one rebuild per timed-out group read, and such a read takes the request timeout. One
    /// another call already replaced is left alone, so concurrent timeouts on one client replace it once.
    /// </summary>
    /// <param name="lease">The lease whose group call timed out.</param>
    /// <param name="code">The error code that said so, for the log.</param>
    /// <returns>Whether this call took the client out.</returns>
    public bool RetireAfterTimeout(Lease lease, string code) => Retire(lease, code, applyCooldown: false);

    private bool Retire(Lease lease, string code, bool applyCooldown)
    {
        ArgumentNullException.ThrowIfNull(lease);

        lock (gate)
        {
            if (disposed || !slots.TryGetValue(lease.Cluster, out Slot? current) || !ReferenceEquals(current, lease.Held))
            {
                return false;
            }

            if (applyCooldown)
            {
                DateTimeOffset now = timeProvider.GetUtcNow();

                if (retiredAt.TryGetValue(lease.Cluster, out DateTimeOffset last) && now - last < RetireCooldown)
                {
                    return false;
                }

                retiredAt[lease.Cluster] = now;
            }

            slots.Remove(lease.Cluster);
            current.Retired = true;
            retiredPending++;
        }

        string key = KafkaLogValue.Cap(lease.Cluster);
        string reason = KafkaLogValue.Cap(code);
        KafkaEngineLog.AdminClientReplaced(logger, clientKind, key, reason);
        return true;
    }

    private void Reap(object? state)
    {
        // A timer callback that throws ends the process, so nothing escapes this method.
        List<KeyValuePair<string, Slot>> expired = [];

        try
        {
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
                    KafkaEngineLog.AdminClientDisposed(logger, clientKind, key, (int) (now - pair.Value.LastUsed).TotalSeconds);
                }
            }
        }
        catch (Exception exception)
        {
            // The clients this pass had picked to dispose (the dispose of each is guarded in DisposeClient, so this is the pass itself failing).
            string clusters = expired.Count == 0 ? "(none picked)" : KafkaLogValue.Cap(string.Join(", ", expired.Select(static x => x.Key)));
            string kind = exception.GetType().Name;
            KafkaEngineLog.AdminClientDisposeFailed(logger, clientKind, clusters, kind);
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
            string kind = exception.GetType().Name;
            KafkaEngineLog.AdminClientDisposeFailed(logger, clientKind, key, kind);
            return false;
        }
    }

    private void Release(Slot slot)
    {
        bool dispose = false;

        lock (gate)
        {
            slot.Leases--;
            slot.LastUsed = timeProvider.GetUtcNow();

            if (slot.Retired && slot.Leases == 0)
            {
                slot.Retired = false;
                dispose = true;
            }
        }

        if (dispose)
        {
            // The lease is released on whatever thread finished the call, and disposing the client blocks while librdkafka tears down.
            ThreadPool.UnsafeQueueUserWorkItem(DisposeRetired, slot, preferLocal: false);
        }
    }

    private void DisposeRetired(Slot slot)
    {
        try
        {
            DisposeClient(slot.Key, slot);
        }
        finally
        {
            lock (gate)
            {
                if (--retiredPending == 0)
                {
                    retiredDrained?.TrySetResult();
                }
            }
        }
    }

    internal sealed class Slot(string key, TClient client, DateTimeOffset lastUsed)
    {
        public string Key { get; } = key;

        public TClient Client { get; } = client;

        public int Leases { get; set; }

        public bool Retired { get; set; }

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

        /// <summary>The slot this lease holds, so that a retire retires only the client the call ran on.</summary>
        internal Slot Held => slot;

        /// <summary>Takes this lease's client out of the pool for a stale coordinator, at most once per cooldown (see <see cref="KafkaAdminPool{TClient}.RetireStaleCoordinator" />).</summary>
        /// <param name="code">The error code that said the client's coordinator is stale.</param>
        public bool RetireStaleCoordinator(string code) => pool.RetireStaleCoordinator(this, code);

        /// <summary>Takes this lease's client out of the pool because a group call on it timed out, with no cooldown (see <see cref="KafkaAdminPool{TClient}.RetireAfterTimeout" />).</summary>
        /// <param name="code">The timeout's error code.</param>
        public bool RetireAfterTimeout(string code) => pool.RetireAfterTimeout(this, code);

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
