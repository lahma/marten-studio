namespace KStudio;

/// <summary>
/// What a host declares about one topic (AGENTS.md D36): a description, the format of its keys and, from ENG-B, how its values
/// are decoded. A topic that declares nothing is still browsable; every verdict about its keys then says "assumed".
/// </summary>
public sealed partial class KafkaTopicBuilder
{
    internal KafkaTopicBuilder()
    {
    }

    /// <summary>
    /// A copy of what this builder says now, rules included: the registry keeps this, so that a declaration made on a builder the host
    /// kept after validation is not seen.
    /// </summary>
    internal KafkaTopicBuilder Snapshot()
    {
        KafkaTopicBuilder copy = new()
        {
            DescriptionText = DescriptionText,
            KeyFormat = KeyFormat,
            DecoderSource = DecoderSource,
            DecoderInstance = DecoderInstance,
            Rules = Rules?.Copy(),
        };

        return copy;
    }

    /// <summary>The description, when one was declared.</summary>
    internal string? DescriptionText { get; private set; }

    /// <summary>The key format, when one was declared; <see langword="null" /> means <see cref="KafkaKeyFormat.Utf8String" /> assumed.</summary>
    internal KafkaKeyFormat? KeyFormat { get; private set; }

    /// <summary>A sentence about the topic, shown beside it (1 to 500 characters).</summary>
    /// <param name="description">The description.</param>
    /// <returns>This builder.</returns>
    public KafkaTopicBuilder Description(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        DescriptionText = description;
        return this;
    }

    /// <summary>How the topic's keys are written. The default, when never called, is <see cref="KafkaKeyFormat.Utf8String" />, assumed.</summary>
    /// <param name="format">The format.</param>
    /// <returns>This builder.</returns>
    public KafkaTopicBuilder Key(KafkaKeyFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        KeyFormat = format;
        return this;
    }
}
