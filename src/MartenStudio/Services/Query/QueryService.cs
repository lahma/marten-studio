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
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Services.Query;

/// <summary>
/// The Query page's Marten mode - a <c>where</c> clause against one document type - and the plan for it, which it
/// fetches through the shared SQL console's gate.
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
/// composes the statement itself (<see cref="QuerySqlComposer.Compose" />), with its own predicates on
/// both sides of the visitor's, and runs it on <c>IMartenDatabase.CreateConnection(ConnectionUsage.Read)</c> -
/// which is also what makes the SQL tab, the command that ran and the statement <c>EXPLAIN</c> planned all
/// the same text. Rows come back as <c>data::text</c> and are never deserialized, so no property can be
/// dropped on the way to the screen (AGENTS.md hard rule 10 the easy way: nothing serializes).
/// </para>
/// <para>
/// <b>Mode A runs inside the same read-only transaction as the console</b>, through
/// <see cref="ReadOnlySqlSession.InTransactionAsync{T}" />, and reads the rows itself because a document's
/// <c>data</c> column must arrive whole where a console cell is capped for a grid. It did not used to, and
/// that was the defect: a plain command on a read connection, with no <c>SET TRANSACTION READ ONLY</c>, no
/// server-side <c>statement_timeout</c> and no <see cref="MartenStudioOptions.SqlConsoleRole" />, reached
/// by a mode that needs no capability at all. The transaction is not the whole guard - an advisory lock
/// and a <c>setval</c> are both "reads" as far as SQLSTATE 25006 is concerned, which is why
/// <see cref="QuerySqlComposer.DisallowedFunctions" /> applies here whatever capabilities the visitor
/// holds - but a clause that turns out to write now changes nothing, and a clause that hangs is cancelled
/// by Postgres with a 57014 the page can render rather than by breaking the connection.
/// </para>
/// <para>
/// <b>Mode B - the SQL console - is not here.</b> It is the shared <see cref="SqlConsoleService" />, which every
/// studio's console runs through and which holds the gate - the capability, the write policy asked about the
/// database as a whole (<see cref="SqlScope" />), the statement guard and the read-only transaction (D13). The
/// Query page's SQL tab is the shared <c>SqlConsolePanel</c> over it.
/// </para>
/// <para>
/// The plan for a Mode A clause is Mode B work: fetching it means sending a statement to Postgres, so it
/// goes through the very same gate (<see cref="SqlConsoleService.ExecuteAsync" />) and is refused - as a value, not an exception - when the console
/// capability is off. That is why a studio without <c>RunSql</c> shows the composed SQL and says there is
/// no plan, rather than quietly running an <c>EXPLAIN</c> nobody granted.
/// </para>
/// </remarks>
internal sealed class QueryService : IQueryService
{
    /// <summary>What the audit ring calls a Mode A plan fetch.</summary>
    internal const string ExplainAction = "ExplainQuery";

    /// <summary>What the audit ring calls a Marten where clause.</summary>
    /// <remarks>
    /// <b>Every run, not only the refusals.</b> A clause is arbitrary SQL against one collection, run with
    /// no capability; "who read what, with which filter, in which tenant" is exactly the question an
    /// incident asks, and a read is cheap to record. Refusals are written the same way with
    /// <c>succeeded: false</c> (hard rule 5).
    /// </remarks>
    internal const string MartenQueryAction = "MartenQuery";

    /// <summary>How much of a statement goes into the audit ring's target column (<see cref="SqlConsoleService.AuditTargetLength" />).</summary>
    internal const int AuditTargetLength = SqlConsoleService.AuditTargetLength;

    private const string Unknown = "(unknown)";

    private readonly StudioScopeResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly ColumnCatalog columnCatalog;
    private readonly SqlConsoleService console;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly ILogger<QueryService> logger;
    private readonly AuthenticationStateProvider authenticationStateProvider;

    public QueryService(
        StudioScopeResolver resolver,
        StudioAuthorization authorization,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        ColumnCatalog columnCatalog,
        SqlConsoleService console,
        IOptions<MartenStudioOptions> options,
        ILogger<QueryService> logger,
        AuthenticationStateProvider authenticationStateProvider)
    {
        this.resolver = resolver;
        this.authorization = authorization;
        this.capabilities = capabilities;
        this.audit = audit;
        this.columnCatalog = columnCatalog;
        this.console = console;
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

        MartenStudioOptions value = options.Value;
        string user = UserName();
        string clause = request.WhereClause ?? string.Empty;
        string target = Target(clause);

        // The scope first, and audited when it is refused. Resolving before any audit call is how a refusal
        // used to leave no trace at all: a visitor could probe stores, databases and tenants through this
        // page and the only record of it was the exception the browser never showed. 9203 is the same entry
        // the console writes for the same refusal, because it is the same refusal.
        ResolvedScope resolved;

        try
        {
            resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, ReadPolicyName(value), MartenQueryAction, target);
            logger.SqlRejected(StudioProduct.MartenStudio, user, scope.StoreKey, scope.DatabaseId, "not authorized for this scope", clause);
            throw;
        }

