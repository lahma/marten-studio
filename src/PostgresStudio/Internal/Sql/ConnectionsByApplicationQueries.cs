using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// Connections by application (issue #8 G9): this database's client sessions grouped by <c>application_name</c> - total,
/// active, idle, idle in transaction, waiting on a lock, of a state the role may not see, and the oldest transaction - with
/// the server's connection budget beside them.
/// </summary>
/// <remarks>
/// <para>
/// <b>One aggregate over every session the role may see, never the capped list.</b> Database › Sessions lists at most
/// <see cref="HealthConnectionQueries.MaxSessions" /> sessions, oldest transaction first, so grouping that list would undercount
/// exactly the busy applications this is for. The grouping is Postgres', over every client session of this database
/// (<see cref="HealthConnectionQueries.ClientSession" />), the studio's own reading backend excluded by
/// <c>pg_backend_pid()</c>. The studio's other sessions - a page's read, a sweep - share the host's data source and so its
/// <c>application_name</c>, and are counted under it.
/// </para>
/// <para>
/// <b>What a role without <c>pg_read_all_stats</c> sees</b> (B0 G9, measured): every session's <c>application_name</c>, and the
/// state, waits and transaction start only of its own role's sessions. So the totals are exact for every role, and every
/// other role's session is counted as <c>state_unknown</c> rather than as idle; the first row says whether the role reads
/// every state.
/// </para>
/// <para>
/// <b>The totals and the applications in one statement</b>: <c>grouping sets ((), (application))</c>, the database's own row
/// first and then the applications by size, at most <see cref="MaxApplications" /> of them - with how many there are, so a
/// cap is said rather than hidden. A database with nobody connected still answers its total row, with zeros.
/// </para>
/// <para>
/// <b>Reads no relation that a migration locks</b>: <c>pg_stat_activity</c> is the process array, and the settings are
/// <c>current_setting</c>; nothing is named into the text, and every value is a parameter. Not on any poll: Database ›
/// Sessions reads it at <c>HealthInterval</c> and on Refresh, through the sessions snapshot's ten-second floor
/// (<c>ActivityViewsOffThePollPathTests</c>).
/// </para>
/// </remarks>
internal static class ConnectionsByApplicationQueries
{
    /// <summary>The most applications one read lists; the count says how many there are.</summary>
    public const int MaxApplications = 100;

    /// <summary>
    /// The statement: the role's visibility, the server's budget (<c>max_connections</c>, the reservations, the whole
    /// server's client sessions), then this database's total row and its applications, the most sessions first.
    /// </summary>
    public static HealthQuery Read() => new(
        "with visibility as (\n" +
        "  select pg_catalog.pg_has_role(current_user, 'pg_read_all_stats', 'USAGE') as can_read_all_stats,\n" +
        "         current_setting('max_connections')::int4 as max_connections,\n" +
        "         current_setting('superuser_reserved_connections')::int4\n" +
        "           + coalesce(nullif(current_setting('reserved_connections', true), ''), '0')::int4 as reserved,\n" +
        "         (select count(*) from pg_catalog.pg_stat_activity a where " + HealthConnectionQueries.ClientSession + ") as server_connections\n" +
        "),\n" +
        "sessions as (\n" +
        "  select coalesce(a.application_name, '') as application, a.state, a.wait_event_type, a.xact_start\n" +
        "    from pg_catalog.pg_stat_activity a\n" +
        "   where a.datname = current_database()\n" +
        "     and " + HealthConnectionQueries.ClientSession + "\n" +
        "     and a.pid <> pg_catalog.pg_backend_pid()\n" +
        "),\n" +
        "grouped as (\n" +
        "  select grouping(s.application) = 1 as is_total,\n" +
        "         s.application,\n" +
        "         count(*) as total,\n" +
        "         count(*) filter (where s.state = 'active') as active,\n" +
        "         count(*) filter (where s.state = 'idle') as idle,\n" +
        "         count(*) filter (where s.state in ('idle in transaction', 'idle in transaction (aborted)')) as idle_in_transaction,\n" +
        "         count(*) filter (where s.wait_event_type = 'Lock') as waiting,\n" +
        "         count(*) filter (where s.state is null) as state_unknown,\n" +
        "         extract(epoch from (now() - min(s.xact_start)))::float8 as oldest_transaction_seconds\n" +
        "    from sessions s\n" +
        "   group by grouping sets ((), (s.application))\n" +
        "),\n" +
        "ranked as (\n" +
        "  select g.*,\n" +
        "         count(*) filter (where not g.is_total) over () as applications,\n" +
        "         row_number() over (order by g.is_total desc, g.total desc, g.application) as rank\n" +
        "    from grouped g\n" +
        ")\n" +
        "select v.can_read_all_stats, v.max_connections, v.reserved, v.server_connections,\n" +
        "       r.is_total, r.application, r.total, r.active, r.idle, r.idle_in_transaction, r.waiting, r.state_unknown,\n" +
        "       r.oldest_transaction_seconds, r.applications\n" +
        "  from visibility v\n" +
        "  left join ranked r on r.rank <= @limit + 1\n" +
        " order by r.rank",
        [new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = MaxApplications }]);
}
