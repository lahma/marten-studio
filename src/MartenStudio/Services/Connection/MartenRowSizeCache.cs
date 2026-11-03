using MartenStudio.Services.Health.Checks;

using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Connection;

/// <summary>
/// The one number per kind that the deeper checks hand the Connection page's <c>BUFFER</c> hint: the average size of an event
/// (E04's recent window) and of a document (T09's samples), kept for as long as the deeper sweep that measured it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a cache of its own.</b> The deeper tier's shared reads are kept for nobody (<c>HealthSweeper</c>): they hold stream ids,
/// event type names and the like, and a page that wanted a figure from them would hold all of it. The two checks record the one
/// aggregate - a database-wide mean of stored bytes, which E04's evidence "Stored in the window" already gives every visitor of the
/// report - and nothing else: no type, stream, tenant or table is kept.
/// </para>
/// <para>
/// <b>Bounded like every per-database state</b>: it registers with <see cref="HealthDatabaseBound" />, which forgets the least
/// recently used database past its capacity, and an entry older than <see cref="HealthSweepCache.DeeperTimeToLive" /> - the time the
/// deeper results themselves are shown - is not read. The page's Database gate decides who is shown the hint it feeds.
/// </para>
/// <para>
/// <b>Stored, not serialized.</b> <c>pg_column_size</c> is the size after TOAST compression, so the bytes Npgsql reads are at least
/// this many: the hint can only under-state a problem, never invent one.
/// </para>
/// </remarks>
internal sealed class MartenRowSizeCache : IPerDatabaseState
{
    /// <summary>The kind of the average event size.</summary>
    public const string Events = "events";

    /// <summary>The kind of the average document size.</summary>
    public const string Documents = "documents";

    private readonly Dictionary<string, Dictionary<string, (long Bytes, DateTimeOffset At)>> byDatabase = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider clock;
    private readonly Lock gate = new();

    /// <param name="bound">The process's bound on databases with state, or <see langword="null" /> for none, in a test.</param>
    /// <param name="clock">The clock, or <see langword="null" /> for the system's.</param>
    public MartenRowSizeCache(HealthDatabaseBound? bound = null, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        bound?.Register(this);
    }

    /// <inheritdoc />
    public void Forget(string storeKey, string databaseId)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);

        lock (gate)
        {
            byDatabase.Remove(KeyOf(storeKey, databaseId));
        }
    }

    /// <summary>How many databases hold a figure, for a test.</summary>
    internal int DatabaseCount
    {
        get
        {
            lock (gate)
            {
                return byDatabase.Count;
            }
        }
    }

    /// <summary>Remembers the average a deeper check measured, or forgets the kind's when it measured none.</summary>
    /// <param name="storeKey">The store.</param>
    /// <param name="databaseId">The database.</param>
    /// <param name="kind"><see cref="Events" /> or <see cref="Documents" />.</param>
    /// <param name="averageBytes">The average stored size in bytes, or <see langword="null" /> when the check had too little to judge.</param>
    public void Record(string storeKey, string databaseId, string kind, long? averageBytes)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);
        ArgumentNullException.ThrowIfNull(kind);

        lock (gate)
        {
            string key = KeyOf(storeKey, databaseId);

            if (averageBytes is not { } bytes)
            {
                if (byDatabase.TryGetValue(key, out Dictionary<string, (long, DateTimeOffset)>? kinds))
                {
                    kinds.Remove(kind);

                    if (kinds.Count == 0)
                    {
                        byDatabase.Remove(key);
                    }
                }

                return;
            }

            if (!byDatabase.TryGetValue(key, out Dictionary<string, (long, DateTimeOffset)>? held))
            {
                byDatabase[key] = held = new Dictionary<string, (long, DateTimeOffset)>(StringComparer.Ordinal);
            }

            held[kind] = (bytes, clock.GetUtcNow());
        }
    }

    /// <summary>
    /// The largest average the deeper checks measured for this database that is still within the time their results are shown, or
    /// <see langword="null" /> when none was.
    /// </summary>
    /// <param name="storeKey">The store.</param>
    /// <param name="databaseId">The database.</param>
    public long? Average(string storeKey, string databaseId)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);

        DateTimeOffset now = clock.GetUtcNow();
        long? largest = null;

        lock (gate)
        {
            if (byDatabase.TryGetValue(KeyOf(storeKey, databaseId), out Dictionary<string, (long Bytes, DateTimeOffset At)>? kinds))
            {
                foreach ((long bytes, DateTimeOffset at) in kinds.Values)
                {
                    if (now - at < HealthSweepCache.DeeperTimeToLive && (largest is null || bytes > largest))
                    {
                        largest = bytes;
                    }
                }
            }
        }

        return largest;
    }

    /// <summary>
    /// The window's total stored bytes over its events - E04's figure - or <see langword="null" /> below E04's minimum window.
    /// </summary>
    /// <param name="traffic">E04's shared read.</param>
    public static long? AverageEventBytes(TrafficProfile? traffic) =>
        traffic is { Events: >= LargeEventPayloadsCheck.MinimumWindow, Totals.Bytes: > 0 } window
            ? window.Totals.Bytes / window.Events
            : null;

    /// <summary>
    /// The largest per-table mean of the stored documents T09 sampled - only tables whose sample is large enough for T09 to judge.
    /// </summary>
    /// <param name="tables">T09's tables.</param>
    public static long? AverageDocumentBytes(IReadOnlyList<DocumentSizeFacts> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        long? largest = null;

        foreach (DocumentSizeFacts table in tables)
        {
            if (table.Sample is { Sampled: >= LargeDocumentsCheck.MinSampledRows, AverageBytes: { } mean })
            {
                long bytes = (long) Math.Round(mean);

                if (largest is null || bytes > largest)
                {
                    largest = bytes;
                }
            }
        }

        return largest;
    }

    private static string KeyOf(string storeKey, string databaseId) => storeKey + "|" + databaseId;
}
