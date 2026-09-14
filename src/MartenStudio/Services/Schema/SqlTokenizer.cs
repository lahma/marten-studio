using System.Collections.Frozen;
using System.Collections.Immutable;

namespace MartenStudio.Services.Schema;

/// <summary>What a run of characters in a SQL block is, so the stylesheet can colour it.</summary>
internal enum SqlTokenKind
{
    /// <summary>Whitespace, an unrecognised word, or anything else with no colour of its own.</summary>
    Plain = 0,

    /// <summary>A reserved or otherwise well-known SQL word.</summary>
    Keyword,

    /// <summary>A double-quoted identifier.</summary>
    Identifier,

    /// <summary>A single-quoted literal, or a dollar-quoted body.</summary>
    String,

    /// <summary>A <c>--</c> line comment or a <c>/* */</c> block comment.</summary>
    Comment,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>Punctuation and operators.</summary>
    Operator,
}

/// <summary>One coloured run of text in a SQL block.</summary>
/// <param name="Text">The characters, exactly as they appear.</param>
/// <param name="Kind">What they are.</param>
internal readonly record struct SqlToken(string Text, SqlTokenKind Kind);

/// <summary>One line of a SQL block.</summary>
/// <param name="Number">The 1-based line number shown in the gutter.</param>
/// <param name="Tokens">The line's coloured runs, in order; concatenating their text reproduces the line.</param>
internal readonly record struct SqlLine(int Number, ImmutableArray<SqlToken> Tokens);

/// <summary>A tokenized SQL document ready to render.</summary>
/// <param name="Lines">The lines to show.</param>
/// <param name="TotalLines">How many lines the text has, which is more than <paramref name="Lines" /> when clamped.</param>
/// <param name="Truncated">Whether the line list was clamped.</param>
/// <param name="LinesTrimmed">
/// How many lines were cut short because they were longer than the per-line cap. A migration for a wide
/// table is one <c>create table</c> statement on a single line, and Marten's own function bodies arrive
/// as one long line when a server has reformatted them - a cap on lines alone does not bound the DOM.
/// </param>
internal sealed record SqlDocument(
    ImmutableArray<SqlLine> Lines,
    int TotalLines,
    bool Truncated,
    int LinesTrimmed = 0)
{
    /// <summary>Nothing to show.</summary>
    public static SqlDocument Empty { get; } = new([], 0, false);

    /// <summary>Whether any line was cut short.</summary>
    public bool HasTrimmedLines => LinesTrimmed > 0;
}

/// <summary>
/// Colours SQL on the server, with no highlighter in the browser.
/// </summary>
/// <remarks>
/// <para>
/// Hard rule 3 rules out a third-party library, and an embeddable RCL that fetches one from a CDN is
/// worse still - so the Schema screen's migration preview, function definitions and DDL script are
/// tokenized here and rendered as spans, the same approach <c>JsonPrettyPrinter</c> takes for JSON.
/// This is a <em>colouring</em> lexer and nothing more: it never decides what a statement means, and no
/// security decision anywhere in the studio is taken from its output. The read-only guard on the SQL
/// console is a transaction, not a parser (D13), and this is not even that parser.
/// </para>
/// <para>
/// Three Postgres details are handled because Marten's own DDL contains all three. Dollar quoting
/// (<c>$function$ ... $function$</c>) wraps every function body Marten installs - the jsonb helpers, the
/// immutable casts, <c>mt_quick_append_events</c> - and a lexer that did not know about it would colour
/// half a function as a string; a doubled <c>''</c> inside a literal continues the
/// literal rather than ending it; and <c>"quoted identifiers"</c> get a different colour from literals,
/// because mixing those two up is exactly the confusion that makes people write injectable SQL.
/// </para>
/// </remarks>
internal static class SqlTokenizer
{
    /// <summary>How many lines are rendered before the document is clamped.</summary>
    public const int DefaultMaxLines = 4_000;

    /// <summary>
    /// How many characters of one line are rendered before the rest of it is cut.
    /// </summary>
    /// <remarks>
    /// The line cap alone does not bound the work: Weasel writes a <c>create table</c> for a wide
    /// document as a single line, and <c>pg_get_functiondef</c> can return a whole function body on one.
    /// Four thousand lines of a megabyte each is a megabyte of spans on the circuit's render tree and a
    /// browser that stops responding, so the two caps exist together. Copy and download are unaffected -
    /// they carry the original text, which is the point of having them.
    /// </remarks>
    public const int DefaultMaxLineLength = 2_000;

