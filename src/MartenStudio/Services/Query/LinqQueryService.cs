using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using JasperFx.MultiTenancy;

using Marten.Schema;
using Marten.Services;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using PostgresStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

using Weasel.Core;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Services.Query;

/// <summary>
/// LINQ mode: a typed predicate over one document type, parsed under the studio's allow-list, translated by
/// Marten's own <c>Query&lt;T&gt;()</c> and run in the studio's read-only transaction (F7).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate, in order, and the order is the security property.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <see cref="StudioCapabilityGuard.Require" /> for <see cref="StudioCapability.RunLinqQueries" /> - off by default,
/// in <c>All()</c>, off under <c>ReadOnly</c> - before anything is resolved or parsed.
/// </description></item>
/// <item><description>
/// <see cref="StudioScopeResolver.ResolveAsync" /> with the capability named, so the store policy and then the
/// write policy are asked (D13), <b>with the tenant the scope names</b>. That is the read's own question, and it
/// is the right one for a conjoined collection because the statement Marten generates is narrowed to that tenant:
/// the session is opened with <c>SessionOptions.ForDatabase(tenant, database)</c> (hard rule 15), and Marten writes
/// its own <c>tenant_id = :p</c> with the session's tenant bound - nothing in a predicate can widen it, because
/// <c>AnyTenant()</c>, <c>TenantIsOneOf()</c> and <c>MatchesSql()</c> are extension methods Dynamic LINQ cannot
/// reach (JasperFx's parser has no custom types and so no extension methods) and the guard refuses every method
/// not on its list. With no tenant selected the studio adds <c>AnyTenant()</c> itself and the question was asked
/// with <c>TenantId = null</c> - the database as a whole, which is what is read.
/// </description></item>
/// <item><description>
/// <b>Reach, not scope, for a collection that is not divided by tenant.</b> A single-tenanted collection read from
/// a tenant scope is not narrowed by anything - Marten writes no tenant filter, and a table such as Marten's own
/// <c>mt_doc_deadletterevent</c> holds every tenant's rows with no <c>tenant_id</c> column at all - so it is asked
/// again as <c>(store, database, null)</c> through <see cref="DatabaseReachAuthorization.RequireWholeDatabaseAsync" />,
/// unless the database is exclusively that tenant's (database-per-tenant), D27's rule for a read nothing narrows.
/// </description></item>
/// <item><description>
/// The plan is asked separately, and always with <c>TenantId = null</c>: <c>EXPLAIN</c> of a tenant-narrowed
/// statement still reports table-wide statistics - the row estimates count every tenant's rows - and on a table
/// partitioned by tenant it names the partitions, which are the tenant list (D27). It needs
/// <c>RunLinqQueries</c>, not <c>RunSql</c>: the statement explained is Marten's, not text somebody typed.
/// </description></item>
/// </list>
/// <para>
/// <b>Then three walls, before Marten sees anything.</b> <see cref="LinqTextScreen" /> over the text,
/// JasperFx's <c>DynamicQuery</c> with its own allow-list and the studio's guard as the first rule of its policy
/// (<see cref="DynamicLinqAdapter" />), and <see cref="LinqExpressionGuard" /> again over the composed expression.
/// Marten's translation is built without <c>ToCommand</c>, which creates tables (<see cref="MartenLinqCommand" />),
/// and its statement passes <see cref="ReadOnlySqlGuard" /> before it is sent.
/// </para>
/// <para>
/// <b>Run where the studio controls it.</b> Marten's statement and a copy of its parameters go to
/// <see cref="ReadOnlySqlSession" />: <c>SET TRANSACTION READ ONLY</c>, <c>statement_timeout</c> =
/// <see cref="MartenStudioOptions.QueryTimeout" />, the lock and idle timeouts, the optional
/// <see cref="MartenStudioOptions.SqlConsoleRole" />, and a rollback whatever happened. Rows are read with
/// sequential access, the JSON as text and never deserialized, each document capped at
/// <see cref="MartenStudioOptions.MaxInlineDocumentBytes" /> characters and the page at
/// <see cref="LinqQueryLimits.PageTextBudget" />.
/// </para>
/// <para>
/// <b>Paging is <c>OFFSET</c>, bounded, and that is a choice.</b> Keyset paging needs the last row's value of every
/// ordering key, compared in SQL with the ordering's own null placement - and Marten's <c>ORDER BY</c> is built from
/// the mapping (<c>d.data -&gt;&gt; 'Name'</c>, <c>CAST(… as numeric)</c>, a duplicated column), so the studio would
/// either have to read keys back out of Marten's statement text or evaluate the ordering members on deserialized
/// documents in this process. Neither is honest. So pages go through Marten's own <c>Skip</c> / <c>Take</c>, with
/// the identity column (and the tenant column across tenants) added as Marten's diagnostics read adds it, so a
/// page is deterministic; and a page may not start past <see cref="LinqQueryLimits.MaxOffset" />. There is no
/// count at all - not the exact count <c>IDocumentStoreDiagnostics.QueryDocumentsAsync</c> runs on every page (D8):
/// one more row than the page is read to know whether another page exists.
/// </para>
/// <para>
/// <b>Audited by signature.</b> Every refusal is recorded, at every stage: the capability, the policies, a scope that
/// names nothing this process has or a store that will not build, the type (a visibility delegate that throws
/// included), the availability, the page, the screen, the parse, the guard, Marten's translation and Postgres. A
/// success is recorded once per circuit for each store, database, tenant, type, predicate, ordering and deleted
/// filter - the first page, or the first page a pasted link opens on - and a page turn or a re-run of the same query
/// is not a new opening, as the database browser records its first row read (D27).
/// </para>
/// <para>
/// <b>Capped where it is written down.</b> The ring's target and events 9260 and 9261 carry the alias, the predicate
/// and the ordering cut by <see cref="QueryService.Target" /> - one line, 200 characters - and never whole: a predicate
/// is up to a thousand characters of the visitor's own text, and the log is Information. A refusal at the capability
/// stage carries none of it, because nothing authorized it and only a scripted circuit reaches it.
/// </para>
/// </remarks>
internal sealed class LinqQueryService : ILinqQueryService
{
    /// <summary>What the audit ring calls a LINQ query.</summary>
    internal const string LinqQueryAction = "LinqQuery";

    /// <summary>What the audit ring calls a plan the policies refused.</summary>
    internal const string LinqExplainAction = "LinqExplain";

