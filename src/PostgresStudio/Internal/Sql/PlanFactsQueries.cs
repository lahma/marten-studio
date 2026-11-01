using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>A relation a plan named, as the catalog resolved it.</summary>
/// <param name="Ordinal">Its position in the list that was asked about.</param>
/// <param name="Oid">Its oid, or <see langword="null" /> when no such relation exists.</param>
/// <param name="Schema">The catalog's schema name for it.</param>
/// <param name="Name">The catalog's name for it.</param>
/// <param name="Kind"><c>relkind</c>.</param>
internal sealed record PlanFactsResolvedRow(int Ordinal, uint? Oid, string? Schema, string? Name, string? Kind)
{
    /// <summary>The schemas of every ancestor (the partitioned or inherited-from tables above it), nearest first.</summary>
    public IReadOnlyList<string> AncestorSchemas { get; init; } = [];

    /// <summary>The ancestors' names, in the order of <see cref="AncestorSchemas" />.</summary>
    public IReadOnlyList<string> AncestorNames { get; init; } = [];
}

/// <summary>One row of <see cref="PlanFactsQueries.RelationsSql" />.</summary>
internal sealed record PlanFactsRelationRow
{
    /// <summary>The relation.</summary>
    public required uint Oid { get; init; }

    /// <summary><c>relkind</c>.</summary>
    public string? Kind { get; init; }

    /// <summary><c>reltuples</c>, <c>-1</c> where never analyzed.</summary>
    public double? Tuples { get; init; }

    /// <summary><c>relpages</c>.</summary>
    public long? Pages { get; init; }

    /// <summary><c>relallvisible</c>.</summary>
    public long? AllVisible { get; init; }

    /// <summary>Last manual analyze.</summary>
    public DateTimeOffset? LastAnalyze { get; init; }

    /// <summary>Last automatic analyze.</summary>
    public DateTimeOffset? LastAutoAnalyze { get; init; }

    /// <summary>Last manual vacuum.</summary>
    public DateTimeOffset? LastVacuum { get; init; }

    /// <summary>Last automatic vacuum.</summary>
    public DateTimeOffset? LastAutoVacuum { get; init; }

    /// <summary>Rows changed since the last analyze.</summary>
    public long? ModifiedSinceAnalyze { get; init; }

    /// <summary>Dead tuples.</summary>
    public long? DeadTuples { get; init; }

    /// <summary>Live tuples.</summary>
    public long? LiveTuples { get; init; }

    /// <summary>The partitioned parent's schema, when the relation is a partition.</summary>
    public string? ParentSchema { get; init; }

    /// <summary>The partitioned parent.</summary>
    public string? ParentName { get; init; }

    /// <summary>How many partitions the parent has directly.</summary>
    public long? ParentPartitions { get; init; }

    /// <summary>The parent's partition key columns.</summary>
    public IReadOnlyList<string> ParentKey { get; init; } = [];
}

/// <summary>One row of <see cref="PlanFactsQueries.IndexesSql" />.</summary>
internal sealed record PlanFactsIndexRow(
    uint Table,
    string Name,
    bool IsPrimary,
    bool IsUnique,
    bool IsValid,
    bool IsPartial,
    bool HasExpressions,
    IReadOnlyList<string?> Keys,
    string AccessMethod,
    long? SizeBytes,
    long? Scans,
    DateTimeOffset? LastScan);

/// <summary>One row of <see cref="PlanFactsQueries.ExtendedStatisticsSql" />.</summary>
internal sealed record PlanFactsExtendedRow(uint Table, string Schema, string Name, IReadOnlyList<string> Columns, IReadOnlyList<string> Kinds);

/// <summary>One row of <see cref="PlanFactsQueries.ColumnsSql" />.</summary>
internal sealed record PlanFactsColumnRow(uint Table, string Column, string? Type, bool HasStatistics, bool Inherited, double? NullFraction, double? Distinct, double? Correlation);

/// <summary>One row of <see cref="PlanFactsQueries.ValuesSql" />.</summary>
internal sealed record PlanFactsValuesRow(uint Table, string Column, bool Inherited, string? MostCommonValues, string? MostCommonFrequencies, string? HistogramBounds);

