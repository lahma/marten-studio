using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace PostgresStudio.Services.Statements;

/// <summary>The Statements page's addresses: the list, and one statement's drill-in by its <c>queryid</c> (D9).</summary>
internal static class StatementLinks
{
    /// <summary>The page's route under the studio's path.</summary>
    public const string Route = "statements";

    /// <summary>The query-string name of the drill-in's statement.</summary>
    public const string QueryIdParameter = "queryid";

    /// <summary>The page, with the scope carried under the studio's own source parameter.</summary>
    public static string ToStatements(IStudioSettings settings, StudioDescriptor descriptor, StudioScope? scope)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);
        return Components.StudioScopeQuery.Append(Components.StudioLink.To(settings, Route), scope, descriptor.SourceQueryParameter);
    }

    /// <summary>One statement's drill-in: the page, its <c>queryid</c> and the scope - a link that reopens the same statement.</summary>
    public static string ToStatement(IStudioSettings settings, StudioDescriptor descriptor, StudioScope? scope, long queryId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);

        string url = Components.StudioLink.To(settings, Route) + "?" + QueryIdParameter + "=" + queryId.ToString(CultureInfo.InvariantCulture);
        return Components.StudioScopeQuery.Append(url, scope, descriptor.SourceQueryParameter);
    }

    /// <summary>A <c>queryid</c> from the query string, or <see langword="null" /> for anything that is not one.</summary>
    public static long? ParseQueryId(string? value) =>
        long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long queryId) ? queryId : null;
}

/// <summary>What the Statements page's drill-in shows of one statement, and of its plan, for one visitor (X1).</summary>
internal sealed record StatementPlanView
{
    /// <summary>Why nothing is shown, or <see cref="DatabaseRefusal.None" />: the Statements page's own gate.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal.</summary>
    public string? Reason { get; init; }

    /// <summary>The statement's <c>queryid</c>.</summary>
    public long QueryId { get; init; }

    /// <summary>The statement as this visitor may see it - its text masked, its counters - or <see langword="null" /> when it is not among those read.</summary>
    public StatementRowView? Statement { get; init; }

    /// <summary>Its plan, masked for this visitor, or <see langword="null" />.</summary>
    public QueryPlan? Plan { get; init; }

    /// <summary>Why there is no plan, or <see langword="null" />.</summary>
    public string? NoPlan { get; init; }

    /// <summary>What the plan is and can say: PostgreSQL's generic plan of the recorded text.</summary>
    public string? Caveat { get; init; }

    /// <summary>When it was planned.</summary>
    public DateTimeOffset? PlannedAt { get; init; }

    /// <summary>What ran, for "Show SQL": the plan's statements, its batch's shape - never the text.</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>A refusal: nothing but the sentence.</summary>
    public static StatementPlanView Refused(long queryId, DatabaseRefusal refusal, string reason) =>
        new() { QueryId = queryId, Refusal = refusal, Reason = reason };
}

/// <summary>What one plan read came to: the plan's JSON, or the studio's sentence for why there is none.</summary>
internal sealed record StatementPlanRead
{
    /// <summary>The plan, as <c>EXPLAIN (VERBOSE, FORMAT JSON)</c> printed it, unmasked: the service masks it.</summary>
    public string? Json { get; init; }

    /// <summary>Why there is no plan, in the studio's words, or <see langword="null" />.</summary>
    public string? NotPlanned { get; init; }

    /// <summary>The SQLSTATE Postgres refused the plan with, or <see langword="null" />.</summary>
    public string? SqlState { get; init; }

    /// <summary>What ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];
}

