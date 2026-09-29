using System.Globalization;
using System.Text;

using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Services.Database;

/// <summary>
/// The database browser's row reads, over <see cref="TableRowQueryBuilder" />, behind
/// <see cref="DatabaseAccess.RequireRowAccessAsync" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of every method.</b> The row gate first - capability, the tenant-less scope with the
/// capability named, <c>BrowsableSchemas</c>, the catalog's judgement of the relation - and nothing is
/// built, opened or read until it has said yes; its refusals are audited there. Then the request is
/// checked against what the catalog said (the filter's columns, the sort column, the cursor, the key), and
/// a request that cannot be used is a value, not an exception. Only then is a connection opened, and every
/// statement runs inside <see cref="ReadOnlySqlSession.InTransactionAsync{T}" /> as
/// <see cref="MartenStudioOptions.SqlConsoleRole" />, under <see cref="MartenStudioOptions.QueryTimeout" />.
/// </para>
/// <para>
/// <b>Only the grant's names reach SQL.</b> The schema and name a page passes are compared by the gate and
/// never quoted; the builder is handed the catalog's spelling of the relation and of every column, and a
/// filter's column is resolved to the catalog's spelling before it is.
/// </para>
/// <para>
/// <b>Postgres' answers are values.</b> A statement timeout, an unrefreshed materialized view, a privilege
/// the role lacks, a row-level security policy that needs a setting the studio does not set, a filter
/// value that does not fit its column - each is a <see cref="TableRowError" /> with the SQLSTATE and a
/// sentence, logged at Debug (event 9237). Anything else is an anomaly, logged through
/// <see cref="StudioLogThrottle" />.
/// </para>
/// </remarks>
internal sealed class TableRowService : ITableRowService
{
    /// <summary>What the audit ring calls a filter change on a relation's rows.</summary>
    internal const string FilterAction = "Filter database rows";

    /// <summary>What the audit ring calls opening one row.</summary>
    internal const string RowAction = "Open database row";

    /// <summary>What the audit ring calls reading a row's references (refusals only).</summary>
    internal const string ReferencesAction = "Read database row references";

    /// <summary>What the audit ring calls expanding one cell (refusals only).</summary>
    internal const string CellAction = "Read database cell";

    /// <summary>What the audit ring calls an exact count: its refusals, and a filtered count that ran.</summary>
    internal const string CountAction = "Count database rows";

    /// <summary>
    /// The longest one reference check may run: the lesser of this and
    /// <see cref="MartenStudioOptions.QueryTimeout" />.
    /// </summary>
    internal static readonly TimeSpan ReferenceStatementCap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long all of one row's reference checks together may take before the rest are left unchecked: the
    /// lesser of this and <see cref="MartenStudioOptions.QueryTimeout" />.
    /// </summary>
    internal static readonly TimeSpan ReferenceBudgetCap = TimeSpan.FromSeconds(10);

    private const char Separator = '\u001f';

    private static readonly SqlValueFormatter Formatter = new();

    private readonly DatabaseAccess access;
    private readonly DatabaseCatalog catalog;
    private readonly StudioActionLog audit;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly ILogger<TableRowService> logger;
    private readonly StudioLogThrottle throttle;
    private readonly AuthenticationStateProvider authentication;

    /// <summary>
    /// What this circuit last recorded per store, database and relation: the filter, sort and paging a read
    /// was made with. A read with the same signature - a page turn, a refresh - is not a new opening.
    /// </summary>
    private readonly TableRowAuditLedger ledger = new();

    public TableRowService(
        DatabaseAccess access,
        DatabaseCatalog catalog,
        StudioActionLog audit,
        IOptions<MartenStudioOptions> options,
        ILogger<TableRowService> logger,
        StudioLogThrottle throttle,
        AuthenticationStateProvider authentication)
    {
        this.access = access;
        this.catalog = catalog;
        this.audit = audit;
        this.options = options;
        this.logger = logger;
        this.throttle = throttle;
        this.authentication = authentication;
    }

    private MartenStudioOptions Options => options.Value;

    /// <summary>A little past the server-side <c>statement_timeout</c>, which is what should fire.</summary>
    private int CommandTimeoutSeconds => (int) Math.Ceiling(Options.QueryTimeout.TotalSeconds) + 5;

    // ---------------------------------------------------------------------------------------------------
    // Pages
    // ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<TableRowPage> ListRowsAsync(
        StudioScope scope,
        string schema,
        string name,
        TableRowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        DatabaseRowAccessResult result = await access
            .RequireRowAccessAsync(scope, schema, name, DatabaseAccess.RowsAction, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Allowed)
        {
            return TableRowPage.Refused(result.Refusal, result.Reason ?? "Refused.");
        }

        DatabaseRowGrant grant = result.Grant;
        CatalogRelation relation = grant.Relation.Relation;

        DatabaseObjectDetail? detail = await DetailAsync(grant, cancellationToken).ConfigureAwait(false);
        List<TableRowColumn> available = Columns(grant, detail?.ForeignKeysOut ?? []);

        var page = new TableRowPage
        {
            Schema = relation.Schema,
            Name = relation.Name,
            Kind = DatabaseObjectKinds.FromRelkind(relation.Kind),
            Ownership = grant.Ownership,
            RowKey = grant.RowKey,
            EstimatedRows = relation.EstimatedRows,
            RowSecurity = relation.RowSecurity,
            AvailableColumns = available,
        };

        RowFilterParse parse = RowFilterGrammar.Parse(request.Filter, grant.Columns);
        (string? sortColumn, string? sortProblem) = ResolveSort(grant, request.SortColumn);
        bool descending = request.Direction == SortDirection.Descending;

        List<TableRowColumn> shown = SelectColumns(available, grant, request.Columns);
        page = page with { Columns = shown };

        if (sortProblem is not null)
        {
            return page with
            {
                State = TableRowPageState.Invalid,
                Reason = sortProblem,
                Verdict = BuildVerdict(grant.Relation, grant.RowKey, parse, null, TableRowPagingMode.Key),
            };
        }

        if (parse.HasErrors)
        {
            return page with
            {
                State = TableRowPageState.Invalid,
                Reason = "The filter cannot be used as it is; the marked terms say why.",
                Verdict = BuildVerdict(grant.Relation, grant.RowKey, parse, sortColumn, TableRowPagingMode.Key),
            };
        }

        // Checked whatever paging was asked for: a keyset request against a view still pages by offset.
        if (request.Offset is < 0 or > TableRowQueryBuilder.MaxOffset)
        {
            return page with
            {
                State = TableRowPageState.Invalid,
                Reason = "An offset of " + request.Offset.ToString("N0", CultureInfo.InvariantCulture) + " is outside the " +
                         TableRowQueryBuilder.MaxOffset.ToString("N0", CultureInfo.InvariantCulture) +
                         " rows the studio will page to by offset. Filter the rows, or page by key.",
                Verdict = BuildVerdict(grant.Relation, grant.RowKey, parse, sortColumn, TableRowPagingMode.Offset),
            };
        }

        MartenStudioOptions value = Options;
        int pageSize = Math.Clamp(
            request.PageSize <= 0 ? value.DefaultPageSize : request.PageSize,
            1,
            Math.Max(1, Math.Min(value.MaxPageSize, DocumentListQuery.MaxPageSize - 1)));

        try
        {
            await using NpgsqlConnection connection = grant.Resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // The version is on the connection already - Postgres sends it at startup - so asking costs
            // nothing and needs no statement.
            TableRowPlan plan = Plan(relation, grant.Columns, grant.RowKey, request.Paging, sortColumn, descending, SupportsTidRange(connection));

            if (!plan.Accepts(request.Cursor))
            {
                return page with
                {
                    State = TableRowPageState.Invalid,
                    Reason = "This page's cursor belongs to a different sort or order of this relation. Go back to the first page.",
                    Verdict = BuildVerdict(grant.Relation, grant.RowKey, parse, plan.SortColumn, plan.Mode),
                    Paging = plan.Describe(request.Offset),
                };
            }

            var spec = new TableRowListSpec
            {
                Schema = relation.Schema,
                Name = relation.Name,
                Columns = [.. shown.Select(static x => new TableRowColumnRead(x.Name, x.Type))],
                Paging = plan.Mode,
                KeyColumns = plan.KeyColumns,
                SelectLocator = plan.SelectLocator,
                TieBreakColumns = plan.TieBreakColumns,
                SortColumn = plan.SortColumn,
                Descending = descending,
                Cursor = plan.Mode == TableRowPagingMode.Offset ? null : request.Cursor,
                Offset = plan.Mode == TableRowPagingMode.Offset ? request.Offset : 0,
                PageSize = pageSize,
                Filter = parse.Terms,
                Caps = TableRowCaps.List,
            };

            TableRowStatement statement = TableRowQueryBuilder.BuildList(spec);
            RowFilterVerdict verdict = BuildVerdict(grant.Relation, grant.RowKey, parse, plan.SortColumn, plan.Mode);

            page = page with
            {
                Sql = statement.Sql,
                ParameterNames = statement.ParameterNames,
                Paging = plan.Describe(spec.Offset),
                Verdict = verdict,
            };

            if (!request.RunAnyway && ShouldWithhold(relation, verdict, value.ExactCountThreshold) is { } withheld)
            {
                return page with { State = TableRowPageState.Withheld, Reason = withheld };
            }

            ReadOnlySqlSession session = Session(pageSize + 1);

            (List<TableRow> rows, List<string?> sortValues, bool shortened) = await session
                .InTransactionAsync(
                    connection,
                    (transaction, token) => ReadPageAsync(connection, transaction, statement, token),
                    cancellationToken)
                .ConfigureAwait(false);

            bool hasMore = rows.Count > pageSize;

            if (hasMore)
            {
                // The row past the page was read only to know there is one. The cursor is the last row
                // kept - its key and its own sort value, never the extra row's, which would skip it.
                rows.RemoveRange(pageSize, rows.Count - pageSize);
                sortValues.RemoveRange(pageSize, sortValues.Count - pageSize);
            }

            page = page with
            {
                State = TableRowPageState.Loaded,
                Rows = rows,
                HasMore = hasMore,
                NextCursor = hasMore && plan.Mode != TableRowPagingMode.Offset ? plan.CursorAfter(rows[^1], sortValues[^1], descending) : null,
                NextOffset = hasMore && plan.Mode == TableRowPagingMode.Offset && spec.Offset + pageSize <= TableRowQueryBuilder.MaxOffset
                    ? spec.Offset + pageSize
                    : null,
                Shortened = shortened,
            };

            // Every read that returned rows, whatever position it started from: a cursor or an offset is
            // in the URL, so a pasted link is a first contact that never saw a first page.
            AuditRead(grant, request, parse, plan);

            return page;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return page with
            {
                State = TableRowPageState.Failed,
                Error = Fail(exception, grant, DatabaseAccess.RowsAction),
            };
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // One row
    // ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<TableRowDetail> GetRowAsync(
        StudioScope scope,
        string schema,
        string name,
        IReadOnlyDictionary<string, string> key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);