/// <summary>
/// The statements behind the Plan page's facts (0.10-A, issue #28): what the database knows about the relations a plan names and the
/// columns it uses, read after the plan, in the console's read-only session, as the console's role.
/// </summary>
/// <remarks>
/// <para>
/// <b>The catalog, locking nothing.</b> Every statement reads <c>pg_catalog</c> and the statistics views, by oid, and calls no size
/// function and no deparse function: <c>pg_get_indexdef</c>, <c>pg_get_expr</c> with a relation and <c>pg_get_partkeydef</c> open
/// the relation they describe and queue behind a migration's <c>ACCESS EXCLUSIVE</c> (D27). An index is described by its key
/// columns (the expressions are <see langword="null" />), its size by <c>relpages</c> times the block size - the expressions
/// <c>DatabaseCatalogQueries.IndexesSql</c> uses for the same columns, without its deparse - and a partition key by the columns
/// <c>partattrs</c> names. A name resolves by a catalog join, with no lock. The matrix proves it behind a held
/// <c>ACCESS EXCLUSIVE</c> lock on every release.
/// </para>
/// <para>
/// <b>Names are parameters.</b> A schema, relation or column name goes in as an array element, never into the statement's text. The
/// oids every later statement is asked about are the ones <see cref="ResolveSql" /> returned - the catalog's own - and the caller
/// decides, before any of them is asked about, which the visitor may see.
/// </para>
/// <para>
/// <b>Data is a read of its own.</b> <see cref="ValuesSql" /> returns <c>most_common_vals</c> and <c>histogram_bounds</c>, which are
/// rows' values, and is run only for relations whose row gate passed (<c>DatabaseAccess.RequireRowAccessAsync</c>).
/// </para>
/// </remarks>
internal static class PlanFactsQueries
{
    /// <summary>The most index rows one read returns.</summary>
    public const int MaxIndexes = 1_000;

    /// <summary>The most extended statistics objects one read returns.</summary>
    public const int MaxExtended = 500;

    /// <summary>The most column rows one read returns.</summary>
    public const int MaxColumns = 2_000;

    /// <summary>The most characters of a most-common-values or histogram text kept.</summary>
    public const int MaxValueCharacters = 1_000;

    /// <summary>
    /// Resolves relation names, which the plan printed, to the catalog's oids, names and kinds - with no lock, and with no privilege
    /// check on the schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A join of the two catalogs on the exact names, never <c>to_regclass</c>: that function raises <c>42501</c> for a schema the
    /// reading role has no <c>USAGE</c> on, and a plan names such a table whenever it reads a view whose owner does - the one failure
    /// would take every other relation's facts with it. The catalogs themselves are readable by every role, and the visitor's gate
    /// (<c>DatabaseGate.CanSeeStructure</c>) is asked of what this returns before anything else is read.
    /// </para>
    /// <para>
    /// Each relation comes back with every ancestor it inherits from or is a partition of, found by walking <c>pg_inherits</c> up
    /// to the root (never <c>pg_partition_root</c>, which opens the relation): a partition is judged by its root as well as by its
    /// own name, because a hidden document type's per-tenant partition is named <c>mt_doc_&lt;type&gt;_&lt;tenant&gt;</c> and
    /// is no document table by its name alone.
    /// </para>
    /// </remarks>
    internal const string ResolveSql =
        """
        with recursive named as (
            select t.ord,
                   c.oid as relid,
                   n.nspname as sch,
                   c.relname as nam,
                   c.relkind as kind
            from rows from (pg_catalog.unnest(@schemas::pg_catalog.text[]), pg_catalog.unnest(@names::pg_catalog.text[])) with ordinality as t(sch0, nam0, ord)
            left join pg_catalog.pg_namespace n on n.nspname = t.sch0::pg_catalog.name
            left join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = t.nam0::pg_catalog.name
        ),
        walk as (
            select x.ord, x.relid as cur, 0 as depth
            from named x
            where x.relid is not null
            union all
            select w.ord, i.inhparent, w.depth + 1
            from walk w
            join pg_catalog.pg_inherits i on i.inhrelid = w.cur
            where w.depth < 32
        )
        select x.ord::pg_catalog.int4,
               x.relid,
               x.sch::pg_catalog.text,
               x.nam::pg_catalog.text,
               x.kind::pg_catalog.text,
               (select pg_catalog.array_agg(an.nspname::pg_catalog.text order by w.depth, w.cur)
                  from walk w
                  join pg_catalog.pg_class ac on ac.oid = w.cur
                  join pg_catalog.pg_namespace an on an.oid = ac.relnamespace
                  where w.ord = x.ord and w.depth > 0),
               (select pg_catalog.array_agg(ac.relname::pg_catalog.text order by w.depth, w.cur)
                  from walk w
                  join pg_catalog.pg_class ac on ac.oid = w.cur
                  where w.ord = x.ord and w.depth > 0)
        from named x
        order by x.ord
        """;

