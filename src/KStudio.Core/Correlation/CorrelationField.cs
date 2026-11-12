using KStudio.Core.Predicates;

namespace KStudio.Core.Correlation;

/// <summary>Where a correlation field is read (D42).</summary>
internal enum CorrelationSource
{
    /// <summary>A header, by name.</summary>
    Header,

    /// <summary>A CloudEvents attribute, by name.</summary>
    CloudEvent,

    /// <summary>The record's key.</summary>
    Key,

    /// <summary>A path into the value; read only under <c>ReadPayloads</c>.</summary>
    JsonPath,
}

/// <summary>
/// One correlation field of a topic, resolved: its source and name, its kind, and where it points (a document or a stream) for a link.
/// Fields of one kind are compared across topics; fields of different kinds never are.
/// </summary>
/// <param name="Source">Where it is read.</param>
/// <param name="Name">A header's or attribute's name as declared, <c>key</c> for the key, the path's canonical text for a path.</param>
/// <param name="Kind">What a value means: what <c>As</c> names, <c>document:{type}</c>, <c>stream</c>, the default's kind, or the field's own name.</param>
/// <param name="Path">The parsed path of a <see cref="CorrelationSource.JsonPath" /> field.</param>
/// <param name="DocumentType">The document type a <c>Document(...)</c> field names.</param>
/// <param name="Store">The store a <c>Document(...)</c> or <c>Stream(...)</c> field names; <see langword="null" /> for the host's default.</param>
/// <param name="IsStream">Whether the field names a stream.</param>
/// <param name="IsDefault">Whether this is one of the defaults of D42 (not replaced by the host).</param>
internal sealed record CorrelationField(
    CorrelationSource Source,
    string Name,
    string Kind,
    JsonPathExpression? Path = null,
    string? DocumentType = null,
    string? Store = null,
    bool IsStream = false,
    bool IsDefault = false)
{
    /// <summary>The kind of the trace fields: a value is a trace id, searched as a prefix <c>00-{traceId}</c>.</summary>
    public const string TraceKind = "trace";

    /// <summary>Whether the field is a trace field.</summary>
    public bool IsTrace => Source == CorrelationSource.Header && Kind == TraceKind;

    /// <summary>The field as the search grammar names it: <c>header.x</c>, <c>ce.x</c>, <c>key</c> or a path.</summary>
    public string Grammar => Source switch
    {
        CorrelationSource.Header => "header." + Name,
        CorrelationSource.CloudEvent => "ce." + Name,
        CorrelationSource.Key => "key",
        _ => Name,
    };
}
