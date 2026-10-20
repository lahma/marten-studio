using System.Globalization;
using System.Net.Sockets;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Settings;
using MartenStudio.Services.Sources;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Services.Query;

/// <summary>
/// The read-only SQL console: one statement, against the database a scope names, as the visitor's console role -
/// with the gate in front of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate, in this order, and the order is the security property:</b>
/// <see cref="StudioCapabilityGuard.Require" /> for <see cref="StudioCapability.RunSql" />, then
/// <see cref="StudioSourceResolver.ResolveAsync" /> with the capability named so the studio's write policy is the
/// policy asked - against the scope's database as a whole, never its tenant (<see cref="SqlScope" />), because
/// nothing narrows a typed statement to a tenant - then <see cref="ReadOnlySqlGuard" /> for a nicer message, and only
/// then the statement, inside <see cref="ReadOnlySqlSession" />'s read-only transaction, which is what actually makes
/// it safe (D13): <c>SET TRANSACTION READ ONLY</c>, <c>statement_timeout</c> and, when the host set one,
/// <c>SET LOCAL ROLE</c> to <see cref="IStudioSettings.SqlConsoleRole" />. The parser is a nicer error, not the
/// boundary. Every outcome is audited, failures included (hard rule 5), and logged as 9204 or 9205 with the whole
/// statement.
/// </para>
/// <para>
/// <b>Shared by every studio.</b> Nothing here is a store's: the database is the resolved source's own connection
/// (<see cref="IStudioDatabase.CreateConnection" />), and the statement is sent as it was typed. A studio's own
/// screens that need a statement planned through the same gate - MartenStudio's Mode A <c>EXPLAIN</c> - go through
/// <see cref="ExecuteAsync" /> with their own audit action and their own bound values.
/// </para>
/// </remarks>
internal sealed class SqlConsoleService : ISqlConsoleService
{
    /// <summary>What the audit ring calls a SQL console run.</summary>
    internal const string RunSqlAction = "RunSql";

    /// <summary>How much of a statement goes into the audit ring's target column.</summary>
    internal const int AuditTargetLength = 200;

    /// <summary>
    /// What a visitor the policies refuse the console is told - by a plan panel and by the console tab alike.
    /// </summary>
    /// <remarks>
    /// It names the database rather than the source or the tenant, because that is the question that was asked
    /// (<see cref="SqlScope" />): a visitor allowed one tenant is refused because the console is not limited to one,
    /// and "this store" would send them to ask for the wrong thing.
    /// </remarks>
    internal const string SqlRefusedMessage =
        "Your account may not run SQL against this database as a whole, and the SQL console is not limited to one tenant.";

    private const string Unknown = "(unknown)";

    private readonly StudioSourceResolver resolver;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly IStudioSettings settings;
    private readonly ILogger<SqlConsoleService> logger;
    private readonly AuthenticationStateProvider authenticationStateProvider;

    public SqlConsoleService(
        StudioSourceResolver resolver,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        IStudioSettings settings,
        ILogger<SqlConsoleService> logger,
        AuthenticationStateProvider authenticationStateProvider)
    {
        this.resolver = resolver;
        this.capabilities = capabilities;
        this.audit = audit;
        this.settings = settings;
        this.logger = logger;
        this.authenticationStateProvider = authenticationStateProvider;
    }

