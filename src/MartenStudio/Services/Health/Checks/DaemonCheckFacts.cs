using System.Globalization;

using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

using Marten;
using Marten.Events.Projections;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>The daemon checks' thresholds: first values, written down, calibrated by the live tests.</summary>
/// <remarks>
/// A threshold becomes configurable only once its unit is honest (the <see cref="ProjectionLagThresholds" />
/// rule). These are times and multiples of the store's own polling settings, read off the store each sweep.
/// </remarks>
internal static class DaemonThresholds
{
    /// <summary>D01: a mark pinned behind a gap this long is a Warning. Marten itself holds a gap for <c>StaleSequenceThreshold</c> (3 s) before it even asks whether it is dead.</summary>
    public static readonly TimeSpan PinnedWarning = TimeSpan.FromMinutes(1);

    /// <summary>D01: ... and this long, Critical.</summary>
    public static readonly TimeSpan PinnedCritical = TimeSpan.FromMinutes(10);

    /// <summary>D02's floor: the least "nothing has been written for" window a running daemon is given.</summary>
    public static readonly TimeSpan MinimumQuiet = TimeSpan.FromMinutes(1);

    /// <summary>D02: no progression write for more than this many <c>SlowPollingTime</c>s, plus <see cref="MinimumQuiet" />.</summary>
    public const int DaemonQuietPolls = 2;

    /// <summary>D03: a shard that has not moved for this many <c>SlowPollingTime</c>s (and at least <see cref="ShardQuietFloor" />).</summary>
    public const int ShardQuietPolls = 5;

    /// <summary>
    /// D03's floor. A healthy shard is still for as long as one page takes, and a page of a slow projection - a
    /// handler that calls out, a large <c>BatchSize</c> - can take minutes, so a minute was a slow shard's false alarm.
    /// </summary>
    public static readonly TimeSpan ShardQuietFloor = TimeSpan.FromMinutes(5);

    /// <summary>D03: the window is at least this many times the shard's typical page, as the advisor saw it.</summary>
    public const int PageCalibrationMultiple = 3;

    /// <summary>
    /// D03's ceiling: however slow the shard's pages, the window is never longer than this - nor than half of what the
    /// advisor's history holds, or "ahead of the shard for the whole window" could never be seen and D03 would go blind.
    /// </summary>
    public static readonly TimeSpan ShardQuietCeiling = TimeSpan.FromMinutes(20);

    /// <summary>D04 and D06: the trailing window a trend is read over.</summary>
    public static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(10);

    /// <summary>D04 and D06: the shortest span two samples must cover before a trend is believed.</summary>
    public static readonly TimeSpan MinimumWatch = TimeSpan.FromMinutes(1);

    /// <summary>
    /// D02's window for a store - and the window every daemon check gives a running high-water agent to write: a
    /// poll is <c>SlowPollingTime</c> while idle, so two of them and a minute is a daemon that is not writing.
    /// </summary>
    public static TimeSpan DaemonQuiet(TimeSpan slowPollingTime) => slowPollingTime * DaemonQuietPolls + MinimumQuiet;

    /// <summary>
    /// D03's window for a shard: <c>max(5 x SlowPollingTime, 5 min, 3 x its typical page)</c>, and never more than
    /// <c>min(20 min, half the history's span)</c> - calibrated by the shard's own cadence where the history has one,
    /// floored where it has not, and capped so that the window can always be watched whole.
    /// </summary>
    /// <param name="slowPollingTime">The store's <c>SlowPollingTime</c>.</param>
    /// <param name="healthInterval">The advisor's sampling interval (<c>EffectiveHealthInterval</c>), which sets how long its history spans.</param>
    /// <param name="typicalPage">The median page the advisor saw this shard take (<see cref="ProjectionTrend.TypicalPage" />), if it saw one.</param>
    public static TimeSpan ShardQuiet(TimeSpan slowPollingTime, TimeSpan healthInterval, TimeSpan? typicalPage = null)
    {
        TimeSpan window = slowPollingTime * ShardQuietPolls;

        if (window < ShardQuietFloor)
        {
            window = ShardQuietFloor;
        }

        if (typicalPage is { } page && page * PageCalibrationMultiple > window)
        {
            window = page * PageCalibrationMultiple;
        }

        TimeSpan ceiling = ShardQuietCeilingFor(healthInterval);
        return window > ceiling ? ceiling : window;
    }

    /// <summary>The most D03's window may be for a history sampled every <paramref name="healthInterval" />.</summary>
    public static TimeSpan ShardQuietCeilingFor(TimeSpan healthInterval)
    {
        TimeSpan half = healthInterval * HealthHistory.Capacity / 2;
        return half < ShardQuietCeiling ? half : ShardQuietCeiling;
    }
}

