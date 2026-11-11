using System.Text;

using KStudio.Core.Decoding;
using KStudio.Core.Redaction;
using KStudio.Core.Wire;

namespace KStudio.Core.Predicates;

/// <summary>The three values of the predicate logic (decision 7.5, Kleene): a record that could not be judged is neither a match nor a non-match.</summary>
internal enum TriState
{
    /// <summary>The record does not satisfy the condition.</summary>
    False,

    /// <summary>The record satisfies it.</summary>
    True,

    /// <summary>It could not be decided: the record is counted per reason and shown beside every negative.</summary>
    Unknown,
}

/// <summary>How a clause is run on a topic, decided before a scan and shown before it (decision 7.5).</summary>
internal enum ClauseVerdictKind
{
    /// <summary>Compared as bytes: cheap.</summary>
    Scan,

    /// <summary>Needs a decode of every record it reaches: slow.</summary>
    SlowScan,
}

/// <summary>What the engine says about one clause on one topic before it reads anything.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Field">The clause's field, without its literals.</param>
/// <param name="Kind">How it runs.</param>
/// <param name="Description">The sentence.</param>
internal sealed record ClauseVerdict(string Topic, string Field, ClauseVerdictKind Kind, string Description);

/// <summary>A node of the compiled tree.</summary>
internal abstract class CompiledNode
{
    /// <summary>The cost class of the costliest clause in the subtree (decision 7.5): the order children are tried in.</summary>
    public abstract int Cost { get; }

    /// <summary>Judges a record.</summary>
    /// <param name="context">The record.</param>
    public abstract TriState Evaluate(RecordEvaluationContext context);
}

/// <summary>A predicate compiled for one topic: its key format, its redaction and its grant are fixed.</summary>
internal sealed class CompiledPredicate
{
    private readonly CompiledNode? root;

    /// <summary>Creates a compiled predicate.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="root">The tree; <see langword="null" /> matches every record.</param>
    /// <param name="verdicts">What is said about each clause.</param>
    /// <param name="auditText">The text for a sink: literals of redacted fields as <c>&lt;redacted&gt;</c>.</param>
    public CompiledPredicate(string topic, CompiledNode? root, IReadOnlyList<ClauseVerdict> verdicts, string auditText)
    {
        Topic = topic;
        this.root = root;
        Verdicts = verdicts;
        AuditText = auditText;
    }

    /// <summary>The topic.</summary>
    public string Topic { get; }

    /// <summary>What is said about each clause.</summary>
    public IReadOnlyList<ClauseVerdict> Verdicts { get; }

    /// <summary>The canonical text for the audit and for a search link: a literal on a redacted field is shown as <c>&lt;redacted&gt;</c> (decision 10.5).</summary>
    public string AuditText { get; }

    /// <summary>Whether the predicate has no condition and so matches every record.</summary>
    public bool MatchesEverything => root is null;

    /// <summary>Judges a record: only <see cref="TriState.True" /> is a match.</summary>
    /// <param name="context">The record.</param>
    public TriState Evaluate(RecordEvaluationContext context) => root is null ? TriState.True : root.Evaluate(context);
}

internal sealed class CompiledAnd(IReadOnlyList<CompiledNode> children) : CompiledNode
{
    // Stable: children of equal cost keep the order they were typed in.
    private readonly CompiledNode[] ordered = [.. children.OrderBy(static x => x.Cost)];

    public override int Cost { get; } = children.Max(static x => x.Cost);

    public override TriState Evaluate(RecordEvaluationContext context)
    {
        TriState result = TriState.True;
        KafkaUnknownReason? reason = null;

        foreach (CompiledNode child in ordered)
        {
            context.ClearUnknown();

            switch (child.Evaluate(context))
            {
                case TriState.False:
                    context.ClearUnknown();
                    return TriState.False;
                case TriState.Unknown:
                    result = TriState.Unknown;
                    reason ??= context.UnknownReason;
                    break;
            }
        }

        // Only the clauses that left the result unknown say why; an unknown child that another child made false does not.
        context.SetUnknown(reason);
        return result;
    }
}

internal sealed class CompiledOr(IReadOnlyList<CompiledNode> children) : CompiledNode
{
    private readonly CompiledNode[] ordered = [.. children.OrderBy(static x => x.Cost)];

    public override int Cost { get; } = children.Max(static x => x.Cost);

    public override TriState Evaluate(RecordEvaluationContext context)
    {
        TriState result = TriState.False;
        KafkaUnknownReason? reason = null;

        foreach (CompiledNode child in ordered)
        {
            context.ClearUnknown();

            switch (child.Evaluate(context))
            {
                case TriState.True:
                    context.ClearUnknown();
                    return TriState.True;
                case TriState.Unknown:
                    result = TriState.Unknown;
                    reason ??= context.UnknownReason;
                    break;
            }
        }

        context.SetUnknown(reason);
        return result;
    }
}

internal sealed class CompiledNot(CompiledNode operand) : CompiledNode
{
    public override int Cost => operand.Cost;

