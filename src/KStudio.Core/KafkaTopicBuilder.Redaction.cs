using KStudio.Core.Redaction;

namespace KStudio;

public sealed partial class KafkaTopicBuilder
{
    /// <summary>
    /// Declares what is masked in every match of this topic, before any sink sees it (AGENTS.md D40). Called more than once, the rules add
    /// up. The built-in header deny-list (<c>authorization</c>, <c>proxy-authorization</c>, <c>cookie</c>, <c>set-cookie</c>,
    /// <c>x-api-key</c>) applies whether or not this is called.
    /// </summary>
    /// <param name="configure">Declares the rules.</param>
    /// <returns>This builder.</returns>
    public KafkaTopicBuilder Redact(Action<KafkaRedactionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        // The rules object is created on the first call and reused, so a second call adds to the first; configure runs at once, so the
        // registry's snapshot sees the rules however the options are used afterwards.
        Rules ??= new KafkaRedactionRules();
        configure(new KafkaRedactionBuilder(Rules));
        return this;
    }
}
