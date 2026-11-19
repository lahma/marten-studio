using System.Collections.Concurrent;

namespace DataStudio.Kafka.Core.Catalog;

/// <summary>
/// One read per interval, however many tabs are watching: a port of the studios' <c>StudioSnapshotCache</c> (decision 5.3).
/// </summary>
/// <remarks>
/// <para>
/// Single flight: concurrent callers for one key share one in-flight task. The work runs under <see cref="CancellationToken.None" /> and
/// each caller waits through <c>WaitAsync(token)</c>, so one tab navigating away never cancels the read the others wait on. A faulted or
/// cancelled entry is removed. <b>Authorization is never part of a key</b>: the Kafka module asks its cluster policy before it calls, and the
/// engine answers only what the broker allows this application's principal. The time-to-live is per call, so the 60-second entries
/// (configs, authorized operations, the cluster description) and the refresh-interval entries share one cache.
/// </para>
/// <para>
/// <b>One read per key at a time.</b> The time-to-live decides how long a <em>completed</em> value is reused, counted from when the entry was created;
/// an entry whose read is still running is shared until the read completes, however old it is. A slow read is therefore shared and never doubled: a
/// group read that times out after the request timeout (10 seconds) against a refresh interval of 5 would otherwise start a second read of the same
/// key beside the first, on the same client, and a client a group call has timed out on is replaced (D50) but disposed only when its last call ends,
/// so a doubled read keeps it alive past the broker's return, which is when it crashes the process (D36). The price is that a factory that never
/// completes would hold its key for good: every caller is a wire call bounded by the request timeout (see <see cref="KafkaWireReads" />), and a
/// caller added here must be bounded the same way.
/// </para>
/// </remarks>
internal sealed class KafkaSnapshotCache(TimeProvider timeProvider)
{
    /// <summary>Above this many live keys, expired entries are swept on the next write.</summary>
    private const int PruneThreshold = 256;

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>How many entries are held, for a test and for nothing else.</summary>
    internal int Count => entries.Count;

    /// <summary>The cached value for <paramref name="key" />, running <paramref name="factory" /> only if no usable one is in flight or in hand.</summary>
    /// <typeparam name="T">What the read returns.</typeparam>
    /// <param name="key">The cluster and read this value is about.</param>
    /// <param name="timeToLive">How long the value is reused.</param>
    /// <param name="factory">How to produce it. Runs at most once per key per time-to-live.</param>
    /// <param name="cancellationToken">Cancels <em>this caller's</em> wait, never the shared work.</param>
    public async Task<T> GetAsync<T>(
        string key,
        TimeSpan timeToLive,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(factory);

        DateTimeOffset now = timeProvider.GetUtcNow();

        Entry fresh = new(now, timeToLive, new Lazy<Task<object?>>(
            async () => (object?) await factory(CancellationToken.None).ConfigureAwait(false),
            LazyThreadSafetyMode.ExecutionAndPublication));

        Entry entry = entries.AddOrUpdate(
            key,
            static (_, state) => state,
            static (_, existing, state) => IsUsable(existing, state.CreatedAt) ? existing : state,
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
            // This caller gave up waiting. The shared work carries on for everyone else, and the entry stays.
            throw;
        }
        catch (Exception)
        {
            // A faulted or cancelled entry is never served for the rest of its time-to-live.
            entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            throw;
        }
    }

    /// <summary>Drops one key, so the next read really reads.</summary>
    /// <param name="key">The key.</param>
    public void Invalidate(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        entries.TryRemove(key, out _);
    }

    /// <summary>Drops every key whose name starts with <paramref name="prefix" />.</summary>
    /// <param name="prefix">The prefix.</param>
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

    private static bool IsUsable(Entry entry, DateTimeOffset now)
    {
        // One read per key at a time: an entry whose read is still running is shared whatever its age, so a read that outlasts its
        // time-to-live (a group read that times out after the request timeout, against a refresh interval below it) is not doubled.
        if (!entry.Work.IsValueCreated || !entry.Work.Value.IsCompleted)
        {
            return true;
        }

        // A completed entry is reused for its time-to-live, counted from when the entry was created, and not when it faulted or was cancelled.
        return now - entry.CreatedAt < entry.TimeToLive && !entry.Work.Value.IsFaulted && !entry.Work.Value.IsCanceled;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, Entry> pair in entries)
        {
            if (!IsUsable(pair.Value, now))
            {
                entries.TryRemove(pair);
            }
        }
    }

    private sealed record Entry(DateTimeOffset CreatedAt, TimeSpan TimeToLive, Lazy<Task<object?>> Work);
}