    public override TriState Evaluate(RecordEvaluationContext context) =>
        operand.Evaluate(context) switch
        {
            TriState.True => TriState.False,
            TriState.False => TriState.True,
            _ => TriState.Unknown,
        };
}

/// <summary>A clause on the key (cost 0 for bytes, 3 for <c>~</c>).</summary>
internal sealed class CompiledKeyClause(PredicateOperator op, byte[][] forms, string[] texts, bool decodeEscapes = false) : CompiledNode
{
    private const byte Backslash = (byte) '\\';

    public override int Cost { get; } = op == PredicateOperator.Contains ? 3 : 0;

    public override TriState Evaluate(RecordEvaluationContext context)
    {
        byte[]? key = context.Record.Key;

        switch (op)
        {
            case PredicateOperator.IsNull:
                return key is null ? TriState.True : TriState.False;
            case PredicateOperator.IsNotNull:
                return key is not null ? TriState.True : TriState.False;
        }

        if (key is null)
        {
            return TriState.False;
        }

        switch (op)
        {
            case PredicateOperator.Equals or PredicateOperator.In:
                foreach (byte[] form in forms)
                {
                    if (key.AsSpan().SequenceEqual(form))
                    {
                        return TriState.True;
                    }
                }

                return EscapedKeyMatches(context, key) ? TriState.True : TriState.False;

            case PredicateOperator.Prefix:
                foreach (byte[] form in forms)
                {
                    if (key.AsSpan().StartsWith(form))
                    {
                        return TriState.True;
                    }
                }

                return EscapedKeyMatches(context, key) ? TriState.True : TriState.False;

            default:
                if (!context.TryKeyText(out string text))
                {
                    context.MarkUnknown(KafkaUnknownReason.KeyNotText);
                    return TriState.Unknown;
                }

                return TextMatch.Any(op, text, texts) ? TriState.True : TriState.False;
        }
    }

    /// <summary>
    /// The byte forms are the fast path and cover the two escapings the studio knows. A JSON serializer may escape differently (Python's
    /// <c>json.dumps</c> writes <c>é</c> for é, Go escapes <c>&lt;</c>), so when no form matched and the key contains a backslash, it is
    /// decoded once as JSON (counted as a key decode) and its text is compared.
    /// </summary>
    private bool EscapedKeyMatches(RecordEvaluationContext context, byte[] key)
    {
        if (!decodeEscapes || !key.AsSpan().Contains(Backslash) || !context.TryKeyText(out string text))
        {
            return false;
        }

        return TextMatch.Any(op == PredicateOperator.Prefix ? PredicateOperator.Prefix : PredicateOperator.Equals, text, texts);
    }
}

/// <summary>A clause on a header (cost 1 for bytes, 3 for <c>~</c>).</summary>
internal sealed class CompiledHeaderClause(string name, PredicateOperator op, byte[][] forms, string[] texts) : CompiledNode
{
    public override int Cost { get; } = op == PredicateOperator.Contains ? 3 : 1;

    public override TriState Evaluate(RecordEvaluationContext context)
    {
        WireHeader[] headers = context.Record.Headers;
        bool present = false;
        bool unreadable = false;

        for (int i = 0; i < headers.Length; i++)
        {
            WireHeader header = headers[i];

            if (!header.Name.Equals(name, StringComparison.Ordinal) || header.Value is not { } value)
            {
                continue;
            }

            present = true;

            switch (op)
            {
                case PredicateOperator.Equals or PredicateOperator.In:
                    foreach (byte[] form in forms)
                    {
                        if (value.AsSpan().SequenceEqual(form))
                        {
                            return TriState.True;
                        }
                    }

                    break;

                case PredicateOperator.Prefix:
                    foreach (byte[] form in forms)
                    {
                        if (value.AsSpan().StartsWith(form))
                        {
                            return TriState.True;
                        }
                    }

                    break;

                case PredicateOperator.Contains:
                    if (context.TryHeaderText(i, out string text))
                    {
                        if (TextMatch.Any(op, text, texts))
                        {
                            return TriState.True;
                        }
                    }
                    else
                    {
                        unreadable = true;
                    }

                    break;
            }
        }

        switch (op)
        {
            case PredicateOperator.IsNull:
                return present ? TriState.False : TriState.True;
            case PredicateOperator.IsNotNull:
                return present ? TriState.True : TriState.False;
        }

        if (unreadable)
        {
            context.MarkUnknown(KafkaUnknownReason.HeaderNotText);
            return TriState.Unknown;
        }

        return TriState.False;
    }
}

/// <summary>A clause on a CloudEvents attribute (cost 2 when read from headers, 4 when the value is parsed).</summary>
internal sealed class CompiledCloudEventClause(string attribute, PredicateOperator op, string[] texts, bool structured) : CompiledNode
{
    public override int Cost { get; } = structured ? 4 : 2;

