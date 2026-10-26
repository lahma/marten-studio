using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>One table the tables-and-indexes checks read: a schema and a name, both values, never SQL text.</summary>
/// <param name="Schema">The schema, as the store's configuration names it.</param>
/// <param name="Table">The table.</param>
internal sealed record HealthTableTarget(string Schema, string Table);

/// <summary>One table a plain data source's checks judge, as <see cref="HealthTableQueries.RelationalTables" /> listed it.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="Table">The table.</param>
/// <param name="Kind"><c>relkind</c>: <c>r</c>, or <c>p</c> for a partitioned table.</param>
/// <param name="EstimatedPages"><c>relpages</c>, summed over the partitions of a partitioned table - an estimate.</param>
/// <param name="Total">How many tables the admitted schemas hold in all, the ones past the cap included.</param>
internal sealed record HealthRelationalTableRow(string Schema, string Table, string Kind, long EstimatedPages, int Total);

/// <summary>
/// One Marten table as the catalog and the statistics collector see it, its partitions rolled in - what
/// <see cref="HealthTableQueries.Tables" /> reads.
/// </summary>
/// <remarks>
/// Init properties rather than a positional record, so a unit test states only the facts its case is about.
/// Every counter is <see langword="null" /> when the statistics collector has nothing for the table.
/// </remarks>
internal sealed record HealthTableRow
{
    /// <summary>The schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The table.</summary>
    public required string Table { get; init; }

    /// <summary><c>relkind</c>: <c>r</c> for an ordinary table, <c>p</c> for a partitioned one.</summary>
    public string Kind { get; init; } = "r";

    /// <summary>Whether it is a partitioned table, whose partitions are rolled into this row.</summary>
    public bool IsPartitioned => Kind == "p";

    /// <summary>
    /// <c>reltuples</c>, summed over the leaf partitions of a partitioned table; <c>-1</c> before the first
    /// analyze (or, for a partitioned table, while none of its partitions has been analysed).
    /// </summary>
    public long EstimatedRows { get; init; } = -1;

    /// <summary>The main fork, from <c>relpages</c> - an estimate that <c>VACUUM</c> and <c>ANALYZE</c> refresh.</summary>
    public long HeapBytes { get; init; }

    /// <summary>The TOAST table's main fork, from its <c>relpages</c>.</summary>
    public long ToastBytes { get; init; }

    /// <summary>Every index on it, from their <c>relpages</c>.</summary>
    public long IndexBytes { get; init; }

    /// <summary><c>n_live_tup</c>.</summary>
    public long? LiveRows { get; init; }

    /// <summary><c>n_dead_tup</c>.</summary>
    public long? DeadRows { get; init; }

    /// <summary><c>seq_scan</c>: one per participant of a parallel scan, and one per partition scanned.</summary>
    public long? SequentialScans { get; init; }

    /// <summary><c>seq_tup_read</c>: the live rows sequential scans fetched.</summary>
    public long? SequentialRowsRead { get; init; }

    /// <summary><c>idx_scan</c>.</summary>
    public long? IndexScans { get; init; }

    /// <summary><c>n_tup_ins</c>.</summary>
    public long? Inserts { get; init; }

    /// <summary><c>n_tup_upd</c>, HOT updates included.</summary>
    public long? Updates { get; init; }

    /// <summary><c>n_tup_del</c>.</summary>
    public long? Deletes { get; init; }

    /// <summary><c>n_tup_hot_upd</c>: the updates that needed no index entry.</summary>
    public long? HotUpdates { get; init; }

    /// <summary><c>n_mod_since_analyze</c>.</summary>
    public long? ModifiedSinceAnalyze { get; init; }

    /// <summary><c>last_vacuum</c> (a manual one), the latest over the partitions.</summary>
    public DateTimeOffset? LastVacuum { get; init; }

    /// <summary><c>last_autovacuum</c>, the latest over the partitions.</summary>
    public DateTimeOffset? LastAutovacuum { get; init; }

    /// <summary><c>last_analyze</c> (a manual one), the latest over the partitions.</summary>
    public DateTimeOffset? LastAnalyze { get; init; }

    /// <summary><c>last_autoanalyze</c>, the latest over the partitions.</summary>
    public DateTimeOffset? LastAutoanalyze { get; init; }

    /// <summary>
    /// The oldest <c>age(relfrozenxid)</c> of the table, its TOAST table and every partition's - or
    /// <see langword="null" /> for a partitioned table with no partition yet. A partitioned parent's own
    /// <c>relfrozenxid</c> is never read: it is zero, and <c>age()</c> of zero is <c>int.MaxValue</c>.
    /// </summary>
    public long? OldestXidAge { get; init; }

    /// <summary>
    /// How many indexes on its partitions Postgres has marked invalid and no build is working on - see
    /// <see cref="BuildingPartitionIndexes" />. Never their names.
    /// </summary>
    public int InvalidPartitionIndexes { get; init; }

    /// <summary>
    /// How many invalid indexes on its partitions a build running right now is working on: its own index
    /// (<c>index_relid</c>), a <c>REINDEX CONCURRENTLY</c>'s <c>_ccnew</c> or <c>_ccold</c> copy of the index it
    /// reports, or a partitioned index a <c>CREATE INDEX</c> in the tree is completing.
    /// </summary>
    public int BuildingPartitionIndexes { get; init; }

    /// <summary><c>reloptions</c>: the storage parameters set on the table, as <c>name=value</c>.</summary>
    public IReadOnlyList<string> StorageOptions { get; init; } = [];

    /// <summary>When the database's statistics were last reset, or <see langword="null" /> for never.</summary>
    public DateTimeOffset? StatsReset { get; init; }

    /// <summary>The server's <c>autovacuum</c> setting.</summary>
    public bool AutovacuumEnabled { get; init; } = true;

    /// <summary>The server's <c>max_parallel_workers_per_gather</c>: how many participants one scan may count as.</summary>
    public int MaxParallelWorkersPerGather { get; init; } = 2;

    /// <summary>How many leaf partitions a partitioned table has; zero for an ordinary table. Never their names.</summary>
    public int PartitionLeaves { get; init; }

    /// <summary>The largest leaf partition's <c>n_live_tup</c>, or <see langword="null" /> for an ordinary table.</summary>
    public long? LargestLeafRows { get; init; }

    /// <summary>
    /// For a partitioned table, the storage parameters its leaf partitions do not all have at one value - by name
    /// only. <see cref="StorageOptions" /> then holds the ones they agree on, because Postgres keeps none on a
    /// partitioned parent.
    /// </summary>
    public IReadOnlyList<string> DifferingStorageOptions { get; init; } = [];

    /// <summary>
    /// The phase of a build working on one of <see cref="BuildingPartitionIndexes" />
    /// (<c>pg_stat_progress_create_index</c>), or <see langword="null" />.
    /// </summary>
    public string? PartitionBuildPhase { get; init; }

