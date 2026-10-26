namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The one statement behind <c>StatisticsWindow</c> (W5-CAP): since when this database's cumulative counters have counted,
/// and the signals that say whether they were reset - read once per sweep, and once per Storage facts read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every column readable by every role</b> (B0 G4 and G5, measured on 14 to 18 as a role in no monitoring group):
/// <c>pg_stat_database</c>, <c>pg_stat_bgwriter</c>, <c>pg_stat_checkpointer</c> and <c>pg_stat_archiver</c>,
/// <c>pg_postmaster_start_time()</c>, and <c>pg_control_checkpoint()</c> and <c>pg_control_system()</c>, which are
/// executable by <c>PUBLIC</c>. Those two are still guarded by <c>has_function_privilege</c>, so a platform that revoked
/// them answers null rather than refusing the whole read.
/// </para>
/// <para>
/// <b>Which shared view.</b> PostgreSQL 17 moved the checkpoint counters - and their own <c>stats_reset</c> - from
/// <c>pg_stat_bgwriter</c> to <c>pg_stat_checkpointer</c>, a view that does not exist below 17, so naming it there is
/// <c>42P01</c> however the statement guards it. The caller picks with <c>PostgresFeatures</c>
/// (<c>PgStatCheckpointer</c>), from the sweep's one version read. Both views move on a crash on 14 and 17 (B0 G4).
/// </para>
/// <para>
/// <b>Locks nothing.</b> Statistics views read the statistics snapshot; the control functions read <c>pg_control</c>;
/// the never-analysed count reads <c>pg_class</c> and <c>pg_namespace</c>, which costs catalog <c>AccessShareLock</c>s
/// and nothing else. No relation is opened, so nothing here queues behind a migration (D27).
/// </para>
/// </remarks>
internal static class StatisticsWindowQueries
{
    /// <summary>The window's statement, its shared view chosen by <paramref name="checkpointer" />.</summary>
    /// <param name="checkpointer">
    /// Whether the server has <c>pg_stat_checkpointer</c> (PostgreSQL 17 and later); below it, <c>pg_stat_bgwriter</c>.
    /// </param>
    public static HealthQuery Read(bool checkpointer) => new(checkpointer ? CheckpointerSql : BgwriterSql);

    /// <summary>The statement on PostgreSQL 17 and later.</summary>
    internal static string CheckpointerSql { get; } = Sql("(select s.stats_reset from pg_catalog.pg_stat_checkpointer s)");

    /// <summary>The statement below PostgreSQL 17.</summary>
    internal static string BgwriterSql { get; } = Sql("(select s.stats_reset from pg_catalog.pg_stat_bgwriter s)");

    /// <summary>The columns, in the order <c>StatisticsWindow</c> maps them.</summary>
    /// <param name="sharedReset">The shared view's <c>stats_reset</c>, as a scalar subquery.</param>
    private static string Sql(string sharedReset) =>
        "select pg_catalog.current_setting('server_version_num')::int,\n"
        + "       d.stats_reset,\n"
        + "       " + sharedReset + ",\n"
        + "       (select a.stats_reset from pg_catalog.pg_stat_archiver a),\n"
        + "       pg_catalog.pg_postmaster_start_time(),\n"
        + "       case when pg_catalog.has_function_privilege('pg_catalog.pg_control_checkpoint()', 'execute')\n"
        + "            then (pg_catalog.pg_control_checkpoint()).timeline_id end,\n"
        + "       case when pg_catalog.has_function_privilege('pg_catalog.pg_control_system()', 'execute')\n"
        + "            then (pg_catalog.pg_control_system()).system_identifier end,\n"
        + "       d.xact_commit,\n"
        + "       d.blks_read,\n"
        + "       d.blks_hit,\n"
        + "       t.tables,\n"
        + "       t.unanalysed,\n"
        + "       pg_catalog.pg_is_in_recovery()\n"
        + "  from pg_catalog.pg_stat_database d\n"
        + " cross join lateral (\n"
        + "       select pg_catalog.count(*)::int as tables,\n"
        + "              (pg_catalog.count(*) filter (where c.reltuples < 0))::int as unanalysed\n"
        + "         from pg_catalog.pg_class c\n"
        + "         join pg_catalog.pg_namespace n on n.oid = c.relnamespace\n"
        + "        where c.relkind = 'r'\n"
        + "          and n.nspname <> 'information_schema'\n"
        + "          and pg_catalog.left(n.nspname::text, 3) <> 'pg_') t\n"
        + " where d.datname = pg_catalog.current_database()";
}
