using System.Text.Json;

using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>The server's autovacuum workers as <see cref="MaintenanceQueries.Workers" /> reads them.</summary>
/// <remarks>Init properties, so a unit test states only the facts its case is about.</remarks>
internal sealed record MaintenanceWorkersRow
{
    /// <summary><c>autovacuum_max_workers</c>.</summary>
    public int MaxWorkers { get; init; } = 3;

    /// <summary>
    /// <c>pg_settings.context</c> of <c>autovacuum_max_workers</c>: <c>postmaster</c> before PostgreSQL 18, <c>sighup</c> from 18
    /// (B0 §6) - what tells its remedy whether a restart is needed.
    /// </summary>
    public string? MaxWorkersContext { get; init; } = "postmaster";

    /// <summary><c>autovacuum_worker_slots</c> (PostgreSQL 18, postmaster): the most workers a reload may raise it to.</summary>
    public int? WorkerSlots { get; init; }

    /// <summary><c>autovacuum_naptime</c>, in seconds: how often the launcher starts a worker per database.</summary>
    public int NaptimeSeconds { get; init; } = 60;

    /// <summary>The server's <c>autovacuum</c>.</summary>
    public bool Autovacuum { get; init; } = true;

    /// <summary>Whether the reading role is a member of <c>pg_read_all_stats</c>, so it sees every worker for what it is.</summary>
    public bool CanReadAllStats { get; init; }

    /// <summary>
    /// The autovacuum workers running on the whole server, by <c>backend_type = 'autovacuum worker'</c> - which only a role
    /// with <c>pg_read_all_stats</c> is shown (B0 §7c); 0 for any other.
    /// </summary>
    public int WorkersSeen { get; init; }

    /// <summary>
    /// The processes running a <c>VACUUM</c> or an <c>ANALYZE</c> on the whole server - <c>pg_stat_progress_vacuum</c> and
    /// <c>pg_stat_progress_analyze</c>, whose process ids every role sees: autovacuum's workers and manual runs together, and
    /// a worker between two tables is in neither, so it is a lower bound.
    /// </summary>
    public int MaintenanceRunning { get; init; }

    /// <summary>
    /// <c>autovacuum_vacuum_cost_delay</c>, in milliseconds: how long a worker sleeps each time it has spent its cost limit
    /// (2 by default from PostgreSQL 12; -1 means <c>vacuum_cost_delay</c>, which is 0).
    /// </summary>
    public double CostDelayMilliseconds { get; init; } = 2;

    /// <summary>The cost limit autovacuum's workers share: <c>autovacuum_vacuum_cost_limit</c>, or <c>vacuum_cost_limit</c> at -1.</summary>
    public int CostLimit { get; init; } = 200;
}

/// <summary>One maintenance command running in this database, as <see cref="MaintenanceQueries.Progress" /> reads it.</summary>
/// <remarks>Init properties, so a unit test states only the facts its case is about.</remarks>
internal sealed record MaintenanceProgressRow
{
    /// <summary>Which progress view it is in: <c>vacuum</c>, <c>analyze</c>, <c>create index</c> or <c>cluster</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The process running it. A detail.</summary>
    public required int ProcessId { get; init; }

    /// <summary>
    /// The relation it works on, as Postgres numbers it, or <see langword="null" /> when the reading role may not see it
    /// (another role's command, autovacuum's included, without <c>pg_read_all_stats</c>).
    /// </summary>
    public uint? RelationId { get; init; }

    /// <summary>The schema of the table the relation belongs to: itself, or a partition's root, or a TOAST table's owner.</summary>
    public string? Schema { get; init; }

    /// <summary>That table.</summary>
    public string? Table { get; init; }

    /// <summary>What of the table it works on: <c>self</c>, <c>partition</c> or <c>toast</c>.</summary>
    public string? Part { get; init; }

    /// <summary>Whether that table is in Postgres' own schemas (<c>pg_catalog</c>, <c>information_schema</c>).</summary>
    public bool SystemCatalog { get; init; }

    /// <summary>The phase, as the view spells it - "scanning heap", "acquiring sample rows".</summary>
    public string? Phase { get; init; }

    /// <summary>The blocks the current phase has to get through (<c>heap_blks_total</c>, <c>sample_blks_total</c>, <c>blocks_total</c>).</summary>
    public long? BlocksTotal { get; init; }

