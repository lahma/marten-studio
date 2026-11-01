using Npgsql;

using NpgsqlTypes;

using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>One Marten-named table or function whose name carries a dotless <c>ı</c> (U+0131), and whether its invariant twin exists (K09).</summary>
/// <param name="IsFunction"><see langword="true" /> for a function, <see langword="false" /> for a table.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name, as the catalog holds it.</param>
/// <param name="HasInvariantTwin">Whether an object of the same kind exists in the same schema with every <c>ı</c> replaced by <c>i</c>.</param>
internal sealed record DotlessNameRow(bool IsFunction, string Schema, string Name, bool HasInvariantTwin);

/// <summary>
/// The statement behind K09: the tables and functions in the store's schemas that Marten named under a Turkish or
/// Azerbaijani culture (<c>mt_doc_ınvoice</c>), which Marten 9.55 names <c>mt_doc_invoice</c> (marten#5778, #5783).
/// </summary>
/// <remarks>
/// <para>
/// <b>Locks nothing it lists</b> (D27): <c>pg_class</c>, <c>pg_proc</c> and <c>pg_namespace</c>, no size, definition or
/// partition function. Tables are the ordinary and partitioned ones that are not themselves a partition (a tenant's partition
/// follows its parent's name); functions are the four families Marten writes for a document type
/// (<see cref="FunctionPrefixes" />). Every name is a value, and so are the dotless letter, its ASCII twin and the schemas: no
/// identifier is in the text.
/// </para>
/// <para>
/// Read as the store's own role, on the catalogs Postgres lets every role read; a function the role cannot execute is still
/// listed by <c>pg_proc</c>.
/// </para>
/// </remarks>
internal static class DotlessNameQueries
{
    /// <summary>What Marten starts every table and function name with.</summary>
    internal const string Prefix = "mt_";

    /// <summary>LATIN SMALL LETTER DOTLESS I, which a Turkish or Azerbaijani culture makes of <c>I</c> when lowering.</summary>
    internal const string Dotless = "ı";

    /// <summary>The letter the invariant culture makes of <c>I</c>.</summary>
    internal const string Plain = "i";

    /// <summary>How many of each kind are read; a read that returns more says so.</summary>
    internal const int Limit = 500;

    /// <summary>The function families Marten writes per document type.</summary>
    internal static readonly string[] FunctionPrefixes = ["mt_upsert_", "mt_insert_", "mt_update_", "mt_overwrite_"];

    /// <summary>The tables and functions of <c>@schemas</c> named with <c>@dotless</c>, each with whether its twin exists.</summary>
    internal const string RowsSql =
        """
        (select false as is_function,
                n.nspname::text as schema_name,
                c.relname::text as object_name,
                exists (select 1
                        from pg_catalog.pg_class t
                        where t.relnamespace = c.relnamespace
                          and t.relkind in ('r', 'p')
                          and t.relname::text = pg_catalog.replace(c.relname::text, @dotless, @plain)) as has_twin
         from pg_catalog.pg_class c
         join pg_catalog.pg_namespace n on n.oid = c.relnamespace
         where n.nspname = any(@schemas)
           and c.relkind in ('r', 'p')
           and not c.relispartition
           and pg_catalog.starts_with(c.relname::text, @prefix)
           and pg_catalog.strpos(c.relname::text, @dotless) > 0
         order by 2, 3
         limit @limit)
        union all
        (select distinct true,
                n.nspname::text,
                p.proname::text,
                exists (select 1
                        from pg_catalog.pg_proc t
                        where t.pronamespace = p.pronamespace
                          and t.proname::text = pg_catalog.replace(p.proname::text, @dotless, @plain))
         from pg_catalog.pg_proc p
         join pg_catalog.pg_namespace n on n.oid = p.pronamespace
         where n.nspname = any(@schemas)
           and pg_catalog.strpos(p.proname::text, @dotless) > 0
           and exists (select 1 from pg_catalog.unnest(@functionPrefixes) as f(prefix) where pg_catalog.starts_with(p.proname::text, f.prefix))
         order by 2, 3
         limit @limit)
        order by 1, 2, 3
        """;

    /// <summary>The rows for <paramref name="schemas" /> (<see cref="RowsSql" />), at most <see cref="Limit" /> + 1 of each kind.</summary>
    public static HealthQuery Rows(IReadOnlyList<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        return new HealthQuery(
            RowsSql,
            [
                new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas as string[] ?? [.. schemas] },
                new NpgsqlParameter("prefix", NpgsqlDbType.Text) { Value = Prefix },
                new NpgsqlParameter("dotless", NpgsqlDbType.Text) { Value = Dotless },
                new NpgsqlParameter("plain", NpgsqlDbType.Text) { Value = Plain },
                new NpgsqlParameter("functionPrefixes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = FunctionPrefixes },
                new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = Limit + 1 },
            ]);
    }

    /// <summary>Maps one row of <see cref="RowsSql" />.</summary>
    public static DotlessNameRow ReadRow(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new DotlessNameRow(reader.GetBoolean(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
    }
}
