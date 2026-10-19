using System.Reflection;
using System.Text.RegularExpressions;

namespace MartenStudio.Services.Query;

/// <summary>
/// The first of LINQ mode's three walls: a lexical screen over the text, run before any parser sees it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a screen at all, when JasperFx and the studio both walk the parsed tree.</b> The parse itself runs
/// code. System.Linq.Dynamic.Core 1.7.4 (read at its <c>v1.7.4</c> tag) evaluates a few things while it parses,
/// before any allow-list exists to look at the result: a <see cref="System.ComponentModel.TypeConverter" /> for a
/// string literal compared with a member, or named in type syntax (<c>Guid("…")</c>, <c>Uri("…")</c>); an indexer
/// on a constant with constant arguments, compiled and invoked (<c>ExpressionParser.ParseElementAccess</c>); enum
/// parsing; and type lookups over every public enum in the core library. Each of those is harmless for the shapes
/// this screen lets through, and the screen is what makes "the shapes this screen lets through" a short list:
/// </para>
/// <list type="bullet">
/// <item><description>
/// every identifier is bound where the parser will bind it - a bare name on the current <c>it</c>, a name after a
/// <c>.</c> on the type of what is before it - and must be the graph's member there
/// (<see cref="LinqDocumentGraph.Bind" />), a keyword on a short list, one of a few methods of the receiver's own
/// type directly after a <c>.</c> and before a <c>(</c>, or <c>StringComparison.X</c>. So no type name, no
/// <c>new</c>, <c>iif</c>, <c>np</c>, <c>is</c>, <c>as</c>, <c>cast</c>, <c>parent</c>, <c>root</c>, no operator
/// alias, and no member the graph left out - whatever names other types in the graph allow - reaches the parser;
/// </description></item>
/// <item><description>
/// no two values in a row, which is how Dynamic LINQ spells a type-name conversion of a string
/// (<c>Uri "http://…"</c>);
/// </description></item>
/// <item><description>
/// no <c>@n</c> placeholder (the studio passes no arguments), no <c>$</c>, <c>^</c> or <c>~</c> (Dynamic LINQ's
/// <c>it</c>, <c>parent</c> and <c>root</c> symbols), no <c>[</c> (so no indexer), no <c>{</c>, no <c>?</c>
/// (no conditional, no null propagation, no coalescing), no <c>=&gt;</c> (no lambda of the visitor's own), no
/// arithmetic but <c>%</c>, and no <c>&amp;</c> or <c>|</c> on their own (Dynamic LINQ's string concatenation
/// and bitwise operators);
/// </description></item>
/// <item><description>
/// a string literal has no backslash in it. Dynamic LINQ's tokenizer skips the character after <c>\"</c> twice
/// (<c>TextParser.NextToken</c>), so a screen that read escapes any differently would disagree with the parser
/// about where a literal ends - which is the one disagreement that could hide a token from it. With no
/// backslash both read the same thing: a literal ends at the next quote of its own kind. A single-quoted literal
/// is a character and must be one character long (<c>ExpressionParser.ParseStringLiteral</c>), and a string
/// directly before <c>(</c> - Dynamic LINQ's cast-by-type-name syntax - is refused;
/// </description></item>
/// <item><description>
/// and the text is short and shallow: <see cref="LinqQueryLimits.MaxWhereLength" />,
/// <see cref="LinqQueryLimits.MaxTokens" />, <see cref="LinqQueryLimits.MaxDepth" /> and
/// <see cref="LinqQueryLimits.MaxUnaryRun" />. The parser is recursive descent, and a stack overflow is the one
/// failure a .NET process cannot catch.
/// </description></item>
/// </list>
/// <para>
/// It is deliberately stricter than the parser, never looser: anything it cannot classify is refused, with a
/// position the editor puts a caret under. It is the <em>only</em> wall in front of the parse itself - JasperFx's
/// rules and <see cref="LinqExpressionGuard" /> both walk the tree Dynamic LINQ has already built, so whatever the
/// parser evaluated while building it has already run. They are the walls in front of Marten.
/// </para>
/// </remarks>
internal static partial class LinqTextScreen
{
    /// <summary>The keywords a predicate may use.</summary>
    internal static readonly IReadOnlySet<string> WhereKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "and", "or", "not", "null", "true", "false", "it", "in" };

    /// <summary>The keywords that are operators, and so may stand before an opening parenthesis.</summary>
    private static readonly HashSet<string> OperatorKeywords = new(StringComparer.OrdinalIgnoreCase) { "and", "or", "not", "in" };

    /// <summary>The keywords an ordering may use, each after a member path.</summary>
    internal static readonly IReadOnlySet<string> OrderKeywords =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "asc", "desc", "ascending", "descending" };

    /// <summary>
    /// The methods a predicate may call, each only directly after a <c>.</c> and before a <c>(</c>. What each may be
    /// called on is the guard's question; this is only the vocabulary.
    /// </summary>
    internal static readonly IReadOnlySet<string> Methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "StartsWith", "EndsWith", "Contains", "Equals", "ToLower", "ToUpper", "ToLowerInvariant", "ToUpperInvariant",
        "Any", "All", "Count",
    };

    /// <summary>Members of framework types a predicate may read: a string's or an array's length, a count, a nullable's value.</summary>
    internal static readonly IReadOnlySet<string> FrameworkMembers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Length", "Count", "HasValue", "Value" };

    /// <summary>The one enum a predicate may name: the comparison a string method takes.</summary>
    internal const string StringComparisonName = "StringComparison";

    /// <summary>Its members.</summary>
    internal static readonly IReadOnlySet<string> StringComparisonMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Ordinal", "OrdinalIgnoreCase", "InvariantCulture", "InvariantCultureIgnoreCase", "CurrentCulture",
        "CurrentCultureIgnoreCase",
    };

    /// <summary>
    /// Words Dynamic LINQ reads as something other than a member wherever they stand - its functions, its context
    /// keywords and its operator aliases (1.7.4, <c>KeywordsHelper</c> and <c>TextParser</c>) - plus a few C# words
    /// somebody might reach for. Refused even when the document happens to have a member of that name, because the
    /// parser would not read it as the member.
    /// </summary>
    internal static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "new", "iif", "isnull", "np", "is", "as", "cast", "parent", "root",
        "eq", "equal", "ne", "notequal", "neq", "lt", "lessthan", "le", "lessthanequal", "gt", "greaterthan", "ge",
        "greaterthanequal", "andalso", "orelse", "mod",
        "typeof", "default", "nameof", "sizeof", "checked", "unchecked", "this", "base", "select",
    };

    /// <summary>The string methods a predicate may call, and the type each produces.</summary>
    private static readonly Dictionary<string, Type> StringMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["StartsWith"] = typeof(bool),
        ["EndsWith"] = typeof(bool),
        ["Contains"] = typeof(bool),
        ["Equals"] = typeof(bool),
        ["ToLower"] = typeof(string),
        ["ToUpper"] = typeof(string),
        ["ToLowerInvariant"] = typeof(string),
        ["ToUpperInvariant"] = typeof(string),
    };

    /// <summary>The collection methods a predicate may call, and the type each produces.</summary>
    private static readonly Dictionary<string, Type> CollectionMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Any"] = typeof(bool),
        ["All"] = typeof(bool),
        ["Contains"] = typeof(bool),
        ["Count"] = typeof(int),
    };

    /// <summary>
    /// Screens one box's text against the document's graph. <see langword="null" /> means it may go to the parser.
    /// </summary>
    /// <param name="clause">Which box: the predicate or the ordering.</param>
    /// <param name="text">What was typed. Blank is allowed - it means "no filter" or "the default order".</param>
    /// <param name="graph">The document's graph, which every name is bound against, receiver by receiver.</param>
    public static LinqRefusal? Check(LinqClause clause, string? text, LinqDocumentGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int limit = clause == LinqClause.OrderBy ? LinqQueryLimits.MaxOrderByLength : LinqQueryLimits.MaxWhereLength;
        if (text.Length > limit)
        {
            return Refuse(clause, limit, $"is {text.Length} characters long; LINQ mode accepts at most {limit}.");
        }

        var tokens = new List<Token>();

        if (Tokenize(clause, text, tokens) is { } lexical)
        {
            return lexical;
        }

        if (tokens.Count > LinqQueryLimits.MaxTokens)
        {
            return Refuse(clause, tokens[LinqQueryLimits.MaxTokens].Position,
                $"has more than {LinqQueryLimits.MaxTokens} tokens; write a shorter predicate.");
        }

        return clause == LinqClause.OrderBy
            ? CheckOrdering(tokens, graph)
            : CheckPredicate(tokens, graph);
    }

    // ---------------------------------------------------------------------------------------------------
    // The predicate
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The predicate, walked with the type of every value it reads, so that each name is bound where Dynamic LINQ
    /// will bind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Receiver by receiver, never against one list of names.</b> A bare name binds on the current <c>it</c> - the
    /// document, or inside <c>Any(…)</c>, <c>All(…)</c> and <c>Count(…)</c> the collection's element (but not inside
    /// <c>Contains(…)</c>, which Dynamic LINQ reads where the call is) - and a name after a <c>.</c> binds on the type of
    /// what is before it. Each is bound with
    /// <see cref="LinqDocumentGraph.Bind" />, which finds the member exactly as the parser's
    /// <c>FindPropertyOrField</c> does and then requires it to be the graph's. A flat set of every name the graph
    /// holds anywhere would let <c>Code</c> through on a type whose own <c>Code</c> the graph left out for its
    /// converter, because some other type's <c>Code</c> is allowed - and the parser would bind the left-out one and
    /// run its converter on the literal (the F7 security review's B1). The framework members (a string's
    /// <c>Length</c>, a collection's <c>Count</c>, a nullable's <c>HasValue</c> and <c>Value</c>) are allowed only on
    /// a receiver of that framework type, for the same reason.
    /// </para>
    /// <para>
    /// <b>No two values in a row.</b> Dynamic LINQ reads a type name followed by a string as a conversion of the
    /// string (<c>Uri "http://…"</c>, <c>ParseTypeAccess</c>), which runs a converter at parse time. A name, a string, a
    /// number or a closing parenthesis followed by another of those, or by an opening parenthesis, is refused before
    /// the parser can read it that way.
    /// </para>
    /// </remarks>
    private static LinqRefusal? CheckPredicate(List<Token> tokens, LinqDocumentGraph graph)
    {
        var frames = new Stack<Frame>();
        frames.Push(new Frame(graph.Root, Returns: null));

        int unaryRun = 0;
        bool previousEndsValue = false;
        Type? readable = null;
        Frame? pendingCall = null;

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            Token? previous = i > 0 ? tokens[i - 1] : null;
            Token? next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            bool afterDot = previous?.Kind == TokenKind.Dot;

            bool isUnary = (token.Kind == TokenKind.Operator && token.Text == "!")
                || (token.Kind == TokenKind.Identifier && !afterDot && string.Equals(token.Text, "not", StringComparison.OrdinalIgnoreCase));

            unaryRun = isUnary ? unaryRun + 1 : 0;
            if (unaryRun > LinqQueryLimits.MaxUnaryRun)
            {
                return Refuse(LinqClause.Where, token.Position,
                    $"negates more than {LinqQueryLimits.MaxUnaryRun} times in a row.");
            }

            // A reserved word is said to be one wherever it stands, before it is taken for a value.
            if (token.Kind == TokenKind.Identifier && Reserved.Contains(token.Text))
            {
                return ReservedWord(token);
            }

            bool startsValue = token.Kind is TokenKind.Number or TokenKind.String or TokenKind.Open
                || (token.Kind == TokenKind.Identifier && !afterDot && !OperatorKeywords.Contains(token.Text));

            if (previousEndsValue && startsValue)
            {
                return Refuse(LinqClause.Where, token.Position, token.Kind == TokenKind.Open
                    ? "has a '(' straight after a value; only a method called on a member opens one there."
                    : "has two values in a row with nothing between them; join them with an operator such as == or and.");
            }

            switch (token.Kind)
            {
                case TokenKind.Open:
                    frames.Push(pendingCall ?? new Frame(frames.Peek().It, Returns: null));
                    pendingCall = null;

                    if (frames.Count - 1 > LinqQueryLimits.MaxDepth)
                    {
                        return Refuse(LinqClause.Where, token.Position,
                            $"nests parentheses more than {LinqQueryLimits.MaxDepth} deep.");
                    }

                    readable = null;
                    previousEndsValue = false;
                    break;

                case TokenKind.Close:
                    if (frames.Count == 1)
                    {
                        return Refuse(LinqClause.Where, token.Position, "closes a parenthesis that was never opened.");
                    }

                    // A method call's result can be read further (Name.ToLower().Length); a grouped expression cannot.
                    readable = frames.Pop().Returns;
                    previousEndsValue = true;
                    break;

                case TokenKind.Dot:
                    if (readable is null || next?.Kind != TokenKind.Identifier)
                    {
                        return Refuse(LinqClause.Where, token.Position,
                            "has a '.' that is not between a member and the member or method it reads.");
                    }

                    previousEndsValue = false;
                    break;

                case TokenKind.Number when afterDot:
                    return Refuse(LinqClause.Where, token.Position, "has a number where a member name belongs.");

                // A string directly before '(' is Dynamic LINQ's type-cast syntax ("System.DateTime"(x)). JasperFx
                // turns type names in strings off; the screen does not let the shape through to find out.
                case TokenKind.String when next?.Kind == TokenKind.Open:
                    return Refuse(LinqClause.Where, token.Position, "puts a string before '(', which Dynamic LINQ reads as a cast.");

                case TokenKind.Number or TokenKind.String:
                    readable = null;
                    previousEndsValue = true;
                    break;

                case TokenKind.Identifier:
                {
                    Binding binding = BindIdentifier(tokens, i, afterDot ? readable : null, frames.Peek().It, graph);

                    if (binding.Refusal is { } refusal)
                    {
                        return refusal;
                    }

                    readable = binding.Readable;
                    pendingCall = binding.Call;
                    previousEndsValue = binding.EndsValue;
                    break;
                }

                default:
                    readable = null;
                    previousEndsValue = false;
                    break;
            }
        }

        return frames.Count == 1
            ? null
            : Refuse(LinqClause.Where, tokens[^1].Position, "leaves a parenthesis open.");
    }

    /// <summary>One identifier, bound: what it lets the next <c>.</c> read, the call it opens, and whether it ends a value.</summary>
    private static Binding BindIdentifier(List<Token> tokens, int index, Type? receiver, Type it, LinqDocumentGraph graph)
    {
        Token token = tokens[index];
        string word = token.Text;
        bool afterDot = index > 0 && tokens[index - 1].Kind == TokenKind.Dot;
        bool beforeOpen = index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Open;
        bool beforeDot = index + 1 < tokens.Count && tokens[index + 1].Kind == TokenKind.Dot;

        if (Reserved.Contains(word))
        {
            return Binding.Refused(ReservedWord(token));
        }

        if (afterDot)
        {
            return receiver is null
                ? Binding.Refused(Refuse(LinqClause.Where, token.Position, $"reads '{word}' from something that has no members."))
                : beforeOpen
                    ? BindMethod(token, receiver, it)
                    : BindMember(token, receiver, graph);
        }

        // An operator keyword may open a parenthesis - `Status in ("Open", "Shipped")`, `not (Rush or Express)` -
        // and is not a call.
        if (OperatorKeywords.Contains(word))
        {
            return new Binding(null, null, null, EndsValue: false);
        }

        if (beforeOpen)
        {
            return Binding.Refused(Refuse(LinqClause.Where, token.Position,
                $"calls '{word}' with no receiver; a method is called on a member, as in Name.StartsWith(\"A\")."));
        }

        if (word.Equals("true", StringComparison.OrdinalIgnoreCase)
            || word.Equals("false", StringComparison.OrdinalIgnoreCase)
            || word.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return new Binding(null, null, null, EndsValue: true);
        }

        // `it` is the document, or inside Any(…) the element. Read through (it.Name) it is a chain; on its own it is
        // the element of a collection of scalars (Tags.Any(it == "vip")) and nothing else - an object compared with a
        // literal is a converter call on that object's type.
        if (word.Equals("it", StringComparison.OrdinalIgnoreCase))
        {
            return beforeDot || LinqDocumentGraph.IsScalar(it)
                ? new Binding(null, it, null, EndsValue: true)
                : Binding.Refused(Refuse(LinqClause.Where, token.Position,
                    "uses 'it' as a value where it is not a text, a number or a date; compare one of its members instead."));
        }

        // A bare name binds on `it` first: Dynamic LINQ prefers a member to a type of the same name
        // (PrioritizePropertyOrFieldOverTheType), and asks the very question Bind asks.
        if (graph.Bind(it, word) is { } member)
        {
            return new Binding(null, member.MemberType, null, EndsValue: true);
        }

        if (string.Equals(word, StringComparisonName, StringComparison.OrdinalIgnoreCase))
        {
            return beforeDot
                ? new Binding(null, typeof(StringComparisonValues), null, EndsValue: true)
                : Binding.Refused(Refuse(LinqClause.Where, token.Position, "names StringComparison without one of its values."));
        }

        return Binding.Refused(Refuse(LinqClause.Where, token.Position,
            $"names '{word}', which is not a member LINQ mode can query here."));
    }

    private static LinqRefusal ReservedWord(Token token) =>
        Refuse(LinqClause.Where, token.Position,
            $"uses '{token.Text}', which Dynamic LINQ reads as a function, a keyword or an operator rather than a member; LINQ mode does not allow it.");

    private static Binding BindMember(Token token, Type receiver, LinqDocumentGraph graph)
    {
        string word = token.Text;

        if (receiver == typeof(StringComparisonValues))
        {
            return StringComparisonMembers.Contains(word)
                ? new Binding(null, null, null, EndsValue: true)
                : Binding.Refused(Refuse(LinqClause.Where, token.Position, $"names '{word}', which is not a StringComparison value."));
        }

        Type? framework = FrameworkMember(receiver, word);

        if (framework is not null)
        {
            return new Binding(null, framework, null, EndsValue: true);
        }

        return graph.Bind(receiver, word) is { } member
            ? new Binding(null, member.MemberType, null, EndsValue: true)
            : Binding.Refused(Refuse(LinqClause.Where, token.Position, $"reads '{word}', which is not a member LINQ mode can query there."));
    }

    /// <summary>
    /// The type a framework member produces, when <paramref name="word" /> is one of the four LINQ mode reads and the
    /// receiver is of the framework type that has it: a string's <c>Length</c>, an array's <c>Length</c>, a
    /// collection's <c>Count</c>, a nullable's <c>HasValue</c> and <c>Value</c>. Bound as the parser binds it, and
    /// required to be the framework's own.
    /// </summary>
    private static Type? FrameworkMember(Type receiver, string word)
    {
        if (!FrameworkMembers.Contains(word))
        {
            return null;
        }

        bool owned = receiver == typeof(string)
                     || Nullable.GetUnderlyingType(receiver) is not null
                     || LinqDocumentGraph.Classify(receiver) == LinqTypeKind.Collection;

        if (!owned || LinqDocumentGraph.FindAsDynamicLinq(receiver, word) is not PropertyInfo property
            || property.DeclaringType?.Namespace is not ("System" or "System.Collections.Generic" or "System.Collections.ObjectModel"))
        {
            return null;
        }

        return property.PropertyType;
    }

    private static Binding BindMethod(Token token, Type receiver, Type it)
    {
        string word = token.Text;

        if (receiver == typeof(string) && StringMethods.TryGetValue(word, out Type? produces))
        {
            // A string method's arguments are read where the call is: the same `it`.
            return new Binding(null, null, new Frame(it, produces), EndsValue: false);
        }

        if (LinqDocumentGraph.Classify(receiver) == LinqTypeKind.Collection
            && LinqDocumentGraph.ElementTypeOf(receiver) is { } element
            && CollectionMethods.TryGetValue(word, out Type? counted))
        {
            // A collection method's condition is read against the element - inside Lines.Any(…), `Quantity` is the
            // line's - except Contains, whose argument Dynamic LINQ reads where the call is (TryParseEnumerable scopes
            // Contains, ContainsKey, Skip and Take to the outer `it`).
            Type scope = word.Equals("Contains", StringComparison.OrdinalIgnoreCase) ? it : element;

            return new Binding(null, null, new Frame(scope, counted), EndsValue: false);
        }

        return Binding.Refused(Refuse(LinqClause.Where, token.Position, Methods.Contains(word)
            ? $"calls '{word}' on something it cannot be called on here."
            : $"calls '{word}', which is not one of the methods LINQ mode allows: {string.Join(", ", Methods.Order(StringComparer.Ordinal))}."));
    }

    // ---------------------------------------------------------------------------------------------------
    // The ordering
    // ---------------------------------------------------------------------------------------------------

    private static LinqRefusal? CheckOrdering(List<Token> tokens, LinqDocumentGraph graph)
    {
        int keys = 1;
        bool expectMember = true;

        // Each key is a member path from the document, every step bound on the type of the step before it, ending at
        // a value that can be sorted.
        Type receiver = graph.Root;
        int keyStart = 0;

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];

            switch (token.Kind)
            {
                case TokenKind.Identifier when expectMember:
                    if (Reserved.Contains(token.Text) || OrderKeywords.Contains(token.Text) || graph.Bind(receiver, token.Text) is not { } member)
                    {
                        return Refuse(LinqClause.OrderBy, token.Position,
                            $"names '{token.Text}', which is not a member of this document type that can be ordered by.");
                    }

                    if (ReferenceEquals(receiver, graph.Root))
                    {
                        keyStart = token.Position;
                    }

                    receiver = member.MemberType;
                    expectMember = false;
                    break;

                case TokenKind.Dot when !expectMember:
                    expectMember = true;
                    break;

                case TokenKind.Identifier when OrderKeywords.Contains(token.Text):
                    if (SortableKey(receiver, keyStart) is { } unsortable)
                    {
                        return unsortable;
                    }

                    // A direction ends a key: only a comma or the end may follow it.
                    if (i + 1 < tokens.Count && tokens[i + 1].Kind != TokenKind.Comma)
                    {
                        return Refuse(LinqClause.OrderBy, tokens[i + 1].Position, "has something after a sort direction other than ','.");
                    }

                    break;

                case TokenKind.Comma when !expectMember:
                    if (SortableKey(receiver, keyStart) is { } notSortable)
                    {
                        return notSortable;
                    }

                    keys++;
                    if (keys > LinqQueryLimits.MaxOrderKeys)
                    {
                        return Refuse(LinqClause.OrderBy, token.Position,
                            $"orders by more than {LinqQueryLimits.MaxOrderKeys} keys.");
                    }

                    receiver = graph.Root;
                    expectMember = true;
                    break;

                default:
                    return Refuse(LinqClause.OrderBy, token.Position,
                        "may only list member paths, separated by ',', each optionally followed by asc or desc - as in Total desc, Name.");
            }
        }

        if (expectMember)
        {
            return Refuse(LinqClause.OrderBy, tokens.Count == 0 ? 0 : tokens[^1].Position, "ends where a member name belongs.");
        }

        return SortableKey(receiver, keyStart);
    }

    private static LinqRefusal? SortableKey(Type keyType, int position) =>
        LinqDocumentGraph.IsScalar(keyType)
            ? null
            : Refuse(LinqClause.OrderBy, position, "orders by something that is not a text, a number, a date or another single value.");

    // ---------------------------------------------------------------------------------------------------
    // The tokenizer - Dynamic LINQ's own token rules (1.7.4, TextParser.NextToken), restricted
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The double-quoted strings of a predicate, without their quotes, in the order they are written - or none when
    /// the text does not tokenize. The screen admits no escapes, so each is exactly the text Dynamic LINQ reads.
    /// </summary>
    internal static IReadOnlyList<string> StringLiterals(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = new List<Token>();

        if (Tokenize(LinqClause.Where, text, tokens) is not null)
        {
            return [];
        }

        return [.. tokens.Where(static x => x.Kind == TokenKind.String && x.Text[0] == '"').Select(static x => x.Text[1..^1])];
    }

    private static LinqRefusal? Tokenize(LinqClause clause, string text, List<Token> tokens)
    {
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsControl(c))
            {
                return Refuse(clause, i, "contains a control character.");
            }

            int start = i;

            if (c is '"' or '\'')
            {
                int end = text.IndexOf(c, i + 1);
                if (end < 0)
                {
                    return Refuse(clause, start, "has a string that is never closed.");
                }

                string body = text[(i + 1)..end];
                int backslash = body.IndexOf('\\', StringComparison.Ordinal);

                if (backslash >= 0)
                {
                    return Refuse(clause, start + 1 + backslash,
                        "has a backslash in a string. LINQ mode reads strings literally and has no escapes.");
                }

                if (c == '\'' && body.Length != 1)
                {
                    return Refuse(clause, start,
                        "uses single quotes around more than one character. Single quotes make a character ('A'); write a string in double quotes (\"Alice\").");
                }

                if (body.Any(char.IsControl))
                {
                    return Refuse(clause, start, "has a control character in a string.");
                }

                if (body.Length > LinqQueryLimits.MaxLiteralLength)
                {
                    return Refuse(clause, start, $"has a string longer than {LinqQueryLimits.MaxLiteralLength} characters.");
                }

                tokens.Add(new Token(TokenKind.String, text[start..(end + 1)], start));
                i = end + 1;
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1]) && IsOperandStart(tokens)))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '_'))
                {
                    i++;
                }

                string number = text[start..i];
                if (!NumberLiteral().IsMatch(number))
                {
                    return Refuse(clause, start,
                        $"has '{number}', which is not a number LINQ mode reads; write digits, an optional decimal part and an optional m, d, f, L or U suffix.");
                }

                tokens.Add(new Token(TokenKind.Number, number, start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..i], start));
                continue;
            }

            char n = i + 1 < text.Length ? text[i + 1] : '\0';

            (TokenKind Kind, int Length)? op = c switch
            {
                '(' => (TokenKind.Open, 1),
                ')' => (TokenKind.Close, 1),
                ',' => (TokenKind.Comma, 1),
                '.' => (TokenKind.Dot, 1),
                '%' => (TokenKind.Operator, 1),
                '=' when n == '=' => (TokenKind.Operator, 2),
                '=' when n == '>' => null,
                '=' => (TokenKind.Operator, 1),
                '!' when n == '=' => (TokenKind.Operator, 2),
                '!' => (TokenKind.Operator, 1),
                '<' when n is '=' or '>' => (TokenKind.Operator, 2),
                '<' when n == '<' => null,
                '<' => (TokenKind.Operator, 1),
                '>' when n == '=' => (TokenKind.Operator, 2),
                '>' when n == '>' => null,
                '>' => (TokenKind.Operator, 1),
                '&' when n == '&' => (TokenKind.Operator, 2),
                '|' when n == '|' => (TokenKind.Operator, 2),
                _ => null,
            };

            if (op is not { } found)
            {
                return Refuse(clause, start, Unsupported(c, n));
            }

            tokens.Add(new Token(found.Kind, text.Substring(start, found.Length), start));
            i += found.Length;
        }

        return null;
    }

    /// <summary>Whether a <c>-</c> here would be a sign rather than a subtraction.</summary>
    private static bool IsOperandStart(List<Token> tokens) =>
        tokens.Count == 0 || tokens[^1].Kind is TokenKind.Open or TokenKind.Comma or TokenKind.Operator
        || (tokens[^1].Kind == TokenKind.Identifier && WhereKeywords.Contains(tokens[^1].Text)
            && tokens[^1].Text is not ("null" or "true" or "false" or "it"));

    private static string Unsupported(char c, char next) => c switch
    {
        '@' => "uses an @ placeholder; LINQ mode takes no arguments, so write the value into the predicate.",
        '$' or '^' or '~' => $"uses '{c}', Dynamic LINQ's shorthand for it, parent or root; LINQ mode does not allow it.",
        '[' or ']' => "uses an indexer; LINQ mode does not allow one.",
        '{' or '}' => "uses braces; LINQ mode does not allow them.",
        '?' => "uses '?' - a conditional, null propagation or coalescing - which LINQ mode does not allow. Use and / or, and = null.",
        '=' when next == '>' => "uses a lambda arrow; inside Any(...) and All(...) write the condition directly, as in Items.Any(Quantity > 5).",
        '+' or '-' or '*' or '/' => $"uses arithmetic ('{c}'); LINQ mode compares members with values and does not compute.",
        '&' or '|' => $"uses '{c}' on its own; write && or and, || or or.",
        '<' or '>' => "uses a shift operator, which LINQ mode does not allow.",
        _ => $"contains '{c}', which LINQ mode does not read.",
    };

    private static LinqRefusal Refuse(LinqClause clause, int position, string message) =>
        new(LinqStage.Screen, clause, (clause == LinqClause.OrderBy ? "The ordering " : "The predicate ") + message, position);

    /// <summary>
    /// Digits, an optional decimal part, and an optional suffix - the subset of Dynamic LINQ's number syntax that
    /// needs no exponent, no hex and no binary. Suffixes are case-sensitive where Dynamic LINQ's are: it reads
    /// <c>U</c> and <c>L</c> only in upper case.
    /// </summary>
    [GeneratedRegex(@"^-?\d+(?:(?:\.\d+)?[mMdDfF]?|U|L|UL)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberLiteral();

    private enum TokenKind
    {
        Identifier,
        Number,
        String,
        Operator,
        Dot,
        Comma,
        Open,
        Close,
    }

    private sealed record Token(TokenKind Kind, string Text, int Position);

    /// <summary>One level of parentheses: what a bare name binds on inside it, and what a call's parentheses produce.</summary>
    /// <param name="It">The current <c>it</c>: the document, or a collection's element inside its method's parentheses.</param>
    /// <param name="Returns">For a method call's parentheses, the type the call produces; for grouping, nothing.</param>
    private sealed record Frame(Type It, Type? Returns);

    /// <summary>What one identifier was bound to.</summary>
    /// <param name="Refusal">Why it may not stand there.</param>
    /// <param name="Readable">The type a following <c>.</c> reads from, or <see langword="null" /> when nothing may follow.</param>
    /// <param name="Call">The parentheses it opens, when it names a method.</param>
    /// <param name="EndsValue">Whether a value ends with it, so that another may not follow directly.</param>
    private sealed record Binding(LinqRefusal? Refusal, Type? Readable, Frame? Call, bool EndsValue)
    {
        public static Binding Refused(LinqRefusal refusal) => new(refusal, null, null, EndsValue: false);
    }

    /// <summary>Stands for <c>StringComparison</c> named as the enum, whose values are the only thing after its <c>.</c>.</summary>
    private sealed class StringComparisonValues;
}
