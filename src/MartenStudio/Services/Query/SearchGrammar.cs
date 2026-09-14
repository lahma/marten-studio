using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MartenStudio.Services.Query;

/// <summary>The comparisons the search grammar understands.</summary>
internal enum ComparisonOperator
{
    /// <summary><c>=</c>, or <c>field:value</c>.</summary>
    Equal,

    /// <summary><c>!=</c> or <c>&lt;&gt;</c>.</summary>
    NotEqual,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThan,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterThanOrEqual,

    /// <summary><c>&lt;</c>.</summary>
    LessThan,

    /// <summary><c>&lt;=</c>.</summary>
    LessThanOrEqual,
}

/// <summary>
/// A literal on the right of a comparison, already classified, because the classification decides the
/// Postgres cast the query builder applies to the JSON path.
/// </summary>
/// <remarks>
/// Quoting is what forces <see cref="Text"/>: <c>status = 1</c> compares numerically,
/// <c>status = "1"</c> compares as text. That is the only way a user can say which they meant, and it
/// matters because <c>(data ->> 'status')::numeric</c> throws on a document whose <c>status</c> is a word.
/// </remarks>
internal abstract record SearchValue
{
    private SearchValue()
    {
    }

    /// <summary>Compared as text, with no cast.</summary>
    /// <param name="Value">The literal.</param>
    public sealed record Text(string Value) : SearchValue;

    /// <summary>Compared with a <c>::numeric</c> cast.</summary>
    /// <param name="Value">The literal.</param>
    public sealed record Number(decimal Value) : SearchValue;

    /// <summary>Compared with a <c>::boolean</c> cast.</summary>
    /// <param name="Value">The literal.</param>
    public sealed record Boolean(bool Value) : SearchValue;

    /// <summary>Compared with a <c>::timestamptz</c> cast.</summary>
    /// <param name="Value">The literal, normalised to UTC.</param>
    public sealed record Timestamp(DateTimeOffset Value) : SearchValue;

    /// <summary>Compared with <c>is null</c> / <c>is not null</c>.</summary>
    public sealed record Null : SearchValue;
}

/// <summary>
/// One term of a parsed search. The list is ANDed; there is no OR and no negation in v1, because a filter
/// whose index verdict cannot be explained is worse than one the user has to type twice.
/// </summary>
internal abstract record DocumentPredicate
{
    private DocumentPredicate()
    {
    }

    /// <summary><c>id:&lt;value&gt;</c> — the primary key, typed from the id column.</summary>
    /// <param name="Id">The id as typed, parsed later against the column's real type.</param>
    public sealed record IdEquals(string Id) : DocumentPredicate;

    /// <summary><c>path op value</c> — a duplicated column when there is one, otherwise the JSON path.</summary>
    /// <param name="Path">The dotted path, split into segments.</param>
    /// <param name="Operator">The comparison.</param>
    /// <param name="Value">The literal.</param>
    public sealed record FieldCompare(IReadOnlyList<string> Path, ComparisonOperator Operator, SearchValue Value)
        : DocumentPredicate;

    /// <summary><c>@&gt; {json}</c> — jsonb containment, the one filter a plain GIN index serves.</summary>
    /// <param name="Json">The JSON document, verbatim and already checked to parse.</param>
    public sealed record Contains(string Json) : DocumentPredicate;

    /// <summary><c>path ~ text</c> — case-insensitive substring match on one field.</summary>
    /// <param name="Path">The dotted path, split into segments.</param>
    /// <param name="Text">The substring, taken literally (LIKE metacharacters are escaped).</param>
    public sealed record FieldLike(IReadOnlyList<string> Path, string Text) : DocumentPredicate;

    /// <summary>A bare word — a case-insensitive substring match over the whole document text.</summary>
    /// <param name="Text">The substring, taken literally.</param>
    public sealed record FreeText(string Text) : DocumentPredicate;

