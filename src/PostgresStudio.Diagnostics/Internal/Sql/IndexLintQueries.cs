using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>One foreign key of a judged table, as <see cref="IndexLintQueries.ForeignKeysSql" /> read it - never a partition's clone.</summary>
/// <param name="Schema">The referencing table's schema.</param>
/// <param name="Table">The referencing table.</param>
/// <param name="Name">The constraint.</param>
/// <param name="Attributes">Its columns as attribute numbers of the referencing table (<c>conkey</c>), in key order.</param>
/// <param name="Columns">Its columns' names, in key order.</param>
/// <param name="ReferencedSchema">The referenced table's schema.</param>
/// <param name="ReferencedTable">The referenced table.</param>
/// <param name="ReferencedColumns">The referenced columns, in key order.</param>
/// <param name="PartitionKey">
/// The referencing table's partition key columns (<c>partattrs</c>, zero for an expression) when it is a partitioned table;
/// empty otherwise.
/// </param>
/// <param name="ReferencedDeletes">
/// <c>n_tup_del</c> of the referenced table, summed over its partitions - how many times a delete there ran this key's
/// check - or <see langword="null" /> when the statistics collector has nothing for it.
/// </param>
/// <param name="Validated"><c>convalidated</c>; a <c>NOT VALID</c> key is checked on every delete all the same.</param>
internal sealed record HealthForeignKeyRow(
    string Schema,
    string Table,
    string Name,
    IReadOnlyList<short> Attributes,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    IReadOnlyList<short> PartitionKey,
    long? ReferencedDeletes,
    bool Validated)
{
    /// <summary>The referencing table's key.</summary>
    public string TableKey => Schema + "." + Table;

    /// <summary>The key a finding names it by: schema, table, constraint.</summary>
    public string Key => Schema + "." + Table + "." + Name;
}

/// <summary>
/// One integer column of a judged table that a sequence feeds, as <see cref="IndexLintQueries.IntegerKeysSql" /> read it (T26)
/// - never the sequence's value, which is read only under a lock (B0 §3).
/// </summary>
/// <remarks>Init properties, so a unit test states only what its case is about.</remarks>
internal sealed record HealthIntegerKeyRow
{
    /// <summary>The table's schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The table.</summary>
    public required string Table { get; init; }

    /// <summary>The column.</summary>
    public required string Column { get; init; }

    /// <summary>The column's type: <c>smallint</c>, <c>integer</c> or <c>bigint</c>.</summary>
    public required string ColumnType { get; init; }

    /// <summary>The sequence's schema.</summary>
    public required string SequenceSchema { get; init; }

    /// <summary>The sequence.</summary>
    public required string Sequence { get; init; }

    /// <summary>The sequence's own type (<c>pg_sequence.seqtypid</c>).</summary>
    public string SequenceType { get; init; } = "bigint";

    /// <summary>How the column draws from it: <c>identity</c>, <c>serial</c> (an owned sequence) or <c>default</c> (a default that calls it).</summary>
    public string FedBy { get; init; } = "identity";

    /// <summary><c>seqstart</c>.</summary>
    public long Start { get; init; } = 1;

    /// <summary><c>seqincrement</c>.</summary>
    public long Increment { get; init; } = 1;

    /// <summary><c>seqmax</c>.</summary>
    public long Maximum { get; init; } = long.MaxValue;

    /// <summary><c>seqcycle</c>.</summary>
    public bool Cycles { get; init; }

    /// <summary>
    /// The table's <c>reltuples</c>, summed over its partitions; <see langword="null" /> while any of them was never analysed
    /// (<c>-1</c>).
    /// </summary>
    public double? Rows { get; init; }

    /// <summary>
    /// The largest value <c>pg_stats</c> holds for the column - the last histogram bound or the largest common value, over
    /// the partitions - as of the last <c>ANALYZE</c>; <see langword="null" /> when none is visible.
    /// </summary>
    public long? LargestSampled { get; init; }

    /// <summary>
    /// Whether the reading role may read the column's statistics: <c>SELECT</c> on the column of the table and of every
    /// partition, which neither <c>pg_read_all_stats</c> nor <c>pg_monitor</c> gives (B0 §8c).
    /// </summary>
    public bool StatisticsReadable { get; init; } = true;

    /// <summary>Whether a valid, unconditional one-column unique index or primary key is on the column - what makes the row count a floor.</summary>
    public bool Unique { get; init; } = true;

    /// <summary>The table's key.</summary>
    public string TableKey => Schema + "." + Table;
}