    /// <summary>What a visitor is told when a plan is refused them.</summary>
    internal const string PlanRefusedMessage =
        "No plan: a plan describes the table as a whole - its estimates count every tenant's rows, and a table " +
        "partitioned by tenant lists its partitions - so it needs RunLinqQueries for this database with no tenant, " +
        "which your account does not have.";

    /// <summary>What a visitor is told when the database-wide question for a collection with no tenant column is refused.</summary>
    internal const string WholeDatabaseRefusedMessage =
        "Your account may not run LINQ queries against this database as a whole, and this collection is not " +
        "divided by tenant, so a query on it is not limited to the tenant you selected.";

    /// <summary>What the ring and the log say in place of a predicate and an ordering that were not recorded.</summary>
    internal const string TextNotRecorded = "(text not recorded)";

    private const char Separator = '\u001f';

    private const int MaxPlanCharacters = 256 * 1024;

    private readonly StudioScopeResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly ColumnCatalog columnCatalog;
    private readonly IndexCatalog indexCatalog;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly ILogger<LinqQueryService> logger;
    private readonly IServiceProvider services;
    private readonly DatabaseReachAuthorization reach;
    private readonly HypotheticalIndexReader hypothetical;

    /// <summary>The signatures this circuit has already recorded a success for, per store, database, tenant and type.</summary>
    private readonly TableRowAuditLedger ledger = new();

    public LinqQueryService(
        StudioScopeResolver resolver,
        StudioAuthorization authorization,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        ColumnCatalog columnCatalog,
        IndexCatalog indexCatalog,
        IOptions<MartenStudioOptions> options,
        ILogger<LinqQueryService> logger,
        IServiceProvider services,
        HypotheticalIndexReader hypothetical)
    {
        this.resolver = resolver;
        this.authorization = authorization;
        this.capabilities = capabilities;
        this.audit = audit;
        this.columnCatalog = columnCatalog;
        this.indexCatalog = indexCatalog;
        this.options = options;
        this.logger = logger;
        this.services = services;
        this.hypothetical = hypothetical;
        reach = new DatabaseReachAuthorization(authorization, logger);
    }

    // ---------------------------------------------------------------------------------------------------
    // Describe
    // ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<LinqModeStatus> DescribeAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!capabilities.IsEnabled(StudioCapability.RunLinqQueries))
        {
            return capabilities.ReadOnly
                ? new LinqModeStatus(LinqAvailability.ReadOnly,
                    "MartenStudioOptions.ReadOnly is true, which turns every capability off, LINQ mode included.", [])
                : new LinqModeStatus(LinqAvailability.CapabilityOff,
                    "Set " + StudioCapabilityGuard.OptionName(StudioCapability.RunLinqQueries) + " to true to run LINQ queries here.", []);
        }

        (LinqAvailability availability, string? message) = DynamicLinqAdapter.Availability(MartenRuntime.Current);
        if (availability != LinqAvailability.Available)
        {
            return new LinqModeStatus(availability, message, []);
        }

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<Type> hidden = HiddenDocumentTypes(resolved);

        List<LinqDocumentTypeInfo> types = [];

        foreach (LinqTarget target in VisibleTypes(resolved))
        {
            DocumentTableInfo table = DocumentTableInfo.FromDocumentType(target.Root);
            LinqDocumentGraph graph = LinqDocumentGraph.For(target.ClrType, hidden);

            types.Add(new LinqDocumentTypeInfo(
                target.Alias,
                target.ClrType.Name,
                target.ClrType.FullName ?? target.ClrType.Name,
                target.RootAlias,
                table.TenancyStyle == TenancyStyle.Conjoined,
                table.SoftDeleteEnabled,
                graph.HintMembers));
        }

        types.Sort(static (left, right) => QueryService.CompareAliases(left.Alias, right.Alias));

        // The page's hints ask what the run and the plan will be asked: by the registered key and the database's own
        // identity, never the visitor's spelling of either (D29, H1b) - or a policy keyed on those names would offer a Run
        // button, or a plan, that the run then refuses.
        StudioScope named = StudioSourceResolver.Named(scope, resolved.Source, resolved.Target);

        bool mayRun = await authorization.IsAuthorizedAsync(named, StudioCapability.RunLinqQueries, cancellationToken).ConfigureAwait(false);

