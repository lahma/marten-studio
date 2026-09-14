using System.Collections.Concurrent;

using JasperFx.Events.Projections;

using Marten.Storage;

using Microsoft.Extensions.Logging;

namespace MartenStudio.Services.Live;

/// <summary>
/// The latest shard state the in-process daemon published, per store and database.
/// </summary>
/// <remarks>
/// <para>
/// Where the daemon runs in this process, <c>IMartenDatabase.Tracker</c> is an
/// <see cref="IObservable{T}" /> of <c>ShardState</c> that pushes every advance as it happens. Reading it
/// is free and sub-second, and it is the one part of the studio that does not have to poll (plan D10).
/// Pages still poll, because the database is the source of truth and a daemon somewhere else publishes
/// nothing here; the tracker only makes the numbers fresher between polls.
/// </para>
/// <para>
/// The subscription is <em>lazy and leased</em>: it is attached when the first page asks and disposed
/// when the last one goes. A studio nobody has open subscribes to nothing, which is what keeps
/// registering the package from changing how the host behaves.
/// </para>
/// <para>
/// Singleton. The tracker is a process-wide object and its states are the same for every circuit, so one
/// subscription serves all of them.
/// </para>
/// </remarks>
internal sealed class StudioLiveState : IDisposable
{
    private readonly ILogger<StudioLiveState> logger;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, Watch> watches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private bool disposed;

    public StudioLiveState(ILogger<StudioLiveState> logger) : this(logger, TimeProvider.System)
    {
    }

    public StudioLiveState(ILogger<StudioLiveState> logger, TimeProvider timeProvider)
    {
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    /// <summary>How many tracker subscriptions are open, for a test and for nothing else.</summary>
    internal int WatchCount
    {
        get
        {
            lock (gate)
            {
                return watches.Count;
            }
        }
    }

    /// <summary>
    /// Starts watching this database's tracker if nobody is yet, and returns the lease that stops it.
    /// </summary>
    /// <param name="storeKey">The store's registration key.</param>
    /// <param name="database">The database whose tracker to observe.</param>
    /// <returns>
    /// A lease. Disposing the last one for a database detaches the observer. Disposing one twice is
    /// harmless, which matters because a Blazor component's <c>DisposeAsync</c> can run after a circuit
    /// has already been torn down.
    /// </returns>
    public IDisposable Subscribe(string storeKey, IMartenDatabase database)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeKey);
        ArgumentNullException.ThrowIfNull(database);

        string key = KeyFor(storeKey, database.Id.Identity);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (!watches.TryGetValue(key, out Watch? watch))
            {
                watch = new Watch();
                watches[key] = watch;

                try
                {
                    watch.Subscription = ((IObservable<ShardState>) database.Tracker).Subscribe(
                        new TrackerObserver(watch, timeProvider));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A tracker that will not be observed costs the page nothing: it falls back to the
                    // database rows it was already reading.
                    logger.LogWarning(
                        exception,
                        "Marten Studio could not observe the shard state tracker of store {StoreKey}, database {DatabaseId}",
                        storeKey,
                        database.Id.Identity);
                }
            }

            watch.Leases++;
            return new Lease(this, key);
        }
    }

    /// <summary>
    /// The latest state of every shard this process has seen for one database, by shard name.
    /// </summary>
    /// <remarks>Empty when nothing is watching, which is exactly what a page reading it should then show.</remarks>
    public IReadOnlyDictionary<string, ShardState> Latest(string storeKey, string databaseId)
    {
        Watch? watch;
        lock (gate)
        {
            watches.TryGetValue(KeyFor(storeKey, databaseId), out watch);
        }

        if (watch is null)
        {
            return new Dictionary<string, ShardState>(StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, ShardState>(watch.States, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>When this process last saw the tracker publish anything for that database.</summary>
    public DateTimeOffset? LastObservedAt(string storeKey, string databaseId)
    {
        lock (gate)
        {
            return watches.TryGetValue(KeyFor(storeKey, databaseId), out Watch? watch) ? watch.LastObservedAt : null;
        }
    }

    /// <summary>Detaches every observer. The studio's own shutdown, not the host's.</summary>
    public void Dispose()
    {
        List<Watch> open;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            open = [.. watches.Values];
            watches.Clear();
        }

        foreach (Watch watch in open)
        {
            watch.Subscription?.Dispose();
        }
    }

    private static string KeyFor(string storeKey, string databaseId) => storeKey + "|" + databaseId;

    private void Release(string key)
    {
        IDisposable? subscription = null;

        lock (gate)
        {
            if (!watches.TryGetValue(key, out Watch? watch))
            {
                return;
            }

            watch.Leases--;
            if (watch.Leases > 0)
            {
                return;
            }

            watches.Remove(key);
            subscription = watch.Subscription;
        }

        subscription?.Dispose();
    }

    /// <summary>One database's observer, its leases and what it has seen.</summary>
    private sealed class Watch
    {
        private long lastObservedTicks;

        public ConcurrentDictionary<string, ShardState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IDisposable? Subscription { get; set; }

        public int Leases { get; set; }

        /// <summary>
        /// When the tracker last published. Ticks behind an interlocked read because the daemon's thread
        /// writes this while a circuit reads it, and a <see cref="DateTimeOffset" /> is wider than a word.
        /// </summary>
        public DateTimeOffset? LastObservedAt
        {
            get
            {
                long ticks = Interlocked.Read(ref lastObservedTicks);
                return ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
            }
        }

        public void MarkObserved(DateTimeOffset at) => Interlocked.Exchange(ref lastObservedTicks, at.UtcTicks);
    }

    /// <summary>One page's claim on a watch.</summary>
    private sealed class Lease : IDisposable
    {
        private StudioLiveState? owner;
        private readonly string key;

        public Lease(StudioLiveState owner, string key)
        {
            this.owner = owner;
            this.key = key;
        }

        public void Dispose()
        {
            StudioLiveState? released = Interlocked.Exchange(ref owner, null);
            released?.Release(key);
        }
    }

    /// <summary>
    /// The observer itself: it stores and never blocks.
    /// </summary>
    /// <remarks>
    /// <c>OnNext</c> runs on the daemon's own thread. Anything slow here would slow the projections down,
    /// which would make an admin page a performance problem for the application it is watching - so this
    /// writes one dictionary entry and returns, and the pages read it on their next poll tick.
    /// </remarks>
    private sealed class TrackerObserver : IObserver<ShardState>
    {
        private readonly Watch watch;
        private readonly TimeProvider timeProvider;

        public TrackerObserver(Watch watch, TimeProvider timeProvider)
        {
            this.watch = watch;
            this.timeProvider = timeProvider;
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ShardState value)
        {
            if (value?.ShardName is not { Length: > 0 } name)
            {
                return;
            }

            watch.States[name] = value;
            watch.MarkObserved(timeProvider.GetUtcNow());
        }
    }
}
