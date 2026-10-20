using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// One table in one of the store's schemas, as the catalog and the statistics collector see it - never a
/// partition, whose figures are added into its parent's.
/// </summary>
/// <param name="Schema">The schema the table lives in.</param>
/// <param name="Table">The table name.</param>
/// <param name="TotalBytes">
/// <c>pg_total_relation_size</c> — heap, indexes, toast — plus, for a partitioned table, its partitions'
/// catalog page counts (see <see cref="SchemaStatsQueries.TableStatsSql" />).
/// </param>
/// <param name="HeapBytes"><c>pg_relation_size</c> — the main fork alone — plus the partitions' heap pages.</param>
/// <param name="IndexBytes"><c>pg_indexes_size</c>, plus the partitions' index pages.</param>
/// <param name="EstimatedRows">
/// <c>pg_class.reltuples</c> cast to <c>bigint</c>, which is an estimate and is <c>-1</c> before the
/// first analyze — the same shape <see cref="CountEstimator" /> reads, so the two agree. For a partitioned
/// table, the sum over its leaf partitions, and <c>-1</c> while none of them has been analyzed.
/// </param>
/// <param name="LiveRows"><c>n_live_tup</c>, or <see langword="null" /> when the statistics collector has nothing.</param>
/// <param name="DeadRows"><c>n_dead_tup</c>, or <see langword="null" />.</param>
/// <param name="SequentialScans"><c>seq_scan</c>, or <see langword="null" />.</param>
/// <param name="IndexScans"><c>idx_scan</c>, or <see langword="null" />.</param>
/// <param name="LastVacuum">The later of <c>last_vacuum</c> and <c>last_autovacuum</c> - over the partitions too.</param>
/// <param name="LastAnalyze">The later of <c>last_analyze</c> and <c>last_autoanalyze</c> - over the partitions too.</param>
/// <param name="Kind"><c>relkind</c>: <c>r</c> for an ordinary table, <c>p</c> for a partitioned one.</param>
/// <param name="PartitionCount">How many partitions hang directly off it; zero for an ordinary table.</param>
/// <param name="Updates"><c>n_tup_upd</c>, HOT updates included - over the partitions too - or <see langword="null" />.</param>
/// <param name="HotUpdates"><c>n_tup_hot_upd</c>, over the partitions too, or <see langword="null" />.</param>
/// <param name="LastAutovacuum"><c>last_autovacuum</c> alone - over the partitions too.</param>
/// <param name="ToastBytes">
/// The TOAST table and its index: <c>pg_total_relation_size</c> of the listed table's own TOAST relation, plus
/// its partitions' TOAST pages.
/// </param>
/// <param name="StorageOptions">
/// <c>reloptions</c>: the storage parameters set on the table, as <c>name=value</c>. For a partitioned table -
/// on which Postgres keeps none - the ones every leaf partition has at one value.
/// </param>
/// <param name="DifferingStorageOptions">
/// For a partitioned table, the parameters its leaf partitions do not all have at one value, by name only -
/// never a partition's.
/// </param>
internal sealed record TableStatsRow(
    string Schema,
    string Table,
    long TotalBytes,
    long HeapBytes,
    long IndexBytes,
    long EstimatedRows,
    long? LiveRows,
    long? DeadRows,
    long? SequentialScans,
    long? IndexScans,
    DateTimeOffset? LastVacuum,
    DateTimeOffset? LastAnalyze,
    string Kind = "r",
    int PartitionCount = 0,
    long? Updates = null,
    long? HotUpdates = null,
    DateTimeOffset? LastAutovacuum = null,
    long ToastBytes = 0,
    IReadOnlyList<string>? StorageOptions = null,
    IReadOnlyList<string>? DifferingStorageOptions = null)
{
    /// <summary>Whether it is a partitioned table, whose partitions are rolled into it.</summary>
    public bool IsPartitioned => Kind == "p";
}

