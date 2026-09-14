using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The reads the documents browser needs that are neither the list nor the single document: the grouped
/// row-count estimate behind the collections rail, the <c>_recent</c> pseudo-collection, and the two
/// sizes the metadata pane shows.
/// </summary>
/// <remarks>
/// They live here rather than in the data service because every one of them puts an identifier into SQL
/// text, and AGENTS.md hard rule 4 says there is exactly one directory where that may happen. Identifiers
/// are quoted through <see cref="SqlIdentifier"/>; every value is a parameter.
/// </remarks>
internal static class DocumentBrowseQueries
{
    /// <summary>One <c>mt_doc_*</c> table the database has, as <c>information_schema</c> reports it.</summary>
    /// <param name="Schema">The schema.</param>
    /// <param name="Name">The table name.</param>
    internal sealed record DocumentTableName(string Schema, string Name)
    {
        /// <summary>The alias the browser gives it: the table name with Marten's prefix taken off.</summary>
        public string Alias => Name.StartsWith(TablePrefix, StringComparison.OrdinalIgnoreCase)
            ? Name[TablePrefix.Length..]
            : Name;
    }

    /// <summary>The prefix Marten gives every document table.</summary>
    public const string TablePrefix = "mt_doc_";

    /// <summary>
    /// Lists the document tables of the given schemas, from <c>information_schema</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because <c>IMartenDatabase.DocumentTables()</c> is not a read.</b> It is
    /// <c>AllObjects().OfType&lt;DocumentTable&gt;()</c>, <c>AllObjects()</c> is
    /// <c>BuildFeatureSchemas().SelectMany(x =&gt; x.Objects)</c>, and Marten's feature set includes the
    /// lazy HiLo <c>Sequences</c> feature for any store with a numeric or HiLo id — whose factory blocks on
    /// <c>Migrator.ApplyAllAsync</c> under the database's own <c>AutoCreate</c>. A collections rail that
    /// called it created <c>mt_hilo</c> and <c>mt_get_next_hi</c> because somebody opened a tab, on a
    /// connection of Marten's own with no command timeout and no cancellation token (P7 proved this live;
    /// <c>SchemaNoDdlLiveTests</c> is the regression).
    /// </para>
    /// <para>
    /// The schemas come from <c>StoreOptions</c> — never from <c>AllSchemaNames()</c>, which is the same
    /// call by another name.
    /// </para>
    /// </remarks>
    internal const string DocumentTablesSql =
        """
        select table_schema, table_name
        from information_schema.tables
        where table_schema = any(@schemas)
          and table_type = 'BASE TABLE'
          and table_name like 'mt\_doc\_%'
        order by table_schema, table_name
        """;

