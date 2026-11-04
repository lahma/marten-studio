using System.Text;

using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>A foreign key as the catalog holds it, read again for the T15 what-if.</summary>
/// <param name="Schema">The referencing table's schema.</param>
/// <param name="Table">The referencing table.</param>
/// <param name="Constraint">The constraint's name.</param>
/// <param name="Kind"><c>relkind</c> of the referencing table.</param>
/// <param name="IsPartition">Whether the referencing table is a partition.</param>
/// <param name="Columns">The key's columns in key order.</param>
internal sealed record ForeignKeyLookupKey(string Schema, string Table, string Constraint, string Kind, bool IsPartition, IReadOnlyList<string> Columns);

/// <summary>
/// 1.2-INDEX-B: T15's "What if it had an index?" - the foreign key read again from the catalog, and the lookup Postgres runs for it,
/// as a statement the studio composes from those names alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key is read again, never taken from the finding's words.</b> <see cref="KeySql" /> finds the constraint by the finding's
/// subject (<c>schema.table.constraint</c>, held server-side in the visitor's own gated report) and reads its columns in key order
/// from <c>pg_catalog</c> alone; the subject is a parameter.
/// </para>
/// <para>
/// <b>No <c>FOR KEY SHARE</c>.</b> Postgres' own check statement is <c>SELECT 1 FROM [ONLY] t x WHERE $1 = col FOR KEY SHARE OF x</c>,
/// run as the table's owner. <c>EXPLAIN</c> checks the row-lock permission, so a <c>SqlConsoleRole</c> that may only <c>SELECT</c> - the
/// careful host's configuration - would be refused with <c>42501</c>. The lock clause adds a <c>LockRows</c> node on top and changes
/// no access path, so the scan compared is the one Postgres chooses. The statement carries every identifier quoted and one
/// placeholder per column; no value and nobody's text.
/// </para>
/// </remarks>
internal static class ForeignKeyLookupQueries
{
    /// <summary>The parameter the finding's subject travels in.</summary>
    internal const string SubjectParameter = "subject";

    /// <summary>
    /// The one foreign key a finding's subject names: its table, constraint, kind and columns in key order. Only the non-inherited
    /// constraint of a partitioned table (<c>conparentid = 0</c>), and nothing but <c>pg_catalog</c>.
    /// </summary>
    internal const string KeySql =
        """
        select n.nspname, c.relname, k.conname, c.relkind::pg_catalog.text, c.relispartition,
               array(select a.attname::pg_catalog.text
                     from pg_catalog.unnest(k.conkey) with ordinality as u(attnum, ord)
                     join pg_catalog.pg_attribute a on a.attrelid = k.conrelid and a.attnum = u.attnum
                     order by u.ord) as columns
        from pg_catalog.pg_constraint k
        join pg_catalog.pg_class c on c.oid = k.conrelid
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        where k.contype = 'f' and k.conparentid = 0
          and n.nspname || '.' || c.relname || '.' || k.conname = @subject
        """;

    /// <summary>
    /// <c>select 1 from only "s"."t" x where x."c1" = $1 and x."c2" = $2</c>, or <see langword="null" /> when a name cannot be
    /// printed honestly.
    /// </summary>
    /// <param name="schema">The referencing table's schema.</param>
    /// <param name="table">The referencing table.</param>
    /// <param name="columns">The key's columns, in key order.</param>
    public static string? Lookup(string schema, string table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        // CreateIndex quotes exactly the names a statement may carry and refuses the rest: the same test, in one place.
        if (columns.Count == 0 || MigrationSqlText.CreateIndex(schema, table, columns) is null)
        {
            return null;
        }

        StringBuilder text = new("select 1 from only ");
        text.Append(SqlIdentifier.Qualify(schema, table)).Append(" x where ");

        for (int i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                text.Append(" and ");
            }

            text.Append("x.").Append(SqlIdentifier.Quote(columns[i])).Append(" = $").Append((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>Reads <see cref="KeySql" /> in <paramref name="transaction" />: every row it matches (more or fewer than one is the caller's refusal).</summary>
    public static async Task<IReadOnlyList<ForeignKeyLookupKey>> ReadKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string subject,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(subject);

        await using NpgsqlCommand command = new(KeySql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter(SubjectParameter, NpgsqlDbType.Text) { Value = subject });

        List<ForeignKeyLookupKey> keys = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(new ForeignKeyLookupKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetFieldValue<string[]>(5)));
        }

        return keys;
    }
}
