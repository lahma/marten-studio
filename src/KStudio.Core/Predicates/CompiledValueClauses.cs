using System.Globalization;
using System.Text.Json;

using KStudio.Core.Decoding;
using KStudio.Core.Redaction;

namespace KStudio.Core.Predicates;

/// <summary>
/// What the value-side clauses share (decision 2.2 of D-PAYLOAD): the value is a CloudEvent's data when the record is one, a clause is true
/// if it is true of any event of a batch, and with payload rules a clause compares the value as the visitor would see it, rules applied.
/// </summary>
internal static class ValueJudge
{
    /// <summary>The unknown reason for a value that could not be decoded.</summary>
    /// <param name="reason">Why the value is undecodable.</param>
    public static KafkaUnknownReason UnknownFor(KafkaUndecodableReason? reason) =>
        reason switch
        {
            KafkaUndecodableReason.TooLarge => KafkaUnknownReason.TooLarge,
            KafkaUndecodableReason.InvalidUtf8 => KafkaUnknownReason.ValueNotText,
            KafkaUndecodableReason.NotACloudEvent => KafkaUnknownReason.NotACloudEvent,
            KafkaUndecodableReason.DecoderFailed or KafkaUndecodableReason.DecoderRefused => KafkaUnknownReason.DecoderFailed,
            KafkaUndecodableReason.InvalidBase64 => KafkaUnknownReason.InvalidBase64,
            _ => KafkaUnknownReason.InvalidJson,
        };

    /// <summary>Whether a value has nothing a clause can compare: a tombstone, a CloudEvent without data, or an empty value for a path.</summary>
    /// <param name="value">The value.</param>
    public static bool IsAbsent(RecordValue value) => value.NoData || value.Kind == KafkaValueKind.Tombstone;

    /// <summary>
    /// The masked form of a JSON or text value when the topic has payload rules, or <see langword="null" /> when the rules do not apply.
    /// A value the rules could not be applied to is a reason, not a form.
    /// </summary>
    /// <param name="context">The record.</param>
    /// <param name="part">The value.</param>
    /// <param name="redactor">The topic's rules.</param>
    /// <param name="withheld">Whether the rules could not be applied.</param>
    public static RedactedValue? Masked(RecordEvaluationContext context, RecordValue part, IKafkaRedactor redactor, out bool withheld)
    {
        withheld = false;

        if (!redactor.HasPayloadRules)
        {
            return null;
        }

        RedactedValue? result = context.RedactedOf(part);
        withheld = result?.WithheldBy is not null;
        return result;
    }

    /// <summary>
    /// Reads a JSON number as a <c>decimal</c> when that is exact enough to compare: at most 28 significant digits, and a non-zero number
    /// that does not round to zero. Anything else is out of range, so that a comparison says "unknown" and never a wrong "equal".
    /// </summary>
    /// <param name="raw">The number as written.</param>
    /// <param name="number">The value, when it can be read.</param>
    public static bool TryReadNumber(string raw, out decimal number)
    {
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        int significant = 0;
        bool leading = true;
        bool nonZero = false;

        foreach (char c in raw)
        {
            if (c is 'e' or 'E')
            {
                break;
            }

            if (!char.IsAsciiDigit(c) || (leading && c == '0'))
            {
                continue;
            }

            leading = false;
            significant++;
            nonZero |= c != '0';
        }

        return significant <= 28 && !(nonZero && number == 0);
    }

    /// <summary>Compares a decimal literal with a number leaf; <see langword="false" /> in <paramref name="inRange" /> when the leaf is too large for a decimal.</summary>
    /// <param name="leaf">The number.</param>
    /// <param name="op">The operator.</param>
    /// <param name="literal">The literal.</param>
    /// <param name="inRange">Whether the leaf could be read.</param>
    public static bool CompareNumber(JsonElement leaf, PredicateOperator op, decimal literal, out bool inRange)
    {
        inRange = TryReadNumber(leaf.GetRawText(), out decimal number);

        if (!inRange)
        {
            return false;
        }

        return op switch
        {
            PredicateOperator.Equals or PredicateOperator.In => number == literal,
            PredicateOperator.Less => number < literal,
            PredicateOperator.LessOrEqual => number <= literal,
            PredicateOperator.Greater => number > literal,
            PredicateOperator.GreaterOrEqual => number >= literal,
            _ => false,
        };
    }
}

