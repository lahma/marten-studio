using KStudio.Core.Correlation;

namespace KStudio;

public sealed partial class KafkaTopicBuilder
{
    /// <summary>The correlation fields the host declared, when it declared any.</summary>
    internal CorrelationDeclarations? Correlations { get; private set; }

    /// <summary>
    /// Declares the fields this topic's records are correlated by (AGENTS.md D42): what a record's related records are found by, on this
    /// and on other topics. Without a call, the defaults of D42 apply (Wolverine's headers and CloudEvents' <c>id</c>). Called more than
    /// once, the declarations add up.
    /// </summary>
    /// <param name="configure">Declares the fields.</param>
    /// <returns>This builder.</returns>
    public KafkaTopicBuilder Correlation(Action<KafkaCorrelationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Created on the first call and reused; configure runs at once, so the registry's snapshot sees the declarations however the
        // options are used afterwards.
        Correlations ??= new CorrelationDeclarations();
        configure(new KafkaCorrelationBuilder(Correlations));
        return this;
    }
}
