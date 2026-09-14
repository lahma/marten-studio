using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

using MartenStudio.Services.Query;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The outcome of turning a typed-in id into the CLR value its column expects.
/// </summary>
/// <remarks>
/// A failure here is an ordinary thing — somebody pasted half a GUID into the address bar — and the page
/// has to be able to say so. It is therefore a value and never an exception (plan §4.4).
/// </remarks>
/// <param name="Success">Whether the id parsed.</param>
/// <param name="Value">The value to bind, when it did.</param>
/// <param name="Error">Why it did not, phrased for the person who typed it.</param>
internal readonly record struct IdParseResult(bool Success, object? Value, string? Error)
{
    /// <summary>A parsed id.</summary>
    public static IdParseResult Ok(object value) => new(true, value, null);

    /// <summary>An id that could not be parsed against its column type.</summary>
    public static IdParseResult Failed(string error) => new(false, null, error);
}

/// <summary>Whether a single-document read locks the row it reads.</summary>
internal enum DocumentRowLock
{
    /// <summary>A plain read. Every page uses this.</summary>
    None,

    /// <summary>
    /// <c>for update</c>: the row stays as it was read until the reading transaction ends. Only the
    /// write service uses this, and only inside the transaction that is about to write.
    /// </summary>
    ForUpdate,
}

/// <summary>
/// Builds the two document reads: the list and the single document. Every identifier is quoted, every
/// value is a parameter, and every sort key and column comes from the allow-list that
/// <see cref="DocumentColumn"/> is (AGENTS.md hard rule 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is interpolated.</b> Quoted identifiers, and keywords the builder itself chooses (<c>desc</c>,
/// <c>nulls last</c>, the comparison operators, the <c>::numeric</c>-style casts). Nothing else. A JSON
/// path is never written into the SQL: it travels as a <c>text[]</c> parameter to <c>#&gt;&gt;</c>, which
/// is what makes a property called <c>'); drop table</c> a property name rather than an incident.
/// </para>
/// <para>
/// <b>Why the id tiebreaker is mandatory.</b> <c>order by mt_last_modified desc</c> has no total order, so
/// two pages of a keyset walk can repeat a row and skip another. Every sort therefore ends in
/// <c>, id</c> and every cursor carries both halves.
/// </para>
/// <para>
/// <b>Errors.</b> A request the builder cannot honour — a sort key the table has no column for, an offset
/// past <see cref="MaxOffset"/>, <c>is:deleted</c> on a collection with no soft delete, a filter on a
/// malformed id — throws <see cref="ArgumentException"/>, because it is a request that should not have
/// been assembled. The one exception is the single-document id, which is typed by a human and therefore
/// comes back as an <see cref="IdParseResult"/>.
/// </para>
/// </remarks>
internal static class DocumentQueryBuilder
{
    /// <summary>
    /// The largest offset the builder will emit. Beyond this Postgres is counting past ten thousand rows
    /// to throw them away, and the page must use the keyset cursor instead.
    /// </summary>
    public const int MaxOffset = 10_000;

    private const string Alias = DocumentTableInfo.SqlAlias;
    private const string AsAlias = " as " + Alias;

    private static readonly string DataRef = Alias + "." + SqlIdentifier.Quote(DocumentTableInfo.DataColumn);
    private static readonly string IdRef = Alias + "." + SqlIdentifier.Quote(DocumentTableInfo.IdColumn);

