using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// How one listed table's rows spread over the values of one column (<see cref="ColumnSkewQueries.SkewSql" />): from the
/// planner's statistics, or - for a table list-partitioned on that column - from each partition's estimated rows (T18, X4).
/// </summary>
/// <remarks>
/// Init properties, so a unit test states only what its case is about. A value is data - a tenant's id, where the column is
/// <c>tenant_id</c> - and only the two largest come back; never a partition's name.
/// </remarks>
internal sealed record HealthColumnSkewRow
{
    /// <summary>The schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The table.</summary>
    public required string Table { get; init; }

    /// <summary>
    /// Whether the table is list-partitioned on the column, so each partition is a value (or a few) and its rows come from the
    /// catalog - no statistics, and no privilege on the column, needed.
    /// </summary>
    public bool ByPartition { get; init; }

    /// <summary>Its estimated rows: every leaf's greater of <c>reltuples</c> and <c>n_live_tup</c>, summed.</summary>
    public double Rows { get; init; }

    /// <summary>How many leaves it has: itself, when it is not partitioned.</summary>
    public int Leaves { get; init; }

    /// <summary>
    /// How many of its leaves the reading role may read the column of - which <c>pg_stats</c> needs to show anything (B0 §8c).
    /// Counted for the statistics path only; 0 for <see cref="ByPartition" />.
    /// </summary>
    public int ReadableLeaves { get; init; }

    /// <summary>
    /// The rows the shares are over: the leaves <c>pg_stats</c> has a row for, or - by partition - every value partition, the
    /// <c>DEFAULT</c> partition's rows apart.
    /// </summary>
    public double RowsCounted { get; init; }

    /// <summary>
    /// How many values there are: by partition, the value partitions; otherwise the larger of the common values the statistics
    /// list and a leaf's <c>n_distinct</c>.
    /// </summary>
    public double Values { get; init; }

    /// <summary>
    /// The largest values, most rows first, at most two: the value itself from the statistics, or - by partition - the
    /// partition's bound text (<c>FOR VALUES IN ('acme')</c>), which <see cref="PartitionBoundText.SingleListValue" /> reads.
    /// </summary>
    public IReadOnlyList<string> TopValues { get; init; } = [];

    /// <summary>The rows of each of <see cref="TopValues" />, in the same order.</summary>
    public IReadOnlyList<double> TopRows { get; init; } = [];

    /// <summary>By partition, the <c>DEFAULT</c> partition's rows - values that have no partition of their own; otherwise 0.</summary>
    public double DefaultRows { get; init; }

    /// <summary>The table's key.</summary>
    public string Key => Schema + "." + Table;
}