        if (FindDocumentType(resolved, request.Alias) is not { } documentType)
        {
            // An alias that is not there - or one the host's IsDocumentTypeVisible hides - is a failed run,
            // not a silent one: typing an alias into the URL is exactly how somebody looks for a collection
            // they were not offered.
            string missing =
                $"Marten store '{scope.StoreKey}' has no visible document type with alias '{request.Alias}'.";

            audit.Record(MartenQueryAction, target, succeeded: false, missing, null, scope);
            logger.SqlRejected(StudioProduct.MartenStudio, user, scope.StoreKey, scope.DatabaseId, missing, clause);

            throw new KeyNotFoundException(missing);
        }

        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType);
        int limit = Math.Clamp(request.PageSize ?? value.DefaultPageSize, 1, value.MaxPageSize);
        string? tenantId = scope.TenantId is { Length: > 0 } scoped ? scoped : null;

        // A where clause needs no capability, so it has to be a clause. Without RunSql it may read only the
        // table that was picked; with RunSql the nested-read rules alone are lifted, because everything
        // *they* refuse the same person could type into the console. Every other rule - one statement, not
        // a statement of its own, balanced brackets, a placeable tail, and the function denylist - holds
        // either way. The read-only transaction below is the second boundary, not a replacement for this
        // one: it refuses writes, and an advisory lock or a `setval` is not a write.
        bool mayRunSql = await MayRunSqlAsync(scope, cancellationToken).ConfigureAwait(false);
        SqlGuardResult clauseGuard = QuerySqlComposer.CheckClause(request.WhereClause, mayRunSql);

        if (!clauseGuard.Allowed)
        {
            SqlRejection rejected = SqlRejection.FromGuard(clauseGuard);
            ComposedQuery refusedShape = QuerySqlComposer.Compose(
                table, request.WhereClause, limit, tenantId, request.IncludeDeleted);

            audit.Record(MartenQueryAction, target, succeeded: false, rejected.Message, null, scope);
            logger.SqlRejected(StudioProduct.MartenStudio, user, scope.StoreKey, scope.DatabaseId, rejected.Message, clause);

            return new MartenQueryResult(
                documentType.Alias, [], refusedShape.Statement, refusedShape.DescribeParameters(), TimeSpan.Zero,
                limit, refusedShape.LimitApplied, null,
                ExplainResult.Unavailable("The clause was never sent."), ScopeOf(refusedShape, table),
                refusedShape.Predicate, rejected);
        }

        await using NpgsqlConnection connection = MartenStudioDatabase.CreateConnection(resolved.Database, ConnectionUsage.Read);
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
        List<MartenQueryRow> rows;

        // The same read-only transaction the console runs in, with the same three timeouts and the same
        // optional SET LOCAL ROLE - one preamble, in ReadOnlySqlSession, rather than a second copy here.
        // Mode A used to run as a plain command on a read connection, which is how `where
        // pg_advisory_lock(42) is not null` from a RunSql holder took a session-level lock on a pooled
        // connection. The transaction does not refuse that call (an advisory lock is not a write); what it
        // does refuse is everything 25006 covers, and it guarantees a ROLLBACK whatever the clause did.
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = value.QueryTimeout,
            MaxRows = limit,
            Role = value.SqlConsoleRole,
        });

        try
        {
            rows = await session.InTransactionAsync(
                connection,
                async (transaction, token) =>
                {
                    List<MartenQueryRow> read = [];

                    await using NpgsqlCommand command = new(composed.Statement, connection, transaction)
                    {
                        // A little past the server-side statement_timeout, which is what should fire:
                        // statement_timeout produces 57014 with a message and a position that the page
                        // renders, while a client-side timeout only breaks the connection.
                        CommandTimeout = (int) Math.Ceiling(value.QueryTimeout.TotalSeconds) + 5,
                    };

                    composed.Bind(command);

                    // Measured around the execution alone, as the console measures its own.
                    stopwatch.Restart();

                    await using NpgsqlDataReader reader = await command
                        .ExecuteReaderAsync(token).ConfigureAwait(false);

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (read.Count >= composed.Limit)
                        {
                            // The composed statement carries `limit @limit`, so this never fires. It is here
                            // because the cap is a server-side clamp (plan §4.8) and a cap that exists in
                            // exactly one place is a cap one edit away from being gone.
                            break;
                        }

                        read.Add(ReadRow(reader, composed));
                    }

                    return read;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && FindPostgresException(exception) is { } postgres)
        {
            stopwatch.Stop();

            // A malformed clause is a value, not a failure: it is the single most common thing that
            // happens on this page, and the person typing needs the SQLSTATE and the position.
            string reason = QuerySqlErrors.Summarize(ToSqlError(postgres));

            audit.Record(MartenQueryAction, target, succeeded: false, reason, null, scope);
            logger.SqlRejected(StudioProduct.MartenStudio, user, scope.StoreKey, scope.DatabaseId, reason, statement);

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
            StudioProduct.MartenStudio, user, scope.StoreKey, scope.DatabaseId, elapsedMilliseconds, rows.Count, statement);

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

    private static string Outcome(int rows, TimeSpan duration) => SqlConsoleService.Outcome(rows, duration);

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
    /// The scope <c>RunSql</c> is authorized against - the database as a whole (<see cref="SqlConsoleService.SqlScope" />):
    /// the console run, the plan for a Mode A clause, and the nested-read rules <see cref="MayRunSqlAsync" /> lifts are
    /// all asked about this one scope.
    /// </summary>
    internal static StudioScope SqlScope(StudioScope scope) => SqlConsoleService.SqlScope(scope);

    /// <summary>
    /// Whether this visitor may run SQL against this scope's database, which is what lifts the clause
    /// guard's nested-read rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same three questions the console's own gate asks, in the same order, against the same
    /// tenant-less scope (<see cref="SqlScope" />): the process-wide capability (D4), then the store
    /// policy and the write policy with <c>RunSql</c> on the resource - which is exactly what
    /// the shared resolver asks when <see cref="SqlConsoleService.ExecuteAsync" /> resolves it. Asking only the capability would hand subqueries to somebody the
    /// <see cref="MartenStudioOptions.WriteAuthorizationPolicy" /> refuses the console to; asking only the
    /// write policy would hand them to somebody the store policy allows one tenant of the database and not
    /// the database - and a subquery reads beyond the tenant exactly as the console does.
    /// </para>
    /// </remarks>
    private async Task<bool> MayRunSqlAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        if (!capabilities.IsEnabled(StudioCapability.RunSql))
        {
            return false;
        }

        StudioScope sqlScope = SqlScope(scope);

        return await authorization.IsAuthorizedAsync(sqlScope, capability: null, cancellationToken).ConfigureAwait(false)
            && await authorization
                .IsAuthorizedAsync(sqlScope, nameof(StudioCapability.RunSql), cancellationToken)
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
    /// <para>
    /// Internal rather than private for <c>SqlConsoleReachTests</c> alone: the page reaches it only after a
    /// Mode A read has run, which takes a database, and the gate it asks is the one thing about it a test
    /// with none can prove.
    /// </para>
    /// </remarks>
    internal async Task<ExplainResult> TryExplainAsync(
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
                      capabilities.OptionFor(StudioCapability.RunSql) + ".");
        }

        try
        {
            SqlConsoleResult result = await console.ExecuteAsync(
                    scope,
                    QuerySqlComposer.Explain(composed.Statement, json: true),
                    ExplainAction,
                    composed.Bind,
                    cancellationToken)
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
            // The plan is fetched through the console's gate, which asks about the database as a whole
            // (SqlScope): a visitor allowed one tenant and not the database gets the clause's rows and no
            // plan, which is the same answer the console tab would give them.
            return ExplainResult.Unavailable("No plan: fetching one runs EXPLAIN through the SQL console. " + SqlConsoleService.SqlRefusedMessage);
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

    /// <summary>
    /// The policy a Mode A refusal names. A <c>where</c> clause is resolved with no capability, so the
    /// policy that answered is the store policy - naming the write policy in the log would send whoever
    /// reads 9203 to the wrong setting.
    /// </summary>
    private static string ReadPolicyName(MartenStudioOptions value) =>
        value.StoreAuthorizationPolicy ?? "(none)";

    /// <summary>
    /// The statement as the audit ring's target: enough to recognise it, on one line
    /// (<see cref="SqlConsoleService.Target" />). The <em>whole</em> statement goes to the application's own log.
    /// </summary>
    internal static string Target(string? statement) => SqlConsoleService.Target(statement);

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