        DatabaseRowAccessResult result = await access
            .RequireRowAccessAsync(scope, schema, name, RowAction, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Allowed)
        {
            return TableRowDetail.Refused(result.Refusal, result.Reason ?? "Refused.");
        }

        DatabaseRowGrant grant = result.Grant;
        CatalogRelation relation = grant.Relation.Relation;

        if (KeyOf(grant.RowKey, relation.Kind, key, allowLocator: false) is not { } matched)
        {
            return TableRowDetail.Refused(DatabaseRefusal.NotApplicable, KeyProblem(grant, allowLocator: false));
        }

        DatabaseObjectDetail? detail = await DetailAsync(grant, cancellationToken).ConfigureAwait(false);
        List<TableRowColumn> columns = [.. Columns(grant, detail?.ForeignKeysOut ?? []).Where(static x => x.Shown)];

        TableRowStatement statement = TableRowQueryBuilder.BuildRow(
            relation.Schema,
            relation.Name,
            [.. columns.Select(static x => new TableRowColumnRead(x.Name, x.Type))],
            matched.Columns,
            matched.Values,
            null,
            TableRowCaps.Detail);

        var shell = new TableRowDetail
        {
            Schema = relation.Schema,
            Name = relation.Name,
            Ownership = grant.Ownership,
            RowKey = grant.RowKey,
            Columns = columns,
            RowSecurity = relation.RowSecurity,
            Sql = statement.Sql,
            ParameterNames = statement.ParameterNames,
        };

        try
        {
            await using NpgsqlConnection connection = grant.Resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            (List<TableRow> rows, _, bool shortened) = await Session(2)
                .InTransactionAsync(
                    connection,
                    (transaction, token) => ReadRowsAsync(connection, transaction, statement, TableRowCaps.Detail, token),
                    cancellationToken)
                .ConfigureAwait(false);

            string target = Target(relation);
            audit.Record(RowAction, target, succeeded: true, rows.Count == 0 ? "No row with that key." : "Row opened.",
                StudioCapability.BrowseDatabase, grant.Resolved.Scope);
            LogKey(grant, RowAction, matched);

            return rows.Count == 0
                ? shell with { Found = false, Reason = "There is no row with that key - it may have been deleted or changed." }
                : shell with { Found = true, Key = rows[0].Key, Cells = rows[0].Cells, Shortened = shortened };
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return shell with { Error = Fail(exception, grant, RowAction) };
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // One cell
    // ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<TableCellValue> GetCellAsync(
        StudioScope scope,
        string schema,
        string name,
        IReadOnlyDictionary<string, string> key,
        string column,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);

        DatabaseRowAccessResult result = await access
            .RequireRowAccessAsync(scope, schema, name, CellAction, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Allowed)
        {
            return TableCellValue.Refused(result.Refusal, result.Reason ?? "Refused.");
        }

        DatabaseRowGrant grant = result.Grant;
        CatalogRelation relation = grant.Relation.Relation;

        if (KeyOf(grant.RowKey, relation.Kind, key, allowLocator: true) is not { } matched)
        {
            return TableCellValue.Refused(DatabaseRefusal.NotApplicable, KeyProblem(grant, allowLocator: true));
        }

        DatabaseColumnInfo? info = ResolveColumn(grant.Columns, column);

        if (info is null || !info.Quotable)
        {
            return TableCellValue.Refused(
                DatabaseRefusal.NotFound,
                info is null
                    ? "There is no column '" + column + "' in " + Target(relation) + "."
                    : "'" + info.Name + "' cannot be put into SQL safely, so it is never read.");
        }

        TableRowColumn described = Columns(grant, []).First(x => string.Equals(x.Name, info.Name, StringComparison.Ordinal));

        // Bytes, as the option says: the server sends one character past the cap and the reader cuts that
        // to the cap in UTF-8 bytes, on a character boundary. The builder binds one past the cap, so the
        // cap leaves room for it.
        int cap = Math.Clamp(Options.MaxInlineDocumentBytes, 1, int.MaxValue - 1);

        TableRowStatement statement = TableRowQueryBuilder.BuildCell(
            relation.Schema,
            relation.Name,
            new TableRowColumnRead(info.Name, info.Type),
            matched.Columns,
            matched.Values,
            matched.Locator,
            cap);

        var shell = new TableCellValue
        {
            Column = described,
            Cap = cap,
            Sql = statement.Sql,
            ParameterNames = statement.ParameterNames,
        };