    /// <summary>The blocks it has got through (<c>heap_blks_scanned</c>, <c>sample_blks_scanned</c>, <c>blocks_done</c>).</summary>
    public long? BlocksDone { get; init; }

    /// <summary><c>indexes_total</c> (PostgreSQL 17+, vacuum), or <see langword="null" />.</summary>
    public long? IndexesTotal { get; init; }

    /// <summary><c>indexes_processed</c> (PostgreSQL 17+, vacuum), or <see langword="null" />.</summary>
    public long? IndexesProcessed { get; init; }

    /// <summary>Whether <c>pg_stat_activity</c> shows it as an autovacuum worker; <see langword="null" /> when the role may not read that.</summary>
    public bool? Autovacuum { get; init; }

    /// <summary>Whether an autovacuum says it runs to prevent wraparound; <see langword="null" /> when the role may not read that.</summary>
    public bool? PreventsWraparound { get; init; }

    /// <summary>How long its current statement has run, or <see langword="null" /> when the role may not read that.</summary>
    public TimeSpan? Running { get; init; }

    /// <summary>The key the page names it by - schema and table - or <see langword="null" /> when it is not known.</summary>
    public string? TableKey => Schema is { } schema && Table is { } table ? schema + "." + table : null;
}

/// <summary>What one on-demand bloat measure found, as <see cref="MaintenanceQueries.MeasureBloatAsync" /> read it.</summary>
/// <param name="TableBytes"><c>pgstattuple_approx</c>'s <c>table_len</c>.</param>
/// <param name="ScannedPercent">How much of the table it read rather than estimated from the visibility map.</param>
/// <param name="LiveTuples">Its live tuple count (approximate).</param>
/// <param name="DeadTuples">Its dead tuple count.</param>
/// <param name="DeadPercent">The share of the table dead tuples take.</param>
/// <param name="FreeBytes">The free space it estimated.</param>
/// <param name="FreePercent">The share of the table that is free space.</param>
/// <param name="Indexes">Each valid index on it, measured where its access method has a measure.</param>
internal sealed record BloatMeasureRow(
    long TableBytes,
    double ScannedPercent,
    double LiveTuples,
    long DeadTuples,
    double DeadPercent,
    long FreeBytes,
    double FreePercent,
    IReadOnlyList<BloatIndexRow> Indexes)
{
    /// <summary>
    /// How many of the table's valid indexes the measure left out: it considers the <see cref="MaintenanceQueries.MeasuredIndexes" />
    /// largest, and measures them smallest first while its time lasts.
    /// </summary>
    public int IndexesNotMeasured { get; init; }
}

/// <summary>One index's measure.</summary>
/// <param name="Name">The index.</param>
/// <param name="AccessMethod">Its access method.</param>
/// <param name="Bytes">Its size, from <c>relpages</c>.</param>
/// <param name="LeafDensity">A b-tree's <c>avg_leaf_density</c>, in percent, or <see langword="null" />.</param>
/// <param name="LeafFragmentation">A b-tree's <c>leaf_fragmentation</c>, in percent, or <see langword="null" />.</param>
/// <param name="PendingPages">A GIN index's <c>pending_pages</c>, or <see langword="null" />.</param>
/// <param name="HashFreePercent">A hash index's <c>free_percent</c>, or <see langword="null" />.</param>
internal sealed record BloatIndexRow(
    string Name,
    string AccessMethod,
    long Bytes,
    double? LeafDensity,
    double? LeafFragmentation,
    long? PendingPages,
    double? HashFreePercent);

/// <summary>What the bloat measure needs, read before it measures: the extension, its schema, and the reading role's grant.</summary>
/// <param name="ReadsAs">The role the measure runs as.</param>
/// <param name="ExtensionSchema">The schema <c>pgstattuple</c> is installed in, or <see langword="null" /> when it is not.</param>
/// <param name="MayScan">Whether the role holds <c>pg_stat_scan_tables</c> (or is a superuser).</param>
/// <param name="TableKind">The table's <c>relkind</c>, or <see langword="null" /> when there is no such table.</param>
internal sealed record BloatPreconditionRow(string ReadsAs, string? ExtensionSchema, bool MayScan, string? TableKind)
{
    /// <summary>The installed <c>pgstattuple</c>'s version, or <see langword="null" /> when it is not installed.</summary>
    public string? ExtensionVersion { get; init; }
}

