using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>What one plan run read: the plan, or the error Postgres refused it with, and whether it took its prepared statement back.</summary>
/// <param name="Json">The plan, whole, when it is at most <see cref="ExplainStatements.MaxPlanCharacters" /> long.</param>
/// <param name="Characters">How long a plan was that is longer than that, and so not kept.</param>
/// <param name="ParameterTypes">The types <c>prepare</c> inferred for the statement's placeholders, as Postgres spells them.</param>
/// <param name="Error">What Postgres refused with, as a value.</param>
/// <param name="ErrorOffset">How many characters precede the typed statement in the command that failed, or <c>-1</c> when it was not that one.</param>
/// <param name="Deallocated">Whether the run deallocated what it prepared, or never prepared anything.</param>
internal sealed record PlanQueryOutcome(
    string? Json,
    long? Characters,
    IReadOnlyList<string> ParameterTypes,
    SqlError? Error,
    int ErrorOffset,
    bool Deallocated);

/// <summary>
/// Runs <see cref="PlanStatements" />' commands in the read-only transaction the caller opened (X3, issue #28): the second of
/// the Plan page's two statement builders, because it is where <c>pg_prepared_statements</c> is read and where the values
/// become parameters.
/// </summary>
/// <remarks>
/// <para>
/// <b>In order, and in one transaction.</b> A custom plan: <c>prepare</c> (to read the types Postgres infers for the
/// placeholders), the inferred parameter types, <c>deallocate</c>, then <c>explain</c> of the statement itself with the values bound
/// to it as parameters - a protocol-level parameter cannot reach an <c>EXECUTE</c> (a utility statement's arguments are not analyzed
/// with the command's own: "could not determine data type of parameter $1", measured on 14, 17 and 18), but it reaches the
/// statement an <c>EXPLAIN</c> explains, and the planner folds a bound value in as a constant. The generic plan: <c>prepare</c>, the
/// types, <c>set local plan_cache_mode = force_generic_plan</c>, <c>explain … execute</c> with nulls, <c>deallocate</c>. Either way a
/// prepared statement never outlives the transaction on a run that finished. A run that stopped short (Postgres refused a command,
/// which aborts the transaction; the caller cancelled; the connection broke) while it held the name leaves it for the caller to
/// deallocate in a transaction of its own (<c>PlanRunner</c>), because a prepared statement survives a rollback.
/// </para>
/// <para>
/// <b>Errors are values</b>, as the console's are: a <see cref="PostgresException" /> comes back as <see cref="SqlError" /> with the
/// number of characters that precede the typed statement in the command that failed, so the editor can put a caret under it.
/// </para>
/// </remarks>
internal static class PlanRunQueries
{
    /// <summary>The inferred types of a prepared statement's placeholders, by its name, as <c>text[]</c>.</summary>
    internal const string ParameterTypesSql =
        "select parameter_types::text[] from pg_catalog.pg_prepared_statements where name = @name";

    /// <summary>Runs <paramref name="statements" /> and reads the plan.</summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The read-only transaction every command is enlisted in.</param>
    /// <param name="statements">The commands.</param>
    /// <param name="values">The values the custom plan binds, one for each placeholder, <see langword="null" /> for SQL <c>NULL</c>.</param>
    /// <param name="commandTimeoutSeconds">The client-side bound on each command, a little past the server's <c>statement_timeout</c>.</param>
    /// <param name="cancellationToken">The token.</param>
    public static async Task<PlanQueryOutcome> RunAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlanStatementSet statements,
        IReadOnlyList<string?> values,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(values);

        List<string> types = [];
        int offset = -1;

        // Whether the run's prepared statement is on the session: true from the prepare that worked to the deallocate that did.
        bool prepared = false;

        try
        {
            if (statements.Prepare is { } prepare)
            {
                offset = statements.PrepareOffset;
                await ExecuteAsync(connection, transaction, prepare, [], commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                prepared = true;

                offset = -1;
                types = await ReadTypesAsync(connection, transaction, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

                if (statements.InferTypesOnly)
                {
                    // Its work is done: the custom plan is the statement itself, explained with its values.
                    await StatementPlanQueries.DeallocateAsync(connection, transaction, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                    prepared = false;
                }
                else
                {
                    await ExecuteAsync(connection, transaction, PlanStatements.SetPlanCacheMode(statements.PlanCacheMode!), [], commandTimeoutSeconds, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            offset = statements.InferTypesOnly || statements.Prepare is null ? statements.ExplainOffset : -1;

            (string? json, long? characters) = await ReadPlanAsync(
                    connection,
                    transaction,
                    statements.Explain,
                    BoundValues(statements, values),
                    commandTimeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);

            if (prepared)
            {
                offset = -1;
                await StatementPlanQueries.DeallocateAsync(connection, transaction, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                prepared = false;
            }

            return new PlanQueryOutcome(json, characters, types, null, -1, Deallocated: true);
        }
        catch (PostgresException exception)
        {
            // The transaction is aborted, so nothing more can be sent in it: a name the run still holds is the caller's to take back.
            return new PlanQueryOutcome(
                null,
                null,
                types,
                new SqlError(exception.SqlState, exception.MessageText, exception.Position, exception.Detail, exception.Hint),
                offset,
                Deallocated: !prepared);
        }
    }

    /// <summary>The values bound to the <c>explain</c> command: the visitor's, for a custom plan; none for the other two.</summary>
    private static IReadOnlyList<string?> BoundValues(PlanStatementSet statements, IReadOnlyList<string?> values) =>
        statements.BoundParameters == 0 ? [] : [.. values.Take(statements.BoundParameters)];

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<string?> values,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(connection, transaction, sql, values, commandTimeoutSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<string>> ReadTypesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ParameterTypesSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = HealthStatementQueries.PlanStatementName });

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string[] types ? [.. types] : [];
    }

    private static async Task<(string? Json, long? Characters)> ReadPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<string?> values,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(connection, transaction, sql, values, commandTimeoutSeconds);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (reader.FieldCount != 1 || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return (null, null);
        }

        string json = reader.GetString(0);

        return json.Length <= ExplainStatements.MaxPlanCharacters ? (json, null) : (null, json.Length);
    }

    /// <summary>
    /// A command whose values are positional parameters, untyped: Postgres reads each with the input function of the type
    /// <c>prepare</c> inferred for its placeholder, so no type name is ever written into a statement.
    /// </summary>
    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<string?> values,
        int commandTimeoutSeconds)
    {
        NpgsqlCommand command = new(sql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };

        foreach (string? value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Unknown, Value = (object?) value ?? DBNull.Value });
        }

        return command;
    }
}
