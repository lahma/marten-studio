using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using KStudio.Core.Clusters;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;

namespace KStudio.Core.Correlation;

/// <summary>
/// The search one topic of a related-records scan runs.
/// </summary>
/// <param name="Topic">The topic.</param>
/// <param name="PredicateText">The predicate, in the search grammar; <see langword="null" /> when no clause is left: the topic is not scanned, and <paramref name="Notes" /> say why.</param>
/// <param name="ReadsValues">Whether the predicate reads the value (it holds a JSON path), so the topic needs the payload grant.</param>
/// <param name="Notes">What was left out of this topic's search and why, in words that name no value.</param>
internal sealed record CorrelationTopicQuery(KafkaTopicName Topic, string? PredicateText, bool ReadsValues, IReadOnlyList<string> Notes)
{
    /// <summary>Whether there is a search to run.</summary>
    public bool HasQuery => PredicateText is not null;
}

/// <summary>
/// Writes the predicate one topic is searched with, from the values of an anchor and the topic's own correlation fields (D42, decision 1.5
/// of D-CORR). Pure: it reads the topic's declarations and redaction rules and nothing else. Every text it writes parses and compiles on the
/// topic; a field it cannot search is left out, with the reason, and never makes the whole scan fail.
/// </summary>
internal static partial class CorrelationQueryBuilder
{
    /// <summary>The order whole kinds are dropped in when the search would be too long: the most speculative first.</summary>
    private static readonly string[] DropOrder = ["trace", "host", "document", "saga", "message", "correlation"];

    /// <summary>Builds the search for <paramref name="topic" />.</summary>
    /// <param name="keys">The anchor's values, or a seed.</param>
    /// <param name="topic">The topic: its correlation fields and key format.</param>
    /// <param name="redactor">The topic's redaction rules.</param>
    /// <param name="payloadGranted">Whether the visitor may read payloads: a JSON path is searched only then.</param>
    public static CorrelationTopicQuery Build(CorrelationKeySet keys, KafkaTopicDescriptor topic, IKafkaRedactor redactor, bool payloadGranted)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(redactor);

        List<Clause> clauses = [];
        List<string> notes = [];
        KafkaTopicName name = new(topic.Name);

        foreach (CorrelationField field in topic.CorrelationFields)
        {
            CorrelationValue[] values = [.. keys.Of(field.Kind)];

            if (values.Length == 0)
            {
                continue;
            }

            if (field.IsTrace)
            {
                AddTrace(clauses, notes, field, values, redactor);
            }
            else
            {
                AddField(clauses, notes, field, values, topic, redactor, payloadGranted);
            }
        }

        clauses = Bounded(clauses, notes);

        if (clauses.Count == 0)
        {
            return new CorrelationTopicQuery(name, null, false, Distinct(notes));
        }

