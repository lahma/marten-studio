using System.Text;

namespace PostgresStudio.Services.Query;

/// <summary>How a column is wrapped where a plan's condition uses it.</summary>
internal enum PlanColumnWrap
{
    /// <summary>Compared as it is.</summary>
    None,

    /// <summary>Cast to another type: <c>(created_at)::date</c>.</summary>
    Cast,

    /// <summary>The first argument of a function: <c>lower((email)::text)</c>.</summary>
    Function,
}

/// <summary>One column reference found in a condition's text.</summary>
/// <param name="Qualifier">The alias or relation in front of it (<c>o.customer_id</c>), or <see langword="null" />.</param>
/// <param name="Column">The column, unquoted.</param>
/// <param name="CastType">The type it is cast to when the reference is cast (<c>date</c>, <c>character varying</c>), lower case and without a size.</param>
/// <param name="Function">The function it is the first argument of, lower case, when it is one.</param>
internal sealed record PlanConditionColumn(string? Qualifier, string Column, string? CastType, string? Function)
{
    /// <summary>The outermost wrapper the reference has.</summary>
    public PlanColumnWrap Wrap => Function is not null ? PlanColumnWrap.Function : CastType is not null ? PlanColumnWrap.Cast : PlanColumnWrap.None;
}

/// <summary>
/// A bounded scan of the text Postgres deparses a plan's conditions into - <c>Filter</c>, <c>Index Cond</c>, <c>Hash Cond</c>,
/// <c>Sort Key</c> - for the column references in it (0.10-A, issue #28).
/// </summary>
/// <remarks>
/// <para>
/// <b>Text in, names out, nothing run.</b> The text is another program's output - Postgres deparsing an expression a visitor wrote -
/// and is read for names that are afterwards only ever bound as parameters (<c>PlanFactsQueries</c>) or compared with the catalog's
/// own; no name found here becomes SQL text. A string literal is skipped whole, with its doubled quotes and an <c>E''</c> string's
/// backslashes; a quoted identifier is read, doubled quotes undone, because a host whose columns are <c>"CustomerId"</c> would
/// otherwise get no facts at all.
/// </para>
/// <para>
/// <b>Why not <c>SqlLexer</c>.</b> The guard's lexer walks over a quoted identifier without returning it - correct for a guard,
/// useless here - and widening it would touch the one scanner the read-only guard stands on. This is a second, much smaller walk
/// with a different job and a hard bound (<see cref="MaxCharacters" />, <see cref="MaxTokens" />): what it cannot read it leaves out.
/// </para>
/// <para>
/// <b>Heuristic, and honest about it.</b> A word is a column unless it is a keyword, a function (it is followed by <c>(</c>), a type
/// name (it follows <c>::</c>) or a qualifier. A column is "wrapped" when a cast follows it, or a function's open parenthesis
/// precedes it as the first argument. A column a function takes as its second argument is not seen as wrapped: a false negative, never
/// a false finding.
/// </para>
/// </remarks>
internal static class PlanConditionText
{
    /// <summary>The longest text read; Postgres deparses a condition in far less, and a longer one is not read.</summary>
    public const int MaxCharacters = 8_000;

    /// <summary>The most tokens read from one text.</summary>
    public const int MaxTokens = 2_000;

    private enum Kind
    {
        Word,
        Quoted,
        Literal,
        Number,
        Param,
        Cast,
        Punct,
    }

    private readonly record struct Token(Kind Kind, string Text, char Punct = '\0');

