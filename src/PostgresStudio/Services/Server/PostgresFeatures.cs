using System.Globalization;

namespace PostgresStudio.Services.Server;

/// <summary>
/// Something a PostgreSQL release added, renamed or changed that the studio reads, and so gates on - each member's
/// first release is in <see cref="PostgresFeatures" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where each one comes from.</b> The 14-18 members were measured on <c>postgres:14.24-alpine</c> to
/// <c>postgres:18.6-alpine</c> by the B0 spike (<c>B0-findings.md</c> §6 and its catalog diff, <c>matrix.sql</c> /
/// <c>diff_matrix.py</c>, 2026-10-20) unless a member says otherwise; the 19 members are from the PostgreSQL 19 release
/// notes (<see href="https://www.postgresql.org/docs/19/release-19.html" />, "as of 2026-10-09", read 2026-10-26),
/// and the matrix runs 19's release candidate against them (<c>VersionMatrixLiveTests</c>).
/// </para>
/// <para>
/// <b>Append-only.</b> A later packet adds a member through its brief, at the end of its version's block; a member is
/// never renamed or removed, because its name is what a gate in a check says.
/// </para>
/// </remarks>
internal enum PostgresFeature
{
    // ---- 14 -----------------------------------------------------------------------------------------------------------

    /// <summary><c>pg_stat_statements_info</c> (<c>dealloc</c>, <c>stats_reset</c>): pg_stat_statements 1.9. B0 §6.</summary>
    StatementsInfo,

    /// <summary><c>pg_stat_statements.toplevel</c>: a nested statement counted apart from the one that ran it. B0 §6.</summary>
    StatementsToplevel,

    /// <summary><c>pg_stat_activity.query_id</c>, joinable to <c>pg_stat_statements.queryid</c>. B0 §6 ("what 13 lacks").</summary>
    ActivityQueryId,

    /// <summary>The <c>pg_stat_wal</c> view. B0 §6 and G8.</summary>
    PgStatWal,

    /// <summary>The <c>pg_stat_replication_slots</c> view: a logical slot's spill and stream counters. B0 §4, §6.</summary>
    ReplicationSlotStats,

    /// <summary><c>pg_get_wal_replay_pause_state()</c>: a standby's replay "not paused", "pause requested" or "paused". B0 §4.</summary>
    WalReplayPauseState,

    /// <summary><c>pg_locks.waitstart</c>: since when a lock has been waited for. B0 §6 ("what 13 lacks").</summary>
    LocksWaitStart,

    /// <summary>
    /// <c>pg_stat_database</c>'s session counters: <c>sessions</c>, <c>session_time</c>, <c>active_time</c>,
    /// <c>idle_in_transaction_time</c> and <c>sessions_abandoned</c> / <c>_fatal</c> / <c>_killed</c>. B0 §6 and G5.
    /// </summary>
    DatabaseSessionCounters,

    /// <summary><c>vacuum_failsafe_age</c> and <c>vacuum_multixact_failsafe_age</c>. B0 §6.</summary>
    VacuumFailsafeAge,

    /// <summary>
    /// The <c>pg_database_owner</c> role, which owns <c>public</c> in a cluster created on 15 or later (and so takes
    /// <c>CREATE</c> on it away from <c>PUBLIC</c>). B0 §6, S06.
    /// </summary>
    DatabaseOwnerRole,

    /// <summary>The <c>pg_read_all_data</c> and <c>pg_write_all_data</c> roles. B0 §6 ("what 13 lacks").</summary>
    ReadAllDataRole,

    /// <summary>
    /// <c>pg_class.reltuples</c> is <c>-1</c> for a table never vacuumed or analysed (13 says <c>0</c>, which is also a
    /// table's honest count when it is empty). B0 §7a and G2.
    /// </summary>
    ReltuplesUnknownIsMinusOne,

    // ---- 15 -----------------------------------------------------------------------------------------------------------

    /// <summary><c>pg_stat_statements</c>' <c>jit_*</c> columns: pg_stat_statements 1.10. B0 §6.</summary>
    StatementsJit,

    /// <summary><c>pg_stat_force_next_flush()</c>: a test's way to see its own counters at once. B0 §7b.</summary>
    ForceNextFlush,

