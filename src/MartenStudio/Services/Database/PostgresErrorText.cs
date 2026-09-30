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
/// <b>Qualified names first, then every whole token.</b> A <c>schema.</c> qualifier is masked as free text is
/// (<see cref="WithheldNames.RedactText" />): outside the double quotes Postgres puts round a name, and inside
/// them. Then every whole token that spells a withheld schema's name is masked wherever it stands, whether or
/// not the word <c>schema</c> comes before it - because that word is only English: a server whose
/// <c>lc_messages</c> is French says <c>droit refusé pour le schéma hr</c>, and a message may print a name with
/// a space or a hyphen in it unquoted (<c>relation "HR Data.salaries" does not exist</c>), which no lexer
/// reads as one identifier. A token is the withheld name as a whole, bounded on both sides by something that
/// cannot continue an identifier - so <c>hr</c> is masked in <c>schema hr</c> and in <c>"hr.x"</c>, and not in
/// <c>hr_public</c>, <c>thr</c> or <c>hr-data</c>, whose hyphen joins it to the next word as a name's own
/// hyphen would. A name with a space or a hyphen in it is matched whole, spaces and hyphens included. This is
/// error text only: it errs towards masking - a word in a message that happens to spell a withheld schema's
/// name is masked as well, which costs a little readability and leaks nothing - and definitions keep the
/// qualifier rule, where a word is a word.
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

    /// <summary>Postgres' own words: qualifiers, then every whole token that spells a withheld schema.</summary>
    private static string Mask(string text, IReadOnlySet<string> withheld) =>
        text.Length == 0 ? text : MaskTokens(WithheldNames.RedactText(text, withheld) ?? text, withheld);

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
    /// Every whole token of <paramref name="text" /> that spells a withheld schema's name, masked - after the
    /// word <c>schema</c>, in another language's word for it, in double quotes or bare. Compared ignoring case,
    /// as a folded bare name would be; a name with a double quote in it is also looked for as Postgres prints it
    /// inside double quotes, with that quote doubled. The mask Redact has already written is passed over whole.
    /// </summary>
    private static string MaskTokens(string text, IReadOnlySet<string> withheld)
    {
        // Longest first, so a schema whose name begins with another's - "hr_archive" and "hr", "HR Data" and
        // "HR" - is masked whole rather than cut in two.
        string[] names =
        [
            .. withheld
                .Where(static x => x.Length > 0)
                .SelectMany(static x => x.Contains('"', StringComparison.Ordinal)
                    ? new[] { x, x.Replace("\"", "\"\"", StringComparison.Ordinal) }
                    : new[] { x })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static x => x.Length),
        ];

        if (names.Length == 0 || !names.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase)))
        {
            return text;
        }

        System.Text.StringBuilder output = new(text.Length);
        int copied = 0;
        int i = 0;

        while (i < text.Length)
        {
            if (string.CompareOrdinal(text, i, WithheldNames.Token, 0, WithheldNames.Token.Length) == 0)
            {
                i += WithheldNames.Token.Length;
                continue;
            }

            string? matched = StartsToken(text, i) ? MatchAt(text, i, names) : null;

            if (matched is null)
            {
                i++;
                continue;
            }

            output.Append(text, copied, i - copied);
            output.Append(WithheldNames.Token);
            i += matched.Length;
            copied = i;
        }

        if (copied == 0)
        {
            return text;
        }

        output.Append(text, copied, text.Length - copied);
        return output.ToString();
    }

    /// <summary>The longest of <paramref name="names" /> that stands whole at <paramref name="index" />, or <see langword="null" />.</summary>
    private static string? MatchAt(string text, int index, string[] names)
    {
        foreach (string name in names)
        {
            if (index + name.Length <= text.Length
                && string.Compare(text, index, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                && EndsToken(text, index + name.Length))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a token may start at <paramref name="index" />: nothing before it that would make it the tail of a
    /// longer name - a letter, a digit, <c>_</c>, <c>$</c>, or a hyphen joined to one.
    /// </summary>
    private static bool StartsToken(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        char before = text[index - 1];

        if (before == '-')
        {
            return index < 2 || !IsNameCharacter(text[index - 2]);
        }

        return !IsNameCharacter(before);
    }

    /// <summary>Whether a token may end at <paramref name="index" />: the end, or nothing that continues a name.</summary>
    private static bool EndsToken(string text, int index)
    {
        if (index >= text.Length)
        {
            return true;
        }

        char after = text[index];

        if (after == '-')
        {
            return index + 1 >= text.Length || !IsNameCharacter(text[index + 1]);
        }

        return !IsNameCharacter(after);
    }

    /// <summary>A character that continues a name as Postgres spells one bare: a letter, a digit, <c>_</c> or <c>$</c>.</summary>
    private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}