        try
        {
            await using NpgsqlConnection connection = grant.Resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            TableCellValue value = await Session()
                .InTransactionAsync(
                    connection,
                    (transaction, token) => ReadCellAsync(connection, transaction, statement, shell, cap, token),
                    cancellationToken)
                .ConfigureAwait(false);

            LogKey(grant, CellAction, matched);

            return value;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return shell with { Error = Fail(exception, grant, CellAction) };
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Counts
    // ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<TableRowCount> CountExactAsync(
        StudioScope scope,
        string schema,
        string name,
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        DatabaseRowAccessResult result = await access
            .RequireRowAccessAsync(scope, schema, name, CountAction, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Allowed)
        {
            return TableRowCount.Refused(result.Refusal, result.Reason ?? "Refused.");
        }

        DatabaseRowGrant grant = result.Grant;
        CatalogRelation relation = grant.Relation.Relation;

        if (relation.Kind == "v")
        {
            return TableRowCount.Refused(
                DatabaseRefusal.NotApplicable,
                "A view has no rows of its own to count: counting it runs its whole query, so the studio does not offer it.");
        }

        RowFilterParse parse = RowFilterGrammar.Parse(filter, grant.Columns);
        RowFilterVerdict verdict = BuildVerdict(grant.Relation, grant.RowKey, parse, null, TableRowPagingMode.Key);

        if (parse.HasErrors)
        {
            return new TableRowCount
            {
                Reason = "The filter cannot be used as it is; the marked terms say why.",
                Verdict = verdict,
            };
        }

        MartenStudioOptions value = Options;
        long threshold = Math.Max(value.ExactCountThreshold, 0);

        // One past the threshold, so "more than" is a fact - and a threshold at long.MaxValue is its own cap.
        long cap = threshold == long.MaxValue ? threshold : threshold + 1;

        TableRowStatement statement = TableRowQueryBuilder.BuildCount(relation.Schema, relation.Name, parse.Terms, cap);

        var shell = new TableRowCount
        {
            Verdict = verdict,
            Sql = statement.Sql,
            ParameterNames = statement.ParameterNames,
            Cap = parse.Terms.Count > 0 ? cap : null,
        };

        try
        {
            await using NpgsqlConnection connection = grant.Resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            TableRowCount answer = await Session()
                .InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        if (parse.Terms.Count == 0)
                        {
                            // The documents list's "=" rule, word for word: an estimate above the threshold
                            // declines without touching the table, and a table with no estimate is settled
                            // by the bounded probe - never by the count it stands in front of.
                            DocumentCount estimate = relation.EstimatedRows is { } rows
                                ? DocumentCount.Estimate(rows)
                                : DocumentCount.Unknown;

                            var estimator = new CountEstimator
                            {
                                ExactCountThreshold = threshold,
                                CommandTimeoutSeconds = CommandTimeoutSeconds,
                            };

                            if (!await estimator
                                    .MayCountExactlyAsync(connection, relation.Schema, relation.Name, estimate, token)
                                    .ConfigureAwait(false))
                            {
                                return shell with { Count = DocumentCount.RefusedExact(estimate, threshold) };
                            }
                        }

                        long counted = await ScalarAsync<long>(connection, transaction, statement, token).ConfigureAwait(false);

                        return parse.Terms.Count > 0 && counted >= cap
                            ? shell with { Count = DocumentCount.Exact(threshold), Bounded = true }
                            : shell with { Count = DocumentCount.Exact(counted) };
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            // A filtered count answers "how many rows match this", which is a question about the rows: it is
            // audited like a read with that filter. An unfiltered one is the relation's size.
            if (parse.Terms.Count > 0)
            {
                AuditCount(grant, filter, parse);
            }

            return answer;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return shell with { Error = Fail(exception, grant, CountAction) };
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // References
    // ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<TableRowReferences> GetReferencesAsync(
        StudioScope scope,
        string schema,
        string name,
        IReadOnlyDictionary<string, string> key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(key);

        DatabaseRowAccessResult result = await access
            .RequireRowAccessAsync(scope, schema, name, ReferencesAction, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Allowed)
        {
            return TableRowReferences.Refused(result.Refusal, result.Reason ?? "Refused.");
        }

        DatabaseRowGrant grant = result.Grant;
        CatalogRelation relation = grant.Relation.Relation;

        if (KeyOf(grant.RowKey, relation.Kind, key, allowLocator: false) is not { } matched)
        {
            return TableRowReferences.Refused(DatabaseRefusal.NotApplicable, KeyProblem(grant, allowLocator: false));
        }

        DatabaseGateRead gateRead;
        DatabaseObjectDetail detail;

        try
        {
            gateRead = await access.GateAsync(grant.Resolved.Scope, grant.Resolved, cancellationToken).ConfigureAwait(false);

            if (!gateRead.Succeeded)
            {
                return TableRowReferences.Refused(gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.");
            }

            detail = DatabaseObjectAssembler.Detail(gateRead.Gate, grant.Relation);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return TableRowReferences.Refused(DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception));
        }

        DatabaseGate gate = gateRead.Gate;

        // Every far end is judged before a connection is opened for this row: the catalog lookups are cached
        // reads of their own, and a far end the gate refuses is never named in a statement below.
        List<OutboundCheck> outbound = [];
        foreach (DatabaseForeignKeyInfo foreignKey in detail.ForeignKeysOut)
        {
            outbound.Add(await JudgeOutboundAsync(grant, gate, foreignKey, cancellationToken).ConfigureAwait(false));
        }

        List<InboundCheck> inbound = [];
        foreach (DatabaseForeignKeyInfo foreignKey in detail.ForeignKeysIn)
        {
            inbound.Add(await JudgeInboundAsync(grant, gate, foreignKey, cancellationToken).ConfigureAwait(false));
        }

        List<string> wanted = [];
        foreach (string column in outbound.SelectMany(static x => x.Key.Columns).Concat(inbound.SelectMany(static x => x.Key.LinkedColumns)))
        {
            if (!wanted.Contains(column, StringComparer.Ordinal) && DatabaseCatalogQueries.IsQuotable(column))
            {
                wanted.Add(column);
            }
        }

        TableRowStatement keyRead = TableRowQueryBuilder.BuildKeyRead(relation.Schema, relation.Name, matched.Columns, matched.Values, wanted);
        List<string> statements = [keyRead.Sql];

        try
        {
            await using NpgsqlConnection connection = grant.Resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // One row's references are 1 + N statements, run one after another: each is held to a short
            // statement_timeout of its own, and all of them to one budget, past which the rest are left
            // unchecked rather than queued - a row with forty inbound keys is not forty QueryTimeouts.
            var budget = new ReferenceBudget(
                ReferenceStatementTimeout(Options.QueryTimeout),
                ReferenceBudgetFor(Options.QueryTimeout));

            TableRowReferences references = await Session(statementTimeout: budget.PerStatement)
                .InTransactionAsync(
                    connection,
                    (transaction, token) => ReadReferencesAsync(connection, transaction, keyRead, wanted, outbound, inbound, statements, budget, token),
                    cancellationToken)
                .ConfigureAwait(false);

            LogKey(grant, ReferencesAction, matched);

            return references;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return new TableRowReferences { Error = Fail(exception, grant, ReferencesAction), Statements = statements };
        }
    }

    private async Task<TableRowReferences> ReadReferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableRowStatement keyRead,
        List<string> wanted,
        List<OutboundCheck> outbound,
        List<InboundCheck> inbound,
        List<string> statements,
        ReferenceBudget budget,
        CancellationToken cancellationToken)
    {
        budget.Start();

        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        bool found = false;

        await using (NpgsqlCommand command = keyRead.CreateCommand(connection, transaction, CommandTimeoutSeconds))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                found = true;

                for (int i = 0; i < wanted.Count; i++)
                {
                    int ordinal = keyRead.Layout.ValueOrdinals[i];
                    values[wanted[i]] = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
                }
            }
        }

        if (!found)
        {
            return new TableRowReferences { Found = false, Reason = "There is no row with that key.", Statements = statements };
        }

        List<RowOutboundReference> resolvedOut = [];

        // A column that could not be quoted was never read, and a value that was never read is not a NULL:
        // such a key keeps the verdict its judgement gave it.
        foreach (OutboundCheck check in outbound)
        {
            IReadOnlyList<string?> own = [.. check.Key.Columns.Select(column => values.GetValueOrDefault(column))];
            bool read = check.Key.Columns.All(values.ContainsKey);
            resolvedOut.Add(await ResolveOutboundAsync(connection, transaction, check, own, read, statements, budget, cancellationToken).ConfigureAwait(false));
        }

        List<RowInboundReference> resolvedIn = [];

        foreach (InboundCheck check in inbound)
        {
            IReadOnlyList<string?> own = [.. check.Key.LinkedColumns.Select(column => values.GetValueOrDefault(column))];
            bool read = check.Key.LinkedColumns.All(values.ContainsKey);
            resolvedIn.Add(await ResolveInboundAsync(connection, transaction, check, own, read, statements, budget, cancellationToken).ConfigureAwait(false));
        }

        return new TableRowReferences
        {
            Found = true,
            Outbound = resolvedOut,
            Inbound = resolvedIn,
            Statements = statements,
        };
    }

    private async Task<OutboundCheck> JudgeOutboundAsync(
        DatabaseRowGrant grant,
        DatabaseGate gate,
        DatabaseForeignKeyInfo key,
        CancellationToken cancellationToken)
    {
        if (!key.LinkedVisible || key.LinkedSchema is null || key.LinkedTable is null)
        {
            return new OutboundCheck(key, RowReferenceState.NotVisible, null, null,
                "It points into a schema the studio does not show you.");
        }

        if (key.OtherEndOwnership is { Owner: DatabaseObjectOwner.MartenDocument } document)
        {
            return new OutboundCheck(key, RowReferenceState.MartenDocument, null, document,
                "A Marten document: open it in Documents, where tenancy and soft delete apply.");
        }

        if (!key.Columns.All(DatabaseCatalogQueries.IsQuotable) || !key.LinkedColumns.All(DatabaseCatalogQueries.IsQuotable))
        {
            return new OutboundCheck(key, RowReferenceState.NotChecked, null, null, "Its column names cannot be put into SQL safely.");
        }

        try
        {
            CatalogRelationDetail? parent = await catalog
                .RelationAsync(grant.Resolved.Database, key.LinkedSchema, key.LinkedTable, cancellationToken)
                .ConfigureAwait(false);

            if (parent is null || gate.Classifier.ClassifyRelation(key.LinkedSchema, key.LinkedTable) is not { } ownership)
            {
                return new OutboundCheck(key, RowReferenceState.NotVisible, null, null, "The table it points at is not one the studio shows you.");
            }

            DatabaseRowAccess rows = gate.RowsFor(parent.Relation, ownership, dependsOnHidden: false);

            return rows.Allowed
                ? new OutboundCheck(key, RowReferenceState.Present, parent, ownership, null)
                : new OutboundCheck(key, RowReferenceState.NotChecked, parent, ownership, rows.Reason);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return new OutboundCheck(key, RowReferenceState.Failed, null, null, DatabaseAccess.CatalogFailure(exception));
        }
    }

    private async Task<InboundCheck> JudgeInboundAsync(
        DatabaseRowGrant grant,
        DatabaseGate gate,
        DatabaseForeignKeyInfo key,
        CancellationToken cancellationToken)
    {
        if (key.OtherEndOwnership is { Owner: DatabaseObjectOwner.MartenDocument } document)
        {
            return new InboundCheck(key, RowInboundState.MartenDocument, document,
                "A Marten document table: its rows are browsed in Documents, where tenancy and soft delete apply.");
        }

        if (!key.Columns.All(DatabaseCatalogQueries.IsQuotable) || !key.LinkedColumns.All(DatabaseCatalogQueries.IsQuotable))
        {
            return new InboundCheck(key, RowInboundState.NotChecked, null, "Its column names cannot be put into SQL safely.");
        }

        try
        {
            CatalogRelationDetail? child = await catalog
                .RelationAsync(grant.Resolved.Database, key.Schema, key.Table, cancellationToken)
                .ConfigureAwait(false);

            if (child is null || gate.Classifier.ClassifyRelation(key.Schema, key.Table) is not { } ownership)
            {
                return new InboundCheck(key, RowInboundState.NotChecked, null, "The pointing table is not one the studio shows you.");
            }

            DatabaseRowAccess rows = gate.RowsFor(child.Relation, ownership, dependsOnHidden: false);

            if (!rows.Allowed)
            {
                return new InboundCheck(key, RowInboundState.NotChecked, ownership, rows.Reason);
            }

            // A bounded count still reads every row of a large table when no index finds the pointing rows:
            // "limit 1000" stops at the thousandth match, and a table with none scans to the end.
            return UnindexedInboundCount(child, key.Columns, Options.ExactCountThreshold) is { } unindexed
                ? new InboundCheck(key, RowInboundState.NotChecked, ownership, unindexed)
                : new InboundCheck(key, RowInboundState.Counted, ownership, null);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return new InboundCheck(key, RowInboundState.Failed, null, DatabaseAccess.CatalogFailure(exception));
        }
    }

    private async Task<RowOutboundReference> ResolveOutboundAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OutboundCheck check,
        IReadOnlyList<string?> own,
        bool read,
        List<string> statements,
        ReferenceBudget budget,
        CancellationToken cancellationToken)
    {
        DatabaseForeignKeyInfo key = check.Key;
        bool anyNull = read && (own.Count == 0 || own.Any(static x => x is null));

        RowOutboundReference reference = new(
            key.Name,
            key.Columns,
            own,
            key.LinkedSchema,
            key.LinkedTable,
            key.LinkedColumns,
            key.Validated,
            anyNull ? RowReferenceState.NoReference : check.State,
            false,
            null,
            null,
            null,
            null,
            null,
            anyNull ? "A key column is NULL, so this row points at nothing." : check.Reason);

        if (!read || anyNull || check.State is RowReferenceState.NotVisible or RowReferenceState.NotChecked or RowReferenceState.Failed)
        {
            return reference;
        }

        string[] values = [.. own.Select(static x => x!)];

        if (check.State == RowReferenceState.MartenDocument)
        {
            int idIndex = Math.Max(0, IndexOf(key.LinkedColumns, "id", StringComparison.OrdinalIgnoreCase));

            return reference with
            {
                DocumentAlias = check.Ownership?.Alias,
                DocumentId = idIndex < values.Length ? values[idIndex] : values[0],
                StoreKey = check.Ownership?.StoreKey,
            };
        }

        DatabaseRowKey? parentKey = check.Parent is null ? null : DatabaseObjectAssembler.RowKey(check.Parent);
        bool targetsKey = parentKey is not null
            && parentKey.Columns.Count == key.LinkedColumns.Count
            && parentKey.Columns.All(column => key.LinkedColumns.Contains(column, StringComparer.Ordinal));

        Dictionary<string, string>? parentKeyValues = null;

        if (targetsKey)
        {
            parentKeyValues = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string column in parentKey!.Columns)
            {
                parentKeyValues[column] = values[IndexOf(key.LinkedColumns, column, StringComparison.Ordinal)];
            }
        }

        reference = reference with
        {
            TargetsRowKey = targetsKey,
            ParentKey = parentKeyValues,
            ParentFilter = RowFilterGrammar.Format(key.LinkedColumns.Select((column, i) => (column, RowFilterOperator.Equal, (string?) values[i]))),
        };

        if (budget.Spent)
        {
            return reference with { State = RowReferenceState.NotChecked, Reason = budget.Sentence };
        }

        TableRowStatement exists = TableRowQueryBuilder.BuildExists(key.LinkedSchema!, key.LinkedTable!, key.LinkedColumns, values);
        statements.Add(exists.Sql);

        (bool ok, bool present, TableRowError? error) = await TryScalarAsync<bool>(connection, transaction, exists, budget, cancellationToken)
            .ConfigureAwait(false);

        return ok
            ? reference with
            {
                State = present ? RowReferenceState.Present : RowReferenceState.Missing,
                Reason = present
                    ? null
                    : key.Validated
                        ? "The row it names does not exist."
                        : "The row it names does not exist: this foreign key is NOT VALID, so rows older than it were never checked.",
            }
            : reference with { State = RowReferenceState.Failed, Reason = error!.Sentence };
    }

    private async Task<RowInboundReference> ResolveInboundAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        InboundCheck check,
        IReadOnlyList<string?> own,
        bool read,
        List<string> statements,
        ReferenceBudget budget,
        CancellationToken cancellationToken)
    {
        DatabaseForeignKeyInfo key = check.Key;
        bool anyNull = read && (own.Count == 0 || own.Any(static x => x is null));

        RowInboundReference reference = new(
            key.Name,
            key.Schema,
            key.Table,
            key.Columns,
            key.LinkedColumns,
            own,
            anyNull ? RowInboundState.NoReference : check.State,
            null,
            false,
            anyNull || !read ? null : RowFilterGrammar.Format(key.Columns.Select((column, i) => (column, RowFilterOperator.Equal, own[i]))),
            check.State == RowInboundState.MartenDocument ? check.Ownership?.Alias : null,
            key.OnDelete,
            anyNull ? "A referenced column of this row is NULL, so nothing can point at it through this key." : check.Reason);

        if (!read || anyNull || check.State != RowInboundState.Counted)
        {
            return reference;
        }

        if (budget.Spent)
        {
            return reference with { State = RowInboundState.NotChecked, Reason = budget.Sentence };
        }

        TableRowStatement count = TableRowQueryBuilder.BuildInboundCount(
            key.Schema, key.Table, key.Columns, [.. own.Select(static x => x!)], TableRowQueryBuilder.InboundCap);
        statements.Add(count.Sql);

        (bool ok, long counted, TableRowError? error) = await TryScalarAsync<long>(connection, transaction, count, budget, cancellationToken)
            .ConfigureAwait(false);

        if (!ok)
        {
            return reference with { State = RowInboundState.Failed, Reason = error!.Sentence };
        }

        bool more = counted >= TableRowQueryBuilder.InboundCap;

        return reference with { Count = more ? TableRowQueryBuilder.InboundCap - 1 : counted, More = more };
    }

    /// <summary>
    /// One scalar statement inside a savepoint, so that a reference check Postgres refuses - a column
    /// privilege the role lacks, a timeout - fails alone rather than aborting the transaction for the rest.
    /// </summary>
    private async Task<(bool Ok, T Value, TableRowError? Error)> TryScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableRowStatement statement,
        ReferenceBudget budget,
        CancellationToken cancellationToken)
    {
        const string savepoint = "ms_reference";

        await transaction.SaveAsync(savepoint, cancellationToken).ConfigureAwait(false);

        try
        {
            T value = await ScalarAsync<T>(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
            await transaction.ReleaseAsync(savepoint, cancellationToken).ConfigureAwait(false);
            return (true, value, null);
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync(savepoint, cancellationToken).ConfigureAwait(false);

            TableRowError error = Describe(exception, null, Options);

            // The per-check limit fired, not QueryTimeout: the sentence names the limit that did.
            return (false, default!, exception.SqlState == "57014" ? error with { Sentence = budget.TimeoutSentence } : error);
        }
    }

    private async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableRowStatement statement,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = statement.CreateCommand(connection, transaction, CommandTimeoutSeconds);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return (T) Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------------------

    private Task<(List<TableRow> Rows, List<string?> SortValues, bool Shortened)> ReadPageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableRowStatement statement,
        CancellationToken cancellationToken) =>
        ReadRowsAsync(connection, transaction, statement, TableRowCaps.List, cancellationToken);

    /// <summary>
    /// The rows, and each row's raw sort value beside it - one per row, so the page can take the one that
    /// belongs to the last row it keeps - and whether the read's byte budget cut any cell short.
    /// </summary>
    private async Task<(List<TableRow> Rows, List<string?> SortValues, bool Shortened)> ReadRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableRowStatement statement,
        TableRowCaps caps,
        CancellationToken cancellationToken)
    {
        List<TableRow> rows = [];
        List<string?> sortValues = [];
        TableRowLayout layout = statement.Layout;
        IReadOnlyList<string> keyColumns = layout.KeyColumns;
        var budget = new CellBudget(caps);

        await using NpgsqlCommand command = statement.CreateCommand(connection, transaction, CommandTimeoutSeconds);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, string>? key = null;

            if (layout.KeyOrdinals.Count > 0)
            {
                key = new Dictionary<string, string>(StringComparer.Ordinal);

                for (int i = 0; i < layout.KeyOrdinals.Count; i++)
                {
                    key[keyColumns[i]] = reader.GetString(layout.KeyOrdinals[i]);
                }
            }

            string? locator = layout.LocatorOrdinal >= 0 ? reader.GetString(layout.LocatorOrdinal) : null;
            sortValues.Add(layout.SortOrdinal >= 0 && !reader.IsDBNull(layout.SortOrdinal) ? reader.GetString(layout.SortOrdinal) : null);

            var cells = new SqlCell[layout.Cells.Count];

            for (int i = 0; i < cells.Length; i++)
            {
                cells[i] = budget.Keep(ReadCell(reader, layout.Cells[i], caps, budget.OverBudget));
            }

            rows.Add(new TableRow(key, locator, cells));
        }

        return (rows, sortValues, budget.Shortened);
    }

    private async Task<TableCellValue> ReadCellAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TableRowStatement statement,
        TableCellValue shell,
        int cap,
        CancellationToken cancellationToken)
    {
        TableRowCellLayout cell = statement.Layout.Cells[0];

        await using NpgsqlCommand command = statement.CreateCommand(connection, transaction, CommandTimeoutSeconds);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return shell with { Found = false, Reason = "There is no row with that key - it may have been deleted or changed." };
        }

        if (reader.IsDBNull(cell.ValueOrdinal))
        {
            return shell with { Found = true, IsNull = true };
        }

        long length = reader.GetInt32(cell.LengthOrdinal);

        if (cell.Shape == TableRowCellShape.Binary)
        {
            byte[] bytes = reader.GetFieldValue<byte[]>(cell.ValueOrdinal);
            return shell with { Found = true, Bytes = bytes, Truncated = length > bytes.Length, FullLength = length };
        }

        string probe = reader.GetString(cell.ValueOrdinal);
        (string text, bool truncated) = CutToUtf8Bytes(probe, cap);

        return shell with { Found = true, Text = text, Truncated = truncated, FullLength = length };
    }

    /// <summary>One cell of a row, from the columns its layout names.</summary>
    /// <param name="reader">The reader, on the row.</param>
    /// <param name="layout">Where the cell's expressions are.</param>
    /// <param name="caps">The caps.</param>
    /// <param name="overBudget">
    /// Whether the read's byte budget is spent, so the cell keeps only <see cref="TableRowCaps.OverBudget" />
    /// bytes.
    /// </param>
    internal static SqlCell ReadCell(NpgsqlDataReader reader, TableRowCellLayout layout, TableRowCaps caps, bool overBudget = false)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(caps);

        if (reader.IsDBNull(layout.ValueOrdinal))
        {
            return SqlCell.Null;
        }

        switch (layout.Shape)
        {
            case TableRowCellShape.Native:
                try
                {
                    return Formatter.Format(reader.GetValue(layout.ValueOrdinal), layout.Type);
                }
                catch (Exception exception) when (exception is InvalidCastException or NotSupportedException or OverflowException)
                {
                    // A timestamp outside .NET's range, say: the page says so rather than losing the row.
                    return new SqlCell("(cannot read " + layout.Type + ")", SqlCellKind.Text, false, 0);
                }

            case TableRowCellShape.Binary:
                return BinaryCell(
                    reader.GetFieldValue<byte[]>(layout.ValueOrdinal),
                    reader.GetInt32(layout.LengthOrdinal),
                    overBudget ? Math.Min(caps.Binary, Math.Max(caps.OverBudget / 2, 1)) : caps.Binary);

            default:
                int cap = layout.Shape == TableRowCellShape.Json ? caps.Json : caps.Text;

                return TextCell(
                    reader.GetString(layout.ValueOrdinal),
                    reader.GetInt32(layout.LengthOrdinal),
                    overBudget ? Math.Min(cap, caps.OverBudget) : cap,
                    TableRowQueryBuilder.KindOf(layout.Type));
        }
    }

    /// <summary>
    /// A text-form cell from what the server sent - at most one character past the cap - cut to
    /// <paramref name="cap" /> UTF-8 bytes on a character boundary, with the value's full size in bytes.
    /// </summary>
    internal static SqlCell TextCell(string probe, int fullBytes, int cap, SqlCellKind kind)
    {
        ArgumentNullException.ThrowIfNull(probe);

        (string text, bool truncated) = CutToUtf8Bytes(probe, cap);

        return new SqlCell(truncated ? text + "…" : text, kind, truncated, fullBytes);
    }

    /// <summary>A <c>bytea</c> cell: the first bytes as <c>\x…</c> hex, and the octet length.</summary>
    internal static SqlCell BinaryCell(byte[] prefix, int fullBytes, int cap)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        int shown = Math.Min(prefix.Length, cap);
        bool truncated = fullBytes > shown;
        var text = new StringBuilder(2 + (shown * 2) + 24).Append("\\x");

        for (int i = 0; i < shown; i++)
        {
            text.Append(prefix[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        if (truncated)
        {
            text.Append('…');
        }

        text.Append(" (").Append(fullBytes.ToString(CultureInfo.InvariantCulture)).Append(" bytes)");

        return new SqlCell(text.ToString(), SqlCellKind.Binary, truncated, fullBytes);
    }

    /// <summary>
    /// The first <paramref name="cap" /> code points of <paramref name="probe" />, and whether there were
    /// more. Postgres' <c>substring</c> counts code points; a .NET string counts UTF-16 units, and cutting
    /// between a surrogate pair would leave half a character on screen.
    /// </summary>
    internal static (string Text, bool Truncated) CutToCodePoints(string probe, int cap)
    {
        ArgumentNullException.ThrowIfNull(probe);

        int index = 0;
        int codePoints = 0;

        while (index < probe.Length)
        {
            if (codePoints == cap)
            {
                return (probe[..index], true);
            }

            index += char.IsHighSurrogate(probe[index]) && index + 1 < probe.Length && char.IsLowSurrogate(probe[index + 1]) ? 2 : 1;
            codePoints++;
        }

        return (probe, false);
    }

    /// <summary>
    /// The longest prefix of <paramref name="probe" /> that is at most <paramref name="cap" /> bytes of UTF-8,
    /// cut between characters, and whether anything was left out.
    /// </summary>
    /// <remarks>
    /// The server sends one character more than the cap, and a character is never less than one byte: so a
    /// probe that fits is the whole value, and one that does not was cut - whatever the database's own
    /// encoding makes of <c>octet_length</c>. A surrogate pair is one four-byte character and is never split;
    /// a lone surrogate counts as the three bytes of the replacement character it would be sent as.
    /// </remarks>
    internal static (string Text, bool Truncated) CutToUtf8Bytes(string probe, int cap)
    {
        ArgumentNullException.ThrowIfNull(probe);

        int index = 0;
        long bytes = 0;

        while (index < probe.Length)
        {
            char c = probe[index];
            bool pair = char.IsHighSurrogate(c) && index + 1 < probe.Length && char.IsLowSurrogate(probe[index + 1]);
            int width = pair ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;

            if (bytes + width > cap)
            {
                return (probe[..index], true);
            }

            bytes += width;
            index += pair ? 2 : 1;
        }

        return (probe, false);
    }

    // ---------------------------------------------------------------------------------------------------
    // Planning
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// How a page of <paramref name="relation" /> is walked: keyset on the row key; keyset on <c>ctid</c> for
    /// a heap relation with no key on PG 14+; offset otherwise.
    /// </summary>
    /// <param name="relation">The relation, as the catalog has it.</param>
    /// <param name="columns">Its columns.</param>
    /// <param name="rowKey">Its row key, or <see langword="null" />.</param>
    /// <param name="preference">What the page asked for.</param>
    /// <param name="sortColumn">The resolved sort column, or <see langword="null" />.</param>
    /// <param name="descending">The direction.</param>
    /// <param name="tidRange">Whether the server can range-scan by ctid (PG 14+).</param>
    internal static TableRowPlan Plan(
        CatalogRelation relation,
        IReadOnlyList<DatabaseColumnInfo> columns,
        DatabaseRowKey? rowKey,
        TableRowPagingPreference preference,
        string? sortColumn,
        bool descending,
        bool tidRange)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(columns);

        Dictionary<string, DatabaseColumnInfo> byName = columns.ToDictionary(static x => x.Name, StringComparer.Ordinal);

        IReadOnlyList<string> key = rowKey is not null
            && rowKey.Columns.All(column => byName.TryGetValue(column, out DatabaseColumnInfo? info) && info.Quotable)
            ? rowKey.Columns
            : [];

        bool heap = relation.Kind is "r" or "m";
        bool locator = key.Count == 0 && heap;

        // The key alone, in either direction, is a plain row-value walk: a sort by the one key column is the
        // same walk, written the simple way.
        if (key.Count == 1 && string.Equals(sortColumn, key[0], StringComparison.Ordinal))
        {
            sortColumn = null;
        }

        TableRowPagingMode mode;
        string? note = null;

        if (preference == TableRowPagingPreference.Offset)
        {
            mode = TableRowPagingMode.Offset;
        }
        else if (key.Count > 0)
        {
            mode = TableRowPagingMode.Key;
        }
        else if (locator && tidRange)
        {
            mode = TableRowPagingMode.Ctid;
            note = "No key, so it pages by physical position (ctid). A row that is updated can move and appear again.";
        }
        else
        {
            mode = TableRowPagingMode.Offset;
            note = relation.Kind switch
            {
                "v" => "A view has no key, so it pages by offset - each page runs the view's query - up to " +
                       TableRowQueryBuilder.MaxOffset.ToString("N0", CultureInfo.InvariantCulture) + " rows in.",
                "p" => "A partitioned table with no key pages by offset: a ctid repeats from one partition to the next.",
                _ => "Postgres before 14 cannot range-scan by ctid, so a table with no key pages by offset.",
            };
        }

        List<string> tieBreak = key.Count == 0 && !locator
            ? [.. columns.Where(static x => x.Sortable && x.Quotable).OrderBy(static x => x.Position).Select(static x => x.Name)]
            : [];

        if (mode == TableRowPagingMode.Offset && key.Count == 0 && !locator && tieBreak.Count == 0 && sortColumn is null)
        {
            note = (note is null ? string.Empty : note + " ") +
                   "None of its columns can be ordered, so pages may repeat or skip rows.";
        }

        return new TableRowPlan(mode, key, locator, tieBreak, sortColumn, descending, note);
    }

    /// <summary>Whether Postgres can range-scan by <c>ctid</c> (PG 14 and later).</summary>
    private static bool SupportsTidRange(NpgsqlConnection connection) => connection.PostgreSqlVersion.Major >= 14;

    private static (string? Column, string? Problem) ResolveSort(DatabaseRowGrant grant, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return (null, null);
        }

        DatabaseColumnInfo? column = ResolveColumn(grant.Columns, requested);

        if (column is null)
        {
            return (null, "There is no column '" + requested + "' to sort by.");
        }

        if (!column.Quotable)
        {
            return (null, "'" + column.Name + "' cannot be put into SQL safely, so it cannot be sorted by.");
        }

        return column.Sortable
            ? (column.Name, null)
            : (null, "'" + column.Name + "' is " + column.Type + ", which has no ordering (no default btree operator class), so it cannot be sorted by.");
    }

    /// <summary>A column by name: exactly, or case-insensitively when that is unambiguous.</summary>
    internal static DatabaseColumnInfo? ResolveColumn(IReadOnlyList<DatabaseColumnInfo> columns, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        DatabaseColumnInfo? exact = columns.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

        if (exact is not null)
        {
            return exact;
        }

        List<DatabaseColumnInfo> folded = [.. columns.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))];

        return folded.Count == 1 ? folded[0] : null;
    }

    /// <summary>
    /// The page's verdict strip: a chip per term, an error chip per error, and the sort's chip - each with
    /// whether a valid index leads with its column.
    /// </summary>
    /// <param name="detail">The relation's catalog detail, whose indexes are read.</param>
    /// <param name="rowKey">Its row key, or <see langword="null" />.</param>
    /// <param name="parse">The filter.</param>
    /// <param name="sortColumn">The sort column, or <see langword="null" /> for the key order.</param>
    /// <param name="mode">How the page is walked.</param>
    internal static RowFilterVerdict BuildVerdict(
        CatalogRelationDetail detail,
        DatabaseRowKey? rowKey,
        RowFilterParse parse,
        string? sortColumn,
        TableRowPagingMode mode)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(parse);

        List<SearchChip> chips = [];
        IndexVerdictLevel filterLevel = IndexVerdictLevel.Green;

        foreach (RowFilterTerm term in parse.Terms)
        {
            (IndexVerdictLevel level, string reason) = term.Operator == RowFilterOperator.Contains
                ? (IndexVerdictLevel.Red, "A substring match reads the text of every row; no index serves it.")
                : IndexVerdict(detail, term.Column);

            chips.Add(new SearchChip(SearchChipKind.Predicate, RowFilterGrammar.Describe(term), level, reason, null));
            filterLevel = level > filterLevel ? level : filterLevel;
        }

        foreach (SearchGrammarError error in parse.Errors)
        {
            chips.Add(new SearchChip(SearchChipKind.Error, error.Message, IndexVerdictLevel.Red, error.Message, null));
        }

        (IndexVerdictLevel sortLevel, string sortReason) = sortColumn is { } sorted
            ? IndexVerdict(detail, sorted)
            : mode switch
            {
                TableRowPagingMode.Key => (IndexVerdictLevel.Green, "The row key's index serves this order."),
                TableRowPagingMode.Ctid => (IndexVerdictLevel.Green, "Physical order: no index is needed."),
                _ => rowKey is null
                    ? (IndexVerdictLevel.Amber, "No key: pages by offset, over every row before the page.")
                    : (IndexVerdictLevel.Green, "The row key's index serves this order."),
            };

        chips.Add(new SearchChip(SearchChipKind.Sort, "sort", sortLevel, sortReason, null));

        return new RowFilterVerdict
        {
            Terms = parse.Terms,
            Errors = parse.Errors,
            Chips = chips,
            FilterLevel = parse.HasErrors ? IndexVerdictLevel.Red : filterLevel,
            SortLevel = sortLevel,
        };
    }

    /// <summary>Whether a valid index leads with <paramref name="column" />, and the sentence.</summary>
    internal static (IndexVerdictLevel Level, string Reason) IndexVerdict(CatalogRelationDetail detail, string column)
    {
        ArgumentNullException.ThrowIfNull(detail);

        if (detail.Relation.Kind == "v")
        {
            return (IndexVerdictLevel.Amber, "A view has no indexes of its own; whether its query can use one is up to Postgres.");
        }

        CatalogIndex? leading = detail.Indexes.FirstOrDefault(index => index.IsValid && !index.HasPredicate
            && index.KeyColumns.Count > 0 && string.Equals(index.KeyColumns[0], column, StringComparison.Ordinal));

        if (leading is not null)
        {
            return (IndexVerdictLevel.Green, column + " leads the index " + leading.Name + ".");
        }

        CatalogIndex? partial = detail.Indexes.FirstOrDefault(index => index.IsValid
            && index.KeyColumns.Count > 0 && string.Equals(index.KeyColumns[0], column, StringComparison.Ordinal));

        if (partial is not null)
        {
            return (IndexVerdictLevel.Amber, column + " leads the partial index " + partial.Name + ", which serves only the rows its predicate matches.");
        }

        CatalogIndex? inside = detail.Indexes.FirstOrDefault(index => index.IsValid
            && index.KeyColumns.Any(key => string.Equals(key, column, StringComparison.Ordinal)));

        return inside is not null
            ? (IndexVerdictLevel.Amber, column + " is in the index " + inside.Name + ", but does not lead it.")
            : (IndexVerdictLevel.Red, "No index covers " + column + "; filtering or sorting on it reads every row.");
    }

    /// <summary>
    /// Why a read is withheld until "Run anyway", or <see langword="null" />: the relation is above
    /// <see cref="MartenStudioOptions.ExactCountThreshold" /> (or, never analysed, above the heap size a
    /// speculative count would scan) and a filter or sort column is one no index serves.
    /// </summary>
    internal static string? ShouldWithhold(CatalogRelation relation, RowFilterVerdict verdict, long threshold)
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(verdict);

        if (verdict.FilterLevel != IndexVerdictLevel.Red && verdict.SortLevel != IndexVerdictLevel.Red)
        {
            return null;
        }

        bool large = relation.Kind is "r" or "p" or "m"
            && (relation.EstimatedRows is { } rows
                ? rows > Math.Max(threshold, 0)
                : relation.SizeBytes > CountEstimator.MaxSpeculativePages * 8192L);

        if (!large)
        {
            return null;
        }

        string size = relation.EstimatedRows is { } estimate
            ? "about " + estimate.ToString("N0", CultureInfo.InvariantCulture) + " rows"
            : "a large table Postgres has never analysed";

        string what = verdict.FilterLevel == IndexVerdictLevel.Red ? "a filter" : "the sort";

        return "Not read yet: " + Target(relation) + " holds " + size + " (more than MartenStudioOptions.ExactCountThreshold, " +
               Math.Max(threshold, 0).ToString("N0", CultureInfo.InvariantCulture) + "), and " + what +
               " no index serves would read every one of them. Run anyway to read it, or filter on an indexed column.";
    }

    // ---------------------------------------------------------------------------------------------------
    // Columns and keys
    // ---------------------------------------------------------------------------------------------------

    private static List<TableRowColumn> Columns(DatabaseRowGrant grant, IReadOnlyList<DatabaseForeignKeyInfo> outbound)
    {
        List<TableRowColumn> columns = [];

        foreach (DatabaseColumnInfo column in grant.Columns.OrderBy(static x => x.Position))
        {
            int keyIndex = grant.RowKey is { } key ? IndexOf(key.Columns, column.Name, StringComparison.Ordinal) : -1;
            (IndexVerdictLevel level, string reason) = IndexVerdict(grant.Relation, column.Name);

            columns.Add(new TableRowColumn(
                column.Name,
                column.Position,
                column.Type,
                column.Nullable,
                column.Sortable,
                column.Quotable,
                TableRowQueryBuilder.KindOf(column.Type),
                keyIndex >= 0 ? keyIndex + 1 : null,
                [
                    .. outbound
                        .Where(foreignKey => foreignKey.Columns.Contains(column.Name, StringComparer.Ordinal))
                        .Select(static foreignKey => new TableRowColumnReference(foreignKey.Name, foreignKey.LinkedSchema, foreignKey.LinkedTable)),
                ],
                level,
                reason,
                column.Comment));
        }

        return columns;
    }

    private static List<TableRowColumn> SelectColumns(
        IReadOnlyList<TableRowColumn> available,
        DatabaseRowGrant grant,
        IReadOnlyList<string>? requested)
    {
        List<TableRowColumn> readable = [.. available.Where(static x => x.Shown)];

        if (requested is null)
        {
            return readable;
        }

        HashSet<string> wanted = new(StringComparer.Ordinal);

        foreach (string name in requested)
        {
            if (ResolveColumn(grant.Columns, name) is { } column)
            {
                wanted.Add(column.Name);
            }
        }

        if (wanted.Count == 0 && grant.RowKey is { } key)
        {
            wanted.UnionWith(key.Columns);
        }

        List<TableRowColumn> chosen = [.. readable.Where(x => wanted.Contains(x.Name))];

        return chosen.Count > 0 ? chosen : readable;
    }

    /// <summary>
    /// The key a caller passed, matched to the row key's columns in key order - or a <c>ctid</c> for a heap
    /// relation with no key, where <paramref name="allowLocator" /> says a cell may be read that way - or
    /// <see langword="null" /> when it does not name exactly one row key.
    /// </summary>
    /// <remarks>
    /// A column is matched by its exact name, or by a name that differs only in case when exactly one entry
    /// does. Two entries that could both be one column (<c>?key.REGION_CODE=SE&amp;key.Region_Code=SE</c>) name
    /// nothing, and neither does an entry used for two columns: the key comes from a URL, and an ambiguous
    /// one is a refusal the page can say, never an exception.
    /// </remarks>
    /// <param name="rowKey">The relation's row key, or <see langword="null" />.</param>
    /// <param name="relationKind">The relation's <c>relkind</c>.</param>
    /// <param name="key">What the caller passed.</param>
    /// <param name="allowLocator">Whether a <c>ctid</c> may stand in for a key.</param>
    internal static MatchedKey? KeyOf(
        DatabaseRowKey? rowKey,
        string relationKind,
        IReadOnlyDictionary<string, string> key,
        bool allowLocator)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (rowKey is not null && key.Count == rowKey.Columns.Count)
        {
            List<string> values = [];
            HashSet<string> used = new(StringComparer.Ordinal);

            foreach (string column in rowKey.Columns)
            {
                if (!DatabaseCatalogQueries.IsQuotable(column))
                {
                    return null;
                }

                string? entry = key.ContainsKey(column) ? column : OnlyCaseVariant(key, column);

                if (entry is null || !used.Add(entry) || !key.TryGetValue(entry, out string? value) || value is null)
                {
                    return null;
                }

                values.Add(value);
            }

            return new MatchedKey(rowKey.Columns, values, null);
        }

        if (allowLocator && rowKey is null && relationKind is "r" or "m"
            && key.Count == 1 && key.TryGetValue("ctid", out string? locator) && !string.IsNullOrEmpty(locator))
        {
            return new MatchedKey([], [], locator);
        }

        return null;
    }

    /// <summary>The one entry whose name is <paramref name="column" /> but for case, or <see langword="null" /> for none or several.</summary>
    private static string? OnlyCaseVariant(IReadOnlyDictionary<string, string> key, string column)
    {
        string? found = null;

        foreach (string candidate in key.Keys)
        {
            if (!string.Equals(candidate, column, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = candidate;
        }

        return found;
    }

    private static string KeyProblem(DatabaseRowGrant grant, bool allowLocator)
    {
        if (grant.RowKey is { } key)
        {
            return "A row of " + Target(grant.Relation.Relation) + " is named by its key: one value for each of " +
                   string.Join(", ", key.Columns) + ".";
        }

        return allowLocator && grant.Relation.Relation.Kind is "r" or "m"
            ? Target(grant.Relation.Relation) + " has no key; a cell of one of its rows is read by that row's ctid."
            : Target(grant.Relation.Relation) + " has no key, so a row of it cannot be opened on its own; its rows are shown inline.";
    }

    // ---------------------------------------------------------------------------------------------------
    // Audit, logging, failures
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a read of a relation's rows is recorded by: its filter, its sort and how it is walked - never
    /// where in the walk it starts, how many rows it asks for or which columns it shows. Two reads with one
    /// signature are one opening: the second is a page turn or a refresh.
    /// </summary>
    /// <param name="request">
    /// What was asked. Its <see cref="TableRowRequest.Cursor" />, <see cref="TableRowRequest.Offset" />,
    /// <see cref="TableRowRequest.PageSize" />, <see cref="TableRowRequest.Columns" /> and
    /// <see cref="TableRowRequest.RunAnyway" /> are deliberately not part of it.
    /// </param>
    /// <param name="parse">The filter, parsed against the catalog's columns.</param>
    /// <param name="plan">The walk: the resolved sort, its direction and the paging mode.</param>
    internal static string AuditSignature(TableRowRequest request, RowFilterParse parse, TableRowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(plan);

        return FilterSignature(parse) + Separator + SortText(plan) + Separator + plan.Mode;
    }

    private static string FilterSignature(RowFilterParse parse) => string.Join(' ', parse.Terms.Select(RowFilterGrammar.Describe));

    private static string SortText(TableRowPlan plan) => (plan.SortColumn ?? "(key)") + (plan.Descending ? " desc" : " asc");

    /// <summary>
    /// One ring entry, and event 9235, for every first contact with a relation's rows under a filter, sort
    /// and walk this circuit has not recorded for that store, database and relation - whatever page the read
    /// starts on, because a cursor or an offset in a pasted URL is a first contact that never saw a first
    /// page. A page turn and a refresh repeat the last signature and record nothing. Values go to event 9235
    /// only.
    /// </summary>
    private void AuditRead(DatabaseRowGrant grant, TableRowRequest request, RowFilterParse parse, TableRowPlan plan)
    {
        CatalogRelation relation = grant.Relation.Relation;
        string target = Target(relation);
        string filter = FilterSignature(parse);
        string sort = SortText(plan);
        string signature = AuditSignature(request, parse, plan);

        if (!ledger.TryRecord(LedgerKey(grant, "rows"), signature, out string? previous))
        {
            return;
        }

        bool filterChanged = previous is not null && !previous.StartsWith(filter + Separator, StringComparison.Ordinal);
        string action = filterChanged ? FilterAction : DatabaseAccess.RowsAction;

        // Column names and the sort, never a value: the ring is readable by anyone with the read policy.
        string message = (parse.Terms.Count == 0
                             ? "Rows read, sorted by " + sort
                             : "Rows read with " + parse.Terms.Count.ToString(CultureInfo.InvariantCulture) + " filter term(s) on " +
                               string.Join(", ", parse.Terms.Select(static x => x.Column).Distinct(StringComparer.Ordinal)) + ", sorted by " + sort)
                         + (request.IsFirstPage ? "." : ", starting past the first page.");

        audit.Record(action, target, succeeded: true, message, StudioCapability.BrowseDatabase, grant.Resolved.Scope);

        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        logger.TableRowsRead(
            UserName(),
            action,
            target,
            grant.Resolved.Registration.Key,
            grant.Resolved.Database.Id.Identity,
            request.Filter ?? string.Empty,
            sort);
    }

    /// <summary>
    /// One ring entry, and event 9235 with the filter as typed, for a filtered count this circuit has not
    /// recorded with that filter for that store, database and relation.
    /// </summary>
    private void AuditCount(DatabaseRowGrant grant, string? filter, RowFilterParse parse)
    {
        if (!ledger.TryRecord(LedgerKey(grant, "count"), FilterSignature(parse), out _))
        {
            return;
        }

        string target = Target(grant.Relation.Relation);

        audit.Record(
            CountAction,
            target,
            succeeded: true,
            "Rows counted with " + parse.Terms.Count.ToString(CultureInfo.InvariantCulture) + " filter term(s) on " +
            string.Join(", ", parse.Terms.Select(static x => x.Column).Distinct(StringComparer.Ordinal)) + ".",
            StudioCapability.BrowseDatabase,
            grant.Resolved.Scope);

        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        logger.TableRowsRead(
            UserName(),
            CountAction,
            target,
            grant.Resolved.Registration.Key,
            grant.Resolved.Database.Id.Identity,
            filter ?? string.Empty,
            "(a count)");
    }

    /// <summary>What the ledger remembers a read under: the store, the database and the relation, and which kind of read.</summary>
    private static string LedgerKey(DatabaseRowGrant grant, string kind) =>
        TableRowAuditLedger.Key(
            grant.Resolved.Registration.Key,
            grant.Resolved.Database.Id.Identity,
            grant.Relation.Relation.Schema,
            grant.Relation.Relation.Name,
            kind);

    private void LogKey(DatabaseRowGrant grant, string action, MatchedKey key)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        string text = key.Locator is { } locator
            ? "ctid=" + locator
            : string.Join(", ", key.Columns.Select((column, i) => column + "=" + key.Values[i]));

        logger.TableRowOpened(
            UserName(),
            action,
            Target(grant.Relation.Relation),
            grant.Resolved.Registration.Key,
            grant.Resolved.Database.Id.Identity,
            text);
    }

    private TableRowError Fail(Exception exception, DatabaseRowGrant grant, string action)
    {
        TableRowError error = Describe(exception, grant.Relation.Relation, Options);

        LogLevel level = IsExpected(error.SqlState, exception)
            ? LogLevel.Debug
            : throttle.WarningOrDebug(
                "TableRowService.Read",
                grant.Resolved.Registration.Key,
                grant.Resolved.Database.Id.Identity,
                StudioLogThrottle.KindOf(exception));

        if (logger.IsEnabled(level))
        {
            logger.TableRowReadFailed(
                level,
                exception,
                action,
                Target(grant.Relation.Relation),
                grant.Resolved.Registration.Key,
                grant.Resolved.Database.Id.Identity,
                error.SqlState.Length > 0 ? error.SqlState : null);
        }

        return error;
    }

    /// <summary>
    /// Whether a failure is one the page explains in words and nothing is wrong with the studio or the
    /// database: the visitor's own timeout, a view never refreshed, a privilege the role was not given,
    /// row-level security, a lock somebody else holds, a relation changed under the page, a value that does
    /// not fit its column.
    /// </summary>
    internal static bool IsExpected(string sqlState, Exception exception) =>
        sqlState is "57014" or "55000" or "42501" or "42704" or "55P03" or "42P01" or "42703"
        || sqlState.StartsWith("22", StringComparison.Ordinal)
        || exception is TimeoutException;

    /// <summary>A failed read as a value: the SQLSTATE and a sentence that names what governs it.</summary>
    internal static TableRowError Describe(Exception exception, CatalogRelation? relation, MartenStudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(options);

        if (exception is PostgresException postgres)
        {
            string timeout = options.QueryTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";

            string sentence = postgres.SqlState switch
            {
                "55000" when relation?.Kind == "m" => "This materialized view has never been refreshed.",
                "42501" => options.SqlConsoleRole is { } role
                    ? "The role '" + role + "' (MartenStudioOptions.SqlConsoleRole) has no privilege to read this."
                    : "The store's Postgres role has no privilege to read this.",
                "57014" => "The read took longer than MartenStudioOptions.QueryTimeout (" + timeout + ") and Postgres cancelled it." +
                           (relation?.Kind == "v"
                               ? " A view runs its whole query for every page."
                               : " Filter on an indexed column, or raise QueryTimeout."),
                "42704" when relation?.RowSecurity == true =>
                    "Row-level security is on for this table, and one of its policies needs something this session does " +
                    "not have - often a setting the application sets for itself and the studio does not.",
                "42704" => "Postgres could not find an object the read needs.",
                "55P03" => "The read waited more than three seconds for a lock - somebody is changing this relation - and gave up.",
                "42P01" => "The relation no longer exists.",
                "42703" => "A column no longer exists: the relation changed since the page was opened.",
                _ when postgres.SqlState.StartsWith("22", StringComparison.Ordinal) =>
                    "A filter or key value does not fit its column's type.",
                _ => "Postgres refused the read.",
            };

            return new TableRowError(postgres.SqlState, sentence, postgres.MessageText);
        }

        return exception switch
        {
            TimeoutException => new TableRowError(string.Empty, "The read timed out before Postgres answered.", exception.Message),
            NpgsqlException => new TableRowError(string.Empty, "The database could not be reached.", exception.Message),
            _ => new TableRowError(string.Empty, "The read failed.", exception.Message),
        };
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is NpgsqlException or InvalidOperationException or TimeoutException or InvalidCastException
        && exception is not OperationCanceledException;

    private ReadOnlySqlSession Session(int maxRows = 500, TimeSpan? statementTimeout = null) => new(new ReadOnlySqlOptions
    {
        StatementTimeout = statementTimeout ?? Options.QueryTimeout,
        Role = Options.SqlConsoleRole,
        MaxRows = Math.Max(maxRows, 1),
    });

    /// <summary>
    /// The <c>statement_timeout</c> each of one row's reference checks runs under: the lesser of
    /// <paramref name="queryTimeout" /> and <see cref="ReferenceStatementCap" />.
    /// </summary>
    internal static TimeSpan ReferenceStatementTimeout(TimeSpan queryTimeout) =>
        queryTimeout > TimeSpan.Zero && queryTimeout < ReferenceStatementCap ? queryTimeout : ReferenceStatementCap;

    /// <summary>
    /// How long one row's reference checks may take together before the rest are left unchecked: the lesser
    /// of <paramref name="queryTimeout" /> and <see cref="ReferenceBudgetCap" />.
    /// </summary>
    internal static TimeSpan ReferenceBudgetFor(TimeSpan queryTimeout) =>
        queryTimeout > TimeSpan.Zero && queryTimeout < ReferenceBudgetCap ? queryTimeout : ReferenceBudgetCap;

    /// <summary>
    /// Why the rows of <paramref name="child" /> that point at a row are not counted - it is larger than
    /// <paramref name="threshold" /> (or, never analysed, larger than a speculative count would scan) and no
    /// valid, unconditional index leads with one of <paramref name="columns" /> - or <see langword="null" />
    /// when counting them is cheap enough to do.
    /// </summary>
    /// <param name="child">The pointing table's catalog detail, indexes included.</param>
    /// <param name="columns">Its foreign key's columns.</param>
    /// <param name="threshold"><see cref="MartenStudioOptions.ExactCountThreshold" />.</param>
    internal static string? UnindexedInboundCount(CatalogRelationDetail child, IReadOnlyList<string> columns, long threshold)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(columns);

        CatalogRelation relation = child.Relation;
        long limit = Math.Max(threshold, 0);

        bool large = relation.EstimatedRows is { } rows
            ? rows > limit
            : relation.SizeBytes > CountEstimator.MaxSpeculativePages * 8192L;

        if (!large)
        {
            return null;
        }

        bool indexed = child.Indexes.Any(index => index.IsValid
            && !index.HasPredicate
            && index.KeyColumns.Count > 0
            && index.KeyColumns[0] is { } first
            && columns.Contains(first, StringComparer.Ordinal));

        if (indexed)
        {
            return null;
        }

        string size = relation.EstimatedRows is { } estimate
            ? "about " + estimate.ToString("N0", CultureInfo.InvariantCulture) + " rows"
            : "a large table Postgres has never analysed";

        return "Not counted: " + Target(relation) + " holds " + size + " (more than MartenStudioOptions.ExactCountThreshold, " +
               limit.ToString("N0", CultureInfo.InvariantCulture) + ") and no index leads with " + string.Join(", ", columns) +
               ", so counting the rows that point here would read every one of them. Filter its rows by the key instead.";
    }

    /// <summary>
    /// The relation's detail through this visitor's gate - what names the far ends of its foreign keys -
    /// or <see langword="null" /> when the gate cannot be read; a header without "→ parent" markers is
    /// still a page.
    /// </summary>
    private async Task<DatabaseObjectDetail?> DetailAsync(DatabaseRowGrant grant, CancellationToken cancellationToken)
    {
        try
        {
            DatabaseGateRead gateRead = await access.GateAsync(grant.Resolved.Scope, grant.Resolved, cancellationToken)
                .ConfigureAwait(false);

            return gateRead.Succeeded ? DatabaseObjectAssembler.Detail(gateRead.Gate, grant.Relation) : null;
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return null;
        }
    }

    private string UserName()
    {
        Task<AuthenticationState> state = authentication.GetAuthenticationStateAsync();

        if (!state.IsCompletedSuccessfully)
        {
            return "(unknown)";
        }

        string? name = state.Result.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
    }

    private static string Target(CatalogRelation relation) => DatabaseAccess.Target(relation.Schema, relation.Name);

    private static int IndexOf(IReadOnlyList<string> list, string value, StringComparison comparison)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, comparison))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A row key matched to its columns, or a ctid.</summary>
    /// <param name="Columns">The row key's columns, in key order - empty for a ctid.</param>
    /// <param name="Values">The caller's values for them, in the same order.</param>
    /// <param name="Locator">The ctid, for a relation with no key.</param>
    internal sealed record MatchedKey(IReadOnlyList<string> Columns, IReadOnlyList<string> Values, string? Locator);

    /// <summary>
    /// The time one row's reference checks are given: a <c>statement_timeout</c> for each, and a budget for
    /// all of them, measured from the first.
    /// </summary>
    /// <param name="perStatement">Each check's <c>statement_timeout</c>.</param>
    /// <param name="total">All of them together.</param>
    private sealed class ReferenceBudget(TimeSpan perStatement, TimeSpan total)
    {
        private readonly System.Diagnostics.Stopwatch clock = new();

        /// <summary>Each check's <c>statement_timeout</c>.</summary>
        public TimeSpan PerStatement { get; } = perStatement;

        /// <summary>All of them together.</summary>
        public TimeSpan Total { get; } = total;

        /// <summary>Whether the budget is gone, so no further check starts.</summary>
        public bool Spent => clock.IsRunning && clock.Elapsed >= Total;

        /// <summary>What a check left unchecked for want of time says.</summary>
        public string Sentence =>
            "Not checked: this row's reference checks had " + Seconds(Total) + " between them (the lesser of " +
            "MartenStudioOptions.QueryTimeout and " + Seconds(ReferenceBudgetCap) + "), and the checks before this one used it.";

        /// <summary>What a check its own <c>statement_timeout</c> stopped says.</summary>
        public string TimeoutSentence =>
            "The check took longer than the " + Seconds(PerStatement) + " each reference check is given (the lesser of " +
            "MartenStudioOptions.QueryTimeout and " + Seconds(ReferenceStatementCap) + "), and Postgres cancelled it.";

        /// <summary>Starts the clock, once.</summary>
        public void Start()
        {
            if (!clock.IsRunning)
            {
                clock.Start();
            }
        }

        private static string Seconds(TimeSpan value) =>
            value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// The bytes of cell text one read has kept so far, against <see cref="TableRowCaps.Budget" />, and
    /// whether the budget cut any cell short.
    /// </summary>
    internal sealed class CellBudget(TableRowCaps caps)
    {
        private long spent;

        /// <summary>Whether the budget is spent, so every further cell keeps <see cref="TableRowCaps.OverBudget" /> bytes.</summary>
        public bool OverBudget => spent >= caps.Budget;

        /// <summary>Whether any cell was cut because the budget was spent.</summary>
        public bool Shortened { get; private set; }

        /// <summary>Counts <paramref name="cell" /> against the budget and hands it back.</summary>
        public SqlCell Keep(SqlCell cell)
        {
            if (OverBudget && cell.IsTruncated)
            {
                Shortened = true;
            }

            spent += Encoding.UTF8.GetByteCount(cell.Text);
            return cell;
        }
    }

    /// <summary>An outbound key, judged before the row is read.</summary>
    private sealed record OutboundCheck(
        DatabaseForeignKeyInfo Key,
        RowReferenceState State,
        CatalogRelationDetail? Parent,
        DatabaseObjectOwnership? Ownership,
        string? Reason);

    /// <summary>An inbound key, judged before the row is read.</summary>
    private sealed record InboundCheck(
        DatabaseForeignKeyInfo Key,
        RowInboundState State,
        DatabaseObjectOwnership? Ownership,
        string? Reason);
}

