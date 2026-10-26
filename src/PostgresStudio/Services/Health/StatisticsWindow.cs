using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Server;

using Npgsql;

namespace PostgresStudio.Services.Health;

/// <summary>Which signal set <see cref="StatisticsWindow.Since" />, for the evidence.</summary>
internal enum StatisticsBasis
{
    /// <summary><c>pg_stat_database.stats_reset</c> of this database: <c>pg_stat_reset()</c>, a table's own reset, or 14's crash.</summary>
    DatabaseReset = 0,

    /// <summary>
    /// The shared statistics view's <c>stats_reset</c> - <c>pg_stat_bgwriter</c> up to 16, <c>pg_stat_checkpointer</c> from 17 -
    /// which moves on a crash, a restore and a server's first start on every version (B0 G4).
    /// </summary>
    ServerReset = 1,

    /// <summary>
    /// The server's own start (<c>pg_postmaster_start_time()</c>), when no reset time could be read. Frozen at its number: a
    /// history written before CAP-fix may carry it for a failover, which now starts at the promoted server's reset times.
    /// </summary>
    ServerStart = 2,

    /// <summary>
    /// A counter went down between two samples (or a reset time moved back) with no reset time to say when: the window starts
    /// at the check that saw it, the later bound, so no verdict that needs a long window is given early.
    /// </summary>
    CounterDrop = 3,

    /// <summary>Nothing was read to say: no reset time and no start time. The window is empty.</summary>
    Unknown = 4,

    /// <summary><c>pg_stat_archiver.stats_reset</c>, which moves with the other shared views (B0 G4).</summary>
    ArchiverReset = 5,
}

/// <summary>
/// Why the counters began counting at <see cref="StatisticsWindow.Since" /> - the studio's best reading of the signals, which
/// a check words for its own finding (V1's <c>since-restart</c>, B1's usage guards). Frozen: a value is never renumbered, and
/// it is written to the history by number.
/// </summary>
/// <remarks>
/// <para>
/// <b>What can be told apart, and from what.</b> One read can only say whether the counters began at the server's start, after
/// it, or before it (<see cref="ServerStarted" />, <see cref="ManualReset" />, <see cref="None" />). The rest needs a previous
/// sample to compare with: a timeline that went up on a server that is no longer a standby, a server that started after the
/// last sample or was already running at it, a system identifier or a major version that changed, another instance
/// answering. From SQL, a crash and a restore from a base backup that replays without promoting look
/// the same, and are both <see cref="Crash" />.
/// </para>
/// </remarks>
internal enum StatisticsResetReason
{
    /// <summary>Nothing reset them as far as the studio can see: they have counted through the server's last restart.</summary>
    None = 0,

    /// <summary>
    /// They began when this server started: its first start, or a start that kept no statistics - a crash, an immediate
    /// shutdown, a restore, or on 14 any start of a standby. One read cannot tell which; a sample from before the start can.
    /// </summary>
    ServerStarted = 1,

    /// <summary>
    /// Somebody reset them: <c>pg_stat_reset()</c>, <c>pg_stat_reset_shared()</c>, or one table's counters
    /// (<c>pg_stat_reset_single_table_counters()</c>, which moves the database's reset time too).
    /// </summary>
    ManualReset = 2,

    /// <summary>
    /// The server restarted - or reinitialised after a backend crashed - without its statistics: a crash, an immediate
    /// shutdown, an out-of-memory kill, or a restore from a base backup that did not promote.
    /// </summary>
    Crash = 3,

    /// <summary>
    /// A point-in-time recovery or a restore from a base backup: the timeline went up, and the server started after the last
    /// sample.
    /// </summary>
    Restore = 4,

    /// <summary>
    /// Another server took over: the server answering started before the last sample and its counters went down or a reset
    /// time moved, or the timeline went up on a server that was not the one watched - started at another time, or out of
    /// recovery with its counters lower. The watched server's own promotion is none: its counters go on (measured on 14, 17
    /// and 18). The window then starts at the promoted server's reset times, which from 15 date from its first start as a
    /// standby, since it keeps its statistics through a clean restart in recovery.
    /// </summary>
    Failover = 5,

    /// <summary>
    /// A new cluster: <c>pg_upgrade</c> (which before PostgreSQL 18 carries no statistics), <c>initdb</c>, or a restore from a dump -
    /// seen as the system identifier or the major version changing since the last check. Never from one read: tables nobody has
    /// analysed yet are what a store with many empty collections has too.
    /// </summary>
    FreshCluster = 6,

    /// <summary>Another instance answered (an Aurora reader or writer, issue #8 A1): the instance identity changed.</summary>
    OtherInstance = 7,

    /// <summary>A counter went down, or a reset time moved back, and nothing read says why.</summary>
    Unexplained = 8,
}

/// <summary>
/// What <see cref="StatisticsWindowQueries" /> read, once per sweep: the reset times, the server's start, timeline and identity,
/// and the counters a reset is told by.
/// </summary>
/// <remarks>Init properties, so a unit test states only what its case is about.</remarks>
internal sealed record StatisticsReading
{
    /// <summary>When, by the studio's clock - the sweep's start, the moment every check of the sweep is judged at.</summary>
    public required DateTimeOffset ReadAt { get; init; }

    /// <summary><c>server_version_num</c>.</summary>
    public int ServerVersion { get; init; } = 170_000;