    /// <summary><c>is:deleted</c> / <c>is:not-deleted</c>.</summary>
    /// <param name="Value">Whether deleted documents are the ones wanted.</param>
    public sealed record IsDeleted(bool Value) : DocumentPredicate;

    /// <summary><c>tenant:&lt;id&gt;</c>.</summary>
    /// <param name="TenantId">The tenant.</param>
    public sealed record Tenant(string TenantId) : DocumentPredicate;

    /// <summary>
    /// <c>mt_doc_type = &lt;alias&gt;</c> — one subclass of a hierarchy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A hierarchy's subclasses share their root's table and are told apart by the discriminator column, so
    /// browsing <c>/marten/documents/car</c> is browsing the vehicle table with this predicate added. It is
    /// a predicate rather than a flag on the query so that it goes through the same allow-listed builder,
    /// gets its own index verdict, and shows up in "Show SQL".
    /// </para>
    /// <para>
    /// The grammar also spells it <c>type:&lt;alias&gt;</c>, because the verdict strip already writes it
    /// back out that way: a chip a user can read and cannot type is a chip that teaches a syntax the search
    /// box then rejects.
    /// </para>
    /// </remarks>
    /// <param name="Alias">The subclass alias, as <c>IDocumentType.AliasFor</c> reports it.</param>
    public sealed record SubclassIs(string Alias) : DocumentPredicate;
}

/// <summary>Something the parser could not make sense of, and where it is.</summary>
/// <param name="Message">What is wrong, phrased for the person who typed it.</param>
/// <param name="Position">Zero-based offset into the search text.</param>
/// <param name="Length">How much of the text the message is about; at least one character.</param>
internal sealed record SearchGrammarError(string Message, int Position, int Length);

/// <summary>The result of parsing a search box: what was understood, and what was not.</summary>
/// <param name="Predicates">The terms that parsed, in the order typed.</param>
/// <param name="Errors">Everything that did not, with positions.</param>
internal sealed record SearchGrammarResult(
    IReadOnlyList<DocumentPredicate> Predicates,
    IReadOnlyList<SearchGrammarError> Errors)
{
    /// <summary>Whether anything failed to parse.</summary>
    public bool HasErrors => Errors.Count > 0;

    /// <summary>An empty search.</summary>
    public static SearchGrammarResult Empty { get; } = new([], []);
}