/// <summary>
/// The statements behind the Maintenance page and T16 (W5-V1): the server's autovacuum workers, what maintenance is running
/// in this database, and the bloat measure a person asks for, one table at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two reads are shared memory and the catalog, and lock nothing they list</b> (D27). The progress views and
/// <c>pg_stat_activity</c> are copies of shared memory; a relation is resolved to the table it belongs to by oid in the
/// catalog - a partition through <c>pg_inherits</c> to its root, never a <c>pg_partition_*</c> function, a TOAST table
/// through its owner's <c>reltoastrelid</c> - and a partition's or a TOAST table's own name is never selected, because a
/// tenant partition's name carries the tenant id. Every limit and every name is a parameter.
/// </para>
/// <para>
/// <b>The measure is on demand, and is the one read here that touches a table.</b> <c>pgstattuple_approx</c> reads the
/// table's pages the visibility map does not vouch for, and <c>pgstatindex</c> every page of a b-tree, each under an
/// <c>AccessShareLock</c> for as long as it reads - so it is never part of a sweep or a list, runs one table at a time,
/// one per database at once, in a read-only transaction under the console's <c>statement_timeout</c>. The extension is
/// relocatable, so its functions are called by the schema <c>pg_extension</c> names, quoted here.
/// </para>
/// </remarks>
internal static class MaintenanceQueries
{
    /// <summary>The <c>context.Shared</c> key of the workers read.</summary>
    public const string WorkersKey = "maintenance.workers";

    /// <summary>The <c>context.Shared</c> key of the progress read.</summary>
    public const string ProgressKey = "maintenance.progress";

    /// <summary>The most running commands one progress read lists.</summary>
    public const int MaxProgress = 100;

    /// <summary>The extension the measure needs.</summary>
    public const string Extension = "pgstattuple";

    /// <summary>The server's workers and what is running: one row.</summary>
    internal const string WorkersSql =
        """
        select pg_catalog.current_setting('autovacuum_max_workers')::int,
               (select s.context::text from pg_catalog.pg_settings s where s.name = 'autovacuum_max_workers'),
               (select s.setting::int from pg_catalog.pg_settings s where s.name = 'autovacuum_worker_slots'),
               (select s.setting::int from pg_catalog.pg_settings s where s.name = 'autovacuum_naptime'),
               pg_catalog.current_setting('autovacuum')::boolean,
               pg_catalog.pg_has_role(current_user, 'pg_read_all_stats', 'USAGE'),
               (select pg_catalog.count(*) from pg_catalog.pg_stat_activity a where a.backend_type = 'autovacuum worker')::int,
               (select pg_catalog.count(distinct p.pid)
                from (select v.pid from pg_catalog.pg_stat_progress_vacuum v
                      union all
                      select z.pid from pg_catalog.pg_stat_progress_analyze z) p)::int,
               (select case when s.setting::float8 < 0
                            then (select c.setting::float8 from pg_catalog.pg_settings c where c.name = 'vacuum_cost_delay')
                            else s.setting::float8 end
                from pg_catalog.pg_settings s where s.name = 'autovacuum_vacuum_cost_delay'),
               (select case when s.setting::int < 0
                            then (select c.setting::int from pg_catalog.pg_settings c where c.name = 'vacuum_cost_limit')
                            else s.setting::int end
                from pg_catalog.pg_settings s where s.name = 'autovacuum_vacuum_cost_limit')
        """;

