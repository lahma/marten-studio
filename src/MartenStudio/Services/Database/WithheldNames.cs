using System.Text;

namespace MartenStudio.Services.Database;

/// <summary>
/// Masks the name of every schema a visitor may not see wherever Postgres printed it: in a column's type
/// and default, a constraint or an index definition, a view's query, a routine's signature and body, a
/// trigger's definition, a <c>search_path</c> setting, a comment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why text has to be masked at all.</b> The gate withholds a schema by never listing it and blanking
/// it wherever a structured field would name it (a foreign key's far end, a trigger's function). But
/// Postgres hands the studio a great deal of <em>free text</em> - <c>format_type</c>, <c>pg_get_expr</c>,
/// <c>pg_get_constraintdef</c>, <c>pg_get_indexdef</c>, <c>pg_get_viewdef</c>, <c>pg_get_functiondef</c>,
/// <c>pg_get_triggerdef</c>, <c>proconfig</c> - and any of it can say <c>nextval('hr.seq'::regclass)</c>,
/// <c>CHECK (hr.is_ok(c))</c> or <c>SETOF hr.salaries</c>. Every such <c>schema.</c> qualifier naming a
/// withheld schema becomes <see cref="Token" />.
/// </para>
/// <para>
/// <b>Why it can be done reliably.</b> Postgres' deparsers qualify a name only when the
/// <c>search_path</c> would not find it, and every catalog transaction pins the path to <c>pg_catalog</c>
/// first (<c>DatabaseCatalogQueries.PinSearchPathSql</c>) - so everything outside <c>pg_catalog</c> is
/// printed <em>with</em> its schema, and a withheld name cannot hide as an unqualified one. What is left is
/// finding the qualifiers, which is lexing, not parsing: an identifier - bare, or double-quoted with
/// <c>""</c> as an escaped quote - followed by a dot. The walk knows where SQL's literals, comments and
/// dollar-quoted bodies are, as <c>SqlLexer</c> does, because a quote inside one of those must not be
/// taken for the start of an identifier that swallows the rest of the text - and then it looks
/// <em>inside</em> them too, because <c>'hr.seq'::regclass</c> names a schema in a string literal, and a
/// function body names them in a dollar-quoted one. A literal that is <em>exactly</em> a withheld schema's
/// name is masked whole, because that is how <c>pg_get_functiondef</c> prints a routine's
/// <c>SET search_path TO 'hr', 'public'</c> and how a <c>regnamespace</c> constant is printed. It errs towards
/// masking: a table alias, a word in a comment or a string that happens to spell a withheld schema's name
/// is masked as well, which costs a little readability and leaks nothing.
/// </para>
/// <para>
/// <b>What it cannot do</b>, and what D27 says about it: a routine's body is the host's own source text,
/// and a name in it that relies on the routine's <c>SET search_path</c> - or that dynamic SQL assembles from
/// pieces - is not a qualifier and cannot be attributed to a schema. The schema's name is still masked in the
/// <c>search_path</c> setting itself; the unqualified object name is shown as written.
/// </para>
/// </remarks>
internal static class WithheldNames
{
    /// <summary>What a withheld schema's name is replaced with. Not an identifier, on purpose.</summary>
    public const string Token = "‹withheld›";

    /// <summary>How deep a dollar-quoted body inside a dollar-quoted body is still read as SQL.</summary>
    private const int MaxNesting = 4;

    /// <summary>
    /// <paramref name="text" /> - SQL, or a fragment of it - with every qualifier naming one of
    /// <paramref name="withheld" /> masked. <see langword="null" /> stays <see langword="null" />.
    /// </summary>
    public static string? Redact(string? text, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(withheld);

        if (string.IsNullOrEmpty(text) || withheld.Count == 0 || !MightName(text, withheld))
        {
            return text;
        }

        StringBuilder output = new(text.Length);
        Sql(text, 0, text.Length, withheld, output, 0);
        return output.ToString();
    }

