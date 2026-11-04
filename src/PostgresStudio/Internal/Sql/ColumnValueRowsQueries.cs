using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// One top-level partition of a list-partitioned table whose bound is one of the bounds the caller built, or the fact that the table
/// is partitioned on the column and has none (<see cref="ColumnValueRowsQueries.PartitionRowsSql" />).
/// </summary>
/// <param name="Schema">The partitioned parent's schema.</param>
/// <param name="Table">The partitioned parent.</param>
/// <param name="Bound">
/// What <c>pg_get_expr(relpartbound, 0)</c> returned: always one of the caller's own bound texts - or <see langword="null" /> when the
/// table is partitioned on the column and none of the bounds asked about is one of its partitions (the row says only that the table
/// is partitioned on it).
/// </param>
/// <param name="Tuples">The partition's estimated rows: its leaves' greater of <c>reltuples</c> and live tuples, summed.</param>
internal sealed record ColumnPartitionRow(string Schema, string Table, string? Bound, double? Tuples);

/// <summary>
/// What the planner's statistics say of one value of a column in one table that is not list-partitioned on it
/// (<see cref="ColumnValueRowsQueries.StatisticsRowsSql" />).
/// </summary>
/// <param name="Schema">The table's schema.</param>
/// <param name="Table">The table.</param>
/// <param name="Value">The value the caller asked about - never another.</param>
/// <param name="Tuples">The rows of that value the statistics single out, weighted by each leaf's rows; 0 when they single none out.</param>
/// <param name="ReadableLeaves">How many leaves the reading role may read the column of, which <c>pg_stats</c> needs to show anything.</param>
/// <param name="Leaves">How many leaves the table has.</param>
/// <param name="HasStatistics">Whether any leaf has a <c>pg_stats</c> row for the column: <see langword="false" /> is "never analysed".</param>
internal sealed record ColumnStatisticsRow(
    string Schema,
    string Table,
    string Value,
    double Tuples,
    int ReadableLeaves,
    int Leaves,
    bool HasStatistics);