    /// <summary>
    /// The phase of any index build running right now on the table or one of its partitions, whatever index it
    /// is building - for the caveat that a re-apply would queue behind it - or <see langword="null" />.
    /// </summary>
    public string? TreeBuildPhase { get; init; }

    /// <summary>
    /// How many index builds in this database the reading role cannot see the table of - another role's, without
    /// <c>pg_read_all_stats</c>.
    /// </summary>
    public int UnseenIndexBuilds { get; init; }

    /// <summary>The server's <c>autovacuum_freeze_max_age</c>, where autovacuum forces a freeze.</summary>
    public long FreezeMaxAge { get; init; } = 200_000_000;

    /// <summary>The server's <c>server_version_num</c>.</summary>
    public int ServerVersion { get; init; } = 170_000;

    /// <summary>The key the history and the findings name this table by.</summary>
    public string Key => Schema + "." + Table;
}

/// <summary>One index on a Marten table - what <see cref="HealthTableQueries.Indexes" /> reads.</summary>
internal sealed record HealthIndexRow
{
    /// <summary>The schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The table it is on.</summary>
    public required string Table { get; init; }

    /// <summary>The index.</summary>
    public required string Name { get; init; }

    /// <summary>The access method: <c>btree</c>, <c>gin</c>, <c>gist</c>, <c>hash</c>, <c>brin</c>...</summary>
    public string AccessMethod { get; init; } = "btree";

    /// <summary><c>pg_get_indexdef</c>: the statement Postgres reconstructs, read with <c>search_path</c> pinned.</summary>
    public string Definition { get; init; } = string.Empty;

    /// <summary>
    /// Each column's definition, key columns first and then <c>INCLUDE</c> ones -
    /// <c>pg_get_indexdef(oid, k, true)</c>: a column name, or an expression.
    /// </summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary><c>indnkeyatts</c>: how many of <see cref="Columns" /> are key columns.</summary>
    public int KeyColumnCount { get; init; }

    /// <summary><c>indclass</c>: each key column's operator class.</summary>
    public IReadOnlyList<uint> OperatorClasses { get; init; } = [];

    /// <summary><c>indcollation</c>: each key column's collation, zero where it has none.</summary>
    public IReadOnlyList<uint> Collations { get; init; } = [];

    /// <summary><c>indoption</c>: each key column's sort flags (descending, nulls first).</summary>
    public IReadOnlyList<short> Options { get; init; } = [];

    /// <summary>Whether it enforces uniqueness.</summary>
    public bool IsUnique { get; init; }

    /// <summary>Whether it backs the primary key.</summary>
    public bool IsPrimaryKey { get; init; }

    /// <summary><c>indisvalid</c>: <see langword="false" /> after a failed concurrent build, or for a partitioned
    /// index some partition has no attached index for.</summary>
    public bool IsValid { get; init; } = true;

    /// <summary>Its size, from <c>relpages</c> - its partitions' indexes' included.</summary>
    public long Bytes { get; init; }

    /// <summary><c>idx_scan</c>, its partitions' indexes' included; <see langword="null" /> when the collector has nothing.</summary>
    public long? Scans { get; init; }

    /// <summary>
    /// Every column of the table it reads: its plain key and <c>INCLUDE</c> columns, and every column its
    /// expressions and its predicate reference (from <c>pg_depend</c>). An update that changes one of these
    /// cannot be a HOT update.
    /// </summary>
    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    /// <summary>
    /// The phase of an index build running right now that is building this index
    /// (<c>pg_stat_progress_create_index</c>), or <see langword="null" />: one whose <c>index_relid</c> is this
    /// index; a <c>REINDEX CONCURRENTLY</c> on the table whose reported index this is the <c>_ccnew</c> or
    /// <c>_ccold</c> copy of (its first phase reports the index it copies, not the copy); or, for a partitioned
    /// index, a <c>CREATE INDEX</c> on a partition, which the index waits for to be attached. Never a build of
    /// another index on the same table, which says nothing about why this one is invalid.
    /// </summary>
    public string? BuildPhase { get; init; }

    /// <summary>
    /// The phase of any index build running right now on the table or one of its partitions, or
    /// <see langword="null" /> - for the caveat that a re-apply would queue behind it.
    /// </summary>
    public string? TableBuildPhase { get; init; }

    /// <summary>
    /// Whether somebody holds or awaits an <c>ACCESS EXCLUSIVE</c> lock on the table - a migration's <c>ALTER</c> - so the
    /// index was not deparsed and <see cref="Definition" /> and <see cref="Columns" /> are empty: <c>pg_get_indexdef</c>
    /// opens the table and would queue behind it (<see cref="HealthTableQueries.IndexesSql" />).
    /// </summary>
    public bool TableLocked { get; init; }

    /// <summary>How many index builds in this database the reading role cannot see the table of.</summary>
    public int UnseenIndexBuilds { get; init; }

    /// <summary>The server's <c>server_version_num</c>.</summary>
    public int ServerVersion { get; init; } = 170_000;

    /// <summary><c>indisexclusion</c>: it backs an exclusion constraint.</summary>
    public bool IsExclusion { get; init; }

    /// <summary><c>indimmediate</c>: its uniqueness is checked at once rather than deferred.</summary>
    public bool IsImmediate { get; init; } = true;

    /// <summary><c>indnullsnotdistinct</c> (PostgreSQL 15+): a unique index that treats nulls as equal.</summary>
    public bool NullsNotDistinct { get; init; }

    /// <summary>The key the findings name this index by: schema, table, index.</summary>
    public string Key => Schema + "." + Table + "." + Name;

    /// <summary>The table's key.</summary>
    public string TableKey => Schema + "." + Table;
}

/// <summary>What the statistics say of one soft-deleted table's <c>mt_deleted</c> column.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="Table">The table.</param>
/// <param name="EstimatedRows">The rows Postgres estimates, over the leaf partitions.</param>
/// <param name="RowsWithStatistics">How many of those rows sit in tables or partitions <c>pg_stats</c> has a row for.</param>
/// <param name="DeletedShare">
/// The share of rows with <c>mt_deleted = true</c>, weighted over the partitions that have statistics, or
/// <see langword="null" /> when none has: never analysed, or a role that may not read the column.
/// </param>
internal sealed record SoftDeleteShareRow(string Schema, string Table, double EstimatedRows, double RowsWithStatistics, double? DeletedShare);

/// <summary>One row of <c>mt_hilo</c>.</summary>
/// <param name="EntityName">The sequence's name: the document type's name, or its <c>HiloSettings.SequenceName</c>.</param>
/// <param name="HiValue">The last "hi" handed out; the ids up to <c>(hi + 1) × MaxLo</c> may have been issued.</param>
internal sealed record HiloRow(string EntityName, long HiValue);