/// <summary>
/// The statements behind the W5-B1 lints: foreign keys without a covering index (T15), integer keys running out (T26), and
/// how old this database's own counters provably are (the usage guards of T01, T05 and T06).
/// </summary>
/// <remarks>
/// <para>
/// <b>A list locks nothing it lists</b> (D27). Every statement reads <c>pg_constraint</c>, <c>pg_index</c>,
/// <c>pg_attribute</c>, <c>pg_depend</c>, <c>pg_attrdef</c>, <c>pg_sequence</c>, <c>pg_inherits</c>,
/// <c>pg_partitioned_table</c>, <c>pg_stats</c> and the statistics views, and calls no deparse function - a key's columns
/// are attribute numbers and names from the catalog, so a table locked for a migration does not make the read wait. A
/// sequence's value is never read here: <c>pg_sequence_last_value</c> and the <c>pg_sequences</c> view lock it (B0 §3).
/// </para>
/// <para>
/// <b>Names are values.</b> The judged tables travel as two <c>text[]</c> parameters, as <see cref="HealthTableQueries" />'
/// do, so a statement shown under "Show SQL" names no table.
/// </para>
/// </remarks>
internal static class IndexLintQueries
{
    /// <summary>
    /// Every foreign key of the listed tables - the constraint on the table itself, never a partition's clone
    /// (<c>conparentid = 0</c>) - with its columns as attribute numbers and names, the referenced table and columns, the
    /// referencing table's partition key, and the referenced table's deletes (B0 §9's shape).
    /// </summary>
    internal const string ForeignKeysSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname, c.relkind
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        keys as (
            select l.nspname, l.relname, l.relkind, k.conname, k.conrelid, k.confrelid, k.conkey, k.confkey, k.convalidated
            from listed l
            join pg_catalog.pg_constraint k on k.conrelid = l.oid
            where k.contype = 'f'
              and k.conparentid = 0
        ),
        referenced (root, relid) as (
            select distinct k.confrelid, k.confrelid
            from keys k
            union all
            select t.root, i.inhrelid
            from referenced t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
        ),
        referenced_deletes as (
            select t.root,
                   pg_catalog.sum(s.n_tup_del) as deletes,
                   pg_catalog.count(s.relid) as counted
            from referenced t
            left join pg_catalog.pg_stat_all_tables s on s.relid = t.relid
            group by t.root
        )
        select k.nspname::text,
               k.relname::text,
               k.conname::text,
               k.conkey,
               (select pg_catalog.array_agg(a.attname::text order by u.ord)
                  from pg_catalog.unnest(k.conkey) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = k.conrelid and a.attnum = u.attnum),
               fn.nspname::text,
               fc.relname::text,
               (select pg_catalog.array_agg(a.attname::text order by u.ord)
                  from pg_catalog.unnest(k.confkey) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = k.confrelid and a.attnum = u.attnum),
               case when k.relkind = 'p'
                    then (select pg_catalog.string_to_array(p.partattrs::text, ' ')::pg_catalog.int2[]
                          from pg_catalog.pg_partitioned_table p
                          where p.partrelid = k.conrelid) end,
               case when d.counted > 0 then d.deletes::bigint end,
               k.convalidated
        from keys k
        join pg_catalog.pg_class fc on fc.oid = k.confrelid
        join pg_catalog.pg_namespace fn on fn.oid = fc.relnamespace
        left join referenced_deletes d on d.root = k.confrelid
        order by 1, 2, 3
        """;

    /// <summary>
    /// The oldest moment this database's statistics record - the earliest of every judged table's and every system catalog's
    /// last vacuum and analyze of either kind, and from 16 their last sequential and index scans and every one of their
    /// indexes' last scan - which its counters provably go back to (W5-B1's usage guards).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is needed.</b> From PostgreSQL 15 a database's own <c>stats_reset</c> stays null until somebody resets it, so a
    /// database created after the server's statistics were last reset reads, from the server's reset times alone, as old as
    /// they are (<c>StatisticsWindow</c>'s known limit, W5-CAP). Every timestamp a database's statistics hold is cleared with
    /// them - by <c>pg_stat_reset()</c>, a crash or a new database - so the oldest one is a moment they had already begun by.
    /// </para>
    /// <para>
    /// <b>Bounded, and never the shared catalogs.</b> The judged tables and <c>pg_catalog</c>'s own tables only - a few
    /// thousand rows at most, never a tenant-partitioned table's partitions - and never a shared catalog
    /// (<c>relisshared</c>), whose statistics belong to the cluster rather than this database.
    /// </para>
    /// </remarks>
    internal const string CountersAgeSql =
        """
        with judged as (
            select c.oid
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where not c.relisshared
            union
            select c.oid
            from pg_catalog.pg_class c
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'pg_catalog'
              and c.relkind = 'r'
              and not c.relisshared
        )
        select least(
            (select pg_catalog.min(least(s.last_vacuum, s.last_autovacuum, s.last_analyze, s.last_autoanalyze/*scans*/))
             from pg_catalog.pg_stat_all_tables s
             where s.relid in (select j.oid from judged j))/*indexes*/)
        """;

    /// <summary>The columns <see cref="CountersAgeSql" /> adds on 16 and later, where <c>last_seq_scan</c> and <c>last_idx_scan</c> exist.</summary>
    internal const string CountersAgeScans = ", s.last_seq_scan, s.last_idx_scan";

    /// <summary>The term <see cref="CountersAgeSql" /> adds on 16 and later: every one of their indexes' own last scan.</summary>
    internal const string CountersAgeIndexes =
        ",\n    (select pg_catalog.min(x.last_idx_scan)\n     from pg_catalog.pg_stat_all_indexes x\n     where x.relid in (select j.oid from judged j))";

    /// <summary>
    /// Every <c>smallint</c> or <c>integer</c> column of the listed tables that a sequence feeds - an identity column, a
    /// <c>serial</c> (an owned sequence: <c>pg_depend</c> deptype <c>i</c> or <c>a</c>), or a default whose <c>pg_attrdef</c>
    /// row depends on a sequence (deptype <c>n</c>, never a <c>nextval</c> matched in text) - and every <c>bigint</c> one fed
    /// by a <c>smallint</c> or <c>integer</c> sequence, or by an ascending sequence whose <c>MAXVALUE</c> is set below
    /// <c>bigint</c>'s (W5-B1 review F8), with the sequence's catalog row, the table's row count and the largest value
    /// <c>pg_stats</c> holds (T26).
    /// </summary>
    /// <remarks>
    /// The value <c>pg_stats</c> holds is visible only to a role with <c>SELECT</c> on the column (B0 §8c), and checked per
    /// partition, so whether the reading role may read it comes back beside it: without it the row count is the only floor.
    /// A partitioned table's statistics are its partitions' - autovacuum never analyses the parent (B0 §8c).
    /// </remarks>
    internal const string IntegerKeysSql =
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
        leaves as (
            select t.root, c.oid, c.relname, c.reltuples, ln.nspname
            from tree t
            join pg_catalog.pg_class c on c.oid = t.relid
            join pg_catalog.pg_namespace ln on ln.oid = c.relnamespace
            where c.relkind = 'r'
        ),
        fed as (
            select l.oid as table_oid, l.nspname, l.relname, a.attnum, a.attname, a.atttypid, d.seq_oid, d.via
            from listed l
            join pg_catalog.pg_attribute a on a.attrelid = l.oid and a.attnum > 0 and not a.attisdropped
            cross join lateral (
                select dep.objid as seq_oid,
                       case dep.deptype when 'i' then 'identity' else 'serial' end as via
                from pg_catalog.pg_depend dep
                where dep.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                  and dep.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
                  and dep.refobjid = l.oid
                  and dep.refobjsubid = a.attnum
                  and dep.deptype in ('a', 'i')
                union
                select dep.refobjid, 'default'
                from pg_catalog.pg_attrdef ad
                join pg_catalog.pg_depend dep
                  on dep.classid = 'pg_catalog.pg_attrdef'::pg_catalog.regclass
                 and dep.objid = ad.oid
                 and dep.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
                 and dep.deptype = 'n'
                where ad.adrelid = l.oid
                  and ad.adnum = a.attnum
            ) d
            where a.atttypid in ('pg_catalog.int2'::pg_catalog.regtype, 'pg_catalog.int4'::pg_catalog.regtype, 'pg_catalog.int8'::pg_catalog.regtype)
        )
        select f.nspname::text,
               f.relname::text,
               f.attname::text,
               pg_catalog.format_type(f.atttypid, null),
               sn.nspname::text,
               sc.relname::text,
               pg_catalog.format_type(q.seqtypid, null),
               f.via,
               q.seqstart,
               q.seqincrement,
               q.seqmax,
               q.seqcycle,
               (select case when pg_catalog.bool_or(lf.reltuples < 0) then null else pg_catalog.sum(lf.reltuples::float8) end
                from leaves lf
                where lf.root = f.table_oid),
               (select pg_catalog.max(greatest(
                           (h.b)[pg_catalog.array_upper(h.b, 1)],
                           (select pg_catalog.max(v) from pg_catalog.unnest(st.most_common_vals::text::bigint[]) as v)))
                from leaves lf
                join pg_catalog.pg_stats st
                  on st.schemaname = lf.nspname and st.tablename = lf.relname and st.attname = f.attname and not st.inherited
                cross join lateral (select st.histogram_bounds::text::bigint[] as b) h
                where lf.root = f.table_oid),
               coalesce((select pg_catalog.bool_and(pg_catalog.has_column_privilege(lf.oid, f.attname::text, 'SELECT'))
                         from leaves lf
                         where lf.root = f.table_oid), false),
               exists (
                   select 1
                   from pg_catalog.pg_index x
                   where x.indrelid = f.table_oid
                     and x.indisunique
                     and x.indisvalid
                     and x.indnkeyatts = 1
                     and x.indpred is null
                     and (pg_catalog.string_to_array(x.indkey::text, ' ')::pg_catalog.int2[])[1] = f.attnum)
        from fed f
        join pg_catalog.pg_class sc on sc.oid = f.seq_oid and sc.relkind = 'S'
        join pg_catalog.pg_namespace sn on sn.oid = sc.relnamespace
        join pg_catalog.pg_sequence q on q.seqrelid = sc.oid
        where f.atttypid in ('pg_catalog.int2'::pg_catalog.regtype, 'pg_catalog.int4'::pg_catalog.regtype)
           or q.seqtypid in ('pg_catalog.int2'::pg_catalog.regtype, 'pg_catalog.int4'::pg_catalog.regtype)
           or (q.seqincrement > 0 and q.seqmax < 9223372036854775807)
        order by 1, 2, 3, 5, 6, 8
        """;