/// <summary>How one page is walked, as decided from the relation and the request.</summary>
/// <param name="Mode">Keyset on the key, keyset on ctid, or offset.</param>
/// <param name="KeyColumns">The row key, when it can be quoted.</param>
/// <param name="SelectLocator">Whether each row's ctid is read - a heap relation with no key.</param>
/// <param name="TieBreakColumns">The deterministic order after the sort, for a relation with neither.</param>
/// <param name="SortColumn">The sort column, or <see langword="null" /> for the key order.</param>
/// <param name="Descending">The direction.</param>
/// <param name="Note">Why this mode, when it is not the obvious one.</param>
internal sealed record TableRowPlan(
    TableRowPagingMode Mode,
    IReadOnlyList<string> KeyColumns,
    bool SelectLocator,
    IReadOnlyList<string> TieBreakColumns,
    string? SortColumn,
    bool Descending,
    string? Note)
{
    /// <summary>Whether <paramref name="cursor" /> is a position in this walk (or there is none).</summary>
    public bool Accepts(TableRowCursor? cursor)
    {
        if (cursor is null || Mode == TableRowPagingMode.Offset)
        {
            return true;
        }

        bool ctid = Mode == TableRowPagingMode.Ctid;

        return cursor.IsCtid == ctid
            && cursor.Key.Count == (ctid ? 1 : KeyColumns.Count)
            && string.Equals(cursor.SortColumn, SortColumn, StringComparison.Ordinal)
            && cursor.Descending == Descending;
    }

    /// <summary>The cursor after <paramref name="last" />.</summary>
    public TableRowCursor CursorAfter(TableRow last, string? lastSort, bool descending)
    {
        ArgumentNullException.ThrowIfNull(last);

        IReadOnlyList<string> key = Mode == TableRowPagingMode.Ctid
            ? [last.Locator!]
            : [.. KeyColumns.Select(column => last.Key![column])];

        return new TableRowCursor(SortColumn, descending, SortColumn is null ? null : lastSort, key, Mode == TableRowPagingMode.Ctid);
    }

    /// <summary>The paging note's facts.</summary>
    public TableRowPaging Describe(int offset) => new(
        Mode,
        Mode == TableRowPagingMode.Ctid || (Mode == TableRowPagingMode.Offset && KeyColumns.Count == 0 && SelectLocator)
            ? ["ctid"]
            : KeyColumns.Count > 0 ? KeyColumns : TieBreakColumns,
        SortColumn,
        Descending ? SortDirection.Descending : SortDirection.Ascending,
        Mode == TableRowPagingMode.Offset ? offset : 0,
        Note);
}

