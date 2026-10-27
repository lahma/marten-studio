using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// One partitioned table of the judged set, as <see cref="PartitionQueries.PartitionsSql" /> read it (T19, T20): its
/// partitioning, what one query that is not pruned locks of it, and - for a single-column range on a time or integer key -
/// where its newest partition ends and where its rows stop.
/// </summary>
/// <remarks>Init properties, so a unit test states only what its case is about. Never a partition's name.</remarks>
internal sealed record HealthPartitionedTableRow
{
    /// <summary>The schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The partitioned table.</summary>
    public required string Table { get; init; }

    /// <summary><c>partstrat</c>: <c>r</c> range, <c>l</c> list, <c>h</c> hash.</summary>
    public string Strategy { get; init; } = "r";

    /// <summary><c>partnatts</c>: how many columns or expressions the partition key has.</summary>
    public int KeyColumns { get; init; } = 1;

    /// <summary>The key column's type, <c>format_type</c>'s spelling, for a single-column key that is a column; otherwise <see langword="null" />.</summary>
    public string? KeyType { get; init; }

    /// <summary>The leaf partitions, at every level.</summary>
    public int Leaves { get; init; }

    /// <summary>The partitions that are partitioned tables themselves, below the root.</summary>
    public int Intermediates { get; init; }

    /// <summary>The indexes on every leaf partition, summed.</summary>
    public long LeafIndexes { get; init; }

    /// <summary>The partitioned indexes - on the root and on every intermediate partitioned table.</summary>
    public int PartitionedIndexes { get; init; }

    /// <summary>How many of the root's own partitions have a finite upper bound that was read as a value.</summary>
    public int BoundedPartitions { get; init; }

    /// <summary>
    /// The newest upper bound of the root's own partitions: Unix seconds for a <c>date</c>, <c>timestamp</c> (read as UTC) or
    /// <c>timestamptz</c> key, the number itself for an integer key - or <see langword="null" /> for none.
    /// </summary>
    public double? NewestUpper { get; init; }

    /// <summary>The lower bound of that same partition, or <see langword="null" /> for <c>MINVALUE</c>.</summary>
    public double? NewestLower { get; init; }

    /// <summary>Whether a partition's upper bound is <c>MAXVALUE</c> (or <c>'infinity'</c>): then no range ever runs out.</summary>
    public bool UnboundedAbove { get; init; }

    /// <summary>
    /// The upper bound of the highest of the root's own partitions that holds rows by the catalog's estimate (its
    /// sub-partitions' summed) - where the data stops - or <see langword="null" /> when none does, or none was read (X4).
    /// </summary>
    public double? PopulatedUpper { get; init; }

    /// <summary>How many of the root's own partitions lie wholly above <see cref="PopulatedUpper" />: the empty ones left (X4).</summary>
    public int EmptyAbove { get; init; }

    /// <summary>
    /// How many of the root's own range bounds the studio did not read (<see cref="PartitionBoundText" />): a date before Christ,
    /// or a time bound under a <c>DateStyle</c> other than ISO (X4).
    /// </summary>
    public int UnreadBounds { get; init; }

    /// <summary>The session's <c>DateStyle</c>, which a time bound was written in (X4); <see langword="null" /> reads as ISO.</summary>
    public string? DateStyle { get; init; }

    /// <summary>Whether it has a <c>DEFAULT</c> partition.</summary>
    public bool HasDefault { get; init; }

    /// <summary>
    /// The default partition's rows: the greater of its <c>reltuples</c> and its <c>n_live_tup</c> (its sub-partitions' summed);
    /// zero for none.
    /// </summary>
    public double DefaultRows { get; init; }

    /// <summary><c>max_locks_per_transaction</c>.</summary>
    public int MaxLocksPerTransaction { get; init; } = 64;

    /// <summary><c>max_connections</c>.</summary>
    public int MaxConnections { get; init; } = 100;

    /// <summary><c>max_prepared_transactions</c>.</summary>
    public int MaxPreparedTransactions { get; init; }

    /// <summary><c>pg_settings.context</c> of <c>max_locks_per_transaction</c> - <c>postmaster</c> - for the remedy's restart sentence.</summary>
    public string? MaxLocksContext { get; init; } = "postmaster";

    /// <summary><c>server_version_num</c>.</summary>
    public int ServerVersion { get; init; } = 170_000;

    /// <summary>
    /// The session's <c>TimeZone</c> - the database's or role's default unless the connection sets one - which a
    /// <c>timestamptz</c> bound written without an offset was read in, so month boundaries are looked for in it (W5-B1 review
    /// F10). <see langword="null" /> reads as UTC.
    /// </summary>
    public string? TimeZone { get; init; }

