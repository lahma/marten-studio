using KStudio.Core.Correlation;

namespace KStudio;

/// <summary>
/// Declares the fields a topic's records are correlated by (AGENTS.md D42): headers, CloudEvents attributes, the key and JSON paths. Each
/// method returns the field it declares, so each field is its own statement (the style of <see cref="KafkaRedactionBuilder" />). Nothing
/// is checked here: the registry validates every declaration when it is built, and says what is wrong in one list.
/// </summary>
public sealed class KafkaCorrelationBuilder
{
    private readonly CorrelationDeclarations declarations;

    internal KafkaCorrelationBuilder(CorrelationDeclarations declarations)
    {
        this.declarations = declarations;
    }

    /// <summary>A header, by name (compared ignoring case). A field with the name of a default replaces it.</summary>
    /// <param name="name">The header's name: 1 to 256 letters, digits, <c>.</c>, <c>_</c> and <c>-</c>.</param>
    /// <returns>The field.</returns>
    public KafkaCorrelationField Header(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return declarations.Add(new KafkaCorrelationField(CorrelationSource.Header, name));
    }

    /// <summary>A CloudEvents attribute, by name.</summary>
    /// <param name="attribute">The attribute's name: 1 to 20 lower-case letters and digits.</param>
    /// <returns>The field.</returns>
    public KafkaCorrelationField CloudEvent(string attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        return declarations.Add(new KafkaCorrelationField(CorrelationSource.CloudEvent, attribute));
    }

    /// <summary>The record's key, in the topic's key format.</summary>
    /// <returns>The field.</returns>
    public KafkaCorrelationField Key() => declarations.Add(new KafkaCorrelationField(CorrelationSource.Key, "key"));

    /// <summary>A path into the value (<c>$.order.id</c>). It is read, and searched, only for a visitor who may read payloads.</summary>
    /// <param name="path">The path, in the search grammar's JSON-path syntax.</param>
    /// <returns>The field.</returns>
    public KafkaCorrelationField JsonPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return declarations.Add(new KafkaCorrelationField(CorrelationSource.JsonPath, path));
    }

    /// <summary>This topic has only the fields declared here: Wolverine's and CloudEvents' defaults do not apply.</summary>
    /// <returns>This builder.</returns>
    public KafkaCorrelationBuilder WithoutDefaults()
    {
        declarations.WithoutDefaults = true;
        return this;
    }
}
