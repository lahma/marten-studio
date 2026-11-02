using Microsoft.Extensions.Options;

using PostgresStudio.Services.Live;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// Issue #43: how the studio's pages read the connection history - once per <see cref="CacheFor" /> and server, whatever the number
/// of circuits polling, and never longer than the studio's <c>QueryTimeout</c>.
/// </summary>
/// <remarks>
/// <para>
/// The store may be the host's own - a table, a time-series database, a remote service - and Database › Sessions polls every
/// <c>HealthInterval</c> in every circuit that has it open. So the reads share one single-flight cache (the same
/// <see cref="StudioSnapshotCache" /> every live page polls through, with a longer time to live: a sample arrives a minute at the
/// most often), and each read is cut off at <c>QueryTimeout</c>: a store that hangs makes the history absent for that poll, never the
/// page slow.
/// </para>
/// <para>
/// A cached entry is keyed by server alone, and holds samples, not a view: the gate - counts for the page's own gate, application
/// names past <c>ViewDatabaseActivity</c> - is the caller's, applied after the read, every time.
/// </para>
/// <para>
/// <b>Incremental (issue #51).</b> The reader keeps each server's series, oldest first, and a refresh asks the store only for the
/// samples since the newest one it holds, less <see cref="Overlap" />, de-duplicated by <c>At</c> (the copy it holds wins), then trims
/// what left the window. The first read, a read after the series was evicted, and one every <see cref="FullReadEvery" /> read the
/// whole window: the last catches samples that land out of order - another process's backlog flushed after a leadership handover,
/// a store that inserts late. A refresh that fails or times out leaves the series as it was and tries again from the same point.
/// A series is dropped after <see cref="IdleFor" /> unread, and past <see cref="MaxServers" /> the least recently read goes first.
/// </para>
/// </remarks>
internal sealed class ConnectionHistoryReader
{
    /// <summary>How long one read of a server's history is reused for.</summary>
    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    /// <summary>How far before its newest sample a refresh reads again, so a sample that landed a little late is still found.</summary>
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    /// <summary>How often the whole window is read again, whatever the refreshes found.</summary>
    internal static readonly TimeSpan FullReadEvery = TimeSpan.FromMinutes(10);

    /// <summary>How long a server's series is kept without a read of it: a page nobody opens costs no memory.</summary>
    internal static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(10);

    /// <summary>The most servers whose series are kept; past it the least recently read is dropped.</summary>
    internal const int MaxServers = 64;

    private readonly Dictionary<string, Series> series = new(StringComparer.Ordinal);
    private readonly Lock seriesGate = new();
    private readonly IPostgresConnectionHistoryStore store;
    private readonly IOptions<PostgresConnectionHistoryOptions> options;
    private readonly IDiagnosticsSettings settings;
    private readonly TimeProvider time;
    private readonly StudioSnapshotCache cache;

    public ConnectionHistoryReader(
        IPostgresConnectionHistoryStore store,
        IOptions<PostgresConnectionHistoryOptions> options,
        IDiagnosticsSettings settings,
        TimeProvider? time = null,
        ConnectionHistoryRegistration? registration = null)
    {
        ReaderOnly = registration?.ReaderOnly ?? false;
        this.store = store;
        this.options = options;
        this.settings = settings;
        this.time = time ?? TimeProvider.System;
        cache = new StudioSnapshotCache(this.time, CacheFor);
    }

    /// <summary>Issue #48: whether this process only reads a history another process fills (<c>AddPostgresConnectionHistoryReader()</c>).</summary>
    public bool ReaderOnly { get; }

    /// <summary>How many series are held, for a test and for nothing else.</summary>
    internal int Servers
    {
        get
        {
            lock (seriesGate)
            {
                return series.Count;
            }
        }
    }

    /// <summary>The history's window: how far back a page reads, the options' <c>Retention</c>.</summary>
    public TimeSpan Window => options.Value.Retention;

