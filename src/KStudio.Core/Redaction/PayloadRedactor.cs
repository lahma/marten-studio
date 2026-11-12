using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using KStudio.Core.Decoding;
using KStudio.Core.Predicates;

namespace KStudio.Core.Redaction;

/// <summary>What the masked text of a value is: JSON, written compactly, or plain text.</summary>
internal enum KafkaValueTextKind
{
    /// <summary>JSON.</summary>
    Json,

    /// <summary>Text.</summary>
    Text,
}

/// <summary>Why a payload rule could not be applied to a value, for the engine's log (the kind only, never the rule or the value).</summary>
internal enum PayloadRedactionFailure
{
    /// <summary>Nothing failed.</summary>
    None,

    /// <summary>The rule cannot be applied to this kind of value: a path to text or to bytes, any rule to bytes.</summary>
    NotApplicable,

    /// <summary>A pattern ran out of time on this value.</summary>
    PatternTimeout,

    /// <summary>The value used up the whole time its rules may take.</summary>
    OutOfTime,

    /// <summary>A member name matches a pattern, so the masking cannot be made without keeping a name that holds the secret.</summary>
    MemberNameMatched,

    /// <summary>The masked text could not be written.</summary>
    WriteFailed,
}

/// <summary>A node a JSON-path rule masked: its concrete path (no <c>[*]</c>), written as the search writes paths, and the rule.</summary>
/// <param name="Steps">The steps from the root.</param>
/// <param name="Text">The path as text.</param>
/// <param name="Rule">The rule's name.</param>
internal sealed record KafkaMaskedPath(IReadOnlyList<JsonPathStep> Steps, string Text, string Rule);

/// <summary>
/// A value with the topic's payload rules applied (decision 3.2): the masked text, which parts were masked and by which rules, or the rule
/// that could not be applied and so withheld the whole value (fail closed).
/// </summary>
internal sealed class RedactedValue
{
    private JsonElement? parsed;
    private bool parsedDone;

    /// <summary>The masked text, whole; <see langword="null" /> when the value is withheld.</summary>
    public string? Text { get; init; }

    /// <summary>Whether <see cref="Text" /> is JSON or text.</summary>
    public KafkaValueTextKind? TextKind { get; init; }

    /// <summary>The nodes JSON-path rules masked, at most 64.</summary>
    public IReadOnlyList<KafkaMaskedPath> MaskedPaths { get; init; } = [];

    /// <summary>How many masked nodes are not in <see cref="MaskedPaths" />.</summary>
    public int MaskedPathsOmitted { get; init; }

    /// <summary>The distinct names of the rules that masked something.</summary>
    public IReadOnlyList<string> MaskedBy { get; init; } = [];

    /// <summary>The rule that could not be applied; the value is withheld whole.</summary>
    public string? WithheldBy { get; init; }

    /// <summary>Why it could not be applied.</summary>
    public PayloadRedactionFailure Failure { get; init; }

    /// <summary>The type name of the exception that stopped it, when one did.</summary>
    public string? ExceptionKind { get; init; }

    /// <summary>The masked JSON parsed (once), so that a path clause no rule covers can select from what the visitor would see.</summary>
    /// <param name="element">The root of the masked JSON.</param>
    public bool TryGetJson(out JsonElement element)
    {
        if (!parsedDone)
        {
            parsedDone = true;

            if (TextKind == KafkaValueTextKind.Json && Text is not null)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(Text, new JsonDocumentOptions { MaxDepth = 64 });
                    parsed = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    parsed = null;
                }
            }
        }

        element = parsed.GetValueOrDefault();
        return parsed is not null;
    }
}

/// <summary>
/// Applies a topic's JSON-path and pattern rules to a value (decision 3.2 of D-PAYLOAD). It runs in two places and only under the payload
/// grant: where a clause needs the masked form, and in the match factory before the value is cut to the match's budget. A rule it cannot
/// apply withholds the whole value: a value that cannot be masked is not shown, and no clause can read it.
/// </summary>
internal static class PayloadRedactor
{
    /// <summary>The most masked nodes whose paths are kept.</summary>
    public const int MaxMaskedPaths = 64;