/// <summary>
/// The document search grammar of plan §3.4:
/// <c>id:&lt;value&gt;</c> · <c>field op value</c> (dotted paths) · <c>@&gt; {json}</c> ·
/// <c>field ~ text</c> · free text · <c>is:deleted</c> · <c>tenant:x</c> · <c>type:&lt;alias&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// It never throws and it never gives up on a whole search because one term is wrong: a term that does not
/// parse becomes a <see cref="SearchGrammarError"/> with a position, and the rest still runs. A search box
/// is typed a character at a time, so a parser that answers "no" to everything until the last quote is
/// closed is a parser that is wrong for most of the time it is being used.
/// </para>
/// <para>
/// Quoting: a segment or a value may be wrapped in <c>"</c> or <c>'</c>, with the quote character doubled
/// or backslash-escaped inside. Quoting a value also pins it to <see cref="SearchValue.Text"/> — see
/// there. Quoting a path segment is how a JSON key with a dot or a space in it is reached.
/// </para>
/// <para>
/// <c>id</c>, <c>is</c>, <c>tenant</c> and <c>type</c> are reserved in front of a colon, and <c>id = …</c>
/// is read as the primary key too, because on a Marten document the JSON <c>Id</c> property and the
/// <c>id</c> column are the same value and the column is the one with the index on it. A document that
/// genuinely has a JSON property called <c>type</c> is still reachable — quote the path
/// (<c>"type": value</c> is a path segment, not the keyword) or use an explicit operator
/// (<c>type = value</c>).
/// </para>
/// </remarks>
internal static class SearchGrammar
{
    /// <summary>Parses a search box. Never throws.</summary>
    public static SearchGrammarResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SearchGrammarResult.Empty;
        }

        List<DocumentPredicate> predicates = [];
        List<SearchGrammarError> errors = [];
        var position = 0;

        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position]))
            {
                position++;
                continue;
            }

            ParseTerm(text, ref position, predicates, errors);
        }

        return new SearchGrammarResult(predicates, errors);
    }

    private static void ParseTerm(
        string text,
        ref int position,
        List<DocumentPredicate> predicates,
        List<SearchGrammarError> errors)
    {
        var termStart = position;

        if (IsContainmentOperator(text, position))
        {
            ParseContainment(text, ref position, predicates, errors);
            return;
        }

        if (!TryReadPath(text, ref position, out var path, out var pathError))
        {
            // TryReadPath has already moved past what it could not read - one character for a stray
            // symbol, the rest of the text for a quote that is never closed - so the loop makes progress
            // and a single bad character does not silence the terms after it.
            errors.Add(pathError!);
            return;
        }

        var afterPath = position;
        var probe = position;

        SkipWhitespace(text, ref probe);

        if (!TryReadOperator(text, ref probe, out var op))
        {
            predicates.Add(new DocumentPredicate.FreeText(FreeTextOf(text, path, termStart, afterPath)));
            position = afterPath;
            return;
        }

        var operatorStart = probe;
        var valueStart = probe;

        SkipWhitespace(text, ref valueStart);

        if (!TryReadValue(text, ref valueStart, out var token, out var valueError))
        {
            errors.Add(valueError ?? new SearchGrammarError(
                $"'{op}' needs a value after it.", operatorStart - op!.Length, Math.Max(op.Length, 1)));
            position = text.Length;
            return;
        }

        position = valueStart;

        AddComparison(path, op!, token!, termStart, predicates, errors);
    }

    private static void AddComparison(
        SearchPath path,
        string op,
        ValueToken token,
        int termStart,
        List<DocumentPredicate> predicates,
        List<SearchGrammarError> errors)
    {
        if (string.Equals(op, ":", StringComparison.Ordinal) && path.IsSimpleWord)
        {
            var keyword = path.Segments[0];

            if (string.Equals(keyword, "id", StringComparison.OrdinalIgnoreCase))
            {
                predicates.Add(new DocumentPredicate.IdEquals(token.Raw));
                return;
            }

            if (string.Equals(keyword, "tenant", StringComparison.OrdinalIgnoreCase))
            {
                predicates.Add(new DocumentPredicate.Tenant(token.Raw));
                return;
            }

            // The one keyword whose need was found by reading the output: the verdict strip renders a
            // subclass filter as `type:car`, so `type:car` has to be something a person can type back.
            if (string.Equals(keyword, "type", StringComparison.OrdinalIgnoreCase))
            {
                predicates.Add(new DocumentPredicate.SubclassIs(token.Raw));
                return;
            }

            if (string.Equals(keyword, "is", StringComparison.OrdinalIgnoreCase))
            {
                switch (token.Raw.ToLowerInvariant())
                {
                    case "deleted":
                        predicates.Add(new DocumentPredicate.IsDeleted(true));
                        return;
                    case "not-deleted":
                    case "live":
                        predicates.Add(new DocumentPredicate.IsDeleted(false));
                        return;
                    default:
                        errors.Add(new SearchGrammarError(
                            $"'is:{token.Raw}' is not a state. Use is:deleted or is:not-deleted.",
                            token.Start,
                            Math.Max(token.Raw.Length, 1)));
                        return;
                }
            }
        }

        if (string.Equals(op, "~", StringComparison.Ordinal))
        {
            predicates.Add(new DocumentPredicate.FieldLike(path.Segments, token.Raw));
            return;
        }

        var comparison = ToComparison(op);

        // On a Marten document the JSON id property and the indexed id column hold the same value, so
        // `id = x` means the column. Every other path is a JSON path.
        if (comparison == ComparisonOperator.Equal && path.IsSimpleWord &&
            string.Equals(path.Segments[0], "id", StringComparison.OrdinalIgnoreCase))
        {
            predicates.Add(new DocumentPredicate.IdEquals(token.Raw));
            return;
        }

        if (path.Segments.Count == 0)
        {
            errors.Add(new SearchGrammarError($"'{op}' needs a field before it.", termStart, 1));
            return;
        }

        predicates.Add(new DocumentPredicate.FieldCompare(path.Segments, comparison, Classify(token)));
    }

    private static void ParseContainment(
        string text,
        ref int position,
        List<DocumentPredicate> predicates,
        List<SearchGrammarError> errors)
    {
        var operatorStart = position;

        position += 2;

        SkipWhitespace(text, ref position);

        if (position >= text.Length || (text[position] != '{' && text[position] != '['))
        {
            errors.Add(new SearchGrammarError("'@>' needs a JSON document after it, for example @> {\"status\":\"open\"}.",
                operatorStart, 2));
            position = text.Length;
            return;
        }

        var jsonStart = position;
        var opening = text[position];
        var closing = opening == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;

        while (position < text.Length)
        {
            var c = text[position];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == opening)
            {
                depth++;
            }
            else if (c == closing)
            {
                depth--;

                if (depth == 0)
                {
                    position++;
                    break;
                }
            }

            position++;
        }

        if (depth != 0)
        {
            errors.Add(new SearchGrammarError(
                $"The JSON after '@>' is missing its closing '{closing}'.", jsonStart, text.Length - jsonStart));
            position = text.Length;
            return;
        }

        var json = text[jsonStart..position];

        try
        {
            using var document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            errors.Add(new SearchGrammarError(
                $"The JSON after '@>' does not parse: {exception.Message}", jsonStart, json.Length));
            return;
        }

        predicates.Add(new DocumentPredicate.Contains(json));
    }

    private static SearchValue Classify(ValueToken token)
    {
        if (token.WasQuoted)
        {
            return new SearchValue.Text(token.Raw);
        }

        if (string.Equals(token.Raw, "null", StringComparison.OrdinalIgnoreCase))
        {
            return new SearchValue.Null();
        }

        if (bool.TryParse(token.Raw, out var boolean))
        {
            return new SearchValue.Boolean(boolean);
        }

        if (decimal.TryParse(token.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return new SearchValue.Number(number);
        }

        if (LooksLikeTimestamp(token.Raw) && DateTimeOffset.TryParse(
                token.Raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return new SearchValue.Timestamp(timestamp);
        }

        return new SearchValue.Text(token.Raw);
    }

    private static bool LooksLikeTimestamp(string value)
    {
        // yyyy-MM-dd, optionally followed by a time. Deliberately strict: "12-34" must stay text.
        if (value.Length < 10)
        {
            return false;
        }

        for (var i = 0; i < 10; i++)
        {
            var expectDash = i is 4 or 7;
            var c = value[i];

            if (expectDash ? c != '-' : !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return value.Length == 10 || value[10] is 'T' or 't' or ' ';
    }

    private static ComparisonOperator ToComparison(string op) => op switch
    {
        "!=" or "<>" => ComparisonOperator.NotEqual,
        ">=" => ComparisonOperator.GreaterThanOrEqual,
        "<=" => ComparisonOperator.LessThanOrEqual,
        ">" => ComparisonOperator.GreaterThan,
        "<" => ComparisonOperator.LessThan,
        _ => ComparisonOperator.Equal,
    };

    private static bool IsContainmentOperator(string text, int position) =>
        text[position] == '@' && position + 1 < text.Length && text[position + 1] == '>';

    private static void SkipWhitespace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }

    private static bool TryReadOperator(string text, ref int position, out string? op)
    {
        op = null;

        if (position >= text.Length)
        {
            return false;
        }

        var two = position + 1 < text.Length ? text.Substring(position, 2) : string.Empty;

        if (two is ">=" or "<=" or "!=" or "<>")
        {
            op = two;
            position += 2;
            return true;
        }

        var one = text[position];

        if (one is '=' or '>' or '<' or '~' or ':')
        {
            op = one.ToString();
            position++;
            return true;
        }

        return false;
    }

    private static bool TryReadPath(string text, ref int position, out SearchPath path, out SearchGrammarError? error)
    {
        List<string> segments = [];
        var quotedAny = false;
        var segmentCount = 0;
        error = null;

        while (true)
        {
            if (position < text.Length && (text[position] == '"' || text[position] == '\''))
            {
                if (!TryReadQuoted(text, ref position, out var quoted, out error))
                {
                    path = SearchPath.Empty;
                    return false;
                }

                segments.Add(quoted!);
                quotedAny = true;
            }
            else
            {
                var start = position;

                while (position < text.Length && IsWordCharacter(text, position))
                {
                    position++;
                }

                if (position == start)
                {
                    break;
                }

                segments.Add(text[start..position]);
            }

            segmentCount++;

            if (position < text.Length && text[position] == '.')
            {
                position++;
                continue;
            }

            break;
        }

        if (segmentCount == 0)
        {
            // A character that starts nothing the grammar knows: consume it so the loop makes progress.
            error = new SearchGrammarError($"'{text[position]}' does not start a search term.", position, 1);
            position++;
            path = SearchPath.Empty;
            return false;
        }

        path = new SearchPath(segments, quotedAny);
        return true;
    }

    private static bool TryReadValue(string text, ref int position, out ValueToken? token, out SearchGrammarError? error)
    {
        token = null;
        error = null;

        if (position >= text.Length)
        {
            return false;
        }

        var start = position;

        if (text[position] is '"' or '\'')
        {
            if (!TryReadQuoted(text, ref position, out var quoted, out error))
            {
                return false;
            }

            token = new ValueToken(quoted!, true, start);
            return true;
        }

        while (position < text.Length && !char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        if (position == start)
        {
            return false;
        }

        token = new ValueToken(text[start..position], false, start);
        return true;
    }

    private static bool TryReadQuoted(string text, ref int position, out string? value, out SearchGrammarError? error)
    {
        var quote = text[position];
        var start = position;
        var builder = new StringBuilder();

        position++;

        while (position < text.Length)
        {
            var c = text[position];

            if (c == '\\' && position + 1 < text.Length)
            {
                builder.Append(text[position + 1]);
                position += 2;
                continue;
            }

            if (c == quote)
            {
                // A doubled quote is an escaped quote, the way SQL and CSV both spell it.
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
        error = new SearchGrammarError(
            $"This {quote} is never closed.", start, Math.Max(text.Length - start, 1));
        return false;
    }

    private static bool IsWordCharacter(string text, int position)
    {
        var c = text[position];

        if (char.IsWhiteSpace(c))
        {
            return false;
        }

        if (c is ':' or '=' or '<' or '>' or '!' or '~' or '.' or '{' or '}' or '"' or '\'')
        {
            return false;
        }

        // '@' only ends a word when it is the containment operator, so that an email address stays one word.
        return c != '@' || !IsContainmentOperator(text, position);
    }

    private static string FreeTextOf(string text, SearchPath path, int termStart, int termEnd)
    {
        // A single quoted term is free text without its quotes; anything else is what the user typed.
        return path.Segments.Count == 1 && path.HadQuotes ? path.Segments[0] : text[termStart..termEnd];
    }

    private sealed record SearchPath(IReadOnlyList<string> Segments, bool HadQuotes)
    {
        public static SearchPath Empty { get; } = new([], false);

        public bool IsSimpleWord => Segments.Count == 1 && !HadQuotes;
    }

    private sealed record ValueToken(string Raw, bool WasQuoted, int Start);
}
