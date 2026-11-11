using KStudio.Core.Redaction;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio;

public sealed partial class KafkaTopicBuilder
{
    /// <summary>How the topic's values are decoded, when the host declared a decoder; ENG-B's part of the descriptor.</summary>
    internal Func<IServiceProvider, IKafkaRecordDecoder>? DecoderSource { get; private set; }

    /// <summary>The declared decoder when the host handed over an instance (the built-ins are recognised by reference).</summary>
    internal IKafkaRecordDecoder? DecoderInstance { get; private set; }

    /// <summary>The topic's redaction rules, when any were declared; the deny-list applies whether or not there are any (decision 10.3).</summary>
    internal KafkaRedactionRules? Rules { get; set; }

    /// <summary>
    /// Declares how the topic's values are decoded. The decoder runs on the scan's thread and only for a visitor who may read payloads;
    /// it returns JSON bytes or text that the engine parses itself, under its own caps. The built-ins in <see cref="KafkaDecoders" /> may be named
    /// to force one.
    /// </summary>
    /// <param name="decoder">The decoder.</param>
    /// <returns>This builder.</returns>
    public KafkaTopicBuilder Value(IKafkaRecordDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        DecoderInstance = decoder;
        DecoderSource = _ => decoder;
        return this;
    }

    /// <summary>
    /// Declares a decoder the container activates once, on first use, as a singleton per topic descriptor.
    /// </summary>
    /// <typeparam name="TDecoder">The decoder's type.</typeparam>
    /// <returns>This builder.</returns>
    public KafkaTopicBuilder Value<TDecoder>()
        where TDecoder : class, IKafkaRecordDecoder
    {
        DecoderInstance = null;
        DecoderSource = static provider => ActivatorUtilities.CreateInstance<TDecoder>(provider);
        return this;
    }
}
