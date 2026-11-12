using System.Globalization;
using System.Text;

namespace KStudio.Core.Predicates;

/// <summary>One step of a JSON path (decision 2.1 of D-PAYLOAD). The names avoid PostgresStudio's <c>JsonPathSegment</c>, which KStudio also imports.</summary>
internal abstract record JsonPathStep;

/// <summary>A member of an object, by name. A search compares names exactly; a redaction rule compares them ignoring case.</summary>
/// <param name="Name">The member's name.</param>
internal sealed record JsonMemberStep(string Name) : JsonPathStep
{
    // A name from a masked value's path is content: it is never printed.
    protected override bool PrintMembers(StringBuilder builder) => false;
}

/// <summary>An element of an array, by position.</summary>
/// <param name="Index">The position, from 0.</param>
internal sealed record JsonIndexStep(int Index) : JsonPathStep;

/// <summary><c>[*]</c>: any element of an array. On an object it selects nothing.</summary>
internal sealed record JsonWildcardStep : JsonPathStep;

/// <summary>
/// A JSON path the search can read: <c>$</c>, then up to 32 steps of <c>.member</c>, <c>['member']</c>, <c>[n]</c> or <c>[*]</c>. The grammar
/// is deliberately small (no filters, no recursive descent): every path selects by structure, never by content, so a redaction rule can
/// say exactly which paths it covers.
/// </summary>
internal sealed class JsonPathExpression
{
    /// <summary>The most steps a path may have.</summary>
    public const int MaxSteps = 32;

    /// <summary>The longest member name a path may hold.</summary>
    public const int MaxMemberLength = 128;

    /// <summary>The largest index.</summary>
    public const int MaxIndex = 999_999_999;

    private const int ShownLength = 64;

    /// <summary>Creates a path from its steps.</summary>
    /// <param name="steps">The steps.</param>
    public JsonPathExpression(IReadOnlyList<JsonPathStep> steps)
    {
        Steps = steps;
        Text = Format(steps);
    }

    /// <summary>The steps, from the root.</summary>
    public IReadOnlyList<JsonPathStep> Steps { get; }

    /// <summary>The canonical text: <c>$.a.b[0][*]</c>, a member that is not a plain name in brackets.</summary>
    public string Text { get; }