    /// <summary>
    /// The words that get the keyword colour.
    /// </summary>
    /// <remarks>
    /// Deliberately a flat list rather than Postgres's reserved-word table: the point is that a person
    /// reading a migration can see the shape of it, and <c>create</c>, <c>index</c> and <c>jsonb</c>
    /// matter far more to that than whether Postgres considers a word reservable.
    /// </remarks>
    private static readonly FrozenSet<string> Keywords = new[]
    {
        "add", "all", "alter", "analyze", "and", "as", "asc", "begin", "between", "bigint", "boolean",
        "by", "cascade", "case", "cast", "char", "character", "check", "collate", "column", "commit",
        "concurrently", "constraint", "create", "cross", "current_timestamp", "database", "declare",
        "default", "deferrable", "deferred", "delete", "desc", "distinct", "do", "drop", "each", "else",
        "end", "except", "execute", "exists", "false", "for", "foreign", "from", "full", "function",
        "grant", "group", "having", "if", "immutable", "in", "index", "inner", "insert", "instead",
        "int", "integer", "intersect", "into", "is", "join", "jsonb", "key", "language", "left", "like",
        "limit", "materialized", "not", "null", "nulls", "numeric", "offset", "on", "or", "order",
        "outer", "owner", "plpgsql", "primary", "procedure", "public", "references", "refresh", "rename",
        "replace", "restrict", "return", "returning", "returns", "revoke", "right", "rollback", "row",
        "schema", "security", "select", "sequence", "set", "shared", "table", "temp", "temporary", "then",
        "to", "transaction", "trigger", "true", "type", "union", "unique", "update", "using", "uuid",
        "values", "varchar", "view", "volatile", "when", "where", "with", "without",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tokenizes <paramref name="sql" /> line by line.</summary>
    /// <param name="sql">The SQL text, or <see langword="null" />.</param>
    /// <param name="maxLines">How many lines to put in the DOM before clamping.</param>
    /// <param name="maxLineLength">How many characters of one line to put in the DOM before cutting it.</param>
    public static SqlDocument Tokenize(
        string? sql,
        int maxLines = DefaultMaxLines,
        int maxLineLength = DefaultMaxLineLength)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return SqlDocument.Empty;
        }