    /// <summary>
    /// Free text - a comment, a setting's value - with every qualifier naming one of
    /// <paramref name="withheld" /> masked, and no SQL structure assumed.
    /// </summary>
    public static string? RedactText(string? text, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(withheld);

        if (string.IsNullOrEmpty(text) || withheld.Count == 0 || !MightName(text, withheld))
        {
            return text;
        }

        StringBuilder output = new(text.Length);
        Loose(text, 0, text.Length, withheld, output);
        return output.ToString();
    }

    /// <summary>
    /// A routine's <c>proconfig</c> (<c>name=value</c> entries): the schemas of a <c>search_path</c> entry
    /// masked item by item - they are bare names there, with no dot after them - and every other entry
    /// masked as free text.
    /// </summary>
    public static IReadOnlyList<string> RedactConfig(IReadOnlyList<string> config, IReadOnlySet<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(withheld);

        if (withheld.Count == 0 || config.Count == 0)
        {
            return config;
        }

        string[] redacted = new string[config.Count];

        for (int i = 0; i < config.Count; i++)
        {
            string entry = config[i] ?? string.Empty;
            int equals = entry.IndexOf('=', StringComparison.Ordinal);

            redacted[i] = equals > 0
                && string.Equals(entry[..equals].Trim(), "search_path", StringComparison.OrdinalIgnoreCase)
                    ? entry[..(equals + 1)] + RedactSearchPath(entry[(equals + 1)..], withheld)
                    : RedactText(entry, withheld) ?? entry;
        }

        return redacted;
    }

    /// <summary>Whether <paramref name="name" /> - as it would be compared - is withheld.</summary>
    /// <remarks>
    /// A bare identifier is folded to lower case the way Postgres folds it; the fold is tried both the
    /// ASCII way (Postgres' own, in UTF-8) and the invariant way, and the name as written too, so every
    /// reading that could name a withheld schema is masked.
    /// </remarks>
    private static bool IsWithheld(string name, bool quoted, IReadOnlySet<string> withheld) =>
        withheld.Contains(name)
        || (!quoted && (withheld.Contains(AsciiLower(name)) || withheld.Contains(name.ToLowerInvariant())));

    private static string AsciiLower(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            buffer[i] = c is >= 'A' and <= 'Z' ? (char) (c + 32) : c;
        }