    /// <summary>
    /// <c>pg_stat_database.stats_reset</c> is null until somebody resets the database's counters, and stays null through a
    /// crash that zeroes them: on 14 the statistics collector set it, and moved it on a crash (B0 G4: 14 kept the counters
    /// and the time across a clean restart and reset both on a <c>SIGQUIT</c>; 17 kept them across the restart, zeroed
    /// them on the crash and left <c>stats_reset</c> null throughout). So from 15 a crash is told by the shared views'
    /// <c>stats_reset</c>, the postmaster's start time or a counter that went down - never by this column.
    /// </summary>
    DatabaseStatsResetNullAfterCrash,

    /// <summary>
    /// <c>initdb</c> makes <c>pg_database_owner</c> the owner of <c>public</c> and grants <c>PUBLIC</c> no <c>CREATE</c> on it, in a
    /// cluster created on 15 or later - one upgraded from 14 keeps 14's grant. B0 §6, S06 (<c>t</c> on 14, <c>f</c> on 17 and 18).
    /// </summary>
    PublicSchemaOwnedByDatabaseOwner,

    // ---- 16 -----------------------------------------------------------------------------------------------------------

    /// <summary>The <c>pg_stat_io</c> view. B0 §6 and G8.</summary>
    PgStatIo,

    /// <summary><c>last_seq_scan</c> and <c>last_idx_scan</c> in <c>pg_stat_all_tables</c> and <c>_indexes</c>. B0 §6 and G4.</summary>
    LastScanColumns,

    /// <summary><c>pg_stat_all_tables.n_tup_newpage_upd</c>: the updates that went to another page. B0 §6 and G3.</summary>
    NewPageUpdates,

    /// <summary><c>EXPLAIN (GENERIC_PLAN)</c>: a plan for a statement with <c>$1</c> placeholders. B0 §2, §6.</summary>
    ExplainGenericPlan,

    /// <summary><c>pg_replication_slots.conflicting</c>. B0 §6.</summary>
    SlotConflicting,

    /// <summary>
    /// The <c>reserved_connections</c> setting and the <c>pg_use_reserved_connections</c> role beside
    /// <c>superuser_reserved_connections</c>: B0's catalog dump of 16 (<c>matrix-16.txt</c>) is the first with either, and
    /// the PostgreSQL 16 release notes say "Add server variable reserved_connections to reserve connection slots for
    /// privileged users".
    /// </summary>
    ReservedConnections,

    /// <summary>
    /// A query that is not pruned - or a generic plan of one that is - also locks a partitioned table's partitioned indexes:
    /// 601 locks for 200 partitions of two indexes each up to 15, 603 from 16 (B0 §8a). T19 counts them from here (W5-B1).
    /// </summary>
    PartitionedIndexLocks,

    // ---- 17 -----------------------------------------------------------------------------------------------------------

    /// <summary>The <c>pg_stat_checkpointer</c> view; <c>pg_stat_bgwriter</c> loses its checkpoint columns. B0 §6 and G8.</summary>
    PgStatCheckpointer,

    /// <summary><c>pg_replication_slots.inactive_since</c> and <c>invalidation_reason</c>. B0 §6.</summary>
    SlotInactiveSince,

    /// <summary><c>pg_stat_progress_vacuum.indexes_total</c> and <c>indexes_processed</c>. B0 §6.</summary>
    ProgressIndexesTotal,

    /// <summary>
    /// pg_stat_statements 1.11: <c>blk_read_time</c> / <c>blk_write_time</c> renamed <c>shared_blk_read_time</c> /
    /// <c>shared_blk_write_time</c>, with <c>local_blk_*_time</c>, <c>stats_since</c>, <c>minmax_stats_since</c> and
    /// <c>jit_deform_*</c> added. B0 §6.
    /// </summary>
    StatementsSharedBlkTimeNames,

    /// <summary>The <c>pg_wait_events</c> view. B0 §6.</summary>
    WaitEventsView,

    /// <summary>The <c>pg_maintain</c> role (<c>VACUUM</c>, <c>ANALYZE</c>, <c>REINDEX</c> on every relation). B0 §6.</summary>
    MaintainRole,

    /// <summary><c>EXPLAIN (FORMAT JSON)</c>'s JIT <c>Generation</c> is an object (<c>Total</c>, <c>Deform</c>), a number before. B0 §6b.</summary>
    ExplainGenerationObject,