    /// <summary>The longest member name kept in a masked path.</summary>
    public const int MaxMemberChars = 128;

    /// <summary>The most time the rules may take on one value, over every call.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    /// <summary>The text that stands in for a masked part.</summary>
    /// <param name="rule">The rule's name.</param>
    public static string MaskText(string rule) => "••• (redacted by " + rule + ")";

    /// <summary>Applies the rules to a value.</summary>
    /// <param name="value">The value, whole.</param>
    /// <param name="redactor">The topic's rules.</param>
    /// <param name="budget">The time left for the rules on this value.</param>
    /// <returns>The value with the rules applied; <see langword="null" /> when the topic has no payload rule or the value has nothing to mask (a tombstone, an empty value, an undecodable one, a CloudEvent without data).</returns>
    public static RedactedValue? Apply(RecordValue value, IKafkaRedactor redactor, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(redactor);

        if (!redactor.HasPayloadRules)
        {
            return null;
        }

        IReadOnlyList<RedactionRule> rules = redactor.PayloadRules;

        switch (value.Kind)
        {
            case KafkaValueKind.Binary or KafkaValueKind.ConfluentWireFormat:
                // The hex preview is the value's bytes: no rule can be applied to them.
                return Withheld(rules[0], PayloadRedactionFailure.NotApplicable, null);

            case KafkaValueKind.Text when value.Utf8 is not null:
                {
                    // A path cannot be found in text: the rule cannot be applied, so the text is withheld.
                    if (rules.FirstOrDefault(static x => x.Target == RedactionTarget.JsonPath) is { } pathRule)
                    {
                        return Withheld(pathRule, PayloadRedactionFailure.NotApplicable, null);
                    }

                    return new Run(rules, budget).Text(value.Text);
                }

            case KafkaValueKind.Json when value.Json is { } json:
                return new Run(rules, budget).Json(json);

            default:
                return null;
        }
    }

    private static RedactedValue Withheld(RedactionRule rule, PayloadRedactionFailure failure, Exception? exception) =>
        new() { WithheldBy = rule.Name, Failure = failure, ExceptionKind = exception?.GetType().Name };

    private sealed class Run
    {
        private readonly IReadOnlyList<RedactionRule> pathRules;
        private readonly IReadOnlyList<RedactionRule> patterns;
        private readonly long deadline;
        private readonly List<JsonPathStep> path = [];
        private readonly List<KafkaMaskedPath> masked = [];
        private readonly List<string> maskedBy = [];
        private int omitted;
        private RedactionRule current;

        public Run(IReadOnlyList<RedactionRule> rules, TimeSpan budget)
        {
            pathRules = [.. rules.Where(static x => x.Target == RedactionTarget.JsonPath)];
            patterns = [.. rules.Where(static x => x.Target == RedactionTarget.Pattern)];
            current = rules[0];
            deadline = Stopwatch.GetTimestamp() + (long) (Math.Max(0, budget.TotalSeconds) * Stopwatch.Frequency);
        }

        public RedactedValue Text(string text)
        {
            try
            {
                string result = Patterns(text);
                return Done(result, KafkaValueTextKind.Text);
            }
            catch (RegexMatchTimeoutException exception)
            {
                return Withheld(current, PayloadRedactionFailure.PatternTimeout, exception);
            }
            catch (OutOfTimeException exception)
            {
                return Withheld(current, PayloadRedactionFailure.OutOfTime, exception);
            }
        }

        public RedactedValue Json(JsonElement root)
        {
            ArrayBufferWriter<byte> buffer = new();

            try
            {
                using (Utf8JsonWriter writer = new(buffer, WriterOptions))
                {
                    Node(root, writer);
                }

                return Done(Encoding.UTF8.GetString(buffer.WrittenSpan), KafkaValueTextKind.Json);
            }
            catch (RegexMatchTimeoutException exception)
            {
                return Withheld(current, PayloadRedactionFailure.PatternTimeout, exception);
            }
            catch (OutOfTimeException exception)
            {
                return Withheld(current, PayloadRedactionFailure.OutOfTime, exception);
            }
            catch (MemberNameException exception)
            {
                return Withheld(current, PayloadRedactionFailure.MemberNameMatched, exception);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or JsonException)
            {
                return Withheld(current, PayloadRedactionFailure.WriteFailed, exception);
            }
        }