    /// <summary><c>pg_stat_database.stats_reset</c> of this database: null on 15+ until somebody resets it (B0 G4).</summary>
    public DateTimeOffset? DatabaseReset { get; init; }

    /// <summary><c>pg_stat_bgwriter.stats_reset</c> up to 16, <c>pg_stat_checkpointer.stats_reset</c> from 17.</summary>
    public DateTimeOffset? SharedReset { get; init; }

    /// <summary><c>pg_stat_archiver.stats_reset</c>.</summary>
    public DateTimeOffset? ArchiverReset { get; init; }

    /// <summary><c>pg_postmaster_start_time()</c>.</summary>
    public DateTimeOffset? ServerStartedAt { get; init; }

    /// <summary>
    /// <c>pg_control_checkpoint().timeline_id</c>: the timeline of the last checkpoint, so a read just after a promotion can
    /// still show the old one (B0 G4). Null where the function may not be called.
    /// </summary>
    public int? Timeline { get; init; }

    /// <summary><c>pg_control_system().system_identifier</c>: the same on a primary and its standbys, new for a new cluster.</summary>
    public long? SystemIdentifier { get; init; }

    /// <summary>
    /// <c>pg_is_in_recovery()</c>: a standby, whose timeline moves when it follows a promotion upstream without any of its own
    /// counters being reset - so in recovery, an unchanged start is enough to call a new timeline the same server's.
    /// </summary>
    public bool InRecovery { get; init; }

    /// <summary>
    /// The instance that answered, on a platform that names one - the single place issue #8 A1 (W5-AUR) adds Aurora's
    /// <c>aurora_db_instance_identifier()</c>. Null everywhere else, and then never compared.
    /// </summary>
    public string? Instance { get; init; }

    /// <summary><c>xact_commit</c>: a counter that only a reset takes down.</summary>
    public long Commits { get; init; }

    /// <summary><c>blks_read</c>: a counter that only a reset takes down, and C11's reads.</summary>
    public long BlocksRead { get; init; }

    /// <summary><c>blks_hit</c>: C11's hits.</summary>
    public long BlocksHit { get; init; }

    /// <summary>The ordinary tables outside the system schemas.</summary>
    public int Tables { get; init; }

    /// <summary>
    /// How many of <see cref="Tables" /> were never analysed (<c>reltuples &lt; 0</c>, PostgreSQL 14+) - for a check that words a
    /// never-analysed majority itself; the window's own reason never rests on it.
    /// </summary>
    public int UnanalysedTables { get; init; }

    /// <summary>Which shared view <see cref="SharedReset" /> is: <c>pg_stat_checkpointer</c> from 17, <c>pg_stat_bgwriter</c> below.</summary>
    public string SharedView => PostgresFeatures.Has(PostgresFeature.PgStatCheckpointer, ServerVersion) ? "pg_stat_checkpointer" : "pg_stat_bgwriter";
}