/// <summary>The history series the daemon checks write and read (<see cref="HealthHistory" />).</summary>
/// <remarks>
/// One writer per series, as the sweep requires: D01 writes the sequence, the mark and whether a committed event
/// waits behind it, D03 the shard positions and per-row thresholds, D05 where each shard it saw failed or paused stood
/// and how, D06 the dead-letter counts. D04 and the projections screen only read. Every writer is a
/// <see cref="HealthGate.Database" /> check, so every series here is read at that gate.
/// </remarks>
internal static class DaemonSeries
{
    /// <summary>The event sequence's handed-out ceiling - the number the projections screen measures lag against.</summary>
    public const string HighestSequence = "daemon.highest-sequence";

    /// <summary>The store-global high-water mark.</summary>
    public const string HighWaterMark = "daemon.high-water-mark";

    /// <summary>
    /// The mark a committed event was waiting behind, pinned by a gap, when this sample was taken - or
    /// <see cref="NotWaiting" />. Written every time D01 reads, so "no value" means "not read", never "not waiting".
    /// </summary>
    public const string WaitingMark = "daemon.waiting-mark";

    /// <summary><see cref="WaitingMark" />'s value when nothing committed waits behind a gap.</summary>
    public const double NotWaiting = -1;

    /// <summary>A shard's position, by identity.</summary>
    public static string ShardPosition(string shard) => "daemon.shard-position:" + shard;

    /// <summary>A shard's per-row warning threshold, when its progression row carries one.</summary>
    public static string ShardWarningThreshold(string shard) => "daemon.shard-warning-threshold:" + shard;

    /// <summary>A shard's per-row critical threshold, when its progression row carries one.</summary>
    public static string ShardCriticalThreshold(string shard) => "daemon.shard-critical-threshold:" + shard;

    /// <summary>A projection's dead-letter count.</summary>
    public static string DeadLetters(string projection) => "daemon.dead-letters:" + projection;

    /// <summary>The prefix every dead-letter series shares.</summary>
    public const string DeadLettersPrefix = "daemon.dead-letters:";

    /// <summary>
    /// D05: the position a shard stood at when a sweep saw it failed or paused - what a later sweep, which may land after
    /// the coordinator has restarted it and before it fails again, when its agent reads Running, judges its progress by.
    /// </summary>
    public static string ShardFailedAt(string shard) => "daemon.shard-failed-at:" + shard;

    /// <summary>D05: when that pause began, in Unix milliseconds of the daemon's own clock - a later pause is a later attempt.</summary>
    public static string ShardPausedAt(string shard) => "daemon.shard-paused-at:" + shard;

    /// <summary>D05: how it failed, as the number of its <c>ShardFailureCategory</c>, so that a finding made from memory names its remedy.</summary>
    public static string ShardFailureCategory(string shard) => "daemon.shard-failure-category:" + shard;
}

