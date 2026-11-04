using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// One relation a migration script names, resolved against the catalog: the table it is (or, for an index, belongs to) and that
/// table's estimated rows and size.
/// </summary>
/// <param name="Ordinal">The position of the relation in the list the read was asked about.</param>
/// <param name="TableOid">The table's <c>oid</c>, which the sessions read is asked about.</param>
/// <param name="Schema">The table's schema.</param>
/// <param name="Table">The table's name; for an index, the table it is on.</param>
/// <param name="IsPartitioned">Whether the table is partitioned (or has inheritance children): its figures include theirs.</param>
/// <param name="EstimatedRows">
/// <c>pg_class.reltuples</c> as a <c>bigint</c>, summed over the leaf relations; <c>-1</c> before any of them has been analyzed.
/// </param>
/// <param name="HeapBytes">The table's pages - and its partitions' - times the block size. An estimate.</param>
/// <param name="TotalBytes">Heap, TOAST and indexes, from page counts times the block size. An estimate.</param>
internal sealed record RelationFootprintRow(
    int Ordinal,
    uint TableOid,
    string Schema,
    string Table,
    bool IsPartitioned,
    long EstimatedRows,
    long HeapBytes,
    long TotalBytes);

/// <summary>One group of sessions holding or waiting for a lock on a table: how many, in what state.</summary>
/// <param name="TableOid">The table (the root of the partition tree the lock was found in).</param>
/// <param name="State">
/// <c>pg_stat_activity.state</c>, <c>prepared transaction</c> for a lock whose backend is gone, or <c>state not visible</c> when
/// the reading role may not see another role's session state.
/// </param>
/// <param name="Granted">Whether the lock is held (<see langword="true" />) or waited for.</param>
/// <param name="Sessions">How many distinct sessions (or prepared transactions) that is.</param>
internal sealed record TableSessionRow(uint TableOid, string State, bool Granted, int Sessions);

/// <summary>
/// The two catalog reads behind the apply section's "what this will lock" notes: the statistics of exactly the relations the
/// migration script names, and, for a visitor who passes the Activity gate, the sessions that have a lock on them now.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here locks the relations it reports on (D27).</b> The footprint read looks the names up in <c>pg_class</c> and
/// <c>pg_namespace</c> and reads <c>relpages</c> and <c>reltuples</c>; it calls no size function, which would queue behind the
/// very <c>ACCESS EXCLUSIVE</c> lock a migration in progress holds, and walks <c>pg_inherits</c> for a partitioned table's
/// partitions rather than <c>pg_partition_tree()</c>, which locks each one (the reasons <c>SchemaStatsQueries.TableStatsSql</c> gives).
/// Every figure is an estimate as of the last <c>VACUUM</c>, <c>ANALYZE</c> or index build, and is labelled so on screen.
/// </para>
/// <para>
/// <b>Counts and states, never text.</b> The sessions read selects no <c>query</c>, no role, no application, no client address and
/// no process id: it groups the lock table's holders and waiters on the named tables by their state and returns how many there are
/// (<c>count(distinct pid)</c>). It is the one place in this folder that reads <c>pg_locks</c> and <c>pg_stat_activity</c>, it is called
/// only by the migration preview's impact read after the Activity gate has passed
/// (<c>ActivityViewsOffThePollPathTests</c>), and it is never on a poll: the Schema tab's Preview button starts it.
/// </para>
/// <para>
/// Names travel as <c>text[]</c> parameters; no identifier is interpolated (hard rule 4).
/// </para>
/// </remarks>
internal static class MigrationImpactQueries
{
    /// <summary>
    /// Each requested relation resolved to its table, with the table's estimated rows and size - the whole partition tree's for a
    /// partitioned table. A relation that is not there, or is neither a table nor an index, has no row.
    /// </summary>
    internal const string FootprintSql =
        """
        with recursive named as (
            select r.schema_name, r.rel_name, r.ord
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@names)) with ordinality as r(schema_name, rel_name, ord)
        ),
        resolved as (
            select nm.ord,
                   case when c.relkind in ('i', 'I') then x.indrelid else c.oid end as table_oid
            from named nm
            join pg_catalog.pg_namespace n on n.nspname = nm.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = nm.rel_name
            left join pg_catalog.pg_index x on x.indexrelid = c.oid and c.relkind in ('i', 'I')
            where c.relkind in ('r', 'p', 'i', 'I')
        ),
        tree (root, relid) as (
            select t.table_oid, t.table_oid
            from (select distinct table_oid from resolved) t
            union all
            select tr.root, i.inhrelid
            from tree tr
            join pg_catalog.pg_inherits i on i.inhparent = tr.relid
        ),
        rolled as (
            select tr.root,
                   pg_catalog.count(*) > 1 as has_children,
                   pg_catalog.sum(d.relpages::bigint) as heap_pages,
                   pg_catalog.sum(coalesce(toast.relpages, 0)::bigint) as toast_pages,
                   pg_catalog.sum((select coalesce(pg_catalog.sum(ic.relpages::bigint), 0)
                                   from pg_catalog.pg_index x
                                   join pg_catalog.pg_class ic on ic.oid = x.indexrelid
                                   where x.indrelid = d.oid)) as index_pages,
                   pg_catalog.bool_and(d.reltuples < 0) filter (where d.relkind = 'r') as unanalyzed,
                   pg_catalog.sum(greatest(d.reltuples, 0)) filter (where d.relkind = 'r') as tuples
            from tree tr
            join pg_catalog.pg_class d on d.oid = tr.relid
            left join pg_catalog.pg_class toast on toast.oid = nullif(d.reltoastrelid, 0::pg_catalog.oid)
            group by tr.root
        )
        select r.ord::int,
               tc.oid,
               tn.nspname::text,
               tc.relname::text,
               ro.has_children,
               case when coalesce(ro.unanalyzed, true) then -1 else ro.tuples::bigint end,
               (ro.heap_pages * pg_catalog.current_setting('block_size')::bigint)::bigint,
               ((ro.heap_pages + ro.toast_pages + ro.index_pages) * pg_catalog.current_setting('block_size')::bigint)::bigint
        from resolved r
        join rolled ro on ro.root = r.table_oid
        join pg_catalog.pg_class tc on tc.oid = r.table_oid
        join pg_catalog.pg_namespace tn on tn.oid = tc.relnamespace
        order by r.ord
        """;

