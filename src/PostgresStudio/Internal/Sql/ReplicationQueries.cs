using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The statements of the Replication page and of R02-R04 (W5-R2, issue #8 G8): the server's recovery state, the standbys
/// streaming from it, its slots' statistics, the archiver, WAL volume, checkpoints, <c>pg_stat_io</c> and the tablespaces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared memory and the statistics snapshot; no relation is locked.</b> <c>pg_stat_replication</c> copies the WAL
/// senders' state, <c>pg_stat_archiver</c>, <c>pg_stat_wal</c>, <c>pg_stat_bgwriter</c>, <c>pg_stat_checkpointer</c> and
/// <c>pg_stat_io</c> read the cumulative statistics, <c>pg_replication_slots</c> and <c>pg_stat_replication_slots</c> the
/// slots' shared state, and the recovery functions the startup process's. None of them opens a relation it lists (B0 G8:
/// every one is readable by a role in no monitoring group, <c>pg_aios</c> aside, which nothing here reads). The one size
/// function, <see cref="TablespaceSize" />, is a single named tablespace behind a button and never part of a list (D27).
/// </para>
/// <para>
/// <b>Version-proof by name, gated by release.</b> A view that a release added - <c>pg_stat_wal</c> (14),
/// <c>pg_stat_replication_slots</c> (14), <c>pg_stat_io</c> (16), <c>pg_stat_checkpointer</c> (17) - is named only by a
/// builder its caller chose with <c>PostgresFeatures</c>, because naming a missing view is <c>42P01</c>; and every column
/// that moved between releases is read through <c>pg_catalog.to_jsonb(v) -&gt;&gt; 'column'</c>, which is null where the
/// column is not, so one statement serves every release that has the view.
/// </para>
/// <para>
/// <b>No identifier of the store's, ever.</b> Nothing here names a relation of the database, so Show SQL carries these
/// statements to anybody the check's gate passes. Every limit and name is a parameter; a setting's name written as a
/// literal is structure, as it is in <see cref="HealthServerQueries" />.
/// </para>
/// </remarks>
internal static class ReplicationQueries
{
    /// <summary>The <c>context.Shared</c> key of the recovery read, which R02, R03 and the page share.</summary>
    public const string RoleKey = "replication.role";

    /// <summary>The <c>context.Shared</c> key of the standby read.</summary>
    public const string StandbysKey = "replication.standbys";

    /// <summary>The <c>context.Shared</c> key of the settings read.</summary>
    public const string SettingsKey = "replication.settings";

    /// <summary>The <c>context.Shared</c> key of the archiver read.</summary>
    public const string ArchiverKey = "replication.archiver";

    /// <summary>The <c>context.Shared</c> key of the <c>pg_wal</c> directory read (<c>pg_monitor</c>).</summary>
    public const string WalDirectoryKey = "replication.wal-directory";

    /// <summary>The <c>context.Shared</c> key of the WAL statistics read.</summary>
    public const string WalKey = "replication.wal";

    /// <summary>The <c>context.Shared</c> key of the checkpoint read.</summary>
    public const string CheckpointsKey = "replication.checkpoints";

    /// <summary>The <c>context.Shared</c> key of the <c>pg_stat_io</c> read.</summary>
    public const string IoKey = "replication.io";

    /// <summary>The <c>context.Shared</c> key of the slot statistics read.</summary>
    public const string SlotStatisticsKey = "replication.slot-statistics";

    /// <summary>The <c>context.Shared</c> key of the tablespace read.</summary>
    public const string TablespacesKey = "replication.tablespaces";

    /// <summary>The most standbys one read lists; the count says how many there are.</summary>
    public const int MaxStandbys = 100;

    /// <summary>The most slots whose statistics one read lists - R01's own bound.</summary>
    public const int MaxSlotStatistics = HealthServerQueries.MaxSlots;

    /// <summary>The most <c>pg_stat_io</c> groups one read lists (18 has about forty, by backend type and object).</summary>
    public const int MaxIoGroups = 200;

    /// <summary>The most tablespaces one read lists; the count says how many there are.</summary>
    public const int MaxTablespaces = 100;

    /// <summary>
    /// The settings the page and the checks read, by name: a missing row is a setting this release does not have
    /// (<c>archive_library</c> before 15, <c>idle_replication_slot_timeout</c> before 18) or one this role may not see.
    /// </summary>
    public static IReadOnlyList<string> SettingNames { get; } =
    [
        "archive_mode", "archive_command", "archive_library", "archive_timeout", "wal_segment_size", "wal_level",
        "checkpoint_timeout", "max_wal_size", "min_wal_size", "checkpoint_completion_target",
        "max_wal_senders", "max_replication_slots", "hot_standby_feedback", "idle_replication_slot_timeout",
    ];

    private const string LsnDiff = "pg_catalog.pg_wal_lsn_diff";

    /// <summary>
    /// Whether the server is in recovery and, when it is, how far its replay is behind what it has received - exact and
    /// unprivileged on a standby (B0 §4) - its last replayed commit's time, and (from 14) whether replay is paused.
    /// </summary>
    /// <param name="pauseState">
    /// Whether the server has <c>pg_get_wal_replay_pause_state()</c> (PostgreSQL 14+). It raises "recovery is not in
    /// progress" on a primary, so it is called only in recovery, inside the <c>case</c>.
    /// </param>
    public static HealthQuery Recovery(bool pauseState) => new(
        "select pg_catalog.pg_is_in_recovery() as in_recovery,\n" +
        "       case when pg_catalog.pg_is_in_recovery() then pg_catalog.pg_last_wal_receive_lsn()::text end as receive_lsn,\n" +
        "       case when pg_catalog.pg_is_in_recovery() then pg_catalog.pg_last_wal_replay_lsn()::text end as replay_lsn,\n" +
        "       case when pg_catalog.pg_is_in_recovery()\n" +
        "            then " + LsnDiff + "(pg_catalog.pg_last_wal_receive_lsn(), pg_catalog.pg_last_wal_replay_lsn())::float8 end as replay_behind,\n" +
        "       case when pg_catalog.pg_is_in_recovery() then pg_catalog.pg_last_xact_replay_timestamp() end as last_replay,\n" +
        "       case when pg_catalog.pg_is_in_recovery()\n" +
        "            then extract(epoch from (pg_catalog.now() - pg_catalog.pg_last_xact_replay_timestamp()))::float8 end as replay_age,\n" +
        "       " + (pauseState
            ? "case when pg_catalog.pg_is_in_recovery() then pg_catalog.pg_get_wal_replay_pause_state() end"
            : "null::text") + " as pause_state");

    /// <summary>
    /// The WAL senders: one row per standby (or <c>pg_basebackup</c>) streaming from this server, how far each has
    /// replayed and its lag times - most behind first, with the total on every row and one row of nulls when there are
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What a role without <c>pg_read_all_stats</c> sees</b> (B0 §4): <c>pid</c>, <c>usename</c> and
    /// <c>application_name</c> of every sender, and every other column null - so <c>state is not null</c> is the row's own
    /// "this role may read it", which a sender of the reading role's own is too.
    /// </para>
    /// <para>
    /// <b>Bytes behind</b> are the server's own WAL position less the standby's replay position (on a cascading standby,
    /// its own replay position), and <c>sent_lsn - replay_lsn</c> beside it: the first counts what was never sent as well.
    /// </para>
    /// </remarks>
    public static HealthQuery Standbys() => new(
        "with r as (\n" +
        "  select r.application_name::text as application_name, pg_catalog.host(r.client_addr) as client_addr,\n" +
        "         r.usename::text as usename, r.state::text as state, r.sync_state::text as sync_state,\n" +
        "         case when pg_catalog.pg_is_in_recovery() then " + LsnDiff + "(pg_catalog.pg_last_wal_replay_lsn(), r.replay_lsn)\n" +
        "              else " + LsnDiff + "(pg_catalog.pg_current_wal_lsn(), r.replay_lsn) end::float8 as behind,\n" +
        "         " + LsnDiff + "(r.sent_lsn, r.replay_lsn)::float8 as sent_behind,\n" +
        "         extract(epoch from r.write_lag)::float8 as write_lag,\n" +
        "         extract(epoch from r.flush_lag)::float8 as flush_lag,\n" +
        "         extract(epoch from r.replay_lag)::float8 as replay_lag\n" +
        "    from pg_catalog.pg_stat_replication r\n" +
        "),\n" +
        "total as (select pg_catalog.count(*)::int8 as n from r)\n" +
        "select t.n, x.application_name, x.client_addr, x.usename, x.state, x.sync_state,\n" +
        "       x.behind, x.sent_behind, x.write_lag, x.flush_lag, x.replay_lag\n" +
        "  from total t\n" +
        "  left join lateral (\n" +
        "    select * from r order by (r.state is null), r.behind desc nulls last, r.replay_lag desc nulls last, r.application_name\n" +
        "    limit @limit) x on true",
        [Integer("limit", MaxStandbys)]);

    /// <summary>
    /// What <c>pg_replication_slots</c> adds beyond R01's read (<c>failover</c> and <c>synced</c>, 17+) and each logical
    /// slot's decoding statistics from <c>pg_stat_replication_slots</c> (14+), by slot name: the statistics are joined to
    /// R01's <c>ReplicationSlotSnapshot</c> in code, never shown by name past its rule.
    /// </summary>
    /// <param name="statistics">Whether the server has <c>pg_stat_replication_slots</c> (PostgreSQL 14+).</param>
    public static HealthQuery SlotStatistics(bool statistics) => new(
        "select s.slot_name::text as slot_name,\n" +
        "       (x.j ->> 'failover')::bool as failover,\n" +
        "       (x.j ->> 'synced')::bool as synced,\n" +
        (statistics
            ? "       (y.j ->> 'spill_txns')::int8 as spill_txns,\n" +
              "       (y.j ->> 'spill_bytes')::int8 as spill_bytes,\n" +
              "       (y.j ->> 'stream_txns')::int8 as stream_txns,\n" +
              "       (y.j ->> 'stream_bytes')::int8 as stream_bytes,\n" +
              "       (y.j ->> 'total_txns')::int8 as total_txns,\n" +
              "       (y.j ->> 'total_bytes')::int8 as total_bytes\n" +
              "  from pg_catalog.pg_replication_slots s\n" +
              "  left join pg_catalog.pg_stat_replication_slots st on st.slot_name = s.slot_name\n" +
              " cross join lateral (select pg_catalog.to_jsonb(s) as j) x\n" +
              " cross join lateral (select pg_catalog.to_jsonb(st) as j) y\n"
            : "       null::int8 as spill_txns, null::int8 as spill_bytes, null::int8 as stream_txns,\n" +
              "       null::int8 as stream_bytes, null::int8 as total_txns, null::int8 as total_bytes\n" +
              "  from pg_catalog.pg_replication_slots s\n" +
              " cross join lateral (select pg_catalog.to_jsonb(s) as j) x\n") +
        " order by s.slot_name\n" +
        " limit @limit",
        [Integer("limit", MaxSlotStatistics)]);

    /// <summary>
    /// The settings in <see cref="SettingNames" /> this role may see: name, value, unit and context - <c>context</c> being
    /// what a server-setting remedy reads to know whether a change waits for a restart.
    /// </summary>
    public static HealthQuery Settings() => new(
        "select s.name, s.setting, s.unit, s.context\n" +
        "  from pg_catalog.pg_settings s\n" +
        " where s.name = any(@names)\n" +
        " order by s.name",
        [new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = SettingNames.ToArray() }]);

    /// <summary>
    /// The archiver's counters (<c>pg_stat_archiver</c>, the same columns on every release this studio supports), how long
    /// ago its last success and its last failure were by the server's clock, and - on a server not in recovery - the WAL
    /// file the server is writing now, which R02's backlog is measured to. <c>pg_walfile_name</c> raises in recovery, so it
    /// is called only inside the <c>case</c>.
    /// </summary>
    public static HealthQuery Archiver() => new(
        "select a.archived_count, a.last_archived_wal, a.last_archived_time,\n" +
        "       extract(epoch from (pg_catalog.now() - a.last_archived_time))::float8 as archived_age,\n" +
        "       a.failed_count, a.last_failed_wal, a.last_failed_time,\n" +
        "       extract(epoch from (pg_catalog.now() - a.last_failed_time))::float8 as failed_age,\n" +
        "       a.stats_reset,\n" +
        "       pg_catalog.pg_is_in_recovery() as in_recovery,\n" +
        "       case when not pg_catalog.pg_is_in_recovery() then pg_catalog.pg_walfile_name(pg_catalog.pg_current_wal_lsn()) end as current_wal\n" +
        "  from pg_catalog.pg_stat_archiver a");

    /// <summary>
    /// How many WAL files wait in <c>archive_status</c> for the archiver (<c>.ready</c>), and the size and number of the
    /// files in <c>pg_wal</c>. Both functions are restricted to superusers and members of <c>pg_monitor</c>, so the
    /// caller runs this only for a role that has it; it is evidence, never a trigger.
    /// </summary>
    public static HealthQuery WalDirectory() => new(
        "select (select pg_catalog.count(*) from pg_catalog.pg_ls_archive_statusdir() f where f.name like '%.ready')::int8 as ready,\n" +
        "       (select pg_catalog.count(*) from pg_catalog.pg_ls_waldir() w)::int8 as files,\n" +
        "       (select coalesce(pg_catalog.sum(w.size), 0) from pg_catalog.pg_ls_waldir() w)::int8 as bytes");

    /// <summary>
    /// <c>pg_stat_wal</c> (PostgreSQL 14+), by name: records, full-page images, bytes and full WAL buffers on every
    /// release, and the write and sync counters that 18 moved to <c>pg_stat_io</c>, null there.
    /// </summary>
    public static HealthQuery Wal() => new(
        "select (x.j ->> 'wal_records')::int8 as wal_records,\n" +
        "       (x.j ->> 'wal_fpi')::int8 as wal_fpi,\n" +
        "       (x.j ->> 'wal_bytes')::numeric::float8 as wal_bytes,\n" +
        "       (x.j ->> 'wal_buffers_full')::int8 as wal_buffers_full,\n" +
        "       (x.j ->> 'wal_write')::int8 as wal_write,\n" +
        "       (x.j ->> 'wal_sync')::int8 as wal_sync,\n" +
        "       (x.j ->> 'wal_write_time')::float8 as wal_write_time,\n" +
        "       (x.j ->> 'wal_sync_time')::float8 as wal_sync_time,\n" +
        "       (x.j ->> 'stats_reset')::timestamptz as stats_reset\n" +
        "  from pg_catalog.pg_stat_wal w\n" +
        " cross join lateral (select pg_catalog.to_jsonb(w) as j) x");

    /// <summary>
    /// The checkpoint counters: from <c>pg_stat_bgwriter</c> up to 16, and from <c>pg_stat_checkpointer</c> from 17 -
    /// with 18's <c>num_done</c> by name - beside the background writer's own counters, which stay in
    /// <c>pg_stat_bgwriter</c> (17 drops <c>buffers_backend</c> from it, and reads null here).
    /// </summary>
    /// <param name="checkpointer">Whether the server has <c>pg_stat_checkpointer</c> (PostgreSQL 17+).</param>
    public static HealthQuery Checkpoints(bool checkpointer) => new(
        checkpointer
            ? "select 'pg_stat_checkpointer' as source,\n" +
              "       (c.j ->> 'num_timed')::int8 as timed,\n" +
              "       (c.j ->> 'num_requested')::int8 as requested,\n" +
              "       (c.j ->> 'num_done')::int8 as done,\n" +
              "       (c.j ->> 'restartpoints_timed')::int8 as restartpoints_timed,\n" +
              "       (c.j ->> 'restartpoints_req')::int8 as restartpoints_requested,\n" +
              "       (c.j ->> 'restartpoints_done')::int8 as restartpoints_done,\n" +
              "       (c.j ->> 'write_time')::float8 as write_time,\n" +
              "       (c.j ->> 'sync_time')::float8 as sync_time,\n" +
              "       (c.j ->> 'buffers_written')::int8 as buffers_written,\n" +
              "       (b.j ->> 'buffers_clean')::int8 as buffers_clean,\n" +
              "       (b.j ->> 'maxwritten_clean')::int8 as maxwritten_clean,\n" +
              "       (b.j ->> 'buffers_backend')::int8 as buffers_backend,\n" +
              "       (b.j ->> 'buffers_alloc')::int8 as buffers_alloc,\n" +
              "       (c.j ->> 'stats_reset')::timestamptz as stats_reset\n" +
              "  from (select pg_catalog.to_jsonb(v) as j from pg_catalog.pg_stat_checkpointer v) c\n" +
              " cross join (select pg_catalog.to_jsonb(v) as j from pg_catalog.pg_stat_bgwriter v) b"
            : "select 'pg_stat_bgwriter' as source,\n" +
              "       (b.j ->> 'checkpoints_timed')::int8 as timed,\n" +
              "       (b.j ->> 'checkpoints_req')::int8 as requested,\n" +
              "       null::int8 as done,\n" +
              "       null::int8 as restartpoints_timed, null::int8 as restartpoints_requested, null::int8 as restartpoints_done,\n" +
              "       (b.j ->> 'checkpoint_write_time')::float8 as write_time,\n" +
              "       (b.j ->> 'checkpoint_sync_time')::float8 as sync_time,\n" +
              "       (b.j ->> 'buffers_checkpoint')::int8 as buffers_written,\n" +
              "       (b.j ->> 'buffers_clean')::int8 as buffers_clean,\n" +
              "       (b.j ->> 'maxwritten_clean')::int8 as maxwritten_clean,\n" +
              "       (b.j ->> 'buffers_backend')::int8 as buffers_backend,\n" +
              "       (b.j ->> 'buffers_alloc')::int8 as buffers_alloc,\n" +
              "       (b.j ->> 'stats_reset')::timestamptz as stats_reset\n" +
              "  from (select pg_catalog.to_jsonb(v) as j from pg_catalog.pg_stat_bgwriter v) b");

    /// <summary>
    /// <c>pg_stat_io</c> (PostgreSQL 16+), summed by backend type and object: operation counts, and bytes - 18's own
    /// <c>read_bytes</c>, <c>write_bytes</c> and <c>extend_bytes</c>, or on 16 and 17 the count times <c>op_bytes</c>
    /// (which 18 drops). 18's WAL rows (<c>object = 'wal'</c>) are groups of their own.
    /// </summary>
    public static HealthQuery Io() => new(
        "select x.backend_type, x.object,\n" +
        "       pg_catalog.sum((x.j ->> 'reads')::numeric)::float8 as reads,\n" +
        "       pg_catalog.sum((x.j ->> 'writes')::numeric)::float8 as writes,\n" +
        "       pg_catalog.sum((x.j ->> 'extends')::numeric)::float8 as extends,\n" +
        "       pg_catalog.sum((x.j ->> 'hits')::numeric)::float8 as hits,\n" +
        "       pg_catalog.sum((x.j ->> 'evictions')::numeric)::float8 as evictions,\n" +
        "       pg_catalog.sum((x.j ->> 'fsyncs')::numeric)::float8 as fsyncs,\n" +
        "       pg_catalog.sum(coalesce((x.j ->> 'read_bytes')::numeric, (x.j ->> 'reads')::numeric * (x.j ->> 'op_bytes')::numeric))::float8 as read_bytes,\n" +
        "       pg_catalog.sum(coalesce((x.j ->> 'write_bytes')::numeric, (x.j ->> 'writes')::numeric * (x.j ->> 'op_bytes')::numeric))::float8 as write_bytes,\n" +
        "       pg_catalog.sum(coalesce((x.j ->> 'extend_bytes')::numeric, (x.j ->> 'extends')::numeric * (x.j ->> 'op_bytes')::numeric))::float8 as extend_bytes,\n" +
        "       pg_catalog.sum((x.j ->> 'read_time')::float8) as read_time,\n" +
        "       pg_catalog.sum((x.j ->> 'write_time')::float8) as write_time\n" +
        "  from (select i.backend_type::text as backend_type, i.object::text as object, pg_catalog.to_jsonb(i) as j\n" +
        "          from pg_catalog.pg_stat_io i) x\n" +
        " group by x.backend_type, x.object\n" +
        " order by x.backend_type, x.object\n" +
        " limit @limit",
        [Integer("limit", MaxIoGroups)]);

    /// <summary>
    /// The tablespaces: name, location (empty for <c>pg_default</c> and <c>pg_global</c>, which live in the data
    /// directory) and whether it is this database's default - never a size, which is <see cref="TablespaceSize" />'s
    /// alone, one at a time.
    /// </summary>
    public static HealthQuery Tablespaces() => new(
        "with t as (\n" +
        "  select t.spcname::text as name, pg_catalog.pg_tablespace_location(t.oid) as location,\n" +
        "         t.oid = (select d.dattablespace from pg_catalog.pg_database d where d.datname = pg_catalog.current_database()) as this_database,\n" +
        "         pg_catalog.count(*) over () as total\n" +
        "    from pg_catalog.pg_tablespace t\n" +
        ")\n" +
        "select t.name, t.location, t.this_database, t.total from t order by t.name limit @limit",
        [Integer("limit", MaxTablespaces)]);

    /// <summary>
    /// One tablespace's size, on demand: <c>pg_tablespace_size</c> walks its directory - no relation lock, but never in a
    /// list - and refuses (<c>42501</c>) a role without <c>CREATE</c> on it or <c>pg_read_all_stats</c>, other than for
    /// this database's default (B0 G8: <c>pg_global</c> is denied to an unprivileged role). No row: no such tablespace.
    /// </summary>
    /// <param name="name">The tablespace, as <see cref="Tablespaces" /> listed it.</param>
    public static HealthQuery TablespaceSize(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new HealthQuery(
            "select pg_catalog.pg_tablespace_size(t.oid)::int8 from pg_catalog.pg_tablespace t where t.spcname = @name",
            [new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name }]);
    }

    private static NpgsqlParameter Integer(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };
}
