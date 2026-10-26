using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>What <see cref="StorageQueries.Database" /> reads of the database as a whole.</summary>
/// <param name="DatabaseBytes">
/// <c>pg_database_size(current_database())</c>, or <see langword="null" /> when the reading role may not ask it - it needs
/// <c>CONNECT</c> on the database or the privileges of <c>pg_read_all_stats</c>, and the read asks first rather than fail.
/// </param>
/// <param name="EstimatedBytes">Every relation's <c>relpages</c>, summed, times the block size: B0 measured it at 90 % of the real size.</param>
/// <param name="BlockSize"><c>block_size</c>.</param>
/// <param name="ServerVersion"><c>server_version_num</c>.</param>
internal sealed record StorageDatabaseRow(long? DatabaseBytes, long EstimatedBytes, int BlockSize, int ServerVersion);

/// <summary>One judged table as <see cref="StorageQueries.Tables" /> read it, its partitions rolled in.</summary>
/// <remarks>
/// Init properties rather than a positional record, so a unit test states only the facts its case is about. A counter is
/// <see langword="null" /> when the statistics collector has nothing for the table or any of its partitions.
/// </remarks>
internal sealed record StorageTableRow
{
    /// <summary>The schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The table.</summary>
    public required string Table { get; init; }

    /// <summary><c>relkind</c>: <c>r</c>, or <c>p</c> for a partitioned table whose partitions are rolled in.</summary>
    public string Kind { get; init; } = "r";

    /// <summary>The main fork, from <c>relpages</c>.</summary>
    public long HeapBytes { get; init; }

    /// <summary>Every index on it, from their <c>relpages</c>.</summary>
    public long IndexBytes { get; init; }

    /// <summary>The TOAST table and its index, from their <c>relpages</c>.</summary>
    public long ToastBytes { get; init; }

    /// <summary><c>reltuples</c>, summed over the leaves of a partitioned table; <c>-1</c> before the first analyze.</summary>
    public long EstimatedRows { get; init; } = -1;

    /// <summary><c>n_tup_ins</c>.</summary>
    public long? Inserts { get; init; }

    /// <summary><c>n_tup_del</c>.</summary>
    public long? Deletes { get; init; }

    /// <summary><c>n_tup_upd</c>, HOT updates included.</summary>
    public long? Updates { get; init; }

    /// <summary><c>n_tup_hot_upd</c>.</summary>
    public long? HotUpdates { get; init; }

    /// <summary><c>n_tup_newpage_upd</c> (PostgreSQL 16+): the updates that went to another page; null below 16.</summary>
    public long? NewPageUpdates { get; init; }
}

/// <summary>
/// The statements behind the Storage page's facts (W5-CAP, issue #8 G3): the database's size, and each judged table's
/// heap, indexes and TOAST from the catalog's page counts, with the counters the growth estimate and the HOT ratio read.
/// </summary>
/// <remarks>
/// <para>
/// <b>A list locks nothing it lists</b> (AGENTS.md D27). No size function is called over a table: B0 measured that
/// <c>pg_relation_size</c>, <c>pg_total_relation_size</c>, <c>pg_indexes_size</c> and <c>pg_table_size</c> take a transient
/// lock and wait behind a migration's <c>ACCESS EXCLUSIVE</c>, though <c>pg_locks</c> shows nothing afterwards. Sizes are
/// <c>relpages × block_size</c>: the heap's own, every index's through <c>pg_index</c>, and the TOAST table's and its index's
/// through <c>reltoastrelid</c> - as stale as the last <c>VACUUM</c> or <c>ANALYZE</c> (B0: TOAST 1.3 % low). Nothing is
/// deparsed either, so nothing opens a table and there is no <c>pg_locks</c> guard to take. Partitions are walked through
/// <c>pg_inherits</c>, never <c>pg_partition_tree()</c>. <c>StorageQueriesTests</c> holds both statements to it, and the live
/// no-lock test measures it behind a held lock.
/// </para>
/// <para>
/// <b><c>pg_database_size</c> is the one size function</b>, and it takes no relation lock (B0 G3, measured while a table was
/// <c>ACCESS EXCLUSIVE</c>): it adds up the database directory's files. It needs <c>CONNECT</c> on the database or the
/// privileges of <c>pg_read_all_stats</c>, so the statement asks <c>has_database_privilege</c> and <c>pg_has_role</c> first
/// and answers null rather than fail with <c>42501</c> - the reading role is <c>SqlConsoleRole</c> when one is set, which a
/// host may have left without <c>CONNECT</c>.
/// </para>
/// <para>
/// <b>Statistics by the row's own oid.</b> <c>pg_stat_user_tables</c> is joined on <c>relid</c>, the <c>pg_class</c> row found by
/// schema and table name - never through a <c>'schema.table'::regclass</c> cast, which needs <c>USAGE</c> on the schema
/// where the view itself does not (B0 G3). <c>n_tup_newpage_upd</c> is PostgreSQL 16's, so it is read by name
/// (<c>to_jsonb(s) -&gt;&gt; 'n_tup_newpage_upd'</c>), null below 16, and one statement runs on every version.
/// </para>
/// <para>
/// <b>Names are values.</b> The tables come from the studio's table source and travel as two <c>text[]</c> parameters, as
/// <see cref="HealthTableQueries" />' do, so "Show SQL" names no table.
/// </para>
/// </remarks>
internal static class StorageQueries
{
    /// <summary>The database as a whole (<see cref="StorageDatabaseRow" />).</summary>
    internal const string DatabaseSql =
        """
        select case when pg_catalog.has_database_privilege(pg_catalog.current_database(), 'CONNECT')
                      or pg_catalog.pg_has_role('pg_read_all_stats', 'USAGE')
                    then pg_catalog.pg_database_size(pg_catalog.current_database()) end,
               (select coalesce(pg_catalog.sum(c.relpages::bigint), 0) from pg_catalog.pg_class c)
                   * pg_catalog.current_setting('block_size')::bigint,
               pg_catalog.current_setting('block_size')::int,
               pg_catalog.current_setting('server_version_num')::int
        """;