        return new string(buffer);
    }

    /// <summary>
    /// A cheap pre-check: whether any withheld name occurs in the text at all, case-insensitively. Most
    /// text names no withheld schema, and is returned as it is.
    /// </summary>
    private static bool MightName(string text, IReadOnlySet<string> withheld)
    {
        foreach (string name in withheld)
        {
            if (name.Length == 0)
            {
                continue;
            }

            // A name with a double quote in it is printed with that quote doubled.
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (name.Contains('"', StringComparison.Ordinal)
                    && text.Contains(name.Replace("\"", "\"\"", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Walks <c>[start, end)</c> as SQL: literals, comments, bodies, identifiers.</summary>
    private static void Sql(string text, int start, int end, IReadOnlySet<string> withheld, StringBuilder output, int nesting)
    {
        int i = start;

        while (i < end)
        {
            char c = text[i];

            if (c == '-' && i + 1 < end && text[i + 1] == '-')
            {
                // A line comment ends at a line feed or a carriage return, as Postgres' own lexer has it.
                int stop = i + 2;
                while (stop < end && text[stop] is not ('\n' or '\r'))
                {
                    stop++;
                }

                output.Append("--");
                Loose(text, i + 2, stop, withheld, output);
                i = stop;
                continue;
            }

            if (c == '/' && i + 1 < end && text[i + 1] == '*')
            {
                int stop = BlockCommentEnd(text, i, end);
                bool closed = stop <= end && stop - 2 >= i + 2 && text[stop - 2] == '*' && text[stop - 1] == '/';

                output.Append("/*");
                Loose(text, i + 2, closed ? stop - 2 : stop, withheld, output);
                if (closed)
                {
                    output.Append("*/");
                }

                i = stop;
                continue;
            }

            if (c == '\'')
            {
                bool escapes = IsEscapeStringPrefix(text, i, start);
                int stop = StringEnd(text, i, end, escapes);
                bool closed = stop > i + 1 && stop <= end && text[stop - 1] == '\'';

                output.Append('\'');

                // A literal that is exactly a schema's name is how Postgres prints one on its own: each item
                // of a routine's SET search_path TO 'hr', 'public', and a regnamespace constant.
                if (closed && IsWithheld(text[(i + 1)..(stop - 1)].Replace("''", "'", StringComparison.Ordinal), quoted: false, withheld))
                {
                    output.Append(Token);
                }
                else
                {
                    Loose(text, i + 1, closed ? stop - 1 : stop, withheld, output);
                }

                if (closed)
                {
                    output.Append('\'');
                }

                i = stop;
                continue;
            }

            if (c == '$' && !IsInsideIdentifier(text, i, start) && TryReadDollarTag(text, i, end, out string? tag))
            {
                int body = i + tag!.Length;
                int closing = text.IndexOf(tag, body, end - body, StringComparison.Ordinal);

                output.Append(tag);

                if (closing < 0)
                {
                    Loose(text, body, end, withheld, output);
                    i = end;
                    continue;
                }

                if (nesting < MaxNesting)
                {
                    Sql(text, body, closing, withheld, output, nesting + 1);
                }
                else
                {
                    Loose(text, body, closing, withheld, output);
                }

                output.Append(tag);
                i = closing + tag.Length;
                continue;
            }

            if (c == '"')
            {
                int stop = QuotedIdentifierEnd(text, i, end);

                if (stop < 0)
                {
                    // Never closed: nothing after it can be told apart, so all of it is read as free text.
                    Loose(text, i, end, withheld, output);
                    i = end;
                    continue;
                }

                string name = text[(i + 1)..(stop - 1)].Replace("\"\"", "\"", StringComparison.Ordinal);

                output.Append(IsQualifier(text, stop, end) && IsWithheld(name, quoted: true, withheld)
                    ? Token
                    : text[i..stop]);

                i = stop;
                continue;
            }

            if (IsIdentifierStart(c) && !(i > start && IsIdentifierContinuation(text[i - 1])))
            {
                int stop = IdentifierEnd(text, i, end);
                string name = text[i..stop];

                output.Append(IsQualifier(text, stop, end) && IsWithheld(name, quoted: false, withheld) ? Token : name);

                i = stop;
                continue;
            }

            output.Append(c);
            i++;
        }
    }

    /// <summary>
    /// Walks <c>[start, end)</c> as free text - the inside of a literal, a comment, a body that never
    /// closed - masking identifiers followed by a dot, and assuming nothing else about it.
    /// </summary>
    /// <remarks>
    /// A double quote is only an identifier here when it closes before <paramref name="end" /> and names a
    /// withheld schema directly in front of a dot; otherwise it is copied and the walk goes on
    /// <em>inside</em> it, so that an unmatched quote in a comment can never swallow a qualifier after it.
    /// </remarks>
    private static void Loose(string text, int start, int end, IReadOnlySet<string> withheld, StringBuilder output)
    {
        int i = start;

        while (i < end)
        {
            char c = text[i];

            if (c == '"')
            {
                int stop = QuotedIdentifierEnd(text, i, end);

                if (stop > 0
                    && IsQualifier(text, stop, end)
                    && IsWithheld(text[(i + 1)..(stop - 1)].Replace("\"\"", "\"", StringComparison.Ordinal), quoted: true, withheld))
                {
                    output.Append(Token);
                    i = stop;
                    continue;
                }

                output.Append(c);
                i++;
                continue;
            }

            if (IsIdentifierStart(c) && !(i > start && IsIdentifierContinuation(text[i - 1])))
            {
                int stop = IdentifierEnd(text, i, end);
                string name = text[i..stop];

                output.Append(IsQualifier(text, stop, end) && IsWithheld(name, quoted: false, withheld) ? Token : name);

                i = stop;
                continue;
            }

            output.Append(c);
            i++;
        }
    }

    /// <summary>A <c>search_path</c> value, item by item: <c>hr, "Legal", public</c>.</summary>
    private static string RedactSearchPath(string value, IReadOnlySet<string> withheld)
    {
        List<string> items = [];
        StringBuilder current = new();
        bool inQuotes = false;

        foreach (char c in value)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }

            if (c == ',' && !inQuotes)
            {
                items.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        items.Add(current.ToString());

        for (int i = 0; i < items.Count; i++)
        {
            string item = items[i];
            string trimmed = item.Trim();

            bool quoted = trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"';
            string name = quoted ? trimmed[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : trimmed;

            if (name.Length > 0 && IsWithheld(name, quoted, withheld))
            {
                int lead = item.Length - item.TrimStart().Length;
                items[i] = item[..lead] + Token;
            }
        }

        return string.Join(',', items);
    }

    /// <summary>Whether a dot - after optional whitespace - follows <paramref name="position" />.</summary>
    private static bool IsQualifier(string text, int position, int end)
    {
        int i = position;
        while (i < end && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i < end && text[i] == '.';
    }

    /// <summary>One past a <c>"…"</c> identifier's closing quote, or -1 when it never closes before <paramref name="end" />.</summary>
    private static int QuotedIdentifierEnd(string text, int open, int end)
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

    /// <summary>One past a <c>'…'</c> literal's closing quote, or <paramref name="end" /> when it never closes.</summary>
    private static int StringEnd(string text, int open, int end, bool escapes)
    {
        int i = open + 1;

        while (i < end)
        {
            if (escapes && text[i] == '\\' && i + 1 < end)
            {
                i += 2;
                continue;
            }

            if (text[i] == '\'')
            {
                if (i + 1 < end && text[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return end;
    }

    /// <summary>One past a <c>/* … */</c> comment's close, nesting counted, or <paramref name="end" />.</summary>
    private static int BlockCommentEnd(string text, int open, int end)
    {
        int nesting = 0;
        int i = open;

        while (i < end)
        {
            if (text[i] == '/' && i + 1 < end && text[i + 1] == '*')
            {
                nesting++;
                i += 2;
                continue;
            }

            if (text[i] == '*' && i + 1 < end && text[i + 1] == '/')
            {
                nesting--;
                i += 2;

                if (nesting == 0)
                {
                    return i;
                }

                continue;
            }

            i++;
        }

        return end;
    }

    /// <summary>Whether the quote at <paramref name="quote" /> opens an <c>E'…'</c> literal.</summary>
    private static bool IsEscapeStringPrefix(string text, int quote, int start)
    {
        if (quote <= start || text[quote - 1] is not ('e' or 'E'))
        {
            return false;
        }

        int before = quote - 2;
        return before < start || !IsIdentifierContinuation(text[before]);
    }

    /// <summary>Whether an unquoted identifier is already running at <paramref name="position" />.</summary>
    private static bool IsInsideIdentifier(string text, int position, int start)
    {
        int first = position;

        while (first > start && IsIdentifierContinuation(text[first - 1]))
        {
            first--;
        }

        return first < position && IsIdentifierStart(text[first]);
    }

    /// <summary>Reads the <c>$tag$</c> that opens a dollar-quoted body at <paramref name="position" />, if one does.</summary>
    private static bool TryReadDollarTag(string text, int position, int end, out string? tag)
    {
        int probe = position + 1;

        if (probe < end && text[probe] != '$')
        {
            if (!char.IsLetter(text[probe]) && text[probe] != '_')
            {
                tag = null;
                return false;
            }

            probe++;

            while (probe < end && (char.IsLetterOrDigit(text[probe]) || text[probe] == '_'))
            {
                probe++;
            }
        }

        if (probe < end && text[probe] == '$')
        {
            tag = text[position..(probe + 1)];
            return true;
        }

        tag = null;
        return false;
    }

    private static int IdentifierEnd(string text, int start, int end)
    {
        int i = start + 1;

        while (i < end && IsIdentifierContinuation(text[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>Postgres' <c>ident_start</c>: a letter, an underscore, or a high character.</summary>
    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c >= '\u0080';

    /// <summary>Postgres' <c>ident_cont</c>: <c>ident_start</c>, a digit, or a dollar sign.</summary>
    private static bool IsIdentifierContinuation(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c >= '\u0080';
}
