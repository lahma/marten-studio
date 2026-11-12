using System.Globalization;
using System.Text;

namespace KStudio.Core.Predicates;

/// <summary>
/// The search grammar of decision 7.1, parsed by hand: a lexer and a recursive-descent parser, no package and no expression escape hatch.
/// Keywords are case-insensitive; precedence is <c>not</c> over <c>and</c> over <c>or</c>; a chain of <c>and</c> or <c>or</c> is one node.
/// The fields are <c>key</c>, <c>header</c>, <c>ce</c>, <c>any</c>, <c>value</c> and JSON paths; the last two need the payload grant, which
/// the compiler asks for, never the parser.
/// </summary>
internal static class PredicateParser
{
    /// <summary>The longest text accepted.</summary>
    public const int MaxTextLength = 2_000;

    /// <summary>The most clauses a text may hold.</summary>
    public const int MaxClauses = 32;

    /// <summary>The most values an <c>in</c> list may hold.</summary>
    public const int MaxListValues = 100;

    /// <summary>The deepest nesting of parentheses and <c>not</c>.</summary>
    public const int MaxNesting = 16;

    /// <summary>The longest literal.</summary>
    public const int MaxLiteralLength = 1_000;

    /// <summary>What a search link carries in place of a redacted literal.</summary>
    public const string RedactedToken = "<redacted>";

