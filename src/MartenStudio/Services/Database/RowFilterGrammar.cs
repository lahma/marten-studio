using System.Text;

using MartenStudio.Services.Query;

namespace MartenStudio.Services.Database;

/// <summary>
/// The database browser's row filter: <c>col op value</c> with <c>= != &lt; &lt;= &gt; &gt;=</c>,
/// <c>col ~ text</c>, and <c>col is:null</c> / <c>col is:notnull</c>, ANDed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Values are never interpreted.</b> The document grammar classifies a literal - number, boolean,
/// timestamp - because it has to pick a cast for a JSON path. A column has a type of its own, so here the
/// raw token goes to Postgres untouched, as untyped text, and the column's input function reads it: an
/// enum label, a domain value, a <c>citext</c>, a <c>bpchar</c>, a range, and <c>007</c> in a text column
/// all mean what they would in <c>psql</c>. Coercing through <c>SearchValue</c> first would turn
/// <c>007</c> into <c>7</c>.
/// </para>
/// <para>
/// <b>Quoting</b> follows the document grammar: <c>"</c> or <c>'</c>, the quote doubled or
/// backslash-escaped inside. A quoted value is how to match whitespace, an empty string, or the word
/// <c>null</c>; an unquoted <c>null</c> is refused with a pointer to <c>is:null</c>, because it would
/// otherwise compare with the four-letter string. A quoted column name matches exactly; a bare one matches
/// case-insensitively, the exact spelling first. Only the catalog's spelling is ever quoted into SQL.
/// </para>
/// <para>
/// <b>It never throws.</b> Every problem is a <see cref="SearchGrammarError" /> with a position, and the
/// terms around it still parse. Its own tokenizer, rather than <see cref="SearchGrammar" />'s: that one's
/// is private to the document grammar, whose paths are dotted and whose values are classified, and neither
/// is true here.
/// </para>
/// </remarks>
internal static class RowFilterGrammar
{
    /// <summary>
    /// Parses a filter and resolves its columns against <paramref name="columns" />: an unknown or ambiguous
    /// column, a comparison on a column with no btree opclass, and a column whose name cannot be quoted are
    /// all errors with positions.
    /// </summary>
    public static RowFilterParse Parse(string? text, IReadOnlyList<DatabaseColumnInfo> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        (IReadOnlyList<RawTerm> raw, List<SearchGrammarError> errors) = Tokenize(text);
        List<RowFilterTerm> terms = [];

        foreach (RawTerm term in raw)
        {
            DatabaseColumnInfo? column = Resolve(columns, term, errors);

            if (column is null)
            {
                continue;
            }

            if (!column.Quotable)
            {
                errors.Add(new SearchGrammarError(
                    "'" + column.Name + "' cannot be put into SQL safely, so it cannot be filtered on.",
                    term.ColumnPosition,
                    term.ColumnLength));
                continue;
            }

            var resolved = new RowFilterTerm(column.Name, term.Operator, term.Value, term.Position, term.Length);

            if (resolved.IsComparison && !column.Sortable)
            {
                errors.Add(new SearchGrammarError(
                    "'" + column.Name + "' is " + column.Type + ", which has no ordering Postgres can compare " +
                    "with (no default btree operator class). Use ~ or is:null on it.",
                    term.OperatorPosition,
                    Math.Max(term.OperatorLength, 1)));
                continue;
            }

            terms.Add(resolved);
        }

        return new RowFilterParse(terms, errors);
    }

    /// <summary>The syntax alone, before any column is resolved - what the unit tests of positions read.</summary>
    internal static (IReadOnlyList<RawTerm> Terms, List<SearchGrammarError> Errors) Tokenize(string? text)
    {
        List<RawTerm> terms = [];
        List<SearchGrammarError> errors = [];

        if (string.IsNullOrWhiteSpace(text))
        {
            return (terms, errors);
        }

        int position = 0;

        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
                continue;
            }