/// <summary>
/// Plans one statement <c>pg_stat_statements</c> recorded, for the Statements page's drill-in (X1): its generic plan, as the
/// console's role, in a read-only transaction, under the visitor's own <c>search_path</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>As <c>SqlConsoleRole</c></b> (D29: "plans run as <c>SqlConsoleRole</c>"), never the store's own role: only the statement
/// sweep's two reads of <c>pg_stat_statements</c> run as that role (<c>StatementChecksFenceTests</c>). A host that narrowed the
/// console role sees a plan of only what that role may read, and Postgres' <c>42501</c> for the rest.
/// </para>
/// <para>
/// <b>The visitor's <c>search_path</c>.</b> In one read-only transaction, under <c>statement_timeout</c> (the studio's
/// <c>QueryTimeout</c>, at most the sweep's own bound) and <c>lock_timeout</c>: the role's effective path is read, every
/// schema the visitor may not see is taken off it, the rest is set for the transaction, and only then is the text prepared and
/// explained. So a name the text reaches only through a withheld schema fails to resolve - no plan, and the sentence says why -
/// and every name the plan prints from such a schema carries its schema, for the mask (<see cref="StatementPlanQueries" />).
/// </para>
/// <para>
/// <b>Nothing left behind.</b> The batch deallocates what it prepared. A batch that stops short - refused, cancelled, cut off -
/// is followed by a deallocate in a read-only transaction of its own; a name another batch left on the pooled session
/// (<c>42P05</c>) is taken back and the plan tried once more, as T02 does. If even the deallocate fails, that one session
/// keeps the one name until Npgsql's <c>DISCARD ALL</c> or the next plan on it takes it back, and a Debug line says so.
/// </para>
/// <para>
/// <b>Locks.</b> Planning a <c>select</c> takes an <c>AccessShareLock</c> on each table and index it reads, as the read itself
/// would, held to the rollback; a write is never planned, because planning one takes the lock its execution would
/// (<see cref="HealthStatementQueries.Refusal" />). <see cref="IHealthStatementObserver" /> - a test seam, absent in a host - is
/// told before the rollback, so the no-lock test sees what the session held.
/// </para>
/// </remarks>
internal sealed class StatementPlanReader
{
    /// <summary>The longest the reader waits to take its prepared statement back.</summary>
    internal static readonly TimeSpan ForgetBound = TimeSpan.FromSeconds(5);

    private readonly IStudioSettings settings;
    private readonly ILogger<StatementPlanReader> logger;
    private readonly IHealthStatementObserver? observer;
    private readonly HypotheticalIndexReader hypothetical;

    public StatementPlanReader(
        IStudioSettings settings,
        ILogger<StatementPlanReader> logger,
        IHealthStatementObserver? observer = null,
        HypotheticalIndexReader? hypothetical = null)
    {
        this.settings = settings;
        this.logger = logger;
        this.observer = observer;
        this.hypothetical = hypothetical ?? new HypotheticalIndexReader(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<HypotheticalIndexReader>.Instance);
    }