    /// <summary>Every <c>mt_doc_*</c> table in the given schemas, in name order.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="schemas">The schemas the store declares; empty means no query is run.</param>
    /// <param name="commandTimeoutSeconds">How long the catalog read may take.</param>
    /// <param name="cancellationToken">The usual.</param>
    public static async Task<IReadOnlyList<DocumentTableName>> ListDocumentTablesAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);

        List<DocumentTableName> tables = [];

        if (schemas.Count == 0)
        {
            return tables;
        }

        await using var command = new NpgsqlCommand(DocumentTablesSql, connection)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = schemas.ToArray(),
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new DocumentTableName(reader.GetString(0), reader.GetString(1)));
        }

        return tables;
    }

    /// <summary>
    /// Every table's <c>reltuples</c> in one round trip.
    /// </summary>
    /// <remarks>
    /// One query for the whole rail, not one per collection: a store with forty document types would
    /// otherwise open the documents page with forty round trips, which is the shape of dashboard that
    /// makes people turn dashboards off. <c>-1</c> is Postgres for "never analysed" and becomes zero.
    /// </remarks>
    internal const string EstimateAllSql =
        """
        select n.nspname, c.relname, c.reltuples::bigint
        from pg_catalog.pg_class c
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        where n.nspname = any(@schemas) and c.relkind = 'r'
        """;

    /// <summary>
    /// Reads the raw <c>reltuples</c> of every table in the given schemas. <c>-1</c> is passed through
    /// rather than clamped, because "never analysed" and "empty" are different things and only the caller
    /// can decide whether an exact count is worth paying for.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, long>> EstimateAllAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> schemas,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);

        Dictionary<string, long> estimates = new(StringComparer.Ordinal);

        if (schemas.Count == 0)
        {
            return estimates;
        }

        await using var command = new NpgsqlCommand(EstimateAllSql, connection)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = schemas.ToArray(),
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            estimates[Key(reader.GetString(0), reader.GetString(1))] = reader.GetInt64(2);
        }

        return estimates;
    }

    /// <summary>The key <see cref="EstimateAllAsync"/> returns estimates under.</summary>
    public static string Key(string schema, string table) => schema + "." + table;

    /// <summary>
    /// The two sizes of one document: <c>octet_length(data::text)</c> and <c>pg_column_size(data)</c>.
    /// </summary>
    /// <remarks>
    /// There is no <c>octet_length(jsonb)</c> — it raises 42883 (Appendix B addendum) — so the text length
    /// goes through the cast. <c>pg_column_size</c> is the other number entirely: the on-disk width of the
    /// value, TOAST compression included, which is usually a fraction of the text length and is what a
    /// person asking "why is this table so big" actually wants.
    /// </remarks>
    /// <param name="table">The collection.</param>
    /// <param name="rawId">The id as typed.</param>
    /// <param name="tenantId">The tenant in scope, when the collection is conjoined.</param>
    /// <param name="commandTimeoutSeconds">The command timeout; zero leaves Npgsql's own default.</param>
    /// <param name="command">The read, when the id parsed.</param>
    /// <param name="error">Why it did not, otherwise.</param>
    public static bool TryBuildSizes(
        DocumentTableInfo table,
        string rawId,
        string? tenantId,
        int commandTimeoutSeconds,
        [NotNullWhen(true)] out NpgsqlCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(table);

        var parsed = DocumentQueryBuilder.ParseId(table.IdColumnType, rawId);

        if (!parsed.Success)
        {
            command = null;
            error = parsed.Error ?? "That id cannot be used against this collection.";
            return false;
        }

        var built = DocumentQueryBuilder.NewCommand(commandTimeoutSeconds);

        try
        {
            var data = SqlIdentifier.Quote(DocumentTableInfo.DataColumn);
            var sql = new StringBuilder();

            sql.Append("select octet_length(").Append(data).Append("::text) as text_bytes,\n");
            sql.Append("       pg_column_size(").Append(data).Append(") as stored_bytes\n");
            sql.Append("from ").Append(table.QualifiedName).Append('\n');
            sql.Append("where ").Append(SqlIdentifier.Quote(DocumentTableInfo.IdColumn)).Append(" = @id");

            built.Parameters.Add(new NpgsqlParameter("id", DocumentIdColumnTypes.DbType(table.IdColumnType))
            {
                Value = parsed.Value!,
            });

            if (tenantId is not null && table.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
            {
                var tenantColumn = table.MetadataColumnName(DocumentMetadataColumn.TenantId) ?? "tenant_id";

                sql.Append("\n  and ").Append(SqlIdentifier.Quote(tenantColumn)).Append(" = @tenant");
                built.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = tenantId });
            }

            built.CommandText = sql.ToString();
            command = built;
            error = null;
            return true;
        }
        catch
        {
            built.Dispose();
            throw;
        }
    }

    /// <summary>How many tables the <c>_recent</c> union will ever have branches for.</summary>
    public const int MaxRecentTables = 50;

    /// <summary>
    /// The <c>_recent</c> pseudo-collection: the most recently modified documents across every collection
    /// that keeps <c>mt_last_modified</c>. Returns <see langword="null"/> when no collection qualifies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bounded <c>union all</c> of per-table <c>order by mt_last_modified desc limit n</c> queries, not
    /// one query over a joined view: each branch can use that table's own index and reads at most
    /// <paramref name="perTable"/> rows, so the total work is bounded by the number of tables rather than
    /// by the size of the store. The table list is capped at <see cref="MaxRecentTables"/> for the same
    /// reason — a store with three hundred document types must not produce a three-hundred-branch union.
    /// </para>
    /// <para>
    /// A collection with <c>DisableInformationalFields()</c> has no <c>mt_last_modified</c> at all and is
    /// simply absent: there is no honest way to say when one of its documents last changed.
    /// </para>
    /// </remarks>
    /// <param name="tables">The collections to consider, in rail order.</param>
    /// <param name="tenantId">The tenant to filter conjoined collections by, or <see langword="null"/>.</param>
    /// <param name="perTable">How many rows each branch reads.</param>
    /// <param name="total">How many rows the whole union returns.</param>
    /// <param name="commandTimeoutSeconds">The command timeout; zero leaves Npgsql's own default.</param>
    public static NpgsqlCommand? BuildRecent(
        IReadOnlyList<DocumentTableInfo> tables,
        string? tenantId,
        int perTable,
        int total,
        int commandTimeoutSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var command = DocumentQueryBuilder.NewCommand(commandTimeoutSeconds);

        try
        {
            var sql = new StringBuilder();
            var branches = 0;

            foreach (var table in tables)
            {
                if (branches >= MaxRecentTables)
                {
                    break;
                }

                var lastModified = table.MetadataColumnName(DocumentMetadataColumn.LastModified);

                if (lastModified is null)
                {
                    continue;
                }

                if (branches > 0)
                {
                    sql.Append("\nunion all\n");
                }

                var aliasParameter = "a" + branches.ToString(CultureInfo.InvariantCulture);

                command.Parameters.Add(new NpgsqlParameter(aliasParameter, NpgsqlDbType.Text) { Value = table.Alias });

                sql.Append("(select @").Append(aliasParameter).Append(" as alias, ")
                    .Append(SqlIdentifier.Quote(DocumentTableInfo.IdColumn)).Append("::text as id, ")
                    .Append(SqlIdentifier.Quote(lastModified)).Append(" as last_modified\n");
                sql.Append(" from ").Append(table.QualifiedName).Append('\n');
                sql.Append(" where 1 = 1\n");

                if (table.SoftDeleteEnabled &&
                    table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted) is { } deleted)
                {
                    sql.Append("   and ").Append(SqlIdentifier.Quote(deleted)).Append(" = false\n");
                }

                if (tenantId is not null && table.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined &&
                    table.MetadataColumnName(DocumentMetadataColumn.TenantId) is { } tenantColumn)
                {
                    var tenantParameter = "t" + branches.ToString(CultureInfo.InvariantCulture);

                    command.Parameters.Add(new NpgsqlParameter(tenantParameter, NpgsqlDbType.Varchar) { Value = tenantId });
                    sql.Append("   and ").Append(SqlIdentifier.Quote(tenantColumn)).Append(" = @").Append(tenantParameter).Append('\n');
                }

                sql.Append(" order by ").Append(SqlIdentifier.Quote(lastModified)).Append(" desc\n");
                sql.Append(" limit @perTable)");

                branches++;
            }

            if (branches == 0)
            {
                command.Dispose();
                return null;
            }

            sql.Append("\norder by last_modified desc\nlimit @total");

            command.Parameters.Add(new NpgsqlParameter("perTable", NpgsqlDbType.Integer) { Value = Math.Clamp(perTable, 1, 200) });
            command.Parameters.Add(new NpgsqlParameter("total", NpgsqlDbType.Integer) { Value = Math.Clamp(total, 1, 500) });

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }
}