/// <summary>
/// Since when this database's cumulative counters have counted, and whether they look reset - the one component every
/// usage-based verdict asks (W5-CAP): C08's and C11's rates per day, the Storage page's growth, B1's usage guards, Q1's window.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract.</b> <see cref="Since" /> is when the counters began, read from the server: the later of
/// <c>pg_stat_database.stats_reset</c> and the earlier of the two shared views' <c>stats_reset</c> (<c>pg_stat_bgwriter</c> up to
/// 16 or <c>pg_stat_checkpointer</c> from 17, and <c>pg_stat_archiver</c>) - <c>max(database, min(shared, archiver))</c> - the
/// server's start where no reset time can be read, or the sample that saw a counter go down; <see cref="Length" /> is from then to
/// <see cref="MeasuredAt" />. <see cref="LooksReset" /> is set for the
/// sweep in which a reset is first seen - the instance, the cluster, the timeline, the server's start or a reset time changed,
/// or a tracked counter (<c>xact_commit</c>, <c>blks_read</c>) went down since the previous sample - with
/// <see cref="Reason" /> saying what it looks like; a verdict that needs a long window gives none while it is set, or until
/// <see cref="Covers" /> says the window is long enough, whichever is later.
/// </para>
/// <para>
/// <b>Why not <c>pg_stat_database.stats_reset</c> alone, nor the latest of every reset.</b> From 15 that column stays null
/// through a crash that zeroes every counter (<see cref="PostgresFeature.DatabaseStatsResetNullAfterCrash" />, B0 G4), so on
/// its own it would say a crashed database's counters had counted since the beginning; the shared views' times move on a crash
/// on 14 and 17 alike. But a <c>pg_stat_reset_shared()</c> of one view - <c>'archiver'</c> to clear a failure count,
/// <c>'bgwriter'</c> - moves that view's time and leaves this database's counters alone, so the latest of every reset would let
/// one such reset, read by a process started after it, shorten the window and inflate every rate over it (counters since
/// January, an archiver reset in June: twice the rate). A crash, a restore and a first start stamp both shared views at one
/// instant, so the earlier of the two is never earlier than the last of those, and the database's own reset - which only
/// <c>pg_stat_reset()</c>, a table's reset or 14's crash moves - is taken whenever it is later. Seen live, a reset of a shared view
/// with this database's counters still climbing is not a reset of the window either. What remains: 17's argument-less
/// <c>pg_stat_reset_shared()</c> resets every shared view at once and reads, after the fact, like a backend's crash - the window
/// then begins at it, the shorter side.
/// </para>
/// <para>
/// <b>A clean restart is not a reset.</b> Both 14 and 15+ keep their statistics across a clean shutdown and start (B0 G4: the
/// counters and <c>stats_reset</c> unchanged); only a crash, an immediate shutdown or a restore discards them. So the server's
/// start moving is a reset only together with a counter going down or a reset time moving, which is how a clean restart and a
/// crash are told apart from SQL. (The brief's "postmaster restarted on a version without StatsPersistAcrossCleanRestart" names
/// no supported version.)
/// </para>
/// <para>
/// <b>A standby, and a failover.</b> Measured on 14.24, 17.11 and 18.6 (CAP-fix): from 15 a standby keeps its statistics through
/// a clean restart in recovery, with or without a restartpoint between, and through its promotion, its reset times those of
/// its first start from the base backup; 14 discards them at every standby start and stamps the times anew. So the watched
/// server's own promotion is no reset, nor is a standby following one upstream; and when another server takes over, the window
/// starts at that server's reset times, never at its start, which on 15+ may be a later restart its counters went on through.
/// Residual: a promoted server counts the rows it writes only from its promotion - replayed rows are not counted - while its
/// reads count from its first start, so growth just after a failover reads low, never high.
/// </para>
/// <para>
/// <b>The history carries it.</b> The sweep computes the window once, after its gatherers, against the previous sample of the
/// database's history (<see cref="Compute(StatisticsReading, HealthHistoryView)" />), writes it back as <c>stats.</c> series (<see cref="Sample" />), and hands it to
/// every evaluator at or above the database-wide gate in <see cref="HealthEvaluation.Statistics" />. So a window shortened by a
/// counter drop stays shortened from one sweep to the next for as long as the history runs unbroken, and a
/// process that starts afresh reads it from the server again - the later bound. Anything outside a sweep (the Storage facts)
/// computes it against the same history without writing to it, through <c>Compute(reading, history, recorded: false)</c>: its
/// counter drop is anchored on the last sample rather than the read, and it stops looking reset once its window is a day long.
/// </para>
/// <para>
/// <b>Clocks.</b> <see cref="Since" /> and the reset times are the server's; <see cref="MeasuredAt" /> is the studio's, as every
/// other health judgement's "now" is. A clock that differs by minutes moves a day's window by minutes.
/// </para>
/// <para>
/// <b>Known limit: a database newer than the server's statistics.</b> On 15+ a database's counters begin when it is first
/// used, and nothing in the catalog says when that was: its <c>stats_reset</c> is null, and no view an unprivileged role may
/// read holds a database's creation time. So a database created after the shared views were last reset - a tenant database
/// Marten created yesterday on a server that has run for a month - reads as having counted for the month. Its rates per day
/// then read low (no false alarm), but a verdict that needs a long window (B1's "unused for seven days") must not take
/// <see cref="Length" /> as proof that this database's own counters are that old. 14 stamps a new database's
/// <c>stats_reset</c> and has no such gap.
/// </para>
/// </remarks>
internal sealed record StatisticsWindow
{
    /// <summary>The memo key of the reading (<see cref="ReadingAsync" />): one read per sweep, whichever check asks first.</summary>
    internal const string ReadingKey = "stats.window";

    /// <summary>How close to the server's start a reset time counts as "at the start": a start's own discard of the statistics.</summary>
    /// <remarks>
    /// From 15 the statistics are discarded before recovery replays (<c>StartupXLOG</c>), within moments of the start; on 14 the
    /// statistics collector starts once recovery is done, which can be minutes on a large backlog.
    /// </remarks>
    internal static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(10);

    /// <summary>How far apart two reset times may be and still be one event: a crash resets every shared view at once.</summary>
    internal static readonly TimeSpan SameEvent = TimeSpan.FromSeconds(2);

    /// <summary>How far a timestamp may move between two reads and still be the same one: two reads of one value agree exactly.</summary>
    private static readonly TimeSpan Unmoved = TimeSpan.FromMilliseconds(500);

    /// <summary>A day: what C08's and C11's rates per day and the Storage page's growth wait for (issue #8 G3, G5).</summary>
    public static readonly TimeSpan Day = TimeSpan.FromDays(1);

    // ---- the history series, all at the database-wide gate --------------------------------------------------------------

    /// <summary>The window's <see cref="Since" />, in Unix seconds - carried from one sweep to the next.</summary>
    internal const string SinceSeries = "stats.since";

    /// <summary>The window's <see cref="Basis" />, by number.</summary>
    internal const string BasisSeries = "stats.basis";

    /// <summary>The window's <see cref="Reason" />, by number.</summary>
    internal const string ReasonSeries = "stats.reason";

    /// <summary><c>pg_stat_database.stats_reset</c>, Unix seconds; 0 for null.</summary>
    internal const string DatabaseResetSeries = "stats.database-reset";

    /// <summary>The shared view's <c>stats_reset</c>, Unix seconds; 0 for null.</summary>
    internal const string SharedResetSeries = "stats.shared-reset";

    /// <summary><c>pg_stat_archiver.stats_reset</c>, Unix seconds; 0 for null.</summary>
    internal const string ArchiverResetSeries = "stats.archiver-reset";