    /// <summary>Whether a member name can be written after a dot: letters, digits and underscores, starting with a letter or an underscore.</summary>
    /// <param name="name">The name.</param>
    public static bool IsPlainName(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The canonical text of some steps.</summary>
    /// <param name="steps">The steps.</param>
    public static string Format(IReadOnlyList<JsonPathStep> steps)
    {
        StringBuilder text = new("$");

        foreach (JsonPathStep step in steps)
        {
            switch (step)
            {
                case JsonMemberStep member when IsPlainName(member.Name):
                    text.Append('.').Append(member.Name);
                    break;
                case JsonMemberStep member:
                    text.Append("['").Append(member.Name.Replace("'", "''", StringComparison.Ordinal)).Append("']");
                    break;
                case JsonIndexStep index:
                    text.Append('[').Append(index.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                default:
                    text.Append("[*]");
                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>Parses a path token.</summary>
    /// <param name="text">The token, starting with <c>$</c>.</param>
    /// <param name="start">Where the token starts in the search text, from 0: positions in an error are counted from there.</param>
    /// <param name="path">The path, when it parsed.</param>
    /// <param name="error">The problem, when it did not. It names the path (cut at 64 characters), never a literal.</param>
    public static bool TryParse(string text, int start, out JsonPathExpression? path, out PredicateError? error)
    {
        ArgumentNullException.ThrowIfNull(text);

        path = null;

        // The search lexer only ever hands over a token that starts with $; a redaction rule's path comes from the host as typed.
        if (text.Length == 0 || text[0] != '$')
        {
            error = Error(text, start, 0, "must start with $.");
            return false;
        }

        List<JsonPathStep> steps = [];
        int i = 1;

        while (i < text.Length)
        {
            if (steps.Count >= MaxSteps)
            {
                error = Error(text, start, i, "has more than 32 steps.");
                return false;
            }

            char c = text[i];

            if (c == '.')
            {
                int from = i + 1;
                int to = from;

                while (to < text.Length && (char.IsAsciiLetterOrDigit(text[to]) || text[to] == '_'))
                {
                    to++;
                }

                if (to == from)
                {
                    error = Error(text, start, i, "has an empty step: write $.name, $.name[0] or $.name[*].");
                    return false;
                }

                string name = text[from..to];

                if (!(char.IsAsciiLetter(name[0]) || name[0] == '_'))
                {
                    error = Error(text, start, i, "has a member the search cannot write: write it in brackets, $['1a'].");
                    return false;
                }

                if (name.Length > MaxMemberLength)
                {
                    error = Error(text, start, i, "has a member longer than 128 characters.");
                    return false;
                }

                steps.Add(new JsonMemberStep(name));
                i = to;
                continue;
            }

            if (c == '[')
            {
                if (!TryBracket(text, start, ref i, steps, out error))
                {
                    return false;
                }

                continue;
            }

            // `$a` is the usual slip; any other stray character is a step that was not written properly.
            error = i == 1
                ? Error(text, start, i, "must go on with . or [ after $.")
                : Error(text, start, i, "has an empty step: write $.name, $.name[0] or $.name[*].");
            return false;
        }

        path = new JsonPathExpression(steps);
        error = null;
        return true;
    }

    /// <summary>
    /// Whether this path, a rule's, selects the node at <paramref name="other" /> or an ancestor of it: member names are compared ignoring
    /// case, <c>[*]</c> covers any index and <c>[*]</c>, and an index covers only itself. Redaction fails closed, so <c>$.Email</c> is covered
    /// by a rule on <c>$.email</c>.
    /// </summary>
    /// <param name="other">The steps of a node (no wildcard) or of a clause's path.</param>
    public bool Covers(IReadOnlyList<JsonPathStep> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Steps.Count > other.Count)
        {
            return false;
        }

        for (int i = 0; i < Steps.Count; i++)
        {
            if (!StepCovers(Steps[i], other[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether this path, a rule's, covers every node a clause's path can select: the same test as <see cref="Covers" />, in which a
    /// <c>[*]</c> in the clause is covered only by a <c>[*]</c> in the rule.
    /// </summary>
    /// <param name="clause">The clause's path.</param>
    public bool CoversClause(JsonPathExpression clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        return Covers(clause.Steps);
    }

    private static bool StepCovers(JsonPathStep rule, JsonPathStep other) =>
        (rule, other) switch
        {
            (JsonMemberStep a, JsonMemberStep b) => string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            (JsonWildcardStep, JsonIndexStep or JsonWildcardStep) => true,
            (JsonIndexStep a, JsonIndexStep b) => a.Index == b.Index,
            _ => false,
        };

    private static bool TryBracket(string text, int start, ref int i, List<JsonPathStep> steps, out PredicateError? error)
    {
        int open = i;
        int from = i + 1;

        if (from < text.Length && text[from] is '\'' or '"')
        {
            char quote = text[from];
            StringBuilder name = new();
            int to = from + 1;
            bool closed = false;

            while (to < text.Length)
            {
                if (text[to] == quote)
                {
                    if (to + 1 < text.Length && text[to + 1] == quote)
                    {
                        name.Append(quote);
                        to += 2;
                        continue;
                    }

                    closed = true;
                    to++;
                    break;
                }

                name.Append(text[to]);
                to++;
            }

            if (!closed || to >= text.Length || text[to] != ']')
            {
                error = Error(text, start, open, "has a '[' with no matching ']'.");
                return false;
            }

            if (name.Length > MaxMemberLength)
            {
                error = Error(text, start, open, "has a member longer than 128 characters.");
                return false;
            }

            steps.Add(new JsonMemberStep(name.ToString()));
            i = to + 1;
            error = null;
            return true;
        }

        int end = text.IndexOf(']', from);

        if (end < 0)
        {
            error = Error(text, start, open, "has a '[' with no matching ']'.");
            return false;
        }

        string inner = text[from..end];

        if (inner == "*")
        {
            steps.Add(new JsonWildcardStep());
        }
        else if (TryIndex(inner, out int index))
        {
            steps.Add(new JsonIndexStep(index));
        }
        else
        {
            error = Error(text, start, open, "has an index that is not a whole number: write [0], [12] or [*].");
            return false;
        }

        i = end + 1;
        error = null;
        return true;
    }

    private static bool TryIndex(string text, out int index)
    {
        index = 0;

        if (text.Length is < 1 or > 9 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        index = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    private static PredicateError Error(string text, int start, int at, string problem)
    {
        string shown = text.Length > ShownLength ? text[..ShownLength] : text;
        int position = start + at + 1;

        return new PredicateError(start + at, 1, string.Create(CultureInfo.InvariantCulture, $"The JSON path '{shown}' at position {position} {problem}"));
    }
}