    /// <summary>
    /// The <c>transaction_timeout</c> setting: a transaction, idle or not, is ended once it has lasted this long. W5-S2: the
    /// PostgreSQL 17 release notes ("Add server variable transaction_timeout"), and the matrix's catalog probe.
    /// </summary>
    TransactionTimeout,

    // ---- 18 -----------------------------------------------------------------------------------------------------------

    /// <summary><c>pg_class.relallfrozen</c>, which the insert trigger of autovacuum scales by. B0 §6, §7.</summary>
    RelAllFrozen,

    /// <summary><c>autovacuum_vacuum_max_threshold</c>, as a setting and as a table's storage parameter. B0 §6, §7b.</summary>
    VacuumMaxThreshold,

    /// <summary><c>pg_stat_io</c>'s <c>read_bytes</c>, <c>write_bytes</c> and <c>extend_bytes</c>; <c>op_bytes</c> gone. B0 §6.</summary>
    PgStatIoBytes,

    /// <summary>
    /// <c>pg_stat_io</c>'s WAL rows (<c>object = 'wal'</c>), and <c>pg_stat_wal</c>'s <c>wal_write</c>, <c>wal_sync</c>,
    /// <c>wal_write_time</c> and <c>wal_sync_time</c> gone. B0 §6 and G8.
    /// </summary>
    PgStatIoWal,

    /// <summary>
    /// <c>pg_stat_checkpointer.num_done</c> (and <c>slru_written</c>): <c>num_timed</c> counts the skipped checkpoints
    /// too, so the done ones are the honest denominator. B0 §6 and G8.
    /// </summary>
    CheckpointerNumDone,

    /// <summary>NOT NULL constraints in <c>pg_constraint</c>, <c>contype = 'n'</c>. B0 §3, §6.</summary>
    NotNullConstraints,

    /// <summary>
    /// The <c>pg_sequences</c> view locks every sequence it lists for every reader, whatever columns are selected - never
    /// read it (<c>NoSequenceLockingReadsTests</c>). B0 §3, §9.
    /// </summary>
    SequencesViewLocksAll,

    /// <summary>EXPLAIN's actual row counts are fractional (<c>1050.00</c>). B0 §6b.</summary>
    ExplainFractionalRows,

    /// <summary><c>EXPLAIN (ANALYZE)</c> reports buffers without <c>BUFFERS</c>. B0 §6b.</summary>
    ExplainBuffersByDefault,

    /// <summary><c>idle_replication_slot_timeout</c>: the server invalidates an idle slot at a checkpoint. B0 §6.</summary>
    IdleSlotTimeout,

    /// <summary><c>initdb</c> turns data checksums on by default, so a cluster created on 18 has them. B0 §6, S03.</summary>
    ChecksumsDefaultOn,

    /// <summary>
    /// <c>pg_stat_all_tables</c>' <c>total_vacuum_time</c>, <c>total_autovacuum_time</c>, <c>total_analyze_time</c> and
    /// <c>total_autoanalyze_time</c>. B0 §6.
    /// </summary>
    TotalVacuumTime,

    /// <summary>
    /// A backend's fast-path lock slots are sized from <c>max_locks_per_transaction</c> - groups of 16 - rather than fixed at
    /// 16: 64 at the default of 64 (B0 §6, §8a: 16 on 13-17, 64 on 18). T19's evidence says which (W5-B1).
    /// </summary>
    FastPathSlotsFromMaxLocks,

    // ---- 19 (release notes as of 2026-10-09; the matrix runs its release candidate) --------------------------------

    /// <summary>
    /// <c>stats_reset</c> in <c>pg_stat_all_tables</c>, <c>pg_stat_all_indexes</c> and <c>pg_statio_all_sequences</c>:
    /// since when one table's counters count (19 release notes, E.1.3.1.3 "System Views").
    /// </summary>
    StatsResetPerTable,

    /// <summary>The <c>pg_stat_autovacuum_scores</c> view: autovacuum's own per-table verdict (19 release notes, E.1.3.1.3).</summary>
    AutovacuumScores,

    /// <summary><c>pg_get_multixact_stats()</c>: multixact activity, members included (19 release notes, E.1.3.1.4 "Monitoring").</summary>
    MultixactStats,

    /// <summary>The <c>pg_stat_lock</c> view and <c>pg_stat_get_lock()</c>: per-lock-type statistics (19 release notes, E.1.3.1.3).</summary>
    PgStatLock,

