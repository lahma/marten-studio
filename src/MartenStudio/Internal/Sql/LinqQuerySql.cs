using System.Globalization;

using Npgsql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The few pieces of SQL text LINQ mode itself contributes, all of them constants or a prefix - the rest of every
/// statement is Marten's (hard rule 4).
/// </summary>
/// <remarks>
/// <para>
/// LINQ mode writes no predicate of its own: the tenant filter, the soft-delete filter, the hierarchy's
/// <c>mt_doc_type</c> filter and every member locator are Marten's, built from the mapping. What the studio adds is
/// a total order for <c>OFFSET</c> paging (the identity column, and the tenant column across tenants - the
/// conjoined primary key) through Marten's own <c>ThenBySql</c>, and an <c>EXPLAIN</c> prefix for the plan. Neither
/// carries anything a visitor typed, and Marten's statement carries what they typed only as parameters.
/// </para>
/// </remarks>
internal static class LinqQuerySql
{
    /// <summary>
    /// The identity column of the document table Marten aliases <c>d</c> in every LINQ statement - the tie-breaker
    /// that makes an <c>OFFSET</c> page deterministic, as Marten's own diagnostics read does (9.48+).
    /// </summary>
    public const string IdTieBreaker = "d.id";

    /// <summary>The tenant column, ordered after the id when a conjoined collection is read across tenants.</summary>
    public const string TenantTieBreaker = "d.tenant_id";

    /// <summary>The plan of Marten's statement: <c>EXPLAIN (FORMAT JSON)</c>, and never <c>ANALYZE</c>.</summary>
    /// <param name="statement">Marten's statement, character for character.</param>
    public static string Explain(string statement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        return "explain (format json) " + statement;
    }

    /// <summary>
    /// A command on the studio's own connection and read-only transaction with Marten's text and a copy of each of
    /// Marten's parameters - the statement Marten built, run where the studio controls it.
    /// </summary>
    /// <param name="sql">The text: Marten's statement, or <see cref="Explain" /> of it.</param>
    /// <param name="marten">The command Marten built, whose parameters are copied - never moved.</param>
    /// <param name="connection">The studio's open connection.</param>
    /// <param name="transaction">The read-only transaction it runs in.</param>
    /// <param name="commandTimeoutSeconds">The client-side timeout, a little past the server's <c>statement_timeout</c>.</param>
    public static NpgsqlCommand Bind(
        string sql,
        NpgsqlCommand marten,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(marten);

        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };

        foreach (NpgsqlParameter parameter in marten.Parameters)
        {
            // A parameter belongs to one collection; Marten's command keeps its own, so the plan and the read can
            // each have theirs.
            command.Parameters.Add(parameter.Clone());
        }

        return command;
    }

    /// <summary>Marten's parameters, by name and type, with no value (D14).</summary>
    public static IReadOnlyList<(string Name, string Type)> Describe(NpgsqlCommand marten)
    {
        ArgumentNullException.ThrowIfNull(marten);

        List<(string Name, string Type)> described = [];

        foreach (NpgsqlParameter parameter in marten.Parameters)
        {
            string name = string.IsNullOrEmpty(parameter.ParameterName)
                ? "$" + (described.Count + 1).ToString(CultureInfo.InvariantCulture)
                : ":" + parameter.ParameterName.TrimStart(':', '@');

            string type = parameter.DataTypeName
                ?? (parameter.NpgsqlDbType == NpgsqlTypes.NpgsqlDbType.Unknown ? "unknown" : parameter.NpgsqlDbType.ToString());

            described.Add((name, type));
        }

        return described;
    }

    /// <summary>
    /// The values Marten bound, as text - what Postgres may echo back in an error, and what the error mask leaves as
    /// it was typed (D27).
    /// </summary>
    public static IReadOnlyList<string?> BoundText(NpgsqlCommand marten)
    {
        ArgumentNullException.ThrowIfNull(marten);

        List<string?> values = [];

        foreach (NpgsqlParameter parameter in marten.Parameters)
        {
            values.Add(parameter.Value switch
            {
                null or DBNull => null,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                var other => other.ToString(),
            });
        }

        return values;
    }
}
