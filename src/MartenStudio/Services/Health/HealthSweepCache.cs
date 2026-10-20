using System.Collections.Concurrent;

using MartenStudio.Services.Settings;
using MartenStudio.Services.Sources;

namespace MartenStudio.Services.Health;

/// <summary>
/// One sweep per database per interval, however many pages are watching - and only while one is.
/// </summary>
/// <remarks>
/// <para>
/// <b>No background work.</b> There is no timer here, no hosted service and nothing that runs on its own: a
/// sweep happens when <see cref="GetAsync" /> is called, and only the Health page and the Overview call it,
/// from their own polling loops, which stop when the page goes away (plan §3.2). A studio nobody has open
/// sweeps nothing; <see cref="SweepCount" /> is how a test proves it.
/// </para>
/// <para>
/// <b>Single flight, whatever the sweep's age.</b> Each store, database and kind has one slot: the sweep
/// running now, if any, and the last one that finished. A caller joins the running sweep however long it
/// has been running - a slow database must not get a second sweep started beside the first because the
/// first outlived the interval, which is what an entry that expires by its start time does - and otherwise
/// takes the finished one while it is fresh: <c>HealthInterval</c> for the regular sweep, five minutes for
/// the deeper and statement sweeps. The work runs under no caller's cancellation (D10); a caller's token
/// cancels only its own wait. The sweeper bounds a sweep's whole run, so a slot cannot stay busy for ever.
/// </para>
/// <para>
/// <b>Authorization comes before this, not inside it.</b> A key is the database and nothing else, so a
/// caller must have resolved the scope - which is where the store policy is asked - before it gets here;
/// what each visitor is then shown of a sweep is <see cref="HealthService" />'s per-visitor decision.
/// </para>
/// <para>
/// <b>The last sweep is kept for the navigation badge and the inline banners.</b> The sidebar polls every
/// <c>RefreshInterval</c> on every page, and a badge must cost nothing there - so it reads
/// <see cref="Latest" />, which is a dictionary lookup, and never starts a sweep. One slot per store,
/// database and kind ever swept, which is bounded by what visitors actually opened.
/// </para>
/// </remarks>
internal sealed class HealthSweepCache
{
    /// <summary>How long a deeper or statement sweep is reused for.</summary>
    public static readonly TimeSpan DeeperTimeToLive = TimeSpan.FromMinutes(5);

    private readonly HealthSweeper sweeper;
    private readonly IStudioSettings settings;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, Slot> slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private int sweepCount;

    public HealthSweepCache(HealthSweeper sweeper, IStudioSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sweeper);
        ArgumentNullException.ThrowIfNull(settings);

        this.sweeper = sweeper;
        this.settings = settings;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How many sweeps have actually started, for a test and for nothing else.</summary>
    internal int SweepCount => Volatile.Read(ref sweepCount);

    /// <summary>The time the cache judges freshness by.</summary>
    public DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>How long a finished sweep of <paramref name="kind" /> is reused for.</summary>
    public TimeSpan TimeToLive(HealthSweepKind kind) =>
        kind == HealthSweepKind.Regular ? settings.EffectiveHealthInterval : DeeperTimeToLive;

    /// <summary>
    /// The sweep of <paramref name="kind" /> for the resolved database: the running one if there is one, the
    /// finished one while it is fresh, otherwise a new one shared with everybody who asks while it runs.
    /// </summary>
    /// <param name="resolved">The database, resolved with no tenant by a visitor the store policy passed.</param>
    /// <param name="kind">Which tiers.</param>
    /// <param name="requestedDatabaseId">
    /// The database as the visitor's scope spelled it - often empty, meaning the store's default - so the
    /// badge, which knows only that spelling, finds this sweep again.
    /// </param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared sweep.</param>
    public async Task<HealthSweep> GetAsync(
        ResolvedSource resolved,
        HealthSweepKind kind,
        string? requestedDatabaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;
        string key = Key(storeKey, databaseId, kind);

        if (requestedDatabaseId is not null && !string.Equals(requestedDatabaseId, databaseId, StringComparison.OrdinalIgnoreCase))
        {
            aliases[Key(storeKey, requestedDatabaseId, kind)] = key;
        }

        Lazy<Task<HealthSweep>> work;

        lock (gate)
        {
            if (!slots.TryGetValue(key, out Slot? slot))
            {
                slot = new Slot();
                slots[key] = slot;
            }

            if (slot.Running is { } running)
            {
                work = running;
            }
            else if (slot.Completed is { } done && Now - slot.CompletedAt < TimeToLive(kind))
            {
                return done;
            }
            else
            {
                // Created under the lock, started outside it: a sweep of configuration checks completes
                // synchronously, and its completion takes this lock to record itself.
                Slot owner = slot;
                Lazy<Task<HealthSweep>>? created = null;
                created = new Lazy<Task<HealthSweep>>(
                    () => RunAsync(resolved, kind, owner, created!),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                slot.Running = created;
                work = created;
            }
        }

        return await work.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The last finished sweep of <paramref name="kind" /> for a database, or <see langword="null" />. A
    /// lookup, never a sweep.
    /// </summary>
    /// <param name="storeKey">The store.</param>
    /// <param name="databaseId">The database, as a scope spells it - an empty identity included.</param>
    /// <param name="kind">Which kind.</param>
    public HealthSweep? Latest(string storeKey, string databaseId, HealthSweepKind kind = HealthSweepKind.Regular)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);

        string key = Key(storeKey, databaseId, kind);

        lock (gate)
        {
            if (slots.TryGetValue(key, out Slot? slot) && slot.Completed is { } sweep)
            {
                return sweep;
            }

            return aliases.TryGetValue(key, out string? alias) && slots.TryGetValue(alias, out Slot? aliased)
                ? aliased.Completed
                : null;
        }
    }

    private async Task<HealthSweep> RunAsync(ResolvedSource resolved, HealthSweepKind kind, Slot slot, Lazy<Task<HealthSweep>> self)
    {
        Interlocked.Increment(ref sweepCount);

        // Off the caller's stack: the shared work is nobody's to cancel and nobody's to run inline.
        await Task.Yield();

        try
        {
            HealthSweep sweep = await sweeper.SweepAsync(resolved, kind, CancellationToken.None).ConfigureAwait(false);

            lock (gate)
            {
                slot.Completed = sweep;
                slot.CompletedAt = Now;
            }

            return sweep;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(slot.Running, self))
                {
                    slot.Running = null;
                }
            }
        }
    }

    private static string Key(string storeKey, string databaseId, HealthSweepKind kind) =>
        storeKey + "|" + databaseId + "|" + kind;

    /// <summary>One store, database and kind: the sweep running now, and the last one that finished.</summary>
    private sealed class Slot
    {
        public Lazy<Task<HealthSweep>>? Running { get; set; }

        public HealthSweep? Completed { get; set; }

        public DateTimeOffset CompletedAt { get; set; }
    }
}