    /// <summary><c>jit</c> defaults to <c>off</c> (19 release notes, E.1.2 "Migration to Version 19"), so T23 has nothing to judge on a default server.</summary>
    JitOffByDefault,

    /// <summary>
    /// <c>max_locks_per_transaction</c> defaults to 128, and the lock table is sized differently: "effectively settings
    /// must now be doubled to match their capacity in previous releases" (19 release notes, E.1.2). A lock-table capacity
    /// read from the setting has to know which side of 19 it is on.
    /// </summary>
    MaxLocksDefault128,

    /// <summary>The wait event type <c>BUFFERPIN</c> is renamed <c>BUFFER</c> (19 release notes, E.1.2).</summary>
    WaitTypeBufferRenamed,

    /// <summary>
    /// <c>pg_stat_subscription_stats.sync_error_count</c> is renamed <c>sync_table_error_count</c>, beside a new
    /// <c>sync_seq_error_count</c> (19 release notes, E.1.2 and E.1.3.1.3).
    /// </summary>
    SubscriptionSyncTableErrorCount,
}

/// <summary>One gate: a feature and the first <c>server_version_num</c> that has it.</summary>
/// <param name="Feature">The feature.</param>
/// <param name="Since">The first <c>server_version_num</c> with it - <c>160000</c> for 16.</param>
internal sealed record PostgresFeatureGate(PostgresFeature Feature, int Since)
{
    /// <summary>The major it arrived in: 16.</summary>
    public int Major => Since / 10_000;
}

/// <summary>
/// The version facts every page and check gates on: the first PostgreSQL release with each <see cref="PostgresFeature" />,
/// and the supported floor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gate on a feature, never on a number.</b> A check that reads <c>pg_stat_io</c> asks
/// <c>PostgresFeatures.Has(PostgresFeature.PgStatIo, server.VersionNumber)</c>, and below the gate shows a row that
/// says what it needs (<see cref="Needs" />, "needs PostgreSQL 16") rather than an error. A column that only some
/// versions have is still read by name (<c>to_jsonb(v) -&gt;&gt; 'col'</c>), so one statement runs on every version;
/// the gate is what decides whether its null means "none" or "not on this version".
/// </para>
/// <para>
/// <b>The floor is a promise, not a refusal.</b> <see cref="MinimumSupported" /> is the oldest release the matrix runs
/// and the studio is tested on. A server below it is still read, read by read, and whatever fails there fails as it
/// would anywhere - a "could not check" row - with S01 and the Overview saying the server is below the floor
/// (<see cref="BelowFloor" />). PostgreSQL 14 reaches the community's end of life on 2026-11-12; the floor moves to 15
/// in a later release, never silently.
/// </para>
/// </remarks>
internal static class PostgresFeatures
{
    /// <summary>The oldest <c>server_version_num</c> the studio supports and the matrix runs: PostgreSQL 14.</summary>
    public const int MinimumSupported = 140000;

    /// <summary>The supported floor's major, as Postgres names it.</summary>
    public const int MinimumSupportedMajor = MinimumSupported / 10_000;

    /// <summary>What the Overview and S01 say of a server below the floor.</summary>
    public const string BelowFloor = "below the supported floor (14)";

