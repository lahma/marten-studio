namespace MartenStudio.Services.Database;

/// <summary>
/// Postgres' own words about a read that failed - an error's message - with every withheld schema's name
/// masked before a page, or the audit ring, sees them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an error needs masking at all.</b> Postgres names objects in its messages the way its deparsers
/// do: <c>invalid input value for enum hr.grade: "nope"</c> for a filter on a column whose type lives in a
/// schema the visitor may not see - probed live, with the Columns tab beside it saying
/// <c>‹withheld›.grade</c> - <c>relation "hr.salaries" does not exist</c>, <c>function hr.norm(integer) does not
/// exist</c>, and, bare, <c>permission denied for schema hr</c>. Every one of those is shown on a page ("What
/// Postgres said") or folded into a reason, so each passes through here with the visitor's withheld set.
/// </para>
/// <para>
/// <b>Qualified names, and the schema a message names outright.</b> A <c>schema.</c> qualifier is masked as
/// free text is (<see cref="WithheldNames.RedactText" />): outside the double quotes Postgres puts round a
/// name, and inside them. A schema a message names on its own - after the word <c>schema</c>, quoted or not -
/// has no dot after it, so it is looked for there too. An unqualified object name is left alone: a name
/// Postgres printed without its schema names no schema.
/// </para>
/// <para>
/// <b>Not by pinning the <c>search_path</c>.</b> The catalog reads pin it to <c>pg_catalog</c> so that every
/// deparsed name carries its schema, but a row read cannot: its filter, key and cursor comparisons are written
/// as bare operators on purpose (<see cref="MartenStudio.Internal.Sql.TableRowQueryBuilder" />), so that a
/// <c>citext</c> column compares with <c>citext</c>'s own <c>=</c> - which lives in the schema the extension
/// was installed into. Under a <c>pg_catalog</c>-only path Postgres resolves <c>sender = 'X'</c> on a
/// <c>citext</c> column to <c>sender::text = 'X'::text</c>: a case-sensitive match that finds none of the
/// rows the column's own equality finds (probed on Postgres 17), and a keyset whose comparison disagreed with
/// its own <c>order by</c>. The mask does not need the pin: a name printed without its schema names no schema.
/// </para>
/// <para>
/// <b>What the visitor typed is theirs.</b> Postgres echoes a value it could not use, in double quotes:
/// <c>invalid input syntax for type integer: "hr.x"</c>. Masking inside the echo would answer a question the
/// visitor asked by typing - "is <c>hr</c> a schema you are hiding from me?" - with a yes. So every value the
/// read bound, wherever it appears in double quotes exactly as it was bound, is left as it was typed, and only
/// Postgres' own words around it are masked.
/// </para>
/// </remarks>
internal static class PostgresErrorText
{
    /// <summary>The word a message names a schema after, on its own.</summary>
    private const string SchemaWord = "schema";

    /// <summary>
    /// <paramref name="message" /> with every withheld schema's name masked, except inside the echo of a value
    /// the read itself bound. <see langword="null" /> stays <see langword="null" />.
    /// </summary>
    /// <param name="message">Postgres' (or Npgsql's) message.</param>
    /// <param name="withheld">The schemas the visitor may not see (<see cref="DatabaseGate.WithheldSchemas" />).</param>
    /// <param name="bound">The values the failed statement bound, as text - what Postgres may echo back.</param>
    public static string? Redact(string? message, IReadOnlySet<string> withheld, IEnumerable<string?>? bound = null)
    {
        ArgumentNullException.ThrowIfNull(withheld);

        if (string.IsNullOrEmpty(message) || withheld.Count == 0)
        {
            return message;
        }

        List<(int Start, int End)> echoes = Echoes(message, bound);

        if (echoes.Count == 0)
        {
            return Mask(message, withheld);
        }

        System.Text.StringBuilder output = new(message.Length);
        int position = 0;

        foreach ((int start, int end) in echoes)
        {
            output.Append(Mask(message[position..start], withheld));
            output.Append(message, start, end - start);
            position = end;
        }

        output.Append(Mask(message[position..], withheld));
        return output.ToString();
    }