/// <summary>What the store's configuration says about its daemon - configuration only, no connection.</summary>
/// <param name="EventSchema">The event store's schema.</param>
/// <param name="TenantPartitionedEvents"><c>Events.UseTenantPartitionedEvents</c>.</param>
/// <param name="ExtendedProgressionTracking"><c>Events.EnableExtendedProgressionTracking</c>.</param>
/// <param name="AsyncMode">The daemon mode this process was configured with.</param>
/// <param name="SlowPollingTime">How often an idle high-water agent polls.</param>
/// <param name="StaleSequenceThreshold">How long the detector waits on a gap before it asks whether it is dead.</param>
/// <param name="StopAndDrainTimeout">How long a stopping shard may take to finish the page it is processing.</param>
/// <param name="DaemonLockId">The base of every leadership lock id.</param>
/// <param name="SkipStaleGapsCap"><c>Projections.SkipStaleGapsDespiteLiveTransactionsAfter</c>.</param>
/// <param name="UsesTransactionEvidence"><c>Projections.UseTransactionEvidenceForGapSkipping</c>.</param>
/// <param name="HasAsyncWork">Whether the store registers an async projection or a subscription.</param>
/// <param name="AsyncShards">Every async shard the daemon would run, by identity.</param>
/// <param name="LockKeys">Every leadership lock id this store's daemon could hold, whichever distributor runs it.</param>
/// <remarks>
/// <b>No heartbeat.</b> <c>mt_event_progression.heartbeat</c> is written when a shard's status changes, and JasperFx
/// documents it so; it is never read as a sign of life here, as K02 does not read it as one.
/// </remarks>
internal sealed record DaemonCheckConfiguration(
    string EventSchema,
    bool TenantPartitionedEvents,
    bool ExtendedProgressionTracking,
    DaemonMode AsyncMode,
    TimeSpan SlowPollingTime,
    TimeSpan StaleSequenceThreshold,
    TimeSpan StopAndDrainTimeout,
    int DaemonLockId,
    TimeSpan? SkipStaleGapsCap,
    bool UsesTransactionEvidence,
    bool HasAsyncWork,
    IReadOnlyList<string> AsyncShards,
    IReadOnlyList<long> LockKeys)
{
    /// <summary>
    /// What a check says, as its "not checked" reason, about a store that partitions its events by tenant: the
    /// high-water bookkeeping there is per tenant, Marten keeps no durable stuck-gap clock for it, and the store-global
    /// rows these checks read are not maintained.
    /// </summary>
    public const string TenantPartitionedReason =
        "This store partitions its events by tenant (Events.UseTenantPartitionedEvents), where every tenant has a high-water "
        + "mark of its own and Marten keeps no durable record of when one was pinned. The store-global high-water checks do "
        + "not apply to it.";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Type>> NoPublishedTypes =
        new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> NoHiddenProjections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The document types each projection writes, by projection name: what a finding that names a projection carries
    /// as its <see cref="HealthFinding.DocumentTypes" />, so a projection of a hidden type is never named.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Type>> PublishedTypes { get; init; } = NoPublishedTypes;

    /// <summary>
    /// The projections that write a document type <c>IsDocumentTypeVisible</c> hides - read at gather time, so an
    /// evaluator's prose (a note, not a finding) can leave them out too.
    /// </summary>
    public IReadOnlySet<string> HiddenProjections { get; init; } = NoHiddenProjections;

    /// <summary>
    /// The advisor's sampling interval (<c>EffectiveHealthInterval</c>) - how far apart its samples are, and so how long
    /// its history of <see cref="HealthHistory.Capacity" /> samples spans. D03 caps its window by it.
    /// </summary>
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The document types the projection behind <paramref name="shard" /> writes.</summary>
    public IReadOnlyList<Type> PublishedTypesOf(string shard) =>
        PublishedTypes.GetValueOrDefault(ProjectionOf(shard)) ?? [];

    /// <summary>Whether the projection behind <paramref name="shard" /> writes a hidden document type.</summary>
    public bool IsHidden(string shard) => HiddenProjections.Contains(ProjectionOf(shard));

    /// <summary>A shard's projection: <c>DailySales:All</c> and <c>DailySales:All:acme</c> are both <c>DailySales</c>.</summary>
    internal static string ProjectionOf(string shard)
    {
        int separator = shard.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 ? shard[..separator] : shard;
    }

    /// <summary>Reads the configuration off the context's store.</summary>
    public static DaemonCheckConfiguration Read(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyStoreOptions options = context.StoreOptions;
        IReadOnlyDaemonSettings daemon = options.Events.Daemon;
        string schema = options.Events.DatabaseSchemaName;

        int lockId = DaemonLockIdOf(options);
        ProjectionOptions? projections = daemon as ProjectionOptions;

        IReadOnlyList<ShardName> shards = AsyncShardsOf(context.Store);
        IReadOnlyDictionary<string, IReadOnlyList<Type>> published = PublishedTypesOf(context.Store);

        return new DaemonCheckConfiguration(
            schema,
            options.Events.UseTenantPartitionedEvents,
            options.Events.EnableExtendedProgressionTracking,
            daemon.AsyncMode,
            daemon.SlowPollingTime,
            daemon.StaleSequenceThreshold,
            daemon.StopAndDrainTimeout,
            lockId,
            projections?.SkipStaleGapsDespiteLiveTransactionsAfter,
            projections?.UseTransactionEvidenceForGapSkipping ?? true,
            DaemonAccessor.HasAsyncWork(context.Store),
            [.. shards.Select(static x => x.Identity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            LockKeysFor(schema, shards, lockId))
        {
            HealthInterval = context.StudioOptions.EffectiveHealthInterval,
            PublishedTypes = published,
            HiddenProjections = new HashSet<string>(
                published.Where(x => x.Value.Any(type => !context.IsDocumentTypeVisible(type))).Select(static x => x.Key),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// What every registered projection writes (<c>IProjectionSource.PublishedTypes()</c>), by projection name. A
    /// projection that will not describe itself writes nothing as far as this knows - and a subscription writes nothing.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<Type>> PublishedTypesOf(IDocumentStore store)
    {
        Dictionary<string, IReadOnlyList<Type>> published = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (ISubscriptionSource source in store.Options.Events.Projections())
            {
                published[source.Name] = ProjectionDataService.PublishedTypesOf(source);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A store that cannot list its projections names none of them either.
        }

        return published;
    }

    /// <summary>JasperFx's default <c>DaemonLockId</c>, for a settings object of another shape.</summary>
    internal const int DefaultDaemonLockId = 4444;

    /// <summary>The store's <c>Projections.DaemonLockId</c>: the base of every leadership lock id.</summary>
    internal static int DaemonLockIdOf(IReadOnlyStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Events.Daemon is DaemonSettings settings ? settings.DaemonLockId : DefaultDaemonLockId;
    }

    /// <summary>
    /// The lock ids, read at JasperFx V2.74.0 and V2.87.1 (byte-identical distributors): <c>DaemonLockId</c> itself
    /// (<c>MultiTenantedProjectionDistributor</c>, HotCold over several databases) and
    /// <c>ProjectionLockIds.Compute(EventGraph.DatabaseSchemaName, shard, DaemonLockId)</c> per shard
    /// (<c>SingleTenantProjectionDistributor</c>, HotCold over one). <c>SoloProjectionDistributor</c> takes no lock.
    /// The one definition of the set: D02 and D01's Explain read it here, and so do C04 and the lock labels of
    /// Database › Sessions and C06 (<c>SessionLockLabels</c>).
    /// </summary>
    internal static IReadOnlyList<long> LockKeysFor(string eventSchema, IEnumerable<ShardName> shards, int daemonLockId)
    {
        HashSet<long> keys = [daemonLockId, .. ShardLockIdsFor(eventSchema, shards, daemonLockId).Select(static x => x.Id)];
        return [.. keys.Order()];
    }

    /// <summary>
    /// Each shard's own lock id - <c>ProjectionLockIds.Compute(eventSchema, shard, DaemonLockId)</c> - with the shard it
    /// is for, so that a label can say whose lock it is. <see cref="LockKeysFor" /> is these and <c>DaemonLockId</c>.
    /// </summary>
    internal static IReadOnlyList<(long Id, ShardName Shard)> ShardLockIdsFor(string eventSchema, IEnumerable<ShardName> shards, int daemonLockId)
    {
        ArgumentNullException.ThrowIfNull(eventSchema);
        ArgumentNullException.ThrowIfNull(shards);

        List<(long Id, ShardName Shard)> ids = [];

        foreach (ShardName shard in shards)
        {
            try
            {
                ids.Add((ProjectionLockIds.Compute(eventSchema, shard, daemonLockId), shard));
            }
            catch (OverflowException)
            {
                // Math.Abs(int.MinValue): the one hash JasperFx cannot turn into an id, and so a lock nobody holds.
            }
        }

        return ids;
    }

    /// <summary>
    /// Every async shard - projections and subscriptions - from <c>ProjectionOptions.AllShards()</c>, the list the
    /// daemon starts from; for an options object of another shape, the async projections' own shards.
    /// </summary>
    internal static IReadOnlyList<ShardName> AsyncShardsOf(IDocumentStore store)
    {
        try
        {
            if (store.Options is StoreOptions concrete)
            {
                return [.. concrete.Projections.AllShards().Select(static x => x.Name)];
            }

            List<ShardName> shards = [];
            foreach (var source in store.Options.Events.Projections().Where(static x => x.Lifecycle == ProjectionLifecycle.Async))
            {
                shards.AddRange(source.ShardNames());
            }

            return shards;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>Evidence lines every daemon finding can carry about the settings it was judged against.</summary>
    public HealthEvidence SlowPollingEvidence() =>
        new("Projections.SlowPollingTime", Format(SlowPollingTime));

    /// <summary>A time span for a person: "45 s", "12 min", "3 h 5 min".</summary>
    internal static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span < TimeSpan.FromSeconds(1))
        {
            return span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        if (span < TimeSpan.FromMinutes(1))
        {
            return span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return ((int) span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        }

        if (span < TimeSpan.FromDays(2))
        {
            return ((int) span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h "
                + span.Minutes.ToString(CultureInfo.InvariantCulture) + " min";
        }

        return ((int) span.TotalDays).ToString(CultureInfo.InvariantCulture) + " days";
    }

    /// <summary>A count for a person.</summary>
    internal static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>The high-water reading: one snapshot of the mark, its bookkeeping and the sequence.</summary>
/// <param name="ServerNow">The server's <c>transaction_timestamp()</c> - every age below is measured against it.</param>
/// <param name="HighestSequence">The sequence's handed-out ceiling (0 for a sequence nothing drew from).</param>
/// <param name="Mark">The store-global high-water mark, or <see langword="null" /> before the daemon ever wrote one.</param>
/// <param name="MarkUpdated">The mark row's <c>last_updated</c>.</param>
/// <param name="StuckMark">The stuck-gap row's mark.</param>
/// <param name="StuckSince">When the daemon first saw that mark pinned.</param>
/// <param name="FenceMark">The allocation fence's sequence.</param>
/// <param name="FenceUpdated">When the fence was last restamped - while caught up, every high-water poll.</param>
/// <param name="LatestWrite">The newest <c>last_updated</c> of any progression row.</param>
/// <param name="NextCommitted">The first committed sequence above the mark, or <see langword="null" /> for none.</param>
internal sealed record HighWaterReading(
    DateTimeOffset ServerNow,
    long HighestSequence,
    long? Mark,
    DateTimeOffset? MarkUpdated,
    long? StuckMark,
    DateTimeOffset? StuckSince,
    long? FenceMark,
    DateTimeOffset? FenceUpdated,
    DateTimeOffset? LatestWrite,
    long? NextCommitted)
{
    /// <summary>
    /// The highest number the sequence has handed out, as Marten itself reads it (#5091's <c>AllocatedSequenceHigh</c>):
    /// <c>last_value</c> once called, <c>last_value - 1</c> before - so a sequence restarted at <c>n</c> reads
    /// <c>n - 1</c>, and the next append takes <c>AllocatedHigh + 1</c>. <see langword="null" /> for a reading that
    /// predates it. D09's.
    /// </summary>
    public long? AllocatedHigh { get; init; }

    /// <summary>
    /// The highest committed <c>seq_id</c> in <c>mt_events</c>, or <see langword="null" /> for an empty table (or a reading
    /// that predates it). D09's.
    /// </summary>
    public long? HighestCommitted { get; init; }

    /// <summary>
    /// Whether the stuck-gap row describes the mark as it is now. Marten upserts it only when the mark differs and
    /// never deletes it (V9.39.0, V9.50.2), so a row whose mark is behind the mark's own row is an old pin, long gone.
    /// </summary>
    public bool StuckRowIsCurrent => Mark is { } mark && StuckMark == mark && StuckSince is not null;

    /// <summary>
    /// Whether the numbers just above the mark were handed out and are not committed: the next committed event is
    /// further on, or there is none and the sequence has handed out more.
    /// </summary>
    public bool GapAboveMark => Mark is { } mark
        && HighestSequence > mark
        && (NextCommitted is null || NextCommitted > mark + 1);

    /// <summary>
    /// The mark is pinned behind a gap, as Marten itself recorded it - whether or not anything committed waits above
    /// it. What D02 stands down on and what the Explain asks about; D01 asks the narrower
    /// <see cref="IsHoldingCommittedEvents" />.
    /// </summary>
    public bool IsPinnedBehindGap => StuckRowIsCurrent && GapAboveMark;

    /// <summary>
    /// The pin is holding something back: a committed event is waiting above the gap. With nothing committed above it,
    /// the numbers are handed out and not committed - a long append on an idle store, a rolled-back append, a tail gap
    /// Marten's legacy skipping never skips - and nothing is late because of it. Marten records that pin too
    /// (<c>GapDetector</c> returns the mark itself and <c>trackStuckGap</c> upserts <c>HighWaterStuckGap</c>, V9.39.0
    /// and V9.50.2), so the stuck-gap row alone is not a delay.
    /// </summary>
    public bool IsHoldingCommittedEvents => IsPinnedBehindGap && NextCommitted is not null;

    /// <summary>How long the mark has been pinned, in server time, when it is.</summary>
    public TimeSpan? PinnedFor => IsPinnedBehindGap && StuckSince is { } since ? ServerNow - since : null;

    /// <summary>The gap's range: from just above the mark to just below the next committed event (or the ceiling).</summary>
    public (long From, long To)? GapRange => IsPinnedBehindGap && Mark is { } mark
        ? (mark + 1, NextCommitted is { } next ? next - 1 : HighestSequence)
        : null;

    /// <summary>How long ago anything at all was written to the progression table, in server time.</summary>
    public TimeSpan? SinceLatestWrite => LatestWrite is { } latest ? ServerNow - latest : null;

    /// <summary>How long ago the mark's row was written.</summary>
    public TimeSpan? SinceMarkUpdated => MarkUpdated is { } updated ? ServerNow - updated : null;

    /// <summary>How long ago the allocation fence was restamped.</summary>
    public TimeSpan? SinceFenceUpdated => FenceUpdated is { } updated ? ServerNow - updated : null;

    /// <summary>
    /// Reads it - once per sweep, whichever daemon check asks first, so D01, D02 and D03 judge one snapshot. The shape must
    /// have <see cref="EventStoreRelations.HasHighWater" />.
    /// </summary>
    public static Task<HighWaterReading> ReadAsync(HealthCheckContext context, string eventSchema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        return context.Shared("daemon.high-water:" + eventSchema, async token =>
        {
            IReadOnlyList<HighWaterReading> rows = await context.Sql.QueryAsync(
                    HealthDaemonQueries.HighWater(eventSchema),
                    static reader => new HighWaterReading(
                        reader.GetFieldValue<DateTimeOffset>(0),
                        reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                        NullableLong(reader, 2),
                        NullableTime(reader, 3),
                        NullableLong(reader, 4),
                        NullableTime(reader, 5),
                        NullableLong(reader, 6),
                        NullableTime(reader, 7),
                        NullableTime(reader, 8),
                        NullableLong(reader, 9))
                    {
                        AllocatedHigh = NullableLong(reader, 10),
                        HighestCommitted = NullableLong(reader, 11),
                    },
                    token)
                .ConfigureAwait(false);

            return rows.Count == 1
                ? rows[0]
                : throw new InvalidOperationException(
                    "The high-water statement returns exactly one row; it returned "
                    + rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        });
    }

    internal static long? NullableLong(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    internal static DateTimeOffset? NullableTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}

/// <summary>One progression row, as the daemon checks read it.</summary>
/// <param name="Name">The progression name (<c>ShardName.Identity</c>).</param>
/// <param name="Sequence">Its position.</param>
/// <param name="LastUpdated">When it last advanced (Marten stamps it on every progress write, #4981).</param>
/// <param name="Heartbeat">Extended tracking: the last heartbeat, or the last status change when no periodic beat is written.</param>
/// <param name="AgentStatus">Extended tracking: Running, Paused or Stopped, as the node that ran it last wrote.</param>
/// <param name="PauseReason">Extended tracking: why it was paused or stopped.</param>
/// <param name="FailureCategory">Extended tracking: the classified failure (<c>ShardFailureCategory</c>), when one stopped it.</param>
/// <param name="FailureEventSequence">The event it failed on, when one could be blamed.</param>
/// <param name="FailureEventType">That event's type.</param>
/// <param name="WarningThreshold">A per-row warning threshold, when somebody wrote one (Marten does not, #5173).</param>
/// <param name="CriticalThreshold">A per-row critical threshold, likewise.</param>
internal sealed record DaemonShardRow(
    string Name,
    long Sequence,
    DateTimeOffset? LastUpdated,
    DateTimeOffset? Heartbeat = null,
    string? AgentStatus = null,
    string? PauseReason = null,
    string? FailureCategory = null,
    long? FailureEventSequence = null,
    string? FailureEventType = null,
    long? WarningThreshold = null,
    long? CriticalThreshold = null)
{
    /// <summary>Whether the persisted status says it is not running.</summary>
    public bool IsNotRunning => AgentStatus is { Length: > 0 } status && !string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the persisted status says it is paused.</summary>
    public bool IsPaused => string.Equals(AgentStatus, "Paused", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a failure is recorded on it.</summary>
    public bool HasFailure => FailureCategory is { Length: > 0 };

    /// <summary>
    /// The registered shards' rows (<see cref="HealthDaemonQueries.ShardRows" />) - once per sweep for each way of asking,
    /// whichever check asks first, so D03 and D09 judge one snapshot of the shards.
    /// </summary>
    public static Task<IReadOnlyList<DaemonShardRow>> ReadAsync(
        HealthCheckContext context,
        EventStoreRelations shape,
        DaemonCheckConfiguration configuration,
        bool extendedProgressionTracking,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        if (configuration.AsyncShards.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<DaemonShardRow>>([]);
        }

        IReadOnlyList<string> selected = HealthDaemonQueries.SelectedShardColumns(shape.ProgressionColumns, extendedProgressionTracking);

        return context.Shared(
            "daemon.shard-rows:" + configuration.EventSchema + (extendedProgressionTracking ? ":extended" : ":plain"),
            token => context.Sql.QueryAsync(
                HealthDaemonQueries.ShardRows(configuration.EventSchema, shape.ProgressionColumns, extendedProgressionTracking, configuration.AsyncShards),
                reader => Read(reader, selected),
                token));
    }

    /// <summary>Every row the extended tracking says is not running or carries a failure (<see cref="HealthDaemonQueries.ProblemShardRows" />).</summary>
    public static Task<IReadOnlyList<DaemonShardRow>> ReadProblemsAsync(
        HealthCheckContext context,
        EventStoreRelations shape,
        string eventSchema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shape);

        IReadOnlyList<string> selected = HealthDaemonQueries.SelectedShardColumns(shape.ProgressionColumns, extendedProgressionTracking: true);
        var query = HealthDaemonQueries.ProblemShardRows(eventSchema, shape.ProgressionColumns);

        return context.Sql.QueryAsync(query, reader => Read(reader, selected), cancellationToken);
    }

    private static DaemonShardRow Read(NpgsqlDataReader reader, IReadOnlyList<string> selected)
    {
        DaemonShardRow row = new(
            reader.GetString(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            HighWaterReading.NullableTime(reader, 2));

        for (int i = 0; i < selected.Count; i++)
        {
            int ordinal = i + 3;

            if (reader.IsDBNull(ordinal))
            {
                continue;
            }

            row = selected[i] switch
            {
                "heartbeat" => row with { Heartbeat = reader.GetFieldValue<DateTimeOffset>(ordinal) },
                "agent_status" => row with { AgentStatus = reader.GetString(ordinal) },
                "pause_reason" => row with { PauseReason = reader.GetString(ordinal) },
                "failure_category" => row with { FailureCategory = reader.GetString(ordinal) },
                "failure_event_sequence" => row with { FailureEventSequence = reader.GetInt64(ordinal) },
                "failure_event_type" => row with { FailureEventType = reader.GetString(ordinal) },
                "warning_behind_threshold" => row with { WarningThreshold = reader.GetInt64(ordinal) },
                "critical_behind_threshold" => row with { CriticalThreshold = reader.GetInt64(ordinal) },
                _ => row,
            };
        }

        return row;
    }
}

/// <summary>One agent the in-process daemon runs.</summary>
/// <param name="ShardName">Its shard identity.</param>
/// <param name="Status">Running, Paused or Stopped.</param>
/// <param name="FailureCategory">The failure that paused or stopped it, as JasperFx classifies it.</param>
/// <param name="FailureMessage">That failure's message.</param>
/// <param name="PausedAt">When it was paused.</param>
/// <param name="Position">
/// <c>ISubscriptionAgent.Position</c>: the last sequence it committed. An agent the coordinator has just built reads 0
/// until its start has been processed, and then the floor it resumed from.
/// </param>
/// <param name="FailureEventSequence">The event its failure was attributed to (<c>ShardFailure.Event</c>), when there is one.</param>
/// <param name="FailureEventType">That event's type.</param>
/// <param name="SystemicDatabaseFailure">
/// Whether its failure is a database error in projection code that JasperFx never dead-letters (JasperFx.Events 2.93,
/// Marten 9.52 - <c>DaemonLightUp.IsSystemicDatabaseFailure</c>): <see langword="null" /> when that cannot be told, which
/// below 2.93 is always.
/// </param>
/// <param name="FailureRootExceptionType">The failure's innermost exception type (<c>ShardFailure.RootExceptionType</c>).</param>
internal sealed record DaemonAgentFact(
    string ShardName,
    string Status,
    string? FailureCategory,
    string? FailureMessage,
    DateTimeOffset? PausedAt,
    long? Position = null,
    long? FailureEventSequence = null,
    string? FailureEventType = null,
    bool? SystemicDatabaseFailure = null,
    string? FailureRootExceptionType = null);

/// <summary>What this process knows about the daemon: whether it hosts one, and what that one is doing.</summary>
/// <param name="Hosting">The accessor's answer.</param>
/// <param name="IsRunning">Whether a daemon hosted here says it is running.</param>
/// <param name="Agents">Its agents.</param>
/// <param name="Rebuilding">
/// The shards it says it is rebuilding right now (JasperFx 2.86+, through F1). Only a <see langword="true" /> from
/// <c>IsRebuilding</c> counts: the default interface method answers <see langword="false" /> for a daemon that does
/// not track rebuilds, so an empty list proves nothing.
/// </param>
/// <param name="PausedByStudio">The coordinator pause this studio recorded for the store, if any.</param>
/// <param name="StartRefusals">The start refusals the studio recorded for this database, by shard.</param>
/// <param name="StudioRebuilds">The projections Marten Studio is rebuilding in this database right now.</param>
/// <param name="LiveExceptions">
/// What the in-process shard tracker last published as a shard's exception, by shard - present only while some page
/// watches the tracker; used for JasperFx's transient/permanent classification where the feature exists.
/// </param>
internal sealed record DaemonInProcess(
    DaemonHostingState Hosting,
    bool IsRunning,
    IReadOnlyList<DaemonAgentFact> Agents,
    IReadOnlyList<string> Rebuilding,
    StudioDaemonPause? PausedByStudio,
    IReadOnlyDictionary<string, StudioStartRefusal> StartRefusals,
    IReadOnlyList<string>? StudioRebuilds = null,
    IReadOnlyDictionary<string, Exception>? LiveExceptions = null)
{
    /// <summary>Nothing hosted here, and nothing recorded.</summary>
    public static DaemonInProcess NotHosted { get; } = new(
        DaemonHostingState.NotHostedInThisProcess, false, [], [], null, new Dictionary<string, StudioStartRefusal>());

    /// <summary>
    /// When this process started - the latest a daemon hosted in it can have started from - or <see langword="null" />
    /// when the platform will not say. Read off the process, so it is the host's restart that is measured, not the
    /// studio's first look.
    /// </summary>
    public DateTimeOffset? ProcessStartedAt { get; init; }

    /// <summary>A daemon is hosted in this process and says it is running.</summary>
    public bool IsHealthyHere => Hosting == DaemonHostingState.Hosted && IsRunning;

    /// <summary>
    /// A daemon is hosted here and its process started less than <paramref name="window" /> before
    /// <paramref name="now" />: it may not have taken the leadership or written its first progress yet.
    /// </summary>
    public bool IsStartingHere(DateTimeOffset now, TimeSpan window) =>
        Hosting == DaemonHostingState.Hosted && ProcessStartedAt is { } started && now - started < window;

    /// <summary>The agent's status here is something other than Running.</summary>
    public bool IsNotRunningHere(string shard) =>
        Agent(shard) is { } agent && !string.Equals(agent.Status, "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>This process's start, read once.</summary>
    private static readonly Lazy<DateTimeOffset?> ThisProcessStarted = new(static () =>
    {
        if (OperatingSystem.IsBrowser())
        {
            // The studio is Blazor Server only (D2); a browser has no process to ask.
            return null;
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    });

    /// <summary>The agent for a shard, when the daemon here runs it.</summary>
    public DaemonAgentFact? Agent(string shard) =>
        Agents.FirstOrDefault(x => string.Equals(x.ShardName, shard, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a shard's projection is known to be being rebuilt: by the daemon here (JasperFx 2.86+), or by Marten
    /// Studio. Not knowing is not "no".
    /// </summary>
    public bool IsKnownRebuilding(string shard)
    {
        if (Rebuilding.Contains(shard, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        string projection = ShardName.TryParse(shard, out ShardName? name) && name is not null ? name.Name : shard;
        return (StudioRebuilds ?? []).Contains(projection, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads it: the accessor (never a daemon of the studio's own - hard rule 11), the daemon's own answers read
    /// defensively, and the studio's recorded pause and start refusals.
    /// </summary>
    public static Task<DaemonInProcess> ReadAsync(HealthCheckContext context, IReadOnlyList<string> shards, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(shards);
        cancellationToken.ThrowIfCancellationRequested();

        // Once per sweep: D02, D03 and D05 all ask, and every one of them asks with the store's async shards.
        return context.Shared("daemon.in-process", token => ReadOnceAsync(context, shards, token));
    }

    private static async Task<DaemonInProcess> ReadOnceAsync(HealthCheckContext context, IReadOnlyList<string> shards, CancellationToken cancellationToken)
    {
        DaemonControlState? control = context.Services.GetService<DaemonControlState>();
        StudioDaemonPause? pause = control?.Find(context.StoreKey);
        IReadOnlyDictionary<string, StudioStartRefusal> refusals =
            control?.StartRefusals(context.StoreKey, context.DatabaseId) ?? new Dictionary<string, StudioStartRefusal>();

        IReadOnlyList<string> studioRebuilds =
        [
            .. (context.Services.GetService<StudioOperationTracker>()?.RunningFor(context.StoreKey, context.DatabaseId) ?? [])
                .Where(static x => string.Equals(x.Kind, "Rebuild", StringComparison.Ordinal))
                .Select(static x => x.Target),
        ];

        Dictionary<string, Exception> exceptions = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string shard, ShardState state) in context.Services.GetService<StudioLiveState>()?.Latest(context.StoreKey, context.DatabaseId)
            ?? new Dictionary<string, ShardState>())
        {
            if (state.Exception is { } exception)
            {
                exceptions[shard] = exception;
            }
        }

        DaemonAccessor? accessor = context.Services.GetService<DaemonAccessor>();
        if (accessor is null)
        {
            return NotHosted with { PausedByStudio = pause, StartRefusals = refusals, StudioRebuilds = studioRebuilds, LiveExceptions = exceptions };
        }

        DaemonHosting hosting = await SweptDaemon.HostingAsync(context, accessor).ConfigureAwait(false);

        if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
        {
            return new DaemonInProcess(hosting.State, false, [], [], pause, refusals, studioRebuilds, exceptions)
            {
                ProcessStartedAt = hosting.State == DaemonHostingState.Hosted ? ThisProcessStarted.Value : null,
            };
        }

        bool running = Read(() => daemon.IsRunning, false);

        List<DaemonAgentFact> agents = [];
        foreach (ISubscriptionAgent agent in Read<IReadOnlyList<ISubscriptionAgent>>(daemon.CurrentAgents, []))
        {
            ShardFailure? failure = Read(() => agent.Failure, null);

            agents.Add(new DaemonAgentFact(
                Read(() => agent.Name?.Identity, null) ?? "(unnamed)",
                Read(() => agent.Status.ToString(), "Unknown"),
                failure?.Category.ToString(),
                failure?.Message,
                Read(() => agent.PausedTime, null),
                Read<long?>(() => agent.Position, null),
                failure?.Event?.Sequence,
                failure?.Event?.EventTypeName,
                Read(() => DaemonLightUp.IsSystemicDatabaseFailure(failure), null),
                failure?.RootExceptionType));
        }

        List<string> rebuilding = [];
        foreach (string shard in shards)
        {
            if (ShardName.TryParse(shard, out ShardName? name) && name is not null && DaemonLightUp.IsRebuilding(daemon, name))
            {
                rebuilding.Add(shard);
            }
        }

        return new DaemonInProcess(DaemonHostingState.Hosted, running, agents, rebuilding, pause, refusals, studioRebuilds, exceptions)
        {
            ProcessStartedAt = ThisProcessStarted.Value,
        };
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return fallback;
        }
    }
}