        private RedactedValue Done(string text, KafkaValueTextKind kind) =>
            new() { Text = text, TextKind = kind, MaskedPaths = masked, MaskedPathsOmitted = omitted, MaskedBy = maskedBy };

        private void Node(JsonElement element, Utf8JsonWriter writer)
        {
            CheckTime();

            if (pathRules.FirstOrDefault(x => x.Path!.Covers(path)) is { } rule)
            {
                writer.WriteStringValue(MaskText(rule.Name));
                Note(rule.Name);

                if (masked.Count < MaxMaskedPaths)
                {
                    IReadOnlyList<JsonPathStep> steps = [.. path];
                    masked.Add(new KafkaMaskedPath(steps, JsonPathExpression.Format(steps), rule.Name));
                }
                else
                {
                    omitted++;
                }

                return;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();

                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        // A name that holds what a pattern masks cannot be written: the name is the secret.
                        if (patterns.Count > 0 && MatchesAny(property.Name))
                        {
                            throw new MemberNameException();
                        }

                        writer.WritePropertyName(property.Name);
                        path.Add(new JsonMemberStep(property.Name.Length > MaxMemberChars ? property.Name[..MaxMemberChars] : property.Name));
                        Node(property.Value, writer);
                        path.RemoveAt(path.Count - 1);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    int index = 0;

                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        path.Add(new JsonIndexStep(index++));
                        Node(item, writer);
                        path.RemoveAt(path.Count - 1);
                    }

                    writer.WriteEndArray();
                    break;

                case JsonValueKind.String when patterns.Count > 0:
                    writer.WriteStringValue(Patterns(element.GetString() ?? string.Empty));
                    break;

                case JsonValueKind.Number when patterns.Count > 0:
                    {
                        // A number a pattern matches becomes a string: the digits are the secret.
                        string raw = element.GetRawText();
                        string result = Patterns(raw);

                        if (ReferenceEquals(result, raw) || result == raw)
                        {
                            element.WriteTo(writer);
                        }
                        else
                        {
                            writer.WriteStringValue(result);
                        }

                        break;
                    }

                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private bool MatchesAny(string text)
        {
            foreach (RedactionRule rule in patterns)
            {
                CheckTime();
                current = rule;

                if (rule.Pattern!.IsMatch(text))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Every match of every pattern replaced by the mask: overlapping matches become one, named for the one that starts first.</summary>
        private string Patterns(string text)
        {
            if (patterns.Count == 0)
            {
                return text;
            }

            List<(int Start, int Length, RedactionRule Rule)> hits = [];

            foreach (RedactionRule rule in patterns)
            {
                current = rule;

                foreach (ValueMatch match in rule.Pattern!.EnumerateMatches(text))
                {
                    CheckTime();

                    if (match.Length > 0)
                    {
                        hits.Add((match.Index, match.Length, rule));
                    }
                }
            }

            if (hits.Count == 0)
            {
                return text;
            }

            hits.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
            StringBuilder result = new(text.Length);
            int position = 0;
            int index = 0;

            while (index < hits.Count)
            {
                (int start, int length, RedactionRule rule) = hits[index++];
                int end = start + length;

                while (index < hits.Count && hits[index].Start < end)
                {
                    end = Math.Max(end, hits[index].Start + hits[index].Length);
                    index++;
                }

                result.Append(text, position, start - position).Append(MaskText(rule.Name));
                Note(rule.Name);
                position = end;
            }

            result.Append(text, position, text.Length - position);
            return result.ToString();
        }

        private void Note(string rule)
        {
            if (!maskedBy.Contains(rule, StringComparer.Ordinal))
            {
                maskedBy.Add(rule);
            }
        }

        private void CheckTime()
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new OutOfTimeException();
            }
        }
    }

    private sealed class OutOfTimeException : Exception;

    private sealed class MemberNameException : Exception;
}
