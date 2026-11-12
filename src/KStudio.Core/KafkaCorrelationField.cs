using KStudio.Core.Correlation;

namespace KStudio;

/// <summary>
/// One correlation field of a topic, as the host declares it (AGENTS.md D42). Without a call, the field's kind is the default's it replaces,
/// or its own name; <see cref="As" />, <see cref="Document" /> and <see cref="Stream" /> name what its values mean, so that fields on
/// different topics can be compared.
/// </summary>
public sealed class KafkaCorrelationField
{
    internal KafkaCorrelationField(CorrelationSource source, string name)
    {
        Source = source;
        Name = name;
    }

    /// <summary>Where the field is read.</summary>
    internal CorrelationSource Source { get; }

    /// <summary>A header's or attribute's name, <c>key</c>, or a JSON path, as declared.</summary>
    internal string Name { get; }

    /// <summary>The kind <see cref="As" /> named.</summary>
    internal string? KindName { get; private set; }

    /// <summary>The document type <see cref="Document" /> named.</summary>
    internal string? DocumentType { get; private set; }

    /// <summary>The store <see cref="Document" /> or <see cref="Stream" /> named.</summary>
    internal string? StoreName { get; private set; }

    /// <summary>Whether <see cref="Stream" /> was called.</summary>
    internal bool IsStream { get; private set; }

    /// <summary>A copy, so that the registry's snapshot does not follow a builder the host kept.</summary>
    internal KafkaCorrelationField Copy() =>
        new(Source, Name)
        {
            KindName = KindName,
            DocumentType = DocumentType,
            StoreName = StoreName,
            IsStream = IsStream,
        };

    /// <summary>Names what the field's values mean: <c>[a-z][a-z0-9-]{0,39}</c>. Fields of one kind are compared across topics.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>This field.</returns>
    public KafkaCorrelationField As(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        KindName = kind;
        return this;
    }

    /// <summary>The field holds the id of a document of this type: its kind is <c>document:{documentType}</c>, and a Marten Studio in the same host can link to it.</summary>
    /// <param name="documentType">The document type's name, as the host's store aliases it or as its class is called.</param>
    /// <param name="store">The store, when the host has more than one; <see langword="null" /> for its default.</param>
    /// <returns>This field.</returns>
    public KafkaCorrelationField Document(string documentType, string? store = null)
    {
        ArgumentNullException.ThrowIfNull(documentType);
        DocumentType = documentType;
        StoreName = store;
        return this;
    }

    /// <summary>The field holds the id or key of an event stream: its kind is <c>stream</c>, and a Marten Studio in the same host can link to it.</summary>
    /// <param name="store">The store, when the host has more than one; <see langword="null" /> for its default.</param>
    /// <returns>This field.</returns>
    public KafkaCorrelationField Stream(string? store = null)
    {
        IsStream = true;
        StoreName = store;
        return this;
    }
}
