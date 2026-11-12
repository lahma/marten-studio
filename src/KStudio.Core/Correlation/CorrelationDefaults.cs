using KStudio.Core.Predicates;

namespace KStudio.Core.Correlation;

/// <summary>What a topic's declarations come to, and the defaults they start from (D42, decisions 1.2 and 1.3 of D-CORR).</summary>
internal static class CorrelationDefaults
{
    /// <summary>The most fields a topic has, the defaults included.</summary>
    public const int MaxFields = 16;

    /// <summary>The kind of an envelope id: a parent's <c>id</c> meets its children's <c>conversation-id</c> and <c>causation-id</c>.</summary>
    public const string MessageKind = "message";

    /// <summary>The kind of a correlation id.</summary>
    public const string CorrelationKind = "correlation";

    /// <summary>The kind of a saga id.</summary>
    public const string SagaKind = "saga";

    /// <summary>The kind of every stream field.</summary>
    public const string StreamKind = "stream";

    /// <summary>The prefix of every document kind.</summary>
    public const string DocumentPrefix = "document:";

    /// <summary>
    /// The defaults: Wolverine's headers and CloudEvents' <c>id</c> (D-CORR F7), and the three headers a trace context is read from. Wolverine's
    /// <c>tenant-id</c> is not here: it is a scope, too broad to correlate on.
    /// </summary>
    public static IReadOnlyList<CorrelationField> Defaults { get; } =
    [
        new(CorrelationSource.Header, "correlation-id", CorrelationKind, IsDefault: true),
        new(CorrelationSource.Header, "id", MessageKind, IsDefault: true),
        new(CorrelationSource.Header, "conversation-id", MessageKind, IsDefault: true),
        new(CorrelationSource.Header, "causation-id", MessageKind, IsDefault: true),
        new(CorrelationSource.CloudEvent, "id", MessageKind, IsDefault: true),
        new(CorrelationSource.Header, "saga-id", SagaKind, IsDefault: true),
        new(CorrelationSource.Header, "traceparent", CorrelationField.TraceKind, IsDefault: true),
        new(CorrelationSource.Header, "ce_traceparent", CorrelationField.TraceKind, IsDefault: true),
        new(CorrelationSource.Header, "parent-id", CorrelationField.TraceKind, IsDefault: true),
    ];

    /// <summary>
    /// The fields of a topic: the defaults (unless <c>WithoutDefaults()</c> was called) with the host's own, a host field replacing the
    /// default of the same source and name. The result may be over <see cref="MaxFields" /> or hold a bad declaration; validation says so
    /// before a registry is built, so this never throws.
    /// </summary>
    /// <param name="declared">What the topic declared; <see langword="null" /> for a topic that declared none.</param>
    public static IReadOnlyList<CorrelationField> Resolve(CorrelationDeclarations? declared)
    {
        if (declared is null)
        {
            return Defaults;
        }

        List<CorrelationField> fields = [];

        if (!declared.WithoutDefaults)
        {
            foreach (CorrelationField fallback in Defaults)
            {
                if (!declared.Fields.Any(x => Same(x, fallback)))
                {
                    fields.Add(fallback);
                }
            }
        }

        foreach (KafkaCorrelationField field in declared.Fields)
        {
            CorrelationField? replaced = declared.WithoutDefaults ? null : Defaults.FirstOrDefault(x => Same(field, x));
            fields.Add(Resolved(field, replaced));
        }

        return fields;
    }

    /// <summary>Whether two declared fields have the same source and name (names of headers and attributes compared ignoring case): validation refuses a second.</summary>
    /// <param name="first">One field.</param>
    /// <param name="second">The other.</param>
    public static bool Same(KafkaCorrelationField first, KafkaCorrelationField second) =>
        first.Source == second.Source && string.Equals(Normalized(first.Source, first.Name), Normalized(second.Source, second.Name), StringComparison.Ordinal);

    private static bool Same(KafkaCorrelationField declared, CorrelationField other) =>
        declared.Source == other.Source && string.Equals(Normalized(declared.Source, declared.Name), Normalized(other.Source, other.Name), StringComparison.Ordinal);

    private static string Normalized(CorrelationSource source, string name)
    {
        switch (source)
        {
            case CorrelationSource.Header:
            case CorrelationSource.CloudEvent:
                return name.ToLowerInvariant();
            case CorrelationSource.Key:
                return "key";
            default:
                return JsonPathExpression.TryParse(name, 0, out JsonPathExpression? path, out _) ? path!.Text : name;
        }
    }

    private static CorrelationField Resolved(KafkaCorrelationField field, CorrelationField? replaced)
    {
        JsonPathExpression? path = null;
        string name = field.Name;

        if (field.Source == CorrelationSource.JsonPath && JsonPathExpression.TryParse(field.Name, 0, out path, out _))
        {
            name = path!.Text;
        }

        string kind =
            field.KindName
            ?? (field.DocumentType is not null ? DocumentPrefix + field.DocumentType : null)
            ?? (field.IsStream ? StreamKind : null)
            ?? replaced?.Kind
            ?? DerivedKind(field.Source, name);

        return new CorrelationField(field.Source, name, kind, path, field.DocumentType, field.StoreName, field.IsStream, IsDefault: false);
    }

    /// <summary>The kind a field has when nothing else names one: its own name (a header's or attribute's folded to lower case, so two spellings are one kind).</summary>
    private static string DerivedKind(CorrelationSource source, string name) =>
        source switch
        {
            CorrelationSource.Header or CorrelationSource.CloudEvent => name.ToLowerInvariant(),
            CorrelationSource.Key => "key",
            _ => name,
        };
}
