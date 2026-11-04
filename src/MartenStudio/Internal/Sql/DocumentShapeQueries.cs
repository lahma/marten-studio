using System.Globalization;
using System.Text;

using JasperFx.MultiTenancy;

using Npgsql;

using NpgsqlTypes;

using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The statements of the documents' shape check (D-SHAPE S1, S3 and S4): a size probe, the keys and stored sizes of a bounded
/// sample, and then the chosen rows by key. Every identifier is quoted through <see cref="SqlIdentifier" />; every value is a
/// parameter; the only text chosen by the builder is from closed tables (the id column's type).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never <c>order by random()</c>, <c>tablesample</c>, or a sort on an unindexed column</b> (D8). The only index every Marten
/// document table has is its primary key, so the sample is chosen by it: the lowest and highest ids, and probes of the primary key
/// at PostgreSQL's own <c>pg_stats.histogram_bounds</c> of <c>id</c>, an equal-population split of the column in its own sort
/// order, read from the catalog for free. <b>The histogram never leaves the database</b>: under conjoined tenancy the bounds are
/// other tenants' ids too, so the bounds are chosen and probed inside one statement and only rows that pass the scope come back.
/// </para>
/// <para>
/// The scope is the documents list's own - the scope's tenant on a conjoined table (no tenant: every tenant of the database),
/// soft-deleted documents left out, and the subclass's <c>mt_doc_type</c> when the alias names one - built here from the same facts
/// rather than by calling the list's builder, so a change to the list's paging can never change what a check reads.
/// </para>
/// </remarks>
internal static class DocumentShapeQueries
{
    /// <summary>The part a key came from: the lowest ids.</summary>
    public const int PartLowest = 1;

    /// <summary>The part a key came from: the highest ids.</summary>
    public const int PartHighest = 2;

    /// <summary>The part a key came from: a probe at a histogram bound.</summary>
    public const int PartSpread = 3;

    private const string Alias = DocumentTableInfo.SqlAlias;

    private static readonly string IdColumn = Alias + "." + SqlIdentifier.Quote(DocumentTableInfo.IdColumn);
    private static readonly string DataColumn = Alias + "." + SqlIdentifier.Quote(DocumentTableInfo.DataColumn);

    /// <summary>
    /// Whether a spread branch can be written for <paramref name="table" />: the cast the histogram's bounds go through is chosen from
    /// a closed table keyed by the id column's type, and there is none for any other.
    /// </summary>
    /// <param name="table">The table.</param>
    public static bool CanSpread(DocumentTableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return IdCast(table.IdColumnType) is not null;
    }

    /// <summary>
    /// Whether the sample of <paramref name="table" /> in <paramref name="tenantId" />'s scope has a spread branch: the id column's type
    /// allows one, and it is not a conjoined table read for every tenant. Under conjoined tenancy the primary key is
    /// <c>(tenant_id, id)</c> by default, so a probe by <c>id</c> alone is not an index descent, and the histogram is every tenant's
    /// ids together; with all tenants in scope only the edges are read, in primary-key order.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="tenantId">The scope's tenant.</param>
    public static bool CanSpread(DocumentTableInfo table, string? tenantId) =>
        CanSpread(table) && !(tenantId is null && TenantColumn(table) is not null);

    /// <summary>
    /// The primary-key order of a conjoined table: <c>DocumentMapping.PrimaryKeyTenancyOrdering</c>, which the edges follow when no
    /// tenant is in scope so that both are index scans.
    /// </summary>
    public enum PrimaryKeyOrder
    {
        /// <summary>The primary key is <c>(tenant_id, id)</c>: Marten's default.</summary>
        TenantThenId,

        /// <summary>The primary key is <c>(id, tenant_id)</c>.</summary>
        IdThenTenant,
    }

    /// <summary>
    /// <c>select count(*) from (select 1 … limit @probe)</c>: at most <paramref name="probe" /> index entries, to tell a collection
    /// small enough to read whole from one that has to be sampled.
    /// </summary>
    public static NpgsqlCommand BuildSizeProbe(DocumentTableInfo table, string? tenantId, string? subclassAlias, int probe, int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);

        NpgsqlCommand command = New(commandTimeoutSeconds);

