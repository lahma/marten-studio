using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// One partitioned table of the judged set, as <see cref="PartitionQueries.PartitionsSql" /> read it (T19, T20): its
/// partitioning, what one query that is not pruned locks of it, and - for a single-column range on a time or integer key -
/// where its newest partition ends.
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

    /// <summary>Whether a partition's upper bound is <c>MAXVALUE</c>: then no range ever runs out.</summary>
    public bool UnboundedAbove { get; init; }

    /// <summary>Whether it has a <c>DEFAULT</c> partition.</summary>
    public bool HasDefault { get; init; }

    /// <summary>The default partition's rows: the greater of its <c>reltuples</c> and its <c>n_live_tup</c>; zero for none.</summary>
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
/// (W5-B1, moved from W5-P1's step 1; the Partitions page that would draw the rest is P1's, in 0.6).
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
/// <b>Read back in the same session.</b> A time bound is deparsed in the session's <c>DateStyle</c> and <c>TimeZone</c> and cast
/// back to its type in the same statement, so it round-trips whatever they are; a <c>timestamp</c> key is read as UTC. The
/// session's <c>TimeZone</c> comes back beside it, so a <c>timestamptz</c> range cut at local midnights is recognised as months
/// in that zone (W5-B1 review F10).
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
        tree (root, relid, depth) as (
            select l.oid, i.inhrelid, 1
            from listed l
            join pg_catalog.pg_inherits i on i.inhparent = l.oid
            union all
            select t.root, i.inhrelid, t.depth + 1
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
        ),
        members as (
            select t.root, t.depth, c.oid, c.relkind, c.reltuples, c.relpartbound,
                   (select pg_catalog.count(*) from pg_catalog.pg_index x where x.indrelid = c.oid) as indexes
            from tree t
            join pg_catalog.pg_class c on c.oid = t.relid
        ),
        keyed as (
            select l.oid, l.nspname, l.relname, p.partstrat, p.partnatts,
                   case when p.partnatts = 1 and p.partattrs[0] > 0 then a.atttypid end as key_type
            from listed l
            join pg_catalog.pg_partitioned_table p on p.partrelid = l.oid
            left join pg_catalog.pg_attribute a on a.attrelid = l.oid and a.attnum = p.partattrs[0]
        ),
        bounds as (
            select m.root, m.oid, m.reltuples,
                   b.bound = 'DEFAULT' as is_default,
                   pg_catalog.substring(b.bound, '^FOR VALUES FROM \((.*)\) TO \(.*\)$') as lower_text,
                   pg_catalog.substring(b.bound, '^FOR VALUES FROM \(.*\) TO \((.*)\)$') as upper_text
            from members m
            cross join lateral (select pg_catalog.pg_get_expr(m.relpartbound, 0) as bound) b
            where m.depth = 1
        ),
        valued as (
            select b.root, b.upper_text,
                   case when k.key_type in ('pg_catalog.date'::pg_catalog.regtype, 'pg_catalog.timestamp'::pg_catalog.regtype,
                                            'pg_catalog.timestamptz'::pg_catalog.regtype, 'pg_catalog.int2'::pg_catalog.regtype,
                                            'pg_catalog.int4'::pg_catalog.regtype, 'pg_catalog.int8'::pg_catalog.regtype)
                        then b.lower_text end as lower_value_text,
                   case when k.key_type in ('pg_catalog.date'::pg_catalog.regtype, 'pg_catalog.timestamp'::pg_catalog.regtype,
                                            'pg_catalog.timestamptz'::pg_catalog.regtype, 'pg_catalog.int2'::pg_catalog.regtype,
                                            'pg_catalog.int4'::pg_catalog.regtype, 'pg_catalog.int8'::pg_catalog.regtype)
                        then b.upper_text end as upper_value_text,
                   k.key_type
            from bounds b
            join keyed k on k.oid = b.root
            where not b.is_default
              and k.partstrat = 'r'
        ),
        read_back as (
            select v.root, v.upper_text,
                   (select case
                               when x.t is null then null
                               when v.key_type = 'pg_catalog.timestamptz'::pg_catalog.regtype and x.t ~ '^''[^'']*''$'
                                   then extract(epoch from pg_catalog.btrim(x.t, '''')::timestamptz)::float8
                               when v.key_type = 'pg_catalog.timestamp'::pg_catalog.regtype and x.t ~ '^''[^'']*''$'
                                   then extract(epoch from pg_catalog.btrim(x.t, '''')::timestamp)::float8
                               when v.key_type = 'pg_catalog.date'::pg_catalog.regtype and x.t ~ '^''[^'']*''$'
                                   then extract(epoch from pg_catalog.btrim(x.t, '''')::date::timestamp)::float8
                               when pg_catalog.btrim(x.t, '''') ~ '^-?[0-9]+$'
                                   then pg_catalog.btrim(x.t, '''')::numeric::float8
                           end
                    from (select v.upper_value_text as t) x) as upper_value,
                   (select case
                               when x.t is null then null
                               when v.key_type = 'pg_catalog.timestamptz'::pg_catalog.regtype and x.t ~ '^''[^'']*''$'
                                   then extract(epoch from pg_catalog.btrim(x.t, '''')::timestamptz)::float8
                               when v.key_type = 'pg_catalog.timestamp'::pg_catalog.regtype and x.t ~ '^''[^'']*''$'
                                   then extract(epoch from pg_catalog.btrim(x.t, '''')::timestamp)::float8
                               when v.key_type = 'pg_catalog.date'::pg_catalog.regtype and x.t ~ '^''[^'']*''$'
                                   then extract(epoch from pg_catalog.btrim(x.t, '''')::date::timestamp)::float8
                               when pg_catalog.btrim(x.t, '''') ~ '^-?[0-9]+$'
                                   then pg_catalog.btrim(x.t, '''')::numeric::float8
                           end
                    from (select v.lower_value_text as t) x) as lower_value
            from valued v
        ),
        settings as (
            select pg_catalog.current_setting('max_locks_per_transaction')::int as max_locks,
                   pg_catalog.current_setting('max_connections')::int as max_connections,
                   pg_catalog.current_setting('max_prepared_transactions')::int as max_prepared,
                   (select s.context::text from pg_catalog.pg_settings s where s.name = 'max_locks_per_transaction') as max_locks_context,
                   pg_catalog.current_setting('server_version_num')::int as server_version,
                   pg_catalog.current_setting('TimeZone') as time_zone
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
               (select pg_catalog.count(*) from read_back r where r.root = k.oid and r.upper_value is not null)::int,
               n.upper_value,
               n.lower_value,
               exists (select 1 from read_back r where r.root = k.oid and r.upper_text = 'MAXVALUE'),
               exists (select 1 from bounds b where b.root = k.oid and b.is_default),
               coalesce((select pg_catalog.max(greatest(b.reltuples::float8, 0, coalesce(s.n_live_tup, 0)::float8))
                         from bounds b
                         left join pg_catalog.pg_stat_all_tables s on s.relid = b.oid
                         where b.root = k.oid and b.is_default), 0),
               st.max_locks,
               st.max_connections,
               st.max_prepared,
               st.max_locks_context,
               st.server_version,
               st.time_zone
        from keyed k
        cross join settings st
        left join lateral (
            select r.upper_value, r.lower_value
            from read_back r
            where r.root = k.oid and r.upper_value is not null
            order by r.upper_value desc
            limit 1
        ) n on true
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

    /// <summary>Maps one row of <see cref="PartitionsSql" />.</summary>
    public static HealthPartitionedTableRow ReadPartitionedTable(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new HealthPartitionedTableRow
        {
            Schema = reader.GetString(0),
            Table = reader.GetString(1),
            Strategy = reader.GetString(2),
            KeyColumns = reader.GetInt32(3),
            KeyType = reader.IsDBNull(4) ? null : reader.GetString(4),
            Leaves = reader.GetInt32(5),
            Intermediates = reader.GetInt32(6),
            LeafIndexes = reader.GetInt64(7),
            PartitionedIndexes = reader.GetInt32(8),
            BoundedPartitions = reader.GetInt32(9),
            NewestUpper = reader.IsDBNull(10) ? null : reader.GetDouble(10),
            NewestLower = reader.IsDBNull(11) ? null : reader.GetDouble(11),
            UnboundedAbove = reader.GetBoolean(12),
            HasDefault = reader.GetBoolean(13),
            DefaultRows = reader.GetDouble(14),
            MaxLocksPerTransaction = reader.GetInt32(15),
            MaxConnections = reader.GetInt32(16),
            MaxPreparedTransactions = reader.GetInt32(17),
            MaxLocksContext = reader.IsDBNull(18) ? null : reader.GetString(18),
            ServerVersion = reader.GetInt32(19),
            TimeZone = reader.IsDBNull(20) ? null : reader.GetString(20),
        };
    }
}
