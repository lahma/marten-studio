using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The statements of the replication and server checks (R01, S01-S03), the managed-platform detector and C10's
/// pooler probe: reads of <c>pg_replication_slots</c>, <c>pg_settings</c>, <c>pg_stat_database</c>, <c>pg_roles</c>
/// and <c>pg_proc</c>, and of the server's own functions for its version, its WAL position and the backend's
/// process id - nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Catalog and shared memory, and no lock beyond a catalog read.</b> <c>pg_replication_slots</c> is a view over
/// <c>pg_get_replication_slots()</c>, which copies each slot's state out of shared memory under the slot control lock
/// for a moment; <c>pg_settings</c> and <c>pg_stat_database</c> read the session's own settings and the statistics
/// snapshot. None of them takes a relation lock on anything they list, and none is a per-object function over a
/// list (D27). The live no-lock sweep (<c>ServerSweepNoDdlLiveTests</c>) is what proves it.
/// </para>
/// <para>
/// <b>Version-proof without a second round trip.</b> <c>wal_status</c> and <c>safe_wal_size</c> exist from
/// PostgreSQL 13, <c>conflicting</c> from 16, <c>inactive_since</c> and <c>invalidation_reason</c> from 17 (the
/// PostgreSQL documentation of <c>pg_replication_slots</c> for each release). Naming a missing column is
/// <c>42703</c>, so every one of them is read through <c>to_jsonb(s) -&gt;&gt; '…'</c>, which is simply null on an
/// older server, as <see cref="HealthConnectionQueries" /> reads <c>pg_locks.waitstart</c>. A setting that does
/// not exist is a missing <c>pg_settings</c> row, which is null too.
/// </para>
/// <para>
/// <b>The primary's WAL position.</b> The health connection reads the primary (<c>ConnectionUsage.ReadWrite</c>), and
/// <c>pg_current_wal_lsn()</c> is what a slot's retention is measured against there. A server in recovery cannot
/// answer it, so a standby is measured against its last replayed position instead, and says so.
/// </para>
/// <para>
/// <b>No table name, ever.</b> Nothing here names a relation of the store's, so Show SQL can carry these statements
/// to anybody the check's gate passes. Every value is a parameter, every limit included; a setting's or a role's
/// name written as a literal is structure, as <c>current_setting('max_connections')</c> is in the connection
/// builder.
/// </para>
/// </remarks>
internal static class HealthServerQueries
{
    /// <summary>The <c>context.Shared</c> key of the replication-slot read, which T14 reads too (R1, H9).</summary>
    public const string ReplicationSlotsKey = "server.replication-slots";

    /// <summary>The <c>context.Shared</c> key of the version and platform read, which S01, S02 and R01 share.</summary>
    public const string PlatformKey = "server.platform";

    /// <summary>The most slots one read lists; the count says how many there are.</summary>
    public const int MaxSlots = 200;

    /// <summary>The most settings one read lists - the named ones and every one waiting for a restart.</summary>
    public const int MaxSettings = 100;

    /// <summary>
    /// A size setting in bytes from its <c>pg_settings</c> row, <c>-1</c> for "no limit", or null when the server has
    /// no such setting. The units the WAL sizes are kept in are <c>MB</c>; the others are here so a later release that
    /// changes one is read rightly rather than off by a factor.
    /// </summary>
    private static string SizeSetting(string name) =>
        "(select case when s.setting::int8 < 0 then -1::int8\n" +
        "             when s.unit = 'MB' then s.setting::int8 * 1048576\n" +
        "             when s.unit = 'kB' then s.setting::int8 * 1024\n" +
        "             when s.unit = 'GB' then s.setting::int8 * 1073741824\n" +
        "             else s.setting::int8 end\n" +
        "   from pg_catalog.pg_settings s where s.name = '" + name + "')";