    /// <summary>
    /// The planner's facts about each relation: its estimate and pages, when it was last analyzed and vacuumed and how much has
    /// changed since, and - for a partition - its parent, the parent's partition count and the parent's key columns.
    /// </summary>
    internal const string RelationsSql =
        """
        select c.oid,
               c.relkind::pg_catalog.text,
               c.reltuples::pg_catalog.float8,
               c.relpages::pg_catalog.int8,
               c.relallvisible::pg_catalog.int8,
               s.last_analyze,
               s.last_autoanalyze,
               s.last_vacuum,
               s.last_autovacuum,
               s.n_mod_since_analyze,
               s.n_dead_tup,
               s.n_live_tup,
               pn.nspname::pg_catalog.text,
               p.relname::pg_catalog.text,
               (select pg_catalog.count(*) from pg_catalog.pg_inherits ip where ip.inhparent = p.oid)::pg_catalog.int8,
               (select pg_catalog.array_agg(coalesce(a.attname::pg_catalog.text, '(expression)') order by u.ord)
                  from pg_catalog.pg_partitioned_table pt
                  cross join pg_catalog.unnest(pt.partattrs::pg_catalog.int2[]) with ordinality as u(attnum, ord)
                  left join pg_catalog.pg_attribute a on a.attrelid = pt.partrelid and a.attnum = u.attnum and u.attnum > 0
                  where pt.partrelid = p.oid)
        from pg_catalog.pg_class c
        left join pg_catalog.pg_stat_all_tables s on s.relid = c.oid
        left join pg_catalog.pg_inherits i on i.inhrelid = c.oid and c.relispartition
        left join pg_catalog.pg_class p on p.oid = i.inhparent
        left join pg_catalog.pg_namespace pn on pn.oid = p.relnamespace
        where c.oid = any(@oids)
        """;

    /// <summary>
    /// The relations' indexes with the columns <c>DatabaseCatalogQueries.IndexesSql</c> gives them - key columns, access method, an
    /// estimated size from <c>relpages</c>, scans, last scan (<c>last_idx_scan</c> through <c>to_jsonb</c>, as it exists from
    /// PostgreSQL 16) - and none of its deparse.
    /// </summary>
    internal const string IndexesSql =
        """
        select i.indrelid,
               ic.relname::pg_catalog.text,
               i.indisprimary,
               i.indisunique,
               i.indisvalid,
               i.indpred is not null,
               i.indexprs is not null,
               (select pg_catalog.array_agg(a.attname::pg_catalog.text order by u.ord)
                  from pg_catalog.unnest(i.indkey::pg_catalog.int2[]) with ordinality as u(attnum, ord)
                  left join pg_catalog.pg_attribute a on a.attrelid = i.indrelid and a.attnum = u.attnum
                  where u.ord <= i.indnkeyatts),
               am.amname::pg_catalog.text,
               ic.relpages::pg_catalog.int8 * pg_catalog.current_setting('block_size')::pg_catalog.int8,
               s.idx_scan,
               (pg_catalog.to_jsonb(s) operator(pg_catalog.->>) 'last_idx_scan')::pg_catalog.timestamptz
        from pg_catalog.pg_index i
        join pg_catalog.pg_class ic on ic.oid = i.indexrelid
        join pg_catalog.pg_am am on am.oid = ic.relam
        left join pg_catalog.pg_stat_all_indexes s on s.indexrelid = i.indexrelid
        where i.indrelid = any(@oids)
        order by i.indrelid, i.indisprimary desc, ic.relname
        limit @cap
        """;

