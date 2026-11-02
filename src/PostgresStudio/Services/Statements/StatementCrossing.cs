using System.Text;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Statements;

/// <summary>The names through which a plan would reach a withheld schema, and whether the walk that found them was complete.</summary>
/// <param name="Names">Every visible view, function, operator symbol and table the walk found to cross.</param>
/// <param name="Converged">
/// Whether the last round of the fixed point added nothing. <see langword="false" /> means the bound was reached while the set was
/// still growing: the caller cannot tell, and refuses.
/// </param>
internal readonly record struct CrossingResult(IReadOnlySet<string> Names, bool Converged);

/// <summary>
/// 1.0-HARD, widened by RVH-FIX: whether a text names a view, a function, an operator or a table, in a schema the visitor may
/// see, through which the planner would expand or inline something of a schema they may not - so its plan is never prepared.
/// </summary>
/// <remarks>
/// <para>
/// <b>What crosses.</b> An object in a visible schema whose definition directly depends, through <c>pg_depend</c>, on a withheld
/// schema's view, function or operator (<see cref="StatementPlanCrossingQueries" />): a view's query, a function's SQL-standard
/// body and every parameter default, an operator's function, a table's row-level-security policies and (PostgreSQL 18) virtual
/// generated columns; a <c>LANGUAGE sql</c> function with a string body, which records no dependencies, whose body names a
/// withheld schema (<see cref="PostgresErrorText.Redact" />, as the text's own check) or names something that crosses; and
/// whatever reaches anything that crosses. Resolved to a fixed point of at most <see cref="MaxRounds" /> rounds; a set still
/// growing in the last round is not an answer, and the text is refused.
/// </para>
/// <para>
/// <b>Matched by name, over-approximating.</b> The text is matched against the crossing objects' names as whole identifiers,
/// ignoring case and wherever they stand - inside a literal, a comment or a quoted name too - so a text that only mentions such a
/// name is refused as well. That costs a plan, never a leak. Names are compared as Postgres keeps them: a quoted identifier's
/// <c>""</c> is one <c>"</c>, and an identifier is cut to 63 bytes of UTF-8 (NAMEDATALEN - 1) before it is compared. An operator
/// is a symbol, found as spelled anywhere in the text. A walk or a read cut at its bound refuses every text: it cannot tell.
/// </para>
/// <para>
/// <b>What the walk cannot see</b> (and so what remains): what the catalog does not record and the text does not spell. A body in
/// another language than SQL is never inlined, so it adds nothing to a plan and needs no walk; but an extension's planner hook or a
/// function's support function (C code) can put into a plan what no catalog row says, and a database that does so is outside this
/// read.
/// </para>
/// </remarks>
internal static class StatementCrossing
{
    /// <summary>The most rounds of the fixed point; every crossing chain this short is found, and a longer one refuses.</summary>
    internal const int MaxRounds = 32;

    /// <summary>NAMEDATALEN - 1: the most bytes of UTF-8 Postgres keeps of an identifier.</summary>
    internal const int MaxIdentifierBytes = 63;

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

        if (edgesCut || functionsCut)
        {
            return true;
        }

        CrossingResult crossing = Resolve(edges, functions, withheld);

