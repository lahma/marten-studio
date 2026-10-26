using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The statements of Aurora's own diagnostic functions (issue #8 A1, A3, A4; W5-AUR): which instance answered, the replica
/// status of every instance, the instance's cumulative waits, this database's commit latency and DML activity, and - behind
/// the activity gate only - the waits of this database's sessions. Run only once the platform read
/// (<see cref="HealthServerQueries.Platform" />) has found <c>aurora_version()</c> in <c>pg_catalog</c>: no other server has
/// these functions, and naming one that does not exist is <c>42883</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every function by its <c>pg_catalog</c> name, every column by its name.</b> Each call is qualified, so a function of the
/// same name a database owner made in another schema is never the one read. And AWS documents these functions' columns more
/// loosely than their names (B0, "Aurora (A1-A7)"; the <i>Aurora PostgreSQL functions reference</i>, Aurora User Guide, read
/// 2026-10-26): <c>aurora_replica_status()</c>'s column set varies by release - the <i>aurora_db_instance_identifier</i>
/// page's own example has no <c>read_ios</c>, <c>iops</c> or <c>cpu</c> - <c>aurora_stat_wait_event()</c> is documented with a
/// <c>type_name</c> its example does not have, and <c>aurora_stat_get_db_commit_latency(oid)</c> is a <c>SETOF record</c> in
/// the text and a scalar in every example. So every row comes back as <c>to_jsonb(row)</c> and is read by column name
/// (<c>AuroraJson</c>): a column a release lacks is simply absent, and a scalar and a one-column record read alike.
/// </para>
/// <para>
/// <b>Called in <c>FROM</c>.</b> <c>aurora_stat_dml_activity(oid)</c> in a select list prints a record, and
/// <c>(,,,,,,,)</c> for a template database (its own page); in <c>FROM</c> its row is a row.
/// </para>
/// <para>
/// <b>Statistics and memory, no relation.</b> Each function reads the instance's own counters or its replica status manager;
/// <c>pg_database</c>, <c>pg_stat_database</c> and <c>pg_stat_activity</c> are the catalog and the statistics snapshot. Nothing
/// here opens a relation of anybody's, so nothing queues behind a migration (D27). Every limit is a parameter.
/// </para>
/// </remarks>
internal static class AuroraQueries
{
    /// <summary>The most instances one read lists: a cluster has a writer and up to fifteen readers.</summary>
    public const int MaxInstances = 32;

    /// <summary>The most sessions whose waits one read lists.</summary>
    public const int MaxSessions = 20;

    private static readonly HealthQuery InstanceWithIdentifier = new(
        "select pg_catalog.aurora_version()::text as aurora_version,\n" +
        "       pg_catalog.aurora_db_instance_identifier()::text as instance");

    private static readonly HealthQuery InstanceWithoutIdentifier = new(
        "select pg_catalog.aurora_version()::text as aurora_version,\n" +
        "       null::text as instance");

    private static readonly HealthQuery SystemWaitsQuery = new(
        "select (select pg_catalog.jsonb_agg(pg_catalog.to_jsonb(w)) from pg_catalog.aurora_stat_system_waits() w)::text as waits,\n" +
        "       " + EventNames + " as events,\n" +
        "       " + TypeNames + " as types");

    private static readonly HealthQuery WaitNamesQuery = new(
        "select " + EventNames + " as events,\n" +
        "       " + TypeNames + " as types");

    private static readonly HealthQuery DatabaseQuery = new(
        "select s.xact_commit,\n" +
        "       (select pg_catalog.to_jsonb(l)::text from pg_catalog.aurora_stat_get_db_commit_latency(d.oid) l limit 1) as commit_latency,\n" +
        "       (select pg_catalog.to_jsonb(a)::text from pg_catalog.aurora_stat_dml_activity(d.oid) a limit 1) as dml\n" +
        "  from pg_catalog.pg_database d\n" +
        "  join pg_catalog.pg_stat_database s on s.datid = d.oid\n" +
        " where d.datname = pg_catalog.current_database()");

    /// <summary><c>aurora_stat_wait_event()</c>'s rows, as one JSON array.</summary>
    private const string EventNames = "(select pg_catalog.jsonb_agg(pg_catalog.to_jsonb(e)) from pg_catalog.aurora_stat_wait_event() e)::text";

    /// <summary><c>aurora_stat_wait_type()</c>'s rows, as one JSON array.</summary>
    private const string TypeNames = "(select pg_catalog.jsonb_agg(pg_catalog.to_jsonb(t)) from pg_catalog.aurora_stat_wait_type() t)::text";

    /// <summary>
    /// The Aurora version and, where the release has the function, the identifier of the instance this session reached
    /// (A1): <c>aurora_version()</c> and <c>aurora_db_instance_identifier()</c>.
    /// </summary>
    /// <param name="identifier">
    /// Whether <c>pg_catalog.aurora_db_instance_identifier()</c> exists - the platform read says (13.7, 12.11, 11.16, 10.21
    /// and later); without it the instance is null, "unknown".
    /// </param>
    public static HealthQuery Instance(bool identifier) => identifier ? InstanceWithIdentifier : InstanceWithoutIdentifier;

    /// <summary>
    /// Every instance of the cluster as <c>aurora_replica_status()</c> reports it (A3), one JSON object a row, read by name -
    /// the writer is the row whose <c>session_id</c> is <c>MASTER_SESSION_ID</c>.
    /// </summary>
    public static HealthQuery ReplicaStatus() => new(
        "select pg_catalog.to_jsonb(r)::text from pg_catalog.aurora_replica_status() r limit @limit",
        [Integer("limit", MaxInstances)]);

    /// <summary>
    /// The instance's cumulative waits since it started (<c>aurora_stat_system_waits()</c>, microseconds), with the two
    /// functions that name them (<c>aurora_stat_wait_event()</c>, <c>aurora_stat_wait_type()</c>) - three JSON arrays in one
    /// row, joined and ranked by the caller (A4).
    /// </summary>
    public static HealthQuery SystemWaits() => SystemWaitsQuery;

    /// <summary>The wait events' and types' names alone, for the per-session read.</summary>
    public static HealthQuery WaitNames() => WaitNamesQuery;

    /// <summary>
    /// This database's commits, its cumulative commit latency (<c>aurora_stat_get_db_commit_latency(oid)</c>, microseconds)
    /// and its successful DML by kind (<c>aurora_stat_dml_activity(oid)</c>: counts and cumulative microseconds) - one row.
    /// </summary>
    public static HealthQuery Database() => DatabaseQuery;

    /// <summary>
    /// This database's client sessions, the longest-connected first, each with its waits since it connected
    /// (<c>aurora_stat_backend_waits(pid)</c>) as one JSON array. A session another role owns is listed only where the reading
    /// role may see its backend type (<c>pg_read_all_stats</c>), as Database › Sessions lists it.
    /// </summary>
    public static HealthQuery BackendWaits() => new(
        "select a.pid, a.usename::text, a.application_name, a.state, a.backend_start,\n" +
        "       (select pg_catalog.jsonb_agg(pg_catalog.to_jsonb(b)) from pg_catalog.aurora_stat_backend_waits(a.pid) b)::text as waits\n" +
        "  from pg_catalog.pg_stat_activity a\n" +
        " where a.datname = pg_catalog.current_database()\n" +
        "   and a.backend_type = 'client backend'\n" +
        "   and a.pid <> pg_catalog.pg_backend_pid()\n" +
        " order by a.backend_start, a.pid\n" +
        " limit @limit",
        [Integer("limit", MaxSessions)]);

    private static NpgsqlParameter Integer(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };
}