            ParseTerm(text, ref position, terms, errors);
        }

        return (terms, errors);
    }

    /// <summary>
    /// One term written back out so that <see cref="Parse" /> reads it as the same term: the value quoted
    /// when it has to be. What "filter by this value" and a reference's filter link are built with.
    /// </summary>
    public static string Format(string column, RowFilterOperator op, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);

        string name = NeedsQuoting(column, isColumn: true) ? Quote(column) : column;

        return op switch
        {
            RowFilterOperator.IsNull => name + " is:null",
            RowFilterOperator.IsNotNull => name + " is:notnull",
            _ => name + " " + OperatorText(op) + " " + FormatValue(value ?? string.Empty),
        };
    }

    /// <summary>Several terms, ANDed.</summary>
    public static string Format(IEnumerable<(string Column, RowFilterOperator Operator, string? Value)> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        return string.Join(' ', terms.Select(static x => Format(x.Column, x.Operator, x.Value)));
    }

    /// <summary>A term as its chip reads.</summary>
    public static string Describe(RowFilterTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);

        return Format(term.Column, term.Operator, term.Value);
    }

    /// <summary>An operator as the grammar spells it.</summary>
    public static string OperatorText(RowFilterOperator op) => op switch
    {
        RowFilterOperator.Equal => "=",
        RowFilterOperator.NotEqual => "!=",
        RowFilterOperator.LessThan => "<",
        RowFilterOperator.LessThanOrEqual => "<=",
        RowFilterOperator.GreaterThan => ">",
        RowFilterOperator.GreaterThanOrEqual => ">=",
        RowFilterOperator.Contains => "~",
        RowFilterOperator.IsNull => "is:null",
        _ => "is:notnull",
    };

    private static void ParseTerm(string text, ref int position, List<RawTerm> terms, List<SearchGrammarError> errors)
    {
        int termStart = position;

        if (!TryReadColumn(text, ref position, out string? column, out bool quoted, out SearchGrammarError? columnError))
        {
            errors.Add(columnError!);
            return;
        }

        int columnLength = position - termStart;
        int probe = position;
        SkipWhitespace(text, ref probe);

        if (probe < text.Length && StartsWithWord(text, probe, "is:"))
        {
            int stateStart = probe;
            probe += 3;
            int wordStart = probe;

            while (probe < text.Length && !char.IsWhiteSpace(text[probe]))
            {
                probe++;
            }

            string state = text[wordStart..probe];
            position = probe;

            if (string.Equals(state, "null", StringComparison.OrdinalIgnoreCase))
            {
                terms.Add(new RawTerm(column!, quoted, termStart, columnLength, RowFilterOperator.IsNull, null, stateStart, probe - stateStart, termStart, probe - termStart));
            }
            else if (string.Equals(state, "notnull", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(state, "not-null", StringComparison.OrdinalIgnoreCase))
            {
                terms.Add(new RawTerm(column!, quoted, termStart, columnLength, RowFilterOperator.IsNotNull, null, stateStart, probe - stateStart, termStart, probe - termStart));
            }
            else
            {
                errors.Add(new SearchGrammarError(
                    "'is:" + state + "' is not a state. Use is:null or is:notnull.",
                    stateStart,
                    Math.Max(probe - stateStart, 1)));
            }

            return;
        }

        if (!TryReadOperator(text, ref probe, out RowFilterOperator op, out int operatorLength))
        {
            errors.Add(new SearchGrammarError(
                "'" + column + "' needs an operator after it: =, !=, <, <=, >, >=, ~, is:null or is:notnull.",
                termStart,
                Math.Max(columnLength, 1)));

            // The column was consumed, so the loop makes progress; whatever follows is read as a term of
            // its own and reported in its own right.
            return;
        }

        int operatorStart = probe - operatorLength;
        int valueStart = probe;
        SkipWhitespace(text, ref valueStart);

        if (valueStart >= text.Length)
        {
            errors.Add(new SearchGrammarError(
                "'" + OperatorText(op) + "' needs a value after it.",
                operatorStart,
                operatorLength));
            position = text.Length;
            return;
        }

        if (!TryReadValue(text, ref valueStart, out string? value, out bool valueQuoted, out SearchGrammarError? valueError))
        {
            errors.Add(valueError!);
            position = text.Length;
            return;
        }

        position = valueStart;

        if (!valueQuoted && op != RowFilterOperator.Contains && string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new SearchGrammarError(
                "Use '" + column + " is:null' to find missing values; quote \"null\" to compare with the word.",
                termStart,
                position - termStart));
            return;
        }

        terms.Add(new RawTerm(column!, quoted, termStart, columnLength, op, value, operatorStart, operatorLength, termStart, position - termStart));
    }

    private static DatabaseColumnInfo? Resolve(IReadOnlyList<DatabaseColumnInfo> columns, RawTerm term, List<SearchGrammarError> errors)
    {
        foreach (DatabaseColumnInfo column in columns)
        {
            if (string.Equals(column.Name, term.Column, StringComparison.Ordinal))
            {
                return column;
            }
        }

        if (!term.ColumnQuoted)
        {
            List<DatabaseColumnInfo> folded = [.. columns.Where(x => string.Equals(x.Name, term.Column, StringComparison.OrdinalIgnoreCase))];

            if (folded.Count == 1)
            {
                return folded[0];
            }

            if (folded.Count > 1)
            {
                errors.Add(new SearchGrammarError(
                    "'" + term.Column + "' matches more than one column (" + string.Join(", ", folded.Select(static x => x.Name)) +
                    "); quote the one you mean, spelled exactly.",
                    term.ColumnPosition,
                    Math.Max(term.ColumnLength, 1)));
                return null;
            }
        }

        errors.Add(new SearchGrammarError(
            "There is no column '" + term.Column + "'.",
            term.ColumnPosition,
            Math.Max(term.ColumnLength, 1)));
        return null;
    }

    private static bool TryReadColumn(
        string text,
        ref int position,
        out string? column,
        out bool quoted,
        out SearchGrammarError? error)
    {
        column = null;
        quoted = false;
        error = null;

        if (text[position] is '"' or '\'')
        {
            quoted = true;

            if (!TryReadQuoted(text, ref position, out column, out error))
            {
                return false;
            }

            if (column!.Length == 0)
            {
                error = new SearchGrammarError("A column name cannot be empty.", position - 2, 2);
                return false;
            }

            return true;
        }

        int start = position;

        while (position < text.Length && IsColumnCharacter(text[position]))
        {
            position++;
        }

        if (position == start)
        {
            error = new SearchGrammarError("'" + text[position] + "' does not start a filter term; a term starts with a column name.", position, 1);
            position++;
            return false;
        }

        column = text[start..position];
        return true;
    }

    private static bool TryReadOperator(string text, ref int position, out RowFilterOperator op, out int length)
    {
        op = RowFilterOperator.Equal;
        length = 0;

        if (position >= text.Length)
        {
            return false;
        }

        string two = position + 1 < text.Length ? text.Substring(position, 2) : string.Empty;

        (RowFilterOperator Op, int Length)? found = two switch
        {
            ">=" => (RowFilterOperator.GreaterThanOrEqual, 2),
            "<=" => (RowFilterOperator.LessThanOrEqual, 2),
            "!=" or "<>" => (RowFilterOperator.NotEqual, 2),
            _ => text[position] switch
            {
                '=' => (RowFilterOperator.Equal, 1),
                '<' => (RowFilterOperator.LessThan, 1),
                '>' => (RowFilterOperator.GreaterThan, 1),
                '~' => (RowFilterOperator.Contains, 1),
                _ => null,
            },
        };

        if (found is not { } match)
        {
            return false;
        }

        op = match.Op;
        length = match.Length;
        position += match.Length;
        return true;
    }

    private static bool TryReadValue(
        string text,
        ref int position,
        out string? value,
        out bool quoted,
        out SearchGrammarError? error)
    {
        error = null;

        if (text[position] is '"' or '\'')
        {
            quoted = true;
            return TryReadQuoted(text, ref position, out value, out error);
        }

        quoted = false;
        int start = position;

        while (position < text.Length && !char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        value = text[start..position];
        return true;
    }

    private static bool TryReadQuoted(string text, ref int position, out string? value, out SearchGrammarError? error)
    {
        char quote = text[position];
        int start = position;
        var builder = new StringBuilder();

        position++;

        while (position < text.Length)
        {
            char c = text[position];

            if (c == '\\' && position + 1 < text.Length)
            {
                builder.Append(text[position + 1]);
                position += 2;
                continue;
            }

            if (c == quote)
            {
                if (position + 1 < text.Length && text[position + 1] == quote)
                {
                    builder.Append(quote);
                    position += 2;
                    continue;
                }

                position++;
                value = builder.ToString();
                error = null;
                return true;
            }

            builder.Append(c);
            position++;
        }

        value = null;
        error = new SearchGrammarError("This " + quote + " is never closed.", start, Math.Max(text.Length - start, 1));
        position = text.Length;
        return false;
    }

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private static bool StartsWithWord(string text, int position, string word) =>
        string.Compare(text, position, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool IsColumnCharacter(char c) =>
        !char.IsWhiteSpace(c) && c is not ('=' or '<' or '>' or '!' or '~' or ':' or '"' or '\'');

    private static string FormatValue(string value) => NeedsQuoting(value, isColumn: false) ? Quote(value) : value;

    private static bool NeedsQuoting(string value, bool isColumn)
    {
        if (value.Length == 0 || value[0] is '"' or '\'')
        {
            return true;
        }

        if (!isColumn && string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c) || (isColumn && !IsColumnCharacter(c)))
            {
                return true;
            }
        }

        return isColumn && value.StartsWith("is:", StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');

        foreach (char c in value)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.Append('"').ToString();
    }

    /// <summary>One term as typed, before its column is resolved.</summary>
    /// <param name="Column">The column as typed, quotes removed.</param>
    /// <param name="ColumnQuoted">Whether it was quoted - an exact match only.</param>
    /// <param name="ColumnPosition">Where the column starts.</param>
    /// <param name="ColumnLength">How long the column is, quotes included.</param>
    /// <param name="Operator">The comparison.</param>
    /// <param name="Value">The raw value, or <see langword="null" /> for <c>is:</c>.</param>
    /// <param name="OperatorPosition">Where the operator starts.</param>
    /// <param name="OperatorLength">How long it is.</param>
    /// <param name="Position">Where the term starts.</param>
    /// <param name="Length">How long it is.</param>
    internal sealed record RawTerm(
        string Column,
        bool ColumnQuoted,
        int ColumnPosition,
        int ColumnLength,
        RowFilterOperator Operator,
        string? Value,
        int OperatorPosition,
        int OperatorLength,
        int Position,
        int Length);
}