    /// <summary>The words the grammar reserves.</summary>
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "in", "is", "null", "true", "false", "key", "header", "ce", "any", "value",
    };

    /// <summary>Parses a search text. An empty text is valid and has no tree: it matches every record in the window.</summary>
    /// <param name="text">What the visitor typed.</param>
    public static PredicateParseResult Parse(string? text)
    {
        text ??= string.Empty;

        if (text.Length > MaxTextLength)
        {
            return Failure(0, text.Length, string.Create(CultureInfo.InvariantCulture, $"The query is {text.Length:N0} characters long; the most is 2,000."));
        }

        try
        {
            List<Token> tokens = Lexer.Lex(text);
            return new Parser(tokens).Run();
        }
        catch (ParseFailure failure)
        {
            return new PredicateParseResult(null, [failure.Error]);
        }
    }

    private static PredicateParseResult Failure(int start, int length, string message) =>
        new(null, [new PredicateError(start, length, message)]);

    private enum TokenKind
    {
        Word,
        String,
        Number,
        Operator,
        LeftParen,
        RightParen,
        LeftBracket,
        RightBracket,
        Comma,
        JsonPath,
        Placeholder,
        End,
    }

    private sealed record Token(TokenKind Kind, string Text, int Start, int Length, string? Value = null)
    {
        public bool IsWord(string keyword) => Kind == TokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

        public bool IsKeyword => Kind == TokenKind.Word && Keywords.Contains(Text);

        /// <summary>How the token reads in "Unexpected …": never the text of a string, a number or a name.</summary>
        public string Describe() =>
            Kind switch
            {
                TokenKind.Word when IsKeyword => "'" + Text.ToLowerInvariant() + "'",
                TokenKind.Word => "a name",
                TokenKind.String => "a string",
                TokenKind.Number => "a number",
                TokenKind.Operator => "'" + Text + "'",
                TokenKind.LeftParen => "'('",
                TokenKind.RightParen => "')'",
                TokenKind.LeftBracket => "'['",
                TokenKind.RightBracket => "']'",
                TokenKind.Comma => "','",
                TokenKind.JsonPath => "a JSON path",
                TokenKind.Placeholder => "a redacted value",
                _ => "the end",
            };
    }

    private sealed class ParseFailure(PredicateError error) : Exception("The search text is not valid.")
    {
        public PredicateError Error { get; } = error;
    }

    private static ParseFailure Fail(int start, int length, string message) => new(new PredicateError(start, length, message));

    private static class Lexer
    {
        public static List<Token> Lex(string text)
        {
            List<Token> tokens = [];
            int i = 0;

            while (i < text.Length)
            {
                char c = text[i];

                if (c is ' ' or '\t' or '\r' or '\n')
                {
                    i++;
                    continue;
                }

                if (c == '<' && string.CompareOrdinal(text, i, RedactedToken, 0, RedactedToken.Length) == 0)
                {
                    tokens.Add(new Token(TokenKind.Placeholder, RedactedToken, i, RedactedToken.Length));
                    i += RedactedToken.Length;
                    continue;
                }

                switch (c)
                {
                    case '(':
                        tokens.Add(new Token(TokenKind.LeftParen, "(", i, 1));
                        i++;
                        continue;
                    case ')':
                        tokens.Add(new Token(TokenKind.RightParen, ")", i, 1));
                        i++;
                        continue;
                    case '[':
                        tokens.Add(new Token(TokenKind.LeftBracket, "[", i, 1));
                        i++;
                        continue;
                    case ']':
                        tokens.Add(new Token(TokenKind.RightBracket, "]", i, 1));
                        i++;
                        continue;
                    case ',':
                        tokens.Add(new Token(TokenKind.Comma, ",", i, 1));
                        i++;
                        continue;
                    case '=':
                        tokens.Add(new Token(TokenKind.Operator, "=", i, 1));
                        i++;
                        continue;
                    case '~':
                        tokens.Add(new Token(TokenKind.Operator, "~", i, 1));
                        i++;
                        continue;
                    case '^' when i + 1 < text.Length && text[i + 1] == '=':
                        tokens.Add(new Token(TokenKind.Operator, "^=", i, 2));
                        i += 2;
                        continue;
                    case '<' or '>':
                        if (i + 1 < text.Length && text[i + 1] == '=')
                        {
                            tokens.Add(new Token(TokenKind.Operator, c + "=", i, 2));
                            i += 2;
                        }
                        else
                        {
                            tokens.Add(new Token(TokenKind.Operator, c.ToString(), i, 1));
                            i++;
                        }

                        continue;
                    case '\'' or '"':
                        tokens.Add(LexString(text, ref i));
                        continue;
                    case '$':
                        tokens.Add(LexJsonPath(text, ref i));
                        continue;
                }

                if (char.IsAsciiDigit(c) || (c == '-' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1])))
                {
                    tokens.Add(LexNumber(text, ref i));
                    continue;
                }

                if (char.IsAsciiLetter(c) || c == '_')
                {
                    int start = i;

                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Word, text[start..i], start, i - start));
                    continue;
                }

                throw Fail(i, 1, string.Create(CultureInfo.InvariantCulture, $"Unexpected character at position {i + 1}."));
            }

            tokens.Add(new Token(TokenKind.End, string.Empty, text.Length, 0));
            return tokens;
        }

        private static Token LexString(string text, ref int i)
        {
            int start = i;
            char quote = text[i++];
            StringBuilder value = new();

            while (true)
            {
                if (i >= text.Length)
                {
                    throw Fail(start, text.Length - start, string.Create(CultureInfo.InvariantCulture, $"The string starting at position {start + 1} has no closing quote."));
                }

                char c = text[i];

                if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote)
                    {
                        value.Append(quote);
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                value.Append(c);
                i++;
            }

            if (value.Length > MaxLiteralLength)
            {
                throw Fail(start, i - start, string.Create(CultureInfo.InvariantCulture, $"The value at position {start + 1} is {value.Length:N0} characters long; the most is 1,000."));
            }

            return new Token(TokenKind.String, string.Empty, start, i - start, value.ToString());
        }

        private static Token LexNumber(string text, ref int i)
        {
            int start = i;

            if (text[i] == '-')
            {
                i++;
            }

            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
            {
                i++;

                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }
            }

            if (i < text.Length && text[i] is 'e' or 'E')
            {
                int exponent = i + 1;

                if (exponent < text.Length && text[exponent] is '+' or '-')
                {
                    exponent++;
                }

                if (exponent < text.Length && char.IsAsciiDigit(text[exponent]))
                {
                    i = exponent;

                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }
                }
            }

            return new Token(TokenKind.Number, text[start..i], start, i - start);
        }

        private static Token LexJsonPath(string text, ref int i)
        {
            int start = i;
            i++;

            while (i < text.Length)
            {
                char c = text[i];

                if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '*' or ']')
                {
                    i++;
                    continue;
                }

                if (c != '[')
                {
                    break;
                }

                i++;

                if (i < text.Length && text[i] is '\'' or '"')
                {
                    // A bracketed member name: any characters, a doubled quote for the quote itself. Without a closing quote the token
                    // runs to the end, and the path parser says the '[' has no ']'.
                    char quote = text[i++];

                    while (i < text.Length)
                    {
                        if (text[i] == quote)
                        {
                            if (i + 1 < text.Length && text[i + 1] == quote)
                            {
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        i++;
                    }
                }
                else
                {
                    // An index or [*]; letters, signs and dots are taken too, so that `[x]` and `[-1]` get the index message.
                    while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '*' or '-' or '+' or '.'))
                    {
                        i++;
                    }
                }
            }

            return new Token(TokenKind.JsonPath, text[start..i], start, i - start);
        }
    }

    private sealed class Parser(List<Token> tokens)
    {
        private const string FieldExpectation = "a field (key, header.<name>, ce.<attribute>, any, value or a $.json.path)";

        private int position;
        private int depth;
        private int clauses;

        private Token Current => tokens[position];

        public PredicateParseResult Run()
        {
            if (Current.Kind == TokenKind.End)
            {
                return new PredicateParseResult(null, []);
            }

            PredicateNode tree = ParseOr();

            if (Current.Kind != TokenKind.End)
            {
                throw Unexpected("'and', 'or' or the end");
            }

            if (clauses > MaxClauses)
            {
                throw Fail(0, tokens[^1].Start, string.Create(CultureInfo.InvariantCulture, $"The query has {clauses} conditions; the most is 32."));
            }

            return new PredicateParseResult(tree, []);
        }

        private ParseFailure Unexpected(string expected) =>
            Fail(
                Current.Start,
                Math.Max(1, Current.Length),
                string.Create(CultureInfo.InvariantCulture, $"Unexpected {Current.Describe()} at position {Current.Start + 1}: expected {expected}."));

        private PredicateNode ParseOr()
        {
            List<PredicateNode> children = [];
            AddFlat<OrNode>(children, ParseAnd());

            while (Current.IsWord("or"))
            {
                position++;
                AddFlat<OrNode>(children, ParseAnd());
            }

            return children.Count == 1 ? children[0] : new OrNode(children, children[0].Start, End(children[^1]) - children[0].Start);
        }

        private PredicateNode ParseAnd()
        {
            List<PredicateNode> children = [];
            AddFlat<AndNode>(children, ParseNot());

            while (Current.IsWord("and"))
            {
                position++;
                AddFlat<AndNode>(children, ParseNot());
            }

            return children.Count == 1 ? children[0] : new AndNode(children, children[0].Start, End(children[^1]) - children[0].Start);
        }

        /// <summary>Adds a child; a parenthesised chain of the same operator is spliced in, so the tree is as flat as the text without the parentheses.</summary>
        private static void AddFlat<T>(List<PredicateNode> children, PredicateNode child)
            where T : PredicateNode
        {
            if (child is T)
            {
                children.AddRange(child switch
                {
                    OrNode or => or.Children,
                    AndNode and => and.Children,
                    _ => [],
                });
            }
            else
            {
                children.Add(child);
            }
        }

        private PredicateNode ParseNot()
        {
            if (Current.IsWord("not"))
            {
                Token not = Current;
                Enter(not);
                position++;
                PredicateNode operand = ParseNot();
                depth--;
                return new NotNode(operand, not.Start, End(operand) - not.Start);
            }

            return ParsePrimary();
        }

        private PredicateNode ParsePrimary()
        {
            if (Current.Kind == TokenKind.LeftParen)
            {
                Token open = Current;
                Enter(open);
                position++;
                PredicateNode inner = ParseOr();

                if (Current.Kind != TokenKind.RightParen)
                {
                    throw Fail(open.Start, 1, string.Create(CultureInfo.InvariantCulture, $"The '(' at position {open.Start + 1} has no matching ')'."));
                }

                position++;
                depth--;
                return inner;
            }

            return ParseClause();
        }

        private void Enter(Token at)
        {
            depth++;

            if (depth > MaxNesting)
            {
                throw Fail(at.Start, Math.Max(1, at.Length), string.Create(CultureInfo.InvariantCulture, $"The query nests more than 16 levels at position {at.Start + 1}."));
            }
        }

        private ClauseNode ParseClause()
        {
            FieldNode field = ParseField();
            clauses++;
            Token next = Current;

            if (next.Kind == TokenKind.Operator)
            {
                position++;
                LiteralNode literal = ParseLiteral();
                PredicateOperator op = next.Text switch
                {
                    "=" => PredicateOperator.Equals,
                    "^=" => PredicateOperator.Prefix,
                    "~" => PredicateOperator.Contains,
                    "<" => PredicateOperator.Less,
                    "<=" => PredicateOperator.LessOrEqual,
                    ">" => PredicateOperator.Greater,
                    _ => PredicateOperator.GreaterOrEqual,
                };

                return new ClauseNode(field, op, [literal], field.Start, End(literal) - field.Start);
            }

            if (next.IsWord("in"))
            {
                position++;

                if (Current.Kind != TokenKind.LeftParen)
                {
                    throw Unexpected("'('");
                }

                position++;
                List<LiteralNode> literals = [ParseLiteral()];

                while (Current.Kind == TokenKind.Comma)
                {
                    position++;
                    literals.Add(ParseLiteral());
                }

                if (Current.Kind != TokenKind.RightParen)
                {
                    throw Unexpected("',' or ')'");
                }

                Token close = Current;
                position++;

                if (literals.Count > MaxListValues)
                {
                    throw Fail(
                        literals[0].Start,
                        close.Start - literals[0].Start,
                        string.Create(CultureInfo.InvariantCulture, $"The list at position {literals[0].Start + 1} has {literals.Count} values; the most is 100."));
                }

                return new ClauseNode(field, PredicateOperator.In, literals, field.Start, close.Start + 1 - field.Start);
            }

            if (next.IsWord("is"))
            {
                position++;
                bool not = false;

                if (Current.IsWord("not"))
                {
                    not = true;
                    position++;
                }

                if (!Current.IsWord("null"))
                {
                    throw Unexpected("'null'");
                }

                Token last = Current;
                position++;
                return new ClauseNode(
                    field,
                    not ? PredicateOperator.IsNotNull : PredicateOperator.IsNull,
                    [],
                    field.Start,
                    last.Start + last.Length - field.Start);
            }

            throw Unexpected("an operator ('=', '^=', '~', '<', '<=', '>', '>=', 'in' or 'is')");
        }

        private FieldNode ParseField()
        {
            Token token = Current;

            if (token.Kind == TokenKind.JsonPath)
            {
                if (!JsonPathExpression.TryParse(token.Text, token.Start, out JsonPathExpression? path, out PredicateError? problem))
                {
                    throw new ParseFailure(problem!);
                }

                position++;
                return new JsonPathField(path!, token.Start, token.Length);
            }

            if (token.Kind != TokenKind.Word)
            {
                throw Unexpected(FieldExpectation);
            }

            string word = token.Text;

            if (word.Equals("key", StringComparison.OrdinalIgnoreCase))
            {
                position++;
                return new KeyField(token.Start, token.Length);
            }

            if (word.Equals("any", StringComparison.OrdinalIgnoreCase))
            {
                position++;
                return new AnyField(token.Start, token.Length);
            }

            if (word.Equals("value", StringComparison.OrdinalIgnoreCase))
            {
                position++;
                return new ValueField(token.Start, token.Length);
            }

            if (word.StartsWith("header.", StringComparison.OrdinalIgnoreCase) && word.Length > "header.".Length)
            {
                position++;
                return new HeaderField(word["header.".Length..], token.Start, token.Length);
            }

            if (word.Equals("header", StringComparison.OrdinalIgnoreCase) && tokens[position + 1].Kind == TokenKind.LeftBracket)
            {
                position += 2;

                if (Current.Kind != TokenKind.String)
                {
                    throw Unexpected("a string");
                }

                string name = Current.Value!;
                position++;

                if (Current.Kind != TokenKind.RightBracket)
                {
                    throw Unexpected("']'");
                }

                Token close = Current;
                position++;

                if (name.Length == 0)
                {
                    throw Fail(token.Start, close.Start + 1 - token.Start, string.Create(CultureInfo.InvariantCulture, $"The header name at position {token.Start + 1} is empty."));
                }

                return new HeaderField(name, token.Start, close.Start + 1 - token.Start);
            }

            if (word.StartsWith("ce.", StringComparison.OrdinalIgnoreCase) && IsAttribute(word["ce.".Length..]))
            {
                position++;
                return new CloudEventField(word["ce.".Length..], token.Start, token.Length);
            }

            // `header` and `ce` on their own are fields that were not finished, so they get the message that lists the fields.
            if (token.IsKeyword && !word.Equals("header", StringComparison.OrdinalIgnoreCase) && !word.Equals("ce", StringComparison.OrdinalIgnoreCase))
            {
                throw Unexpected(FieldExpectation);
            }

            string shown = word.Length > 64 ? word[..64] : word;
            throw Fail(
                token.Start,
                token.Length,
                string.Create(CultureInfo.InvariantCulture, $"Unknown field '{shown}' at position {token.Start + 1}. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths."));
        }

        private static bool IsAttribute(string text)
        {
            if (text.Length is < 1 or > 64)
            {
                return false;
            }

            foreach (char c in text)
            {
                if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
                {
                    return false;
                }
            }

            return true;
        }

        private LiteralNode ParseLiteral()
        {
            Token token = Current;

            switch (token.Kind)
            {
                case TokenKind.String:
                    position++;
                    return new StringLiteral(token.Value!, token.Start, token.Length);

                case TokenKind.Number:
                    position++;

                    if (!decimal.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number))
                    {
                        throw Fail(token.Start, token.Length, string.Create(CultureInfo.InvariantCulture, $"The number at position {token.Start + 1} is out of range."));
                    }

                    return new NumberLiteral(number, token.Start, token.Length);

                case TokenKind.Placeholder:
                    throw Fail(
                        token.Start,
                        token.Length,
                        string.Create(CultureInfo.InvariantCulture, $"The link left out a redacted value at position {token.Start + 1}: type it in."));

                case TokenKind.Word when token.IsWord("null"):
                    position++;
                    return new NullLiteral(token.Start, token.Length);

                case TokenKind.Word when token.IsWord("true"):
                    position++;
                    return new BoolLiteral(true, token.Start, token.Length);

                case TokenKind.Word when token.IsWord("false"):
                    position++;
                    return new BoolLiteral(false, token.Start, token.Length);

                default:
                    throw Unexpected("a value");
            }
        }

        private static int End(PredicateNode node) => node.Start + node.Length;

        private static int End(LiteralNode node) => node.Start + node.Length;
    }
}