    /// <inheritdoc />
    public async Task<SqlConsoleResult> RunSqlAsync(
        StudioScope scope,
        SqlConsoleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        return await ExecuteAsync(scope, request.Statement, RunSqlAction, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The scope <c>RunSql</c> is authorized against: <paramref name="scope" /> with no tenant - the database as a
    /// whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A tenant does not narrow the SQL console, so it does not narrow what the console is authorized for.</b> A
    /// statement somebody typed is sent as it is: nothing puts a tenant predicate into it, and
    /// <c>select * from mt_doc_order</c> from a scope on <c>acme</c> returns every tenant's orders. Authorizing
    /// <c>(source, database, acme)</c> for that let a host whose policy grants one tenant hand the whole database to
    /// anyone it granted a tenant - the rule D27 already applies to the database browser: a read nothing filters by
    /// tenant is asked with <c>TenantId = null</c>.
    /// </para>
    /// <para>
    /// There is no "exclusively this tenant's database" exception here, as there is for a daemon operation. A console
    /// statement is bounded by the connection's role and not by the tenants a database lists - it reads every schema
    /// and table that role can, and <c>SqlConsoleRole</c> is the boundary for that (D13) - so the question is about
    /// what the console is, not about which tenants the database happens to hold.
    /// </para>
    /// <para>
    /// Every <c>RunSql</c> question is asked against this one scope: the console run, a plan fetched through it, and
    /// any page's own "may this visitor run SQL here" question, so the console a page shows and the console the
    /// service runs never disagree.
    /// </para>
    /// </remarks>
    internal static StudioScope SqlScope(StudioScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return scope.TenantId is null ? scope : scope with { TenantId = null };
    }

    /// <summary>
    /// The statement as the audit ring's target: enough to recognise it, on one line. The <em>whole</em> statement
    /// goes to the application's own log (event 9204 or 9205), which is the record that survives a restart.
    /// </summary>
    internal static string Target(string? statement)
    {
        string collapsed = string.Join(' ', (statement ?? string.Empty).Split(
            ['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return collapsed.Length <= AuditTargetLength ? collapsed : collapsed[..AuditTargetLength] + "…";
    }

    /// <summary>What the audit ring says a successful read returned: <c>12 rows in 3 ms</c>.</summary>
    internal static string Outcome(int rows, TimeSpan duration) =>
        rows.ToString(CultureInfo.InvariantCulture) + " rows in " +
        ((long) duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";

    /// <summary>
    /// The gate, and then the statement. Everything that reaches Postgres through the console path goes through here.
    /// </summary>
    /// <param name="scope">The scope the statement runs in; its tenant is ignored, never applied (<see cref="SqlScope" />).</param>
    /// <param name="statement">The statement, exactly as it will be sent.</param>
    /// <param name="action">What the audit ring calls it.</param>
    /// <param name="bind">
    /// Binds the statement's parameters, when it has any. The console itself never does - what somebody typed is sent
    /// verbatim - but a plan fetched for a statement a studio composed has to bind the very same values its read
    /// bound, or it would be planning a different statement.
    /// </param>
    /// <param name="cancellationToken">The token.</param>
    /// <exception cref="StudioCapabilityDeniedException"><c>RunSql</c> is off, or the studio is read-only. Nothing is sent.</exception>
    /// <exception cref="StudioNotAuthorizedException">The write policy refused the database as a whole. Nothing is sent.</exception>
    internal async Task<SqlConsoleResult> ExecuteAsync(
        StudioScope scope,
        string statement,
        string action,
        Action<NpgsqlCommand>? bind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

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

        // 2. The scope, with the capability named so the write policy is the one asked. It is a read, but it is the
        //    dangerous read, and D13 is explicit that it is treated as a write. And it is asked without the tenant:
        //    nothing narrows a statement to one, so the database as a whole is what it reads and what it is
        //    authorized for (SqlScope). The audit and the log name that scope too, success and refusal alike, because
        //    it is what was asked and what ran.
        StudioScope sqlScope = SqlScope(scope);
        ResolvedSource resolved;
        try
        {
            resolved = await resolver
                .ResolveAsync(sqlScope, nameof(StudioCapability.RunSql), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException denied)
        {
            audit.RecordScopeDenied(denied.Scope, WritePolicyName(), action, target, StudioCapability.RunSql);
            logger.SqlRejected(
                user, sqlScope.StoreKey, sqlScope.DatabaseId, "not authorized for this database as a whole", statement);
            throw;
        }

        // 3. The advisory guard. Not the security boundary - the transaction below is - but it is what turns
        //    "delete from …" into a sentence instead of a SQLSTATE.
        SqlGuardResult guard = ReadOnlySqlGuard.Check(statement);
        if (!guard.Allowed)
        {
            SqlRejection rejection = SqlRejection.FromGuard(guard);

            audit.Record(action, target, succeeded: false, rejection.Message, StudioCapability.RunSql, sqlScope);
            logger.SqlRejected(user, sqlScope.StoreKey, sqlScope.DatabaseId, rejection.Reason, statement);

            return SqlConsoleResult.Refused(rejection, settings.MaxSqlConsoleRows);
        }

        return await RunAsync(resolved, sqlScope, statement, action, user, bind, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqlConsoleResult> RunAsync(
        ResolvedSource resolved,
        StudioScope scope,
        string statement,
        string action,
        string user,
        Action<NpgsqlCommand>? bind,
        CancellationToken cancellationToken)
    {
        List<string> notices = [];
        string target = Target(statement);
        int maxRows = settings.MaxSqlConsoleRows;

        void OnNotice(object? sender, NpgsqlNoticeEventArgs args)
        {
            // Synchronous by construction: an `async` handler on an event is an async void with different spelling,
            // and AGENTS.md hard rule 6 forbids it in every shape.
            if (notices.Count < 50)
            {
                notices.Add(args.Notice.Severity + ": " + args.Notice.MessageText);
            }
        }

        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = settings.QueryTimeout,
            MaxRows = maxRows,
            Role = settings.SqlConsoleRole,
        });

        await using NpgsqlConnection connection = resolved.Target.CreateConnection(StudioConnectionUsage.Read);
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
                maxRows,
                result.Duration,
                notices,
                result.Error,
                null);
        }
        catch (Exception exception) when (exception is (NpgsqlException and not PostgresException) or SocketException)
        {
            // The connection itself, rather than the statement: nothing ran, and the page says so with the same shape
            // it uses for a SQLSTATE. A host name that does not resolve reaches here as the SocketException itself -
            // Npgsql 9.0.4 does not wrap it - and used to escape unaudited.
            audit.Record(action, target, succeeded: false, exception.Message, StudioCapability.RunSql, scope);
            logger.SqlRejected(user, scope.StoreKey, scope.DatabaseId, exception.Message, statement);

            return new SqlConsoleResult(
                [],
                [],
                false,
                maxRows,
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

    /// <summary>The policy a console refusal names: the write policy, which falls back to the scope policy.</summary>
    private string WritePolicyName() => settings.WritePolicy ?? settings.ScopePolicy ?? "(none)";

    private string UserName()
    {
        // Read from the already-completed task, exactly as StudioActionLog does: every caller is a step of a service
        // method and a circuit's authentication state is settled long before a click.
        Task<AuthenticationState> state = authenticationStateProvider.GetAuthenticationStateAsync();

        if (!state.IsCompletedSuccessfully)
        {
            return Unknown;
        }

        string? name = state.Result.User.Identity?.Name;

        return string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
    }
}
