namespace KStudio.Core.Predicates;

/// <summary>
/// Something wrong with a search text, for the visitor who typed it (decision 7.6). The message names fields, operators and positions and
/// <b>never contains a literal</b>: an error is a sink like any other (D40).
/// </summary>
/// <param name="Start">Where the problem starts, counted from 0 in the text; the message counts positions from 1.</param>
/// <param name="Length">How many characters it covers.</param>
/// <param name="Message">What is wrong, with no literal in it.</param>
internal sealed record PredicateError(int Start, int Length, string Message);

/// <summary>A node of the search tree; every node knows where it came from (decision 7.2).</summary>
/// <param name="Start">Where it starts in the text, from 0.</param>
/// <param name="Length">How many characters it covers.</param>
internal abstract record PredicateNode(int Start, int Length);

/// <summary>Every child must be true. A chain of <c>and</c> is one node.</summary>
/// <param name="Children">The conditions.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record AndNode(IReadOnlyList<PredicateNode> Children, int Start, int Length) : PredicateNode(Start, Length);

/// <summary>One child must be true. A chain of <c>or</c> is one node.</summary>
/// <param name="Children">The conditions.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record OrNode(IReadOnlyList<PredicateNode> Children, int Start, int Length) : PredicateNode(Start, Length);

/// <summary>The opposite of its operand; <c>not unknown</c> is unknown.</summary>
/// <param name="Operand">The condition.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record NotNode(PredicateNode Operand, int Start, int Length) : PredicateNode(Start, Length);

/// <summary>One comparison of a field with literals.</summary>
/// <param name="Field">What is compared.</param>
/// <param name="Operator">How.</param>
/// <param name="Literals">What with: none for <c>is null</c>, one or more for <c>in</c>, one otherwise.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record ClauseNode(FieldNode Field, PredicateOperator Operator, IReadOnlyList<LiteralNode> Literals, int Start, int Length)
    : PredicateNode(Start, Length);

/// <summary>The operators of the grammar (decision 7.2).</summary>
internal enum PredicateOperator
{
    /// <summary><c>=</c>.</summary>
    Equals,

    /// <summary><c>^=</c>: a prefix.</summary>
    Prefix,

    /// <summary><c>~</c>: contains, ignoring case.</summary>
    Contains,

    /// <summary><c>in (…)</c>.</summary>
    In,

    /// <summary><c>is null</c>.</summary>
    IsNull,

    /// <summary><c>is not null</c>.</summary>
    IsNotNull,

    /// <summary><c>&lt;</c> (M3, JSON paths).</summary>
    Less,

    /// <summary><c>&lt;=</c> (M3).</summary>
    LessOrEqual,

    /// <summary><c>&gt;</c> (M3).</summary>
    Greater,

    /// <summary><c>&gt;=</c> (M3).</summary>
    GreaterOrEqual,
}

/// <summary>What a clause reads.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal abstract record FieldNode(int Start, int Length);

/// <summary>The record's key.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record KeyField(int Start, int Length) : FieldNode(Start, Length);

/// <summary>A header by name; the name is case-sensitive.</summary>
/// <param name="Name">The header's name.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record HeaderField(string Name, int Start, int Length) : FieldNode(Start, Length);

/// <summary>A CloudEvents attribute, <c>[a-z0-9]{1,64}</c>.</summary>
/// <param name="Attribute">The attribute's name.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record CloudEventField(string Attribute, int Start, int Length) : FieldNode(Start, Length);

/// <summary>Any of the key's text, header values and CloudEvents attributes.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record AnyField(int Start, int Length) : FieldNode(Start, Length);

/// <summary>The record's value, or a CloudEvent's data (it needs the payload grant, except for <c>is null</c>).</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record ValueField(int Start, int Length) : FieldNode(Start, Length);

/// <summary>A JSON path into the value, or into a CloudEvent's data (it needs the payload grant).</summary>
/// <param name="Path">The parsed path; its <see cref="JsonPathExpression.Text" /> is the canonical text.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record JsonPathField(JsonPathExpression Path, int Start, int Length) : FieldNode(Start, Length);

/// <summary>A literal of a clause.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal abstract record LiteralNode(int Start, int Length);

/// <summary>A quoted string.</summary>
/// <param name="Value">Its text, quotes and doubled quotes resolved.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record StringLiteral(string Value, int Start, int Length) : LiteralNode(Start, Length);