/// <summary>What one bounded sample of a document table's <c>data</c> column measured.</summary>
/// <param name="Sampled">How many rows the sample read.</param>
/// <param name="P95Bytes">The 95th percentile of <c>pg_column_size(data)</c> - the stored, compressed size.</param>
/// <param name="MaxBytes">The largest stored document the sample saw.</param>
internal sealed record DocumentSizeSampleRow(int Sampled, double? P95Bytes, long? MaxBytes);

/// <summary>
/// The statements behind the tables-and-indexes health checks (T01, T03-T12).
/// </summary>
/// <remarks>
/// <para>
/// <b>A list locks nothing it lists</b> (AGENTS.md D27). Every list statement here reads <c>pg_class</c>,
/// <c>pg_index</c>, <c>pg_inherits</c>, <c>pg_depend</c>, <c>pg_stats</c> and the statistics views - the
/// catalog, which costs catalog <c>AccessShareLock</c>s and nothing else. Sizes are <c>relpages</c> estimates,
/// never a size function, because <c>pg_relation_size</c> opens the relation and queues behind any migration
/// holding a lock on it; partitions are walked through <c>pg_inherits</c>, never <c>pg_partition_tree()</c>,
/// which holds a lock on every partition until the transaction ends. A tenant-partitioned table has thousands
/// of partitions and the lock table is shared by the whole server. <c>HealthTableQueriesTests</c> holds every
/// list statement to it, and the live sweep test measures it.
/// </para>
/// <para>
/// <b>Names are values.</b> The tables come from <c>SchemaDeclarationReader</c> - never from
/// <c>AllObjects()</c> or <c>AllSchemaNames()</c>, which migrate (hard rule 14) - and travel as two
/// <c>text[]</c> parameters, so a statement shown under "Show SQL" names no table at all, and a hidden
/// document type's table is never in a statement's text. Only the two statements that must read one table's
/// rows - <c>mt_hilo</c>, and the deeper checks' sample of one visible document table - put a name in the
/// text, and that name goes through <see cref="SqlIdentifier" />.
/// </para>
/// <para>
/// <b>Fresh every call.</b> A <see cref="HealthQuery" /> carries its <see cref="NpgsqlParameter" />s, and a
/// parameter belongs to one command.
/// </para>
/// </remarks>
internal static class HealthTableQueries
{
    /// <summary>The bookkeeping table Marten's HiLo ids are drawn from, in the store's document schema.</summary>
    internal const string HiloTable = "mt_hilo";

    /// <summary>The soft-delete flag column Marten adds to a soft-deleted document table.</summary>
    internal const string DeletedColumn = "mt_deleted";