        var writer = new LineWriter(maxLines, maxLineLength);

        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];

            if (c is '\n' or '\r')
            {
                // \r\n counts once; a lone \r is a line break too.
                if (c == '\r' && i + 1 < sql.Length && sql[i + 1] == '\n')
                {
                    i++;
                }

                writer.EndLine();
                i++;
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                int end = sql.IndexOfAny(['\n', '\r'], i);
                end = end < 0 ? sql.Length : end;
                writer.Write(sql[i..end], SqlTokenKind.Comment);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? sql.Length : end + 2;
                writer.Write(sql[i..end], SqlTokenKind.Comment);
                i = end;
                continue;
            }

            if (c == '\'')
            {
                int end = ScanQuoted(sql, i, '\'');
                writer.Write(sql[i..end], SqlTokenKind.String);
                i = end;
                continue;
            }

            if (c == '"')
            {
                int end = ScanQuoted(sql, i, '"');
                writer.Write(sql[i..end], SqlTokenKind.Identifier);
                i = end;
                continue;
            }

            if (c == '$' && TryScanDollarQuote(sql, i, out int dollarEnd))
            {
                writer.Write(sql[i..dollarEnd], SqlTokenKind.String);
                i = dollarEnd;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                int j = i;
                while (j < sql.Length && char.IsWhiteSpace(sql[j]) && sql[j] is not ('\n' or '\r'))
                {
                    j++;
                }

                writer.Write(sql[i..j], SqlTokenKind.Plain);
                i = j;
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                int j = i;
                while (j < sql.Length && (char.IsAsciiDigit(sql[j]) || sql[j] == '.'))
                {
                    j++;
                }

                writer.Write(sql[i..j], SqlTokenKind.Number);
                i = j;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] is '_' or '$'))
                {
                    j++;
                }

                string word = sql[i..j];
                writer.Write(word, Keywords.Contains(word) ? SqlTokenKind.Keyword : SqlTokenKind.Plain);
                i = j;
                continue;
            }

            writer.Write(sql[i..(i + 1)], SqlTokenKind.Operator);
            i++;
        }

        return writer.Complete();
    }

    /// <summary>The index just past a quoted run starting at <paramref name="start" />.</summary>
    /// <remarks>A doubled quote character escapes itself and continues the run, as Postgres defines it.</remarks>
    private static int ScanQuoted(string sql, int start, char quote)
    {
        int j = start + 1;
        while (j < sql.Length)
        {
            if (sql[j] != quote)
            {
                j++;
                continue;
            }

            if (j + 1 < sql.Length && sql[j + 1] == quote)
            {
                j += 2;
                continue;
            }

            return j + 1;
        }

        return sql.Length;
    }

    /// <summary>
    /// Reads a dollar-quoted literal starting at <paramref name="start" />.
    /// </summary>
    /// <remarks>
    /// The tag is whatever sits between the two dollar signs (<c>$$</c> is the empty tag) and the literal
    /// ends at the next occurrence of the same tag. Marten writes every function body this way, so without
    /// it the whole Functions tab would be one long string.
    /// </remarks>
    private static bool TryScanDollarQuote(string sql, int start, out int end)
    {
        end = start;

        int close = sql.IndexOf('$', start + 1);
        if (close < 0)
        {
            return false;
        }

        for (int i = start + 1; i < close; i++)
        {
            char c = sql[i];
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        string tag = sql[start..(close + 1)];
        int terminator = sql.IndexOf(tag, close + 1, StringComparison.Ordinal);
        end = terminator < 0 ? sql.Length : terminator + tag.Length;
        return true;
    }

    /// <summary>
    /// Collects tokens into lines, splitting any token that spans one.
    /// </summary>
    /// <remarks>
    /// A block comment and a dollar-quoted function body both contain newlines, so the lexer cannot be
    /// line-oriented; this is what keeps the gutter honest anyway.
    /// </remarks>
    private sealed class LineWriter(int maxLines, int maxLineLength)
    {
        /// <summary>What is put where the rest of a cut line would have been.</summary>
        private const string Ellipsis = " … (line truncated)";

        private readonly ImmutableArray<SqlLine>.Builder lines = ImmutableArray.CreateBuilder<SqlLine>();
        private readonly ImmutableArray<SqlToken>.Builder tokens = ImmutableArray.CreateBuilder<SqlToken>();
        private int lineNumber = 1;
        private int totalLines;
        private int lineLength;
        private int linesTrimmed;
        private bool lineCut;
        private bool clamped;

        public void Write(string text, SqlTokenKind kind)
        {
            int start = 0;
            while (true)
            {
                int newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    Append(text[start..], kind);
                    return;
                }

                Append(text[start..newline], kind);
                EndLine();
                start = newline + 1;
            }
        }

        public void EndLine()
        {
            if (lines.Count >= maxLines)
            {
                clamped = true;
            }
            else
            {
                lines.Add(new SqlLine(lineNumber, tokens.ToImmutable()));
            }

            tokens.Clear();
            lineNumber++;
            totalLines = lineNumber - 1;
            lineLength = 0;
            lineCut = false;
        }

        public SqlDocument Complete()
        {
            if (tokens.Count > 0 || lines.Count == 0)
            {
                EndLine();
            }

            return new SqlDocument(lines.ToImmutable(), Math.Max(totalLines, lines.Count), clamped, linesTrimmed);
        }

        private void Append(string text, SqlTokenKind kind)
        {
            string trimmed = text.TrimEnd('\r');
            if (trimmed.Length == 0)
            {
                return;
            }

            if (lineCut)
            {
                return;
            }

            int room = maxLineLength - lineLength;
            if (trimmed.Length > room)
            {
                // Cut once, mark the line, and say so in the document rather than silently dropping the
                // rest: a person reading a truncated statement has to know it was truncated.
                if (room > 0)
                {
                    tokens.Add(new SqlToken(trimmed[..room], kind));
                    lineLength += room;
                }

                tokens.Add(new SqlToken(Ellipsis, SqlTokenKind.Comment));
                lineCut = true;
                linesTrimmed++;
                return;
            }

            tokens.Add(new SqlToken(trimmed, kind));
            lineLength += trimmed.Length;
        }
    }
}
