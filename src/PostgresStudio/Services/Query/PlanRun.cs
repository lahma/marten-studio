using System.Diagnostics;
using System.Globalization;

using Npgsql;

using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Services.Query;

/// <summary>What the Plan page asks the console to plan (X3, issue #28).</summary>
/// <param name="Statement">The statement as typed - and as sent, but for the <c>EXPLAIN</c> or <c>PREPARE</c> in front of it.</param>
/// <param name="Mode">Planned, analyzed or the generic plan.</param>
internal sealed record PlanRunRequest(string Statement, PlanMode Mode)
{
    /// <summary>The <c>EXPLAIN</c> options asked for.</summary>
    public PlanOptions Options { get; init; } = new();

    /// <summary>
    /// The value of each <c>$n</c> placeholder, in order, <see langword="null" /> for SQL <c>NULL</c> - for a custom plan, and
    /// for an analyzed statement that has placeholders. The generic plan takes none.
    /// </summary>
    public IReadOnlyList<string?> Values { get; init; } = [];
}

/// <summary>What the console came back with for a <see cref="PlanRunRequest" />: the plan, or why there is none.</summary>
internal sealed record PlanRunResult
{
    /// <summary>The plan, whole, as <c>EXPLAIN (FORMAT JSON)</c> printed it.</summary>
    public string? PlanJson { get; init; }

    /// <summary>Why a plan Postgres returned is not drawn - it is larger than the studio draws - or <see langword="null" />.</summary>
    public string? PlanUnavailable { get; init; }

    /// <summary>The Postgres error, when there was one.</summary>
    public SqlError? Error { get; init; }

    /// <summary>The guard's or the console's refusal, when nothing was sent.</summary>
    public SqlRejection? Rejection { get; init; }

    /// <summary>How long the commands took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Anything Postgres raised as a notice.</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>The types <c>prepare</c> inferred for the statement's placeholders, in order, when it was prepared.</summary>
    public IReadOnlyList<string> ParameterTypes { get; init; } = [];

    /// <summary>The options the server is too old for, which were not sent.</summary>
    public IReadOnlyList<string> LeftOut { get; init; } = [];

    /// <summary>What <c>EXPLAIN</c> was asked for, as its option list.</summary>
    public string Options { get; init; } = string.Empty;

    /// <summary>The server's major version, once a connection was made.</summary>
    public int? ServerMajor { get; init; }

    /// <summary>How many characters precede the typed statement in the command that failed, or <c>-1</c>: the error is not in the typed text.</summary>
    public int ErrorOffset { get; init; } = -1;

    /// <summary>How many <c>$n</c> placeholders the statement has.</summary>
    public int Placeholders { get; init; }

    /// <summary>Whether there is a plan to draw.</summary>
    public bool Succeeded => PlanJson is not null && Error is null && Rejection is null;

    /// <summary>A run the console refused before anything was sent.</summary>
    public static PlanRunResult Refused(SqlRejection rejection, int placeholders) =>
        new() { Rejection = rejection, Placeholders = placeholders };
}

/// <summary>The checks on a <see cref="PlanRunRequest" /> that need no database, after the guard has let the statement through.</summary>
internal static class PlanRequests
{
    /// <summary>The reason for a generic plan of a statement with no placeholder.</summary>
    internal const string NoPlaceholdersReason = "NoPlaceholders";

    /// <summary>The reason for a custom or analyzed plan of a statement whose placeholders have no values.</summary>
    internal const string ValuesMissingReason = "ValuesMissing";

    /// <summary>The reason for a statement with more placeholders than the page offers boxes for.</summary>
    internal const string TooManyPlaceholdersReason = "TooManyPlaceholders";

    /// <summary>The reason for a value longer than a box carries.</summary>
    internal const string ValueTooLongReason = "ValueTooLong";

    /// <summary>Why <paramref name="request" /> is not planned, whatever the database says, or <see langword="null" />.</summary>
    /// <param name="request">The request.</param>
    /// <param name="placeholders">The statement's highest <c>$n</c> (<see cref="PlanStatements.Placeholders" />).</param>
    public static SqlRejection? Refusal(PlanRunRequest request, int placeholders)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (placeholders > PlanStatements.MaxParameters)
        {
            return new SqlRejection(
                TooManyPlaceholdersReason,
                "This statement has more than " + PlanStatements.MaxParameters.ToString(CultureInfo.InvariantCulture) + " placeholders, which is more than the Plan page takes values for.",
                null,
                -1);
        }

        if (request.Mode == PlanMode.Generic)
        {
            return placeholders == 0
                ? new SqlRejection(
                    NoPlaceholdersReason,
                    "A generic plan is the plan of a statement with $1, $2 … placeholders, and this one has none: Explain plans it as it is.",
                    null,
                    -1)
                : null;
        }

        if (placeholders > 0 && request.Values.Count != placeholders)
        {
            return new SqlRejection(
                ValuesMissingReason,
                "This statement has " + placeholders.ToString(CultureInfo.InvariantCulture) + (placeholders == 1 ? " placeholder" : " placeholders")
                + " ($1" + (placeholders > 1 ? " to $" + placeholders.ToString(CultureInfo.InvariantCulture) : string.Empty)
                + "): give each a value, or ask for the Generic plan, which needs none.",
                null,
                -1);
        }

