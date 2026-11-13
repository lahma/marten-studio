using KStudio.Core.Clusters;
using KStudio.Core.Correlation;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;

namespace KStudio.Core.Index;

/// <summary>The one equality a plan asks the host's index about.</summary>
/// <param name="Field">Which field.</param>
/// <param name="FieldName">The header's or attribute's name; <see langword="null" /> for the key.</param>
/// <param name="Value">The literal, as typed.</param>
/// <param name="CorrelationKind">The kind the host declared for the field on the topics, when they agree on one.</param>
internal sealed record IndexClause(KafkaIndexField Field, string? FieldName, string Value, string? CorrelationKind);

/// <summary>
/// Chooses what a search asks the host's record index (AGENTS.md D48, D-M5 decision 5.2): an equality on <c>key</c>, <c>header.&lt;name&gt;</c> or
/// <c>ce.&lt;attribute&gt;</c> with a literal, in the search's top-level conjunction, on a field no topic redacts. One literal per question: an
/// <c>in</c> list, a prefix, a range, <c>any</c>, a value and a JSON path are never asked, and nothing under an <c>or</c> or a <c>not</c> is.
/// </summary>
internal static class IndexClauseSelector
{
    /// <summary>The clause to ask about, or <see langword="null" /> when the search has none worth asking.</summary>
    /// <param name="tree">The parsed search; <see langword="null" /> for an empty one.</param>
    /// <param name="descriptors">The topics' descriptors, for the correlation fields they declare.</param>
    /// <param name="redactors">Every searched topic's redactor: a field any of them masks is never asked.</param>
    public static IndexClause? Choose(PredicateNode? tree, IReadOnlyList<KafkaTopicDescriptor> descriptors, IReadOnlyList<IKafkaRedactor> redactors)
    {
        List<ClauseNode> candidates = [];
        Collect(tree, candidates);

        ClauseNode? chosen = candidates
            .Where(clause => !redactors.Any(redactor => PredicateCompiler.IsRedacted(clause.Field, redactor)))
            .Select((clause, order) => (Clause: clause, Order: order, Rank: Rank(clause.Field, descriptors)))
            .OrderBy(static x => x.Rank)
            .ThenBy(static x => x.Order)
            .Select(static x => x.Clause)
            .FirstOrDefault();

        if (chosen is null)
        {
            return null;
        }

        string value = ((StringLiteral) chosen.Literals[0]).Value;

        return chosen.Field switch
        {
            KeyField => new IndexClause(KafkaIndexField.Key, null, value, Kind(CorrelationSource.Key, "key", descriptors)),
            HeaderField header => new IndexClause(KafkaIndexField.Header, header.Name, value, Kind(CorrelationSource.Header, header.Name, descriptors)),
            CloudEventField ce => new IndexClause(KafkaIndexField.CloudEventAttribute, ce.Attribute, value, Kind(CorrelationSource.CloudEvent, ce.Attribute, descriptors)),
            _ => null,
        };
    }

    private static void Collect(PredicateNode? node, List<ClauseNode> into)
    {
        switch (node)
        {
            case AndNode and:
                foreach (PredicateNode child in and.Children)
                {
                    Collect(child, into);
                }

                break;

            case ClauseNode { Operator: PredicateOperator.Equals, Field: KeyField or HeaderField or CloudEventField, Literals: [StringLiteral] } clause:
                into.Add(clause);
                break;
        }
    }

    private static int Rank(FieldNode field, IReadOnlyList<KafkaTopicDescriptor> descriptors) =>
        field switch
        {
            KeyField => 0,
            HeaderField header when Declared(CorrelationSource.Header, header.Name, descriptors) => 1,
            CloudEventField ce when Declared(CorrelationSource.CloudEvent, ce.Attribute, descriptors) => 1,
            _ => 2,
        };

    private static bool Declared(CorrelationSource source, string name, IReadOnlyList<KafkaTopicDescriptor> descriptors) =>
        descriptors.Any(d => d.CorrelationFields.Any(f => f.Source == source && string.Equals(f.Name, name, StringComparison.Ordinal)));

    private static string? Kind(CorrelationSource source, string name, IReadOnlyList<KafkaTopicDescriptor> descriptors)
    {
        string[] kinds =
        [
            .. descriptors
                .SelectMany(static d => d.CorrelationFields)
                .Where(f => f.Source == source && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(static f => f.Kind)
                .Distinct(StringComparer.Ordinal),
        ];

        return kinds.Length == 1 ? kinds[0] : null;
    }
}