    /// <summary>Each listed table, its partitions rolled in (<see cref="StorageTableRow" />).</summary>
    internal const string TablesSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        tree (root, relid) as (
            select l.oid, l.oid
            from listed l
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
                   pg_catalog.sum((select coalesce(pg_catalog.sum(ic.relpages::bigint), 0)
                                   from pg_catalog.pg_index x
                                   join pg_catalog.pg_class ic on ic.oid = x.indexrelid
                                   where x.indrelid = d.oid)) as index_pages,
                   pg_catalog.sum(coalesce(toast.relpages, 0)::bigint
                                  + (select coalesce(pg_catalog.sum(ti.relpages::bigint), 0)
                                     from pg_catalog.pg_index tx
                                     join pg_catalog.pg_class ti on ti.oid = tx.indexrelid
                                     where tx.indrelid = d.reltoastrelid)) as toast_pages,
                   pg_catalog.bool_and(d.reltuples < 0) filter (where d.relkind = 'r') as unanalysed,
                   pg_catalog.sum(greatest(d.reltuples, 0)) filter (where d.relkind = 'r') as tuples,
                   pg_catalog.sum(s.n_tup_ins) as ins,
                   pg_catalog.sum(s.n_tup_del) as del,
                   pg_catalog.sum(s.n_tup_upd) as upd,
                   pg_catalog.sum(s.n_tup_hot_upd) as hot,
                   pg_catalog.sum((pg_catalog.to_jsonb(s) ->> 'n_tup_newpage_upd')::bigint) as newpage
            from tree t
            join pg_catalog.pg_class d on d.oid = t.relid
            left join pg_catalog.pg_class toast on toast.oid = d.reltoastrelid
            left join pg_catalog.pg_stat_user_tables s on s.relid = d.oid
            group by t.root
        )
        select l.nspname::text,
               l.relname::text,
               l.relkind::text,
               coalesce(r.heap_pages, 0) * b.block_size,
               coalesce(r.index_pages, 0) * b.block_size,
               coalesce(r.toast_pages, 0) * b.block_size,
               case when coalesce(r.unanalysed, true) then -1 else r.tuples::bigint end,
               r.ins::bigint,
               r.del::bigint,
               r.upd::bigint,
               r.hot::bigint,
               r.newpage::bigint
        from listed l
        left join rolled r on r.root = l.oid
        cross join (select pg_catalog.current_setting('block_size')::bigint as block_size) b
        order by 1, 2
        """;

    /// <summary>Both statements, for the tests that hold them to D27.</summary>
    internal static IReadOnlyList<string> Statements { get; } = [DatabaseSql, TablesSql];

    /// <summary>The database as a whole (<see cref="DatabaseSql" />).</summary>
    public static HealthQuery Database() => new(DatabaseSql);

    /// <summary>The tables of <paramref name="tables" />, their partitions rolled in (<see cref="TablesSql" />).</summary>
    /// <param name="tables">The judged tables, as the table source listed them.</param>
    public static HealthQuery Tables(IReadOnlyList<HealthTableTarget> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        string[] schemas = new string[tables.Count];
        string[] names = new string[tables.Count];

        for (int i = 0; i < tables.Count; i++)
        {
            schemas[i] = tables[i].Schema;
            names[i] = tables[i].Table;
        }

        return new HealthQuery(
            TablesSql,
            [
                new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas },
                new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names },
            ]);
    }

    /// <summary>Maps the one row of <see cref="DatabaseSql" />.</summary>
    public static StorageDatabaseRow ReadDatabase(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new StorageDatabaseRow(
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    /// <summary>Maps one row of <see cref="TablesSql" />.</summary>
    public static StorageTableRow ReadTable(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new StorageTableRow
        {
            Schema = reader.GetString(0),
            Table = reader.GetString(1),
            Kind = reader.GetString(2),
            HeapBytes = reader.GetInt64(3),
            IndexBytes = reader.GetInt64(4),
            ToastBytes = reader.GetInt64(5),
            EstimatedRows = reader.GetInt64(6),
            Inserts = Nullable(reader, 7),
            Deletes = Nullable(reader, 8),
            Updates = Nullable(reader, 9),
            HotUpdates = Nullable(reader, 10),
            NewPageUpdates = Nullable(reader, 11),
        };
    }

    private static long? Nullable(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