    /// <summary>Builds the document list query.</summary>
    public static NpgsqlCommand BuildList(DocumentTableInfo table, DocumentListQuery query)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);

        if (query.Offset < 0)
        {
            throw new ArgumentException("An offset cannot be negative.", nameof(query));
        }

        if (query.Offset > MaxOffset)
        {
            throw new ArgumentException(
                $"An offset of {query.Offset.ToString(CultureInfo.InvariantCulture)} is past the " +
                $"{MaxOffset.ToString(CultureInfo.InvariantCulture)} the studio will page to. Use the keyset cursor.",
                nameof(query));
        }

        var command = new NpgsqlCommand();

        try
        {
            var builder = new ParameterBuilder(command);
            var sql = new StringBuilder();

            AppendListSelect(sql, table, query, builder);

            sql.Append("from ").Append(table.QualifiedName).Append(AsAlias).Append('\n');

            AppendWhere(sql, table, query, builder);
            AppendOrderAndPaging(sql, table, query, builder);

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
    /// Builds the single-document read. Returns <see langword="false"/> with a message when the id does not
    /// parse against its column type, which is a thing a link can say and not a thing to throw about.
    /// </summary>
    /// <remarks>
    /// The single read has no soft-delete predicate, deliberately: a document page reached by a link has
    /// to be able to show a deleted row (and to undelete it), and the tri-state that hides them belongs
    /// to the list.
    /// </remarks>
    public static bool TryBuildSingle(
        DocumentTableInfo table,
        string rawId,
        string? tenantId,
        [NotNullWhen(true)] out NpgsqlCommand? command,
        [NotNullWhen(false)] out string? error) =>
        TryBuildSingle(table, rawId, tenantId, DocumentRowLock.None, out command, out error);

    /// <summary>
    /// The same read, with the option of taking a row lock — which is what makes a write's concurrency
    /// check an actual check rather than a hopeful one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DocumentRowLock.ForUpdate"/> appends <c>for update</c>, so the row the studio just read
    /// the version of cannot be changed by anybody else until the transaction that read it ends. Without
    /// it, read committed lets another session commit between the studio's version check and its upsert,
    /// and the studio overwrites an edit it told the user about — the lost update this check exists to
    /// prevent.
    /// </para>
    /// <para>
    /// Only ever used inside a Marten session's own transaction. A <c>for update</c> outside one takes a
    /// lock and drops it on the next statement, which costs a lock and buys nothing.
    /// </para>
    /// </remarks>
    public static bool TryBuildSingle(
        DocumentTableInfo table,
        string rawId,
        string? tenantId,
        DocumentRowLock rowLock,
        [NotNullWhen(true)] out NpgsqlCommand? command,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(table);

        var parsed = ParseId(table.IdColumnType, rawId);

        if (!parsed.Success)
        {
            command = null;
            error = parsed.Error ?? "That id cannot be used against this collection.";
            return false;
        }

        var built = new NpgsqlCommand();

        try
        {
            var builder = new ParameterBuilder(built);
            var sql = new StringBuilder();

            sql.Append("select ").Append(DataRef).Append("::text as data,\n");
            sql.Append("       octet_length(").Append(DataRef).Append("::text) as data_bytes");

            foreach (var metadata in table.MetadataColumns)
            {
                sql.Append(",\n       ").Append(Column(metadata.ColumnName));
            }

            // The duplicated columns as well as the metadata, which plan §3.2's "duplicated-field agreement
            // dots" need: the detail page compares each column with the value inside the JSON and shows
            // where the two have drifted, which it cannot do without reading both.
            foreach (var duplicated in table.DuplicatedColumns)
            {
                sql.Append(",\n       ").Append(Column(duplicated.ColumnName));
            }

            sql.Append('\n');
            sql.Append("from ").Append(table.QualifiedName).Append(AsAlias).Append('\n');
            sql.Append("where ").Append(IdRef).Append(" = ")
                .Append(builder.Add(DocumentIdColumnTypes.DbType(table.IdColumnType), parsed.Value!, "id"))
                .Append('\n');

            AppendTenantFilter(sql, table, tenantId, builder);

            if (rowLock == DocumentRowLock.ForUpdate)
            {
                sql.Append("for update\n");
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

    /// <summary>
    /// Parses an id against the type of the column it will be compared with — the column type, not the CLR
    /// <c>IdType</c>, so strong-typed ids, <c>UseIdentityKey</c> and F# unions all work without unwrapping.
    /// </summary>
    public static IdParseResult ParseId(DocumentIdColumnType columnType, string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return IdParseResult.Failed("An id is required.");
        }

        var trimmed = rawId.Trim();

        switch (columnType)
        {
            case DocumentIdColumnType.Uuid:
                return Guid.TryParse(trimmed, CultureInfo.InvariantCulture, out var guid)
                    ? IdParseResult.Ok(guid)
                    : IdParseResult.Failed($"'{trimmed}' is not a GUID, and this collection's id column is uuid.");

            case DocumentIdColumnType.Int4:
                return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? IdParseResult.Ok(i)
                    : IdParseResult.Failed($"'{trimmed}' is not a 32-bit integer, and this collection's id column is int4.");

            case DocumentIdColumnType.Int8:
                return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                    ? IdParseResult.Ok(l)
                    : IdParseResult.Failed($"'{trimmed}' is not a 64-bit integer, and this collection's id column is int8.");

            default:
                // text, varchar and anything the studio could not identify: the id is whatever was typed,
                // and Postgres decides whether it fits.
                return IdParseResult.Ok(trimmed);
        }
    }

    private static void AppendListSelect(
        StringBuilder sql,
        DocumentTableInfo table,
        DocumentListQuery query,
        ParameterBuilder builder)
    {
        var maxInline = builder.Add(NpgsqlDbType.Integer, Math.Max(query.MaxInlineDocumentBytes, 0), "maxInline");

        sql.Append("select ").Append(IdRef).Append(",\n");
        sql.Append("       case when octet_length(").Append(DataRef).Append("::text) <= ").Append(maxInline)
            .Append(" then ").Append(DataRef).Append("::text end as data,\n");
        sql.Append("       octet_length(").Append(DataRef).Append("::text) as data_bytes");

        var jsonColumn = 0;

        foreach (var column in SelectedColumns(table, query))
        {
            switch (column)
            {
                case DocumentColumn.Id:
                    // Already the first column of every list.
                    break;

                case DocumentColumn.Metadata metadata:
                    sql.Append(",\n       ").Append(Column(MetadataColumnName(table, metadata.Column)));
                    break;

                case DocumentColumn.Duplicated duplicated:
                    sql.Append(",\n       ").Append(Column(DuplicatedColumnName(table, duplicated.ColumnName)));
                    break;

                case DocumentColumn.JsonPath path:
                    sql.Append(",\n       (").Append(DataRef).Append(" #>> ").Append(JsonPathParameter(builder, path.Path))
                        .Append(") as json_").Append(jsonColumn.ToString(CultureInfo.InvariantCulture));
                    jsonColumn++;
                    break;

                default:
                    throw new ArgumentException($"{column.GetType().Name} is not a column the studio can select.", nameof(query));
            }
        }

        sql.Append('\n');
    }

    private static IEnumerable<DocumentColumn> SelectedColumns(DocumentTableInfo table, DocumentListQuery query)
    {
        if (query.Columns.Count > 0)
        {
            return query.Columns;
        }

        // The default view: every metadata column the store kept, and every duplicated field.
        List<DocumentColumn> columns = [];

        foreach (var metadata in table.MetadataColumns)
        {
            columns.Add(new DocumentColumn.Metadata(metadata.Column));
        }

        foreach (var duplicated in table.DuplicatedColumns)
        {
            columns.Add(new DocumentColumn.Duplicated(duplicated.ColumnName));
        }

        return columns;
    }

    private static void AppendWhere(
        StringBuilder sql,
        DocumentTableInfo table,
        DocumentListQuery query,
        ParameterBuilder builder)
    {
        // `where 1 = 1` so that every filter below is an `and`, and the shape of the SQL does not change
        // with the number of terms - which is what makes the "Show SQL" disclosure readable.
        sql.Append("where 1 = 1\n");

        AppendTenantFilter(sql, table, query.TenantId, builder);

        // An explicit `is:deleted` in the search wins over the tri-state. Emitting both would produce
        // `mt_deleted = false and mt_deleted = true`, which is a page that is always empty and never says
        // why - and the search box is the more specific of the two things the user touched.
        var statedExplicitly = false;

        foreach (var predicate in query.Predicates)
        {
            statedExplicitly |= predicate is DocumentPredicate.IsDeleted;
        }

        if (!table.SoftDeleteEnabled && query.IncludeDeleted == DeletedFilter.Only)
        {
            throw new ArgumentException(
                $"'{table.Alias}' is not soft-deleted, so it has no deleted documents to show.", nameof(query));
        }

        if (table.SoftDeleteEnabled && !statedExplicitly)
        {
            var deleted = MetadataColumnName(table, DocumentMetadataColumn.IsSoftDeleted);

            switch (query.IncludeDeleted)
            {
                case DeletedFilter.Exclude:
                    sql.Append("  and ").Append(Column(deleted)).Append(" = false\n");
                    break;
                case DeletedFilter.Only:
                    sql.Append("  and ").Append(Column(deleted)).Append(" = true\n");
                    break;
                default:
                    break;
            }
        }

        foreach (var predicate in query.Predicates)
        {
            sql.Append("  and ").Append(Predicate(table, predicate, builder)).Append('\n');
        }
    }

    private static void AppendTenantFilter(
        StringBuilder sql,
        DocumentTableInfo table,
        string? tenantId,
        ParameterBuilder builder)
    {
        // A single-tenant collection is not filtered by the scope's tenant: there is nothing to filter on,
        // and refusing to show it would hide a collection that is genuinely shared.
        if (tenantId is null || table.TenancyStyle != JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            return;
        }

        var column = MetadataColumnName(table, DocumentMetadataColumn.TenantId);

        sql.Append("  and ").Append(Column(column)).Append(" = ")
            .Append(builder.Add(NpgsqlDbType.Varchar, tenantId, "tenant")).Append('\n');
    }

    private static void AppendOrderAndPaging(
        StringBuilder sql,
        DocumentTableInfo table,
        DocumentListQuery query,
        ParameterBuilder builder)
    {
        var descending = query.Direction == SortDirection.Descending;
        var sortIsId = query.Sort is DocumentColumn.Id;
        var sortExpression = sortIsId ? IdRef : SortExpression(table, query.Sort, builder);

        if (query.Cursor is { } cursor)
        {
            sql.Append("  and ").Append(Keyset(table, query, cursor, sortExpression, sortIsId, descending, builder))
                .Append('\n');
        }

        sql.Append("order by ").Append(sortExpression);

        if (descending)
        {
            sql.Append(" desc");
        }

        if (!sortIsId)
        {
            sql.Append(" nulls last, ").Append(IdRef);
        }

        sql.Append('\n');
        sql.Append("limit ").Append(builder.Add(
            NpgsqlDbType.Integer, Math.Clamp(query.PageSize, 1, DocumentListQuery.MaxPageSize), "limit"));

        if (query.Cursor is null)
        {
            sql.Append(" offset ").Append(builder.Add(NpgsqlDbType.Integer, query.Offset, "offset"));
        }
    }

    private static string Keyset(
        DocumentTableInfo table,
        DocumentListQuery query,
        DocumentKeysetCursor cursor,
        string sortExpression,
        bool sortIsId,
        bool descending,
        ParameterBuilder builder)
    {
        var lastId = ParseId(table.IdColumnType, cursor.LastId);

        if (!lastId.Success)
        {
            throw new ArgumentException($"The page cursor's id is not usable: {lastId.Error}", nameof(query));
        }

        var idParameter = builder.Add(DocumentIdColumnTypes.DbType(table.IdColumnType), lastId.Value!, "i");
        var after = descending ? "<" : ">";

        if (sortIsId)
        {
            return sortExpression + " " + after + " " + idParameter;
        }

        // The sort column may be null, and `(a, b) > (x, y)` is null the moment a is - which would drop
        // every null-sorted row out of the walk instead of putting it at the end where `nulls last` says it
        // goes. So the comparison is written out, in the one form that matches the order it pages through.
        if (cursor.SortValue is null)
        {
            return "(" + sortExpression + " is null and " + IdRef + " > " + idParameter + ")";
        }

        var sortParameter = builder.Add(
            SortDbType(table, query.Sort),
            ConvertCursorValue(table, query.Sort, cursor.SortValue),
            "k");

        return "(" + sortExpression + " " + after + " " + sortParameter +
               " or " + sortExpression + " is null" +
               " or (" + sortExpression + " = " + sortParameter + " and " + IdRef + " > " + idParameter + "))";
    }

    private static string Predicate(DocumentTableInfo table, DocumentPredicate predicate, ParameterBuilder builder)
    {
        switch (predicate)
        {
            case DocumentPredicate.IdEquals id:
            {
                var parsed = ParseId(table.IdColumnType, id.Id);

                if (!parsed.Success)
                {
                    throw new ArgumentException(parsed.Error, nameof(predicate));
                }

                return IdRef + " = " + builder.Add(DocumentIdColumnTypes.DbType(table.IdColumnType), parsed.Value!);
            }

            case DocumentPredicate.Tenant tenant:
            {
                if (table.TenancyStyle != JasperFx.MultiTenancy.TenancyStyle.Conjoined)
                {
                    throw new ArgumentException(
                        $"'{table.Alias}' is not conjoined-tenanted, so it has no tenant_id to filter on.",
                        nameof(predicate));
                }

                return Column(MetadataColumnName(table, DocumentMetadataColumn.TenantId)) + " = " +
                       builder.Add(NpgsqlDbType.Varchar, tenant.TenantId);
            }

            case DocumentPredicate.IsDeleted deleted:
            {
                if (!table.SoftDeleteEnabled)
                {
                    throw new ArgumentException(
                        $"'{table.Alias}' is not soft-deleted, so it has no mt_deleted column to filter on.",
                        nameof(predicate));
                }

                return Column(MetadataColumnName(table, DocumentMetadataColumn.IsSoftDeleted)) + " = " +
                       builder.Add(NpgsqlDbType.Boolean, deleted.Value);
            }

            case DocumentPredicate.SubclassIs subclass:
            {
                var column = table.MetadataColumnName(DocumentMetadataColumn.DocumentType)
                    ?? throw new ArgumentException(
                        $"'{table.Alias}' is not a hierarchy, so it has no mt_doc_type column to filter on.",
                        nameof(predicate));

                // Marten writes the discriminator lower-cased, and compares it the same way.
                return Column(column) + " = " +
                       builder.Add(NpgsqlDbType.Varchar, subclass.Alias.ToLowerInvariant());
            }

            case DocumentPredicate.Contains contains:
                return DataRef + " @> " + builder.Add(NpgsqlDbType.Text, contains.Json) + "::jsonb";

            case DocumentPredicate.FreeText free:
                return DataRef + "::text ilike " + builder.Add(NpgsqlDbType.Text, LikePattern(free.Text));

            case DocumentPredicate.FieldLike like:
            {
                var duplicated = table.FindDuplicated(like.Path);
                var target = duplicated is not null && IsTextual(duplicated.DbType)
                    ? Column(duplicated.ColumnName)
                    : "(" + DataRef + " #>> " + JsonPathParameter(builder, like.Path) + ")";

                return target + " ilike " + builder.Add(NpgsqlDbType.Text, LikePattern(like.Text));
            }

            case DocumentPredicate.FieldCompare compare:
                return Comparison(table, compare, builder);

            default:
                throw new ArgumentException(
                    $"{predicate.GetType().Name} is not a predicate the studio can build SQL for.", nameof(predicate));
        }
    }

    private static string Comparison(DocumentTableInfo table, DocumentPredicate.FieldCompare compare, ParameterBuilder builder)
    {
        var duplicated = table.FindDuplicated(compare.Path);

        if (compare.Value is SearchValue.Null)
        {
            var nullTarget = duplicated is not null
                ? Column(duplicated.ColumnName)
                : "(" + DataRef + " #>> " + JsonPathParameter(builder, compare.Path) + ")";

            return nullTarget + (compare.Operator == ComparisonOperator.NotEqual ? " is not null" : " is null");
        }

        // The duplicated column is the whole point of duplicating a field: it is the one form of this
        // comparison an index can serve. It is only usable when the literal converts to the column's type -
        // `age > "old"` against an int4 column falls back to the JSON path rather than failing at Postgres.
        if (duplicated is not null && TryConvert(duplicated.DbType, compare.Value, out var converted))
        {
            return Column(duplicated.ColumnName) + " " + Operator(compare.Operator) + " " +
                   builder.Add(duplicated.DbType, converted!);
        }

        var expression = "(" + DataRef + " #>> " + JsonPathParameter(builder, compare.Path) + ")";

        return compare.Value switch
        {
            SearchValue.Number number => expression + "::numeric " + Operator(compare.Operator) + " " +
                                         builder.Add(NpgsqlDbType.Numeric, number.Value),
            SearchValue.Timestamp timestamp => expression + "::timestamptz " + Operator(compare.Operator) + " " +
                                               builder.Add(NpgsqlDbType.TimestampTz, timestamp.Value),
            SearchValue.Boolean boolean => expression + "::boolean " + Operator(compare.Operator) + " " +
                                           builder.Add(NpgsqlDbType.Boolean, boolean.Value),
            SearchValue.Text text => expression + " " + Operator(compare.Operator) + " " +
                                     builder.Add(NpgsqlDbType.Text, text.Value),
            _ => throw new ArgumentException($"{compare.Value.GetType().Name} is not a value the studio can compare."),
        };
    }

    private static string SortExpression(DocumentTableInfo table, DocumentColumn sort, ParameterBuilder builder) =>
        sort switch
        {
            DocumentColumn.Id => IdRef,
            DocumentColumn.Metadata metadata => Column(MetadataColumnName(table, metadata.Column)),
            DocumentColumn.Duplicated duplicated => Column(DuplicatedColumnName(table, duplicated.ColumnName)),
            DocumentColumn.JsonPath path => "(" + DataRef + " #>> " + JsonPathParameter(builder, path.Path) + ")",
            _ => throw new ArgumentException($"{sort.GetType().Name} is not a sort key the studio allows.", nameof(sort)),
        };

    private static NpgsqlDbType SortDbType(DocumentTableInfo table, DocumentColumn sort) => sort switch
    {
        DocumentColumn.Id => DocumentIdColumnTypes.DbType(table.IdColumnType),
        DocumentColumn.Metadata metadata => DocumentTableInfo.MetadataDbType(metadata.Column),
        DocumentColumn.Duplicated duplicated => Duplicated(table, duplicated.ColumnName).DbType,
        _ => NpgsqlDbType.Text,
    };

    private static object ConvertCursorValue(DocumentTableInfo table, DocumentColumn sort, string value)
    {
        var dbType = SortDbType(table, sort);

        if (TryConvert(dbType, new SearchValue.Text(value), out var converted))
        {
            return converted!;
        }

        throw new ArgumentException($"The page cursor's sort value '{value}' does not fit the column it orders by.");
    }

    private static string MetadataColumnName(DocumentTableInfo table, DocumentMetadataColumn column) =>
        table.MetadataColumnName(column)
        ?? throw new ArgumentException(
            $"'{table.Alias}' has no {column} metadata column: the store has it disabled.", nameof(column));

    private static string DuplicatedColumnName(DocumentTableInfo table, string columnName) =>
        Duplicated(table, columnName).ColumnName;

    private static DuplicatedColumnInfo Duplicated(DocumentTableInfo table, string columnName)
    {
        foreach (var candidate in table.DuplicatedColumns)
        {
            if (string.Equals(candidate.ColumnName, columnName, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new ArgumentException($"'{table.Alias}' has no duplicated column named '{columnName}'.", nameof(columnName));
    }

    private static string Column(string name) => Alias + "." + SqlIdentifier.Quote(name);

    private static string JsonPathParameter(ParameterBuilder builder, IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("A JSON path needs at least one segment.", nameof(path));
        }

        return builder.Add(NpgsqlDbType.Array | NpgsqlDbType.Text, path.ToArray());
    }

    private static string Operator(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => "=",
        ComparisonOperator.NotEqual => "<>",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        _ => throw new ArgumentException($"{op} is not a comparison the studio emits.", nameof(op)),
    };

    /// <summary>Escapes the LIKE metacharacters, so a search for <c>50%</c> looks for a percent sign.</summary>
    internal static string LikePattern(string text)
    {
        var escaped = new StringBuilder(text.Length + 2);

        escaped.Append('%');

        foreach (var c in text)
        {
            if (c is '\\' or '%' or '_')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.Append('%').ToString();
    }

    private static bool IsTextual(NpgsqlDbType type) =>
        type is NpgsqlDbType.Text or NpgsqlDbType.Varchar or NpgsqlDbType.Char or NpgsqlDbType.Citext;

    /// <summary>
    /// Converts a search literal to the CLR type a duplicated column's parameter needs, or says it cannot.
    /// Saying it cannot is a normal outcome: the caller then compares through the JSON path instead.
    /// </summary>
    internal static bool TryConvert(NpgsqlDbType dbType, SearchValue value, out object? converted)
    {
        converted = null;

        try
        {
            converted = value switch
            {
                SearchValue.Text text => FromText(dbType, text.Value),
                SearchValue.Number number => FromNumber(dbType, number.Value),
                SearchValue.Boolean boolean => dbType == NpgsqlDbType.Boolean ? boolean.Value : null,
                SearchValue.Timestamp timestamp => FromTimestamp(dbType, timestamp.Value),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            converted = null;
        }

        return converted is not null;
    }

    private static object? FromText(NpgsqlDbType dbType, string value) => dbType switch
    {
        NpgsqlDbType.Text or NpgsqlDbType.Varchar or NpgsqlDbType.Char or NpgsqlDbType.Citext => value,
        NpgsqlDbType.Uuid => Guid.TryParse(value, CultureInfo.InvariantCulture, out var guid) ? guid : null,
        NpgsqlDbType.Boolean => bool.TryParse(value, out var boolean) ? boolean : null,
        NpgsqlDbType.Smallint => short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null,
        NpgsqlDbType.Integer => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
        NpgsqlDbType.Bigint => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
        NpgsqlDbType.Numeric or NpgsqlDbType.Money =>
            decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        NpgsqlDbType.Real => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null,
        NpgsqlDbType.Double => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var db) ? db : null,
        NpgsqlDbType.TimestampTz => DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tz)
            ? tz
            : null,
        NpgsqlDbType.Timestamp => DateTime.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)
            ? DateTime.SpecifyKind(ts, DateTimeKind.Unspecified)
            : null,
        NpgsqlDbType.Date => DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var date) ? date : null,
        _ => null,
    };

    private static object? FromNumber(NpgsqlDbType dbType, decimal value) => dbType switch
    {
        // An integer column and a fractional literal do not meet: 3.7 is not 3, and quietly truncating it
        // would answer a question nobody asked. The comparison falls back to the JSON path instead.
        NpgsqlDbType.Smallint => IsIntegral(value) ? (short)value : null,
        NpgsqlDbType.Integer => IsIntegral(value) ? (int)value : null,
        NpgsqlDbType.Bigint => IsIntegral(value) ? (long)value : null,
        NpgsqlDbType.Numeric or NpgsqlDbType.Money => value,
        NpgsqlDbType.Real => (float)value,
        NpgsqlDbType.Double => (double)value,
        NpgsqlDbType.Text or NpgsqlDbType.Varchar => value.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static bool IsIntegral(decimal value) => value == Math.Truncate(value);

    private static object? FromTimestamp(NpgsqlDbType dbType, DateTimeOffset value) => dbType switch
    {
        NpgsqlDbType.TimestampTz => value,
        NpgsqlDbType.Timestamp => DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified),
        NpgsqlDbType.Date => DateOnly.FromDateTime(value.UtcDateTime),
        _ => null,
    };

    /// <summary>
    /// Names and adds parameters. Generated names are <c>@p0</c>, <c>@p1</c>, …; the ones the plan names
    /// (<c>@maxInline</c>, <c>@tenant</c>, <c>@limit</c>, <c>@offset</c>, <c>@k</c>, <c>@i</c>, <c>@id</c>)
    /// keep those names, because they are what the "Show SQL" disclosure shows a human.
    /// </summary>
    private sealed class ParameterBuilder(NpgsqlCommand command)
    {
        private int next;

        public string Add(NpgsqlDbType type, object value, string? name = null)
        {
            var parameterName = name ?? "p" + next++.ToString(CultureInfo.InvariantCulture);

            command.Parameters.Add(new NpgsqlParameter(parameterName, type) { Value = value });

            return "@" + parameterName;
        }
    }
}