/// <summary><c>value is null</c> and <c>value is not null</c> (cost 0): true of a tombstone only. An empty value is not null, and nothing is decoded.</summary>
internal sealed class CompiledValueNullClause(PredicateOperator op) : CompiledNode
{
    public override int Cost => 0;

    public override TriState Evaluate(RecordEvaluationContext context)
    {
        bool isNull = context.Record.Value is null;
        return (op == PredicateOperator.IsNull ? isNull : !isNull) ? TriState.True : TriState.False;
    }
}

/// <summary><c>value</c>: the value (a CloudEvent's data) as text, compared as the visitor would see it (cost 6).</summary>
internal sealed class CompiledValueClause(PredicateOperator op, string[] texts, IKafkaRedactor redactor) : CompiledNode
{
    public override int Cost => 6;

    public override bool ReadsValues => true;

    public override TriState Evaluate(RecordEvaluationContext context)
    {
        KafkaUnknownReason? unknown = null;

        foreach (RecordValue part in context.Value.All())
        {
            switch (Judge(context, part, out KafkaUnknownReason? reason))
            {
                case TriState.True:
                    return TriState.True;
                case TriState.Unknown:
                    unknown ??= reason;
                    break;
            }
        }

        if (unknown is { } why)
        {
            context.MarkUnknown(why);
            return TriState.Unknown;
        }

        return TriState.False;
    }

    private TriState Judge(RecordEvaluationContext context, RecordValue part, out KafkaUnknownReason? reason)
    {
        reason = null;

        if (ValueJudge.IsAbsent(part))
        {
            return TriState.False;
        }

        switch (part.Kind)
        {
            case KafkaValueKind.Empty:
                return TextMatch.Any(op, string.Empty, texts) ? TriState.True : TriState.False;

            case KafkaValueKind.Undecodable:
                reason = ValueJudge.UnknownFor(part.Reason);
                return TriState.Unknown;

            case KafkaValueKind.Binary or KafkaValueKind.ConfluentWireFormat:
                // Bytes are never equal to or prefixed by a text; only a contains-search could have found something in them.
                if (op == PredicateOperator.Contains)
                {
                    reason = KafkaUnknownReason.ValueNotText;
                    return TriState.Unknown;
                }

                return TriState.False;

            case KafkaValueKind.Json or KafkaValueKind.Text when part.Utf8 is not null:
                {
                    if (part.Cut)
                    {
                        reason = KafkaUnknownReason.TooLarge;
                        return TriState.Unknown;
                    }

                    string text = part.Text;

                    if (ValueJudge.Masked(context, part, redactor, out bool withheld) is { } masked)
                    {
                        if (withheld)
                        {
                            reason = KafkaUnknownReason.RedactionNotApplied;
                            return TriState.Unknown;
                        }

                        text = masked.Text!;
                    }

                    return TextMatch.Any(op, text, texts) ? TriState.True : TriState.False;
                }

            default:
                return TriState.False;
        }
    }
}

/// <summary>
/// A JSON path (cost 7): the leaves it selects are compared with the literals by their own type, and the clause is true if any leaf is. A
/// path a searchable rule covers compares the raw leaf by an exact match (and is the only clause that does); every other path selects from
/// what the visitor would see.
/// </summary>
internal sealed class CompiledJsonPathClause : CompiledNode
{
    private readonly IReadOnlyList<JsonPathStep> steps;
    private readonly PredicateOperator op;
    private readonly string[] strings;
    private readonly decimal[] numbers;
    private readonly bool[] booleans;
    private readonly bool hasNull;
    private readonly bool rawLeaf;
    private readonly IKafkaRedactor redactor;