        return new CorrelationTopicQuery(name, Text(clauses), clauses.Any(static x => x.ReadsValues), Distinct(notes));
    }

    private static void AddTrace(List<Clause> clauses, List<string> notes, CorrelationField field, CorrelationValue[] values, IKafkaRedactor redactor)
    {
        if (redactor.ForHeader(field.Name) is not null)
        {
            // `^=` is refused on a redacted field (D40), searchable or not.
            notes.Add("`" + field.Grammar + "` is redacted on this topic and is not searched.");
            return;
        }

        foreach (CorrelationValue value in values)
        {
            clauses.Add(new Clause(field.Kind, field.Grammar + " ^= " + PredicateFormatter.QuoteText("00-" + value.Value), false));
        }
    }

    private static void AddField(
        List<Clause> clauses,
        List<string> notes,
        CorrelationField field,
        CorrelationValue[] values,
        KafkaTopicDescriptor topic,
        IKafkaRedactor redactor,
        bool payloadGranted)
    {
        RedactionRule? rule = field.Source switch
        {
            CorrelationSource.Header => redactor.ForHeader(field.Name),
            CorrelationSource.CloudEvent => redactor.ForCloudEvent(field.Name),
            CorrelationSource.Key => redactor.ForKey(),
            _ => field.Path is { } path ? redactor.ForJsonPath(path) : null,
        };

        if (field.Source == CorrelationSource.Header && HeaderDenyList.Contains(field.Name))
        {
            notes.Add("`" + field.Grammar + "` is never searched.");
            return;
        }

        if (field.Source == CorrelationSource.JsonPath && !payloadGranted)
        {
            notes.Add("`" + field.Grammar + "` needs `ReadPayloads`.");
            return;
        }

        // `=` and `in` search a redacted field only where the rule says it may be searched.
        if (rule is not null && !rule.Searchable)
        {
            notes.Add("`" + field.Grammar + "` is redacted on this topic and is not searched.");
            return;
        }

        List<string> literals = [];
        int unsearchable = 0;

        foreach (CorrelationValue value in values)
        {
            if (field.Source == CorrelationSource.Key && !topic.KeyFormat.TryEncode(value.Value, out _, out _))
            {
                unsearchable++;
                continue;
            }

            Literals(field, value, literals);
        }

        if (unsearchable > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{field.Grammar}: {unsearchable} {(unsearchable == 1 ? "value cannot" : "values cannot")} be held in this topic's key format and {(unsearchable == 1 ? "was" : "were")} not searched."));
        }

        if (literals.Count == 0)
        {
            return;
        }

        string text = literals.Count == 1
            ? field.Grammar + " = " + literals[0]
            : field.Grammar + " in (" + string.Join(", ", literals.Take(PredicateParser.MaxListValues)) + ")";

        clauses.Add(new Clause(field.Kind, text, field.Source == CorrelationSource.JsonPath));
    }

    /// <summary>
    /// The literals one value is searched as. A text field compares text. A path compares by type, so a value that reads as a number or a
    /// boolean is also searched as one: the same id is a string on one topic and a number on another.
    /// </summary>
    private static void Literals(CorrelationField field, CorrelationValue value, List<string> literals)
    {
        void Add(string literal)
        {
            if (!literals.Contains(literal, StringComparer.Ordinal))
            {
                literals.Add(literal);
            }
        }

        // Every value is searched as its text, whatever it was where it was read: a JSON number is a header's text on another topic.
        Add(PredicateFormatter.QuoteText(value.Value));

        if (field.Source != CorrelationSource.JsonPath)
        {
            return;
        }

        // Only a canonical number: "007" is a text id, and the number 7 is not it.
        if (CanonicalNumber().IsMatch(value.Value) && decimal.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            Add(value.Value);
        }
        else if (value.Value is "true" or "false")
        {
            Add(value.Value);
        }
    }

    /// <summary>
    /// Keeps the search inside the parser's bounds (32 clauses, 2,000 characters): over a bound, whole kinds are dropped, the most speculative
    /// first, and each drop is said.
    /// </summary>
    private static List<Clause> Bounded(List<Clause> clauses, List<string> notes)
    {
        int step = 0;

        while (clauses.Count > 0 && (clauses.Count > PredicateParser.MaxClauses || Text(clauses).Length > PredicateParser.MaxTextLength) && step < DropOrder.Length)
        {
            string group = DropOrder[step++];
            Clause[] dropped = [.. clauses.Where(x => GroupOf(x.Kind) == group)];

            if (dropped.Length == 0)
            {
                continue;
            }

            foreach (string kind in dropped.Select(static x => x.Kind).Distinct(StringComparer.Ordinal))
            {
                notes.Add("`" + kind + "` was not searched on this topic: the search would be too long.");
            }

            clauses = [.. clauses.Where(x => GroupOf(x.Kind) != group)];
        }

        return clauses;
    }

    private static string GroupOf(string kind) =>
        kind switch
        {
            CorrelationField.TraceKind => "trace",
            CorrelationDefaults.SagaKind => "saga",
            CorrelationDefaults.MessageKind => "message",
            CorrelationDefaults.CorrelationKind => "correlation",
            CorrelationDefaults.StreamKind => "document",
            _ when kind.StartsWith(CorrelationDefaults.DocumentPrefix, StringComparison.Ordinal) => "document",
            _ => "host",
        };

    private static string Text(List<Clause> clauses)
    {
        StringBuilder text = new();

        foreach (Clause clause in clauses)
        {
            if (text.Length > 0)
            {
                text.Append(" or ");
            }

            text.Append(clause.Text);
        }

        return text.ToString();
    }

    private static string[] Distinct(List<string> notes) => [.. notes.Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"\A-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CanonicalNumber();

    private readonly record struct Clause(string Kind, string Text, bool ReadsValues);
}
