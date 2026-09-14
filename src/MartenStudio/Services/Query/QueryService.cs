using System.Diagnostics;
using System.Globalization;
using System.Reflection;

using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Services.Query;

/// <summary>
/// The Query page's two modes, with the gate in front of the dangerous one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mode A - a Marten <c>where</c> clause - is a read and needs no capability, and it runs the studio's
/// own composed SQL.</b> It used to run through
/// <c>Marten.QuerySessionExtensions.QueryAsync(session, Type, sql, token, parameters)</c>, the one
/// string-query overload that takes a runtime <see cref="Type" />. That overload composes through
/// <c>UserSuppliedQueryHandler</c> and <c>DocumentStorage.Apply</c>, which produce
/// <c>select … from &lt;table&gt; as d &lt;clause&gt;</c> with <b>no tenant filter and no soft-delete
/// filter</b> - so on a conjoined, soft-deleted collection a clause of <c>where 1 = 1</c> typed by
/// somebody scoped to one tenant returned every tenant's rows and every deleted row. The studio therefore
/// composes the statement itself (<see cref="QuerySqlComposer.Compose" />), with its own predicates in
/// front of the visitor's, and runs it on <c>IMartenDatabase.CreateConnection(ConnectionUsage.Read)</c> -
/// which is also what makes the SQL tab, the command that ran and the statement <c>EXPLAIN</c> planned all
/// the same text. Rows come back as <c>data::text</c> and are never deserialized, so no property can be
/// dropped on the way to the screen (AGENTS.md hard rule 10 the easy way: nothing serializes).
/// </para>
/// <para>
/// <b>Mode B - the SQL console - is the one that is gated</b>, in this order, and the order is the
/// security property: <see cref="StudioCapabilityGuard.Require" /> for
/// <see cref="StudioCapability.RunSql" />, then <see cref="StudioScopeResolver.ResolveAsync" /> with the
/// capability named so <see cref="MartenStudioOptions.WriteAuthorizationPolicy" /> is the policy asked,
/// then <see cref="ReadOnlySqlGuard" /> for a nicer message, and only then the statement - inside
/// <see cref="ReadOnlySqlSession" />'s read-only transaction, which is what actually makes it safe (D13).
/// Every outcome is audited, failures included (hard rule 5).
/// </para>
/// <para>
/// The plan for a Mode A clause is Mode B work: fetching it means sending a statement to Postgres, so it
/// goes through the very same gate and is refused - as a value, not an exception - when the console
/// capability is off. That is why a studio without <c>RunSql</c> shows the composed SQL and says there is
/// no plan, rather than quietly running an <c>EXPLAIN</c> nobody granted.
/// </para>
/// </remarks>
internal sealed class QueryService : IQueryService
{
    /// <summary>What the audit ring calls a Mode A plan fetch.</summary>
    internal const string ExplainAction = "ExplainQuery";

    /// <summary>What the audit ring calls a SQL console run.</summary>
    internal const string RunSqlAction = "RunSql";

    /// <summary>What the audit ring calls a Marten where clause.</summary>
    /// <remarks>
    /// <b>Every run, not only the refusals.</b> A clause is arbitrary SQL against one collection, run with
    /// no capability; "who read what, with which filter, in which tenant" is exactly the question an
    /// incident asks, and a read is cheap to record. Refusals are written the same way with
    /// <c>succeeded: false</c> (hard rule 5).
    /// </remarks>
    internal const string MartenQueryAction = "MartenQuery";

    /// <summary>How much of a statement goes into the audit ring's target column.</summary>
    internal const int AuditTargetLength = 200;

    private const string Unknown = "(unknown)";

    private readonly StudioScopeResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly ColumnCatalog columnCatalog;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly ILogger<QueryService> logger;
    private readonly AuthenticationStateProvider authenticationStateProvider;