    /// <summary>The relations' extended statistics objects: name, covered columns and kinds. No deparse.</summary>
    internal const string ExtendedStatisticsSql =
        """
        select se.stxrelid,
               sn.nspname::pg_catalog.text,
               se.stxname::pg_catalog.text,
               (select pg_catalog.array_agg(a.attname::pg_catalog.text order by u.ord)
                  from pg_catalog.unnest(se.stxkeys::pg_catalog.int2[]) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = se.stxrelid and a.attnum = u.attnum),
               se.stxkind::pg_catalog.text[]
        from pg_catalog.pg_statistic_ext se
        join pg_catalog.pg_namespace sn on sn.oid = se.stxnamespace
        where se.stxrelid = any(@oids)
        order by se.stxrelid, se.stxname
        limit @cap
        """;

    /// <summary>
    /// The statistics of the columns a plan uses - <c>null_frac</c>, <c>n_distinct</c>, <c>correlation</c> - from <c>pg_stats</c>,
    /// which shows only the columns the reading role may select; and each column's type. No value of a row is in it.
    /// </summary>
    internal const string ColumnsSql =
        """
        select w.relid,
               a.attname::pg_catalog.text,
               pg_catalog.format_type(a.atttypid, a.atttypmod),
               ps.attname is not null,
               coalesce(ps.inherited, false),
               ps.null_frac::pg_catalog.float8,
               ps.n_distinct::pg_catalog.float8,
               ps.correlation::pg_catalog.float8
        from rows from (pg_catalog.unnest(@relids::pg_catalog.oid[]), pg_catalog.unnest(@names::pg_catalog.text[])) as w(relid, attname)
        join pg_catalog.pg_attribute a on a.attrelid = w.relid and a.attname = w.attname and a.attnum > 0 and not a.attisdropped
        join pg_catalog.pg_class c on c.oid = w.relid
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        left join pg_catalog.pg_stats ps on ps.schemaname = n.nspname and ps.tablename = c.relname and ps.attname = a.attname
        order by w.relid, a.attnum, ps.inherited
        limit @cap
        """;

    /// <summary>
    /// The values <c>pg_stats</c> keeps for those columns: <c>most_common_vals</c>, <c>most_common_freqs</c> and
    /// <c>histogram_bounds</c>, as text, cut at <see cref="MaxValueCharacters" />. Data: run only past the row gate.
    /// </summary>
    internal const string ValuesSql =
        """
        select w.relid,
               ps.attname::pg_catalog.text,
               ps.inherited,
               pg_catalog.left(ps.most_common_vals::pg_catalog.text, @max),
               pg_catalog.left(ps.most_common_freqs::pg_catalog.text, @max),
               pg_catalog.left(ps.histogram_bounds::pg_catalog.text, @max)
        from rows from (pg_catalog.unnest(@relids::pg_catalog.oid[]), pg_catalog.unnest(@names::pg_catalog.text[])) as w(relid, attname)
        join pg_catalog.pg_class c on c.oid = w.relid
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        join pg_catalog.pg_stats ps on ps.schemaname = n.nspname and ps.tablename = c.relname and ps.attname = w.attname
        order by w.relid, ps.attname, ps.inherited
        limit @cap
        """;

    /// <summary>Every statement the facts read sends, for the tests that hold them to the catalog.</summary>
    internal static IReadOnlyList<string> Statements { get; } =
        [ResolveSql, RelationsSql, IndexesSql, ExtendedStatisticsSql, ColumnsSql, ValuesSql];

    /// <summary>Resolves the names a plan printed.</summary>
    public static async Task<IReadOnlyList<PlanFactsResolvedRow>> ResolveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        IReadOnlyList<string> names,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ResolveSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(TextArray("schemas", schemas));
        command.Parameters.Add(TextArray("names", names));