    /// <summary>
    /// X2: the generic plan of <paramref name="text" /> before and after one hypothetical index, as <see cref="ReadAsync" />
    /// plans it - <c>SqlConsoleRole</c>, the visitor's <c>search_path</c>, the read-only transaction - with hypopg asked for the
    /// index between the two and reset before the connection goes back (<see cref="HypotheticalIndexReader" />).
    /// </summary>
    /// <param name="database">The database, resolved through the Statements page's gate.</param>
    /// <param name="queryId">The statement's <c>queryid</c>.</param>
    /// <param name="text">Its text, as the statement sweep read it.</param>
    /// <param name="truncated">Whether that text was cut.</param>
    /// <param name="withheld">The schemas the visitor may not see: taken off the <c>search_path</c>.</param>
    /// <param name="index">The index to imagine.</param>
    /// <param name="vet">Judges the plan without the index before anything is imagined (<see cref="HypotheticalIndexPlanner.Vet" />).</param>
    /// <param name="cancellationToken">Cancels the what-if; never the cleanup after it.</param>
    /// <returns>What the engine read, or why the text is not planned.</returns>
    public async Task<(HypotheticalIndexRead? Read, string? NotPlanned)> WhatIfAsync(
        IStudioDatabase database,
        long queryId,
        string text,
        bool truncated,
        IReadOnlySet<string> withheld,
        HypotheticalIndex index,
        Func<string, string?> vet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(withheld);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(vet);

        if (StatementPlanQueries.GenericPlan(text, truncated) is not { } batch)
        {
            return (null, Health.Checks.ExpensiveStatementsCheck.RefusalText(HealthStatementQueries.Refusal(text, truncated)));
        }

        TimeSpan statementTimeout = settings.QueryTimeout < HealthSweeper.MaxStatementTimeout ? settings.QueryTimeout : HealthSweeper.MaxStatementTimeout;
        int commandTimeout = (int) Math.Ceiling(statementTimeout.TotalSeconds) + 5;

        HypotheticalIndexPlanner planner = new(
            PlanAsync: async (connection, transaction, token) =>
            {
                (string? json, _) = await StatementPlanQueries
                    .ReadPlanAsync(connection, transaction, batch, ExplainStatements.MaxPlanCharacters, commandTimeout, token)
                    .ConfigureAwait(false);
                return json;
            },
            PrepareAsync: async (connection, transaction, token) =>
            {
                IReadOnlyList<string> path = await StatementPlanQueries.ReadSearchPathAsync(connection, transaction, commandTimeout, token).ConfigureAwait(false);
                await StatementPlanQueries
                    .SetSearchPathAsync(connection, transaction, StatementPlanQueries.SearchPath(path.Where(x => !withheld.Contains(x))), commandTimeout, token)
                    .ConfigureAwait(false);
            },
            Vet: vet,
            AfterFailureAsync: connection =>
                ForgetAsync(connection, new ReadOnlySqlSession(new ReadOnlySqlOptions { StatementTimeout = statementTimeout, Role = settings.SqlConsoleRole }), commandTimeout, queryId));

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return (await hypothetical.ReadAsync(database, index, planner, cancellationToken).ConfigureAwait(false), null);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicatePreparedStatement && attempt == 0)
            {
                // The name was on that session - left by a plan that could not take it back. The failure path took it back; once more.
            }
        }
    }

    /// <summary>
    /// Every statement's <c>lock_timeout</c>, when a test needs one shorter than the session's default; <see langword="null" /> -
    /// always, in a host - is <see cref="ReadOnlySqlOptions.LockTimeout" />'s default.
    /// </summary>
    internal TimeSpan? LockTimeout { get; set; }

    /// <summary>The plan of <paramref name="text" /> on <paramref name="database" />, or why there is none.</summary>
    /// <param name="database">The database, resolved through the Statements page's gate.</param>
    /// <param name="queryId">The statement's <c>queryid</c>, for "Show SQL".</param>
    /// <param name="text">Its text, as the statement sweep read it.</param>
    /// <param name="truncated">Whether that text was cut.</param>
    /// <param name="withheld">The schemas the visitor may not see: taken off the <c>search_path</c>.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    public async Task<StatementPlanRead> ReadAsync(
        IStudioDatabase database,
        long queryId,
        string text,
        bool truncated,
        IReadOnlySet<string> withheld,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(withheld);

        int parameters = HealthStatementQueries.HighestParameter(text);
        IReadOnlyList<HealthStatement> shown =
        [
            new(StatementPlanQueries.SearchPathSql, []),
            new(StatementPlanQueries.SetSearchPathSql, ["path"]),
            new(StatementPlanQueries.Shown(queryId, parameters), []),
        ];

        if (StatementPlanQueries.GenericPlan(text, truncated) is not { } batch)
        {
            return new StatementPlanRead
            {
                NotPlanned = Health.Checks.ExpensiveStatementsCheck.RefusalText(HealthStatementQueries.Refusal(text, truncated)),
                Statements = shown,
            };
        }

        TimeSpan statementTimeout = settings.QueryTimeout < HealthSweeper.MaxStatementTimeout ? settings.QueryTimeout : HealthSweeper.MaxStatementTimeout;
        ReadOnlySqlOptions options = new() { StatementTimeout = statementTimeout, Role = settings.SqlConsoleRole };

        if (LockTimeout is { } lockTimeout)
        {
            options = options with { LockTimeout = lockTimeout };
        }

        ReadOnlySqlSession session = new(options);
        int commandTimeout = (int) Math.Ceiling(statementTimeout.TotalSeconds) + 5;

        await using NpgsqlConnection connection = database.CreateConnection(StudioConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                (string? json, long? characters) = await session.InTransactionAsync(
                        connection,
                        async (transaction, token) =>
                        {
                            IReadOnlyList<string> path = await StatementPlanQueries
                                .ReadSearchPathAsync(connection, transaction, commandTimeout, token)
                                .ConfigureAwait(false);

                            await StatementPlanQueries
                                .SetSearchPathAsync(connection, transaction, StatementPlanQueries.SearchPath(path.Where(x => !withheld.Contains(x))), commandTimeout, token)
                                .ConfigureAwait(false);

                            (string? Json, long? Characters) plan = await StatementPlanQueries
                                .ReadPlanAsync(connection, transaction, batch, ExplainStatements.MaxPlanCharacters, commandTimeout, token)
                                .ConfigureAwait(false);

                            if (observer is not null)
                            {
                                await observer.ObserveAsync(connection.ProcessID, shown[2], token).ConfigureAwait(false);
                            }

                            return plan;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return new StatementPlanRead
                {
                    Json = json,
                    NotPlanned = json is not null ? null
                        : characters is { } length ? QueryPlanReader.TooLarge(length)
                        : "Not planned: Postgres returned no plan.",
                    Statements = shown,
                };
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicatePreparedStatement && attempt == 0)
            {
                // The name is on this session already - left by a plan that could not take it back. Take it back, and plan once more.
                await ForgetAsync(connection, session, commandTimeout, queryId).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                await ForgetAsync(connection, session, commandTimeout, queryId).ConfigureAwait(false);

                return new StatementPlanRead { NotPlanned = NotPlanned(exception.SqlState), SqlState = exception.SqlState, Statements = shown };
            }
            catch (Exception)
            {
                // A cancelled plan, a broken connection, anything else: the prepared statement must not outlive the plan.
                await ForgetAsync(connection, session, commandTimeout, queryId).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>The studio's sentence for a plan Postgres refused, by SQLSTATE: never Postgres' own words, which name what they name.</summary>
    internal static string NotPlanned(string sqlState) =>
        "Not planned: Postgres refused the plan (SQLSTATE " + sqlState + ")" + sqlState switch
        {
            PostgresErrorCodes.InsufficientPrivilege => " - SqlConsoleRole, which the studio plans as, may not read a table it names.",
            PostgresErrorCodes.IndeterminateDatatype => " - the type of one of its placeholders cannot be told from the statement alone.",
            PostgresErrorCodes.QueryCanceled => " - planning it ran out of the statement_timeout.",
            PostgresErrorCodes.LockNotAvailable => " - somebody holds a lock on a table it names that a plan has to wait for.",
            PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedFunction or PostgresErrorCodes.UndefinedObject =>
                " - a name in it does not resolve on the search_path the studio plans with: the console role's own, less every "
                + "schema you may not see.",
            _ => ".",
        };

    /// <summary>
    /// Takes the plan's prepared statement back, under a bound of its own and never the caller's token - a cancelled plan is
    /// exactly the one that has to - in a read-only transaction of its own. If that fails, the session keeps the one name, and a
    /// Debug line says so.
    /// </summary>
    private async Task ForgetAsync(NpgsqlConnection connection, ReadOnlySqlSession session, int commandTimeout, long queryId)
    {
        if (connection.State is not System.Data.ConnectionState.Open)
        {
            // A broken connection is discarded, with whatever it held.
            return;
        }

        using CancellationTokenSource bound = new(ForgetBound);

        try
        {
            await session.InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        await StatementPlanQueries.DeallocateAsync(connection, transaction, commandTimeout, token).ConfigureAwait(false);
                        return 0;
                    },
                    bound.Token)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidSqlStatementName)
        {
            // Never prepared, or the batch's own deallocate ran: there is nothing to take back.
        }
        catch (Exception exception)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    exception,
                    "The plan of queryid {QueryId} could not take its prepared statement {Name} back; that one session keeps it until it is next reset or planned on",
                    queryId.ToString(CultureInfo.InvariantCulture),
                    HealthStatementQueries.PlanStatementName);
            }
        }
    }
}