    public override TriState Evaluate(RecordEvaluationContext context)
    {
        CloudEventView view = context.CloudEvent;

        if (view.Unreadable is { } reason)
        {
            context.MarkUnknown(reason switch
            {
                KafkaUndecodableReason.TooLarge => KafkaUnknownReason.TooLarge,
                KafkaUndecodableReason.NotACloudEvent => KafkaUnknownReason.NotACloudEvent,
                _ => KafkaUnknownReason.InvalidJson,
            });
            return TriState.Unknown;
        }

        switch (op)
        {
            case PredicateOperator.IsNull:
                // A record that is not a CloudEvent has no attribute: true.
                return view.Events.Count == 0 || view.Events.Any(x => !x.Attributes.ContainsKey(attribute)) ? TriState.True : TriState.False;
            case PredicateOperator.IsNotNull:
                return view.Events.Any(x => x.Attributes.ContainsKey(attribute)) ? TriState.True : TriState.False;
        }

        // In a batch, a clause is true if it is true of any event.
        foreach (KafkaCloudEvent cloudEvent in view.Events)
        {
            if (cloudEvent.Attributes.TryGetValue(attribute, out string? text) && TextMatch.Any(op, text, texts, ordinalEquality: true))
            {
                return TriState.True;
            }
        }

        return TriState.False;
    }
}

/// <summary><c>any</c>: the key's text, every header's text and every CloudEvents attribute, except the fields redaction masks (decision 10.6).</summary>
internal sealed class CompiledAnyClause(PredicateOperator op, string[] texts, IKafkaRedactor redactor) : CompiledNode
{
    public override int Cost => 5;

    public override TriState Evaluate(RecordEvaluationContext context)
    {
        // "Some field is true" decides the clause; otherwise a field that could not be judged leaves it unknown, never false (decision 7.5).
        // A field that is not text can only fail to be judged for `~` (an equality or a prefix of a text literal is simply not met by bytes
        // that are not that text), and a masked field is never looked at, so it cannot make the clause unknown either.
        KafkaUnknownReason? unknown = null;
        bool contains = op == PredicateOperator.Contains;

        if (context.Record.Key is not null && redactor.ForKey() is null)
        {
            if (context.TryKeyText(out string key))
            {
                if (TextMatch.Any(op, key, texts, ordinalEquality: true))
                {
                    return TriState.True;
                }
            }
            else if (contains)
            {
                unknown ??= KafkaUnknownReason.KeyNotText;
            }
        }

        WireHeader[] headers = context.Record.Headers;

        for (int i = 0; i < headers.Length; i++)
        {
            // A masked header is never looked at: the deny-list is in this check, so a secret cannot be found one character at a time.
            if (headers[i].Value is null || redactor.ForHeader(headers[i].Name) is not null)
            {
                continue;
            }

            if (context.TryHeaderText(i, out string text))
            {
                if (TextMatch.Any(op, text, texts, ordinalEquality: true))
                {
                    return TriState.True;
                }
            }
            else if (contains)
            {
                unknown ??= KafkaUnknownReason.HeaderNotText;
            }
        }

        CloudEventView view = context.CloudEvent;

        if (view.Unreadable is { } reason)
        {
            unknown ??= reason switch
            {
                KafkaUndecodableReason.TooLarge => KafkaUnknownReason.TooLarge,
                KafkaUndecodableReason.NotACloudEvent => KafkaUnknownReason.NotACloudEvent,
                _ => KafkaUnknownReason.InvalidJson,
            };
        }
        else
        {
            foreach (KafkaCloudEvent cloudEvent in view.Events)
            {
                foreach (KeyValuePair<string, string> attribute in cloudEvent.Attributes)
                {
                    if (redactor.ForCloudEvent(attribute.Key) is null && TextMatch.Any(op, attribute.Value, texts, ordinalEquality: true))
                    {
                        return TriState.True;
                    }
                }
            }
        }

        if (unknown is { } why)
        {
            context.MarkUnknown(why);
            return TriState.Unknown;
        }

        return TriState.False;
    }
}

/// <summary>Text comparison: ordinal for equality and prefix, ignoring case for contains (decision 7.4).</summary>
internal static class TextMatch
{
    /// <summary>Whether <paramref name="value" /> satisfies the operator against any of <paramref name="literals" />.</summary>
    /// <param name="op">The operator.</param>
    /// <param name="value">The text of the field.</param>
    /// <param name="literals">The literals.</param>
    /// <param name="ordinalEquality">Kept for readability at the call sites: equality is always ordinal.</param>
    public static bool Any(PredicateOperator op, string value, string[] literals, bool ordinalEquality = true)
    {
        _ = ordinalEquality;

        foreach (string literal in literals)
        {
            bool hit = op switch
            {
                PredicateOperator.Equals or PredicateOperator.In => string.Equals(value, literal, StringComparison.Ordinal),
                PredicateOperator.Prefix => value.StartsWith(literal, StringComparison.Ordinal),
                PredicateOperator.Contains => value.Contains(literal, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The UTF-8 bytes of a literal.</summary>
    /// <param name="literal">The literal.</param>
    public static byte[] Utf8(string literal) => Encoding.UTF8.GetBytes(literal);
}