    /// <summary>Every gate, oldest first and in declaration order within a release.</summary>
    public static IReadOnlyList<PostgresFeatureGate> Gates { get; } =
    [
        // 14
        new(PostgresFeature.StatementsInfo, 140000),
        new(PostgresFeature.StatementsToplevel, 140000),
        new(PostgresFeature.ActivityQueryId, 140000),
        new(PostgresFeature.PgStatWal, 140000),
        new(PostgresFeature.ReplicationSlotStats, 140000),
        new(PostgresFeature.WalReplayPauseState, 140000),
        new(PostgresFeature.LocksWaitStart, 140000),
        new(PostgresFeature.DatabaseSessionCounters, 140000),
        new(PostgresFeature.VacuumFailsafeAge, 140000),
        new(PostgresFeature.DatabaseOwnerRole, 140000),
        new(PostgresFeature.ReadAllDataRole, 140000),
        new(PostgresFeature.ReltuplesUnknownIsMinusOne, 140000),

        // 15
        new(PostgresFeature.StatementsJit, 150000),
        new(PostgresFeature.ForceNextFlush, 150000),
        new(PostgresFeature.DatabaseStatsResetNullAfterCrash, 150000),
        new(PostgresFeature.PublicSchemaOwnedByDatabaseOwner, 150000),

        // 16
        new(PostgresFeature.PgStatIo, 160000),
        new(PostgresFeature.LastScanColumns, 160000),
        new(PostgresFeature.NewPageUpdates, 160000),
        new(PostgresFeature.ExplainGenericPlan, 160000),
        new(PostgresFeature.SlotConflicting, 160000),
        new(PostgresFeature.ReservedConnections, 160000),
        new(PostgresFeature.PartitionedIndexLocks, 160000),

        // 17
        new(PostgresFeature.PgStatCheckpointer, 170000),
        new(PostgresFeature.SlotInactiveSince, 170000),
        new(PostgresFeature.ProgressIndexesTotal, 170000),
        new(PostgresFeature.StatementsSharedBlkTimeNames, 170000),
        new(PostgresFeature.WaitEventsView, 170000),
        new(PostgresFeature.MaintainRole, 170000),
        new(PostgresFeature.ExplainGenerationObject, 170000),
        new(PostgresFeature.TransactionTimeout, 170000),

        // 18
        new(PostgresFeature.RelAllFrozen, 180000),
        new(PostgresFeature.VacuumMaxThreshold, 180000),
        new(PostgresFeature.PgStatIoBytes, 180000),
        new(PostgresFeature.PgStatIoWal, 180000),
        new(PostgresFeature.CheckpointerNumDone, 180000),
        new(PostgresFeature.NotNullConstraints, 180000),
        new(PostgresFeature.SequencesViewLocksAll, 180000),
        new(PostgresFeature.ExplainFractionalRows, 180000),
        new(PostgresFeature.ExplainBuffersByDefault, 180000),
        new(PostgresFeature.IdleSlotTimeout, 180000),
        new(PostgresFeature.ChecksumsDefaultOn, 180000),
        new(PostgresFeature.TotalVacuumTime, 180000),
        new(PostgresFeature.FastPathSlotsFromMaxLocks, 180000),

        // 19
        new(PostgresFeature.StatsResetPerTable, 190000),
        new(PostgresFeature.AutovacuumScores, 190000),
        new(PostgresFeature.MultixactStats, 190000),
        new(PostgresFeature.PgStatLock, 190000),
        new(PostgresFeature.JitOffByDefault, 190000),
        new(PostgresFeature.MaxLocksDefault128, 190000),
        new(PostgresFeature.WaitTypeBufferRenamed, 190000),
        new(PostgresFeature.SubscriptionSyncTableErrorCount, 190000),
    ];

    private static readonly Dictionary<PostgresFeature, int> SinceByFeature = Gates.ToDictionary(static x => x.Feature, static x => x.Since);

    /// <summary>The first <c>server_version_num</c> with <paramref name="feature" />.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The feature has no gate - which <c>PostgresFeaturesTests</c> forbids.</exception>
    public static int Since(PostgresFeature feature) =>
        SinceByFeature.TryGetValue(feature, out int since)
            ? since
            : throw new ArgumentOutOfRangeException(nameof(feature), feature, "This feature has no row in PostgresFeatures.Gates.");

    /// <summary>Whether a server of <paramref name="versionNumber" /> (<c>server_version_num</c>) has <paramref name="feature" />.</summary>
    public static bool Has(PostgresFeature feature, int versionNumber) => versionNumber >= Since(feature);

    /// <summary>Whether a server of <paramref name="versionNumber" /> is below <see cref="MinimumSupported" />.</summary>
    public static bool IsBelowFloor(int versionNumber) => versionNumber < MinimumSupported;

    /// <summary>
    /// Whether a server whose version reads <paramref name="version" /> (<c>13.23</c>, as a studio's own version read
    /// prints it) is below the floor; <see langword="false" /> for a version that was not read.
    /// </summary>
    public static bool IsBelowFloor(Version? version) => version is not null && version.Major < MinimumSupportedMajor;

    /// <summary>
    /// What a row below a gate says: "needs PostgreSQL 16" - the version, never an error.
    /// </summary>
    public static string Needs(PostgresFeature feature) =>
        "needs PostgreSQL " + (Since(feature) / 10_000).ToString(CultureInfo.InvariantCulture);
}