    /// <summary>Every list statement here - the ones that must lock nothing they list - for the tests that hold them to it.</summary>
    internal static IReadOnlyList<string> ListStatements { get; } =
        [ForeignKeysSql, CountersAgeText(lastScanColumns: false), CountersAgeText(lastScanColumns: true), IntegerKeysSql];

    /// <summary>The foreign keys of the listed tables (<see cref="ForeignKeysSql" />).</summary>
    public static HealthQuery ForeignKeys(IReadOnlyList<HealthTableTarget> tables) => new(ForeignKeysSql, TargetParameters(tables));

    /// <summary>
    /// The oldest moment this database's statistics record (<see cref="CountersAgeSql" />): with <paramref name="lastScanColumns" />
    /// (PostgreSQL 16+), the last sequential and index scans too.
    /// </summary>
    public static HealthQuery CountersAge(IReadOnlyList<HealthTableTarget> tables, bool lastScanColumns) =>
        new(CountersAgeText(lastScanColumns), TargetParameters(tables));

    /// <summary>The integer keys a sequence feeds (<see cref="IntegerKeysSql" />).</summary>
    public static HealthQuery IntegerKeys(IReadOnlyList<HealthTableTarget> tables) => new(IntegerKeysSql, TargetParameters(tables));

    /// <summary>Maps one row of <see cref="ForeignKeysSql" />.</summary>
    public static HealthForeignKeyRow ReadForeignKey(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthForeignKeyRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? [] : reader.GetFieldValue<short[]>(3),
            reader.IsDBNull(4) ? [] : reader.GetFieldValue<string[]>(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? [] : reader.GetFieldValue<string[]>(7),
            reader.IsDBNull(8) ? [] : reader.GetFieldValue<short[]>(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            reader.GetBoolean(10));
    }

    /// <summary>Maps the one row of <see cref="CountersAge" />: the oldest moment, or <see langword="null" /> for none.</summary>
    public static DateTimeOffset? ReadCountersAge(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>Maps one row of <see cref="IntegerKeysSql" />.</summary>
    public static HealthIntegerKeyRow ReadIntegerKey(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthIntegerKeyRow
        {
            Schema = reader.GetString(0),
            Table = reader.GetString(1),
            Column = reader.GetString(2),
            ColumnType = reader.GetString(3),
            SequenceSchema = reader.GetString(4),
            Sequence = reader.GetString(5),
            SequenceType = reader.GetString(6),
            FedBy = reader.GetString(7),
            Start = reader.GetInt64(8),
            Increment = reader.GetInt64(9),
            Maximum = reader.GetInt64(10),
            Cycles = reader.GetBoolean(11),
            Rows = reader.IsDBNull(12) ? null : reader.GetDouble(12),
            LargestSampled = reader.IsDBNull(13) ? null : reader.GetInt64(13),
            StatisticsReadable = reader.GetBoolean(14),
            Unique = reader.GetBoolean(15),
        };
    }

    /// <summary>The counters-age statement for one side of 16.</summary>
    internal static string CountersAgeText(bool lastScanColumns) =>
        CountersAgeSql
            .Replace("/*scans*/", lastScanColumns ? CountersAgeScans : string.Empty, StringComparison.Ordinal)
            .Replace("/*indexes*/", lastScanColumns ? CountersAgeIndexes : string.Empty, StringComparison.Ordinal);

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
}