        try
        {
            ParameterBuilder parameters = new(command);
            string scope = Scope(table, tenantId, subclassAlias, parameters);

            command.CommandText =
                "select count(*)\n"
                + "from (\n"
                + "    select 1\n"
                + "    from " + table.QualifiedName + " as " + Alias + "\n"
                + "    where true\n"
                + scope
                + "    limit " + parameters.Add(NpgsqlDbType.Integer, probe, "probe") + "\n"
                + ") as x";

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The keys of a collection that fits the sample: all of them, in id order, with each one's stored size.
    /// </summary>
    public static NpgsqlCommand BuildCompleteKeys(
        DocumentTableInfo table,
        string? tenantId,
        string? subclassAlias,
        int sample,
        int commandTimeoutSeconds,
        PrimaryKeyOrder keyOrder = PrimaryKeyOrder.TenantThenId)
    {
        ArgumentNullException.ThrowIfNull(table);

        NpgsqlCommand command = New(commandTimeoutSeconds);

        try
        {
            ParameterBuilder parameters = new(command);
            string scope = Scope(table, tenantId, subclassAlias, parameters);

            command.CommandText =
                "select " + Keys(table, 0) + "\n"
                + "from " + table.QualifiedName + " as " + Alias + "\n"
                + "where true\n"
                + scope
                + "order by " + KeyOrdering(table, tenantId, keyOrder, descending: false) + "\n"
                + "limit " + parameters.Add(NpgsqlDbType.Integer, sample, "sample");

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The keys of a sampled collection, in one statement of up to three <c>union all</c> branches: the lowest ids, the highest, and
    /// - when <see cref="CanSpread(DocumentTableInfo, string?)" /> - 150 probes of the primary key at the histogram's bounds.
    /// </summary>
    public static NpgsqlCommand BuildSampledKeys(
        DocumentTableInfo table,
        string? tenantId,
        string? subclassAlias,
        int edge,
        int spread,
        int commandTimeoutSeconds,
        PrimaryKeyOrder keyOrder = PrimaryKeyOrder.TenantThenId)
    {
        ArgumentNullException.ThrowIfNull(table);

        NpgsqlCommand command = New(commandTimeoutSeconds);

        try
        {
            ParameterBuilder parameters = new(command);
            string scope = Scope(table, tenantId, subclassAlias, parameters);
            string from = "from " + table.QualifiedName + " as " + Alias + "\nwhere true\n" + scope;
            string edgeParameter = parameters.Add(NpgsqlDbType.Integer, edge, "edge");

            StringBuilder sql = new();

            sql.Append("(select ").Append(Keys(table, PartLowest)).Append('\n').Append(from)
                .Append("order by ").Append(KeyOrdering(table, tenantId, keyOrder, descending: false)).Append("\nlimit ").Append(edgeParameter).Append(")\n");
            sql.Append("union all\n");
            sql.Append("(select ").Append(Keys(table, PartHighest)).Append('\n').Append(from)
                .Append("order by ").Append(KeyOrdering(table, tenantId, keyOrder, descending: true)).Append("\nlimit ").Append(edgeParameter).Append(')');

            if (CanSpread(table, tenantId) && IdCast(table.IdColumnType) is { } cast)
            {
                string schema = parameters.Add(NpgsqlDbType.Text, table.Schema, "schema");
                string tableName = parameters.Add(NpgsqlDbType.Text, table.Table, "table");
                string idColumn = parameters.Add(NpgsqlDbType.Text, DocumentTableInfo.IdColumn, "idColumn");
                string spreadParameter = parameters.Add(NpgsqlDbType.Integer, spread, "spread");

                sql.Append("\nunion all\n");
                sql.Append("(with stats as (\n");
                sql.Append("    select st.histogram_bounds::text::text[] as b\n");
                sql.Append("    from pg_catalog.pg_stats as st\n");
                sql.Append("    where st.schemaname = ").Append(schema)
                    .Append(" and st.tablename = ").Append(tableName)
                    .Append(" and st.attname = ").Append(idColumn).Append('\n');
                sql.Append("    order by st.inherited desc\n");
                sql.Append("    limit 1\n");
                sql.Append(")\n");
                sql.Append("select p.id, p.tenant_id, p.stored_bytes, p.part\n");
                sql.Append("from stats\n");
                sql.Append("cross join lateral generate_series(0, ").Append(spreadParameter).Append(" - 1) as g(i)\n");
                sql.Append("cross join lateral (\n");
                sql.Append("    select ").Append(Keys(table, PartSpread)).Append('\n');
                sql.Append("    ").Append(from.Replace("\n", "\n    ", StringComparison.Ordinal).TrimEnd(' '));
                sql.Append("    and ").Append(IdColumn).Append(" >= (stats.b[1 + (g.i * (cardinality(stats.b) - 1)) / greatest(")
                    .Append(spreadParameter).Append(" - 1, 1)])::").Append(cast).Append('\n');
                sql.Append("    order by ").Append(IdColumn).Append('\n');
                sql.Append("    limit 1\n");
                sql.Append(") as p\n");
                sql.Append("where cardinality(stats.b) > 0)");
            }

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The chosen rows by key: the id, the document as text (blanked past <paramref name="maxItemBytes" />, which the server
    /// measures, not the keys' stored size), its size, and the discriminator and tenant where the table has them.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="tenantId">The scope's tenant.</param>
    /// <param name="subclassAlias">The subclass the alias names.</param>
    /// <param name="ids">The ids phase 1 read, as the clr values <c>reader.GetValue</c> returned.</param>
    /// <param name="tenants">Each id's tenant, parallel to <paramref name="ids" />; used when the table is conjoined and the scope has no tenant.</param>
    /// <param name="maxItemBytes">The largest document text returned.</param>
    /// <param name="commandTimeoutSeconds">The command timeout.</param>
    public static NpgsqlCommand BuildRows(
        DocumentTableInfo table,
        string? tenantId,
        string? subclassAlias,
        IReadOnlyList<object> ids,
        IReadOnlyList<string?> tenants,
        int maxItemBytes,
        int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(tenants);

        NpgsqlCommand command = New(commandTimeoutSeconds);

        try
        {
            ParameterBuilder parameters = new(command);
            string scope = Scope(table, tenantId, subclassAlias, parameters);

            string tenantColumn = TenantColumn(table) is { } name ? Alias + "." + SqlIdentifier.Quote(name) : "null::text";
            string typeColumn = table.MetadataColumnName(DocumentMetadataColumn.DocumentType) is { } docType
                ? Alias + "." + SqlIdentifier.Quote(docType)
                : "null::text";

            StringBuilder sql = new();

            sql.Append("select ").Append(IdExpression(table)).Append(" as id,\n");
            sql.Append("       case when octet_length(t.j) <= ").Append(parameters.Add(NpgsqlDbType.Integer, maxItemBytes, "maxBytes"))
                .Append(" then t.j end as stored_json,\n");
            sql.Append("       octet_length(t.j) as json_bytes,\n");
            sql.Append("       ").Append(typeColumn).Append(" as doc_type,\n");
            sql.Append("       ").Append(tenantColumn).Append(" as tenant_id\n");
            sql.Append("from ").Append(table.QualifiedName).Append(" as " + Alias).Append('\n');
            // "offset 0" keeps the planner from pulling the subquery up: inlined, d.data::text was computed three times a row
            // (the blanking case, its test and the size), and rendering a jsonb as text is the one costly thing this statement does.
            sql.Append("cross join lateral (select ").Append(DataColumn).Append("::text as j offset 0) as t\n");
            sql.Append("where true\n");
            sql.Append(scope);

            if (TenantColumn(table) is { } && tenantId is null)
            {
                // Conjoined, and no tenant in scope: a key is (tenant, id), because the same id exists once per tenant.
                sql.Append("  and (").Append(tenantColumn).Append(", ").Append(IdExpression(table)).Append(") in (select * from unnest(")
                    .Append(parameters.Add(NpgsqlDbType.Array | NpgsqlDbType.Varchar, tenants.Select(static x => x ?? string.Empty).ToArray(), "tenants"))
                    .Append(", ")
                    .Append(parameters.Add(IdArrayDbType(table.IdColumnType), IdArray(table.IdColumnType, ids), "ids"))
                    .Append("))\n");
            }
            else
            {
                sql.Append("  and ").Append(IdExpression(table)).Append(" = any(")
                    .Append(parameters.Add(IdArrayDbType(table.IdColumnType), IdArray(table.IdColumnType, ids), "ids"))
                    .Append(")\n");
            }

            sql.Append("order by ").Append(IdColumn);

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>The names of a command's parameters, spelled as its statement spells them.</summary>
    /// <param name="command">The command.</param>
    public static IReadOnlyList<string> ParameterNames(NpgsqlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return [.. command.Parameters.Select(static x => "@" + x.ParameterName)];
    }

    /// <summary>The Postgres type name the histogram's text bounds are cast to, from a closed table; <see langword="null" /> for any other column type.</summary>
    internal static string? IdCast(DocumentIdColumnType type) => type switch
    {
        DocumentIdColumnType.Uuid => "uuid",
        DocumentIdColumnType.Int4 => "integer",
        DocumentIdColumnType.Int8 => "bigint",
        DocumentIdColumnType.Text => "text",
        DocumentIdColumnType.Varchar => "varchar",
        _ => null,
    };

    private static NpgsqlCommand New(int commandTimeoutSeconds)
    {
        NpgsqlCommand command = new();

        if (commandTimeoutSeconds > 0)
        {
            command.CommandTimeout = commandTimeoutSeconds;
        }

        return command;
    }

    /// <summary>
    /// The scope's predicates, each on a line of its own beginning <c>and</c>; the tenant column is refused rather than left out, because
    /// leaving out a tenant predicate would read other tenants' documents.
    /// </summary>
    private static string Scope(DocumentTableInfo table, string? tenantId, string? subclassAlias, ParameterBuilder parameters)
    {
        StringBuilder sql = new();

        if (tenantId is not null && table.TenancyStyle == TenancyStyle.Conjoined)
        {
            string column = TenantColumn(table)
                ?? throw new InvalidOperationException($"'{table.Alias}' is conjoined-tenanted but has no tenant column the studio can filter on.");

            sql.Append("      and " + Alias + ".").Append(SqlIdentifier.Quote(column)).Append(" = ")
                .Append(parameters.Add(NpgsqlDbType.Varchar, tenantId, "tenant")).Append('\n');
        }

        if (table.SoftDeleteEnabled && table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted) is { } deleted)
        {
            sql.Append("      and " + Alias + ".").Append(SqlIdentifier.Quote(deleted)).Append(" = false\n");
        }

        if (subclassAlias is not null)
        {
            string column = table.MetadataColumnName(DocumentMetadataColumn.DocumentType)
                ?? throw new InvalidOperationException($"'{table.Alias}' is not a hierarchy, so it has no discriminator to filter on.");

            // Marten writes the discriminator lower-cased, and compares it the same way (the list's own rule).
            sql.Append("      and " + Alias + ".").Append(SqlIdentifier.Quote(column)).Append(" = ")
                .Append(parameters.Add(NpgsqlDbType.Varchar, subclassAlias.ToLowerInvariant(), "subclass")).Append('\n');
        }

        return sql.ToString();
    }

    /// <summary>The tenant column of a conjoined table; <see langword="null" /> when the table has none.</summary>
    private static string? TenantColumn(DocumentTableInfo table) =>
        table.TenancyStyle == TenancyStyle.Conjoined ? table.MetadataColumnName(DocumentMetadataColumn.TenantId) : null;

    /// <summary>
    /// The edges' <c>order by</c>: the id alone, except for a conjoined table read for every tenant, where it is the primary key's own
    /// columns in the primary key's own order, ascending or descending together, so each edge is an index scan.
    /// </summary>
    private static string KeyOrdering(DocumentTableInfo table, string? tenantId, PrimaryKeyOrder keyOrder, bool descending)
    {
        string direction = descending ? " desc" : " asc";

        if (tenantId is not null || TenantColumn(table) is not { } tenant)
        {
            return IdColumn + direction;
        }

        string tenantColumn = Alias + "." + SqlIdentifier.Quote(tenant);

        return keyOrder == PrimaryKeyOrder.IdThenTenant
            ? IdColumn + direction + ", " + tenantColumn + direction
            : tenantColumn + direction + ", " + IdColumn + direction;
    }

    private static string IdExpression(DocumentTableInfo table) =>
        table.IdColumnType == DocumentIdColumnType.Unknown ? IdColumn + "::text" : IdColumn;

    /// <summary>The key columns of one branch: id, tenant, stored size, and which part the key is from.</summary>
    private static string Keys(DocumentTableInfo table, int part)
    {
        string tenant = TenantColumn(table) is { } column ? Alias + "." + SqlIdentifier.Quote(column) : "null::text";

        return IdExpression(table) + " as id, "
            + tenant + " as tenant_id, "
            + "pg_catalog.pg_column_size(" + DataColumn + ")::bigint as stored_bytes, "
            + part.ToString(CultureInfo.InvariantCulture) + " as part";
    }

    private static NpgsqlDbType IdArrayDbType(DocumentIdColumnType type) =>
        NpgsqlDbType.Array | (type == DocumentIdColumnType.Unknown ? NpgsqlDbType.Text : DocumentIdColumnTypes.DbType(type));

    private static object IdArray(DocumentIdColumnType type, IReadOnlyList<object> ids) => type switch
    {
        DocumentIdColumnType.Uuid => ids.Cast<Guid>().ToArray(),
        DocumentIdColumnType.Int4 => ids.Cast<int>().ToArray(),
        DocumentIdColumnType.Int8 => ids.Cast<long>().ToArray(),
        _ => ids.Select(static x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? string.Empty).ToArray(),
    };

    /// <summary>Names and adds parameters: the ones this file names keep their names, because "Show SQL" shows them to a human.</summary>
    private sealed class ParameterBuilder(NpgsqlCommand command)
    {
        public string Add(NpgsqlDbType type, object value, string name)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
            return "@" + name;
        }
    }
}