    /// <summary>Postgres' own words: qualifiers, then a schema named after the word <c>schema</c>.</summary>
    private static string Mask(string text, IReadOnlySet<string> withheld) =>
        text.Length == 0 ? text : MaskNamedSchemas(WithheldNames.RedactText(text, withheld) ?? text, withheld);

    /// <summary>
    /// Every <c>"value"</c> in <paramref name="message" /> whose value is one the read bound, as sorted,
    /// non-overlapping spans.
    /// </summary>
    private static List<(int Start, int End)> Echoes(string message, IEnumerable<string?>? bound)
    {
        List<(int Start, int End)> spans = [];

        if (bound is null)
        {
            return spans;
        }

        foreach (string? value in bound.Distinct(StringComparer.Ordinal))
        {
            if (value is null)
            {
                continue;
            }

            string quoted = "\"" + value + "\"";
            int from = 0;

            while (from <= message.Length - quoted.Length)
            {
                int found = message.IndexOf(quoted, from, StringComparison.Ordinal);
                if (found < 0)
                {
                    break;
                }

                spans.Add((found, found + quoted.Length));
                from = found + 1;
            }
        }

        spans.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : right.End.CompareTo(left.End));

        List<(int Start, int End)> merged = [];

        foreach ((int start, int end) in spans)
        {
            if (merged.Count > 0 && start < merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
                continue;
            }

            merged.Add((start, end));
        }

        return merged;
    }

    /// <summary>
    /// A withheld schema a message names on its own - <c>permission denied for schema hr</c>,
    /// <c>schema "hr" does not exist</c> - masked. The name follows the word <c>schema</c> and whitespace,
    /// bare or double-quoted, and ends at the end of the text, whitespace, a quote or punctuation.
    /// </summary>
    private static string MaskNamedSchemas(string text, IReadOnlySet<string> withheld)
    {
        if (text.IndexOf(SchemaWord, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return text;
        }

        // Longest first, so a schema whose name begins with another's is never cut in two.
        string[] names = [.. withheld.Where(static x => x.Length > 0).OrderByDescending(static x => x.Length)];
        System.Text.StringBuilder output = new(text.Length);
        int position = 0;

        while (position < text.Length)
        {
            int word = text.IndexOf(SchemaWord, position, StringComparison.OrdinalIgnoreCase);
            if (word < 0)
            {
                break;
            }

            int after = word + SchemaWord.Length;
            bool wholeWord = (word == 0 || !char.IsLetterOrDigit(text[word - 1]) && text[word - 1] != '_')
                && after < text.Length && char.IsWhiteSpace(text[after]);

            if (!wholeWord)
            {
                output.Append(text, position, after - position);
                position = after;
                continue;
            }

            int nameStart = after;
            while (nameStart < text.Length && char.IsWhiteSpace(text[nameStart]))
            {
                nameStart++;
            }

            bool quoted = nameStart < text.Length && text[nameStart] == '"';
            int candidate = quoted ? nameStart + 1 : nameStart;
            string? matched = null;

            foreach (string name in names)
            {
                string spelled = quoted ? name.Replace("\"", "\"\"", StringComparison.Ordinal) : name;

                if (string.Compare(text, candidate, spelled, 0, spelled.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && candidate + spelled.Length <= text.Length
                    && EndsName(text, candidate + spelled.Length, quoted))
                {
                    matched = spelled;
                    break;
                }
            }

            output.Append(text, position, candidate - position);

            if (matched is null)
            {
                position = candidate;
                continue;
            }

            output.Append(WithheldNames.Token);
            position = candidate + matched.Length;
        }

        output.Append(text, position, text.Length - position);
        return output.ToString();
    }

    /// <summary>Whether a name ends at <paramref name="index" />: the closing quote, or a boundary after a bare name.</summary>
    private static bool EndsName(string text, int index, bool quoted)
    {
        if (quoted)
        {
            return index < text.Length && text[index] == '"';
        }

        return index == text.Length
            || char.IsWhiteSpace(text[index])
            || text[index] is '"' or '\'' or '.' or ',' or ':' or ';' or ')' or '(';
    }
}