    public QueryService(
        StudioScopeResolver resolver,
        StudioAuthorization authorization,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        ColumnCatalog columnCatalog,
        IOptions<MartenStudioOptions> options,
        ILogger<QueryService> logger,
        AuthenticationStateProvider authenticationStateProvider)
    {
        this.resolver = resolver;
        this.authorization = authorization;
        this.capabilities = capabilities;
        this.audit = audit;
        this.columnCatalog = columnCatalog;
        this.options = options;
        this.logger = logger;
        this.authenticationStateProvider = authenticationStateProvider;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QueryDocumentTypeInfo>> ListDocumentTypesAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        List<QueryDocumentTypeInfo> types = [];

        foreach (IDocumentType documentType in VisibleDocumentTypes(resolved))
        {
            DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType);

            types.Add(new QueryDocumentTypeInfo(
                documentType.Alias,
                documentType.DocumentType.Name,
                documentType.DocumentType.FullName ?? documentType.DocumentType.Name,
                table.Schema,
                table.Table,
                table.QualifiedName,
                [.. table.DuplicatedColumns.Select(static x => x.MemberPath)],
                table.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined,
                table.SoftDeleteEnabled));
        }

        types.Sort(static (left, right) => CompareAliases(left.Alias, right.Alias));

        return types;
    }

    /// <inheritdoc />
    public async Task<MartenQueryResult> RunMartenQueryAsync(
        StudioScope scope,
        MartenQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        IDocumentType documentType = FindDocumentType(resolved, request.Alias)
            ?? throw new KeyNotFoundException(
                $"Marten store '{scope.StoreKey}' has no visible document type with alias '{request.Alias}'.");

        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType);
        MartenStudioOptions value = options.Value;
        int limit = Math.Clamp(request.PageSize ?? value.DefaultPageSize, 1, value.MaxPageSize);
        string? tenantId = scope.TenantId is { Length: > 0 } scoped ? scoped : null;
        string user = UserName();
        string clause = request.WhereClause ?? string.Empty;
        string target = Target(clause);

        // A where clause needs no capability, so it has to be a clause. Without RunSql it may read only the
        // table that was picked; with RunSql the nested-read rules are lifted, because everything they
        // refuse the same person could type into the console. The shape rules - one statement, not a
        // statement of its own, and a tail the composer can place - hold either way: Mode A runs outside
        // the console's read-only transaction, so a second statement here would really write.
        bool mayRunSql = await MayRunSqlAsync(scope, cancellationToken).ConfigureAwait(false);
        SqlGuardResult clauseGuard = QuerySqlComposer.CheckClause(request.WhereClause, mayRunSql);

        if (!clauseGuard.Allowed)
        {
            SqlRejection rejected = SqlRejection.FromGuard(clauseGuard);
            ComposedQuery refusedShape = QuerySqlComposer.Compose(
                table, request.WhereClause, limit, tenantId, request.IncludeDeleted);

            audit.Record(MartenQueryAction, target, succeeded: false, rejected.Message, null, scope);
            logger.SqlRejected(user, scope.StoreKey, scope.DatabaseId, rejected.Message, clause);

            return new MartenQueryResult(
                documentType.Alias, [], refusedShape.Statement, refusedShape.DescribeParameters(), TimeSpan.Zero,
                limit, refusedShape.LimitApplied, null,
                ExplainResult.Unavailable("The clause was never sent."), ScopeOf(refusedShape, table),
                refusedShape.Predicate, rejected);
        }

        await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The configuration describes the table Marten would create; the studio reads one somebody else
        // migrated. Reconciling before the predicates are written is what keeps a physical tenant_id or
        // mt_deleted the mapping has forgotten about from silently going unfiltered. The catalog is cached,
        // so this is one information_schema read per table per process.
        table = table.WithPhysicalColumns(
            await columnCatalog.GetAsync(connection, table.Schema, table.Table, cancellationToken)
                .ConfigureAwait(false));

        ComposedQuery composed = QuerySqlComposer.Compose(
            table, request.WhereClause, limit, tenantId, request.IncludeDeleted);

        MartenQueryScope queryScope = ScopeOf(composed, table);
        string statement = composed.Statement;
        var stopwatch = Stopwatch.StartNew();
        List<MartenQueryRow> rows = [];

        try
        {
            await using NpgsqlCommand command = new(composed.Statement, connection)
            {
                // Every command the studio issues carries a timeout (plan §4.8). A clause somebody is still
                // writing is exactly the kind of query that turns into a sequential scan of ten million rows.
                CommandTimeout = (int) Math.Ceiling(value.QueryTimeout.TotalSeconds),
            };

            composed.Bind(command);

            await using NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadRow(reader, composed));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && FindPostgresException(exception) is { } postgres)
        {
            stopwatch.Stop();

            // A malformed clause is a value, not a failure: it is the single most common thing that
            // happens on this page, and the person typing needs the SQLSTATE and the position.
            string reason = QuerySqlErrors.Summarize(ToSqlError(postgres));

            audit.Record(MartenQueryAction, target, succeeded: false, reason, null, scope);
            logger.SqlRejected(user, scope.StoreKey, scope.DatabaseId, reason, statement);

            return new MartenQueryResult(
                documentType.Alias,
                [],
                composed.Statement,
                composed.DescribeParameters(),
                stopwatch.Elapsed,
                composed.Limit,
                composed.LimitApplied,
                ToSqlError(postgres),
                await TryExplainAsync(scope, composed, cancellationToken).ConfigureAwait(false),
                queryScope,
                composed.Predicate);
        }
        catch (OperationCanceledException)
        {
            audit.Record(MartenQueryAction, target, succeeded: false, "cancelled", null, scope);
            throw;
        }

        stopwatch.Stop();

        // Successful reads are audited too. A clause is arbitrary SQL against one collection that needs no
        // capability at all, so "who read what, in which tenant" is precisely the question an incident asks.
        long elapsedMilliseconds = (long) stopwatch.Elapsed.TotalMilliseconds;

        audit.Record(
            MartenQueryAction, target, succeeded: true, Outcome(rows.Count, stopwatch.Elapsed), null, scope);
        logger.SqlExecuted(
            user, scope.StoreKey, scope.DatabaseId, elapsedMilliseconds, rows.Count, statement);

        return new MartenQueryResult(
            documentType.Alias,
            rows,
            composed.Statement,
            composed.DescribeParameters(),
            stopwatch.Elapsed,
            composed.Limit,
            composed.LimitApplied,
            null,
            await TryExplainAsync(scope, composed, cancellationToken).ConfigureAwait(false),
            queryScope,
            composed.Predicate);
    }

    /// <summary>One row of a composed Mode A read, straight out of the columns the composer selected.</summary>
    /// <remarks>
    /// <c>data</c> arrives as text and stays text. There is no CLR type to deserialize into on this path
    /// and, deliberately, no attempt to find one: the page renders JSON strings, and a document that
    /// round-tripped through a type would quietly lose every property the type does not have (D7 is about
    /// making that loss visible when it is unavoidable, not about doing it for a read).
    /// </remarks>
    private static MartenQueryRow ReadRow(NpgsqlDataReader reader, ComposedQuery composed)
    {
        object? id = reader.IsDBNull(ComposedQuery.IdOrdinal) ? null : reader.GetValue(ComposedQuery.IdOrdinal);
        string json = reader.IsDBNull(ComposedQuery.DataOrdinal)
            ? string.Empty
            : reader.GetString(ComposedQuery.DataOrdinal);

        string? tenant = composed.TenantOrdinal >= 0 && !reader.IsDBNull(composed.TenantOrdinal)
            ? reader.GetString(composed.TenantOrdinal)
            : null;

        bool? deleted = composed.DeletedOrdinal >= 0 && !reader.IsDBNull(composed.DeletedOrdinal)
            ? reader.GetBoolean(composed.DeletedOrdinal)
            : null;

        return new MartenQueryRow(
            id is null ? string.Empty : Convert.ToString(id, CultureInfo.InvariantCulture) ?? string.Empty,
            json,
            tenant,
            deleted);
    }

    private static MartenQueryScope ScopeOf(ComposedQuery composed, DocumentTableInfo table) =>
        new(composed.TenantId, composed.CrossTenant, composed.Deleted, table.SoftDeleteEnabled);

    private static string Outcome(int rows, TimeSpan duration) =>
        rows.ToString(CultureInfo.InvariantCulture) + " rows in " +
        ((long) duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";

    /// <inheritdoc />
    public async Task<SqlConsoleResult> RunSqlAsync(
        StudioScope scope,
        SqlConsoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        return await ExecuteStatementAsync(scope, request.Statement, RunSqlAction, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<QueryExamples> BuildExamplesAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        List<QueryExampleSource> sources = [];

        foreach (IDocumentType documentType in VisibleDocumentTypes(resolved))
        {
            DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType);

            sources.Add(new QueryExampleSource(
                documentType.Alias,
                table.QualifiedName,
                PickPropertyName(documentType, table),
                table.HasMetadata(DocumentMetadataColumn.LastModified)));
        }

        sources.Sort(static (left, right) => CompareAliases(left.Alias, right.Alias));

        return QueryExampleBuilder.Build(sources, resolved.Store.Options.Events.DatabaseSchemaName);
    }

    /// <summary>
    /// Whether this visitor may run SQL against this scope, which is what lifts the clause guard's
    /// nested-read rules.
    /// </summary>
    /// <remarks>
    /// Both halves, in the order the rest of the studio asks them: the process-wide capability (D4), and
    /// then the per-visitor write policy against this very scope. Asking only the first would hand
    /// subqueries to somebody the <see cref="MartenStudioOptions.WriteAuthorizationPolicy" /> refuses the
    /// console to, which is the hole the policy exists to close.
    /// </remarks>
    private async Task<bool> MayRunSqlAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        if (!capabilities.IsEnabled(StudioCapability.RunSql))
        {
            return false;
        }

        return await authorization
            .IsAuthorizedAsync(scope, nameof(StudioCapability.RunSql), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The plan for a composed Mode A statement, or the reason there is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every refusal is caught here and becomes a value. A plan is a courtesy on a page whose main job
    /// already succeeded, and a studio that threw away a working result because the explain was not
    /// permitted would be worse than one that says "no plan, and here is why".
    /// </para>
    /// <para>
    /// <b>The same statement, with the same values bound.</b> The composed statement carries <c>@tenant</c>
    /// and <c>@limit</c>, so the explain has to bind them too - otherwise Postgres answers "there is no
    /// parameter $1" and the page would be showing a plan for something other than what ran.
    /// </para>
    /// </remarks>
    private async Task<ExplainResult> TryExplainAsync(
        StudioScope scope,
        ComposedQuery composed,
        CancellationToken cancellationToken)
    {
        if (!capabilities.IsEnabled(StudioCapability.RunSql))
        {
            return ExplainResult.Unavailable(
                capabilities.ReadOnly
                    ? "No plan: MartenStudioOptions.ReadOnly is true, and a plan has to be fetched from Postgres."
                    : "No plan: fetching one runs EXPLAIN, which needs " +
                      StudioCapabilityGuard.OptionName(StudioCapability.RunSql) + ".");
        }

        try
        {
            SqlConsoleResult result = await ExecuteStatementAsync(
                    scope,
                    QuerySqlComposer.Explain(composed.Statement, json: true),
                    ExplainAction,
                    cancellationToken,
                    composed.Bind)
                .ConfigureAwait(false);

            if (result.Rejection is { } rejection)
            {
                return ExplainResult.Unavailable(rejection.Message);
            }

            if (result.Error is { } error)
            {
                return new ExplainResult(null, null, error, null);
            }

            string? plan = result.Rows.Count > 0 && result.Rows[0].Count > 0 ? result.Rows[0][0].Text : null;

            return new ExplainResult(null, plan, null, plan is null ? "Postgres returned no plan." : null);
        }
        catch (StudioCapabilityDeniedException denied)
        {
            return ExplainResult.Unavailable(denied.Message);
        }
        catch (StudioNotAuthorizedException)
        {
            return ExplainResult.Unavailable("No plan: you are not authorized to run SQL against this store.");
        }
    }

    /// <summary>
    /// The gate, and then the statement. Everything that reaches Postgres through the console path goes
    /// through here.
    /// </summary>
    /// <param name="scope">The scope the statement runs in.</param>
    /// <param name="statement">The statement, exactly as it will be sent.</param>
    /// <param name="action">What the audit ring calls it.</param>
    /// <param name="cancellationToken">The token.</param>
    /// <param name="bind">
    /// Binds the statement's parameters, when it has any. The console itself never does - what somebody
    /// typed is sent verbatim - but a Mode A <c>EXPLAIN</c> has to bind the very same values the query
    /// bound, or it would be planning a different statement.
    /// </param>
    private async Task<SqlConsoleResult> ExecuteStatementAsync(
        StudioScope scope,
        string statement,
        string action,
        CancellationToken cancellationToken,
        Action<NpgsqlCommand>? bind = null)
    {
        MartenStudioOptions value = options.Value;
        string target = Target(statement);
        string user = UserName();

        // 1. The capability. Refused before anything is resolved, and recorded either way.
        try
        {
            capabilities.Require(StudioCapability.RunSql);
        }
        catch (StudioCapabilityDeniedException denied)
        {
            audit.RecordCapabilityDenied(denied, action, target);
            logger.SqlRejected(user, scope.StoreKey, scope.DatabaseId, denied.Message, statement);
            throw;
        }

        // 2. The scope, with the capability named so the write policy is the one asked. It is a read, but
        //    it is the dangerous read, and D13 is explicit that it is treated as a write.
        ResolvedScope resolved;
        try
        {
            resolved = await resolver
                .ResolveAsync(scope, nameof(StudioCapability.RunSql), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, WritePolicyName(value), action, target);
            logger.SqlRejected(
                user, scope.StoreKey, scope.DatabaseId, "not authorized for this scope", statement);
            throw;
        }

        // 3. The advisory guard. Not the security boundary - the transaction below is - but it is what
        //    turns "delete from …" into a sentence instead of a SQLSTATE.
        SqlGuardResult guard = ReadOnlySqlGuard.Check(statement);
        if (!guard.Allowed)
        {
            SqlRejection rejection = SqlRejection.FromGuard(guard);

            audit.Record(action, target, succeeded: false, rejection.Message, StudioCapability.RunSql, scope);
            logger.SqlRejected(user, scope.StoreKey, scope.DatabaseId, rejection.Reason, statement);

            return SqlConsoleResult.Refused(rejection, value.MaxSqlConsoleRows);
        }

        return await RunAsync(resolved, scope, statement, action, value, user, bind, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SqlConsoleResult> RunAsync(
        ResolvedScope resolved,
        StudioScope scope,
        string statement,
        string action,
        MartenStudioOptions value,
        string user,
        Action<NpgsqlCommand>? bind,
        CancellationToken cancellationToken)
    {
        List<string> notices = [];
        string target = Target(statement);

        void OnNotice(object? sender, NpgsqlNoticeEventArgs args)
        {
            // Synchronous by construction: an `async` handler on an event is an async void with different
            // spelling, and AGENTS.md hard rule 6 forbids it in every shape.
            if (notices.Count < 50)
            {
                notices.Add(args.Notice.Severity + ": " + args.Notice.MessageText);
            }
        }

        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = value.QueryTimeout,
            MaxRows = value.MaxSqlConsoleRows,
            Role = value.SqlConsoleRole,
        });

        await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
        connection.Notice += OnNotice;

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            SqlResultSet result = await session
                .ExecuteAsync(connection, statement, bind, cancellationToken)
                .ConfigureAwait(false);

            long elapsed = (long) result.Duration.TotalMilliseconds;

            if (result.Error is { } error)
            {
                string reason = QuerySqlErrors.Summarize(error);

                audit.Record(action, target, succeeded: false, reason, StudioCapability.RunSql, scope);
                logger.SqlRejected(user, scope.StoreKey, scope.DatabaseId, reason, statement);
            }
            else
            {
                string outcome = Outcome(result.Rows.Count, result.Duration);

                audit.Record(action, target, succeeded: true, outcome, StudioCapability.RunSql, scope);
                logger.SqlExecuted(user, scope.StoreKey, scope.DatabaseId, elapsed, result.Rows.Count, statement);
            }

            return new SqlConsoleResult(
                result.Columns,
                result.Rows,
                result.Truncated,
                value.MaxSqlConsoleRows,
                result.Duration,
                notices,
                result.Error,
                null);
        }
        catch (NpgsqlException exception) when (exception is not PostgresException)
        {
            // The connection itself, rather than the statement: nothing ran, and the page says so with the
            // same shape it uses for a SQLSTATE.
            audit.Record(action, target, succeeded: false, exception.Message, StudioCapability.RunSql, scope);
            logger.SqlRejected(user, scope.StoreKey, scope.DatabaseId, exception.Message, statement);

            return new SqlConsoleResult(
                [],
                [],
                false,
                value.MaxSqlConsoleRows,
                TimeSpan.Zero,
                notices,
                new SqlError("08006", exception.Message, 0, null, null),
                null);
        }
        catch (OperationCanceledException)
        {
            audit.Record(action, target, succeeded: false, "cancelled", StudioCapability.RunSql, scope);
            throw;
        }
        finally
        {
            connection.Notice -= OnNotice;
        }
    }

    private IEnumerable<IDocumentType> VisibleDocumentTypes(ResolvedScope resolved)
    {
        Func<Type, bool>? isVisible = options.Value.IsDocumentTypeVisible;

        foreach (IDocumentType documentType in resolved.Store.Options.AllKnownDocumentTypes())
        {
            // The filter is applied in the data layer and not only in navigation: a type the host hid must
            // not be reachable by typing its alias into the URL.
            if (isVisible is null || isVisible(documentType.DocumentType))
            {
                yield return documentType;
            }
        }
    }

    /// <summary>
    /// Marten's own dead-letter collection, which a store gets for free as soon as it has an event store.
    /// </summary>
    internal const string DeadLetterAlias = "deadletterevent";

    /// <summary>
    /// Orders aliases, with Marten's own bookkeeping collection last.
    /// </summary>
    /// <remarks>
    /// <c>DeadLetterEvent</c> is a document type the host never registered: it comes with the event store.
    /// It stays in the picker - it is queryable, and the studio does not hide tables - but it must not be
    /// the type the page opens on or the one the examples are built from, because an example about the
    /// dead-letter table teaches nothing about this application's data. Dead letters have a page of their
    /// own.
    /// </remarks>
    internal static int CompareAliases(string left, string right)
    {
        bool leftInfrastructure = string.Equals(left, DeadLetterAlias, StringComparison.OrdinalIgnoreCase);
        bool rightInfrastructure = string.Equals(right, DeadLetterAlias, StringComparison.OrdinalIgnoreCase);

        return leftInfrastructure == rightInfrastructure
            ? string.CompareOrdinal(left, right)
            : (leftInfrastructure ? 1 : -1);
    }

    private IDocumentType? FindDocumentType(ResolvedScope resolved, string alias)
    {
        foreach (IDocumentType documentType in VisibleDocumentTypes(resolved))
        {
            if (string.Equals(documentType.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                return documentType;
            }
        }

        return null;
    }

    /// <summary>
    /// A property worth putting in an example: a duplicated field first, because that is the filter that
    /// can use an index, and otherwise the first simple public property that is not the id.
    /// </summary>
    private static string? PickPropertyName(IDocumentType documentType, DocumentTableInfo table)
    {
        if (table.DuplicatedColumns.Count > 0)
        {
            return table.DuplicatedColumns[0].MemberPath;
        }

        string? idName = documentType.IdMember?.Name;

        foreach (PropertyInfo property in documentType.DocumentType.GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0
                || string.Equals(property.Name, idName, StringComparison.Ordinal)
                || !IsSimple(property.PropertyType))
            {
                continue;
            }

            return property.Name;
        }

        return null;
    }

    private static bool IsSimple(Type type)
    {
        Type underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying == typeof(string)
            || underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly)
            || underlying == typeof(Guid);
    }

    /// <summary>
    /// The <see cref="PostgresException" /> behind whatever Marten threw, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// Marten wraps command failures in its own exception type, so the SQLSTATE a person needs is one or
    /// two levels down. The chain is walked rather than the top type inspected, which also covers an
    /// <see cref="AggregateException" /> from a batched call.
    /// </remarks>
    internal static PostgresException? FindPostgresException(Exception? exception)
    {
        for (Exception? candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is PostgresException postgres)
            {
                return postgres;
            }

            if (candidate is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    if (FindPostgresException(inner) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    private static SqlError ToSqlError(PostgresException exception) =>
        new(exception.SqlState, exception.MessageText, exception.Position, exception.Detail, exception.Hint);

    private static string WritePolicyName(MartenStudioOptions value) =>
        value.WriteAuthorizationPolicy ?? value.StoreAuthorizationPolicy ?? "(none)";

    /// <summary>
    /// The statement as the audit ring's target: enough to recognise it, on one line. The <em>whole</em>
    /// statement goes to the application's own log (event 9204 or 9205), which is the record that survives
    /// a restart.
    /// </summary>
    internal static string Target(string? statement)
    {
        string collapsed = string.Join(' ', (statement ?? string.Empty).Split(
            ['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return collapsed.Length <= AuditTargetLength ? collapsed : collapsed[..AuditTargetLength] + "…";
    }

    private string UserName()
    {
        // Read from the already-completed task, exactly as StudioActionLog does: every caller is a step of
        // a service method and a circuit's authentication state is settled long before a click.
        Task<AuthenticationState> state = authenticationStateProvider.GetAuthenticationStateAsync();

        if (!state.IsCompletedSuccessfully)
        {
            return Unknown;
        }

        string? name = state.Result.User.Identity?.Name;

        return string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
    }
}