        List<PlanFactsResolvedRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PlanFactsResolvedRow(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetFieldValue<uint>(1),
                NullableString(reader, 2),
                NullableString(reader, 3),
                NullableString(reader, 4))
            {
                AncestorSchemas = reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5),
                AncestorNames = reader.IsDBNull(6) ? [] : reader.GetFieldValue<string[]>(6),
            });
        }

        return rows;
    }

    /// <summary>Reads the relations' own facts.</summary>
    public static async Task<IReadOnlyList<PlanFactsRelationRow>> ReadRelationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<uint> oids,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(RelationsSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(OidArray("oids", oids));

        List<PlanFactsRelationRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PlanFactsRelationRow
            {
                Oid = reader.GetFieldValue<uint>(0),
                Kind = NullableString(reader, 1),
                Tuples = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                Pages = NullableInt64(reader, 3),
                AllVisible = NullableInt64(reader, 4),
                LastAnalyze = NullableTime(reader, 5),
                LastAutoAnalyze = NullableTime(reader, 6),
                LastVacuum = NullableTime(reader, 7),
                LastAutoVacuum = NullableTime(reader, 8),
                ModifiedSinceAnalyze = NullableInt64(reader, 9),
                DeadTuples = NullableInt64(reader, 10),
                LiveTuples = NullableInt64(reader, 11),
                ParentSchema = NullableString(reader, 12),
                ParentName = NullableString(reader, 13),
                ParentPartitions = NullableInt64(reader, 14),
                ParentKey = reader.IsDBNull(15) ? [] : reader.GetFieldValue<string[]>(15),
            });
        }

        return rows;
    }

    /// <summary>Reads the relations' indexes.</summary>
    public static async Task<IReadOnlyList<PlanFactsIndexRow>> ReadIndexesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<uint> oids,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(IndexesSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(OidArray("oids", oids));
        command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = MaxIndexes });

        List<PlanFactsIndexRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PlanFactsIndexRow(
                reader.GetFieldValue<uint>(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? [] : reader.GetFieldValue<string?[]>(7),
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                NullableInt64(reader, 9),
                NullableInt64(reader, 10),
                NullableTime(reader, 11)));
        }

        return rows;
    }

    /// <summary>Reads the relations' extended statistics.</summary>
    public static async Task<IReadOnlyList<PlanFactsExtendedRow>> ReadExtendedStatisticsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<uint> oids,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ExtendedStatisticsSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(OidArray("oids", oids));
        command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = MaxExtended });

        List<PlanFactsExtendedRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PlanFactsExtendedRow(
                reader.GetFieldValue<uint>(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? [] : reader.GetFieldValue<string[]>(3),
                reader.IsDBNull(4) ? [] : reader.GetFieldValue<string[]>(4)));
        }

        return rows;
    }

    /// <summary>Reads the statistics of the listed columns: <paramref name="relids" /> and <paramref name="columns" /> are parallel.</summary>
    public static async Task<IReadOnlyList<PlanFactsColumnRow>> ReadColumnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<uint> relids,
        IReadOnlyList<string> columns,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ColumnsSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(OidArray("relids", relids));
        command.Parameters.Add(TextArray("names", columns));
        command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = MaxColumns });

        List<PlanFactsColumnRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PlanFactsColumnRow(
                reader.GetFieldValue<uint>(0),
                reader.GetString(1),
                NullableString(reader, 2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7)));
        }

        return rows;
    }

    /// <summary>Reads the values of the listed columns - data: only for relations whose row gate passed.</summary>
    public static async Task<IReadOnlyList<PlanFactsValuesRow>> ReadValuesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<uint> relids,
        IReadOnlyList<string> columns,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ValuesSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(OidArray("relids", relids));
        command.Parameters.Add(TextArray("names", columns));
        command.Parameters.Add(new NpgsqlParameter("max", NpgsqlDbType.Integer) { Value = MaxValueCharacters });
        command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = MaxColumns });

        List<PlanFactsValuesRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PlanFactsValuesRow(
                reader.GetFieldValue<uint>(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                NullableString(reader, 3),
                NullableString(reader, 4),
                NullableString(reader, 5)));
        }

        return rows;
    }

    private static NpgsqlParameter TextArray(string name, IReadOnlyList<string> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values as string[] ?? [.. values] };

    private static NpgsqlParameter OidArray(string name, IReadOnlyList<uint> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Oid) { Value = values as uint[] ?? [.. values] };

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? NullableTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