    /// <summary>
    /// How many sessions hold or wait for a lock on each table (or any of its partitions), by state - the lock table's
    /// <c>relation</c> locks in this database, counted by backend, never listed. This session's own backend is left out.
    /// </summary>
    internal const string SessionsSql =
        """
        with recursive roots as (
            select pg_catalog.unnest(@tables) as oid
        ),
        tree (root, relid) as (
            select r.oid, r.oid from roots r
            union all
            select tr.root, i.inhrelid
            from tree tr
            join pg_catalog.pg_inherits i on i.inhparent = tr.relid
        ),
        locked as (
            select tr.root,
                   l.pid,
                   l.virtualtransaction,
                   l.granted
            from tree tr
            join pg_catalog.pg_locks l
              on l.locktype = 'relation'
             and l.relation = tr.relid
             and l.database = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database())
            where l.pid is distinct from pg_catalog.pg_backend_pid()
        )
        select k.root,
               case
                   when k.pid is null then 'prepared transaction'
                   else coalesce(a.state, 'state not visible')
               end::text,
               k.granted,
               pg_catalog.count(distinct coalesce(k.pid::text, k.virtualtransaction))::int
        from locked k
        left join pg_catalog.pg_stat_activity a on a.pid = k.pid
        group by 1, 2, 3
        order by 1, 3 desc, 2
        """;

    /// <summary>Reads the footprint of every relation in <paramref name="relations" />, in the order asked.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="transaction">The read-only transaction to run in.</param>
    /// <param name="relations">Schema and name of each relation the script names, unquoted and folded as Postgres folds them.</param>
    /// <param name="commandTimeoutSeconds">The command timeout.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<RelationFootprintRow>> ReadFootprintsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<(string Schema, string Name)> relations,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = new(FootprintSql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = relations.Select(static x => x.Schema).ToArray(),
        });
        command.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = relations.Select(static x => x.Name).ToArray(),
        });

        List<RelationFootprintRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RelationFootprintRow(
                reader.GetInt32(0) - 1,
                reader.GetFieldValue<uint>(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return rows;
    }

    /// <summary>Counts the sessions with a lock on each of <paramref name="tables" />, by state.</summary>
    /// <param name="connection">An open connection to the database in scope.</param>
    /// <param name="transaction">The read-only transaction to run in.</param>
    /// <param name="tables">The tables' <c>oid</c>s, from <see cref="ReadFootprintsAsync" />.</param>
    /// <param name="commandTimeoutSeconds">The command timeout.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<TableSessionRow>> ReadSessionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IReadOnlyList<uint> tables,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = new(SessionsSql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        command.Parameters.Add(new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Oid)
        {
            Value = tables.ToArray(),
        });

        List<TableSessionRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new TableSessionRow(
                reader.GetFieldValue<uint>(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetInt32(3)));
        }

        return rows;
    }
}
