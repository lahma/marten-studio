using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KStudio.Core.Predicates;

/// <summary>
/// Writes a tree back as text (decision 7.3): lower-case keywords, single quotes with <c>''</c>, invariant numbers, <c>header.name</c>
/// when the name allows it and <c>header['…']</c> otherwise, and parentheses only where precedence needs them. Parsing the result gives
/// the same tree. The text a sink gets (the audit, a search link) shows a literal of a redacted field as <c>&lt;redacted&gt;</c>
/// (decision 10.5).
/// </summary>
internal static partial class PredicateFormatter
{
    /// <summary>The canonical text of a tree; the empty text for no tree.</summary>
    /// <param name="node">The tree.</param>
    public static string Format(PredicateNode? node) => Format(node, null);

    /// <summary>The canonical text of a tree, with the literals of some clauses replaced by <c>&lt;redacted&gt;</c>.</summary>
    /// <param name="node">The tree.</param>
    /// <param name="redacted">Whether a clause's literals must not leave the engine; <see langword="null" /> keeps them all.</param>
    public static string Format(PredicateNode? node, Func<ClauseNode, bool>? redacted)
    {
        if (node is null)
        {
            return string.Empty;
        }

        StringBuilder text = new();
        Write(text, node, Precedence.Or, redacted);
        return text.ToString();
    }

    private enum Precedence
    {
        Or = 0,
        And = 1,
        Not = 2,
        Clause = 3,
    }

    private static void Write(StringBuilder text, PredicateNode node, Precedence parent, Func<ClauseNode, bool>? redacted)
    {
        switch (node)
        {
            case OrNode or:
                Join(text, or.Children, " or ", Precedence.Or, parent, redacted);
                break;
            case AndNode and:
                Join(text, and.Children, " and ", Precedence.And, parent, redacted);
                break;
            case NotNode not:
                // The operand of a not needs parentheses unless it is a clause or another not.
                text.Append("not ");
                Write(text, not.Operand, Precedence.Not, redacted);
                break;
            case ClauseNode clause:
                WriteClause(text, clause, redacted);
                break;
        }
    }

    private static void Join(StringBuilder text, IReadOnlyList<PredicateNode> children, string separator, Precedence own, Precedence parent, Func<ClauseNode, bool>? redacted)
    {
        // A child of the same kind needs no parentheses (the operators are associative); a looser child inside a tighter parent does.
        bool wrap = own < parent;

        if (wrap)
        {
            text.Append('(');
        }

        for (int i = 0; i < children.Count; i++)
        {
            if (i > 0)
            {
                text.Append(separator);
            }

            Write(text, children[i], own, redacted);
        }

        if (wrap)
        {
            text.Append(')');
        }
    }

    private static void WriteClause(StringBuilder text, ClauseNode clause, Func<ClauseNode, bool>? redacted)
    {
        text.Append(Field(clause.Field));

        switch (clause.Operator)
        {
            case PredicateOperator.IsNull:
                text.Append(" is null");
                return;
            case PredicateOperator.IsNotNull:
                text.Append(" is not null");
                return;
            case PredicateOperator.In:
                text.Append(" in (");
                break;
            default:
                text.Append(' ').Append(OperatorText(clause.Operator)).Append(' ');
                break;
        }

        bool hide = redacted?.Invoke(clause) == true;

        for (int i = 0; i < clause.Literals.Count; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }

            text.Append(hide ? PredicateParser.RedactedToken : Literal(clause.Literals[i]));
        }

        if (clause.Operator == PredicateOperator.In)
        {
            text.Append(')');
        }
    }

    /// <summary>A field as the canonical text writes it.</summary>
    /// <param name="field">The field.</param>
    public static string Field(FieldNode field) =>
        field switch
        {
            KeyField => "key",
            AnyField => "any",
            ValueField => "value",
            CloudEventField ce => "ce." + ce.Attribute,
            JsonPathField path => path.Path,
            HeaderField header when SimpleName().IsMatch(header.Name) => "header." + header.Name,
            HeaderField header => "header[" + Quote(header.Name) + "]",
            _ => "?",
        };

    /// <summary>An operator as the canonical text writes it.</summary>
    /// <param name="op">The operator.</param>
    public static string OperatorText(PredicateOperator op) =>
        op switch
        {
            PredicateOperator.Equals => "=",
            PredicateOperator.Prefix => "^=",
            PredicateOperator.Contains => "~",
            PredicateOperator.Less => "<",
            PredicateOperator.LessOrEqual => "<=",
            PredicateOperator.Greater => ">",
            PredicateOperator.GreaterOrEqual => ">=",
            PredicateOperator.In => "in",
            PredicateOperator.IsNull => "is null",
            _ => "is not null",
        };

    private static string Literal(LiteralNode literal) =>
        literal switch
        {
            StringLiteral text => Quote(text.Value),
            NumberLiteral number => number.Value.ToString(CultureInfo.InvariantCulture),
            NullLiteral => "null",
            BoolLiteral boolean => boolean.Value ? "true" : "false",
            _ => PredicateParser.RedactedToken,
        };

    /// <summary>A string as the canonical text writes a literal, so that it parses back to the same value ("copy as" uses it).</summary>
    /// <param name="value">The value.</param>
    public static string QuoteText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Quote(value);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SimpleName();
}