    /// <summary>
    /// The vacuums, analyzes, index builds and clusters running in this database, each resolved to the table it belongs
    /// to - with, where the role may read it, whether it is autovacuum's and how long it has run.
    /// </summary>
    internal const string ProgressSql =
        """
        with recursive here as (
            select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database()
        ),
        running as (
            select 'vacuum'::text as kind, v.pid, v.relid, v.phase::text as phase,
                   v.heap_blks_total::bigint as blocks_total, v.heap_blks_scanned::bigint as blocks_done,
                   (pg_catalog.to_jsonb(v) ->> 'indexes_total')::bigint as indexes_total,
                   (pg_catalog.to_jsonb(v) ->> 'indexes_processed')::bigint as indexes_processed
            from pg_catalog.pg_stat_progress_vacuum v
            where v.datid = (select h.oid from here h)
            union all
            select 'analyze', z.pid, z.relid, z.phase::text, z.sample_blks_total::bigint, z.sample_blks_scanned::bigint, null, null
            from pg_catalog.pg_stat_progress_analyze z
            where z.datid = (select h.oid from here h)
            union all
            select 'create index', x.pid, x.relid, x.phase::text, x.blocks_total::bigint, x.blocks_done::bigint, null, null
            from pg_catalog.pg_stat_progress_create_index x
            where x.datid = (select h.oid from here h)
            union all
            select 'cluster', c.pid, c.relid, c.phase::text, c.heap_blks_total::bigint, c.heap_blks_scanned::bigint, null, null
            from pg_catalog.pg_stat_progress_cluster c
            where c.datid = (select h.oid from here h)
        ),
        listed as (
            select r.* from running r order by r.pid, r.kind limit @limit
        ),
        owner_of_toast as (
            select l.relid, o.oid as owner
            from listed l
            join pg_catalog.pg_class o on o.reltoastrelid = l.relid
            where l.relid is not null
        ),
        start (relid, rel, part) as (
            select l.relid, coalesce(t.owner, l.relid), case when t.owner is not null then 'toast' else 'self' end
            from listed l
            left join owner_of_toast t on t.relid = l.relid
            where l.relid is not null
        ),
        up (relid, rel, part, depth) as (
            select s.relid, s.rel, s.part, 0 from start s
            union all
            select u.relid, i.inhparent, case when u.part = 'toast' then 'toast' else 'partition' end, u.depth + 1
            from up u
            join pg_catalog.pg_class c on c.oid = u.rel and c.relispartition
            join pg_catalog.pg_inherits i on i.inhrelid = u.rel
            where u.depth < 32
        ),
        roots as (
            select distinct on (u.relid) u.relid, u.rel, u.part
            from up u
            order by u.relid, u.depth desc
        )
        select l.kind, l.pid, l.relid::pg_catalog.oid, n.nspname::text, c.relname::text, r.part,
               coalesce(n.nspname in ('pg_catalog', 'information_schema', 'pg_toast'), false),
               l.phase, l.blocks_total, l.blocks_done, l.indexes_total, l.indexes_processed,
               case when a.backend_type is not null then a.backend_type = 'autovacuum worker' end,
               case when a.backend_type = 'autovacuum worker' then a.query like '%(to prevent wraparound)%' end,
               extract(epoch from (pg_catalog.now() - a.query_start))::float8
        from listed l
        left join roots r on r.relid = l.relid
        left join pg_catalog.pg_class c on c.oid = r.rel
        left join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        left join pg_catalog.pg_stat_activity a on a.pid = l.pid
        order by l.pid, l.kind
        """;

    /// <summary>Every list statement here, for the tests that hold them to locking nothing they list.</summary>
    internal static IReadOnlyList<string> ListStatements { get; } = [WorkersSql, ProgressSql];

    /// <summary>The server's workers (<see cref="WorkersSql" />).</summary>
    public static HealthQuery Workers() => new(WorkersSql);