    /// <summary>The samples of <paramref name="server" /> within <see cref="Window" />, as the store holds them.</summary>
    /// <param name="server">The server, <c>host:port</c> as the connection history keys it.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    /// <exception cref="OperationCanceledException">
    /// The store took longer than <c>QueryTimeout</c>, while <paramref name="cancellationToken" /> was not cancelled.
    /// </exception>
    public Task<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);

        string key = server.ToLowerInvariant();
        Series held = Touch(key);

        return cache.GetAsync<IReadOnlyList<PostgresConnectionSample>>(
            "connection-history:" + key,
            _ => RefreshAsync(held, server),
            cancellationToken);
    }

    /// <summary>The series of <paramref name="key" />, made if there is none; the idle ones are dropped and the bound kept first.</summary>
    private Series Touch(string key)
    {
        DateTimeOffset now = time.GetUtcNow();

        lock (seriesGate)
        {
            foreach (string idle in series.Where(x => now - x.Value.LastRead >= IdleFor).Select(static x => x.Key).ToList())
            {
                series.Remove(idle);
            }

            if (!series.TryGetValue(key, out Series? found))
            {
                if (series.Count >= MaxServers)
                {
                    series.Remove(series.MinBy(static x => x.Value.LastRead).Key);
                }

                series[key] = found = new Series();
            }

            found.LastRead = now;
            return found;
        }
    }

    /// <summary>One refresh of <paramref name="held" />: the whole window the first time and every <see cref="FullReadEvery" />, the new samples otherwise.</summary>
    private async Task<IReadOnlyList<PostgresConnectionSample>> RefreshAsync(Series held, string server)
    {
        DateTimeOffset now = time.GetUtcNow();
        DateTimeOffset oldest = now - Window;
        bool full;
        DateTimeOffset since;

        lock (held.Gate)
        {
            full = held.FullReadAt is not { } fullAt || now - fullAt >= FullReadEvery || held.Samples.Count == 0;
            since = full ? oldest : Max(oldest, held.Samples[^1].At - Overlap);
        }

        IReadOnlyList<PostgresConnectionSample> read;

        using (CancellationTokenSource limit = new(settings.QueryTimeout, time))
        {
            read = await store.ReadAsync(server, since, limit.Token).ConfigureAwait(false);
        }

        lock (held.Gate)
        {
            if (full)
            {
                held.Samples = [.. read.Where(x => x.At >= oldest)];
                held.FullReadAt = now;
                Order(held.Samples);
            }
            else
            {
                // The store answers from `since` on, so the samples held from there are the only ones it can repeat.
                HashSet<DateTimeOffset> known = [];

                for (int i = held.Samples.Count - 1; i >= 0 && held.Samples[i].At >= since; i--)
                {
                    known.Add(held.Samples[i].At);
                }

                DateTimeOffset last = held.Samples[^1].At;
                bool ordered = true;

                foreach (PostgresConnectionSample sample in read)
                {
                    if (sample.At >= oldest && known.Add(sample.At))
                    {
                        ordered &= sample.At >= last;
                        last = sample.At;
                        held.Samples.Add(sample);
                    }
                }

                if (!ordered)
                {
                    Order(held.Samples);
                }
            }

            int expired = 0;

            while (expired < held.Samples.Count && held.Samples[expired].At < oldest)
            {
                expired++;
            }

            held.Samples.RemoveRange(0, expired);
            return [.. held.Samples];
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static void Order(List<PostgresConnectionSample> samples) => samples.Sort(static (a, b) => a.At.CompareTo(b.At));

    /// <summary>One server's samples, oldest first, and when they were last read whole.</summary>
    private sealed class Series
    {
        public Lock Gate { get; } = new();

        public List<PostgresConnectionSample> Samples { get; set; } = [];

        public DateTimeOffset? FullReadAt { get; set; }

        /// <summary>Under the reader's gate, not this series'.</summary>
        public DateTimeOffset LastRead { get; set; }
    }

    /// <summary>
    /// Issue #46: <see cref="ReadAsync" />'s samples merged with <paramref name="pending" /> - the samples this process holds because
    /// the store refused their append - ordered by time. A sample the store holds too (an append that timed out here but landed) is
    /// counted once. When the store cannot be read, or took longer than <c>QueryTimeout</c>, and <paramref name="pending" /> is not
    /// empty, those alone are answered: the page shows what the process knows, not nothing.
    /// </summary>
    /// <param name="server">The server, <c>host:port</c> as the connection history keys it.</param>
    /// <param name="pending">The sampler's backlog of that server, oldest first; none when this process does not sample.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    public async Task<IReadOnlyList<PostgresConnectionSample>> ReadWithPendingAsync(
        string server,
        IReadOnlyList<PostgresConnectionSample> pending,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pending);

        if (pending.Count == 0)
        {
            return await ReadAsync(server, cancellationToken).ConfigureAwait(false);
        }

        DateTimeOffset since = time.GetUtcNow() - Window;
        IEnumerable<PostgresConnectionSample> held = pending.Where(x => x.At >= since);

        try
        {
            IReadOnlyList<PostgresConnectionSample> stored = await ReadAsync(server, cancellationToken).ConfigureAwait(false);
            HashSet<DateTimeOffset> known = [.. stored.Select(static x => x.At)];
            return [.. stored.Concat(held.Where(x => !known.Contains(x.At))).OrderBy(static x => x.At)];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return [.. held.OrderBy(static x => x.At)];
        }
    }
}