    public CompiledJsonPathClause(JsonPathExpression path, PredicateOperator op, IReadOnlyList<LiteralNode> literals, bool rawLeaf, IKafkaRedactor redactor)
    {
        steps = path.Steps;
        this.op = op;
        this.rawLeaf = rawLeaf;
        this.redactor = redactor;
        strings = [.. literals.OfType<StringLiteral>().Select(static x => x.Value)];
        numbers = [.. literals.OfType<NumberLiteral>().Select(static x => x.Value)];
        booleans = [.. literals.OfType<BoolLiteral>().Select(static x => x.Value)];
        hasNull = literals.Any(static x => x is NullLiteral);
    }

    public override int Cost => 7;

    public override bool ReadsValues => true;

    public override TriState Evaluate(RecordEvaluationContext context)
    {
        KafkaUnknownReason? unknown = null;

        foreach (RecordValue part in context.Value.All())
        {
            switch (Judge(context, part, out KafkaUnknownReason? reason))
            {
                case TriState.True:
                    return TriState.True;
                case TriState.Unknown:
                    unknown ??= reason;
                    break;
            }
        }

        if (unknown is { } why)
        {
            context.MarkUnknown(why);
            return TriState.Unknown;
        }

        return TriState.False;
    }

    private TriState Judge(RecordEvaluationContext context, RecordValue part, out KafkaUnknownReason? reason)
    {
        reason = null;

        if (ValueJudge.IsAbsent(part))
        {
            return TriState.False;
        }

        switch (part.Kind)
        {
            case KafkaValueKind.Undecodable:
                reason = ValueJudge.UnknownFor(part.Reason);
                return TriState.Unknown;

            case KafkaValueKind.Text when part.LooksLikeJson:
                // It starts like JSON and does not parse: a path cannot say whether the member is there.
                reason = KafkaUnknownReason.InvalidJson;
                return TriState.Unknown;

            case KafkaValueKind.Json when part.Json is { } json:
                {
                    JsonElement root = json;

                    if (!rawLeaf && ValueJudge.Masked(context, part, redactor, out bool withheld) is { } masked)
                    {
                        if (withheld || !masked.TryGetJson(out root))
                        {
                            reason = KafkaUnknownReason.RedactionNotApplied;
                            return TriState.Unknown;
                        }
                    }

                    bool tooLarge = false;

                    if (Select(root, 0, ref tooLarge))
                    {
                        return TriState.True;
                    }

                    if (tooLarge)
                    {
                        reason = KafkaUnknownReason.NumberOutOfRange;
                        return TriState.Unknown;
                    }

                    return TriState.False;
                }

            default:
                return TriState.False;
        }
    }

    /// <summary>Walks the path; true as soon as a selected leaf satisfies the clause. Member names are compared exactly.</summary>
    private bool Select(JsonElement element, int index, ref bool tooLarge)
    {
        if (index == steps.Count)
        {
            return Matches(element, ref tooLarge);
        }

        switch (steps[index])
        {
            case JsonMemberStep member when element.ValueKind == JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.NameEquals(member.Name) && Select(property.Value, index + 1, ref tooLarge))
                    {
                        return true;
                    }
                }

                return false;

            case JsonIndexStep position when element.ValueKind == JsonValueKind.Array:
                return position.Index < element.GetArrayLength() && Select(element[position.Index], index + 1, ref tooLarge);

            case JsonWildcardStep when element.ValueKind == JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (Select(item, index + 1, ref tooLarge))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private bool Matches(JsonElement leaf, ref bool tooLarge)
    {
        switch (leaf.ValueKind)
        {
            case JsonValueKind.String:
                return strings.Length > 0 && TextMatch.Any(op, leaf.GetString() ?? string.Empty, strings);

            case JsonValueKind.Number:
                foreach (decimal literal in numbers)
                {
                    if (ValueJudge.CompareNumber(leaf, op, literal, out bool inRange))
                    {
                        return true;
                    }

                    // A masked leaf is compared exactly and never hinted at: that a number is out of range would tell a visitor something about it.
                    tooLarge |= !inRange && !rawLeaf;
                }

                return false;

            case JsonValueKind.True or JsonValueKind.False:
                bool value = leaf.ValueKind == JsonValueKind.True;
                return op is PredicateOperator.Equals or PredicateOperator.In && booleans.Contains(value);

            case JsonValueKind.Null:
                return op is PredicateOperator.Equals or PredicateOperator.In && hasNull;

            default:
                return false;
        }
    }
}