        if (placeholders > 0 && request.Values.Any(static x => x is { Length: > PlanStatements.MaxValueCharacters }))
        {
            return new SqlRejection(
                ValueTooLongReason,
                "A value is longer than the " + PlanStatements.MaxValueCharacters.ToString("N0", CultureInfo.InvariantCulture) + " characters a box carries.",
                null,
                -1);
        }

        return null;
    }
}

/// <summary>
/// Runs one <see cref="PlanRunRequest" /> on a connection the console opened, in its read-only session (X3, issue #28): the
/// commands <see cref="PlanStatements" /> builds, in one transaction, and the cleanup a prepared statement needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is left behind.</b> A prepared statement outlives its transaction, and a rollback does not take it back; so a run
/// that stopped before its own <c>deallocate</c> - Postgres refused a command, the visitor cancelled, the connection broke -
/// deallocates in a read-only transaction of its own, under a bound of its own and never the caller's token. A name another
/// run left on the pooled session (<c>42P05</c>) is taken back and the run tried once more, as T02 and the drill-in do.
/// If even the deallocate fails, that one session keeps the one name until Npgsql's reset or the next plan on it takes it back,
/// and the caller's <c>onForgetFailed</c> says so.
/// </para>
/// </remarks>
internal static class PlanRunner
{
    /// <summary>The longest the runner waits to take its prepared statement back.</summary>
    internal static readonly TimeSpan ForgetBound = TimeSpan.FromSeconds(5);

    /// <summary>Runs the request.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="session">The read-only session: transaction, timeouts and role.</param>
    /// <param name="request">What to plan.</param>
    /// <param name="placeholders">The statement's highest <c>$n</c>.</param>
    /// <param name="commandTimeoutSeconds">The client-side bound on each command.</param>
    /// <param name="notices">What Postgres raised, filled by the caller's handler.</param>
    /// <param name="onForgetFailed">Told when the prepared statement could not be taken back.</param>
    /// <param name="cancellationToken">Cancels the run, never the cleanup.</param>
    public static async Task<PlanRunResult> RunAsync(
        NpgsqlConnection connection,
        ReadOnlySqlSession session,
        PlanRunRequest request,
        int placeholders,
        int commandTimeoutSeconds,
        IReadOnlyList<string> notices,
        Action<Exception>? onForgetFailed,
        CancellationToken cancellationToken)
    {
        int major = connection.PostgreSqlVersion.Major;
        PlanStatementSet statements = PlanStatements.Build(request.Statement, request.Mode, request.Options, placeholders, major * 10_000);
        Stopwatch stopwatch = Stopwatch.StartNew();

        for (int attempt = 0; ; attempt++)
        {
            PlanQueryOutcome outcome;

            try
            {
                outcome = await session.InTransactionAsync(
                        connection,
                        (transaction, token) => PlanRunQueries.RunAsync(connection, transaction, statements, request.Values, commandTimeoutSeconds, token),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A cancelled run, a broken connection, anything else: the prepared statement must not outlive the run.
                if (statements.Prepare is not null)
                {
                    await ForgetAsync(connection, session, commandTimeoutSeconds, onForgetFailed).ConfigureAwait(false);
                }

                throw;
            }

            if (outcome.Error is { SqlState: PostgresErrorCodes.DuplicatePreparedStatement } && attempt == 0 && statements.Prepare is not null)
            {
                // The name is on this session already, left by a run that could not take it back - taken back now; once more.
                await ForgetAsync(connection, session, commandTimeoutSeconds, onForgetFailed).ConfigureAwait(false);
                continue;
            }

            if (!outcome.Deallocated)
            {
                await ForgetAsync(connection, session, commandTimeoutSeconds, onForgetFailed).ConfigureAwait(false);
            }

            stopwatch.Stop();

            return new PlanRunResult
            {
                PlanJson = outcome.Json,
                PlanUnavailable = outcome.Characters is { } characters ? QueryPlanReader.TooLarge(characters) : null,
                Error = outcome.Error,
                Duration = stopwatch.Elapsed,
                Notices = notices,
                ParameterTypes = outcome.ParameterTypes,
                LeftOut = statements.LeftOut,
                Options = statements.Options,
                ServerMajor = major,
                ErrorOffset = outcome.ErrorOffset,
                Placeholders = placeholders,
            };
        }
    }

    /// <summary>
    /// Takes the prepared statement back, under a bound of its own and never the caller's token - a cancelled run is exactly the
    /// one that has to - in a read-only transaction of its own.
    /// </summary>
    private static async Task ForgetAsync(NpgsqlConnection connection, ReadOnlySqlSession session, int commandTimeoutSeconds, Action<Exception>? onFailed)
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
                        await StatementPlanQueries.DeallocateAsync(connection, transaction, commandTimeoutSeconds, token).ConfigureAwait(false);
                        return 0;
                    },
                    bound.Token)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InvalidSqlStatementName)
        {
            // Never prepared, or the run's own deallocate ran: there is nothing to take back.
        }
        catch (Exception exception)
        {
            onFailed?.Invoke(exception);
        }
    }

    /// <summary>What the audit ring says of a plan that was drawn: <c>planned in 3 ms</c>.</summary>
    internal static string Outcome(PlanMode mode, TimeSpan duration)
    {
        string took = ((long) duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";

        return mode switch
        {
            PlanMode.Analyze => "ran under EXPLAIN ANALYZE in " + took,
            PlanMode.Generic => "generic plan in " + took,
            _ => "planned in " + took,
        };
    }
}