/// <summary>One index in one of the store's schemas, with whatever use the database has recorded for it.</summary>
/// <param name="Schema">The schema the index lives in.</param>
/// <param name="Table">The table it is on.</param>
/// <param name="Name">The index name.</param>
/// <param name="Definition">The <c>create index</c> statement Postgres reports for it.</param>
/// <param name="Bytes"><c>pg_relation_size</c> of the index.</param>
/// <param name="Scans"><c>idx_scan</c>, or <see langword="null" /> when the collector has nothing.</param>
/// <param name="TuplesRead"><c>idx_tup_read</c>, or <see langword="null" />.</param>
/// <param name="IsPrimaryKey">Whether the index backs the table's primary key.</param>
/// <param name="IsUnique">Whether the index is unique.</param>
internal sealed record IndexStatsRow(
    string Schema,
    string Table,
    string Name,
    string Definition,
    long Bytes,
    long? Scans,
    long? TuplesRead,
    bool IsPrimaryKey,
    bool IsUnique)
{
    /// <summary><c>pg_am.amname</c>: <c>btree</c>, <c>gin</c>, <c>hnsw</c>, <c>ivfflat</c>...</summary>
    public string AccessMethod { get; init; } = "btree";

    /// <summary>Whether the <c>vector</c> extension owns the access method.</summary>
    public bool AccessMethodIsPgVector { get; init; }

    /// <summary>For an <c>hnsw</c> or <c>ivfflat</c> index, each key column's operator class.</summary>
    public IReadOnlyList<string?>? OperatorClasses { get; init; }

    /// <summary>For the same, each key column as <c>pg_get_indexdef(oid, k, true)</c> prints it.</summary>
    public IReadOnlyList<string?>? KeyDefinitions { get; init; }

    /// <summary>For the same, the index's <c>reloptions</c>.</summary>
    public IReadOnlyList<string>? Options { get; init; }
}

/// <summary>One pgvector column of a table in one of the store's schemas.</summary>
/// <param name="Schema">The table's schema.</param>
/// <param name="Table">The table.</param>
/// <param name="Column">The column.</param>
/// <param name="TypeName">Its type's - or its domain's base type's - <c>typname</c>: <c>vector</c>, <c>halfvec</c>, <c>sparsevec</c>.</param>
/// <param name="TypeModifier">Its type modifier, which is pgvector's dimensions, or <c>-1</c>.</param>
internal sealed record VectorColumnRow(string Schema, string Table, string Column, string TypeName, int TypeModifier);

/// <summary>
/// One routine in one of the store's schemas - a function, procedure, aggregate or window function - by
/// name and kind. Never its body: that is read one routine at a time, when a person opens it.
/// </summary>
/// <param name="Schema">The schema the routine lives in.</param>
/// <param name="Name">The routine name, without its argument list.</param>
/// <param name="IdentityArguments">
/// <c>pg_get_function_identity_arguments</c>: what tells one overload from another, and what the definition
/// read looks it up by.
/// </param>
/// <param name="Kind"><c>prokind</c>: <c>f</c>, <c>p</c>, <c>a</c> or <c>w</c>.</param>
internal sealed record FunctionStatsRow(string Schema, string Name, string IdentityArguments, string Kind);

/// <summary>One routine's body, as <c>pg_get_functiondef</c> prints it.</summary>
/// <param name="Schema">The schema the routine lives in.</param>
/// <param name="Name">The routine name.</param>
/// <param name="IdentityArguments">What tells one overload from another.</param>
/// <param name="Definition">The <c>CREATE OR REPLACE</c> statement, or <see langword="null" /> when the routine vanished mid-read.</param>
internal sealed record FunctionBodyRow(string Schema, string Name, string IdentityArguments, string? Definition);