    /// <summary>What maintenance runs in this database (<see cref="ProgressSql" />).</summary>
    public static HealthQuery Progress() => new(ProgressSql, [new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = MaxProgress }]);

    /// <summary>Maps the one row of <see cref="WorkersSql" />.</summary>
    public static MaintenanceWorkersRow ReadWorkers(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new MaintenanceWorkersRow
        {
            MaxWorkers = reader.GetInt32(0),
            MaxWorkersContext = reader.IsDBNull(1) ? null : reader.GetString(1),
            WorkerSlots = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            NaptimeSeconds = reader.IsDBNull(3) ? 60 : reader.GetInt32(3),
            Autovacuum = reader.IsDBNull(4) || reader.GetBoolean(4),
            CanReadAllStats = !reader.IsDBNull(5) && reader.GetBoolean(5),
            WorkersSeen = reader.GetInt32(6),
            MaintenanceRunning = reader.GetInt32(7),
            CostDelayMilliseconds = reader.IsDBNull(8) ? 2 : reader.GetDouble(8),
            CostLimit = reader.IsDBNull(9) ? 200 : reader.GetInt32(9),
        };
    }

    /// <summary>Maps one row of <see cref="ProgressSql" />.</summary>
    public static MaintenanceProgressRow ReadProgress(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new MaintenanceProgressRow
        {
            Kind = reader.GetString(0),
            ProcessId = reader.GetInt32(1),
            RelationId = reader.IsDBNull(2) ? null : reader.GetFieldValue<uint>(2),
            Schema = Text(reader, 3),
            Table = Text(reader, 4),
            Part = Text(reader, 5),
            SystemCatalog = !reader.IsDBNull(6) && reader.GetBoolean(6),
            Phase = Text(reader, 7),
            BlocksTotal = Int64(reader, 8),
            BlocksDone = Int64(reader, 9),
            IndexesTotal = Int64(reader, 10),
            IndexesProcessed = Int64(reader, 11),
            Autovacuum = reader.IsDBNull(12) ? null : reader.GetBoolean(12),
            PreventsWraparound = reader.IsDBNull(13) ? null : reader.GetBoolean(13),
            Running = reader.IsDBNull(14) ? null : TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(14))),
        };
    }

    /// <summary>
    /// What the measure needs to know first, as the role it runs as: who that is, where <c>pgstattuple</c> is installed, whether
    /// the role holds <c>pg_stat_scan_tables</c>, and what kind of relation the table is.
    /// </summary>
    internal const string BloatPreconditionsSql =
        """
        select current_user::text,
               (select n.nspname::text
                from pg_catalog.pg_extension e
                join pg_catalog.pg_namespace n on n.oid = e.extnamespace
                where e.extname = @extension),
               (select e.extversion::text from pg_catalog.pg_extension e where e.extname = @extension),
               coalesce((select r.rolsuper from pg_catalog.pg_roles r where r.rolname = current_user), false)
                   or pg_catalog.pg_has_role(current_user, 'pg_stat_scan_tables', 'USAGE'),
               (select c.relkind::text
                from pg_catalog.pg_class c
                join pg_catalog.pg_namespace n on n.oid = c.relnamespace
                where n.nspname = @schema and c.relname = @table)
        """;

    /// <summary>Reads <see cref="BloatPreconditionsSql" /> in the caller's transaction.</summary>
    public static async Task<BloatPreconditionRow> ReadBloatPreconditionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        int timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(BloatPreconditionsSql, connection, transaction) { CommandTimeout = timeout };
        command.Parameters.Add(new NpgsqlParameter("extension", NpgsqlDbType.Text) { Value = Extension });
        command.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Text) { Value = schema });
        command.Parameters.Add(new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = table });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new BloatPreconditionRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            !reader.IsDBNull(3) && reader.GetBoolean(3),
            reader.IsDBNull(4) ? null : reader.GetString(4))
        {
            ExtensionVersion = reader.IsDBNull(2) ? null : reader.GetString(2),
        };
    }

    /// <summary>How many of a table's indexes the measure considers - its largest, measured smallest first; the rest are counted.</summary>
    public const int MeasuredIndexes = 5;

    /// <summary>The least time left in the budget for the measure to start another index.</summary>
    internal static readonly TimeSpan MinimumIndexBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The heap's measure, in a statement of its own so that it stands whatever the indexes cost (W5-V1 review):
    /// <c>pgstattuple_approx</c> of the table - or, for an extension older than 1.3, which has no such function, the exact
    /// <c>pgstattuple</c> - with the indexes to measure after it (the <see cref="MeasuredIndexes" /> largest valid ones, smallest
    /// first, by number, access method and size) and how many valid indexes the table has. The table is found by schema and
    /// name, both values.
    /// </summary>
    /// <param name="extensionSchema">Where <c>pgstattuple</c> is installed, as <see cref="ReadBloatPreconditionsAsync" /> read it.</param>
    /// <param name="approximate">Whether the installed version has <c>pgstattuple_approx</c> (<see cref="HasApproximate" />).</param>
    internal static string BloatHeapSql(string extensionSchema, bool approximate = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionSchema);

        string heap = SqlIdentifier.Qualify(extensionSchema, approximate ? "pgstattuple_approx" : "pgstattuple");

        return
            "with target as (\n"
            + "    select c.oid from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace\n"
            + "    where n.nspname = @schema and c.relname = @table and c.relkind in ('r', 'm')\n"
            + ")\n"
            + "select (select pg_catalog.to_jsonb(s) from " + heap + "((select t.oid from target t)::pg_catalog.regclass) s)::text,\n"
            + "       (select coalesce(pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(\n"
            + "                   'oid', c.oid::bigint, 'name', c.relname, 'am', c.am, 'bytes', c.bytes) order by c.pages, c.relname), '[]'::jsonb)\n"
            + "        from (select i.oid, i.relname::text as relname, am.amname::text as am, i.relpages as pages,\n"
            + "                     i.relpages::bigint * pg_catalog.current_setting('block_size')::bigint as bytes\n"
            + "              from pg_catalog.pg_index x\n"
            + "              join pg_catalog.pg_class i on i.oid = x.indexrelid\n"
            + "              join pg_catalog.pg_am am on am.oid = i.relam\n"
            + "              where x.indrelid = (select t.oid from target t) and x.indisvalid and i.relkind = 'i'\n"
            + "              order by i.relpages desc, i.relname\n"
            + "              limit " + MeasuredIndexes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ") c)::text,\n"
            + "       (select pg_catalog.count(*)\n"
            + "        from pg_catalog.pg_index x\n"
            + "        join pg_catalog.pg_class i on i.oid = x.indexrelid\n"
            + "        where x.indrelid = (select t.oid from target t) and x.indisvalid and i.relkind = 'i')::int";
    }

    /// <summary>
    /// One index's measure, by its number: <c>pgstatindex</c> (b-tree), <c>pgstatginindex</c> (GIN) or <c>pgstathashindex</c>
    /// (hash) - or <see langword="null" /> for an access method <c>pgstattuple</c> has no measure for, which is listed with its
    /// size alone.
    /// </summary>
    internal static string? BloatIndexSql(string extensionSchema, string accessMethod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionSchema);

        string? function = accessMethod switch
        {
            "btree" => "pgstatindex",
            "gin" => "pgstatginindex",
            "hash" => "pgstathashindex",
            _ => null,
        };

        return function is null
            ? null
            : "select pg_catalog.to_jsonb(m)::text from " + SqlIdentifier.Qualify(extensionSchema, function) + "(@index::pg_catalog.regclass) m";
    }

    /// <summary>What the time left in the measure's budget becomes before each index: its own <c>statement_timeout</c>.</summary>
    internal const string IndexTimeoutSql = "select pg_catalog.set_config('statement_timeout', @timeout, true)";

    /// <summary>Whether a <c>pgstattuple</c> version has <c>pgstattuple_approx</c>, which 1.3 added.</summary>
    internal static bool HasApproximate(string? version) =>
        version is null
        || !System.Version.TryParse(version.Contains('.', StringComparison.Ordinal) ? version : version + ".0", out System.Version? parsed)
        || parsed >= new System.Version(1, 3);

    /// <summary>
    /// The measure, in the caller's read-only transaction: the heap first (<see cref="BloatHeapSql" />), under the session's
    /// <c>statement_timeout</c>; then the indexes, smallest first, each in its own statement under a savepoint and a
    /// <c>statement_timeout</c> of what is left of <paramref name="budget" /> - so an index that runs out of time is rolled
    /// back and the measure stops there with the heap and the indexes it has, never losing the heap (W5-V1 review).
    /// </summary>
    /// <returns>The measure, or <see langword="null" /> when there is no such table.</returns>
    public static async Task<BloatMeasureRow?> MeasureBloatAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string extensionSchema,
        string schema,
        string table,
        TimeSpan budget,
        int timeout,
        CancellationToken cancellationToken,
        bool approximate = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        string? heap;
        string candidates;
        int indexCount;

        await using (NpgsqlCommand command = new(BloatHeapSql(extensionSchema, approximate), connection, transaction) { CommandTimeout = timeout })
        {
            command.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Text) { Value = schema });
            command.Parameters.Add(new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = table });

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            heap = reader.IsDBNull(0) ? null : reader.GetString(0);
            candidates = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
            indexCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        }

        if (heap is null)
        {
            return null;
        }

        List<BloatIndexRow> indexes = [];

        foreach ((uint oid, string name, string accessMethod, long bytes) in ParseCandidates(candidates))
        {
            if (BloatIndexSql(extensionSchema, accessMethod) is not { } sql)
            {
                indexes.Add(ParseIndex(name, accessMethod, bytes, measure: null));
                continue;
            }

            TimeSpan left = budget - watch.Elapsed;

            if (left < MinimumIndexBudget)
            {
                break;
            }

            const string Savepoint = "ms_bloat_index";
            await transaction.SaveAsync(Savepoint, cancellationToken).ConfigureAwait(false);

            try
            {
                await using (NpgsqlCommand limit = new(IndexTimeoutSql, connection, transaction) { CommandTimeout = timeout })
                {
                    limit.Parameters.Add(new NpgsqlParameter("timeout", NpgsqlDbType.Text)
                    {
                        Value = ((long) Math.Ceiling(left.TotalMilliseconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
                    await limit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                string? measured;

                await using (NpgsqlCommand index = new(sql, connection, transaction) { CommandTimeout = timeout })
                {
                    index.Parameters.Add(new NpgsqlParameter("index", NpgsqlDbType.Oid) { Value = oid });
                    measured = await index.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                }

                await transaction.ReleaseAsync(Savepoint, cancellationToken).ConfigureAwait(false);
                indexes.Add(ParseIndex(name, accessMethod, bytes, measured));
            }
            catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable)
            {
                // Out of time, or the index is locked for a schema change: back to before it, and the measure is what it has.
                await transaction.RollbackAsync(Savepoint, cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        return ParseHeap(heap) with { Indexes = indexes, IndexesNotMeasured = Math.Max(0, indexCount - indexes.Count) };
    }

    /// <summary>The heap's measure, from <c>pgstattuple_approx</c>'s or the exact <c>pgstattuple</c>'s <c>jsonb</c>, with no index yet.</summary>
    internal static BloatMeasureRow ParseHeap(string heap)
    {
        using JsonDocument table = JsonDocument.Parse(heap);
        JsonElement t = table.RootElement;

        // pgstattuple_approx's names, else the exact pgstattuple's - which reads every page, so all of it is scanned.
        return new BloatMeasureRow(
            (long) Number(t, "table_len"),
            NullableNumber(t, "scanned_percent") ?? 100,
            NullableNumber(t, "approx_tuple_count") ?? Number(t, "tuple_count"),
            (long) Number(t, "dead_tuple_count"),
            Number(t, "dead_tuple_percent"),
            (long) (NullableNumber(t, "approx_free_space") ?? Number(t, "free_space")),
            NullableNumber(t, "approx_free_percent") ?? Number(t, "free_percent"),
            []);
    }

    /// <summary>The indexes <see cref="BloatHeapSql" /> lists to measure, in its order.</summary>
    internal static IReadOnlyList<(uint Oid, string Name, string AccessMethod, long Bytes)> ParseCandidates(string candidates)
    {
        using JsonDocument list = JsonDocument.Parse(candidates);

        return
        [
            .. list.RootElement.EnumerateArray().Select(static x => (
                (uint) Number(x, "oid"),
                x.GetProperty("name").GetString() ?? string.Empty,
                x.GetProperty("am").GetString() ?? string.Empty,
                (long) Number(x, "bytes"))),
        ];
    }

    /// <summary>One index's row, from its measure's <c>jsonb</c> - or its size alone, where it has no measure.</summary>
    internal static BloatIndexRow ParseIndex(string name, string accessMethod, long bytes, string? measure)
    {
        if (measure is null)
        {
            return new BloatIndexRow(name, accessMethod, bytes, null, null, null, null);
        }

        using JsonDocument document = JsonDocument.Parse(measure);
        JsonElement m = document.RootElement;

        return new BloatIndexRow(
            name,
            accessMethod,
            bytes,
            NullableNumber(m, "avg_leaf_density"),
            NullableNumber(m, "leaf_fragmentation"),
            NullableNumber(m, "pending_pages") is { } pending ? (long) pending : null,
            NullableNumber(m, "free_percent"));
    }

    private static double Number(JsonElement element, string name) => NullableNumber(element, name) ?? 0;

    private static double? NullableNumber(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Int64(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
