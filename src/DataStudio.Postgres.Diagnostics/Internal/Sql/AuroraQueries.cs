using Npgsql;

using NpgsqlTypes;

namespace DataStudio.Postgres.Internal.Sql;

/// <summary>
/// The statements of Aurora's own diagnostic functions (issue #8 A1, A3, A4; W5-AUR): which instance answered, the replica
/// status of every instance, the instance's cumulative waits, this database's commit latency and DML activity, and - behind
/// the activity gate only - the waits of this database's sessions. Run only once the platform read
/// (<see cref="HealthServerQueries.Platform" />) has found the schema Aurora's functions live in: no other server has these
/// functions, and naming one that does not exist is <c>42883</c>. A server marked only by its <c>aurora_*</c> settings has no
/// such schema, and its sections say so in a sentence instead of calling anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every function by the schema the platform read found, every column by its name.</b> Each call is qualified by that
/// schema, quoted through <see cref="SqlIdentifier" />: <c>pg_catalog</c> where Aurora keeps them, or the schema of the
/// <c>aurora_version()</c> a superuser owns when it is elsewhere (issue #81), and only a schema only a superuser can create
/// in (<see cref="HealthServerQueries.Platform" />), so a function of the same name a database owner made in another schema
/// is never the one called. And AWS documents these functions' columns more
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

    /// <summary>One of Aurora's functions by its schema-qualified, quoted name: the schema the platform read found its <c>aurora_version()</c> in.</summary>
    private static string Function(string schema, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);

        return SqlIdentifier.Qualify(schema, name);
    }

    /// <summary><c>aurora_stat_wait_event()</c>'s rows, as one JSON array.</summary>
    private static string EventNames(string schema) =>
        "(select pg_catalog.jsonb_agg(pg_catalog.to_jsonb(e)) from " + Function(schema, "aurora_stat_wait_event") + "() e)::text";

    /// <summary><c>aurora_stat_wait_type()</c>'s rows, as one JSON array.</summary>
    private static string TypeNames(string schema) =>
        "(select pg_catalog.jsonb_agg(pg_catalog.to_jsonb(t)) from " + Function(schema, "aurora_stat_wait_type") + "() t)::text";

    /// <summary>
    /// The Aurora version and, where the release has the function, the identifier of the instance this session reached
    /// (A1): <c>aurora_version()</c> and <c>aurora_db_instance_identifier()</c>.
    /// </summary>
    /// <param name="schema">The schema of the <c>aurora_version()</c> the platform read found (<see cref="HealthServerQueries.Platform" />).</param>
    /// <param name="identifier">
    /// Whether <c>aurora_db_instance_identifier()</c> exists there - the platform read says (13.7, 12.11, 11.16, 10.21
    /// and later); without it the instance is null, "unknown".
    /// </param>
    public static HealthQuery Instance(string schema, bool identifier) => new(
        "select " + Function(schema, "aurora_version") + "()::text as aurora_version,\n" +
        "       " + (identifier ? Function(schema, "aurora_db_instance_identifier") + "()::text" : "null::text") + " as instance");

    /// <summary>
    /// Every instance of the cluster as <c>aurora_replica_status()</c> reports it (A3), one JSON object a row, read by name -
    /// the writer is the row whose <c>session_id</c> is <c>MASTER_SESSION_ID</c>.
    /// </summary>
    /// <param name="schema">The schema of the <c>aurora_version()</c> the platform read found.</param>
    public static HealthQuery ReplicaStatus(string schema) => new(
        "select pg_catalog.to_jsonb(r)::text from " + Function(schema, "aurora_replica_status") + "() r limit @limit",
        [Integer("limit", MaxInstances)]);

    /// <summary>
    /// The instance's cumulative waits since it started (<c>aurora_stat_system_waits()</c>, microseconds), with the two
    /// functions that name them (<c>aurora_stat_wait_event()</c>, <c>aurora_stat_wait_type()</c>) - three JSON arrays in one
    /// row, joined and ranked by the caller (A4).
    /// </summary>
    /// <param name="schema">The schema of the <c>aurora_version()</c> the platform read found.</param>
    public static HealthQuery SystemWaits(string schema) => new(
        "select (select pg_catalog.jsonb_agg(pg_catalog.to_jsonb(w)) from " + Function(schema, "aurora_stat_system_waits") + "() w)::text as waits,\n" +
        "       " + EventNames(schema) + " as events,\n" +
        "       " + TypeNames(schema) + " as types");

    /// <summary>The wait events' and types' names alone, for the per-session read.</summary>
    /// <param name="schema">The schema of the <c>aurora_version()</c> the platform read found.</param>
    public static HealthQuery WaitNames(string schema) => new(
        "select " + EventNames(schema) + " as events,\n" +
        "       " + TypeNames(schema) + " as types");

    /// <summary>
    /// This database's commits, its cumulative commit latency (<c>aurora_stat_get_db_commit_latency(oid)</c>, microseconds)
    /// and its successful DML by kind (<c>aurora_stat_dml_activity(oid)</c>: counts and cumulative microseconds) - one row.
    /// </summary>
    /// <param name="schema">The schema of the <c>aurora_version()</c> the platform read found.</param>
    public static HealthQuery Database(string schema) => new(
        "select s.xact_commit,\n" +
        "       (select pg_catalog.to_jsonb(l)::text from " + Function(schema, "aurora_stat_get_db_commit_latency") + "(d.oid) l limit 1) as commit_latency,\n" +
        "       (select pg_catalog.to_jsonb(a)::text from " + Function(schema, "aurora_stat_dml_activity") + "(d.oid) a limit 1) as dml\n" +
        "  from pg_catalog.pg_database d\n" +
        "  join pg_catalog.pg_stat_database s on s.datid = d.oid\n" +
        " where d.datname = pg_catalog.current_database()");

    /// <summary>
    /// This database's client sessions, the longest-connected first, each with its waits since it connected
    /// (<c>aurora_stat_backend_waits(pid)</c>) as one JSON array. A session another role owns is listed only where the reading
    /// role may see its backend type (<c>pg_read_all_stats</c>), as Database › Sessions lists it.
    /// </summary>
    /// <param name="schema">The schema of the <c>aurora_version()</c> the platform read found.</param>
    public static HealthQuery BackendWaits(string schema) => new(
        "select a.pid, a.usename::text, a.application_name, a.state, a.backend_start,\n" +
        "       (select pg_catalog.jsonb_agg(pg_catalog.to_jsonb(b)) from " + Function(schema, "aurora_stat_backend_waits") + "(a.pid) b)::text as waits\n" +
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
