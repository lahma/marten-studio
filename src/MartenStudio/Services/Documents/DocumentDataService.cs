using System.Globalization;
using System.Text.Json;

using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using Weasel.Core;

namespace MartenStudio.Services.Documents;

/// <summary>The aliases the documents browser reserves for itself.</summary>
internal static class CollectionAliases
{
    /// <summary>The pseudo-collection of the most recently modified documents, whatever their type.</summary>
    public const string Recent = "_recent";

    /// <summary>
    /// Marten's own dead-letter table, which the events area owns and the documents browser therefore
    /// leaves out of the discovered group.
    /// </summary>
    public const string DeadLetterTable = "mt_doc_deadletterevent";

    /// <summary>
    /// Whether a document type is Marten's own bookkeeping rather than one of the host's collections.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <c>DeadLetterEvent</c> so far, and only once the store has an event store: a store with an
    /// async projection gets the mapping for free, so the type arrives in
    /// <c>AllKnownDocumentTypes()</c> in the registered band rather than as a discovered orphan table -
    /// which is why excluding the table name from discovery is not enough on its own.
    /// </para>
    /// <para>
    /// <b>The documents browser leaves it out entirely, and this is not cosmetic.</b> Dead letters have
    /// a screen of their own whose actions are gated on <c>ManageDeadLetters</c>; the same rows reached
    /// as an ordinary collection would be editable and deletable under <c>EditDocuments</c> and
    /// <c>DeleteDocuments</c> instead, which is a different capability answering for the same data. The
    /// Query page takes the opposite line on purpose and keeps it in the picker - it is queryable there,
    /// read-only, and sorted last (<c>QueryService.CompareAliases</c>).
    /// </para>
    /// </remarks>
    public static bool IsMartenInfrastructure(Type documentType) =>
        documentType == typeof(JasperFx.Events.Daemon.DeadLetterEvent);
}

/// <summary>
/// Everything the documents browser reads, against one resolved scope at a time.
/// </summary>
/// <remarks>
/// <para>
/// Reads are raw parameterised SQL built from <c>IDocumentType</c> metadata (D6): the studio only ever
/// knows a document type as a runtime <see cref="Type"/>, so a generic <c>session.Query&lt;T&gt;()</c> is
/// not available to it. Every statement comes out of <see cref="DocumentQueryBuilder"/> or
/// <see cref="DocumentBrowseQueries"/>, which is the one place an identifier becomes SQL text (AGENTS.md
/// hard rule 4).
/// </para>
/// <para>
/// Every method resolves its <see cref="StudioScope"/> first and passes nothing else across the seam
/// (plan §4.2). Failures are values wherever a page has to keep rendering: a collection whose count could
/// not be read is <see cref="DocumentCount.Unavailable"/> rather than an exception, and a list that could
/// not be read blanks the list region rather than the page (plan §4.8).
/// </para>
/// </remarks>
internal sealed partial class DocumentDataService : IDocumentDataService
{
    /// <summary>How many never-analysed tables the rail will pay an exact <c>count(*)</c> for.</summary>
    private const int MaxExactCountsPerRail = 25;

    /// <summary>How many JSON keys the column chooser offers from a sampled page.</summary>
    private const int MaxJsonSuggestions = 50;

    /// <summary>How many collections the "search other collections for this id" action probes.</summary>
    private const int MaxIdProbes = 25;

    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioScopeResolver resolver;
    private readonly ColumnCatalog columnCatalog;
    private readonly IndexCatalog indexCatalog;
    private readonly ILogger<DocumentDataService> logger;

