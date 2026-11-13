using System.Globalization;

namespace KStudio.Core.Predicates;

/// <summary>
/// Something wrong with a search text, for the visitor who typed it (decision 7.6). The message names fields, operators and what was found and
/// <b>never contains a literal</b>: an error is a sink like any other (D40). It does not carry its position: a page draws
/// "Position {n}: {message}", so the position is said once (#75).
/// </summary>
/// <param name="Start">Where the problem starts, counted from 0 in the text; the page counts positions from 1.</param>
/// <param name="Length">How many characters it covers.</param>
/// <param name="Message">What is wrong, with no literal in it and no position, starting in lower case after "Position {n}: ".</param>
/// <param name="Kind">What kind of problem it is, which fixes the constant phrase the audit and the log record for it.</param>
internal sealed record PredicateError(int Start, int Length, string Message, PredicateErrorKind Kind = PredicateErrorKind.Invalid)
{
    /// <summary>The page's wording: "Position {n}: {message}", the position counted from 1.</summary>
    public string Shown => string.Create(CultureInfo.InvariantCulture, $"Position {Start + 1}: {Message}");

    /// <summary>
    /// The constant phrase for this kind of problem, fit for the audit and the log (#75): it carries nothing the visitor typed - no field, header
    /// or rule name, no JSON path and no literal.
    /// </summary>
    public string AuditPhrase => Kind switch
    {
        PredicateErrorKind.Expected => "expected a value",
        PredicateErrorKind.UnexpectedToken => "unexpected token",
        PredicateErrorKind.UnexpectedCharacter => "unexpected character",
        PredicateErrorKind.UnclosedString => "unclosed string",
        PredicateErrorKind.ValueTooLong => "value too long",
        PredicateErrorKind.TextTooLong => "search text too long",
        PredicateErrorKind.TooManyConditions => "too many conditions",
        PredicateErrorKind.TooManyValues => "too many values",
        PredicateErrorKind.UnclosedParenthesis => "unclosed parenthesis",
        PredicateErrorKind.NestedTooDeep => "nested too deeply",
        PredicateErrorKind.EmptyHeaderName => "empty header name",
        PredicateErrorKind.UnknownField => "unknown field",
        PredicateErrorKind.NumberOutOfRange => "number out of range",
        PredicateErrorKind.RedactedValueLeftOut => "redacted value left out",
        PredicateErrorKind.JsonPath => "invalid JSON path",
        PredicateErrorKind.RedactedFieldOperator => "redacted field operator",
        PredicateErrorKind.ValuesNotAvailable => "values not available",
        PredicateErrorKind.OperatorNotAllowed => "operator not allowed on this field",
        PredicateErrorKind.LiteralType => "wrong kind of value",
        PredicateErrorKind.KeyNotValid => "value not valid for the key format",
        _ => "invalid search text",
    };
}

/// <summary>The kinds of <see cref="PredicateError" />, each with one constant audit phrase (#75).</summary>
internal enum PredicateErrorKind
{
    /// <summary>A search text that is not valid in a way no other kind names.</summary>
    Invalid,

    /// <summary>A value was expected and something else was found.</summary>
    Expected,

    /// <summary>Another token than the grammar allows.</summary>
    UnexpectedToken,

    /// <summary>A character the grammar has no use for.</summary>
    UnexpectedCharacter,

    /// <summary>A string with no closing quote.</summary>
    UnclosedString,

    /// <summary>A literal longer than the most the grammar takes.</summary>
    ValueTooLong,

    /// <summary>A search text longer than the most the grammar takes.</summary>
    TextTooLong,

    /// <summary>More conditions than the grammar takes.</summary>
    TooManyConditions,

    /// <summary>More values in a list than the grammar takes.</summary>
    TooManyValues,

    /// <summary>A parenthesis that is never closed.</summary>
    UnclosedParenthesis,

    /// <summary>Parentheses nested deeper than the grammar takes.</summary>
    NestedTooDeep,

    /// <summary>A <c>header[]</c> with no name.</summary>
    EmptyHeaderName,

    /// <summary>A name that is not a field.</summary>
    UnknownField,

    /// <summary>A number the engine cannot hold.</summary>
    NumberOutOfRange,

    /// <summary>A permalink's redacted value that was not typed in.</summary>
    RedactedValueLeftOut,

    /// <summary>A JSON path that does not parse.</summary>
    JsonPath,

    /// <summary>An operator a redacted field does not take.</summary>
    RedactedFieldOperator,

    /// <summary>A clause that reads values where the visitor may not.</summary>
    ValuesNotAvailable,

    /// <summary>An operator the field does not take.</summary>
    OperatorNotAllowed,

    /// <summary>A literal of the wrong kind for the field.</summary>
    LiteralType,

    /// <summary>A literal the topic's key format cannot encode.</summary>
    KeyNotValid,
}

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
