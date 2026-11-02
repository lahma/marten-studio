using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Statements;

/// <summary>
/// 1.0-HARD (the Statements drill-in's 0.6 known limit): whether a text names a view or a SQL function, in a schema the visitor may
/// see, through which the planner would expand or inline something of a schema they may not - so its plan is never prepared.
/// </summary>
/// <remarks>
/// <para>
/// <b>What crosses.</b> A view or SQL-standard-body function that reaches, through <c>pg_depend</c>, a withheld schema's view or
/// function (<see cref="StatementPlanCrossingQueries" />); a <c>LANGUAGE sql</c> function with a string body, which records no
/// dependencies, whose body names a withheld schema (<see cref="PostgresErrorText.Redact" />, as the text's own check) or names
/// something that crosses; and a view or function that reaches anything that crosses. Resolved to a fixed point, bounded.
/// </para>
/// <para>
/// <b>Matched by name, over-approximating.</b> The text is matched against the crossing objects' names as whole words, ignoring
/// case and wherever they stand - inside a literal, a comment or a quoted name too - so a text that only mentions such a name is
/// refused as well. That costs a plan, never a leak. A walk or a read cut at its bound refuses every text: it cannot tell.
/// </para>
/// </remarks>
internal static class StatementCrossing
{
    /// <summary>The most rounds of the fixed point; every crossing chain this short is found.</summary>
    internal const int MaxRounds = 10;

    /// <summary>
    /// Whether <paramref name="text" /> names something that crosses into a withheld schema, read in the plan's own transaction
    /// before anything is prepared.
    /// </summary>
    public static async Task<bool> ReachesWithheldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string text,
        IReadOnlySet<string> withheld,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(withheld);

        if (withheld.Count == 0)
        {
            return false;
        }

        (IReadOnlyList<CrossingEdge> edges, bool edgesCut) = await StatementPlanCrossingQueries
            .ReadEdgesAsync(connection, transaction, withheld, commandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        (IReadOnlyList<SqlFunctionSource> functions, bool functionsCut) = await StatementPlanCrossingQueries
            .ReadSqlFunctionSourcesAsync(connection, transaction, withheld, commandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        return edgesCut || functionsCut || NamesAny(text, Crossing(edges, functions, withheld));
    }

    /// <summary>The names of every visible view and function through which a plan would reach a withheld schema. Pure.</summary>
    internal static IReadOnlySet<string> Crossing(IReadOnlyList<CrossingEdge> edges, IReadOnlyList<SqlFunctionSource> functions, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(functions);
        ArgumentNullException.ThrowIfNull(withheld);

        HashSet<string> crossing = new(StringComparer.OrdinalIgnoreCase);

        foreach (CrossingEdge edge in edges)
        {
            if (edge.Withheld && edge.Expands)
            {
                crossing.Add(edge.Root);
            }
        }

        foreach (SqlFunctionSource function in functions)
        {
            if (!string.Equals(PostgresErrorText.Redact(function.Source, withheld), function.Source, StringComparison.Ordinal))
            {
                crossing.Add(function.Name);
            }
        }

        for (int round = 0; round < MaxRounds; round++)
        {
            bool grew = false;

            foreach (SqlFunctionSource function in functions)
            {
                if (!crossing.Contains(function.Name) && NamesAny(function.Source, crossing))
                {
                    grew |= crossing.Add(function.Name);
                }
            }

            foreach (CrossingEdge edge in edges)
            {
                if (!edge.Withheld && crossing.Contains(edge.Reached) && !crossing.Contains(edge.Root))
                {
                    grew |= crossing.Add(edge.Root);
                }
            }

            if (!grew)
            {
                break;
            }
        }

        return crossing;
    }

    /// <summary>Whether <paramref name="text" /> has any of <paramref name="names" /> as a whole word, ignoring case, anywhere. Pure.</summary>
    internal static bool NamesAny(string text, IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);

        if (names.Count == 0)
        {
            return false;
        }

        // A name that is not one word (a quoted name with a space or a dot in it) is looked for as it is spelled, anywhere.
        foreach (string name in names)
        {
            if (!name.All(IsWordCharacter) && text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        int at = 0;

        while (at < text.Length)
        {
            if (!IsWordCharacter(text[at]))
            {
                at++;
                continue;
            }

            int start = at;

            while (at < text.Length && IsWordCharacter(text[at]))
            {
                at++;
            }

            if (names.Contains(text[start..at]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A character of a Postgres identifier: a letter, a digit, an underscore, a dollar sign or a high character.</summary>
    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c >= '\u0080';
}