    /// <summary>
    /// The listed tables, rolled up: sizes from <c>relpages</c>, the collector's counters summed over the
    /// partitions, the latest maintenance times, the oldest transaction-id age, the storage parameters, and
    /// the database-wide facts the evaluators need beside each row.
    /// </summary>
    internal const string TablesSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind, c.reltuples, c.relpages, c.reltoastrelid, c.reloptions,
                   case when c.relkind = 'r' then pg_catalog.age(c.relfrozenxid) end as xid_age
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        tree (root, relid) as (
            select i.inhparent, i.inhrelid
            from pg_catalog.pg_inherits i
            join listed l on l.oid = i.inhparent
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where l.relkind = 'p' and ch.relispartition
            union all
            select t.root, i.inhrelid
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where ch.relispartition
        ),
        rolled as (
            select t.root,
                   pg_catalog.sum(d.relpages::bigint) as heap_pages,
                   pg_catalog.sum(coalesce(toast.relpages, 0)::bigint) as toast_pages,
                   pg_catalog.sum((select coalesce(pg_catalog.sum(ic.relpages::bigint), 0)
                                   from pg_catalog.pg_index x
                                   join pg_catalog.pg_class ic on ic.oid = x.indexrelid
                                   where x.indrelid = d.oid)) as index_pages,
                   pg_catalog.bool_and(d.reltuples < 0) filter (where d.relkind = 'r') as unanalyzed,
                   pg_catalog.sum(greatest(d.reltuples, 0)) filter (where d.relkind = 'r') as tuples,
                   pg_catalog.sum(s.n_live_tup) as live,
                   pg_catalog.sum(s.n_dead_tup) as dead,
                   pg_catalog.sum(s.seq_scan) as seq,
                   pg_catalog.sum(s.seq_tup_read) as seq_read,
                   pg_catalog.sum(s.idx_scan) as idx,
                   pg_catalog.sum(s.n_tup_ins) as ins,
                   pg_catalog.sum(s.n_tup_upd) as upd,
                   pg_catalog.sum(s.n_tup_del) as del,
                   pg_catalog.sum(s.n_tup_hot_upd) as hot,
                   pg_catalog.sum(s.n_mod_since_analyze) as modified,
                   pg_catalog.max(s.last_vacuum) as vacuumed,
                   pg_catalog.max(s.last_autovacuum) as autovacuumed,
                   pg_catalog.max(s.last_analyze) as analyzed,
                   pg_catalog.max(s.last_autoanalyze) as autoanalyzed,
                   pg_catalog.max(greatest(
                       case when d.relkind = 'r' then pg_catalog.age(d.relfrozenxid) end,
                       case when toast.oid is not null then pg_catalog.age(toast.relfrozenxid) end)) as xid_age,
                   pg_catalog.count(*) filter (where d.relkind = 'r') as leaves,
                   pg_catalog.max(s.n_live_tup) filter (where d.relkind = 'r') as largest_leaf
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            left join pg_catalog.pg_class toast on toast.oid = d.reltoastrelid
            left join pg_catalog.pg_stat_user_tables s on s.relid = d.oid
            group by t.root
        ),
        leaf_options as (
            select t.root,
                   pg_catalog.split_part(o.opt, '=', 1) as name,
                   pg_catalog.substr(o.opt, pg_catalog.strpos(o.opt, '=') + 1) as value
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            cross join lateral pg_catalog.unnest(d.reloptions) as o(opt)
            where d.relkind = 'r'
        ),
        option_agreement as (
            select o.root,
                   o.name,
                   pg_catalog.min(o.value) as value,
                   pg_catalog.count(distinct o.value) = 1 and pg_catalog.count(*) = r.leaves as agreed
            from leaf_options o
            join rolled r on r.root = o.root
            group by o.root, o.name, r.leaves
        ),
        options_rolled as (
            select a.root,
                   pg_catalog.array_agg(a.name || '=' || a.value order by a.name) filter (where a.agreed) as agreed,
                   pg_catalog.array_agg(a.name order by a.name) filter (where not a.agreed) as differing
            from option_agreement a
            group by a.root
        ),
        builds as (
            select p.relid, p.index_relid, p.command::text as command, p.phase::text as phase
            from pg_catalog.pg_stat_progress_create_index p
            where p.datid = (select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database())
        ),
        tree_invalid as (
            select t.root,
                   (select b.phase
                    from builds b
                    where b.index_relid = x.indexrelid
                       or (b.command = 'REINDEX CONCURRENTLY'
                           and b.relid = d.oid
                           and xc.relname ~ '_cc(new|old)[0-9]*$'
                           and exists (
                               select 1
                               from pg_catalog.pg_class bi
                               where bi.oid = b.index_relid
                                 and pg_catalog.left(bi.relname::text,
                                         pg_catalog.length(pg_catalog.regexp_replace(xc.relname::text, '_cc(new|old)[0-9]*$', '')))
                                     = pg_catalog.regexp_replace(xc.relname::text, '_cc(new|old)[0-9]*$', '')))
                       or (xc.relkind = 'I'
                           and b.command not like 'REINDEX%'
                           and b.relid in (select t2.relid from tree t2 where t2.root = t.root))
                    limit 1) as phase
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            join pg_catalog.pg_index x on x.indrelid = d.oid and not x.indisvalid
            join pg_catalog.pg_class xc on xc.oid = x.indexrelid
        ),
        invalid_rolled as (
            select v.root,
                   pg_catalog.count(*) filter (where v.phase is null) as invalid,
                   pg_catalog.count(*) filter (where v.phase is not null) as building,
                   pg_catalog.min(v.phase) as building_phase
            from tree_invalid v
            group by v.root
        ),
        settings as (
            select pg_catalog.current_setting('block_size')::bigint as block_size,
                   (select db.stats_reset
                    from pg_catalog.pg_stat_database db
                    where db.datname = pg_catalog.current_database()) as stats_reset,
                   pg_catalog.current_setting('autovacuum')::boolean as autovacuum,
                   pg_catalog.current_setting('max_parallel_workers_per_gather')::int as parallel_workers,
                   (select pg_catalog.count(*) from builds b where b.relid is null)::int as unseen_builds,
                   pg_catalog.current_setting('autovacuum_freeze_max_age')::bigint as freeze_max_age,
                   pg_catalog.current_setting('server_version_num')::int as server_version
        )
        select l.nspname::text,
               l.relname::text,
               l.relkind::text,
               case
                   when l.relkind <> 'p' then l.reltuples::bigint
                   when coalesce(r.unanalyzed, true) then -1
                   else r.tuples::bigint
               end,
               (l.relpages::bigint + coalesce(r.heap_pages, 0)) * st.block_size,
               (coalesce(lt.relpages, 0)::bigint + coalesce(r.toast_pages, 0)) * st.block_size,
               ((select coalesce(pg_catalog.sum(ic.relpages::bigint), 0)
                 from pg_catalog.pg_index x
                 join pg_catalog.pg_class ic on ic.oid = x.indexrelid
                 where x.indrelid = l.oid) + coalesce(r.index_pages, 0)) * st.block_size,
               case when l.relkind = 'p' then r.live::bigint else s.n_live_tup end,
               case when l.relkind = 'p' then r.dead::bigint else s.n_dead_tup end,
               case when l.relkind = 'p' then r.seq::bigint else s.seq_scan end,
               case when l.relkind = 'p' then r.seq_read::bigint else s.seq_tup_read end,
               case when l.relkind = 'p' then r.idx::bigint else s.idx_scan end,
               case when l.relkind = 'p' then r.ins::bigint else s.n_tup_ins end,
               case when l.relkind = 'p' then r.upd::bigint else s.n_tup_upd end,
               case when l.relkind = 'p' then r.del::bigint else s.n_tup_del end,
               case when l.relkind = 'p' then r.hot::bigint else s.n_tup_hot_upd end,
               case when l.relkind = 'p' then r.modified::bigint else s.n_mod_since_analyze end,
               greatest(s.last_vacuum, r.vacuumed),
               greatest(s.last_autovacuum, r.autovacuumed),
               greatest(s.last_analyze, r.analyzed),
               greatest(s.last_autoanalyze, r.autoanalyzed),
               greatest(l.xid_age, case when lt.oid is not null then pg_catalog.age(lt.relfrozenxid) end, r.xid_age)::bigint,
               coalesce(ir.invalid, 0)::int,
               case
                   when l.relkind = 'p' then coalesce(orl.agreed, '{}'::text[])
                   else coalesce(l.reloptions, '{}'::text[])
               end,
               st.stats_reset,
               st.autovacuum,
               st.parallel_workers,
               coalesce(r.leaves, 0)::int,
               r.largest_leaf::bigint,
               coalesce(orl.differing, '{}'::text[]),
               ir.building_phase,
               st.unseen_builds,
               st.freeze_max_age,
               st.server_version,
               coalesce(ir.building, 0)::int,
               (select b.phase
                from builds b
                where b.relid = l.oid or b.relid in (select t.relid from tree t where t.root = l.oid)
                limit 1)
        from listed l
        cross join settings st
        left join pg_catalog.pg_class lt on lt.oid = l.reltoastrelid
        left join rolled r on r.root = l.oid
        left join invalid_rolled ir on ir.root = l.oid
        left join options_rolled orl on orl.root = l.oid
        left join pg_catalog.pg_stat_user_tables s on s.relid = l.oid
        order by 1, 2
        """;

    /// <summary>
    /// Every index on the listed tables - never an index on a partition, whose pages and scans are added into
    /// its partitioned parent index's - with what the redundancy, validity, use and HOT judgements need.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>pg_get_indexdef</c> deparses each index, as Weasel's own index read does and as the Schema screen's always
    /// has - opening its table to do so (below); it is called for the listed tables' indexes only, never a partition's. The
    /// referenced columns come from <c>pg_depend</c>, where Postgres records the columns an expression or a
    /// predicate reads, and from <c>indkey</c> for the plain ones - an index backing a constraint depends on the
    /// constraint rather than on its columns, so <c>pg_depend</c> alone would miss them.
    /// </para>
    /// <para>
    /// <b>Never deparsed behind a migration.</b> <c>pg_get_indexdef</c>, in every form, builds its deparse context with
    /// <c>deparse_context_for</c>, which opens the indexed table with <c>AccessShareLock</c> and closes it again
    /// (<c>set_relation_column_names</c>, PG 14-19): nothing is held once a call returns, but behind somebody's
    /// <c>ACCESS EXCLUSIVE</c> lock on the table the call queues, like a size function (B0 §3) - measured by
    /// <c>VersionMatrixLiveTests</c> on 14 to 19, where the regular sweep's index checks all waited out the
    /// <c>lock_timeout</c>. So <c>locked</c> reads <c>pg_locks</c> once (a shared-memory snapshot, no relation lock) for
    /// any <c>AccessExclusiveLock</c> held or awaited on a listed table, and such a table's indexes come back undeparsed,
    /// marked in the last column (<see cref="HealthIndexRow.TableLocked" />); the reader leaves that table's indexes out and
    /// counts it, so every other table is still judged and each index check says how many were not
    /// (<c>HealthTables.ReadIndexesAsync</c>). A lock taken between the snapshot and a call still waits, bounded by
    /// <c>lock_timeout</c>; the reader then asks once more, when the snapshot sees it.
    /// </para>
    /// </remarks>
    internal const string IndexesSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        locked as (
            select distinct k.relation as oid
            from pg_catalog.pg_locks k
            where k.locktype = 'relation'
              and k.mode = 'AccessExclusiveLock'
              and k.database = (select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database())
              and k.relation in (select l.oid from listed l)
        ),
        indexes as (
            select l.nspname, l.relname as table_name, l.oid as table_oid,
                   ic.oid, ic.relname as index_name, ic.relkind, ic.relpages,
                   am.amname,
                   x.indisunique, x.indisprimary, x.indisvalid, x.indnatts, x.indnkeyatts,
                   x.indkey, x.indclass, x.indcollation, x.indoption,
                   x.indisexclusion, x.indimmediate,
                   coalesce((pg_catalog.to_jsonb(x) ->> 'indnullsnotdistinct')::boolean, false) as nulls_not_distinct
            from listed l
            join pg_catalog.pg_index x on x.indrelid = l.oid
            join pg_catalog.pg_class ic on ic.oid = x.indexrelid
            join pg_catalog.pg_am am on am.oid = ic.relam
        ),
        table_tree (root, relid) as (
            select l.oid, l.oid
            from listed l
            union all
            select t.root, inh.inhrelid
            from table_tree t
            join pg_catalog.pg_inherits inh on inh.inhparent = t.relid
        ),
        builds as (
            select p.relid, p.index_relid, p.command::text as command, p.phase::text as phase
            from pg_catalog.pg_stat_progress_create_index p
            where p.datid = (select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database())
        ),
        tree (root, relid) as (
            select inh.inhparent, inh.inhrelid
            from pg_catalog.pg_inherits inh
            join indexes i on i.oid = inh.inhparent
            where i.relkind = 'I'
            union all
            select t.root, inh.inhrelid
            from tree t
            join pg_catalog.pg_inherits inh on inh.inhparent = t.relid
        ),
        rolled as (
            select t.root,
                   pg_catalog.sum(d.relpages::bigint) as pages,
                   pg_catalog.sum(s.idx_scan) as scans
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            left join pg_catalog.pg_stat_user_indexes s on s.indexrelid = d.oid
            group by t.root
        )
        select i.nspname::text,
               i.table_name::text,
               i.index_name::text,
               i.amname::text,
               case when i.table_oid in (select lk.oid from locked lk) then null
                    else pg_catalog.pg_get_indexdef(i.oid) end,
               case when i.table_oid in (select lk.oid from locked lk) then null
                    else array(select pg_catalog.pg_get_indexdef(i.oid, k, true)
                               from pg_catalog.generate_series(1, i.indnatts::int) as k
                               order by k) end,
               i.indnkeyatts::int,
               i.indclass::pg_catalog.oid[],
               i.indcollation::pg_catalog.oid[],
               i.indoption::pg_catalog.int2[],
               i.indisunique,
               i.indisprimary,
               i.indisvalid,
               (i.relpages::bigint + coalesce(r.pages, 0)) * pg_catalog.current_setting('block_size')::bigint,
               case when i.relkind = 'I' then r.scans::bigint else s.idx_scan end,
               array(select a.attname::text
                     from pg_catalog.pg_attribute a
                     where a.attrelid = i.table_oid
                       and a.attnum > 0
                       and not a.attisdropped
                       and (a.attnum = any(i.indkey::pg_catalog.int2[])
                            or exists (
                                select 1
                                from pg_catalog.pg_depend d
                                where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                                  and d.objid = i.oid
                                  and d.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
                                  and d.refobjid = i.table_oid
                                  and d.refobjsubid = a.attnum))
                     order by a.attnum),
               (select b.phase
                from builds b
                where b.index_relid = i.oid
                   or (b.command = 'REINDEX CONCURRENTLY'
                       and b.relid = i.table_oid
                       and i.index_name ~ '_cc(new|old)[0-9]*$'
                       and exists (
                           select 1
                           from pg_catalog.pg_class bi
                           where bi.oid = b.index_relid
                             and pg_catalog.left(bi.relname::text,
                                     pg_catalog.length(pg_catalog.regexp_replace(i.index_name::text, '_cc(new|old)[0-9]*$', '')))
                                 = pg_catalog.regexp_replace(i.index_name::text, '_cc(new|old)[0-9]*$', '')))
                   or (i.relkind = 'I'
                       and b.command not like 'REINDEX%'
                       and b.relid in (select t.relid from table_tree t where t.root = i.table_oid))
                limit 1),
               (select pg_catalog.count(*) from builds b where b.relid is null)::int,
               pg_catalog.current_setting('server_version_num')::int,
               i.indisexclusion,
               i.indimmediate,
               i.nulls_not_distinct,
               (select b.phase
                from builds b
                where b.relid in (select t.relid from table_tree t where t.root = i.table_oid)
                limit 1),
               i.table_oid in (select lk.oid from locked lk)
        from indexes i
        left join rolled r on r.root = i.oid
        left join pg_catalog.pg_stat_user_indexes s on s.indexrelid = i.oid
        order by 1, 2, 3
        """;