    public DocumentDataService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        IndexCatalog indexCatalog,
        ILogger<DocumentDataService> logger)
    {
        this.options = options;
        this.resolver = resolver;
        this.columnCatalog = columnCatalog;
        this.indexCatalog = indexCatalog;
        this.logger = logger;
    }

    private int CommandTimeoutSeconds => Math.Max(1, (int) options.Value.QueryTimeout.TotalSeconds);

    /// <inheritdoc />
    public async Task<CollectionRail> GetCollectionsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            List<DocumentTableInfo> tables = [];
            HashSet<string> schemas = new(StringComparer.Ordinal) { resolved.Store.Options.DatabaseSchemaName };
            HashSet<string> knownTables = new(StringComparer.OrdinalIgnoreCase);

            foreach (IDocumentType documentType in VisibleDocumentTypes(resolved.Store))
            {
                DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType);

                tables.Add(table);
                schemas.Add(table.Schema);
                knownTables.Add(DocumentBrowseQueries.Key(table.Schema, table.Table));
            }

            // The schemas come from the mappings rather than from `Storage.AllSchemaNames()`: the latter
            // walks Marten's feature set, which lazily migrates the HiLo sequence on first touch, and the
            // rail is a read - it has no business applying schema changes to get a row count.
            IReadOnlyDictionary<string, long> estimates = await DocumentBrowseQueries
                .EstimateAllAsync(connection, [.. schemas], CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            var exactCountsSpent = 0;

            List<CollectionInfo> registered = [];
            var index = 0;

            foreach (IDocumentType documentType in VisibleDocumentTypes(resolved.Store))
            {
                DocumentTableInfo table = tables[index++];

                DocumentCount count = await CountForRailAsync(
                        connection, estimates, table, () => exactCountsSpent++ < MaxExactCountsPerRail, cancellationToken)
                    .ConfigureAwait(false);

                registered.Add(Describe(documentType, table, count));
            }

            registered.Sort(static (left, right) => string.CompareOrdinal(left.Alias, right.Alias));

            List<CollectionInfo> discovered = await DiscoverAsync(
                    resolved, connection, knownTables, estimates, cancellationToken)
                .ConfigureAwait(false);

            List<CollectionGroup> groups =
            [
                new(CollectionGroupKind.Recent, "Recent", [RecentCollection()]),
                new(CollectionGroupKind.Documents, "Documents", registered),
            ];

            if (discovered.Count > 0)
            {
                groups.Add(new CollectionGroup(CollectionGroupKind.Discovered, "Discovered (unregistered)", discovered));
            }

            return new CollectionRail(groups);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not list the document collections of {StoreKey}", scope.StoreKey);
            return CollectionRail.Failed(Describe(exception));
        }
    }

    /// <inheritdoc />
    public async Task<DocumentCount> CountExactAsync(
        StudioScope scope,
        string alias,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            CollectionContext? context = await LoadContextAsync(resolved, connection, alias, cancellationToken)
                .ConfigureAwait(false);

            if (context is null)
            {
                return DocumentCount.Unavailable;
            }

            var estimator = new CountEstimator { CommandTimeoutSeconds = CommandTimeoutSeconds };

            return await estimator
                .CountExactAsync(connection, context.Table.Schema, context.Table.Table, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not count '{Alias}' exactly", alias);
            return DocumentCount.Unavailable;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecentDocument>> GetRecentAsync(
        StudioScope scope,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        List<DocumentTableInfo> tables = [];

        foreach (IDocumentType documentType in VisibleDocumentTypes(resolved.Store))
        {
            DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType);

            if (table.HasMetadata(DocumentMetadataColumn.LastModified))
            {
                tables.Add(table);
            }
        }

        var perTable = Math.Clamp(limit, 1, 100);

        await using NpgsqlCommand? command = DocumentBrowseQueries.BuildRecent(
            tables, scope.TenantId, perTable, Math.Clamp(limit, 1, 200));

        if (command is null)
        {
            return [];
        }

        command.Connection = connection;
        command.CommandTimeout = CommandTimeoutSeconds;

        List<RecentDocument> recent = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var alias = reader.GetString(0);

            recent.Add(new RecentDocument(
                alias,
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                CollectionColorizer.HueFor(alias)));
        }

        return recent;
    }

    /// <inheritdoc />
    public async Task<DocumentPage> ListAsync(
        StudioScope scope,
        string alias,
        DocumentListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            CollectionContext? context = await LoadContextAsync(resolved, connection, alias, cancellationToken)
                .ConfigureAwait(false);

            if (context is null)
            {
                return DocumentPage.Failed($"This store has no collection called '{alias}'.");
            }

            return await ListCoreAsync(resolved, connection, context, request, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException postgres)
        {
            return FromPostgres(postgres);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not list '{Alias}'", alias);
            return DocumentPage.Failed(Describe(exception));
        }
    }

    private async Task<DocumentPage> ListCoreAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        CollectionContext context,
        DocumentListRequest request,
        CancellationToken cancellationToken)
    {
        DocumentTableInfo table = context.Table;

        IReadOnlyList<PostgresIndex> indexes = await indexCatalog
            .GetAsync(connection, table.Schema, table.Table, cancellationToken)
            .ConfigureAwait(false);

        SearchGrammarResult parse = SearchGrammar.Parse(request.Search);

        List<DocumentPredicate> predicates = [.. parse.Predicates];

        if (context.SubclassAlias is { } subclass)
        {
            predicates.Insert(0, new DocumentPredicate.SubclassIs(subclass));
        }

        IReadOnlyList<DocumentColumnHeader> available = AvailableColumns(table, context.IsRegistered);
        List<DocumentColumnHeader> visible = SelectColumns(table, request.ColumnKeys, available, context);

        DocumentColumn sort = DocumentColumnKeys.Resolve(table, request.SortKey) ?? DefaultSort(table);
        SortDirection direction = request.SortKey is null ? DefaultDirection(table) : request.Direction;

        // The sort column has to be in the select list even when the chooser hides it: the keyset cursor
        // is (sort value, id), and a cursor whose sort half was never read cannot page.
        List<DocumentColumn> queryColumns = [];

        foreach (DocumentColumnHeader header in visible)
        {
            if (header.Column is not DocumentColumn.Id)
            {
                queryColumns.Add(header.Column);
            }
        }

        var sortOrdinal = -1;

        if (sort is not DocumentColumn.Id)
        {
            sortOrdinal = queryColumns.FindIndex(x => x == sort);

            if (sortOrdinal < 0)
            {
                queryColumns.Add(sort);
                sortOrdinal = queryColumns.Count - 1;
            }
        }

        IndexAdvice advice = IndexAdvisor.Evaluate(table, indexes, predicates, sort);
        SearchVerdict verdict = BuildVerdict(parse, advice, predicates);

        var pageSize = Math.Clamp(
            request.PageSize <= 0 ? options.Value.DefaultPageSize : request.PageSize,
            1,
            Math.Min(options.Value.MaxPageSize, DocumentListQuery.MaxPageSize - 1));

        var query = new DocumentListQuery
        {
            Predicates = predicates,
            Sort = sort,
            Direction = direction,
            Cursor = request.UseOffsetPaging ? null : request.Cursor,
            Offset = request.UseOffsetPaging ? request.Offset : 0,
            // One more than the page, so "is there a next page" needs no second query.
            PageSize = pageSize + 1,
            IncludeDeleted = table.SoftDeleteEnabled ? request.Deleted : DeletedFilter.Exclude,
            TenantId = resolved.TenantId,
            Columns = queryColumns,
            MaxInlineDocumentBytes = options.Value.MaxInlineDocumentBytes,
        };

        DocumentCount estimate = await EstimateAsync(connection, table, request.ExactCount, cancellationToken)
            .ConfigureAwait(false);

        await using NpgsqlCommand command = DocumentQueryBuilder.BuildList(table, query);

        var sql = command.CommandText;
        List<string> parameterNames = [];

        foreach (NpgsqlParameter parameter in command.Parameters)
        {
            parameterNames.Add("@" + parameter.ParameterName);
        }

        var shell = new DocumentPage
        {
            Columns = visible,
            AvailableColumns = available,
            Estimate = estimate,
            Sql = sql,
            ParameterNames = parameterNames,
            Verdict = verdict,
            DocumentClrType = context.ClrType,
            NamingPolicy = context.NamingPolicy,
            SortKey = DocumentColumnKeys.KeyFor(sort),
            Direction = direction,
        };

        if (verdict.FilterIsRed && !request.RunAnyway)
        {
            return shell with { State = DocumentListState.BlockedByVerdict, Suggestion = verdict.Suggestion };
        }

        command.Connection = connection;
        command.CommandTimeout = CommandTimeoutSeconds;

        List<DocumentRow> rows = [];

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadRow(reader, table, queryColumns, visible, sortOrdinal));
            }
        }

        var hasMore = rows.Count > pageSize;

        if (hasMore)
        {
            rows.RemoveRange(pageSize, rows.Count - pageSize);
        }

        DocumentKeysetCursor? next = null;

        if (!request.UseOffsetPaging && hasMore && rows.Count > 0)
        {
            DocumentRow last = rows[^1];
            next = new DocumentKeysetCursor(sort is DocumentColumn.Id ? null : last.SortValue, last.Id);
        }

        return shell with
        {
            Rows = rows,
            HasMore = hasMore,
            NextCursor = next,
            JsonSuggestions = SampleJsonKeys(rows),
            State = DocumentListState.Loaded,
        };
    }

    private async Task<DocumentCount> EstimateAsync(
        NpgsqlConnection connection,
        DocumentTableInfo table,
        bool exact,
        CancellationToken cancellationToken)
    {
        var estimator = new CountEstimator { CommandTimeoutSeconds = CommandTimeoutSeconds };

        try
        {
            return exact
                ? await estimator.CountExactAsync(connection, table.Schema, table.Table, cancellationToken).ConfigureAwait(false)
                : await estimator.EstimateAsync(connection, table.Schema, table.Table, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            logger.LogWarning(exception, "Marten Studio could not count '{Alias}'", table.Alias);
            return DocumentCount.Unavailable;
        }
    }

    private static DocumentRow ReadRow(
        NpgsqlDataReader reader,
        DocumentTableInfo table,
        List<DocumentColumn> queryColumns,
        IReadOnlyList<DocumentColumnHeader> visible,
        int sortOrdinal)
    {
        // The builder's select list is always id, data, data_bytes, then the requested columns in order.
        const int FirstColumnOrdinal = 3;

        var id = DisplayValue(reader.IsDBNull(0) ? null : reader.GetValue(0)) ?? string.Empty;
        var json = reader.IsDBNull(1) ? null : reader.GetString(1);
        var bytes = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);

        var values = new object?[queryColumns.Count];

        for (var i = 0; i < queryColumns.Count; i++)
        {
            var ordinal = FirstColumnOrdinal + i;
            values[i] = reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
        }

        List<string?> cells = [];

        foreach (DocumentColumnHeader header in visible)
        {
            if (header.Column is DocumentColumn.Id)
            {
                cells.Add(id);
                continue;
            }

            var index = IndexOf(queryColumns, header.Column);
            cells.Add(index < 0 ? null : DisplayValue(values[index]));
        }

        return new DocumentRow
        {
            Id = id,
            Json = json,
            SizeBytes = bytes,
            Cells = cells,
            LastModified = Timestamp(Find(table, queryColumns, values, DocumentMetadataColumn.LastModified)),
            Version = DisplayValue(Find(table, queryColumns, values, DocumentMetadataColumn.Version)),
            IsDeleted = Find(table, queryColumns, values, DocumentMetadataColumn.IsSoftDeleted) is true,
            TenantId = DisplayValue(Find(table, queryColumns, values, DocumentMetadataColumn.TenantId)),
            DocumentTypeAlias = DisplayValue(Find(table, queryColumns, values, DocumentMetadataColumn.DocumentType)),
            SortValue = sortOrdinal < 0 ? null : CursorValue(values[sortOrdinal]),
        };
    }

    private static object? Find(
        DocumentTableInfo table,
        List<DocumentColumn> queryColumns,
        object?[] values,
        DocumentMetadataColumn column)
    {
        if (!table.HasMetadata(column))
        {
            return null;
        }

        var index = IndexOf(queryColumns, new DocumentColumn.Metadata(column));
        return index < 0 ? null : values[index];
    }

    private static int IndexOf(List<DocumentColumn> columns, DocumentColumn column)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i] == column)
            {
                return i;
            }
        }

        return -1;
    }

    private static DateTimeOffset? Timestamp(object? value) => value switch
    {
        DateTimeOffset offset => offset,
        DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
        _ => null,
    };

    /// <summary>
    /// A value as the table shows it: invariant, never localised, and never a round-trip format where a
    /// friendlier one exists. Timestamps keep their offset because a row's time without one is a lie.
    /// </summary>
    internal static string? DisplayValue(object? value) => value switch
    {
        null or DBNull => null,
        string text => text,
        bool boolean => boolean ? "true" : "false",
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>
    /// A value as the keyset cursor carries it: round-trippable, because it is parsed back into the sort
    /// column's own type before it is bound to the next page's query.
    /// </summary>
    internal static string? CursorValue(object? value) => value switch
    {
        null or DBNull => null,
        DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        _ => DisplayValue(value),
    };

    /// <summary>
    /// The top-level JSON keys of the documents on this page, most frequent first.
    /// </summary>
    /// <remarks>
    /// Sampled from what was loaded rather than from the collection: reading every document to find out
    /// what properties exist is the query the studio is trying to help people avoid, and the page in front
    /// of the user is a perfectly good sample of the shape they are looking at.
    /// </remarks>
    internal static IReadOnlyList<JsonPropertySuggestion> SampleJsonKeys(IReadOnlyList<DocumentRow> rows)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);

        foreach (DocumentRow row in rows)
        {
            if (row.Json is null)
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(row.Json, new JsonDocumentOptions { MaxDepth = 64 });

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    counts[property.Name] = counts.GetValueOrDefault(property.Name) + 1;
                }
            }
            catch (JsonException)
            {
                // A row whose data is not valid JSON is a real thing to find in a database somebody
                // migrated by hand. It contributes no suggestions and breaks nothing.
            }
        }

        return
        [
            .. counts
                .OrderByDescending(static x => x.Value)
                .ThenBy(static x => x.Key, StringComparer.Ordinal)
                .Take(MaxJsonSuggestions)
                .Select(static x => new JsonPropertySuggestion(x.Key, x.Value))
        ];
    }

    /// <summary>Turns the parse result and the advisor's verdicts into the chips the strip renders.</summary>
    internal static SearchVerdict BuildVerdict(
        SearchGrammarResult parse,
        IndexAdvice advice,
        IReadOnlyList<DocumentPredicate> predicates)
    {
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(advice);

        List<SearchChip> chips = [];
        var worst = IndexVerdictLevel.Green;
        var filterWorst = IndexVerdictLevel.Green;
        string? suggestion = null;

        foreach (PredicateVerdict verdict in advice.Predicates)
        {
            if (verdict.Verdict.Level > filterWorst)
            {
                filterWorst = verdict.Verdict.Level;
            }

            chips.Add(new SearchChip(
                SearchChipKind.Predicate,
                DescribePredicate(verdict.Predicate),
                verdict.Verdict.Level,
                verdict.Verdict.Reason,
                verdict.Verdict.Suggestion));

            if (verdict.Verdict.Level > worst)
            {
                worst = verdict.Verdict.Level;
            }

            suggestion ??= verdict.Verdict.Level == IndexVerdictLevel.Red ? verdict.Verdict.Suggestion : null;
        }

        foreach (SearchGrammarError error in parse.Errors)
        {
            chips.Add(new SearchChip(SearchChipKind.Error, error.Message, IndexVerdictLevel.Red, error.Message, null));
        }

        if (advice.Sort is { } sort)
        {
            chips.Add(new SearchChip(SearchChipKind.Sort, "sort", sort.Level, sort.Reason, sort.Suggestion));

            if (sort.Level > worst)
            {
                worst = sort.Level;
            }

            suggestion ??= sort.Level == IndexVerdictLevel.Red ? sort.Suggestion : null;
        }

        return new SearchVerdict
        {
            Level = parse.HasErrors ? IndexVerdictLevel.Red : worst,
            FilterLevel = parse.HasErrors ? IndexVerdictLevel.Red : filterWorst,
            Chips = chips,
            Errors = parse.Errors,
            Predicates = predicates,
            Suggestion = suggestion,
        };
    }

    /// <summary>One filter term, written back out in the grammar's own words.</summary>
    internal static string DescribePredicate(DocumentPredicate predicate) => predicate switch
    {
        DocumentPredicate.IdEquals id => "id:" + id.Id,
        DocumentPredicate.Tenant tenant => "tenant:" + tenant.TenantId,
        DocumentPredicate.IsDeleted deleted => deleted.Value ? "is:deleted" : "is:not-deleted",
        DocumentPredicate.SubclassIs subclass => "type:" + subclass.Alias,
        DocumentPredicate.Contains contains => "@> " + contains.Json,
        DocumentPredicate.FreeText free => "\"" + free.Text + "\"",
        DocumentPredicate.FieldLike like => string.Join('.', like.Path) + " ~ " + like.Text,
        DocumentPredicate.FieldCompare compare =>
            string.Join('.', compare.Path) + " " + OperatorText(compare.Operator) + " " + ValueText(compare.Value),
        _ => predicate.GetType().Name,
    };

    private static string OperatorText(ComparisonOperator op) => op switch
    {
        ComparisonOperator.NotEqual => "!=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        _ => "=",
    };

    private static string ValueText(SearchValue value) => value switch
    {
        SearchValue.Text text => text.Value,
        SearchValue.Number number => number.Value.ToString(CultureInfo.InvariantCulture),
        SearchValue.Boolean boolean => boolean.Value ? "true" : "false",
        SearchValue.Timestamp timestamp => timestamp.Value.ToString("O", CultureInfo.InvariantCulture),
        _ => "null",
    };

    private static DocumentColumn DefaultSort(DocumentTableInfo table) =>
        table.HasMetadata(DocumentMetadataColumn.LastModified)
            ? new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified)
            : DocumentColumn.ById;

    private static SortDirection DefaultDirection(DocumentTableInfo table) =>
        table.HasMetadata(DocumentMetadataColumn.LastModified) ? SortDirection.Descending : SortDirection.Ascending;

    /// <summary>Every column the chooser may offer for this table.</summary>
    internal static IReadOnlyList<DocumentColumnHeader> AvailableColumns(DocumentTableInfo table, bool registered)
    {
        List<DocumentColumnHeader> headers = [DocumentColumnKeys.HeaderFor(DocumentColumn.ById)];

        foreach (DocumentMetadataColumnInfo metadata in table.MetadataColumns)
        {
            headers.Add(DocumentColumnKeys.HeaderFor(new DocumentColumn.Metadata(metadata.Column)));
        }

        foreach (DuplicatedColumnInfo duplicated in table.DuplicatedColumns)
        {
            headers.Add(DocumentColumnKeys.HeaderFor(new DocumentColumn.Duplicated(duplicated.ColumnName)));
        }

        _ = registered;
        return headers;
    }

    private static List<DocumentColumnHeader> SelectColumns(
        DocumentTableInfo table,
        IReadOnlyList<string> keys,
        IReadOnlyList<DocumentColumnHeader> available,
        CollectionContext context)
    {
        if (keys.Count > 0)
        {
            List<DocumentColumnHeader> chosen = [DocumentColumnKeys.HeaderFor(DocumentColumn.ById)];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { DocumentColumnKeys.Id };

            foreach (var key in keys)
            {
                DocumentColumn? column = DocumentColumnKeys.Resolve(table, key);

                if (column is not null && seen.Add(DocumentColumnKeys.KeyFor(column)))
                {
                    chosen.Add(DocumentColumnKeys.HeaderFor(column));
                }
            }

            return chosen;
        }

        // An unregistered table gets id, data and its metadata and nothing else: the other columns are
        // only guesses about what somebody else's schema means (plan §3.2, "read-only always").
        List<DocumentColumnHeader> defaults = [];

        foreach (DocumentColumnHeader header in available)
        {
            if (header.Kind == DocumentColumnKind.Duplicated && !context.IsRegistered)
            {
                continue;
            }

            defaults.Add(header);
        }

        return defaults;
    }

    private static CollectionInfo RecentCollection() => new()
    {
        Alias = CollectionAliases.Recent,
        ClrTypeName = null,
        Schema = string.Empty,
        Table = string.Empty,
        Hue = CollectionColorizer.HueFor(CollectionAliases.Recent),
        Count = DocumentCount.Unavailable,
        IsRegistered = false,
    };

    private static CollectionInfo Describe(IDocumentType documentType, DocumentTableInfo table, DocumentCount count)
    {
        List<CollectionInfo> subclasses = [];

        foreach (SubClassMapping subclass in documentType.SubClasses)
        {
            subclasses.Add(new CollectionInfo
            {
                Alias = subclass.Alias,
                ClrTypeName = subclass.DocumentType.Name,
                FullTypeName = subclass.DocumentType.FullName,
                Schema = table.Schema,
                Table = table.Table,
                Hue = CollectionColorizer.HueFor(subclass.Alias),
                Count = DocumentCount.Unavailable,
                IsRegistered = true,
                IsSubclass = true,
                RootAlias = table.Alias,
                Flags = Flags(table, documentType),
                IdColumnType = table.IdColumnType,
                DuplicatedFieldCount = table.DuplicatedColumns.Count,
            });
        }

        subclasses.Sort(static (left, right) => string.CompareOrdinal(left.Alias, right.Alias));

        return new CollectionInfo
        {
            Alias = table.Alias,
            ClrTypeName = documentType.DocumentType.Name,
            FullTypeName = documentType.DocumentType.FullName,
            Schema = table.Schema,
            Table = table.Table,
            Hue = CollectionColorizer.HueFor(table.Alias),
            Count = count,
            IsRegistered = true,
            Flags = Flags(table, documentType),
            IdColumnType = table.IdColumnType,
            DuplicatedFieldCount = table.DuplicatedColumns.Count,
            SubCollections = subclasses,
        };
    }

    private static CollectionFlags Flags(DocumentTableInfo table, IDocumentType documentType) => new(
        table.SoftDeleteEnabled,
        table.TenancyStyle == TenancyStyle.Conjoined,
        documentType.UseOptimisticConcurrency,
        documentType.IsHierarchy());

    private async Task<List<CollectionInfo>> DiscoverAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        HashSet<string> knownTables,
        IReadOnlyDictionary<string, long> estimates,
        CancellationToken cancellationToken)
    {
        List<CollectionInfo> discovered = [];

        IReadOnlyList<DbObjectName> tables;

        try
        {
            tables = await resolved.Database.DocumentTables().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not list the document tables of the database");
            return discovered;
        }

        foreach (DbObjectName name in tables)
        {
            if (knownTables.Contains(DocumentBrowseQueries.Key(name.Schema, name.Name)) ||
                string.Equals(name.Name, CollectionAliases.DeadLetterTable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TableColumns physical = await columnCatalog
                .GetAsync(connection, name.Schema, name.Name, cancellationToken)
                .ConfigureAwait(false);

            if (!physical.Exists)
            {
                continue;
            }

            DocumentTableInfo table = DocumentTableInfo.FromDiscoveredTable(name.Schema, name.Name, physical.Columns);

            DocumentCount count = await CountForRailAsync(connection, estimates, table, static () => true, cancellationToken)
                .ConfigureAwait(false);

            discovered.Add(new CollectionInfo
            {
                Alias = table.Alias,
                ClrTypeName = null,
                Schema = table.Schema,
                Table = table.Table,
                Hue = CollectionColorizer.HueFor(table.Alias),
                Count = count,
                IsRegistered = false,
                Flags = new CollectionFlags(
                    table.SoftDeleteEnabled,
                    table.TenancyStyle == TenancyStyle.Conjoined,
                    OptimisticConcurrency: false,
                    Hierarchy: table.HasMetadata(DocumentMetadataColumn.DocumentType)),
                IdColumnType = table.IdColumnType,
                DuplicatedFieldCount = table.DuplicatedColumns.Count,
            });
        }

        discovered.Sort(static (left, right) => string.CompareOrdinal(left.Alias, right.Alias));

        return discovered;
    }

    /// <summary>
    /// The rail's count for one table: the grouped estimate, upgraded to an exact count only for a table
    /// Postgres has never analysed.
    /// </summary>
    /// <remarks>
    /// <c>reltuples</c> is <c>-1</c> until the first <c>ANALYZE</c>, which is exactly the state a freshly
    /// seeded demo database is in — and "~0" beside a collection that visibly has rows in it is worse than
    /// the round trip. The budget stops a store with hundreds of never-analysed tables from turning the
    /// rail into hundreds of sequential scans.
    /// </remarks>
    private async Task<DocumentCount> CountForRailAsync(
        NpgsqlConnection connection,
        IReadOnlyDictionary<string, long> estimates,
        DocumentTableInfo table,
        Func<bool> budget,
        CancellationToken cancellationToken)
    {
        if (!estimates.TryGetValue(DocumentBrowseQueries.Key(table.Schema, table.Table), out var rows))
        {
            return DocumentCount.Unavailable;
        }

        if (rows >= 0)
        {
            return DocumentCount.Estimate(rows);
        }

        if (!budget())
        {
            return DocumentCount.Unavailable;
        }

        var estimator = new CountEstimator { CommandTimeoutSeconds = CommandTimeoutSeconds };

        try
        {
            return await estimator
                .CountExactAsync(connection, table.Schema, table.Table, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            logger.LogWarning(exception, "Marten Studio could not count '{Alias}'", table.Alias);
            return DocumentCount.Unavailable;
        }
    }

    private IEnumerable<IDocumentType> VisibleDocumentTypes(IDocumentStore store)
    {
        Func<Type, bool>? visible = options.Value.IsDocumentTypeVisible;

        foreach (IDocumentType documentType in store.Options.AllKnownDocumentTypes())
        {
            // Marten's own bookkeeping is not one of this application's collections, and the browser's
            // capabilities are not the ones that answer for it - see CollectionAliases.
            if (CollectionAliases.IsMartenInfrastructure(documentType.DocumentType))
            {
                continue;
            }

            if (visible is null || visible(documentType.DocumentType))
            {
                yield return documentType;
            }
        }
    }

    /// <summary>
    /// Everything one collection is, resolved once per call: its table as the database really has it, the
    /// mapping behind it when there is one, and the subclass filter when the alias names a subclass.
    /// </summary>
    private sealed record CollectionContext(
        DocumentTableInfo Table,
        IDocumentType? DocumentType,
        Type? ClrType,
        string? SubclassAlias,
        bool IsRegistered,
        JsonNamingPolicy? NamingPolicy);

    private async Task<CollectionContext?> LoadContextAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        string alias,
        CancellationToken cancellationToken)
    {
        JsonNamingPolicy? namingPolicy = NamingPolicyFor(resolved.Store);

        foreach (IDocumentType documentType in VisibleDocumentTypes(resolved.Store))
        {
            var isRoot = string.Equals(documentType.Alias, alias, StringComparison.OrdinalIgnoreCase);
            SubClassMapping? matched = null;

            if (!isRoot)
            {
                foreach (SubClassMapping subclass in documentType.SubClasses)
                {
                    if (string.Equals(subclass.Alias, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = subclass;
                        break;
                    }
                }
            }

            if (!isRoot && matched is null)
            {
                continue;
            }

            DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType);

            TableColumns physical = await columnCatalog
                .GetAsync(connection, table.Schema, table.Table, cancellationToken)
                .ConfigureAwait(false);

            table = table.WithPhysicalColumns(physical);

            return new CollectionContext(
                table,
                documentType,
                matched?.DocumentType ?? documentType.DocumentType,
                matched?.Alias,
                IsRegistered: true,
                namingPolicy);
        }

        // Not a mapped type: it may still be a table the database has and StoreOptions does not.
        foreach (DbObjectName name in await resolved.Database.DocumentTables().ConfigureAwait(false))
        {
            var tableAlias = name.Name.StartsWith("mt_doc_", StringComparison.OrdinalIgnoreCase)
                ? name.Name["mt_doc_".Length..]
                : name.Name;

            if (!string.Equals(tableAlias, alias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TableColumns physical = await columnCatalog
                .GetAsync(connection, name.Schema, name.Name, cancellationToken)
                .ConfigureAwait(false);

            if (!physical.Exists)
            {
                return null;
            }

            return new CollectionContext(
                DocumentTableInfo.FromDiscoveredTable(name.Schema, name.Name, physical.Columns),
                DocumentType: null,
                ClrType: null,
                SubclassAlias: null,
                IsRegistered: false,
                namingPolicy);
        }

        return null;
    }

    /// <summary>
    /// The serializer's casing as a <see cref="JsonNamingPolicy"/>, so the JSON viewer can turn a JSON key
    /// back into the CLR member a Marten LINQ path needs.
    /// </summary>
    /// <remarks>
    /// Read from <c>store.Options.Serializer().Casing</c> rather than assumed: Marten's default is
    /// <c>Casing.Default</c>, which writes the CLR names verbatim, and a viewer that assumed camel case
    /// would fail to find every property of a store that took the default.
    /// </remarks>
    internal static JsonNamingPolicy? NamingPolicyFor(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return store.Options.Serializer().Casing switch
            {
                Casing.CamelCase => JsonNamingPolicy.CamelCase,
                Casing.SnakeCase => JsonNamingPolicy.SnakeCaseLower,
                _ => null,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static DocumentPage FromPostgres(PostgresException exception)
    {
        // 57014 is statement_timeout. It is the one failure the studio can give useful advice about,
        // because it means the filter did exactly what the index verdict warned it would.
        if (string.Equals(exception.SqlState, "57014", StringComparison.Ordinal))
        {
            return DocumentPage.Failed(
                "This read ran past the studio's query timeout. Narrow the filter, or add an index for it.",
                exception.SqlState);
        }

        return DocumentPage.Failed($"{exception.SqlState}: {exception.MessageText}", exception.SqlState);
    }

    private static string Describe(Exception exception) => exception switch
    {
        PostgresException postgres => $"{postgres.SqlState}: {postgres.MessageText}",
        NpgsqlException => "The database could not be reached: " + exception.Message,
        _ => exception.Message,
    };
}