/// <summary>
/// The two lock-free statements that read, for a list of values of one column, how many rows each has in each listed table
/// (1.2, the Tenants overview): by partition for a table list-partitioned on the column, and by the planner's statistics for any
/// other. Plain Postgres, no store: MartenStudio decides which tables, which column and which values.
/// </summary>
/// <remarks>
/// <para>
/// <b>They answer for the caller's values and no others.</b> A partition is returned only when its bound deparses to one of the bound
/// texts the caller built, and then as that text; a statistic only for a value the caller listed, whatever the table holds besides.
/// Neither returns a partition's name, another tenant's value or a count of partitions: a tenant-partitioned table's partitions are
/// the tenant list, and the list is the header's to give (D27).
/// </para>
/// <para>
/// <b>A list locks nothing it lists.</b> The hierarchy comes from <c>pg_inherits</c> and never <c>pg_partition_tree</c>, rows from
/// <c>reltuples</c> and <c>pg_stat_get_live_tuples</c>, bounds through <c>pg_get_expr(relpartbound, 0)</c>, which opens nothing, and
/// every built-in is <c>pg_catalog</c>-qualified. The CTEs are <see cref="ColumnSkewQueries.SkewSql" />'s.
/// </para>
/// </remarks>
internal static class ColumnValueRowsQueries
{
    /// <summary>
    /// For every listed table that is list-partitioned on <c>@column</c> alone, each of its top-level partitions whose bound is one
    /// of <c>@bounds</c> - or one row with a null bound and null tuples when none is: <c>(schema, table, bound, tuples)</c>.
    /// </summary>
    internal const string PartitionRowsSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        by_value as (
            select l.oid
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
            where l.oid in (select b.oid from by_value b)
            union all
            select t.root, case when t.relid = t.root then ch.oid else t.top end, ch.oid, ch.relkind
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where t.relkind = 'p' and ch.relispartition
        ),
        leaves as (
            select t.root, t.top,
                   greatest(c.reltuples::float8, 0, coalesce(pg_catalog.pg_stat_get_live_tuples(c.oid), 0)::float8) as tuples
            from tree t
            join pg_catalog.pg_class c on c.oid = t.relid
            where t.relkind = 'r'
        ),
        partitioned as (
            select lf.root, lf.top, pg_catalog.sum(lf.tuples)::float8 as tuples
            from leaves lf
            group by lf.root, lf.top
        )
        select l.nspname::text,
               l.relname::text,
               m.bound,
               m.tuples
        from listed l
        join by_value b on b.oid = l.oid
        left join (
            select p.root,
                   pg_catalog.pg_get_expr(c.relpartbound, 0)::text as bound,
                   p.tuples
            from partitioned p
            join pg_catalog.pg_class c on c.oid = p.top
            where p.top <> p.root
              and pg_catalog.pg_get_expr(c.relpartbound, 0) = any(@bounds)
        ) m on m.root = l.oid
        order by 1, 2, 3
        """;

    /// <summary>
    /// For every listed table that is not list-partitioned on <c>@column</c> alone, and every one of <c>@values</c>:
    /// <c>(schema, table, value, tuples, readable_leaves, leaves, has_statistics)</c>.
    /// </summary>
    internal const string StatisticsRowsSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        by_value as (
            select l.oid
            from listed l
            join pg_catalog.pg_partitioned_table p on p.partrelid = l.oid
            join pg_catalog.pg_attribute a on a.attrelid = l.oid and a.attnum = p.partattrs[0]
            where p.partstrat = 'l'
              and p.partnatts = 1
              and a.attname = @column
        ),
        plain as (
            select l.oid, l.nspname, l.relname, l.relkind
            from listed l
            where l.oid not in (select b.oid from by_value b)
        ),
        tree (root, relid, relkind) as (
            select p.oid, p.oid, p.relkind
            from plain p
            union all
            select t.root, ch.oid, ch.relkind
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where t.relkind = 'p' and ch.relispartition
        ),
        leaves as (
            select t.root, c.oid, c.relname, ln.nspname,
                   greatest(c.reltuples::float8, 0, coalesce(pg_catalog.pg_stat_get_live_tuples(c.oid), 0)::float8) as tuples
            from tree t
            join pg_catalog.pg_class c on c.oid = t.relid
            join pg_catalog.pg_namespace ln on ln.oid = c.relnamespace
            where t.relkind = 'r'
        ),
        sampled as (
            select lf.root, lf.tuples,
                   coalesce(pg_catalog.has_column_privilege(lf.oid, a.attnum, 'SELECT'), false) as readable,
                   s.attname is not null as analysed,
                   s.most_common_vals::text::text[] as vals,
                   s.most_common_freqs as freqs
            from leaves lf
            left join pg_catalog.pg_attribute a
              on a.attrelid = lf.oid and a.attname = @column and a.attnum > 0 and not a.attisdropped
            left join pg_catalog.pg_stats s
              on s.schemaname = lf.nspname and s.tablename = lf.relname and s.attname = @column and not s.inherited
        ),
        valued as (
            select sa.root, v.value, pg_catalog.sum(sa.tuples * v.freq)::float8 as tuples
            from sampled sa
            cross join lateral rows from (pg_catalog.unnest(sa.vals), pg_catalog.unnest(sa.freqs)) as v(value, freq)
            where v.value = any(@values)
            group by sa.root, v.value
        )
        select p.nspname::text,
               p.relname::text,
               w.value::text,
               coalesce(vd.tuples, 0)::float8,
               (select pg_catalog.count(*) from sampled sa where sa.root = p.oid and sa.readable)::int,
               (select pg_catalog.count(*) from leaves lf where lf.root = p.oid)::int,
               exists (select 1 from sampled sa where sa.root = p.oid and sa.analysed)
        from plain p
        cross join pg_catalog.unnest(@values) as w(value)
        left join valued vd on vd.root = p.oid and vd.value = w.value
        order by 1, 2, 3
        """;