    private static readonly HealthQuery PlatformQuery = new(
        "select pg_catalog.current_setting('server_version_num')::int4 as server_version_num,\n" +
        "       pg_catalog.current_setting('server_version') as server_version,\n" +
        "       exists (select 1 from pg_catalog.pg_settings s where s.name in ('rds.extensions', 'rds.allowed_extensions')) as rds,\n" +
        "       exists (select 1 from pg_catalog.pg_proc p where p.proname = 'aurora_version') as aurora,\n" +
        "       exists (select 1 from pg_catalog.pg_settings s where s.name = 'azure.extensions') as azure,\n" +
        "       exists (select 1 from pg_catalog.pg_settings s where s.name like 'cloudsql.%') as cloud_sql,\n" +
        "       exists (select 1 from pg_catalog.pg_roles r where r.rolname = 'neon_superuser') as neon");

    private static readonly HealthQuery ChecksumsQuery = new(
        "select pg_catalog.current_setting('data_checksums') = 'on' as enabled,\n" +
        "       (pg_catalog.sum(d.checksum_failures) filter (where d.datname = pg_catalog.current_database()))::int8 as this_database,\n" +
        "       pg_catalog.max(d.checksum_last_failure) filter (where d.datname = pg_catalog.current_database()) as this_last,\n" +
        "       (pg_catalog.sum(d.checksum_failures) filter (where d.datid <> 0 and d.datname is distinct from pg_catalog.current_database()))::int8 as other_databases,\n" +
        "       pg_catalog.count(*) filter (where d.datid <> 0 and d.datname is distinct from pg_catalog.current_database() and d.checksum_failures > 0) as other_databases_failing,\n" +
        "       pg_catalog.max(d.checksum_last_failure) filter (where d.datid <> 0 and d.datname is distinct from pg_catalog.current_database()) as other_last,\n" +
        "       (pg_catalog.sum(d.checksum_failures) filter (where d.datid = 0))::int8 as shared_objects,\n" +
        "       pg_catalog.max(d.checksum_last_failure) filter (where d.datid = 0) as shared_last,\n" +
        "       pg_catalog.max(d.stats_reset) filter (where d.datname = pg_catalog.current_database()) as stats_reset\n" +
        "  from pg_catalog.pg_stat_database d");

    private static readonly HealthQuery BackendProcessQuery = new("select pg_catalog.pg_backend_pid()");

    /// <summary>
    /// The server's version and the markers the managed-platform detector reads (<c>PostgresPlatform</c>): the
    /// <c>rds.extensions</c> / <c>rds.allowed_extensions</c> settings of Amazon RDS and Aurora, Aurora's
    /// <c>aurora_version()</c> function, Azure's <c>azure.extensions</c> setting, Cloud SQL's <c>cloudsql.*</c> flags
    /// and Neon's <c>neon_superuser</c> role. Each one is an existence test against the catalog, so a marker that is
    /// absent - every one of them on a server of somebody's own - costs nothing and errs nowhere.
    /// </summary>
    public static HealthQuery Platform() => PlatformQuery;

