using System.Collections.Concurrent;

namespace MartenStudio.Services.Live;

/// <summary>
/// One query per interval, however many browser tabs are watching.
/// </summary>
/// <remarks>
/// <para>
/// Every live page polls (plan D10 - there is no SignalR hub in v1), and without this a studio open in
/// five tabs would run five copies of the same query five times a second against the database it is
/// supposed to help you understand. The cache is single-flight: concurrent callers for the same key
/// <em>share</em> one in-flight task rather than each starting their own, which is the half that matters
/// - a short time-to-live alone would still let N circuits stampede the moment an entry expires.
/// </para>
/// <para>
/// The work runs under <see cref="CancellationToken.None" /> and each caller awaits it through
/// <c>Task.WaitAsync(CancellationToken)</c>. A shared task cannot take one caller's cancellation token:
/// the first tab to navigate away would cancel the query every other tab is waiting on.
/// </para>
/// <para>
/// A key is <c>(scope, query)</c> and nothing else. Authorization is <em>not</em> part of it and must
/// never be: callers resolve their scope - which is where the store, database and tenant policies are
/// enforced - before they reach this, so a cached entry is only ever handed to someone who has just
/// passed the same check for the same scope.
/// </para>
/// </remarks>
internal sealed class StudioSnapshotCache
{
    /// <summary>How long an entry is reused for.</summary>
    /// <remarks>
    /// About one second: long enough that a page polling at the default five seconds costs one query per
    /// interval across every circuit, short enough that a person clicking Refresh twice sees the second
    /// click do something.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromSeconds(1);

    /// <summary>Above this many live keys, expired entries are swept on the next write.</summary>
    private const int PruneThreshold = 256;

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan timeToLive;

    public StudioSnapshotCache() : this(TimeProvider.System, DefaultTimeToLive)
    {
    }

    public StudioSnapshotCache(TimeProvider timeProvider, TimeSpan timeToLive)
    {
        this.timeProvider = timeProvider;
        this.timeToLive = timeToLive;
    }

    /// <summary>How many entries are being held, for a test and for nothing else.</summary>
    internal int Count => entries.Count;

    /// <summary>
    /// The cached value for <paramref name="key" />, running <paramref name="factory" /> only if no
    /// usable one is in flight or in hand.
    /// </summary>
    /// <typeparam name="T">What the query returns.</typeparam>
    /// <param name="key">The scope and query this value is about.</param>
    /// <param name="factory">How to produce it. Runs at most once per key per time-to-live.</param>
    /// <param name="cancellationToken">Cancels <em>this caller's</em> wait, never the shared work.</param>
    public async Task<T> GetAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(factory);

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Constructing the Lazy costs nothing: its factory only runs if this entry wins the race and
        // somebody then asks for its value.
        Entry fresh = new(now, timeToLive, new Lazy<Task<object?>>(
            async () => (object?) await factory(CancellationToken.None).ConfigureAwait(false),
            LazyThreadSafetyMode.ExecutionAndPublication));

        Entry entry = entries.AddOrUpdate(
            key,
            static (_, state) => state,
            static (_, existing, state) => IsUsable(existing, state.CreatedAt, state.TimeToLive) ? existing : state,
            fresh);

        if (ReferenceEquals(entry, fresh) && entries.Count > PruneThreshold)
        {
            Prune(now);
        }

        try
        {
            object? value = await entry.Work.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            return (T) value!;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // This caller gave up waiting. The shared work carries on for everyone else, and the entry
            // stays: a tab navigating away must not throw away the query the other four are waiting on.
            throw;
        }
        catch (Exception)
        {
            // A faulted *or cancelled* entry must not be served for the rest of its time-to-live. A
            // cancelled shared task is as useless as a faulted one - it will never produce a value, and
            // every later caller would await the same dead task - so the next poll is how a page recovers.
            entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            throw;
        }
    }

    /// <summary>Drops one key, so the next read really reads. Called after anything is changed.</summary>
    public void Invalidate(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        entries.TryRemove(key, out _);
    }

    /// <summary>Drops every key whose name starts with <paramref name="prefix" />.</summary>
    public void InvalidatePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        foreach (KeyValuePair<string, Entry> pair in entries)
        {
            if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                entries.TryRemove(pair);
            }
        }
    }

    private static bool IsUsable(Entry entry, DateTimeOffset now, TimeSpan timeToLive)
    {
        if (now - entry.CreatedAt >= timeToLive)
        {
            return false;
        }

        if (!entry.Work.IsValueCreated)
        {
            return true;
        }

        // Faulted and cancelled are the same answer here: a task in either state will never produce a
        // value, so serving it again would hand every caller for the rest of the time-to-live a failure
        // that has already happened.
        Task<object?> work = entry.Work.Value;
        return !work.IsFaulted && !work.IsCanceled;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, Entry> pair in entries)
        {
            if (!IsUsable(pair.Value, now, timeToLive))
            {
                entries.TryRemove(pair);
            }
        }
    }

    /// <summary>
    /// One key's shared work, when it started, and how long it is good for.
    /// </summary>
    /// <remarks>
    /// The time-to-live rides on the entry so that the <c>AddOrUpdate</c> delegates can stay static and
    /// allocate no closure per call - this runs on every poll of every circuit.
    /// </remarks>
    private sealed record Entry(DateTimeOffset CreatedAt, TimeSpan TimeToLive, Lazy<Task<object?>> Work);
}