    /// <summary>The table's key.</summary>
    public string Key => Schema + "." + Table;
}

/// <summary>
/// The partition facts the T19 and T20 checks read: one lock-free catalog statement over the judged set's partitioned tables
/// (W5-B1, moved from W5-P1's step 1; 0.6's X4 reads its bounds as text, parsed by <see cref="PartitionBoundText" />).
/// </summary>
/// <remarks>
/// <para>
/// <b>Walked through <c>pg_inherits</c>, never <c>pg_partition_tree</c></b>, which locks every partition (D27); no size function.
/// </para>
/// <para>
/// <b>Bounds without a relation.</b> <c>pg_get_expr(relpartbound, relid)</c> opens the partition to build its deparse
/// context and queues behind a migration's <c>ACCESS EXCLUSIVE</c> (measured on 14.24, 17.11 and 18.6, VERS review), as
/// <c>pg_get_partkeydef</c> does behind a lock on the parent (W5-B1, 14.24 and 18.6). A bound holds no column reference, so
/// <c>pg_get_expr(relpartbound, 0)</c> deparses it with no relation at all - no context, no open, no lock: measured on 14.24
/// and 18.6 (W5-B1, 2026-10-26) returning at once while another session held <c>ACCESS EXCLUSIVE</c> on the partition and on
/// the parent, and proved by the version matrix's <c>MatrixReads</c> on every leg (a partition held) and by
/// <c>LintMatrixLiveTests</c> on 14 and 18 (the parent held, beside the two deparses that queue). The key column comes from
/// <c>pg_partitioned_table.partattrs</c> and <c>pg_attribute</c>, never deparsed.
/// </para>
/// <para>
/// <b>Read as text, parsed in C#</b> (X4). The statement returns each of a single-column range's partitions' bound text, the
/// <c>DEFAULT</c> partition's left out, with its estimated rows, and the session's <c>DateStyle</c> and <c>TimeZone</c>;
/// <see cref="PartitionBoundText" /> reads the text - a pure function the tests hold to strings captured from 14.24 and 18.6 -
/// and <see cref="ReadPartitionedTable" /> rolls it up. A time bound is read only under an ISO <c>DateStyle</c>, a
/// <c>timestamp</c> key as UTC, and a <c>timestamptz</c> range cut at local midnights is recognised as months in the session's
/// <c>TimeZone</c> (W5-B1 review F10). Only a range's own partitions are deparsed: a tenant-partitioned table's thousands are
/// list partitions, counted and never read, and the <c>DEFAULT</c> partition is <c>pg_partitioned_table.partdefid</c>.
/// </para>
/// <para>
/// <b>Rows as the catalog estimates them</b>: the greater of <c>reltuples</c> and <c>pg_stat_get_live_tuples</c> - a statistics
/// counter, no lock - for each leaf, summed into the partition of the root it sits under. Where the rows stop is what an
/// integer range is judged by.
/// </para>
/// </remarks>
internal static class PartitionQueries
{
    /// <summary>The partitioned tables of the listed tables, with their partitioning facts and the lock-table settings.</summary>
    internal const string PartitionsSql =
        """
        with recursive listed as (
            select c.oid, n.nspname, c.relname
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind = 'p'
        ),
        tree (root, top, relid, depth) as (
            select l.oid, i.inhrelid, i.inhrelid, 1
            from listed l
            join pg_catalog.pg_inherits i on i.inhparent = l.oid
            union all
            select t.root, t.top, i.inhrelid, t.depth + 1
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
        ),
        members as (
            select t.root, t.top, t.depth, c.oid, c.relkind, c.relpartbound,
                   (select pg_catalog.count(*) from pg_catalog.pg_index x where x.indrelid = c.oid) as indexes,
                   case when c.relkind = 'r'
                        then greatest(c.reltuples::float8, 0, coalesce(pg_catalog.pg_stat_get_live_tuples(c.oid), 0)::float8)
                        else 0
                   end as tuples
            from tree t
            join pg_catalog.pg_class c on c.oid = t.relid
        ),
        rolled as (
            select m.top, pg_catalog.sum(m.tuples)::float8 as tuples
            from members m
            group by m.top
        ),
        keyed as (
            select l.oid, l.nspname, l.relname, p.partstrat, p.partnatts, p.partdefid,
                   case when p.partnatts = 1 and p.partattrs[0] > 0 then a.atttypid end as key_type
            from listed l
            join pg_catalog.pg_partitioned_table p on p.partrelid = l.oid
            left join pg_catalog.pg_attribute a on a.attrelid = l.oid and a.attnum = p.partattrs[0]
        ),
        ranged as (
            select m.root, m.oid, r.tuples, pg_catalog.pg_get_expr(m.relpartbound, 0) as bound
            from members m
            join keyed k on k.oid = m.root
            join rolled r on r.top = m.oid
            where m.depth = 1
              and k.partstrat = 'r'
              and k.partnatts = 1
              and m.oid <> k.partdefid
        ),
        settings as (
            select pg_catalog.current_setting('max_locks_per_transaction')::int as max_locks,
                   pg_catalog.current_setting('max_connections')::int as max_connections,
                   pg_catalog.current_setting('max_prepared_transactions')::int as max_prepared,
                   (select s.context::text from pg_catalog.pg_settings s where s.name = 'max_locks_per_transaction') as max_locks_context,
                   pg_catalog.current_setting('server_version_num')::int as server_version,
                   pg_catalog.current_setting('TimeZone') as time_zone,
                   pg_catalog.current_setting('DateStyle') as date_style
        )
        select k.nspname::text,
               k.relname::text,
               k.partstrat::text,
               k.partnatts::int,
               case when k.key_type is not null then pg_catalog.format_type(k.key_type, null) end,
               (select pg_catalog.count(*) from members m where m.root = k.oid and m.relkind = 'r')::int,
               (select pg_catalog.count(*) from members m where m.root = k.oid and m.relkind = 'p')::int,
               (select coalesce(pg_catalog.sum(m.indexes), 0) from members m where m.root = k.oid and m.relkind = 'r')::bigint,
               ((select pg_catalog.count(*) from pg_catalog.pg_index x where x.indrelid = k.oid)
                + (select coalesce(pg_catalog.sum(m.indexes), 0) from members m where m.root = k.oid and m.relkind = 'p'))::int,
               (select pg_catalog.array_agg(g.bound order by g.oid) from ranged g where g.root = k.oid),
               (select pg_catalog.array_agg(g.tuples order by g.oid) from ranged g where g.root = k.oid),
               k.partdefid <> 0,
               coalesce((select r.tuples from rolled r where r.top = k.partdefid), 0)::float8,
               st.max_locks,
               st.max_connections,
               st.max_prepared,
               st.max_locks_context,
               st.server_version,
               st.time_zone,
               st.date_style
        from keyed k
        cross join settings st
        order by 1, 2
        """;