    /// <summary>
    /// The share of <c>mt_deleted = true</c> in each listed soft-deleted table, from <c>pg_stats</c>'
    /// most-common-value frequencies, weighted over the leaf partitions by their <c>reltuples</c>.
    /// </summary>
    /// <remarks>
    /// <c>pg_stats</c> shows only the columns the reading role may select, so a role without that privilege
    /// reads no statistics and the check says "cannot see" rather than "none". A partitioned parent is
    /// judged by its leaves' own statistics (<c>inherited = false</c>): autovacuum never analyses a
    /// partitioned parent, so its inherited row is usually missing.
    /// </remarks>
    internal const string SoftDeleteSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        tree (root, relid, relkind) as (
            select l.oid, l.oid, l.relkind
            from listed l
            union all
            select t.root, i.inhrelid, ch.relkind
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where t.relkind = 'p' and ch.relispartition
        ),
        leaves as (
            select t.root,
                   greatest(c.reltuples, 0)::float8 as tuples,
                   (select coalesce(
                               s.most_common_freqs[pg_catalog.array_position(s.most_common_vals::text::boolean[], true)],
                               0)::float8
                    from pg_catalog.pg_stats s
                    where s.schemaname = ln.nspname
                      and s.tablename = c.relname
                      and s.attname = 'mt_deleted'
                      and not s.inherited) as deleted_share
            from tree t
            join pg_catalog.pg_class c on c.oid = t.relid
            join pg_catalog.pg_namespace ln on ln.oid = c.relnamespace
            where t.relkind = 'r'
        )
        select l.nspname::text,
               l.relname::text,
               coalesce(pg_catalog.sum(v.tuples), 0)::float8,
               coalesce(pg_catalog.sum(v.tuples) filter (where v.deleted_share is not null), 0)::float8,
               (pg_catalog.sum(v.tuples * v.deleted_share) filter (where v.deleted_share is not null)
                   / nullif(pg_catalog.sum(v.tuples) filter (where v.deleted_share is not null), 0))::float8
        from listed l
        left join leaves v on v.root = l.oid
        group by l.nspname, l.relname
        order by 1, 2
        """;

    /// <summary>Whether the store's document schema has an <c>mt_hilo</c> table - read first, so a missing one is never a <c>42P01</c>.</summary>
    internal const string HiloExistsSql =
        """
        select exists (
            select 1
            from pg_catalog.pg_class c
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where n.nspname = @schema
              and c.relname = @table
              and c.relkind in ('r', 'p'))
        """;

    /// <summary>
    /// The ordinary and partitioned tables of the schemas a plain data source's <c>BrowsableSchemas</c> admits - its own,
    /// named exactly, and, under <c>"*"</c>, every other schema the reading role has <c>USAGE</c> on that no extension
    /// owns - largest first by estimated size, at most <c>@cap</c> of them, each row carrying how many there are in all
    /// (issue #8 G1; design R5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same admission as the database browser's</b> (<c>BrowsableSchemaMatcher.Match</c>): never a system schema,
    /// never one without <c>USAGE</c>, and <c>"*"</c> never an extension's schema while an exact name may be. A table an
    /// extension owns is no host's. A partition is judged through its partitioned table, so it is not a row of its own.
    /// </para>
    /// <para>
    /// <b>Estimates only, and locks nothing it lists</b> (D27; B0: a size function waits behind a migration's lock).
    /// The size is <c>relpages</c> - summed over the partition tree, walked through <c>pg_inherits</c>, for a partitioned
    /// table - never <c>pg_relation_size</c>. A database of ten thousand tables is capped at the largest
    /// <c>@cap</c>, and the total (<c>count(*) over ()</c>, computed before the limit) is what the coverage sentence
    /// says was left out.
    /// </para>
    /// </remarks>
    internal const string RelationalTablesSql =
        """
        with recursive admitted as (
            select n.oid, n.nspname
            from pg_catalog.pg_namespace n
            where pg_catalog.left(n.nspname::text, 3) <> 'pg_'
              and not (n.nspname = any(@system))
              and coalesce(pg_catalog.has_schema_privilege(n.oid, 'USAGE'), false)
              and (n.nspname = any(@names)
                   or (@everything and not exists (
                       select 1
                       from pg_catalog.pg_depend d
                       where d.classid = 'pg_catalog.pg_namespace'::pg_catalog.regclass
                         and d.objid = n.oid
                         and d.deptype = 'e')))
        ),
        candidates as (
            select c.oid, a.nspname, c.relname, c.relkind, c.relpages
            from pg_catalog.pg_class c
            join admitted a on a.oid = c.relnamespace
            where c.relkind in ('r', 'p')
              and not c.relispartition
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and d.objid = c.oid
                    and d.deptype = 'e')
        ),
        tree (root, relid) as (
            select t.oid, i.inhrelid
            from candidates t
            join pg_catalog.pg_inherits i on i.inhparent = t.oid
            where t.relkind = 'p'
            union all
            select tree.root, i.inhrelid
            from tree
            join pg_catalog.pg_inherits i on i.inhparent = tree.relid
        ),
        partitioned (root, pages) as (
            select tree.root, pg_catalog.sum(x.relpages::bigint)
            from tree
            join pg_catalog.pg_class x on x.oid = tree.relid
            group by tree.root
        )
        select t.nspname::text,
               t.relname::text,
               t.relkind::text,
               case when t.relkind = 'p' then coalesce(p.pages, 0)::bigint else t.relpages::bigint end,
               (pg_catalog.count(*) over ())::int
        from candidates t
        left join partitioned p on p.root = t.oid
        order by 4 desc, 1, 2
        limit @cap
        """;

    /// <summary>
    /// <see cref="RelationalTablesSql" /> for a source that declares tables of its own (issue #8, G1 for a Marten store):
    /// the same admission, the same estimates, the same order and cap, and two more lines in <c>candidates</c> - a table
    /// the configuration of a registered source declares is left out (<c>@declared_schemas</c>, <c>@declared_tables</c>,
    /// compared ignoring case as the source's own sets compare them), and, with <c>@skip_documents</c>, so is every table
    /// named like a document table (<c>@document_prefix</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Left out before the count and the cap</b>, so the total the coverage sentence says is the number of tables that
    /// could have been judged, and the cap is spent on them alone: a declared table is judged from the configuration, never
    /// from this list, and another source's is not this sweep's to judge.
    /// </para>
    /// <para>
    /// <b>A hidden document type's table is never a value here.</b> The caller passes <c>@skip_documents</c> whenever a
    /// document type may be hidden, so such a table never comes back from the catalog at all and its name is not in any
    /// parameter: only visible declarations are ever bound.
    /// </para>
    /// <para>
    /// Kept the same as <see cref="RelationalTablesSql" /> but for those two predicates, which
    /// <c>HealthTableQueriesTests</c> holds it to: removing them gives back that statement byte for byte.
    /// </para>
    /// </remarks>
    internal const string UndeclaredTablesSql =
        """
        with recursive admitted as (
            select n.oid, n.nspname
            from pg_catalog.pg_namespace n
            where pg_catalog.left(n.nspname::text, 3) <> 'pg_'
              and not (n.nspname = any(@system))
              and coalesce(pg_catalog.has_schema_privilege(n.oid, 'USAGE'), false)
              and (n.nspname = any(@names)
                   or (@everything and not exists (
                       select 1
                       from pg_catalog.pg_depend d
                       where d.classid = 'pg_catalog.pg_namespace'::pg_catalog.regclass
                         and d.objid = n.oid
                         and d.deptype = 'e')))
        ),
        candidates as (
            select c.oid, a.nspname, c.relname, c.relkind, c.relpages
            from pg_catalog.pg_class c
            join admitted a on a.oid = c.relnamespace
            where c.relkind in ('r', 'p')
              and not c.relispartition
              and not (@skip_documents and pg_catalog.starts_with(pg_catalog.lower(c.relname::text), @document_prefix))
              and not exists (
                  select 1
                  from rows from (pg_catalog.unnest(@declared_schemas), pg_catalog.unnest(@declared_tables)) as x(schema_name, table_name)
                  where pg_catalog.lower(x.schema_name) = pg_catalog.lower(a.nspname::text)
                    and pg_catalog.lower(x.table_name) = pg_catalog.lower(c.relname::text))
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and d.objid = c.oid
                    and d.deptype = 'e')
        ),
        tree (root, relid) as (
            select t.oid, i.inhrelid
            from candidates t
            join pg_catalog.pg_inherits i on i.inhparent = t.oid
            where t.relkind = 'p'
            union all
            select tree.root, i.inhrelid
            from tree
            join pg_catalog.pg_inherits i on i.inhparent = tree.relid
        ),
        partitioned (root, pages) as (
            select tree.root, pg_catalog.sum(x.relpages::bigint)
            from tree
            join pg_catalog.pg_class x on x.oid = tree.relid
            group by tree.root
        )
        select t.nspname::text,
               t.relname::text,
               t.relkind::text,
               case when t.relkind = 'p' then coalesce(p.pages, 0)::bigint else t.relpages::bigint end,
               (pg_catalog.count(*) over ())::int
        from candidates t
        left join partitioned p on p.root = t.oid
        order by 4 desc, 1, 2
        limit @cap
        """;

    /// <summary>
    /// The prefix every Marten document table's name carries - <c>DatabaseObjectClassifier.DocumentTablePrefix</c>, bound
    /// rather than spelt in <see cref="UndeclaredTablesSql" />, and held equal to it by a test.
    /// </summary>
    internal const string DocumentTablePrefix = "mt_doc_";

    /// <summary>Every list statement - the ones that must lock nothing they list - for the tests that hold them to it.</summary>
    internal static IReadOnlyList<string> ListStatements { get; } =
        [TablesSql, IndexesSql, SoftDeleteSql, HiloExistsSql, RelationalTablesSql, UndeclaredTablesSql];

    /// <summary>
    /// The tables a plain data source's health checks judge (<see cref="RelationalTablesSql" />): every table in
    /// <paramref name="names" />, and with <paramref name="everything" /> every schema <c>"*"</c> admits, the largest
    /// <paramref name="cap" /> by estimated size.
    /// </summary>
    /// <param name="names">The schemas named exactly.</param>
    /// <param name="everything">Whether <c>"*"</c> is configured.</param>
    /// <param name="cap">How many tables at most.</param>
    public static HealthQuery RelationalTables(IReadOnlyList<string> names, bool everything, int cap)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);

        return new HealthQuery(
            RelationalTablesSql,
            [
                new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names as string[] ?? [.. names] },
                new NpgsqlParameter("everything", NpgsqlDbType.Boolean) { Value = everything },
                new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = cap },
                new NpgsqlParameter("system", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = SystemSchemasBeyondPrefix },
            ]);
    }

    /// <summary>
    /// The tables of the admitted schemas but the ones a registered source declares (<see cref="UndeclaredTablesSql" />):
    /// the schemas named in <paramref name="names" /> and, with <paramref name="everything" />, every schema <c>"*"</c>
    /// admits, the largest <paramref name="cap" /> by estimated size.
    /// </summary>
    /// <param name="names">The schemas named exactly.</param>
    /// <param name="everything">Whether <c>"*"</c> is configured and in force.</param>
    /// <param name="declared">
    /// The tables registered sources declare, left out of the list and its count. Never a hidden document type's: those are
    /// left out by <paramref name="skipDocumentTables" />.
    /// </param>
    /// <param name="skipDocumentTables">Whether to leave out every table named like a document table.</param>
    /// <param name="cap">How many tables at most.</param>
    public static HealthQuery UndeclaredTables(
        IReadOnlyList<string> names,
        bool everything,
        IReadOnlyList<HealthTableTarget> declared,
        bool skipDocumentTables,
        int cap)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);

        NpgsqlParameter[] targets = TargetParameters(declared);

        return new HealthQuery(
            UndeclaredTablesSql,
            [
                new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names as string[] ?? [.. names] },
                new NpgsqlParameter("everything", NpgsqlDbType.Boolean) { Value = everything },
                new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = cap },
                new NpgsqlParameter("system", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = SystemSchemasBeyondPrefix },
                new NpgsqlParameter("skip_documents", NpgsqlDbType.Boolean) { Value = skipDocumentTables },
                new NpgsqlParameter("document_prefix", NpgsqlDbType.Text) { Value = DocumentTablePrefix },
                new NpgsqlParameter("declared_schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = targets[0].Value },
                new NpgsqlParameter("declared_tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = targets[1].Value },
            ]);
    }

    /// <summary>
    /// The system schemas whose names do not start <c>pg_</c> - which the statement refuses by prefix - as
    /// <c>BrowsableSchemaMatcher.IsSystemSchema</c> names them: bound, so the statement and the browser cannot disagree.
    /// </summary>
    internal static string[] SystemSchemasBeyondPrefix => ["information_schema"];

    /// <summary>Maps one row of <see cref="RelationalTablesSql" />.</summary>
    public static HealthRelationalTableRow ReadRelationalTable(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthRelationalTableRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt32(4));
    }

    /// <summary>The listed tables, rolled up (<see cref="TablesSql" />).</summary>
    public static HealthQuery Tables(IReadOnlyList<HealthTableTarget> tables) => new(TablesSql, TargetParameters(tables));

    /// <summary>Every index on the listed tables (<see cref="IndexesSql" />).</summary>
    public static HealthQuery Indexes(IReadOnlyList<HealthTableTarget> tables) => new(IndexesSql, TargetParameters(tables));

    /// <summary>The deleted share of each listed soft-deleted table (<see cref="SoftDeleteSql" />).</summary>
    public static HealthQuery SoftDeleteShares(IReadOnlyList<HealthTableTarget> tables) => new(SoftDeleteSql, TargetParameters(tables));

    /// <summary>Whether <c>mt_hilo</c> exists in <paramref name="schema" /> (<see cref="HiloExistsSql" />).</summary>
    public static HealthQuery HiloExists(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        return new HealthQuery(
            HiloExistsSql,
            [
                new NpgsqlParameter("schema", NpgsqlDbType.Text) { Value = schema },
                new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = HiloTable },
            ]);
    }

    /// <summary>
    /// The <c>mt_hilo</c> rows of the named sequences - asked only once <see cref="HiloExists" /> said the
    /// table is there, because reading a table that is not is a <c>42P01</c>, and creating it is exactly hard
    /// rule 14's trap.
    /// </summary>
    /// <remarks>
    /// The one list-path statement that reads a row of a user table, and so takes an <c>AccessShareLock</c> on
    /// <c>mt_hilo</c> - and on its primary key, which the planner opens to plan it - for its moment: a table of one
    /// row per HiLo sequence, under the session's three-second <c>lock_timeout</c>. The live sweep test names it
    /// as the only lock beyond the catalog's.
    /// </remarks>
    public static HealthQuery HiloValues(string schema, IReadOnlyList<string> entityNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(entityNames);

        return new HealthQuery(
            "select h.entity_name::text, h.hi_value::bigint\nfrom " + SqlIdentifier.Qualify(schema, HiloTable) + " as h\n"
            + "where h.entity_name = any(@names)\norder by 1",
            [new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = entityNames as string[] ?? [.. entityNames] }]);
    }

    /// <summary>
    /// A bounded sample of one document table's stored document sizes, for the deeper checks (T09):
    /// <c>TABLESAMPLE SYSTEM</c> over the percentage of its pages, and never more than
    /// <paramref name="limit" /> rows.
    /// </summary>
    /// <remarks>
    /// A data read, so tier 2 only - behind "Run deeper checks" and cached for five minutes - and it holds an
    /// <c>AccessShareLock</c> on the one table while it runs. Never asked of a partitioned table, whose sample
    /// would lock every partition at once, and never of a hidden type's table, whose name would then be in
    /// the statement's text. <c>pg_column_size</c> reads the stored size from the TOAST pointer and
    /// decompresses nothing.
    /// </remarks>
    public static HealthQuery DocumentSizeSample(string schema, string table, float percent, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100f);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return new HealthQuery(
            "select pg_catalog.count(*)::int,\n"
            + "       (pg_catalog.percentile_cont(0.95) within group (order by pg_catalog.pg_column_size(d.data)))::float8,\n"
            + "       pg_catalog.max(pg_catalog.pg_column_size(d.data))::bigint\n"
            + "from (select t.data from " + SqlIdentifier.Qualify(schema, table) + " as t tablesample system (@percent) limit @limit) as d",
            [
                new NpgsqlParameter("percent", NpgsqlDbType.Real) { Value = percent },
                new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit },
            ]);
    }

    /// <summary>Maps one row of <see cref="TablesSql" />.</summary>
    public static HealthTableRow ReadTable(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthTableRow
        {
            Schema = reader.GetString(0),
            Table = reader.GetString(1),
            Kind = reader.GetString(2),
            EstimatedRows = reader.GetInt64(3),
            HeapBytes = reader.GetInt64(4),
            ToastBytes = reader.GetInt64(5),
            IndexBytes = reader.GetInt64(6),
            LiveRows = NullableInt64(reader, 7),
            DeadRows = NullableInt64(reader, 8),
            SequentialScans = NullableInt64(reader, 9),
            SequentialRowsRead = NullableInt64(reader, 10),
            IndexScans = NullableInt64(reader, 11),
            Inserts = NullableInt64(reader, 12),
            Updates = NullableInt64(reader, 13),
            Deletes = NullableInt64(reader, 14),
            HotUpdates = NullableInt64(reader, 15),
            ModifiedSinceAnalyze = NullableInt64(reader, 16),
            LastVacuum = NullableTimestamp(reader, 17),
            LastAutovacuum = NullableTimestamp(reader, 18),
            LastAnalyze = NullableTimestamp(reader, 19),
            LastAutoanalyze = NullableTimestamp(reader, 20),
            OldestXidAge = NullableInt64(reader, 21),
            InvalidPartitionIndexes = reader.GetInt32(22),
            StorageOptions = reader.IsDBNull(23) ? [] : reader.GetFieldValue<string[]>(23),
            StatsReset = NullableTimestamp(reader, 24),
            AutovacuumEnabled = reader.IsDBNull(25) || reader.GetBoolean(25),
            MaxParallelWorkersPerGather = reader.IsDBNull(26) ? 0 : reader.GetInt32(26),
            PartitionLeaves = reader.GetInt32(27),
            LargestLeafRows = NullableInt64(reader, 28),
            DifferingStorageOptions = reader.IsDBNull(29) ? [] : reader.GetFieldValue<string[]>(29),
            PartitionBuildPhase = reader.IsDBNull(30) ? null : reader.GetString(30),
            UnseenIndexBuilds = reader.GetInt32(31),
            FreezeMaxAge = reader.GetInt64(32),
            ServerVersion = reader.GetInt32(33),
            BuildingPartitionIndexes = reader.GetInt32(34),
            TreeBuildPhase = reader.IsDBNull(35) ? null : reader.GetString(35),
        };
    }

    /// <summary>Maps one row of <see cref="IndexesSql" />.</summary>
    public static HealthIndexRow ReadIndex(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthIndexRow
        {
            Schema = reader.GetString(0),
            Table = reader.GetString(1),
            Name = reader.GetString(2),
            AccessMethod = reader.GetString(3),
            Definition = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            Columns = reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5),
            KeyColumnCount = reader.GetInt32(6),
            OperatorClasses = reader.IsDBNull(7) ? [] : reader.GetFieldValue<uint[]>(7),
            Collations = reader.IsDBNull(8) ? [] : reader.GetFieldValue<uint[]>(8),
            Options = reader.IsDBNull(9) ? [] : reader.GetFieldValue<short[]>(9),
            IsUnique = reader.GetBoolean(10),
            IsPrimaryKey = reader.GetBoolean(11),
            IsValid = reader.GetBoolean(12),
            Bytes = reader.GetInt64(13),
            Scans = NullableInt64(reader, 14),
            ReferencedColumns = reader.IsDBNull(15) ? [] : reader.GetFieldValue<string[]>(15),
            BuildPhase = reader.IsDBNull(16) ? null : reader.GetString(16),
            UnseenIndexBuilds = reader.GetInt32(17),
            ServerVersion = reader.GetInt32(18),
            IsExclusion = reader.GetBoolean(19),
            IsImmediate = reader.GetBoolean(20),
            NullsNotDistinct = reader.GetBoolean(21),
            TableBuildPhase = reader.IsDBNull(22) ? null : reader.GetString(22),
            TableLocked = !reader.IsDBNull(23) && reader.GetBoolean(23),
        };
    }

    /// <summary>Maps one row of <see cref="SoftDeleteSql" />.</summary>
    public static SoftDeleteShareRow ReadSoftDeleteShare(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new SoftDeleteShareRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.IsDBNull(4) ? null : reader.GetDouble(4));
    }

    /// <summary>Maps one row of <see cref="HiloValues" />.</summary>
    public static HiloRow ReadHilo(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HiloRow(reader.GetString(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
    }

    /// <summary>Maps the one row of <see cref="DocumentSizeSample" />.</summary>
    public static DocumentSizeSampleRow ReadDocumentSizeSample(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new DocumentSizeSampleRow(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static NpgsqlParameter[] TargetParameters(IReadOnlyList<HealthTableTarget> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        string[] schemas = new string[tables.Count];
        string[] names = new string[tables.Count];

        for (int i = 0; i < tables.Count; i++)
        {
            schemas[i] = tables[i].Schema;
            names[i] = tables[i].Table;
        }

        return
        [
            new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas },
            new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names },
        ];
    }

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
