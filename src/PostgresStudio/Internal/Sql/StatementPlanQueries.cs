using System.Globalization;

using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The Statements page drill-in's plan of a statement <c>pg_stat_statements</c> recorded (X1): its own builder, beside T02's
/// <see cref="HealthStatementQueries.Explain" /> - which <c>StatementChecksFenceTests</c> keeps T02's alone - with the same
/// refusals, the same one prepared name and the same <c>force_generic_plan</c>, and two differences: the plan is asked for
/// with <c>VERBOSE</c>, so every relation it reads carries its schema for the visitor's mask, and the transaction's
/// <c>search_path</c> is set by the caller to the schemas that visitor may see.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>EXPLAIN (GENERIC_PLAN)</c></b>, which PostgreSQL 16 added and which plans a <c>$1</c> text in <c>psql</c> on 16
/// to 19 (PROBE, 2026-10-26). Npgsql sends every command through the extended protocol, so the server counts the text's
/// placeholders as the command's own parameters: with none bound the bind fails, and with any bound the planner folds the
/// values in as constants (<see cref="HealthStatementQueries.Explain" />' remarks; measured again by
/// <c>StatementPlanLiveTests</c> on 16 and 18). Prepared and explained under <c>force_generic_plan</c>, a statement is planned
/// with no values whatever <c>execute</c> passes - the plan <c>GENERIC_PLAN</c> prints - on every major from 12.
/// </para>
/// <para>
/// <b>Read-only, and nothing left behind.</b> The batch runs in the read-only transaction the caller opens, deallocates what it
/// prepared, and a caller whose batch stopped short deallocates it in a transaction of its own (<see cref="Deallocate" />).
/// The text never contains a <c>;</c> (<see cref="HealthStatementQueries.Refusal" />), so it is one statement of the batch.
/// </para>
/// </remarks>
internal static class StatementPlanQueries
{
    /// <summary>The schemas on the reading role's effective <c>search_path</c>, implicit ones left out.</summary>
    internal const string SearchPathSql = "select pg_catalog.current_schemas(false)";

    /// <summary>Sets the transaction's <c>search_path</c>; the value is a parameter (<see cref="SearchPath" />).</summary>
    internal const string SetSearchPathSql = "select pg_catalog.set_config('search_path', @path, true)";

    /// <summary>What "Show SQL" prints in place of the recorded text.</summary>
    internal const string StatementShown = "<the text pg_stat_statements recorded for queryid ";

    /// <summary>
    /// A <c>search_path</c> value naming <paramref name="schemas" />, each quoted, in order - or <c>pg_catalog</c> alone when
    /// there is none. A name <see cref="SqlIdentifier.Quote" /> refuses is left out: a shorter path only makes a name fail to
    /// resolve, never resolve somewhere else.
    /// </summary>
    public static string SearchPath(IEnumerable<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        List<string> quoted = [];

        foreach (string schema in schemas)
        {
            if (!string.IsNullOrWhiteSpace(schema) && !schema.Contains('"', StringComparison.Ordinal) && !schema.Contains('\0', StringComparison.Ordinal))
            {
                quoted.Add(SqlIdentifier.Quote(schema));
            }
        }

        return quoted.Count == 0 ? "pg_catalog" : string.Join(", ", quoted);
    }

    /// <summary>
    /// The generic plan batch for <paramref name="text" />, or <see langword="null" /> when
    /// <see cref="HealthStatementQueries.Refusal" /> refuses it.
    /// </summary>
    /// <param name="text">The text, as the statement sweep read it.</param>
    /// <param name="truncated">Whether that text was cut.</param>
    public static string? GenericPlan(string text, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(text);

        return HealthStatementQueries.Refusal(text, truncated) != StatementPlanRefusal.None ? null : Batch(text, HealthStatementQueries.HighestParameter(text));
    }

    /// <summary>The batch as "Show SQL" prints it: its shape, and where the text came from - never the text.</summary>
    public static string Shown(long queryId, int parameters) =>
        Batch(StatementShown + queryId.ToString(CultureInfo.InvariantCulture) + ", shown masked above>", parameters);

    /// <summary>Takes the plan's prepared statement back, for a batch that stopped before it did.</summary>
    public static string Deallocate() => "deallocate " + SqlIdentifier.Quote(HealthStatementQueries.PlanStatementName);

    /// <summary>The schemas on the effective <c>search_path</c> of the transaction's role (<see cref="SearchPathSql" />).</summary>
    public static async Task<IReadOnlyList<string>> ReadSearchPathAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(SearchPathSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string[] schemas ? schemas : [];
    }

    /// <summary>Sets the transaction's <c>search_path</c> to <paramref name="path" /> (<see cref="SearchPath" />), as a parameter.</summary>
    public static async Task SetSearchPathAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string path,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(SetSearchPathSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter("path", NpgsqlDbType.Text) { Value = path });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="batch" /> (<see cref="GenericPlan" />) and reads its plan: the first row's one <c>json</c> value,
    /// whole when it is at most <paramref name="maxCharacters" /> long - or its length, when it is longer.
    /// </summary>
    public static async Task<(string? Json, long? Characters)> ReadPlanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string batch,
        int maxCharacters,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch);

        await using NpgsqlCommand command = new(batch, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        do
        {
            if (reader.FieldCount == 1 && await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                string json = reader.GetString(0);
                return json.Length <= maxCharacters ? (json, null) : (null, json.Length);
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));

        return (null, null);
    }

    /// <summary>Runs <see cref="Deallocate" /> on <paramref name="connection" />, in <paramref name="transaction" />.</summary>
    public static async Task DeallocateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(Deallocate(), connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Batch(string statement, int parameters)
    {
        string prepared = SqlIdentifier.Quote(HealthStatementQueries.PlanStatementName);
        string arguments = parameters == 0 ? string.Empty : "(" + string.Join(", ", Enumerable.Repeat("null", parameters)) + ")";

        // The ';' after the text is on a line of its own: a text that ends in a '--' comment would otherwise swallow it.
        return HealthStatementQueries.PlanPrefix + prepared + " as " + statement + "\n;\n"
            + "set local plan_cache_mode = force_generic_plan;\n"
            + "explain (verbose, format json) execute " + prepared + arguments + ";\n"
            + "deallocate " + prepared;
    }
}