        // Fail closed: a set still growing when the rounds ran out is not a set.
        return !crossing.Converged || NamesAny(text, crossing.Names);
    }

    /// <summary>The names of every visible object through which a plan would reach a withheld schema. Pure.</summary>
    /// <param name="edges">The catalog's direct dependencies.</param>
    /// <param name="functions">The string-bodied SQL functions in visible schemas.</param>
    /// <param name="withheld">The schemas the visitor may not see.</param>
    /// <param name="maxRounds">The bound of the fixed point; a test shortens it.</param>
    internal static CrossingResult Resolve(
        IReadOnlyList<CrossingEdge> edges,
        IReadOnlyList<SqlFunctionSource> functions,
        IReadOnlySet<string> withheld,
        int maxRounds = MaxRounds)
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

        // Each body is read once; a round then only looks its names up.
        List<(string Name, string Source, HashSet<string> Words)> bodies = new(functions.Count);

        foreach (SqlFunctionSource function in functions)
        {
            if (!string.Equals(PostgresErrorText.Redact(function.Source, withheld), function.Source, StringComparison.Ordinal))
            {
                crossing.Add(function.Name);
            }
            else
            {
                bodies.Add((function.Name, function.Source, Words(function.Source)));
            }
        }

        bool converged = false;

        for (int round = 0; round < maxRounds; round++)
        {
            bool grew = false;
            List<string> symbols = [.. crossing.Where(static x => !x.All(IsWordCharacter))];

            foreach ((string name, string source, HashSet<string> words) in bodies)
            {
                if (!crossing.Contains(name) && (Spells(words, crossing) || symbols.Exists(x => source.Contains(x, StringComparison.OrdinalIgnoreCase))))
                {
                    grew |= crossing.Add(name);
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
                converged = true;
                break;
            }
        }

        return new CrossingResult(crossing, converged);
    }

    /// <summary>
    /// Whether <paramref name="text" /> has any of <paramref name="names" /> as an identifier, ignoring case, anywhere (<see
    /// cref="Words" />); and, for a name that is not one word - an operator's symbol, a quoted name with a space in it - as
    /// spelled. Pure.
    /// </summary>
    internal static bool NamesAny(string text, IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(names);

        if (names.Count == 0)
        {
            return false;
        }

        foreach (string name in names)
        {
            if (!name.All(IsWordCharacter) && text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return Spells(Words(text), names);
    }

    /// <summary>Whether any of <paramref name="words" /> is one of <paramref name="names" />: looked up one by one, so the cost is the text's.</summary>
    private static bool Spells(HashSet<string> words, IReadOnlySet<string> names)
    {
        foreach (string word in words)
        {
            if (names.Contains(word))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every identifier a text spells, as Postgres keeps it, ignoring case: each run of identifier characters, and each double-quoted
    /// identifier with its <c>""</c> read as one <c>"</c> - and the words inside a quoted one too, because the walk does not tell a
    /// literal or a comment from SQL and a quote inside either must not hide what follows. Every identifier is cut to <see
    /// cref="MaxIdentifierBytes" /> bytes of UTF-8, as Postgres cuts it. Pure.
    /// </summary>
    internal static HashSet<string> Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
        Scan(text, 0, text.Length, words);
        return words;
    }

    private static void Scan(string text, int start, int end, HashSet<string> words)
    {
        int at = start;

        while (at < end)
        {
            char c = text[at];

            if (c == '"')
            {
                int stop = QuotedEnd(text, at, end);

                if (stop > 0)
                {
                    words.Add(Cut(text[(at + 1)..(stop - 1)].Replace("\"\"", "\"", StringComparison.Ordinal)));
                    Scan(text, at + 1, stop - 1, words);
                    at = stop;
                    continue;
                }

                at++;
                continue;
            }

            if (!IsWordCharacter(c))
            {
                at++;
                continue;
            }

            int first = at;

            while (at < end && IsWordCharacter(text[at]))
            {
                at++;
            }

            words.Add(Cut(text[first..at]));
        }
    }

    /// <summary>One past a <c>"…"</c> identifier's closing quote (<c>""</c> is a quote inside it), or -1 when it never closes.</summary>
    private static int QuotedEnd(string text, int open, int end)
    {
        int i = open + 1;

        while (i < end)
        {
            if (text[i] == '"')
            {
                if (i + 1 < end && text[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return -1;
    }

    /// <summary><paramref name="name" /> cut to <see cref="MaxIdentifierBytes" /> bytes of UTF-8 at a character boundary, as Postgres' <c>truncate_identifier</c> does.</summary>
    internal static string Cut(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Length * 3 <= MaxIdentifierBytes || Encoding.UTF8.GetByteCount(name) <= MaxIdentifierBytes)
        {
            return name;
        }

        int bytes = 0;
        int at = 0;

        while (at < name.Length)
        {
            int length = char.IsHighSurrogate(name[at]) && at + 1 < name.Length && char.IsLowSurrogate(name[at + 1]) ? 2 : 1;
            int size = Encoding.UTF8.GetByteCount(name.AsSpan(at, length));

            if (bytes + size > MaxIdentifierBytes)
            {
                break;
            }

            bytes += size;
            at += length;
        }

        return name[..at];
    }

    /// <summary>A character of a Postgres identifier: a letter, a digit, an underscore, a dollar sign or a high character.</summary>
    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c >= '\u0080';
}