/// <summary>
/// One lock-free statement that says how the listed tables' rows spread over one column's values - T18's tenant skew, over
/// <c>tenant_id</c> (X4). Plain Postgres, no store: MartenStudio decides which tables and which column.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two paths, one per table.</b> A table list-partitioned on the column (Marten's per-tenant partitioning) is judged by its
/// partitions: each one's estimated rows, its sub-partitions' summed - the catalog alone, readable by any role that may see the
/// table. Every other table is judged by <c>pg_stats</c>: each leaf's most common values and their frequencies, weighted by
/// the leaf's rows (a partitioned parent's own statistics are absent unless somebody analysed it, B0 §8c). <c>pg_stats</c>
/// shows a column only to a role that may <c>SELECT</c> it - never to <c>pg_monitor</c> - so whether the role may is read
/// beside it, per leaf.
/// </para>
/// <para>
/// <b>A list locks nothing it lists</b> (D27). The hierarchy through <c>pg_inherits</c>, never <c>pg_partition_tree</c>; rows
/// from <c>reltuples</c> and <c>pg_stat_get_live_tuples</c>; the two largest partitions' bounds through
/// <c>pg_get_expr(relpartbound, 0)</c>, which opens nothing. A tenant-partitioned table's thousands of partitions are summed in
/// the statement, and only the two largest bounds come back: their names, which are the tenant list, never do.
/// </para>
/// </remarks>
internal static class ColumnSkewQueries
{
    /// <summary>How each listed table's rows spread over <c>@column</c>'s values.</summary>
    internal const string SkewSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        by_value as (
            select l.oid, p.partdefid
            from listed l
            join pg_catalog.pg_partitioned_table p on p.partrelid = l.oid
            join pg_catalog.pg_attribute a on a.attrelid = l.oid and a.attnum = p.partattrs[0]
            where p.partstrat = 'l'
              and p.partnatts = 1
              and a.attname = @column
        ),
        tree (root, top, relid, relkind) as (
            select l.oid, l.oid, l.oid, l.relkind
            from listed l
            union all
            select t.root, case when t.relid = t.root then ch.oid else t.top end, ch.oid, ch.relkind
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where t.relkind = 'p' and ch.relispartition
        ),
        leaves as (
            select t.root, t.top, c.oid, c.relname, ln.nspname,
                   greatest(c.reltuples::float8, 0, coalesce(pg_catalog.pg_stat_get_live_tuples(c.oid), 0)::float8) as tuples
            from tree t
            join pg_catalog.pg_class c on c.oid = t.relid
            join pg_catalog.pg_namespace ln on ln.oid = c.relnamespace
            where t.relkind = 'r'
        ),
        partitioned as (
            select lf.root, lf.top, pg_catalog.sum(lf.tuples)::float8 as tuples, lf.top = b.partdefid as is_default
            from leaves lf
            join by_value b on b.oid = lf.root
            group by lf.root, lf.top, b.partdefid
        ),
        sampled as (
            select lf.root, lf.tuples,
                   coalesce(pg_catalog.has_column_privilege(lf.oid, a.attnum, 'SELECT'), false) as readable,
                   s.n_distinct,
                   s.most_common_vals::text::text[] as vals,
                   s.most_common_freqs as freqs
            from leaves lf
            left join pg_catalog.pg_attribute a
              on a.attrelid = lf.oid and a.attname = @column and a.attnum > 0 and not a.attisdropped
            left join pg_catalog.pg_stats s
              on s.schemaname = lf.nspname and s.tablename = lf.relname and s.attname = @column and not s.inherited
            where not exists (select 1 from by_value b where b.oid = lf.root)
        ),
        valued as (
            select sa.root, v.value, pg_catalog.sum(sa.tuples * v.freq)::float8 as tuples
            from sampled sa
            cross join lateral pg_catalog.unnest(sa.vals, sa.freqs) as v(value, freq)
            group by sa.root, v.value
        )
        select l.nspname::text,
               l.relname::text,
               b.oid is not null,
               coalesce((select pg_catalog.sum(lf.tuples) from leaves lf where lf.root = l.oid), 0)::float8,
               (select pg_catalog.count(*) from leaves lf where lf.root = l.oid)::int,
               (select pg_catalog.count(*) from sampled sa where sa.root = l.oid and sa.readable)::int,
               (case when b.oid is not null
                     then coalesce((select pg_catalog.sum(p.tuples) from partitioned p where p.root = l.oid and not p.is_default), 0)
                     else coalesce((select pg_catalog.sum(sa.tuples) from sampled sa where sa.root = l.oid and sa.vals is not null), 0)
                end)::float8,
               (case when b.oid is not null
                     then (select pg_catalog.count(*) from partitioned p where p.root = l.oid and not p.is_default)::float8
                     else greatest(
                         (select pg_catalog.count(*) from valued v where v.root = l.oid)::float8,
                         coalesce((select pg_catalog.max(case when sa.n_distinct >= 0 then sa.n_distinct else -sa.n_distinct * sa.tuples end)
                                   from sampled sa
                                   where sa.root = l.oid), 0)::float8)
                end)::float8,
               case when b.oid is not null
                    then (select pg_catalog.array_agg(pg_catalog.pg_get_expr(c.relpartbound, 0) order by x.tuples desc, x.top)
                          from (select p.top, p.tuples
                                from partitioned p
                                where p.root = l.oid and not p.is_default
                                order by p.tuples desc, p.top
                                limit 2) x
                          join pg_catalog.pg_class c on c.oid = x.top)
                    else (select pg_catalog.array_agg(x.value order by x.tuples desc, x.value)
                          from (select v.value, v.tuples
                                from valued v
                                where v.root = l.oid
                                order by v.tuples desc, v.value
                                limit 2) x)
               end,
               case when b.oid is not null
                    then (select pg_catalog.array_agg(x.tuples order by x.tuples desc, x.top)
                          from (select p.top, p.tuples
                                from partitioned p
                                where p.root = l.oid and not p.is_default
                                order by p.tuples desc, p.top
                                limit 2) x)
                    else (select pg_catalog.array_agg(x.tuples order by x.tuples desc, x.value)
                          from (select v.value, v.tuples
                                from valued v
                                where v.root = l.oid
                                order by v.tuples desc, v.value
                                limit 2) x)
               end,
               coalesce((select p.tuples from partitioned p where p.root = l.oid and p.is_default), 0)::float8
        from listed l
        left join by_value b on b.oid = l.oid
        order by 1, 2
        """;

    /// <summary>Every list statement here, for the tests that hold them to D27.</summary>
    internal static IReadOnlyList<string> ListStatements { get; } = [SkewSql];

    /// <summary>How <paramref name="tables" />' rows spread over <paramref name="column" />'s values (<see cref="SkewSql" />).</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="column">The column, by its catalog name: a value, never text in the statement.</param>
    public static HealthQuery Skew(IReadOnlyList<HealthTableTarget> tables, string column)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        string[] schemas = new string[tables.Count];
        string[] names = new string[tables.Count];

        for (int i = 0; i < tables.Count; i++)
        {
            schemas[i] = tables[i].Schema;
            names[i] = tables[i].Table;
        }

        return new HealthQuery(
            SkewSql,
            [
                new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas },
                new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names },
                new NpgsqlParameter("column", NpgsqlDbType.Text) { Value = column },
            ]);
    }

    /// <summary>Maps one row of <see cref="SkewSql" />.</summary>
    public static HealthColumnSkewRow ReadSkew(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthColumnSkewRow
        {
            Schema = reader.GetString(0),
            Table = reader.GetString(1),
            ByPartition = reader.GetBoolean(2),
            Rows = reader.GetDouble(3),
            Leaves = reader.GetInt32(4),
            ReadableLeaves = reader.GetInt32(5),
            RowsCounted = reader.GetDouble(6),
            Values = reader.GetDouble(7),
            TopValues = reader.IsDBNull(8) ? [] : reader.GetFieldValue<string[]>(8),
            TopRows = reader.IsDBNull(9) ? [] : reader.GetFieldValue<double[]>(9),
            DefaultRows = reader.GetDouble(10),
        };
    }
}
