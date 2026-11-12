namespace KStudio.Core.Correlation;

/// <summary>What a host declared about a topic's correlation, before it is resolved: the fields in declaration order and whether the defaults are off.</summary>
internal sealed class CorrelationDeclarations
{
    private readonly List<KafkaCorrelationField> fields = [];

    /// <summary>The declared fields, in order.</summary>
    public IReadOnlyList<KafkaCorrelationField> Fields => fields;

    /// <summary>Whether <c>WithoutDefaults()</c> was called.</summary>
    public bool WithoutDefaults { get; set; }

    /// <summary>Records a field and hands it back.</summary>
    /// <param name="field">The field.</param>
    public KafkaCorrelationField Add(KafkaCorrelationField field)
    {
        fields.Add(field);
        return field;
    }

    /// <summary>A deep copy: the registry keeps this, so that a declaration made on a builder the host kept after validation is not seen.</summary>
    public CorrelationDeclarations Copy()
    {
        CorrelationDeclarations copy = new() { WithoutDefaults = WithoutDefaults };

        foreach (KafkaCorrelationField field in fields)
        {
            copy.fields.Add(field.Copy());
        }

        return copy;
    }
}