        return new LinqModeStatus(LinqAvailability.Available, null, types, mayRun, mayRun && await MayExplainAsync(named, cancellationToken).ConfigureAwait(false));
    }

    // ---------------------------------------------------------------------------------------------------
    // Run
    // ---------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<LinqQueryResult> RunAsync(
        StudioScope scope,
        LinqQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        return await RunCoreAsync(await NewRunAsync(scope, request).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the audit ring calls a LINQ what-if (X2).</summary>
    internal const string LinqWhatIfAction = "LinqWhatIf";

    /// <summary>What a what-if says of an index this query's advice does not offer.</summary>
    internal const string NotOfferedMessage =
        "Not compared: the studio imagines only the index Marten would create on a member this query filters or orders on.";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The run's own gate, then the plan's.</b> The request goes through every step <see cref="RunAsync" /> takes - the
    /// capability, the policies with the scope's tenant, reach, the screen, the parse, the guard, Marten's translation and the
    /// statement guard - recorded under <see cref="LinqWhatIfAction" />, and then through the plan's question: <c>RunLinqQueries</c>
    /// for the database as a whole, because a plan's estimates count every tenant's rows. Nothing is read: the statement is
    /// planned twice, never run.
    /// </para>
    /// <para>
    /// <b>Marten's index, found again.</b> The index must be one <see cref="LinqIndexAdvisor.Hypothetical" /> builds from this
    /// composed query - Marten's own DDL (<see cref="MartenIndexDdl" />) for a member it filters or orders on - matched by key
    /// and definition, so a page can only ask for what the advice offered.
    /// </para>
    /// </remarks>
    public async Task<WhatIfResult> WhatIfAsync(
        StudioScope scope,
        LinqQueryRequest request,
        HypotheticalIndex index,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(index);

        Run run = await NewRunAsync(scope, request with { Offset = 0 }).ConfigureAwait(false) with { Action = LinqWhatIfAction, WhatIf = index };

        try
        {
            LinqQueryResult result = await RunCoreAsync(run, cancellationToken).ConfigureAwait(false);
            return result.WhatIf ?? WhatIfResult.NotCompared(index, result.Refusal?.Message ?? "Not compared.");
        }
        catch (StudioCapabilityDeniedException denied)
        {
            return WhatIfResult.Refused(index, capabilities.ReadOnly ? DatabaseRefusal.ReadOnly : DatabaseRefusal.CapabilityOff, denied.Message);
        }
        catch (StudioNotAuthorizedException)
        {
            return WhatIfResult.Refused(index, DatabaseRefusal.WritePolicy, WholeDatabaseRefusedMessage);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return WhatIfResult.Refused(index, DatabaseRefusal.Unavailable, exception.Message);
        }
    }

    private async Task<Run> NewRunAsync(StudioScope scope, LinqQueryRequest request)
    {
        MartenStudioOptions value = options.Value;

        return new Run(
            scope,
            request,
            await authorization.UserNameAsync().ConfigureAwait(false),
            request.Where ?? string.Empty,
            request.OrderBy ?? string.Empty,
            Math.Clamp(request.PageSize ?? value.DefaultPageSize, 1, value.MaxPageSize));
    }

    private async Task<LinqQueryResult> RunCoreAsync(Run run, CancellationToken cancellationToken)
    {
        StudioScope scope = run.Scope;
        LinqQueryRequest request = run.Request;
        MartenStudioOptions value = options.Value;

        // 1. The capability: refused before anything is resolved, parsed or asked of a policy - and with none of the
        //    text recorded. The page offers no Run button while the capability is off, so a run that gets here was
        //    sent by a scripted circuit, and text nothing has authorized is not written to the ring or the log.
        try
        {
            capabilities.Require(StudioCapability.RunLinqQueries);
        }
        catch (StudioCapabilityDeniedException denied)
        {
            audit.RecordCapabilityDenied(denied, run.Action, run.CapabilityTarget);
            logger.LinqQueryRefused(run.LoggedUser, run.LoggedAlias, run.LoggedStoreKey, run.LoggedDatabaseId, "capability", denied.Message, TextNotRecorded, TextNotRecorded);
            throw;
        }

        // 2. The scope, with the capability named so the write policy is asked - with the scope's own tenant.
        ResolvedScope resolved;

        try
        {
            resolved = await resolver.ResolveAsync(scope, nameof(StudioCapability.RunLinqQueries), cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException denied)
        {
            audit.RecordScopeDenied(denied.Scope, WritePolicyName(value), run.Action, run.Target, StudioCapability.RunLinqQueries);
            logger.LinqQueryRefused(run.LoggedUser, run.LoggedAlias, run.LoggedStoreKey, run.LoggedDatabaseId, "authorization", "not authorized for this scope", run.LoggedWhere, run.LoggedOrderBy);
            throw;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            // The policies passed a scope that names no store, database or tenant this process has, or a store that
            // will not build. A refusal like any other, recorded like any other, rather than an exception that left
            // the ring without a trace of what was tried; the page shows the resolver's own sentence, as it did.
            return Refuse(run, new LinqRefusal(LinqStage.Unavailable, LinqClause.None, exception.Message));
        }

        // 3. The type: a visible root or a visible subclass, by alias. A hidden one is the same answer as none - and
        //    so is one the host's IsDocumentTypeVisible threw for, which fails closed rather than escaping unaudited.
        //    The hidden set is taken here too, for the same reason: it asks the same delegate of every type.
        LinqTarget? found;
        IReadOnlySet<Type> hidden;

        try
        {
            found = FindType(resolved, request.Alias);
            hidden = HiddenDocumentTypes(resolved);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            logger.LinqQueryUnexpected(exception, run.LoggedAlias, run.LoggedStoreKey);
            return Refuse(run, new LinqRefusal(LinqStage.Unavailable, LinqClause.None, NoVisibleType(run)));
        }

        if (found is not { } target)
        {
            return Refuse(run, new LinqRefusal(LinqStage.Unavailable, LinqClause.None, NoVisibleType(run)));
        }

        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(target.Root);
        bool conjoined = table.TenancyStyle == TenancyStyle.Conjoined;
        string? tenant = resolved.TenantId is { Length: > 0 } selected ? selected : null;
        bool crossTenant = conjoined && tenant is null;

        // 4. Reach: a collection nothing narrows by tenant, read from a tenant scope, reaches the whole database.
        //    An authorization question, so it is asked before anything about this process's Marten is.
        if (!conjoined && tenant is not null)
        {
            run = run with { AuditScope = QueryService.SqlScope(scope) };

            try
            {
                await reach.RequireWholeDatabaseAsync(resolved, resolved.Database, StudioCapability.RunLinqQueries, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (StudioNotAuthorizedException denied)
            {
                audit.RecordScopeDenied(denied.Scope, WritePolicyName(value), run.Action, run.Target, StudioCapability.RunLinqQueries);
                logger.LinqQueryRefused(run.LoggedUser, run.LoggedAlias, run.LoggedStoreKey, run.LoggedDatabaseId, "authorization",
                    "not authorized for this database as a whole", run.LoggedWhere, run.LoggedOrderBy);
                throw;
            }
        }

        // 5. Whether this process can do it at all: Marten 9.48+, a DynamicQuery and a Marten LINQ builder the studio
        //    recognises, and run-time code generation.
        (LinqAvailability availability, string? unavailable) = DynamicLinqAdapter.Availability(MartenRuntime.Current);

        if (availability != LinqAvailability.Available || DynamicLinqAdapter.Current is not { } adapter)
        {
            return Refuse(run, new LinqRefusal(LinqStage.Unavailable, LinqClause.None, unavailable ?? "LINQ mode is not available here."));
        }

        // 6. The page.
        if (request.Offset < 0 || request.Offset > LinqQueryLimits.MaxOffset)
        {
            return Refuse(run, new LinqRefusal(LinqStage.Screen, LinqClause.None,
                "LINQ mode pages with OFFSET and stops at row " + LinqQueryLimits.MaxOffset.ToString("N0", CultureInfo.InvariantCulture) +
                ": narrow the predicate, or order it so that the documents you want come first."));
        }

        // 7. The screen, over both boxes, against the document's own graph.
        LinqDocumentGraph graph = LinqDocumentGraph.For(target.ClrType, hidden);

        if ((LinqTextScreen.Check(LinqClause.Where, run.Where, graph)
             ?? LinqTextScreen.Check(LinqClause.OrderBy, run.OrderBy, graph)) is { } screened)
        {
            return Refuse(run, screened);
        }

        // 8. The parse and the guard, then Marten's statement - built, never executed by Marten.
        var guard = new LinqExpressionGuard(
            graph,
            path => DuplicatedEnumAsName(table, path),
            target.Root.EnumStorage == EnumStorage.AsString);

        ComposedLinqQuery composed;

        try
        {
            composed = LinqQueryComposer.Compose(new LinqComposition(
                resolved.Store,
                tenant is null ? SessionOptions.ForDatabase(resolved.Database) : SessionOptions.ForDatabase(tenant, resolved.Database),
                target.ClrType,
                adapter,
                guard,
                run.Where,
                run.OrderBy,
                table.SoftDeleteEnabled ? request.Deleted : DeletedFilter.Exclude,
                crossTenant,
                request.Offset,
                run.PageSize,
                path => table.FindDuplicated(path) is { DbType: NpgsqlDbType.TimestampTz }));
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            logger.LinqQueryUnexpected(exception, run.LoggedAlias, run.LoggedStoreKey);

            return Refuse(run, new LinqRefusal(LinqStage.Translate, LinqClause.None,
                "The query could not be built (" + exception.GetType().Name + "). The application log has the details."));
        }

        if (composed.Unexpected is { } unexpected)
        {
            logger.LinqQueryUnexpected(unexpected, run.LoggedAlias, run.LoggedStoreKey);
        }

        if (composed.Refusal is not null || composed.Command is null)
        {
            return Refuse(run, composed.Refusal ?? new LinqRefusal(LinqStage.Translate, LinqClause.None, "Marten built no statement."));
        }

        string sql = composed.Command.CommandText;
        IReadOnlyList<LinqParameterInfo> parameters =
            [.. LinqQuerySql.Describe(composed.Command).Select(static x => new LinqParameterInfo(x.Name, x.Type))];

        // 9. Marten's statement, held to the console's own statement guard: one statement, a read, no function
        //    that reaches outside the transaction. It always passes; a Marten that changed so it did not is refused.
        SqlGuardResult checkedStatement = ReadOnlySqlGuard.Check(sql);
        if (!checkedStatement.Allowed)
        {
            return Refuse(run, new LinqRefusal(LinqStage.Guard, LinqClause.None,
                "The statement Marten built did not pass the studio's read-only statement guard: " + checkedStatement.Message), sql, parameters);
        }

        var scopeFacts = new MartenQueryScope(
            conjoined && !crossTenant ? tenant : null,
            crossTenant,
            table.SoftDeleteEnabled ? request.Deleted : DeletedFilter.Exclude,
            table.SoftDeleteEnabled);

        // X2: a what-if plans the statement, twice, and reads nothing.
        if (run.WhatIf is { } index)
        {
            return await WhatIfCoreAsync(run, resolved, target, table, composed, sql, parameters, value, index, cancellationToken).ConfigureAwait(false);
        }

        // 10. The read, in the studio's read-only transaction.
        return await ExecuteAsync(run, resolved, target, table, composed, sql, parameters, scopeFacts, value, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// X2: the plan's own question, then Marten's statement planned before and after the index Marten would create, which hypopg
    /// imagines and resets before the connection goes back (<see cref="HypotheticalIndexReader" />).
    /// </summary>
    private async Task<LinqQueryResult> WhatIfCoreAsync(
        Run run,
        ResolvedScope resolved,
        LinqTarget target,
        DocumentTableInfo table,
        ComposedLinqQuery composed,
        string sql,
        IReadOnlyList<LinqParameterInfo> parameters,
        MartenStudioOptions value,
        HypotheticalIndex index,
        CancellationToken cancellationToken)
    {
        StudioScope planScope = QueryService.SqlScope(run.Scope);

        LinqQueryResult Done(WhatIfResult whatIf) =>
            new(run.Request.Alias, [], false, 0, run.PageSize, sql, parameters, TimeSpan.Zero) { WhatIf = whatIf };

        // The plan's question: a plan describes the table as a whole.
        if (!await MayExplainAsync(run.Scope, cancellationToken).ConfigureAwait(false))
        {
            audit.RecordScopeDenied(planScope, WritePolicyName(value), run.Action, run.Target, StudioCapability.RunLinqQueries);
            return Done(WhatIfResult.Refused(index, DatabaseRefusal.WritePolicy, PlanRefusedMessage));
        }

        JsonNamingPolicy? naming = target.Root.Casing switch
        {
            Casing.CamelCase => JsonNamingPolicy.CamelCase,
            Casing.SnakeCase => JsonNamingPolicy.SnakeCaseLower,
            _ => null,
        };

        IReadOnlyList<HypotheticalIndex> offered = composed.Composed is null || composed.Source is null
            ? []
            : LinqIndexAdvisor.Hypothetical(composed.Composed, composed.Source, target.Root.IdMember, table, target.Root, name => naming?.ConvertName(name) ?? name);

        if (!offered.Any(x => x.Same(index)))
        {
            audit.Record(run.Action, run.Target, succeeded: false, NotOfferedMessage, StudioCapability.RunLinqQueries, planScope);
            return Done(WhatIfResult.NotCompared(index, NotOfferedMessage));
        }

        NpgsqlCommand marten = composed.Command!;
        int commandTimeout = (int) Math.Ceiling(value.QueryTimeout.TotalSeconds) + 5;

        HypotheticalIndexPlanner planner = new(async (connection, transaction, token) =>
        {
            // EXPLAIN, never ANALYZE: the statement is planned, not run - and a hypothetical index serves no ANALYZE anyway.
            await using NpgsqlCommand command = LinqQuerySql.Bind(LinqQuerySql.Explain(sql), marten, connection, transaction, commandTimeout);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await reader.ReadAsync(token).ConfigureAwait(false) || await reader.IsDBNullAsync(0, token).ConfigureAwait(false))
            {
                return null;
            }

            string text = reader.GetString(0);
            return text.Length <= MaxPlanCharacters ? text : null;
        });

        WhatIfResult result;

        try
        {
            HypotheticalIndexRead read = await hypothetical.ReadAsync(MartenStudioDatabase.Of(resolved.Database), index, planner, cancellationToken)
                .ConfigureAwait(false);
            result = WhatIfResult.From(index, read);
        }
        catch (OperationCanceledException)
        {
            audit.Record(run.Action, run.Target, succeeded: false, "cancelled", StudioCapability.RunLinqQueries, planScope);
            throw;
        }
        catch (Exception exception) when (QueryService.FindPostgresException(exception) is { } postgres)
        {
            result = WhatIfResult.NotCompared(index, WhatIfResult.Failed(postgres.SqlState), HypotheticalIndexState.Available);
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException or InvalidOperationException)
        {
            result = WhatIfResult.NotCompared(index, "Not compared: the what-if failed (" + exception.GetType().Name + ").");
        }

        audit.Record(run.Action, run.Target, succeeded: result.Before is not null, result.Outcome(), StudioCapability.RunLinqQueries, planScope);
        return Done(result);
    }

    private async Task<LinqQueryResult> ExecuteAsync(
        Run run,
        ResolvedScope resolved,
        LinqTarget target,
        DocumentTableInfo table,
        ComposedLinqQuery composed,
        string sql,
        IReadOnlyList<LinqParameterInfo> parameters,
        MartenQueryScope scopeFacts,
        MartenStudioOptions value,
        CancellationToken cancellationToken)
    {
        NpgsqlCommand marten = composed.Command!;
        int commandTimeout = (int) Math.Ceiling(value.QueryTimeout.TotalSeconds) + 5;
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = value.QueryTimeout,
            MaxRows = run.PageSize + 1,
            Role = value.SqlConsoleRole,
        });

        await using NpgsqlConnection connection = MartenStudioDatabase.CreateConnection(resolved.Database, ConnectionUsage.Read);

        var stopwatch = new Stopwatch();
        LinqPage? page = null;
        LinqQueryError? error = null;

        try
        {
            await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            page = await session.InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        await using NpgsqlCommand command = LinqQuerySql.Bind(sql, marten, connection, transaction, commandTimeout);

                        stopwatch.Restart();

                        await using NpgsqlDataReader reader = await command
                            .ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false);

                        LinqPage read = await ReadPageAsync(reader, run.PageSize, value.MaxInlineDocumentBytes, token).ConfigureAwait(false);

                        stopwatch.Stop();
                        return read;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            audit.Record(LinqQueryAction, run.Target, succeeded: false, "cancelled", StudioCapability.RunLinqQueries, run.AuditScope);
            throw;
        }
        catch (Exception exception) when (QueryService.FindPostgresException(exception) is { } postgres)
        {
            stopwatch.Stop();
            error = await MaskAsync(run.Scope, resolved, postgres.SqlState, postgres.MessageText, postgres.Detail, marten, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException)
        {
            stopwatch.Stop();
            error = await MaskAsync(run.Scope, resolved, "08006", exception.Message, null, marten, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Not Postgres and not the connection: a statement Marten shaped in a way the reader does not expect.
            // Recorded like every other failure, and the page is told no more than the exception's type.
            logger.LinqQueryUnexpected(exception, run.LoggedAlias, run.LoggedStoreKey);

            return Refuse(run, new LinqRefusal(LinqStage.Database, LinqClause.None,
                "The result could not be read (" + exception.GetType().Name + "). The application log has the details."), sql, parameters);
        }

        if (error is not null && error.SqlState == "08006")
        {
            // Nothing ran: no plan, no advice - the connection is not there to fetch them on.
            AuditFailure(run, LinqStage.Database, "Postgres " + error.SqlState);
            logger.LinqQueryFailed(run.LoggedUser, run.LoggedAlias, run.LoggedStoreKey, run.LoggedDatabaseId, error.SqlState);

            return new LinqQueryResult(run.Request.Alias, [], false, run.Request.Offset, run.PageSize, sql, parameters, TimeSpan.Zero,
                Error: error, Scope: scopeFacts);
        }

        // The plan and the advice belong to the query rather than to a page, so they are fetched with the first one.
        ExplainResult? plan = null;
        LinqIndexAdvice? advice = null;

        if (run.Request.Offset == 0)
        {
            plan = await PlanAsync(run, resolved, session, connection, marten, sql, commandTimeout, cancellationToken).ConfigureAwait(false);
            advice = await AdviseAsync(target, table, connection, composed, plan?.PlanJson, cancellationToken).ConfigureAwait(false);
        }

        if (error is not null)
        {
            AuditFailure(run, LinqStage.Database, "Postgres " + error.SqlState);
            logger.LinqQueryFailed(run.LoggedUser, run.LoggedAlias, run.LoggedStoreKey, run.LoggedDatabaseId, error.SqlState);

            return new LinqQueryResult(run.Request.Alias, [], false, run.Request.Offset, run.PageSize, sql, parameters,
                stopwatch.Elapsed, Error: error, Plan: plan, Advice: advice, Scope: scopeFacts);
        }

        LinqPage done = page!;
        AuditSuccess(run, resolved, done.Rows.Count, stopwatch.Elapsed, plan);

        return new LinqQueryResult(
            run.Request.Alias,
            done.Rows,
            done.HasMore,
            run.Request.Offset,
            run.PageSize,
            sql,
            parameters,
            stopwatch.Elapsed,
            Plan: plan,
            Advice: advice,
            Scope: scopeFacts,
            BudgetSpent: done.BudgetSpent);
    }

    /// <summary>
    /// One page, read with sequential access: the identity and the JSON as text, each document capped, the page's
    /// text budgeted, and one row past the page read only far enough to know it is there.
    /// </summary>
    private static async Task<LinqPage> ReadPageAsync(NpgsqlDataReader reader, int pageSize, int maxInline, CancellationToken cancellationToken)
    {
        Ordinals ordinals = Ordinals.Of(reader);

        if (ordinals.Data < 0)
        {
            throw new InvalidOperationException("Marten's statement selected no data column.");
        }

        int documentCap = Math.Clamp(maxInline, 1, LinqQueryLimits.PageTextBudget);
        long budget = LinqQueryLimits.PageTextBudget;
        bool spent = false;
        List<LinqQueryRow> rows = [];
        bool more = false;
        char[] buffer = new char[8 * 1024];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= pageSize)
            {
                more = true;
                break;
            }

            string id = string.Empty;
            string? json = null;
            string? tenant = null;
            bool? deleted = null;
            string? notInlined = null;

            // Sequential access reads columns in ordinal order, and never twice.
            foreach ((int ordinal, ColumnRole role) in ordinals.InOrder)
            {
                if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                switch (role)
                {
                    case ColumnRole.Id:
                        id = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
                        break;

                    case ColumnRole.Data when budget <= 0:
                        spent = true;
                        notInlined = "Not shown: this page's " + Kibibytes(LinqQueryLimits.PageTextBudget) +
                                     " budget for document text was spent on the rows above it. Open it in Documents.";
                        break;

                    case ColumnRole.Data:
                    {
                        int limit = (int) Math.Min(documentCap, budget);
                        (string? text, bool truncated) = await ReadTextAsync(reader, ordinal, limit, buffer, cancellationToken).ConfigureAwait(false);

                        if (truncated)
                        {
                            spent |= limit < documentCap;
                            notInlined = limit < documentCap
                                ? "Not shown in full: this page's budget for document text ran out inside it. Open it in Documents."
                                : "Not shown: it is larger than MartenStudioOptions.MaxInlineDocumentBytes (" + Kibibytes(documentCap) +
                                  "). Open it in Documents.";
                            budget -= limit;
                        }
                        else
                        {
                            json = text;
                            budget -= text?.Length ?? 0;
                        }

                        break;
                    }

                    case ColumnRole.Tenant:
                        tenant = reader.GetString(ordinal);
                        break;

                    case ColumnRole.Deleted:
                        deleted = reader.GetBoolean(ordinal);
                        break;
                }
            }

            rows.Add(new LinqQueryRow(id, json, tenant, deleted, notInlined));
        }

        return new LinqPage(rows, more, spent);
    }

    /// <summary>Up to <paramref name="limit" /> characters of a text column, and whether there was more.</summary>
    /// <remarks>
    /// Streamed, so a document larger than the cap is never materialised: sequential access reads the column off the
    /// wire as it is consumed and skips whatever is left when the reader moves on.
    /// </remarks>
    private static async Task<(string? Text, bool Truncated)> ReadTextAsync(
        NpgsqlDataReader reader,
        int ordinal,
        int limit,
        char[] buffer,
        CancellationToken cancellationToken)
    {
        TextReader text;

        try
        {
            text = await reader.GetFieldValueAsync<TextReader>(ordinal, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidCastException)
        {
            // A column type whose converter does not stream as text: read it whole, then cap it. The page's budget
            // still bounds how many such reads one page makes.
            string whole = await reader.GetFieldValueAsync<string>(ordinal, cancellationToken).ConfigureAwait(false);
            return whole.Length <= limit ? (whole, false) : (null, true);
        }

        using TextReader stream = text;

        var builder = new StringBuilder();

        while (builder.Length <= limit)
        {
            int wanted = Math.Min(buffer.Length, limit + 1 - builder.Length);
            int read = await stream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return (builder.ToString(), false);
            }

            builder.Append(buffer, 0, read);
        }

        // One character past the limit was read, so there is more; the rest of the column is skipped unread.
        return (null, true);
    }

    private static string Kibibytes(int characters) =>
        (characters / 1024).ToString("N0", CultureInfo.InvariantCulture) + " KiB";

    // ---------------------------------------------------------------------------------------------------
    // The plan and the advice
    // ---------------------------------------------------------------------------------------------------

    private async Task<ExplainResult> PlanAsync(
        Run run,
        ResolvedScope resolved,
        ReadOnlySqlSession session,
        NpgsqlConnection connection,
        NpgsqlCommand marten,
        string sql,
        int commandTimeout,
        CancellationToken cancellationToken)
    {
        if (!run.Request.IncludePlan)
        {
            return ExplainResult.Unavailable("No plan was asked for.");
        }

        // The plan is authorized for the store and database the scope resolved to, by the registered key and the database's
        // own identity - never the visitor's spelling of either (D29, H1) - and audited against that same resource.
        StudioScope named = StudioSourceResolver.Named(run.Scope, resolved.Source, resolved.Target);
        StudioScope explainScope = QueryService.SqlScope(named);

        if (!await MayExplainAsync(named, cancellationToken).ConfigureAwait(false))
        {
            // An explicit request the gate refused: one 9203, the same entry any other refused read writes.
            audit.RecordScopeDenied(explainScope, WritePolicyName(options.Value), LinqExplainAction, run.Target, StudioCapability.RunLinqQueries);
            return ExplainResult.Unavailable(PlanRefusedMessage);
        }

        try
        {
            string? plan = await session.InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        // EXPLAIN, never ANALYZE: the statement is planned, not run.
                        await using NpgsqlCommand command = LinqQuerySql.Bind(LinqQuerySql.Explain(sql), marten, connection, transaction, commandTimeout);
                        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                        if (!await reader.ReadAsync(token).ConfigureAwait(false) || await reader.IsDBNullAsync(0, token).ConfigureAwait(false))
                        {
                            return null;
                        }

                        string text = reader.GetString(0);
                        return text.Length <= MaxPlanCharacters ? text : null;
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return plan is null
                ? ExplainResult.Unavailable("Postgres returned no plan, or one too large to show.")
                : new ExplainResult(null, plan, null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (QueryService.FindPostgresException(exception) is { } postgres)
        {
            LinqQueryError masked = await MaskAsync(run.Scope, resolved, postgres.SqlState, postgres.MessageText, postgres.Detail, marten, cancellationToken)
                .ConfigureAwait(false);

            return new ExplainResult(null, null, new SqlError(masked.SqlState, masked.Message, 0, masked.Detail, null), null);
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException or InvalidOperationException)
        {
            return ExplainResult.Unavailable("The plan could not be fetched: the connection failed.");
        }
    }

    private async Task<LinqIndexAdvice?> AdviseAsync(
        LinqTarget target,
        DocumentTableInfo table,
        NpgsqlConnection connection,
        ComposedLinqQuery composed,
        string? planJson,
        CancellationToken cancellationToken)
    {
        if (composed.Composed is null || composed.Source is null)
        {
            return null;
        }

        try
        {
            IReadOnlyList<PostgresIndex> indexes = await indexCatalog.GetAsync(connection, table.Schema, table.Table, cancellationToken)
                .ConfigureAwait(false);
            DocumentTableInfo physical = table.WithPhysicalColumns(
                await columnCatalog.GetAsync(connection, table.Schema, table.Table, cancellationToken).ConfigureAwait(false));

            JsonNamingPolicy? naming = target.Root.Casing switch
            {
                Casing.CamelCase => JsonNamingPolicy.CamelCase,
                Casing.SnakeCase => JsonNamingPolicy.SnakeCaseLower,
                _ => null,
            };

            return LinqIndexAdvisor.Build(
                composed.Composed,
                composed.Source,
                target.Root.IdMember,
                physical,
                indexes,
                name => naming?.ConvertName(name) ?? name,
                planJson,
                target.Root);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            // Advice is a courtesy on a page whose query already ran; a catalog read that failed costs it, not the rows.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LinqAdviceUnavailable(exception, table.QualifiedName);
            }

            return null;
        }
    }

    /// <summary>
    /// The store policy and the write policy, both asked of the database as a whole (<c>TenantId = null</c>) with
    /// <c>RunLinqQueries</c> - what a plan describes.
    /// </summary>
    private async Task<bool> MayExplainAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        StudioScope explainScope = QueryService.SqlScope(scope);

        return await authorization.IsAuthorizedAsync(explainScope, capability: null, cancellationToken).ConfigureAwait(false)
               && await authorization.IsAuthorizedAsync(explainScope, nameof(StudioCapability.RunLinqQueries), cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------------
    // Types, visibility, enums
    // ---------------------------------------------------------------------------------------------------

    private IEnumerable<LinqTarget> VisibleTypes(ResolvedScope resolved)
    {
        Func<Type, bool>? isVisible = options.Value.IsDocumentTypeVisible;

        foreach (IDocumentType documentType in resolved.Store.Options.AllKnownDocumentTypes())
        {
            // Hidden in the data layer and not only in the picker: a type the host hid must not be reachable by
            // typing its alias into the URL - nor its subclasses, which share its table.
            if (isVisible is not null && !isVisible(documentType.DocumentType))
            {
                continue;
            }

            yield return new LinqTarget(documentType, documentType.DocumentType, documentType.Alias, null);

            foreach (SubClassMapping subclass in documentType.SubClasses)
            {
                if (isVisible is null || isVisible(subclass.DocumentType))
                {
                    yield return new LinqTarget(documentType, subclass.DocumentType, subclass.Alias, documentType.Alias);
                }
            }
        }
    }

    private LinqTarget? FindType(ResolvedScope resolved, string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        foreach (LinqTarget target in VisibleTypes(resolved))
        {
            if (string.Equals(target.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>Every document type the store knows that the host hides, subclasses included.</summary>
    private HashSet<Type> HiddenDocumentTypes(ResolvedScope resolved)
    {
        Func<Type, bool>? isVisible = options.Value.IsDocumentTypeVisible;
        var hidden = new HashSet<Type>();

        if (isVisible is null)
        {
            return hidden;
        }

        foreach (IDocumentType documentType in resolved.Store.Options.AllKnownDocumentTypes())
        {
            bool rootHidden = !isVisible(documentType.DocumentType);

            if (rootHidden)
            {
                hidden.Add(documentType.DocumentType);
            }

            foreach (SubClassMapping subclass in documentType.SubClasses)
            {
                if (rootHidden || !isVisible(subclass.DocumentType))
                {
                    hidden.Add(subclass.DocumentType);
                }
            }
        }

        return hidden;
    }

    /// <summary>
    /// For a member path read from the document: whether a duplicated field holds the enum's name (a text column)
    /// or its number, or <see langword="null" /> when the path is read from the JSON.
    /// </summary>
    internal static bool? DuplicatedEnumAsName(DocumentTableInfo table, IReadOnlyList<string> path)
    {
        if (table.FindDuplicated(path) is not { IsMetadataAlias: false } column)
        {
            return null;
        }

        return column.DbType switch
        {
            NpgsqlDbType.Varchar or NpgsqlDbType.Text => true,
            NpgsqlDbType.Integer or NpgsqlDbType.Bigint or NpgsqlDbType.Smallint => false,
            _ => null,
        };
    }

    // ---------------------------------------------------------------------------------------------------
    // Errors, audit
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Postgres' words about a LINQ statement, with every schema the visitor may not see masked as the database
    /// browser masks them (D27) - and withheld outright when the list they would be checked against cannot be read.
    /// </summary>
    private async Task<LinqQueryError> MaskAsync(
        StudioScope scope,
        ResolvedScope resolved,
        string sqlState,
        string message,
        string? detail,
        NpgsqlCommand marten,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<string>? withheld = await WithheldSchemasAsync(scope, resolved, cancellationToken).ConfigureAwait(false);

        if (withheld is null)
        {
            return new LinqQueryError(sqlState, "Postgres refused the statement (SQLSTATE " + sqlState + ").");
        }

        IReadOnlyList<string?> bound = LinqQuerySql.BoundText(marten);

        return new LinqQueryError(
            sqlState,
            PostgresErrorText.Redact(message, withheld, bound) ?? message,
            detail is null ? null : PostgresErrorText.Redact(detail, withheld, bound));
    }

    private async Task<IReadOnlySet<string>?> WithheldSchemasAsync(StudioScope scope, ResolvedScope resolved, CancellationToken cancellationToken)
    {
        if (services.GetService<DatabaseAccess>() is not { } access)
        {
            return null;
        }

        try
        {
            DatabaseGateRead read = await access.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
            return read.Succeeded ? read.Gate.WithheldSchemas : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Marten Studio could not read the schema gate to mask a LINQ query's error");
            return null;
        }
    }

    private LinqQueryResult Refuse(Run run, LinqRefusal refusal, string sql = "", IReadOnlyList<LinqParameterInfo>? parameters = null)
    {
        AuditFailure(run, refusal.Stage, refusal.Message);

        return LinqQueryResult.Refused(run.Request.Alias, refusal, run.Request.Offset, run.PageSize, sql, parameters);
    }

    private void AuditFailure(Run run, LinqStage stage, string reason)
    {
        audit.Record(run.Action, run.Target, succeeded: false, reason, StudioCapability.RunLinqQueries, run.AuditScope);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LinqQueryRefused(run.LoggedUser, run.LoggedAlias, run.LoggedStoreKey, run.LoggedDatabaseId, stage.ToString(), StudioLogValue.CapText(reason), run.LoggedWhere, run.LoggedOrderBy);
        }
    }

    /// <summary>
    /// The refusal for an alias that names no visible type - and for one the host's visibility delegate threw for, which
    /// must not read differently. The alias and the store key are capped as the log caps them: both are the visitor's text
    /// (H1).
    /// </summary>
    private static string NoVisibleType(Run run) =>
        $"Marten store '{run.LoggedStoreKey}' has no visible document type with alias '{run.LoggedAlias}'.";

    /// <summary>
    /// One ring entry, and event 9260, for the first success of this signature in this circuit - whichever page it
    /// starts on, because an offset in a pasted link is a first contact that never saw a first page.
    /// </summary>
    private void AuditSuccess(Run run, ResolvedScope resolved, int rows, TimeSpan duration, ExplainResult? plan)
    {
        // By the registered key and the database's identity, as the run resolved them: two spellings of one store are one
        // first read, and one audit entry (D29, H1).
        string key = resolved.Registration.Key + Separator + resolved.Database.Id.Identity + Separator + (run.Scope.TenantId ?? string.Empty) +
                     Separator + run.Request.Alias.ToLowerInvariant();
        string signature = run.Where + Separator + run.OrderBy + Separator + run.Request.Deleted;

        if (!ledger.TryRecord(key, signature, out _))
        {
            return;
        }

        long elapsed = (long) duration.TotalMilliseconds;
        string outcome = rows.ToString(CultureInfo.InvariantCulture) + " rows in " + elapsed.ToString(CultureInfo.InvariantCulture) + " ms" +
                         (run.Request.Offset > 0 ? ", starting past the first page" : string.Empty) +
                         (string.Equals(plan?.UnavailableReason, PlanRefusedMessage, StringComparison.Ordinal) ? "; plan withheld" : string.Empty) + ".";

        audit.Record(LinqQueryAction, run.Target, succeeded: true, outcome, StudioCapability.RunLinqQueries, run.AuditScope);
        logger.LinqQueryExecuted(run.LoggedUser, run.LoggedAlias, run.LoggedStoreKey, run.LoggedDatabaseId, run.LoggedTenant,
            elapsed, rows, run.LoggedWhere, run.LoggedOrderBy);
    }

    private static string WritePolicyName(MartenStudioOptions value) =>
        value.WriteAuthorizationPolicy ?? value.StoreAuthorizationPolicy ?? "(none)";

    // ---------------------------------------------------------------------------------------------------
    // Shapes
    // ---------------------------------------------------------------------------------------------------

    /// <summary>One request, with what every step of it needs to say who, what and where.</summary>
    private sealed record Run(StudioScope Scope, LinqQueryRequest Request, string User, string Where, string OrderBy, int PageSize)
    {
        /// <summary>What the audit ring calls this run: a LINQ query, or a what-if over one (X2).</summary>
        public string Action { get; init; } = LinqQueryAction;

        /// <summary>X2: the index to imagine, for a run that compares plans rather than reading rows.</summary>
        public HypotheticalIndex? WhatIf { get; init; }

        /// <summary>
        /// The scope the audit entries are recorded under: the request's own, or - for a collection not divided by
        /// tenant, read from a tenant scope - the database as a whole, which is what such a read reaches. The ring
        /// shows an entry under <c>RunLinqQueries</c> only to a visitor who may make the same read
        /// (<c>StudioActionLog.GetVisibleAsync</c>), so the entry carries the question the read was asked.
        /// </summary>
        public StudioScope AuditScope { get; init; } = Scope;

        /// <summary>The audit ring's target: the alias and the text, on one line, capped.</summary>
        public string Target { get; } = QueryService.Target(
            Request.Alias + ": " + (Where.Length == 0 ? "(all)" : Where) + (OrderBy.Length == 0 ? string.Empty : " order by " + OrderBy));

        /// <summary>
        /// The ring's target for a run refused at the capability stage: the alias alone, capped. Nothing has authorized
        /// the text, and the ring's entry is logged as well (9201), so none of it is recorded at all.
        /// </summary>
        public string CapabilityTarget { get; } = QueryService.Target(Request.Alias + ": " + TextNotRecorded);

        /// <summary>
        /// The alias as events 9260 to 9263 carry it: capped and on one line, as <see cref="Target" /> is, because it is
        /// the visitor's text too - a scripted circuit can send any string as an alias - and escaped (wave 6,
        /// <see cref="StudioLogValue" />).
        /// </summary>
        public string LoggedAlias { get; } = StudioLogValue.Cap(QueryService.Target(Request.Alias));

        /// <summary>
        /// The predicate as events 9260 and 9261 carry it: <see cref="QueryService.Target" />'s cap and one line, exactly
        /// as the ring's target is cut, never the text whole, and escaped. Literals are in it, as a console statement's are
        /// in 9204.
        /// </summary>
        public string LoggedWhere { get; } = StudioLogValue.Cap(QueryService.Target(Where));

        /// <summary>The ordering as events 9260 and 9261 carry it, capped as <see cref="LoggedWhere" /> is.</summary>
        public string LoggedOrderBy { get; } = StudioLogValue.Cap(QueryService.Target(OrderBy));

        /// <summary>
        /// The visitor and the scope as events 9260 to 9263 carry them (wave 6): escaped and bounded, because a refused
        /// scope is what the address asked for, before anything resolved it.
        /// </summary>
        public string LoggedUser { get; } = StudioLogValue.Cap(User);

        /// <summary>The scope's store key, as <see cref="LoggedUser" /> is carried.</summary>
        public string LoggedStoreKey { get; } = StudioLogValue.Cap(Scope.StoreKey);

        /// <summary>The scope's database id, as <see cref="LoggedUser" /> is carried.</summary>
        public string LoggedDatabaseId { get; } = StudioLogValue.Cap(Scope.DatabaseId);

        /// <summary>The scope's tenant, or <c>(all)</c>, as <see cref="LoggedUser" /> is carried.</summary>
        public string LoggedTenant { get; } = StudioLogValue.Cap(Scope.TenantId ?? "(all)");
    }

    /// <summary>A document type LINQ mode may query: the mapping whose table it reads, and the CLR type it closes over.</summary>
    private sealed record LinqTarget(IDocumentType Root, Type ClrType, string Alias, string? RootAlias);

    /// <summary>One page of rows.</summary>
    private sealed record LinqPage(IReadOnlyList<LinqQueryRow> Rows, bool HasMore, bool BudgetSpent);

    private enum ColumnRole
    {
        Id,
        Data,
        Tenant,
        Deleted,
    }

    /// <summary>Where the columns LINQ mode reads are in Marten's select list - by name, since the list varies by mapping.</summary>
    private sealed record Ordinals(int Data, IReadOnlyList<(int Ordinal, ColumnRole Role)> InOrder)
    {
        public static Ordinals Of(NpgsqlDataReader reader)
        {
            List<(int Ordinal, ColumnRole Role)> roles = [];
            int data = -1;

            for (int i = 0; i < reader.FieldCount; i++)
            {
                ColumnRole? role = reader.GetName(i) switch
                {
                    "id" => ColumnRole.Id,
                    "data" => ColumnRole.Data,
                    "tenant_id" => ColumnRole.Tenant,
                    "mt_deleted" => ColumnRole.Deleted,
                    _ => null,
                };

                if (role is not { } found || roles.Any(x => x.Role == found))
                {
                    continue;
                }

                roles.Add((i, found));

                if (found == ColumnRole.Data)
                {
                    data = i;
                }
            }

            return new Ordinals(data, roles);
        }
    }
}