    /// <summary><c>pg_postmaster_start_time()</c>, Unix seconds; absent when not read.</summary>
    internal const string ServerStartSeries = "stats.server-start";

    /// <summary>The timeline; absent when not read.</summary>
    internal const string TimelineSeries = "stats.timeline";

    /// <summary>The system identifier's low 52 bits (a double holds no more exactly); absent when not read.</summary>
    internal const string SystemSeries = "stats.system";

    /// <summary>The server's major version.</summary>
    internal const string MajorSeries = "stats.server-major";

    /// <summary><c>xact_commit</c>.</summary>
    internal const string CommitsSeries = "stats.xact-commit";

    /// <summary><c>blks_read</c>.</summary>
    internal const string BlocksReadSeries = "stats.blks-read";

    /// <summary>The answering instance's identity, hashed to 52 bits; absent where the platform names none (A1, W5-AUR).</summary>
    internal const string InstanceSeries = "stats.instance";

    /// <summary>When the window was measured, by the studio's clock.</summary>
    public required DateTimeOffset MeasuredAt { get; init; }

    /// <summary>Since when the counters have counted.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>Which signal set <see cref="Since" />.</summary>
    public required StatisticsBasis Basis { get; init; }

    /// <summary>Why the counters began then, as best the signals tell.</summary>
    public StatisticsResetReason Reason { get; init; }

    /// <summary>
    /// Whether a reset was first seen in this measurement, against the previous sample: the counters are not continuous with
    /// what was seen before. For one sweep; after it <see cref="Since" /> has moved and <see cref="Length" /> is short.
    /// </summary>
    public bool LooksReset { get; init; }

    /// <summary>What showed the reset, in words for the evidence - "the timeline went from 1 to 2" - or <see langword="null" />.</summary>
    public string? Signal { get; init; }

    /// <summary><c>pg_postmaster_start_time()</c>, when read.</summary>
    public DateTimeOffset? ServerStartedAt { get; init; }

    /// <summary>The timeline, when read.</summary>
    public int? Timeline { get; init; }

    /// <summary>The shared view that was read, for the basis's words: <c>pg_stat_checkpointer</c> or <c>pg_stat_bgwriter</c>.</summary>
    public string SharedView { get; init; } = "pg_stat_checkpointer";

    /// <summary>How long the counters have counted: from <see cref="Since" /> to <see cref="MeasuredAt" />, never negative.</summary>
    public TimeSpan Length => MeasuredAt > Since ? MeasuredAt - Since : TimeSpan.Zero;

    /// <summary><see cref="Length" /> in days, for a rate.</summary>
    public double Days => Length.TotalDays;

    /// <summary>Whether a rate over <paramref name="span" /> may be judged: the window is at least that long and does not look reset.</summary>
    /// <param name="span">What the verdict needs: a day for C08 and C11 (G5), seven for B1's usage guards (G4).</param>
    public bool Covers(TimeSpan span) => !LooksReset && Length >= span;

    /// <summary>Whether the counters have counted for a day, and do not look reset.</summary>
    public bool CoversADay => Covers(Day);

    /// <summary>A count over the window, per day; <see langword="null" /> while <see cref="CoversADay" /> is not true.</summary>
    /// <param name="count">A cumulative counter since <see cref="Since" />.</param>
    public double? PerDay(double count) => CoversADay ? count / Days : null;

    /// <summary>Where <see cref="Since" /> came from, for the evidence: "pg_stat_checkpointer.stats_reset".</summary>
    public string BasisText => Basis switch
    {
        StatisticsBasis.DatabaseReset => "pg_stat_database.stats_reset",
        StatisticsBasis.ServerReset => SharedView + ".stats_reset",
        StatisticsBasis.ArchiverReset => "pg_stat_archiver.stats_reset",
        StatisticsBasis.ServerStart => "the server's start (pg_postmaster_start_time()), with no reset time to read",
        StatisticsBasis.CounterDrop => "the check that saw the counters go down, which no reset time explained",
        _ => "nothing: no reset time and no start time could be read",
    };

    /// <summary>The reason in words - what a check that words the window's start says, after "the counters began".</summary>
    public string ReasonText => Describe(Reason);

    /// <summary>"since 2026-10-20 08:57 UTC", for a sentence.</summary>
    public string SinceText => "since " + When(Since);

    /// <summary>
    /// What a verdict that waits for <paramref name="span" /> says while it waits: "Watching: 5 h of statistics so far ..." -
    /// a covered row, never a finding.
    /// </summary>
    /// <param name="what">What is waiting, for the sentence: "the cache hit ratio".</param>
    /// <param name="span">What it waits for.</param>
    public string Watching(string what, TimeSpan span)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(what);

        string needs = span == Day ? "a day" : SessionSpan(span);

        if (LooksReset)
        {
            return "Watching: the statistics look reset since the last check (" + (Signal ?? ReasonText) + "), so " + what
                + " is judged again once they have counted for " + needs + ".";
        }

