using System.Diagnostics;
using System.Globalization;

using Npgsql;

using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Services.Query;

/// <summary>One planner setting a run changes for its plan alone (0.10-B): the name from the allow-list, and the value as typed.</summary>
/// <param name="Name">The setting's name, one of <see cref="PlanSettingsQueries.Allowed" />.</param>
/// <param name="Value">The value, as text: <c>64MB</c>, <c>off</c>, <c>1.1</c>. It reaches Postgres as a parameter, and Postgres reads it.</param>
internal sealed record PlanSetting(string Name, string Value);

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

    /// <summary>
    /// 0.10-B: planner settings changed for this plan alone, at most <see cref="PlanSettingsQueries.MaxSettings" />, each one on the
    /// allow-list. Applied inside the run's read-only transaction before <c>EXPLAIN</c> and rolled back with it.
    /// </summary>
    public IReadOnlyList<PlanSetting> SessionSettings { get; init; } = [];

    /// <summary>
    /// 0.10-B: this run's <c>statement_timeout</c> in seconds, never above the host's <c>QueryTimeout</c> (the console clamps it), or
    /// <see langword="null" /> for the host's own.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// 0.10-B: also read which of the allow-listed settings the server has, in the same transaction - before the run's own settings
    /// are applied, so each is as the server has it - for the page to offer a what-if of.
    /// </summary>
    public bool OfferSettings { get; init; }
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

    /// <summary>0.10-B: the allow-listed settings the server has, when the request asked (<see cref="PlanRunRequest.OfferSettings" />).</summary>
    public IReadOnlyList<PlanSettingRow> SettingOffers { get; init; } = [];

    /// <summary>0.10-B: the <c>statement_timeout</c> the run was held to, in seconds - the override clamped to the host's timeout, or the host's own.</summary>
    public double TimeoutSeconds { get; init; }

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

    /// <summary>The reason for a setting that is not on the allow-list.</summary>
    internal const string SettingNotAllowedReason = "SettingNotAllowed";

    /// <summary>The reason for more settings than a run changes, or one named twice.</summary>
    internal const string SettingsInvalidReason = "SettingsInvalid";

    /// <summary>The reason for a setting's value that is empty, too long or not text a setting takes.</summary>
    internal const string SettingValueInvalidReason = "SettingValueInvalid";

    /// <summary>The reason for a memory setting above the ceiling the page holds it to.</summary>
    internal const string SettingAboveCeilingReason = "SettingAboveCeiling";

    /// <summary>The reason for a timeout override below a second.</summary>
    internal const string TimeoutInvalidReason = "TimeoutInvalid";

    /// <summary>
    /// The <c>statement_timeout</c> a run is held to: the override, but never more than <paramref name="queryTimeout" />, the host's
    /// own. The page says so beside the field; the clamp is here, where the run is, and is the only thing that decides.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="queryTimeout">The host's <c>QueryTimeout</c>.</param>
    public static TimeSpan EffectiveTimeout(PlanRunRequest request, TimeSpan queryTimeout)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.TimeoutSeconds is { } seconds and >= 1
            ? TimeSpan.FromSeconds(Math.Min(seconds, Math.Max(queryTimeout.TotalSeconds, 0.001)))
            : queryTimeout;
    }

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

        if (SettingsRefusal(request) is { } settings)
        {
            return settings;
        }

        if (request.TimeoutSeconds is < 1)
        {
            return new SqlRejection(TimeoutInvalidReason, "A statement timeout override is at least one second; leave it empty for the host's own.", null, -1);
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

    /// <summary>
    /// The refusal of a request's changed settings, which needs no database: a name that is not on the allow-list - exact, so a name
    /// that merely looks like one is not - more than <see cref="PlanSettingsQueries.MaxSettings" />, a name twice, or a value that is
    /// empty, longer than <see cref="PlanSettingsQueries.MaxValueCharacters" /> or holds a control character, or a memory setting above its
    /// ceiling (<see cref="PlanSettingsQueries.Ceiling" />: <c>work_mem</c> 1 GB, <c>hash_mem_multiplier</c> 8) or one the studio cannot
    /// read. Any other value that is text but not one the setting takes is Postgres's to refuse, as an ordinary error.
    /// </summary>
    private static SqlRejection? SettingsRefusal(PlanRunRequest request)
    {
        IReadOnlyList<PlanSetting> settings = request.SessionSettings;

        if (settings.Count == 0)
        {
            return null;
        }

        if (settings.Count > PlanSettingsQueries.MaxSettings)
        {
            return new SqlRejection(
                SettingsInvalidReason,
                "A plan changes at most " + PlanSettingsQueries.MaxSettings.ToString(CultureInfo.InvariantCulture) + " settings.",
                null,
                -1);
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (PlanSetting setting in settings)
        {
            if (setting is null || !PlanSettingsQueries.IsAllowed(setting.Name))
            {
                return new SqlRejection(
                    SettingNotAllowedReason,
                    "That is not a setting the Plan page changes: it offers only the planner's cost, memory, parallelism and enable_* settings.",
                    null,
                    -1);
            }

            if (!seen.Add(setting.Name))
            {
                return new SqlRejection(SettingsInvalidReason, "A setting is changed once in a plan.", null, -1);
            }

            if (string.IsNullOrWhiteSpace(setting.Value)
                || setting.Value.Length > PlanSettingsQueries.MaxValueCharacters
                || setting.Value.Any(char.IsControl))
            {
                return new SqlRejection(
                    SettingValueInvalidReason,
                    "A setting's value is text of at most " + PlanSettingsQueries.MaxValueCharacters.ToString(CultureInfo.InvariantCulture) + " characters.",
                    null,
                    -1);
            }

            // The memory settings have a ceiling in every mode, and a value the studio cannot read is not passed on to be read by Postgres.
            if (PlanSettingsQueries.Ceiling(setting.Name, setting.Value) is { } ceiling)
            {
                return new SqlRejection(ceiling.Unreadable ? SettingValueInvalidReason : SettingAboveCeilingReason, ceiling.Message, null, -1);
            }
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
                        (transaction, token) => PlanRunQueries.RunAsync(
                            connection,
                            transaction,
                            statements,
                            request.Values,
                            commandTimeoutSeconds,
                            token,
                            request.SessionSettings,
                            request.OfferSettings),
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
                SettingOffers = outcome.Offers,
            };
        }
    }

    /// <summary>
    /// Takes the prepared statement back, under a bound of its own and never the caller's token - a cancelled run is exactly the
    /// one that has to - in a read-only transaction of its own.
    /// </summary>
    internal static async Task ForgetAsync(NpgsqlConnection connection, ReadOnlySqlSession session, int commandTimeoutSeconds, Action<Exception>? onFailed)
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

    /// <summary>What the audit ring says of a plan that was drawn: <c>planned in 3 ms</c>, and what the run changed.</summary>
    internal static string Outcome(PlanMode mode, TimeSpan duration, PlanRunRequest? request = null, TimeSpan? timeout = null)
    {
        string took = ((long) duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms";

        string outcome = mode switch
        {
            PlanMode.Analyze => "ran under EXPLAIN ANALYZE in " + took,
            PlanMode.Generic => "generic plan in " + took,
            _ => "planned in " + took,
        };

        return outcome + Changes(request, timeout);
    }

    /// <summary>
    /// What a run changed, as the audit ring and the log say it: <c> with work_mem=64MB, jit=off, statement_timeout=5s</c>, or nothing.
    /// A setting's value is what a visitor typed, so it goes through <see cref="StudioLogValue" />; its name is on the allow-list.
    /// </summary>
    /// <param name="request">The request, or <see langword="null" />.</param>
    /// <param name="timeout">The <c>statement_timeout</c> the run was held to, when it is not the host's own.</param>
    /// <param name="escape">
    /// Whether each value is escaped here. The audit ring takes it so; the log does not, because <c>SqlExecuted</c> and <c>SqlRejected</c>
    /// escape the whole text they are given, and a value escaped here would be escaped twice there.
    /// </param>
    internal static string Changes(PlanRunRequest? request, TimeSpan? timeout = null, bool escape = true)
    {
        List<string> changes = [];

        foreach (PlanSetting setting in request?.SessionSettings ?? [])
        {
            changes.Add(escape ? StudioLogValue.Cap(setting.Name) + "=" + StudioLogValue.Cap(setting.Value) : setting.Name + "=" + setting.Value);
        }

        if (timeout is { } held)
        {
            changes.Add("statement_timeout=" + held.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s");
        }

        return changes.Count == 0 ? string.Empty : " with " + string.Join(", ", changes);
    }
}
