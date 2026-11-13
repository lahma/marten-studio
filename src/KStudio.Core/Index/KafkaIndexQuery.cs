namespace KStudio;

/// <summary>
/// What a search looks for: one value of one field, in a window, on topics the visitor may search. It is asked only after every gate of the search
/// passed, names only topics the visitor may search, and carries the visitor's literal only for a field no rule redacts on any of those topics.
/// </summary>
public sealed class KafkaIndexQuery
{
    internal KafkaIndexQuery(
        string cluster,
        string? tenantId,
        IReadOnlyList<string> topics,
        DateTimeOffset from,
        DateTimeOffset to,
        KafkaIndexField field,
        string? fieldName,
        string? correlationKind,
        string value)
    {
        Cluster = cluster;
        TenantId = tenantId;
        Topics = topics;
        From = from;
        To = to;
        Field = field;
        FieldName = fieldName;
        CorrelationKind = correlationKind;
        Value = value;
    }

    /// <summary>The cluster's key, with its tenant suffix for a broker-per-tenant cluster.</summary>
    public string Cluster { get; }

    /// <summary>The tenant of a broker-per-tenant cluster; <see langword="null" /> for any other.</summary>
    public string? TenantId { get; }

    /// <summary>The topics the search reads: only topics the visitor may search.</summary>
    public IReadOnlyList<string> Topics { get; }

    /// <summary>The window's start, inclusive (UTC).</summary>
    public DateTimeOffset From { get; }

    /// <summary>The window's end, exclusive (UTC).</summary>
    public DateTimeOffset To { get; }

    /// <summary>The field the value is looked for in.</summary>
    public KafkaIndexField Field { get; }

    /// <summary>The header's or attribute's name; <see langword="null" /> for the key.</summary>
    public string? FieldName { get; }

    /// <summary>The correlation kind the host declared for that field on these topics (AGENTS.md D42), when there is one and the topics agree on it.</summary>
    public string? CorrelationKind { get; }

    /// <summary>The literal, as typed.</summary>
    public string Value { get; }
}