/// <summary>A number.</summary>
/// <param name="Value">Its value.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record NumberLiteral(decimal Value, int Start, int Length) : LiteralNode(Start, Length);

/// <summary><c>null</c>.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record NullLiteral(int Start, int Length) : LiteralNode(Start, Length);

/// <summary><c>true</c> or <c>false</c>.</summary>
/// <param name="Value">Which.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record BoolLiteral(bool Value, int Start, int Length) : LiteralNode(Start, Length);

/// <summary>What a search link carries in place of a redacted literal (decision 10.5): the parser refuses it.</summary>
/// <param name="Start">Where it starts.</param>
/// <param name="Length">How long it is.</param>
internal sealed record RedactedPlaceholder(int Start, int Length) : LiteralNode(Start, Length);

/// <summary>The outcome of parsing: a tree (null for an empty query) or the error that stopped it.</summary>
/// <param name="Tree">The tree; <see langword="null" /> for an empty text, which matches every record.</param>
/// <param name="Errors">The errors; empty when the text is valid.</param>
internal sealed record PredicateParseResult(PredicateNode? Tree, IReadOnlyList<PredicateError> Errors)
{
    /// <summary>Whether the text parsed.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Comparing trees without their positions.</summary>
internal static class PredicateTree
{
    /// <summary>Whether two trees are the same condition, wherever each came from.</summary>
    /// <param name="left">One tree.</param>
    /// <param name="right">The other.</param>
    public static bool StructurallyEqual(PredicateNode? left, PredicateNode? right)
    {
        return (left, right) switch
        {
            (null, null) => true,
            (AndNode a, AndNode b) => ListEqual(a.Children, b.Children),
            (OrNode a, OrNode b) => ListEqual(a.Children, b.Children),
            (NotNode a, NotNode b) => StructurallyEqual(a.Operand, b.Operand),
            (ClauseNode a, ClauseNode b) =>
                a.Operator == b.Operator
                && FieldEqual(a.Field, b.Field)
                && a.Literals.Count == b.Literals.Count
                && a.Literals.Zip(b.Literals).All(static x => LiteralEqual(x.First, x.Second)),
            _ => false,
        };
    }

    /// <summary>
    /// Whether the tree reads record values: a clause on <c>value</c> (except <c>is null</c>, which looks at the record, not at its
    /// content) or on a JSON path. <c>any</c> does not count, because it reads the value only when the visitor may.
    /// </summary>
    /// <param name="node">The tree.</param>
    public static bool ReadsValues(PredicateNode? node) =>
        node switch
        {
            AndNode and => and.Children.Any(ReadsValues),
            OrNode or => or.Children.Any(ReadsValues),
            NotNode not => ReadsValues(not.Operand),
            ClauseNode clause => clause.Field is JsonPathField
                || (clause.Field is ValueField && clause.Operator is not (PredicateOperator.IsNull or PredicateOperator.IsNotNull)),
            _ => false,
        };

    private static bool ListEqual(IReadOnlyList<PredicateNode> left, IReadOnlyList<PredicateNode> right) =>
        left.Count == right.Count && left.Zip(right).All(static x => StructurallyEqual(x.First, x.Second));

    private static bool FieldEqual(FieldNode left, FieldNode right) =>
        (left, right) switch
        {
            (KeyField, KeyField) => true,
            (AnyField, AnyField) => true,
            (ValueField, ValueField) => true,
            (HeaderField a, HeaderField b) => string.Equals(a.Name, b.Name, StringComparison.Ordinal),
            (CloudEventField a, CloudEventField b) => string.Equals(a.Attribute, b.Attribute, StringComparison.Ordinal),
            (JsonPathField a, JsonPathField b) => string.Equals(a.Path.Text, b.Path.Text, StringComparison.Ordinal),
            _ => false,
        };

    private static bool LiteralEqual(LiteralNode left, LiteralNode right) =>
        (left, right) switch
        {
            (StringLiteral a, StringLiteral b) => string.Equals(a.Value, b.Value, StringComparison.Ordinal),
            (NumberLiteral a, NumberLiteral b) => a.Value == b.Value,
            (NullLiteral, NullLiteral) => true,
            (BoolLiteral a, BoolLiteral b) => a.Value == b.Value,
            (RedactedPlaceholder, RedactedPlaceholder) => true,
            _ => false,
        };
}