    /// <summary>Every list statement here, for the tests that hold them to D27.</summary>
    internal static IReadOnlyList<string> ListStatements { get; } = [PartitionRowsSql, StatisticsRowsSql];

    /// <summary>The partitions of <paramref name="tables" /> whose bound is one of <paramref name="bounds" /> (<see cref="PartitionRowsSql" />).</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="column">The column, by its catalog name: a value, never text in the statement.</param>
    /// <param name="bounds">The bound texts to match, each built by the caller as Postgres writes a one-value list bound.</param>
    public static HealthQuery PartitionRows(IReadOnlyList<HealthTableTarget> tables, string column, IReadOnlyList<string> bounds)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(bounds);

        return new HealthQuery(
            PartitionRowsSql,
            [
                .. TableParameters(tables),
                new NpgsqlParameter("column", NpgsqlDbType.Text) { Value = column },
                new NpgsqlParameter("bounds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = bounds.ToArray() },
            ]);
    }

    /// <summary>What the statistics say of <paramref name="values" /> in <paramref name="tables" /> (<see cref="StatisticsRowsSql" />).</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="column">The column, by its catalog name: a value, never text in the statement.</param>
    /// <param name="values">The values to ask about.</param>
    public static HealthQuery StatisticsRows(IReadOnlyList<HealthTableTarget> tables, string column, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(values);

        return new HealthQuery(
            StatisticsRowsSql,
            [
                .. TableParameters(tables),
                new NpgsqlParameter("column", NpgsqlDbType.Text) { Value = column },
                new NpgsqlParameter("values", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() },
            ]);
    }

    /// <summary>
    /// <see cref="PartitionRows" /> as a command with its parameters bound, for a caller outside the health sweep that opens its own
    /// connection (the Tenants overview): the statement is built here and nowhere else (hard rule 4).
    /// </summary>
    /// <param name="tables">The tables.</param>
    /// <param name="column">The column.</param>
    /// <param name="bounds">The bound texts to match.</param>
    /// <param name="commandTimeout">A bound on the read, in whole seconds, never zero.</param>
    public static NpgsqlCommand PartitionRowsCommand(IReadOnlyList<HealthTableTarget> tables, string column, IReadOnlyList<string> bounds, TimeSpan commandTimeout) =>
        ToCommand(PartitionRows(tables, column, bounds), commandTimeout);

    /// <summary><see cref="StatisticsRows" /> as a command with its parameters bound (see <see cref="PartitionRowsCommand" />).</summary>
    /// <param name="tables">The tables.</param>
    /// <param name="column">The column.</param>
    /// <param name="values">The values to ask about.</param>
    /// <param name="commandTimeout">A bound on the read, in whole seconds, never zero.</param>
    public static NpgsqlCommand StatisticsRowsCommand(IReadOnlyList<HealthTableTarget> tables, string column, IReadOnlyList<string> values, TimeSpan commandTimeout) =>
        ToCommand(StatisticsRows(tables, column, values), commandTimeout);

    private static NpgsqlCommand ToCommand(HealthQuery query, TimeSpan commandTimeout)
    {
        var command = new NpgsqlCommand(query.Sql) { CommandTimeout = Math.Max((int) Math.Ceiling(commandTimeout.TotalSeconds), 1) };

        foreach (NpgsqlParameter parameter in query.Parameters)
        {
            command.Parameters.Add((NpgsqlParameter) parameter.Clone());
        }

        return command;
    }

    /// <summary>Maps one row of <see cref="PartitionRowsSql" />.</summary>
    public static ColumnPartitionRow ReadPartition(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new ColumnPartitionRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDouble(3));
    }

    /// <summary>Maps one row of <see cref="StatisticsRowsSql" />.</summary>
    public static ColumnStatisticsRow ReadStatistics(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new ColumnStatisticsRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDouble(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetBoolean(6));
    }

    private static NpgsqlParameter[] TableParameters(IReadOnlyList<HealthTableTarget> tables)
    {
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
}