    /// <summary>Every list statement here, for the tests that hold them to D27.</summary>
    internal static IReadOnlyList<string> ListStatements { get; } = [PartitionsSql];

    /// <summary>The partitioned tables among <paramref name="tables" /> (<see cref="PartitionsSql" />).</summary>
    public static HealthQuery Partitions(IReadOnlyList<HealthTableTarget> tables)
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
            PartitionsSql,
            [
                new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas },
                new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names },
            ]);
    }

    /// <summary>Maps one row of <see cref="PartitionsSql" />, its range bounds read by <see cref="PartitionBoundText" />.</summary>
    public static HealthPartitionedTableRow ReadPartitionedTable(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? keyType = reader.IsDBNull(4) ? null : reader.GetString(4);
        string? dateStyle = reader.IsDBNull(19) ? null : reader.GetString(19);
        RangeSummary range = reader.IsDBNull(9)
            ? RangeSummary.Empty
            : PartitionBoundText.Summarise(
                reader.GetFieldValue<string[]>(9),
                reader.IsDBNull(10) ? [] : reader.GetFieldValue<double[]>(10),
                keyType,
                dateStyle);

        return new HealthPartitionedTableRow
        {
            Schema = reader.GetString(0),
            Table = reader.GetString(1),
            Strategy = reader.GetString(2),
            KeyColumns = reader.GetInt32(3),
            KeyType = keyType,
            Leaves = reader.GetInt32(5),
            Intermediates = reader.GetInt32(6),
            LeafIndexes = reader.GetInt64(7),
            PartitionedIndexes = reader.GetInt32(8),
            BoundedPartitions = range.Bounded,
            NewestUpper = range.NewestUpper,
            NewestLower = range.NewestLower,
            UnboundedAbove = range.UnboundedAbove,
            PopulatedUpper = range.PopulatedUpper,
            EmptyAbove = range.EmptyAbove,
            UnreadBounds = range.Unread,
            HasDefault = reader.GetBoolean(11),
            DefaultRows = reader.GetDouble(12),
            MaxLocksPerTransaction = reader.GetInt32(13),
            MaxConnections = reader.GetInt32(14),
            MaxPreparedTransactions = reader.GetInt32(15),
            MaxLocksContext = reader.IsDBNull(16) ? null : reader.GetString(16),
            ServerVersion = reader.GetInt32(17),
            TimeZone = reader.IsDBNull(18) ? null : reader.GetString(18),
            DateStyle = dateStyle,
        };
    }
}