/// <summary>
/// The catalog and statistics reads behind the Schema screen.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is scoped to the schemas the store actually owns — derived from <c>StoreOptions</c>
/// by <c>SchemaDeclarationReader</c> (never <c>AllSchemaNames()</c>, which applies migrations), passed as
/// a single <c>text[]</c> parameter and compared with <c>= any(@schemas)</c>. That is not a
/// convenience: a studio that listed every table on the server would report on the host application's
/// own tables, which is both a surprise and an information leak in a shared database. No identifier is
/// interpolated into any statement here; the schema names travel as a value like everything else
/// (AGENTS.md hard rule 4).
/// </para>
/// <para>
/// <b>Every read takes the transaction it runs in.</b> The service runs them inside
/// <c>ReadOnlySqlSession.InTransactionAsync</c>, as the database browser runs its catalog reads: a read-only
/// transaction, <c>statement_timeout</c>, a three-second <c>lock_timeout</c> and <c>SET LOCAL ROLE</c> to
/// <c>MartenStudioOptions.SqlConsoleRole</c>. The size functions open each listed table with an
/// <c>AccessShareLock</c>, which queues behind an <c>ACCESS EXCLUSIVE</c> lock on it - Marten adding a
/// tenant's partition to a parent, say - and without the lock timeout a Schema tab would sit in that queue
/// for the whole of <c>QueryTimeout</c> and hold up every session queued behind it.
/// </para>
/// <para>
/// The numbers are estimates and are labelled as such on screen. <c>reltuples</c> is a planner estimate
/// that is <c>-1</c> until the table has been analyzed, the <c>pg_stat_*</c> counters are reset by
/// <c>pg_stat_reset()</c> and by a crash, and a table that has never been scanned since a reset is
/// indistinguishable from one that was never useful. The Indexes tab says so where it matters, because a
/// "never used" verdict that is really "statistics were reset yesterday" is how a UI talks somebody into
/// dropping an index they need.
/// </para>
/// </remarks>
internal static class SchemaStatsQueries
{
    /// <summary>
    /// Sizes and activity for every ordinary and partitioned table in the store's schemas, largest first,
    /// with each partitioned table's partitions rolled into it and never listed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>relkind in ('r', 'p')</c>, not <c>= 'r'</c>: <c>'p'</c> is a partitioned table, and Marten
    /// partitions the event tables under <c>UseArchivedStreamPartitioning</c> or
    /// <c>UseTenantPartitionedEvents</c> and any document type with a <c>Partitioning</c> scheme. A
    /// <c>'r'</c>-only filter hid exactly the tables most worth looking at, and hid them silently.
    /// </para>
    /// <para>
    /// <b>A partition is never a row of its own</b> (<c>not relispartition</c>). Marten names a tenant's
    /// partition after the tenant, so a list of partitions is a list of tenants (AGENTS.md D27); what a
    /// partitioned table shows instead is one row, with its partitions' figures added in and the count of
    /// its direct partitions beside it - a count the service shows only past the database browser's gate,
    /// because a per-tenant count is the number of tenants.
    /// </para>
    /// <para>
    /// <b>Lock-free over the partitions.</b> The roll-up walks <c>pg_inherits</c> recursively and reads
    /// nothing but catalog columns (<c>relpages</c>, <c>reltuples</c>) and the statistics collector's
    /// counters, none of which opens a relation. <c>pg_partition_tree()</c> is not used: it calls
    /// <c>find_all_inheritors</c> with <c>AccessShareLock</c> and holds one lock per partition until the
    /// transaction ends - which on a table with thousands of tenant partitions exhausts the lock table - and
    /// neither is any size function over a partition, which queues behind any migration holding a lock on
    /// one. The size functions still run on the listed tables themselves, as they always have; they
    /// release their lock on the spot. The partitions' sizes are therefore <c>relpages</c> estimates, which
    /// <c>VACUUM</c> and <c>ANALYZE</c> refresh, and the tab says so. <c>SchemaAlignmentLiveTests</c>
    /// measures it: a read over a parent whose partition another session holds in
    /// <c>ACCESS EXCLUSIVE</c> mode finishes, and holds no partition lock afterwards.
    /// </para>
    /// <para>
    /// <c>pg_total_relation_size</c> of a partitioned parent is the parent's own forks only (none), so
    /// adding the partitions in double-counts nothing.
    /// </para>
    /// </remarks>
    internal const string TableStatsSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind, c.reltuples, c.reltoastrelid, c.reloptions
            from pg_catalog.pg_class c
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p')
              and not c.relispartition
              and n.nspname = any(@schemas)
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
                   pg_catalog.sum(s.idx_scan) as idx,
                   pg_catalog.sum(s.n_tup_upd) as upd,
                   pg_catalog.sum(s.n_tup_hot_upd) as hot,
                   pg_catalog.max(s.last_autovacuum) as autovacuumed,
                   pg_catalog.max(greatest(s.last_vacuum, s.last_autovacuum)) as vacuumed,
                   pg_catalog.max(greatest(s.last_analyze, s.last_autoanalyze)) as analyzed,
                   pg_catalog.count(*) filter (where d.relkind = 'r') as leaves
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
        )
        select l.nspname::text as schema_name,
               l.relname::text as table_name,
               pg_catalog.pg_total_relation_size(l.oid)
                   + coalesce(r.heap_pages + r.toast_pages + r.index_pages, 0)
                     * pg_catalog.current_setting('block_size')::bigint as total_bytes,
               pg_catalog.pg_relation_size(l.oid)
                   + coalesce(r.heap_pages, 0) * pg_catalog.current_setting('block_size')::bigint,
               pg_catalog.pg_indexes_size(l.oid)
                   + coalesce(r.index_pages, 0) * pg_catalog.current_setting('block_size')::bigint,
               case
                   when l.relkind <> 'p' then l.reltuples::bigint
                   when coalesce(r.unanalyzed, true) then -1
                   else r.tuples::bigint
               end,
               case when l.relkind = 'p' then r.live::bigint else s.n_live_tup end,
               case when l.relkind = 'p' then r.dead::bigint else s.n_dead_tup end,
               case when l.relkind = 'p' then r.seq::bigint else s.seq_scan end,
               case when l.relkind = 'p' then r.idx::bigint else s.idx_scan end,
               greatest(s.last_vacuum, s.last_autovacuum, r.vacuumed),
               greatest(s.last_analyze, s.last_autoanalyze, r.analyzed),
               l.relkind::text,
               (select pg_catalog.count(*)
                  from pg_catalog.pg_inherits i
                  join pg_catalog.pg_class ch on ch.oid = i.inhrelid
                  where i.inhparent = l.oid and ch.relispartition)::int,
               case when l.relkind = 'p' then r.upd::bigint else s.n_tup_upd end,
               case when l.relkind = 'p' then r.hot::bigint else s.n_tup_hot_upd end,
               greatest(s.last_autovacuum, r.autovacuumed),
               coalesce(pg_catalog.pg_total_relation_size(nullif(l.reltoastrelid, 0::pg_catalog.oid)), 0)
                   + coalesce(r.toast_pages, 0) * pg_catalog.current_setting('block_size')::bigint,
               case
                   when l.relkind = 'p' then coalesce(orl.agreed, '{}'::text[])
                   else coalesce(l.reloptions, '{}'::text[])
               end,
               coalesce(orl.differing, '{}'::text[])
        from listed l
        left join rolled r on r.root = l.oid
        left join options_rolled orl on orl.root = l.oid
        left join pg_catalog.pg_stat_user_tables s on s.relid = l.oid
        order by total_bytes desc, schema_name, table_name
        """;

    /// <summary>
    /// Every index in the store's schemas, with its definition, its size and its recorded use - never an
    /// index on a partition, whose figures are added into its partitioned parent index's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An index Postgres builds on a partition is named after the partition, and a Marten partition is
    /// named after its tenant; listing them listed the tenants, and called every one of them "not on a
    /// Marten table" besides. So an index on a partition (<c>t.relispartition</c>) is left out, and a
    /// partitioned index (<c>relkind 'I'</c>, which has no storage of its own) carries its descendants'
    /// pages and scan counters instead, walked through <c>pg_inherits</c> like the tables - lock-free, for
    /// the reasons <see cref="TableStatsSql" /> gives.
    /// </para>
    /// </remarks>
    internal const string IndexStatsSql =
        """
        with recursive listed as (
            select c.oid,
                   n.nspname,
                   t.relname as table_name,
                   c.relname as index_name,
                   c.relkind,
                   i.indisprimary,
                   i.indisunique,
                   i.indnkeyatts,
                   i.indclass,
                   c.reloptions,
                   am.oid as am_oid,
                   am.amname
            from pg_catalog.pg_index i
            join pg_catalog.pg_class c on c.oid = i.indexrelid
            join pg_catalog.pg_class t on t.oid = i.indrelid
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            join pg_catalog.pg_am am on am.oid = c.relam
            where n.nspname = any(@schemas)
              and not t.relispartition
        ),
        tree (root, relid) as (
            select inh.inhparent, inh.inhrelid
            from pg_catalog.pg_inherits inh
            join listed l on l.oid = inh.inhparent
            where l.relkind = 'I'
            union all
            select tr.root, inh.inhrelid
            from tree tr
            join pg_catalog.pg_inherits inh on inh.inhparent = tr.relid
        ),
        rolled as (
            select tr.root,
                   pg_catalog.sum(d.relpages::bigint) as pages,
                   pg_catalog.sum(s.idx_scan) as scans,
                   pg_catalog.sum(s.idx_tup_read) as tuples
            from tree tr
            join pg_catalog.pg_class d on d.oid = tr.relid
            left join pg_catalog.pg_stat_user_indexes s on s.indexrelid = d.oid
            group by tr.root
        )
        select l.nspname::text,
               l.table_name::text,
               l.index_name::text,
               pg_catalog.pg_get_indexdef(l.oid),
               pg_catalog.pg_relation_size(l.oid)
                   + coalesce(r.pages, 0) * pg_catalog.current_setting('block_size')::bigint,
               case when l.relkind = 'I' then r.scans::bigint else s.idx_scan end,
               case when l.relkind = 'I' then r.tuples::bigint else s.idx_tup_read end,
               l.indisprimary,
               l.indisunique,
               l.amname::text,
               exists (
                   select 1
                   from pg_catalog.pg_depend d
                   join pg_catalog.pg_extension e on e.oid = d.refobjid
                   where d.classid = 'pg_catalog.pg_am'::pg_catalog.regclass
                     and d.objid = l.am_oid
                     and d.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass
                     and d.deptype = 'e'
                     and e.extname = 'vector'),
               case when l.amname in ('hnsw', 'ivfflat') then
                   (select pg_catalog.array_agg(oc.opcname::text order by u.ord)
                      from pg_catalog.unnest(l.indclass::pg_catalog.oid[]) with ordinality as u(opclass, ord)
                      join pg_catalog.pg_opclass oc on oc.oid = u.opclass
                      where u.ord <= l.indnkeyatts)
               end,
               case when l.amname in ('hnsw', 'ivfflat') then
                   (select pg_catalog.array_agg(pg_catalog.pg_get_indexdef(l.oid, k, true) order by k)
                      from pg_catalog.generate_series(1, l.indnkeyatts::int) as k)
               end,
               case when l.amname in ('hnsw', 'ivfflat') then l.reloptions end
        from listed l
        left join rolled r on r.root = l.oid
        left join pg_catalog.pg_stat_user_indexes s on s.indexrelid = l.oid
        order by l.nspname, l.table_name, l.index_name
        """;

    /// <summary>
    /// Every pgvector column - <c>vector</c>, <c>halfvec</c>, <c>sparsevec</c>, or a domain over one - of every
    /// ordinary and partitioned table in the store's schemas, never a partition's, with its type modifier, which is
    /// the dimensions the column declares.
    /// </summary>
    /// <remarks>
    /// A type is pgvector's when the <c>vector</c> extension owns it (<c>pg_depend.deptype 'e'</c>), whatever schema
    /// the extension was created in and whatever the type is called - so a host's own type named <c>vector</c> is not
    /// one. Catalog columns only: no value is read to learn a dimension, and nothing here locks what it lists.
    /// </remarks>
    internal const string VectorColumnsSql =
        """
        select n.nspname::text,
               c.relname::text,
               a.attname::text,
               bt.typname::text,
               case when t.typtype = 'd' then t.typtypmod else a.atttypmod end
        from pg_catalog.pg_attribute a
        join pg_catalog.pg_class c on c.oid = a.attrelid
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        join pg_catalog.pg_type t on t.oid = a.atttypid
        join pg_catalog.pg_type bt on bt.oid = case when t.typtype = 'd' then t.typbasetype else t.oid end
        where n.nspname = any(@schemas)
          and c.relkind in ('r', 'p')
          and not c.relispartition
          and a.attnum > 0
          and not a.attisdropped
          and exists (
              select 1
              from pg_catalog.pg_depend d
              join pg_catalog.pg_extension e on e.oid = d.refobjid
              where d.classid = 'pg_catalog.pg_type'::pg_catalog.regclass
                and d.objid = bt.oid
                and d.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass
                and d.deptype = 'e'
                and e.extname = 'vector')
        order by 1, 2, a.attnum
        """;

    /// <summary>
    /// Every function, procedure, aggregate and window function in the store's schemas - names and kinds,
    /// never bodies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to read <c>pg_get_functiondef</c> for every row, filtered to <c>prokind = 'f'</c> on the
    /// belief that it raises <c>42809</c> for a procedure and a window function as well as an aggregate.
    /// It raises only for an aggregate (measured on PG 17 by DB-1): a procedure's and a window function's
    /// bodies print. The bodies are no longer read here at all. A body is read one routine at a time, when
    /// a person opens it, through the database browser's definition read - which is where the gate lives:
    /// Marten's own routines in the store's own schemas for everybody, anything else only past
    /// <c>Capabilities.BrowseDatabase</c>, the write policy and <c>BrowsableSchemas</c>, and never an
    /// aggregate's. So every kind is listed, and each says what it is.
    /// </para>
    /// <para>
    /// A routine an extension owns (<c>pg_depend.deptype 'e'</c>) or that is an internal part of another
    /// object (<c>'i'</c>, the constructors Postgres writes for a range type) is left out, as the database
    /// browser leaves it out: it is not the host's code and not Marten's, and the definition read would not
    /// find it.
    /// </para>
    /// </remarks>
    internal const string FunctionsSql =
        """
        select n.nspname::text,
               p.proname::text,
               pg_catalog.pg_get_function_identity_arguments(p.oid),
               p.prokind::text
        from pg_catalog.pg_proc p
        join pg_catalog.pg_namespace n on n.oid = p.pronamespace
        where n.nspname = any(@schemas)
          and p.prokind in ('f', 'p', 'a', 'w')
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                and d.objid = p.oid
                and d.deptype in ('e', 'i'))
        order by n.nspname, p.proname, 3
        """;

    /// <summary>
    /// The bodies of the named routines in the store's schemas - asked for only while the database browser,
    /// which otherwise reads a body one routine at a time, cannot answer at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The database browser fails closed when a registered Marten store will not build: it cannot tell that
    /// store's objects from anybody else's, so it reads nothing, and the Functions tab's bodies - read through
    /// it - went with it, Marten's own included, although they are Marten's by their <c>mt_</c> name whoever
    /// owns them and the Schema screen always showed them. So in that state, and only then, the service asks
    /// for Marten's own bodies in the store's own schemas here, with the list, by name - the caller passes
    /// only names it has already classified as Marten's - and matches each back to its overload.
    /// </para>
    /// <para>
    /// <c>pg_get_functiondef</c> reads the system cache and locks nothing. It prints a type or name that the
    /// <c>search_path</c> would not find with its schema, so the caller pins the path to <c>pg_catalog</c>
    /// first (as every catalog read of the browser's does) and a withheld schema can be masked. An aggregate
    /// is never asked for: <c>pg_get_functiondef</c> refuses one (<c>42809</c>).
    /// </para>
    /// </remarks>
    internal const string FunctionBodiesSql =
        """
        select n.nspname::text,
               p.proname::text,
               pg_catalog.pg_get_function_identity_arguments(p.oid),
               pg_catalog.pg_get_functiondef(p.oid)
        from pg_catalog.pg_proc p
        join pg_catalog.pg_namespace n on n.oid = p.pronamespace
        where n.nspname = any(@schemas)
          and p.proname::text = any(@names)
          and p.prokind in ('f', 'p', 'w')
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                and d.objid = p.oid
                and d.deptype in ('e', 'i'))
        order by n.nspname, p.proname, 3
        """;

    /// <summary>The size of the database the connection is open against.</summary>
    internal const string DatabaseSizeSql = "select pg_catalog.pg_database_size(pg_catalog.current_database())";

    /// <summary>Every statement this class runs, for the tests that hold them to their rules.</summary>
    internal static IReadOnlyList<string> AllStatements { get; } =
        [TableStatsSql, IndexStatsSql, FunctionsSql, FunctionBodiesSql, DatabaseSizeSql, VectorColumnsSql];

    /// <summary>Reads table sizes and activity for the store's schemas.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="transaction">The read-only transaction to run in, or <see langword="null" /> for none.</param>
    /// <param name="schemas">The schemas the store owns, derived from its options.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<TableStatsRow>> ReadTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = CreateCommand(connection, transaction, TableStatsSql, schemas, commandTimeoutSeconds);

        List<TableStatsRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TableStatsRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                NullableInt64(reader, 6),
                NullableInt64(reader, 7),
                NullableInt64(reader, 8),
                NullableInt64(reader, 9),
                NullableTimestamp(reader, 10),
                NullableTimestamp(reader, 11),
                reader.GetString(12),
                reader.GetInt32(13),
                NullableInt64(reader, 14),
                NullableInt64(reader, 15),
                NullableTimestamp(reader, 16),
                reader.GetInt64(17),
                reader.IsDBNull(18) ? [] : reader.GetFieldValue<string[]>(18),
                reader.IsDBNull(19) ? [] : reader.GetFieldValue<string[]>(19)));
        }

        return rows;
    }

    /// <summary>Reads every index in the store's schemas, with its size and its recorded use.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="transaction">The read-only transaction to run in, or <see langword="null" /> for none.</param>
    /// <param name="schemas">The schemas the store owns.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<IndexStatsRow>> ReadIndexesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = CreateCommand(connection, transaction, IndexStatsSql, schemas, commandTimeoutSeconds);

        List<IndexStatsRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new IndexStatsRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.GetInt64(4),
                NullableInt64(reader, 5),
                NullableInt64(reader, 6),
                reader.GetBoolean(7),
                reader.GetBoolean(8))
            {
                AccessMethod = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                AccessMethodIsPgVector = !reader.IsDBNull(10) && reader.GetBoolean(10),
                OperatorClasses = reader.IsDBNull(11) ? null : reader.GetFieldValue<string?[]>(11),
                KeyDefinitions = reader.IsDBNull(12) ? null : reader.GetFieldValue<string?[]>(12),
                Options = reader.IsDBNull(13) ? null : reader.GetFieldValue<string[]>(13),
            });
        }

        return rows;
    }

    /// <summary>Reads every pgvector column of every table in the store's schemas (<see cref="VectorColumnsSql" />).</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="transaction">The read-only transaction to run in, or <see langword="null" /> for none.</param>
    /// <param name="schemas">The schemas the store owns.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<VectorColumnRow>> ReadVectorColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = CreateCommand(connection, transaction, VectorColumnsSql, schemas, commandTimeoutSeconds);

        List<VectorColumnRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new VectorColumnRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? -1 : reader.GetInt32(4)));
        }

        return rows;
    }

    /// <summary>Reads the routines Marten (or anyone else) put into the store's schemas, without their bodies.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="transaction">The read-only transaction to run in, or <see langword="null" /> for none.</param>
    /// <param name="schemas">The schemas the store owns.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<FunctionStatsRow>> ReadFunctionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = CreateCommand(connection, transaction, FunctionsSql, schemas, commandTimeoutSeconds);

        List<FunctionStatsRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new FunctionStatsRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>Reads the bodies of the named routines in the store's schemas (see <see cref="FunctionBodiesSql" />).</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="transaction">The read-only transaction to run in, its <c>search_path</c> already pinned.</param>
    /// <param name="schemas">The schemas the store owns.</param>
    /// <param name="names">The routine names to read - only ones the caller has classified as Marten's.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<FunctionBodyRow>> ReadFunctionBodiesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<string> schemas,
        IReadOnlyList<string> names,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (names.Count == 0 || schemas.Count == 0)
        {
            return [];
        }

        await using NpgsqlCommand command = CreateCommand(connection, transaction, FunctionBodiesSql, schemas, commandTimeoutSeconds);

        command.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = names as string[] ?? [.. names],
        });

        List<FunctionBodyRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new FunctionBodyRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    /// <summary>The size of the whole database, for the Tables tab's footer.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="transaction">The read-only transaction to run in, or <see langword="null" /> for none.</param>
    /// <param name="commandTimeoutSeconds">The command timeout, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<long> ReadDatabaseSizeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(DatabaseSizeSql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long size ? size : 0L;
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);

        var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = schemas as string[] ?? [.. schemas],
        });

        return command;
    }

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