        return "Watching: " + SessionSpan(Length) + " of statistics so far (" + SinceText + ", " + BasisText + "), and " + what
            + " is judged once they have counted for " + needs + ".";
    }

    /// <summary>
    /// The window, from <paramref name="reading" /> and the database's history before it: what the previous sample saw, carried
    /// forward when nothing has changed, and a reset when something has (<see cref="LooksReset" />).
    /// </summary>
    /// <param name="reading">This read.</param>
    /// <param name="history">The database's samples. Any taken at or after <see cref="StatisticsReading.ReadAt" /> are ignored.</param>
    public static StatisticsWindow Compute(StatisticsReading reading, HealthHistoryView history) => Compute(reading, history, recorded: true);

    /// <summary>
    /// <see cref="Compute(StatisticsReading, HealthHistoryView)" /> for a read whose window is never written to the history -
    /// the Storage facts - when <paramref name="recorded" /> is <see langword="false" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A read that writes nothing compares with the same last sample every time, so it would see the same reset for as long as
    /// no sweep runs. Two things are therefore different for it. A counter drop starts its window at that last sample's time,
    /// which does not move, rather than at the read, which would keep the window at nothing forever. And once its window is a
    /// day long it no longer looks reset: whatever was seen happened at least that long ago.
    /// </para>
    /// </remarks>
    /// <param name="reading">This read.</param>
    /// <param name="history">The database's samples. Any taken at or after <see cref="StatisticsReading.ReadAt" /> are ignored.</param>
    /// <param name="recorded">Whether this window is written to the history as a sweep's is (<see cref="Sample" />).</param>
    public static StatisticsWindow Compute(StatisticsReading reading, HealthHistoryView history, bool recorded)
    {
        StatisticsWindow window = ComputeCore(reading, history, recorded);

        return !recorded && window.LooksReset && window.Length >= Day ? window with { LooksReset = false } : window;
    }

    private static StatisticsWindow ComputeCore(StatisticsReading reading, HealthHistoryView history, bool recorded)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(history);

        (DateTimeOffset raw, StatisticsBasis rawBasis) = RawSince(reading);
        HealthSample? previous = Previous(history, reading.ReadAt);

        StatisticsWindow window = new()
        {
            MeasuredAt = reading.ReadAt,
            Since = raw,
            Basis = rawBasis,
            Reason = FirstSight(reading, raw),
            ServerStartedAt = reading.ServerStartedAt,
            Timeline = reading.Timeline,
            SharedView = reading.SharedView,
        };

        if (previous is null)
        {
            return window;
        }

        if (Transition(previous, reading) is not { } seen)
        {
            // Continuous: the window the previous sample carried, which a counter drop may have shortened -
            // or, after a reset of one shared view this database's counters went on through, kept.
            return Carried(previous, window) ?? window;
        }

        // A failover starts the window at the promoted server's own reset times, never at its start: from 15 a standby keeps its
        // statistics through a clean restart in recovery and through its promotion, so its counters began when it was first
        // started from the base backup - which is what those times say - while 14 discards them at every standby start and
        // stamps the times anew then (measured on 14.24, 17.11 and 18.6, CAP-fix).
        DateTimeOffset since = raw;
        StatisticsBasis basis = rawBasis;

        if (seen.Reason == StatisticsResetReason.Unexplained)
        {
            // The later bound for a sweep, which writes it into its sample and carries it; for a read that writes nothing, the
            // last sample's time, which is the same on every read.
            DateTimeOffset seenBy = recorded ? reading.ReadAt : previous.At;

            if (seenBy > raw)
            {
                since = seenBy;
                basis = StatisticsBasis.CounterDrop;
            }
        }

        return window with { Since = since, Basis = basis, Reason = seen.Reason, LooksReset = true, Signal = seen.Signal };
    }

    /// <summary>What the window and the reading behind it add to the history, for the next sweep to compare with.</summary>
    /// <param name="reading">The reading the window was computed from.</param>
    public IEnumerable<HealthSamplePart> Sample(StatisticsReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        yield return Part(SinceSeries, Seconds(Since));
        yield return Part(BasisSeries, (int) Basis);
        yield return Part(ReasonSeries, (int) Reason);
        yield return Part(DatabaseResetSeries, Seconds(reading.DatabaseReset));
        yield return Part(SharedResetSeries, Seconds(reading.SharedReset));
        yield return Part(ArchiverResetSeries, Seconds(reading.ArchiverReset));
        yield return Part(MajorSeries, reading.ServerVersion / 10_000);
        yield return Part(CommitsSeries, reading.Commits);
        yield return Part(BlocksReadSeries, reading.BlocksRead);

        if (reading.ServerStartedAt is { } started)
        {
            yield return Part(ServerStartSeries, Seconds(started));
        }

        if (reading.Timeline is { } timeline)
        {
            yield return Part(TimelineSeries, timeline);
        }

        if (reading.SystemIdentifier is { } system)
        {
            yield return Part(SystemSeries, system & SystemMask);
        }

        if (reading.Instance is { Length: > 0 } instance)
        {
            yield return Part(InstanceSeries, Hash(instance));
        }

        static HealthSamplePart Part(string series, double value) => new(series, value) { Gate = HealthGate.Database };
    }

    /// <summary>
    /// The sweep's one read of this database's window signals, whichever check asks first (<see cref="HealthCheckContext.Shared{T}" />).
    /// </summary>
    public static Task<StatisticsReading> ReadingAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ReadingKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads the signals: the server's version (the sweep's shared read), then the window's one statement.</summary>
    /// <exception cref="HealthCheckUnavailableException"><c>pg_stat_database</c> has no row for this database.</exception>
    public static async Task<StatisticsReading> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        var query = StatisticsWindowQueries.Read(PostgresFeatures.Has(PostgresFeature.PgStatCheckpointer, server.VersionNumber));
        IReadOnlyList<StatisticsReading> rows = await context.Sql
            .QueryAsync(query, reader => Map(reader, context.Now), cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            throw new HealthCheckUnavailableException("pg_stat_database has no row for this database, so its counters cannot be read.");
        }

        // The one place a platform's instance identity joins the window (issue #8 A1): W5-AUR reads it into the server facts.
        return rows[0] with { Instance = InstanceOf(server) };
    }

    /// <summary>
    /// Why the window could not be read, for a page or a status row - "the statistics window could not be read (SQLSTATE 0A000)":
    /// the studio's words and a SQLSTATE, never Postgres' own, which is drawn for every visitor of the database. C11's row, which
    /// runs the same statement in a sweep, shows the same failure with its own reason.
    /// </summary>
    internal static string NotRead(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        return "the statistics window could not be read"
            + (postgres is not null ? " (SQLSTATE " + postgres.SqlState + ")" : " (" + exception.GetType().Name + ")");
    }

    /// <summary>
    /// The instance that answered, from the server facts: Aurora's <c>aurora_db_instance_identifier()</c> (A1, W5-AUR), and
    /// <see langword="null" /> on every other platform and on an Aurora release without the function - never compared then.
    /// The one place the window takes it from.
    /// </summary>
    internal static string? InstanceOf(PostgresServerFacts server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Platform.Kind == PostgresPlatformKind.AmazonAurora && server.Instance is { Length: > 0 } instance ? instance : null;
    }

    /// <summary>Maps the one row of <see cref="StatisticsWindowQueries.Read" />.</summary>
    internal static StatisticsReading Map(NpgsqlDataReader reader, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new StatisticsReading
        {
            ReadAt = readAt,
            ServerVersion = reader.GetInt32(0),
            DatabaseReset = Timestamp(reader, 1),
            SharedReset = Timestamp(reader, 2),
            ArchiverReset = Timestamp(reader, 3),
            ServerStartedAt = Timestamp(reader, 4),
            Timeline = reader.IsDBNull(5) ? null : reader.GetInt32(5),
            SystemIdentifier = reader.IsDBNull(6) ? null : reader.GetInt64(6),
            Commits = reader.GetInt64(7),
            BlocksRead = reader.GetInt64(8),
            BlocksHit = reader.GetInt64(9),
            Tables = reader.GetInt32(10),
            UnanalysedTables = reader.GetInt32(11),
            InRecovery = reader.GetBoolean(12),
        };
    }

    /// <summary>A reason in words, after "the counters began".</summary>
    internal static string Describe(StatisticsResetReason reason) => reason switch
    {
        StatisticsResetReason.ServerStarted =>
            "when the server started - its first start, or a start that kept no statistics (a crash, an immediate shutdown, a restore, or on 14 a standby's start)",
        StatisticsResetReason.ManualReset =>
            "when they were reset - pg_stat_reset(), pg_stat_reset_shared() or one table's counters - or, on PostgreSQL 14, when "
            + "the database was first used after the server started",
        StatisticsResetReason.Crash =>
            "when the server restarted without its statistics - a crash, an immediate shutdown, or a restore from a base backup that did not promote",
        StatisticsResetReason.Restore =>
            "at a point-in-time recovery or a restore from a base backup: the timeline went up and the server started anew",
        StatisticsResetReason.Failover =>
            "at a failover: another server, already running, took over",
        StatisticsResetReason.FreshCluster =>
            "with a new cluster - pg_upgrade (which before PostgreSQL 18 carries no statistics), initdb or a restore from a dump",
        StatisticsResetReason.OtherInstance =>
            "on another instance: a different one answered than at the last check",
        StatisticsResetReason.Unexplained =>
            "at a reset nothing read explains: a counter went down, or a reset time moved back",
        _ => "as far back as the statistics go: they have counted through the server's last restart",
    };

    /// <summary>A moment, in UTC, the way the evidence prints one.</summary>
    internal static string When(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>The low 52 bits a double holds exactly.</summary>
    private const long SystemMask = (1L << 52) - 1;

    /// <summary>
    /// When the server's statistics say this database's counters began: <c>max(database reset, min(shared reset, archiver
    /// reset))</c>, and which it was - or, with no reset time at all, the server's start, or nothing.
    /// </summary>
    /// <remarks>
    /// The earlier of the two shared views', because a crash, a restore and a first start stamp both at one instant, while a
    /// <c>pg_stat_reset_shared()</c> of one view - <c>'archiver'</c> to clear a failure count, <c>'bgwriter'</c> - moves only that
    /// one and leaves this database's counters alone. Taking the later of them would let such a reset, read by a process that
    /// started after it, shorten the window and inflate every rate over it.
    /// </remarks>
    private static (DateTimeOffset Since, StatisticsBasis Basis) RawSince(StatisticsReading reading)
    {
        (DateTimeOffset At, StatisticsBasis Basis)? server = (reading.SharedReset, reading.ArchiverReset) switch
        {
            ({ } shared, { } archiver) => archiver < shared ? (archiver, StatisticsBasis.ArchiverReset) : (shared, StatisticsBasis.ServerReset),
            ({ } shared, null) => (shared, StatisticsBasis.ServerReset),
            (null, { } archiver) => (archiver, StatisticsBasis.ArchiverReset),
            _ => null,
        };

        if (reading.DatabaseReset is { } database && (server is null || database > server.Value.At))
        {
            return (database, StatisticsBasis.DatabaseReset);
        }

        if (server is { } since)
        {
            return since;
        }

        return reading.ServerStartedAt is { } started
            ? (started, StatisticsBasis.ServerStart)
            : (reading.ReadAt, StatisticsBasis.Unknown);
    }

    /// <summary>What one read says of why the counters began at <paramref name="since" />, with no earlier sample to compare.</summary>
    private static StatisticsResetReason FirstSight(StatisticsReading reading, DateTimeOffset since)
    {
        if (reading.ServerStartedAt is not { } started)
        {
            return StatisticsResetReason.None;
        }

        if (since < started - SameEvent)
        {
            return StatisticsResetReason.None;
        }

        if (since <= started + StartGrace)
        {
            // Never a new cluster on one read: a store with many tables nobody has written yet looks the same as pg_upgrade's.
            // That is said only when the system identifier or the major version is seen to change.
            return StatisticsResetReason.ServerStarted;
        }

        // Later than the start: a reset - a backend's crash, which reinitialises every shared view at one moment while the
        // server stays up, or somebody's reset function.
        return reading.SharedReset is { } shared && reading.ArchiverReset is { } archiver && Near(shared, archiver, SameEvent)
               && Near(since, shared, SameEvent)
            ? StatisticsResetReason.Crash
            : StatisticsResetReason.ManualReset;
    }

    /// <summary>The latest sample before <paramref name="readAt" /> that carries the window's series.</summary>
    private static HealthSample? Previous(HealthHistoryView history, DateTimeOffset readAt)
    {
        for (int i = history.Samples.Count - 1; i >= 0; i--)
        {
            HealthSample sample = history.Samples[i];

            if (sample.At < readAt && sample.Values.ContainsKey(SinceSeries))
            {
                return sample;
            }
        }

        return null;
    }

    /// <summary>The window <paramref name="previous" /> carried, now measured at <paramref name="current" />'s time.</summary>
    private static StatisticsWindow? Carried(HealthSample previous, StatisticsWindow current)
    {
        if (!previous.Values.TryGetValue(SinceSeries, out double since))
        {
            return null;
        }

        StatisticsBasis basis = previous.Values.TryGetValue(BasisSeries, out double b) && Enum.IsDefined(typeof(StatisticsBasis), (int) b)
            ? (StatisticsBasis) (int) b
            : current.Basis;

        StatisticsResetReason reason = previous.Values.TryGetValue(ReasonSeries, out double r) && Enum.IsDefined(typeof(StatisticsResetReason), (int) r)
            ? (StatisticsResetReason) (int) r
            : current.Reason;

        return current with { Since = FromSeconds(since), Basis = basis, Reason = reason };
    }

    /// <summary>What changed between <paramref name="previous" /> and <paramref name="current" />, or <see langword="null" />.</summary>
    private static (StatisticsResetReason Reason, string Signal)? Transition(HealthSample previous, StatisticsReading current)
    {
        IReadOnlyDictionary<string, double> was = previous.Values;

        // 1. Another instance answered (A1, W5-AUR): compared only where both reads named one.
        if (current.Instance is { Length: > 0 } instance && was.TryGetValue(InstanceSeries, out double wasInstance) && wasInstance != Hash(instance))
        {
            // Its counters are its own, so the window starts where that instance's own reset times say (RawSince).
            return (StatisticsResetReason.OtherInstance, "another instance answered; its counters started at " + When(RawSince(current).Since));
        }

        // 2. Another cluster: its system identifier, or its major version - pg_upgrade, a dump restored into a new one.
        if (current.SystemIdentifier is { } system && was.TryGetValue(SystemSeries, out double wasSystem) && wasSystem != (system & SystemMask))
        {
            return (StatisticsResetReason.FreshCluster, "the cluster's system identifier changed");
        }

        int major = current.ServerVersion / 10_000;

        if (was.TryGetValue(MajorSeries, out double wasMajor) && (int) wasMajor != major)
        {
            return (StatisticsResetReason.FreshCluster,
                "the server's major version went from " + ((int) wasMajor).ToString(CultureInfo.InvariantCulture) + " to "
                + major.ToString(CultureInfo.InvariantCulture));
        }

        DateTimeOffset? wasStart = was.TryGetValue(ServerStartSeries, out double s) ? FromSeconds(s) : null;

        bool dropped = current.Commits < Value(was, CommitsSeries, current.Commits)
            || current.BlocksRead < Value(was, BlocksReadSeries, current.BlocksRead);

        // 3. The timeline: another server promoted, or a recovery that ended in a promotion. Not the watched server's own: a
        //    standby that, never restarted, follows a promotion upstream (still in recovery) or is itself promoted (out of it)
        //    went on counting through it - measured on 14.24, 17.11 and 18.6 (CAP-fix) - and on 14 and 17
        //    pg_control_checkpoint() shows the new timeline only from the first checkpoint after the promotion, by when the
        //    server may be out of recovery with nothing else changed. Out of recovery its counters must not have gone down
        //    either: they are what tells a different server, started the same second, apart.
        bool sameServer = current.ServerStartedAt is { } running && wasStart is { } startedBefore && Near(running, startedBefore, Unmoved)
            && (current.InRecovery || !dropped);

        if (!sameServer && current.Timeline is { } timeline && was.TryGetValue(TimelineSeries, out double wasTimeline) && (int) wasTimeline != timeline)
        {
            string moved = "the timeline went from " + ((int) wasTimeline).ToString(CultureInfo.InvariantCulture) + " to "
                + timeline.ToString(CultureInfo.InvariantCulture);

            return current.ServerStartedAt is { } startedAt && startedAt > previous.At + SameEvent
                ? (StatisticsResetReason.Restore, moved + " and the server started after the last check")
                : (StatisticsResetReason.Failover, moved + " on a server that was already running at the last check");
        }

        bool databaseMoved = Moved(was, DatabaseResetSeries, current.DatabaseReset);
        bool sharedMoved = Moved(was, SharedResetSeries, current.SharedReset);
        bool archiverMoved = Moved(was, ArchiverResetSeries, current.ArchiverReset);
        bool anyMoved = databaseMoved || sharedMoved || archiverMoved;

        // 4. The server started again.
        if (current.ServerStartedAt is { } started && wasStart is { } before && !Near(started, before, Unmoved))
        {
            if (!dropped && !anyMoved)
            {
                // A clean restart keeps the statistics on 14 and on 15+ (B0 G4): the same window.
                return null;
            }

            return started < previous.At - SameEvent
                ? (StatisticsResetReason.Failover, "the server answering started before the last check, and its counters went down")
                : (StatisticsResetReason.Crash, "the server restarted at " + When(started) + " without its statistics");
        }

        // 5. The same server: a reset time moved, or a counter went down.
        if (MovedBack(was, DatabaseResetSeries, current.DatabaseReset)
            || MovedBack(was, SharedResetSeries, current.SharedReset)
            || MovedBack(was, ArchiverResetSeries, current.ArchiverReset))
        {
            return (StatisticsResetReason.Unexplained, "a reset time moved back");
        }

        if (anyMoved)
        {
            if (!databaseMoved && !dropped)
            {
                // A pg_stat_reset_shared() of a shared view: its time moved, and this database's counters went on climbing.
                return null;
            }

            return sharedMoved && archiverMoved && dropped
                && current.SharedReset is { } shared && current.ArchiverReset is { } archiver && Near(shared, archiver, SameEvent)
                ? (StatisticsResetReason.Crash, "every shared statistic was reset at " + When(shared) + " and the counters went down: a backend crashed")
                : (StatisticsResetReason.ManualReset, "a statistics reset time moved");
        }

        if (dropped)
        {
            string which = current.Commits < Value(was, CommitsSeries, current.Commits) ? "xact_commit" : "blks_read";
            return (StatisticsResetReason.Unexplained, which + " went down with no reset time moving");
        }

        return null;
    }

    private static double Value(IReadOnlyDictionary<string, double> values, string series, double otherwise) =>
        values.TryGetValue(series, out double value) ? value : otherwise;

    /// <summary>Whether a reset time differs from the sample's - one that appeared or vanished included.</summary>
    private static bool Moved(IReadOnlyDictionary<string, double> was, string series, DateTimeOffset? now) =>
        was.TryGetValue(series, out double before) && Math.Abs(before - Seconds(now)) > Unmoved.TotalSeconds;

    /// <summary>
    /// Whether a reset time is earlier than the sample's. One that vanished is not: on 14 a database's own statistics entry is
    /// gone after a crash until its first report, so its time reads null - a reset, which <see cref="Moved" /> counts.
    /// </summary>
    private static bool MovedBack(IReadOnlyDictionary<string, double> was, string series, DateTimeOffset? now) =>
        now is { } value && was.TryGetValue(series, out double before) && before > 0 && before - Seconds(value) > Unmoved.TotalSeconds;

    private static bool Near(DateTimeOffset a, DateTimeOffset b, TimeSpan within) => (a > b ? a - b : b - a) <= within;

    private static double Seconds(DateTimeOffset? at) => at is { } value ? value.ToUnixTimeMilliseconds() / 1000d : 0d;

    private static DateTimeOffset FromSeconds(double seconds) => DateTimeOffset.FromUnixTimeMilliseconds((long) Math.Round(seconds * 1000d));

    private static DateTimeOffset? Timestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    /// <summary>
    /// FNV-1a over the identity's UTF-8, cut to the 52 bits a double holds - equal for equal names, for comparison only. Also
    /// what Aurora's wait samples carry, so a window of them never spans two instances (<c>AuroraWaitProfile</c>).
    /// </summary>
    internal static double Hash(string value)
    {
        ulong hash = 14695981039346656037UL;

        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash & (ulong) SystemMask;
    }

    /// <summary>A span as a person says it: "under a minute", "45 min", "5 h", "3 days".</summary>
    private static string SessionSpan(TimeSpan span) => span switch
    {
        _ when span < TimeSpan.FromMinutes(1) => "under a minute",
        _ when span < TimeSpan.FromHours(2) => ((int) span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min",
        _ when span < TimeSpan.FromDays(2) => ((int) span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h",
        _ => ((int) span.TotalDays).ToString(CultureInfo.InvariantCulture) + " days",
    };
}