    /// <summary>Words that are never a column: Postgres' keywords as an expression deparses them, and the names it prints bare.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "or", "not", "is", "null", "true", "false", "any", "all", "some", "array", "in", "like", "ilike", "between", "case",
        "when", "then", "else", "end", "asc", "desc", "nulls", "first", "last", "distinct", "from", "similar", "to", "escape",
        "isnull", "notnull", "unknown", "row", "as", "collate", "current_date", "current_time", "current_timestamp", "localtime",
        "localtimestamp", "current_user", "session_user", "user", "interval", "at", "time", "zone", "only", "using", "over",
        "filter", "within", "order", "by", "symmetric", "asymmetric", "exists", "default", "operator", "select", "where", "limit",
        "offset", "group", "having", "union", "intersect", "except", "with", "without", "varying", "precision",
    };

    /// <summary>The words that continue a multi-word type name after its first: <c>character varying</c>, <c>timestamp without time zone</c>.</summary>
    private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal)
    {
        "varying", "precision", "without", "with", "time", "zone",
    };

    /// <summary>The column references in <paramref name="text" />, each once, in the order they first appear.</summary>
    /// <param name="text">A condition or a sort or group key, as <c>EXPLAIN</c> prints it.</param>
    public static IReadOnlyList<PlanConditionColumn> Columns(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxCharacters)
        {
            return [];
        }

        List<Token> tokens = Tokenize(text);
        Dictionary<int, string> castTypes = [];
        HashSet<int> typeTokens = [];

        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == Kind.Cast)
            {
                castTypes[i] = ReadType(tokens, i + 1, typeTokens);
            }
        }

        List<PlanConditionColumn> found = [];

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];

            if (typeTokens.Contains(i) || token.Kind is not (Kind.Word or Kind.Quoted))
            {
                continue;
            }

            string? qualifier = null;
            int start = i;
            int column = i;

            // alias.column: the word before a dot that another name follows is a qualifier, and the name after it the column.
            if (i + 2 < tokens.Count && tokens[i + 1] is { Kind: Kind.Punct, Punct: '.' } && tokens[i + 2].Kind is Kind.Word or Kind.Quoted)
            {
                qualifier = token.Text;
                column = i + 2;
                i += 2;
            }

            Token name = tokens[column];

            if (typeTokens.Contains(column) || IsKeywordOrCall(tokens, column))
            {
                continue;
            }

            // A reference that follows a dot (a record's field, a schema-qualified operator) with no name before it is not a column.
            if (start > 0 && tokens[start - 1] is { Kind: Kind.Punct, Punct: '.' })
            {
                continue;
            }

            (string? cast, string? function) = Wrapper(tokens, start, column, castTypes);
            PlanConditionColumn reference = new(qualifier, name.Text, cast, function);

            if (!found.Contains(reference))
            {
                found.Add(reference);
            }
        }

        return found;
    }

    /// <summary>Whether the bare word <paramref name="word" /> (a keyword: <c>or</c>) appears in <paramref name="text" />, outside every literal and quoted name.</summary>
    /// <param name="text">A condition, as <c>EXPLAIN</c> prints it.</param>
    /// <param name="word">The word, lower case.</param>
    public static bool HasWord(string? text, string word)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxCharacters)
        {
            return false;
        }

        return Tokenize(text).Any(x => x.Kind == Kind.Word && string.Equals(x.Text, word, StringComparison.Ordinal));
    }

    /// <summary>Whether the name at <paramref name="index" /> is a keyword, or a function (followed by an open parenthesis).</summary>
    private static bool IsKeywordOrCall(List<Token> tokens, int index)
    {
        Token token = tokens[index];

        if (token.Kind == Kind.Word && Keywords.Contains(token.Text))
        {
            return true;
        }

        return index + 1 < tokens.Count && tokens[index + 1] is { Kind: Kind.Punct, Punct: '(' };
    }

    /// <summary>The cast and the function the reference at <paramref name="column" /> sits in, when it does.</summary>
    private static (string? Cast, string? Function) Wrapper(List<Token> tokens, int start, int column, Dictionary<int, string> castTypes)
    {
        // Peel the parentheses that close right after the reference and open right before it: (col)::type, lower((col)::text).
        int peeled = 0;
        int after = column + 1;

        while (after < tokens.Count && start - 1 - peeled >= 0
            && tokens[after] is { Kind: Kind.Punct, Punct: ')' }
            && tokens[start - 1 - peeled] is { Kind: Kind.Punct, Punct: '(' })
        {
            peeled++;
            after++;
        }

        string? cast = null;

        if (after < tokens.Count && tokens[after].Kind == Kind.Cast && castTypes.TryGetValue(after, out string? type))
        {
            cast = type;
        }
        else
        {
            // No cast: the parentheses just peeled may be a function's own - lower(col) - which the check below reads at depth 0.
            peeled = 0;
        }

        int opener = start - 1 - peeled;
        string? function = null;

        if (opener >= 1
            && tokens[opener] is { Kind: Kind.Punct, Punct: '(' }
            && tokens[opener - 1] is { Kind: Kind.Word } called
            && !Keywords.Contains(called.Text))
        {
            function = called.Text;
        }

        return (cast, function);
    }

    /// <summary>Reads the type name after a <c>::</c> from <paramref name="from" />, marking its tokens, and returns it lower case, without a size.</summary>
    private static string ReadType(List<Token> tokens, int from, HashSet<int> typeTokens)
    {
        StringBuilder type = new();
        int i = from;

        if (i < tokens.Count && tokens[i].Kind is Kind.Word or Kind.Quoted)
        {
            type.Append(tokens[i].Text.ToLowerInvariant());
            typeTokens.Add(i);
            i++;

            // pg_catalog.int4 and public.mood: keep the last part.
            while (i + 1 < tokens.Count && tokens[i] is { Kind: Kind.Punct, Punct: '.' } && tokens[i + 1].Kind is Kind.Word or Kind.Quoted)
            {
                typeTokens.Add(i);
                typeTokens.Add(i + 1);
                type.Clear().Append(tokens[i + 1].Text.ToLowerInvariant());
                i += 2;
            }

            while (i < tokens.Count && tokens[i] is { Kind: Kind.Word } more && TypeWords.Contains(more.Text.ToLowerInvariant()))
            {
                type.Append(' ').Append(more.Text.ToLowerInvariant());
                typeTokens.Add(i);
                i++;
            }
        }

        // A size or a modifier, and array brackets.
        if (i < tokens.Count && tokens[i] is { Kind: Kind.Punct, Punct: '(' })
        {
            int depth = 0;

            while (i < tokens.Count)
            {
                typeTokens.Add(i);

                if (tokens[i] is { Kind: Kind.Punct, Punct: '(' })
                {
                    depth++;
                }
                else if (tokens[i] is { Kind: Kind.Punct, Punct: ')' } && --depth == 0)
                {
                    i++;
                    break;
                }

                i++;
            }
        }

        while (i + 1 < tokens.Count && tokens[i] is { Kind: Kind.Punct, Punct: '[' } && tokens[i + 1] is { Kind: Kind.Punct, Punct: ']' })
        {
            typeTokens.Add(i);
            typeTokens.Add(i + 1);
            type.Append("[]");
            i += 2;
        }

        // "time zone" continues a type only after time or timestamp; a bare "time" word was consumed above only when it followed one.
        return type.ToString();
    }

    private static List<Token> Tokenize(string text)
    {
        List<Token> tokens = [];
        int i = 0;

        while (i < text.Length && tokens.Count < MaxTokens)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '\'')
            {
                i = SkipLiteral(text, i, escapes: false);
                tokens.Add(new Token(Kind.Literal, string.Empty));
            }
            else if (c == '"')
            {
                (string name, int next) = ReadQuoted(text, i);
                tokens.Add(new Token(Kind.Quoted, name));
                i = next;
            }
            else if (c == ':' && i + 1 < text.Length && text[i + 1] == ':')
            {
                tokens.Add(new Token(Kind.Cast, "::"));
                i += 2;
            }
            else if (c == '$' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
            {
                int end = i + 1;

                while (end < text.Length && char.IsAsciiDigit(text[end]))
                {
                    end++;
                }

                tokens.Add(new Token(Kind.Param, text[i..end]));
                i = end;
            }
            else if (char.IsAsciiDigit(c))
            {
                int end = i;

                while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '.'))
                {
                    end++;
                }

                tokens.Add(new Token(Kind.Number, text[i..end]));
                i = end;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int end = i;

                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '$'))
                {
                    end++;
                }

                string word = text[i..end];

                // E'...' is an escape string: its backslashes escape, which a plain literal's do not.
                if (word.Length == 1 && (word[0] is 'E' or 'e') && end < text.Length && text[end] == '\'')
                {
                    i = SkipLiteral(text, end, escapes: true);
                    tokens.Add(new Token(Kind.Literal, string.Empty));
                }
                else
                {
                    tokens.Add(new Token(Kind.Word, word.ToLowerInvariant()));
                    i = end;
                }
            }
            else
            {
                tokens.Add(new Token(Kind.Punct, c.ToString(), c));
                i++;
            }
        }

        return tokens;
    }

    private static int SkipLiteral(string text, int open, bool escapes)
    {
        int i = open + 1;

        while (i < text.Length)
        {
            if (escapes && text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static (string Name, int Next) ReadQuoted(string text, int open)
    {
        StringBuilder name = new();
        int i = open + 1;

        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    name.Append('"');
                    i += 2;
                    continue;
                }

                return (name.ToString(), i + 1);
            }

            name.Append(text[i]);
            i++;
        }

        return (name.ToString(), text.Length);
    }
}