    /// <summary>
    /// Every replication slot on the server, most WAL retained first, with the server's settings that bound what a
    /// slot may hold - one row with nulls for the slot columns when there is none, so the settings are read either
    /// way. R01, and T14's horizon (<c>xmin</c> and <c>catalog_xmin</c> ages).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server's, not the database's: a physical slot belongs to no database, and a logical slot for another
    /// database still holds WAL every database on the server writes - so every slot is read, and counted.
    /// </para>
    /// <para>
    /// <b>Named only where this database may name it.</b> <c>this_database</c> is true for a physical slot (it belongs
    /// to no database, so it is the server's, and the database-wide gate's) and for a logical slot of the connected
    /// database, and false for a logical slot of any other database. The name, plugin and database of a slot that is
    /// not <c>this_database</c> are another database's - a database-per-tenant store's other tenant, or another
    /// application's - and a visitor admitted to this database is never shown them (H10 review, B1). They are still
    /// read, because a row is a row, and the check withholds them.
    /// </para>
    /// </remarks>
    public static HealthQuery ReplicationSlots() => new(
        "with server as (\n" +
        "  select pg_catalog.current_setting('server_version_num')::int4 as server_version,\n" +
        "         pg_catalog.pg_is_in_recovery() as in_recovery,\n" +
        "         case when pg_catalog.pg_is_in_recovery() then pg_catalog.pg_last_wal_replay_lsn()\n" +
        "              else pg_catalog.pg_current_wal_lsn() end as current_lsn,\n" +
        "         " + SizeSetting("max_slot_wal_keep_size") + " as max_slot_wal_keep_bytes,\n" +
        "         " + SizeSetting("wal_keep_size") + " as wal_keep_bytes\n" +
        "),\n" +
        "slots as (\n" +
        "  select s.slot_name::text as slot_name, s.slot_type, s.plugin::text as plugin, s.database::text as database,\n" +
        "         s.temporary, s.active,\n" +
        "         pg_catalog.pg_wal_lsn_diff(v.current_lsn, s.restart_lsn)::float8 as retained_bytes,\n" +
        "         x.j ->> 'wal_status' as wal_status,\n" +
        "         (x.j ->> 'safe_wal_size')::int8 as safe_wal_size,\n" +
        "         pg_catalog.age(s.xmin) as xmin_age,\n" +
        "         pg_catalog.age(s.catalog_xmin) as catalog_xmin_age,\n" +
        "         extract(epoch from (pg_catalog.now() - (x.j ->> 'inactive_since')::timestamptz))::float8 as inactive_seconds,\n" +
        "         x.j ->> 'invalidation_reason' as invalidation_reason,\n" +
        "         (x.j ->> 'conflicting')::bool as conflicting,\n" +
        "         pg_catalog.count(*) over () as total,\n" +
        "         (s.database is null or s.database = pg_catalog.current_database()) as this_database\n" +
        "    from pg_catalog.pg_replication_slots s\n" +
        "   cross join server v\n" +
        "   cross join lateral (select pg_catalog.to_jsonb(s) as j) x\n" +
        "   order by retained_bytes desc nulls last, s.slot_name\n" +
        "   limit @limit\n" +
        ")\n" +
        "select v.server_version, v.in_recovery, v.max_slot_wal_keep_bytes, v.wal_keep_bytes,\n" +
        "       r.slot_name, r.slot_type, r.plugin, r.database, r.temporary, r.active, r.retained_bytes,\n" +
        "       r.wal_status, r.safe_wal_size, r.xmin_age, r.catalog_xmin_age, r.inactive_seconds,\n" +
        "       r.invalidation_reason, r.conflicting, r.total, r.this_database\n" +
        "  from server v left join slots r on true",
        [Integer("limit", MaxSlots)]);

    /// <summary>
    /// The named settings as this session sees them - which is what the store's own sessions see, because they open
    /// from the same data source - with where each was set, and every setting whose change waits for a restart. S02.
    /// Each with its <c>context</c>, which is how a server-setting remedy knows whether a change needs a restart
    /// (<c>ServerSettingRemedy</c>).
    /// </summary>
    /// <remarks>
    /// <c>pg_settings</c> shows a role that is neither a superuser nor a member of <c>pg_read_all_settings</c> every
    /// setting this list asks for, and only hides <c>sourcefile</c> and <c>sourceline</c>, which are not read. A
    /// setting a later release drops is simply a missing row.
    /// </remarks>
    /// <param name="names">The settings to read.</param>
    public static HealthQuery Settings(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        return new HealthQuery(
            "select s.name, s.setting, s.source, s.pending_restart, pg_catalog.count(*) over () as total, s.context\n" +
            "  from pg_catalog.pg_settings s\n" +
            " where s.name = any(@names) or s.pending_restart\n" +
            " order by s.name\n" +
            " limit @limit",
            [
                new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names.ToArray() },
                Integer("limit", MaxSettings),
            ]);
    }

    /// <summary>
    /// Whether the server checks page checksums, and how many checksum failures <c>pg_stat_database</c> has counted -
    /// in this database, in every other one together (counted, never named), and in the shared catalogs - with the
    /// latest of each and this database's <c>stats_reset</c>. S03.
    /// </summary>
    /// <remarks>
    /// <c>checksum_failures</c> exists from PostgreSQL 12 and is null when checksums are off, which is what makes the
    /// sums null rather than zero there.
    /// </remarks>
    public static HealthQuery Checksums() => ChecksumsQuery;

    /// <summary>
    /// The server process the current transaction runs on. C10 runs it in two consecutive read-only transactions on
    /// one connection: behind a transaction-mode pooler the second may land on another server process.
    /// </summary>
    public static HealthQuery BackendProcess() => BackendProcessQuery;

    private static NpgsqlParameter Integer(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };
}
