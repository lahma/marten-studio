using Npgsql;

using NpgsqlTypes;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

namespace MartenStudio.Internal.Sql;

/// <summary>One table named like a Marten document table in the store's schemas, and its estimated size (T17).</summary>
/// <param name="Schema">Its schema.</param>
/// <param name="Table">Its name - compared with every store's declarations in the gatherer, and never put in a finding.</param>
/// <param name="EstimatedBytes">Heap, TOAST and indexes, its partitions' included, from <c>relpages</c>.</param>
internal sealed record DocumentTableCandidateRow(string Schema, string Table, long EstimatedBytes);

/// <summary>
/// The statement behind T17: every ordinary or partitioned table in the store's own schemas named like a document table
/// (<c>mt_doc_</c>), with its estimated size - which the gatherer holds against every registered store's declarations.
/// </summary>
/// <remarks>
/// <b>Locks nothing it lists</b> (D27): <c>pg_class</c>, <c>pg_namespace</c> and <c>pg_inherits</c>, sizes from
/// <c>relpages</c>, never a size function (B0 §3: they queue behind a migration) and never <c>pg_partition_tree</c>. The
/// schemas and the prefix are values; no table's name is in the statement.
/// </remarks>
internal static class OrphanedDocumentTableQueries
{
    /// <summary>The document-table-named tables of <c>@schemas</c>, each with its estimated size.</summary>
    internal const string CandidatesSql =
        """
        with recursive candidates as (
            select c.oid, n.nspname, c.relname, c.relkind
            from pg_catalog.pg_namespace n
            join pg_catalog.pg_class c on c.relnamespace = n.oid
            where n.nspname = any(@schemas)
              and c.relkind in ('r', 'p')
              and not c.relispartition
              and pg_catalog.starts_with(pg_catalog.lower(c.relname::text), @prefix)
        ),
        tree (root, relid) as (
            select c.oid, c.oid
            from candidates c
            union all
            select t.root, i.inhrelid
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
        ),
        sized as (
            select t.root,
                   pg_catalog.sum(r.relpages::bigint
                                  + coalesce(toast.relpages, 0)::bigint
                                  + (select coalesce(pg_catalog.sum(ic.relpages::bigint), 0)
                                     from pg_catalog.pg_index x
                                     join pg_catalog.pg_class ic on ic.oid = x.indexrelid
                                     where x.indrelid = r.oid)) as pages
            from tree t
            join pg_catalog.pg_class r on r.oid = t.relid
            left join pg_catalog.pg_class toast on toast.oid = r.reltoastrelid
            group by t.root
        )
        select c.nspname::text,
               c.relname::text,
               coalesce(s.pages, 0) * pg_catalog.current_setting('block_size')::bigint
        from candidates c
        left join sized s on s.root = c.oid
        order by 1, 2
        """;

    /// <summary>The document-table-named tables of <paramref name="schemas" /> (<see cref="CandidatesSql" />).</summary>
    public static HealthQuery Candidates(IReadOnlyList<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        return new HealthQuery(
            CandidatesSql,
            [
                new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas as string[] ?? [.. schemas] },
                new NpgsqlParameter("prefix", NpgsqlDbType.Text) { Value = DatabaseObjectClassifier.DocumentTablePrefix },
            ]);
    }

    /// <summary>Maps one row of <see cref="CandidatesSql" />.</summary>
    public static DocumentTableCandidateRow ReadCandidate(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new DocumentTableCandidateRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
    }
}
