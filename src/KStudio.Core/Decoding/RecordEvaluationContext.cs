using KStudio.Core.Wire;

namespace KStudio.Core.Decoding;

/// <summary>
/// One record being judged by a predicate (decision 7.5). It decodes each artefact (the key's text, a header's text, the CloudEvents
/// attributes) at most once and only when a clause asks, and counts every real decode in <see cref="DecodeCounters" />, which is how tests
/// prove that clauses run cheapest first. It is internal to KStudio.Core and never returned from an API KStudio calls (decision 10.4).
/// </summary>
internal sealed class RecordEvaluationContext
{
    private readonly KafkaKeyFormatReader keyFormat;
    private readonly DecodeCounters counters;
    private readonly int maxDecodeBytes;
    private readonly bool declaresCloudEvents;
    private readonly string?[] headerText;
    private readonly bool[] headerTextDone;
    private bool keyTextDone;
    private string? keyText;
    private CloudEventView? cloudEvent;

    /// <summary>Creates the context of one record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="keyFormat">How the topic's keys are written.</param>
    /// <param name="counters">Counts the decodes.</param>
    /// <param name="maxDecodeBytes">The largest value parsed.</param>
    /// <param name="declaresCloudEvents">Whether the topic declared <c>KafkaDecoders.CloudEvents</c>.</param>
    public RecordEvaluationContext(WireRecord record, KafkaKeyFormat keyFormat, DecodeCounters counters, int maxDecodeBytes, bool declaresCloudEvents)
    {
        Record = record;
        this.keyFormat = new KafkaKeyFormatReader(keyFormat);
        this.counters = counters;
        this.maxDecodeBytes = maxDecodeBytes;
        this.declaresCloudEvents = declaresCloudEvents;
        headerText = new string?[record.Headers.Length];
        headerTextDone = new bool[record.Headers.Length];
    }

    /// <summary>The record.</summary>
    public WireRecord Record { get; }

    /// <summary>The first reason a clause could not decide on this record, when one could not.</summary>
    public KafkaUnknownReason? UnknownReason { get; private set; }

    /// <summary>Says a clause could not decide, and why; the first reason is kept.</summary>
    /// <param name="reason">Why.</param>
    public void MarkUnknown(KafkaUnknownReason reason) => UnknownReason ??= reason;

    /// <summary>The key as text in its format; <see langword="false" /> for a null key or one the format cannot write as text.</summary>
    /// <param name="text">The text.</param>
    public bool TryKeyText(out string text)
    {
        if (!keyTextDone)
        {
            keyTextDone = true;

            if (Record.Key is { } key)
            {
                counters.KeyText++;
                keyText = keyFormat.TryFormat(key);
            }
        }

        text = keyText ?? string.Empty;
        return keyText is not null;
    }

    /// <summary>A header's value as text (strict UTF-8, no control character but tab); <see langword="false" /> for a null or binary value.</summary>
    /// <param name="index">The header's position.</param>
    /// <param name="text">The text.</param>
    public bool TryHeaderText(int index, out string text)
    {
        if (!headerTextDone[index])
        {
            headerTextDone[index] = true;

            if (Record.Headers[index].Value is { } value)
            {
                counters.HeaderText++;
                headerText[index] = HeaderDecoder.TryText(value, out string decoded) ? decoded : null;
            }
        }

        text = headerText[index] ?? string.Empty;
        return headerText[index] is not null;
    }

    /// <summary>The record's CloudEvents attributes, read the first time a clause asks.</summary>
    public CloudEventView CloudEvent
    {
        get
        {
            if (cloudEvent is null)
            {
                CloudEventMode mode = CloudEventReader.Classify(Record, declaresCloudEvents);
                cloudEvent = CloudEventReader.Read(Record, mode, maxDecodeBytes, counters);
            }

            return cloudEvent;
        }
    }

    private sealed class KafkaKeyFormatReader(KafkaKeyFormat format)
    {
        public string? TryFormat(byte[] key) => format.TryFormat(key, out string? text) ? text : null;
    }
}

/// <summary>Why a clause could not decide about a record (decision 7.5): such a record is counted, never called a non-match.</summary>
internal enum KafkaUnknownReason
{
    /// <summary>The key cannot be written as text in its format.</summary>
    KeyNotText,

    /// <summary>A header's value is not text.</summary>
    HeaderNotText,

    /// <summary>A structured CloudEvent is not valid JSON.</summary>
    InvalidJson,

    /// <summary>A structured CloudEvent is larger than <c>MaxDecodeBytes</c>.</summary>
    TooLarge,

    /// <summary>The topic declared CloudEvents but the value is not one.</summary>
    NotACloudEvent,
}

/// <summary>The words for the reasons, as "N records could not be …".</summary>
internal static class KafkaUnknownReasonText
{
    /// <summary>Completes "{n} records …".</summary>
    /// <param name="reason">The reason.</param>
    public static string Describe(KafkaUnknownReason reason) =>
        reason switch
        {
            KafkaUnknownReason.KeyNotText => "had a key that could not be read as text",
            KafkaUnknownReason.HeaderNotText => "had a header value that could not be read as text",
            KafkaUnknownReason.InvalidJson => "could not be decoded as JSON",
            KafkaUnknownReason.TooLarge => "were larger than the decode limit",
            _ => "were not CloudEvents although the topic declares them",
        };
}