/// <summary>
/// What one circuit has already written to the audit ring about row reads: per store, database, relation
/// and kind of read, the signature of the last read recorded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by where the rows are, not by their name.</b> <c>legacy.orders</c> in another store or in
/// another database of a multi-database store is other rows, so a read of it is a first contact of its
/// own - a key of the relation's name alone let the second one pass unrecorded.
/// </para>
/// <para>
/// <b>Bounded.</b> A circuit that visits more than <see cref="Capacity" /> relations forgets them all and
/// starts again, which can only ever record a read twice - never skip one.
/// </para>
/// </remarks>
internal sealed class TableRowAuditLedger
{
    /// <summary>How many keys the ledger holds before it forgets them all.</summary>
    internal const int Capacity = 512;

    private const char Separator = '\u001f';

    private readonly Dictionary<string, string> signatures = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>How many keys are remembered - for a test.</summary>
    internal int Count
    {
        get
        {
            lock (gate)
            {
                return signatures.Count;
            }
        }
    }

    /// <summary>The ledger key for one kind of read of one relation, in one database of one store.</summary>
    /// <param name="storeKey">The store's registration key.</param>
    /// <param name="databaseIdentity">The database's identity (<c>IMartenDatabase.Id.Identity</c>).</param>
    /// <param name="schema">The relation's schema, as the catalog spells it.</param>
    /// <param name="name">The relation's name, as the catalog spells it.</param>
    /// <param name="kind">Which kind of read: <c>rows</c> or <c>count</c>.</param>
    public static string Key(string storeKey, string databaseIdentity, string schema, string name, string kind) =>
        storeKey + Separator + databaseIdentity + Separator + schema + Separator + name + Separator + kind;

    /// <summary>
    /// Whether a read under <paramref name="key" /> with <paramref name="signature" /> is one to record - it
    /// is, unless the last one recorded under that key had the same signature - and remembers it if so.
    /// </summary>
    /// <param name="key">From <see cref="Key" />.</param>
    /// <param name="signature">The read's signature.</param>
    /// <param name="previous">The last signature recorded under the key, or <see langword="null" /> on a first contact.</param>
    public bool TryRecord(string key, string signature, out string? previous)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(signature);

        lock (gate)
        {
            if (signatures.TryGetValue(key, out previous) && string.Equals(previous, signature, StringComparison.Ordinal))
            {
                return false;
            }

            if (previous is null && signatures.Count >= Capacity)
            {
                signatures.Clear();
            }

            signatures[key] = signature;
            return true;
        }
    }
}
