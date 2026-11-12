using System.Diagnostics;

using KStudio.Core.Redaction;
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
    private readonly KafkaReadGrant grant;
    private readonly IKafkaRecordDecoder? decoder;
    private readonly IKafkaRedactor redactor;
    private readonly Action<Exception>? onDecoderFailed;
    private readonly Action<RedactedValue>? onRedactionFailed;
    private Dictionary<RecordValue, RedactedValue?>? redacted;
    private long redactionTicks;
    private bool keyTextDone;
    private string? keyText;
    private CloudEventView? cloudEvent;
    private RecordValue? value;

    /// <summary>Creates the context of one record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="keyFormat">How the topic's keys are written.</param>
    /// <param name="counters">Counts the decodes.</param>
    /// <param name="maxDecodeBytes">The largest value parsed.</param>
    /// <param name="declaresCloudEvents">Whether the topic declared <c>KafkaDecoders.CloudEvents</c>.</param>
    /// <param name="grant">What the visitor may read; the value is read only under the payload grant.</param>
    /// <param name="decoder">The topic's declared decoder, resolved.</param>
    /// <param name="redactor">The topic's rules; the deny-list alone when <see langword="null" />.</param>
    /// <param name="onDecoderFailed">Told when a host decoder throws.</param>
    /// <param name="onRedactionFailed">Told when a payload rule could not be applied.</param>
    public RecordEvaluationContext(
        WireRecord record,
        KafkaKeyFormat keyFormat,
        DecodeCounters counters,
        int maxDecodeBytes,
        bool declaresCloudEvents,
        KafkaReadGrant grant = KafkaReadGrant.Envelope,
        IKafkaRecordDecoder? decoder = null,
        IKafkaRedactor? redactor = null,
        Action<Exception>? onDecoderFailed = null,
        Action<RedactedValue>? onRedactionFailed = null)
    {
        Record = record;
        this.keyFormat = new KafkaKeyFormatReader(keyFormat);
        this.counters = counters;
        this.maxDecodeBytes = maxDecodeBytes;
        this.declaresCloudEvents = declaresCloudEvents;
        this.grant = grant;
        this.decoder = decoder;
        this.redactor = redactor ?? KafkaRedactor.DenyListOnly;
        this.onDecoderFailed = onDecoderFailed;
        this.onRedactionFailed = onRedactionFailed;
        headerText = new string?[record.Headers.Length];
        headerTextDone = new bool[record.Headers.Length];
    }

    /// <summary>The record.</summary>
    public WireRecord Record { get; }

    /// <summary>
    /// Why the node last evaluated returned <c>Unknown</c>: set only by a node that returned it, and cleared by every
    /// composite before each child, so that after the root it holds the first reason among the clauses that actually decided the result
    /// (a clause that was unknown inside an <c>and</c> another child made false says nothing).
    /// </summary>
    public KafkaUnknownReason? UnknownReason { get; private set; }

    /// <summary>Says a clause could not decide, and why; the first reason is kept.</summary>
    /// <param name="reason">Why.</param>
    public void MarkUnknown(KafkaUnknownReason reason) => UnknownReason ??= reason;

    /// <summary>Forgets the reason of a node that is not the result (a composite calls it before each child).</summary>
    public void ClearUnknown() => UnknownReason = null;

    /// <summary>Sets the reason of the composite's own result: the first of its unknown children's, or none.</summary>
    /// <param name="reason">The reason.</param>
    public void SetUnknown(KafkaUnknownReason? reason) => UnknownReason = reason;

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

    /// <summary>
    /// The record's value (a CloudEvent's data), read the first time a clause asks: detection under the scan's grant and the topic's decoder,
    /// whole up to the decode limit. A match made from this record reuses it, so a host decoder runs once per matching record.
    /// </summary>
    public RecordValue Value
    {
        get
        {
            if (value is null)
            {
                value = ValueDetector.DetectValue(
                    Record,
                    new ValueDetection(grant, decoder, maxDecodeBytes, maxDecodeBytes, counters, onDecoderFailed, redactor),
                    cloudEvent);

                // The attributes are the same ones a ce. clause would read: a record carries them once.
                cloudEvent ??= value.CloudEvent;
            }

            return value;
        }
    }

    /// <summary>The value, when a clause already read it; <see langword="null" /> when none did.</summary>
    public RecordValue? ComputedValue => value;

    /// <summary>
    /// A value with the topic's payload rules applied, computed once and only when a clause needs the masked form. <see langword="null" />
    /// when the topic has no payload rule or the value has nothing to mask. The time the rules may take is spent across every call on this
    /// record.
    /// </summary>
    /// <param name="part">The value, or one event's data of a batch.</param>
    public RedactedValue? RedactedOf(RecordValue part)
    {
        if (redacted is not null && redacted.TryGetValue(part, out RedactedValue? known))
        {
            return known;
        }

        long started = Stopwatch.GetTimestamp();
        TimeSpan left = PayloadRedactor.Budget - Stopwatch.GetElapsedTime(0, redactionTicks);
        RedactedValue? result = PayloadRedactor.Apply(part, redactor, left > TimeSpan.Zero ? left : TimeSpan.Zero, maxDecodeBytes);
        redactionTicks += Stopwatch.GetTimestamp() - started;
        (redacted ??= new Dictionary<RecordValue, RedactedValue?>(ReferenceEqualityComparer.Instance))[part] = result;

        if (result is not null)
        {
            counters.ValueRedacted++;

            if (result.WithheldBy is not null)
            {
                onRedactionFailed?.Invoke(result);
            }
        }

        return result;
    }

    /// <summary>Whether the payload rules were applied to <paramref name="part" /> already; the result is what <see cref="RedactedOf" /> answered.</summary>
    /// <param name="part">The value.</param>
    /// <param name="result">The masked value, or <see langword="null" />.</param>
    public bool TryGetComputedRedaction(RecordValue part, out RedactedValue? result)
    {
        result = null;
        return redacted is not null && redacted.TryGetValue(part, out result);
    }

    private sealed class KafkaKeyFormatReader(KafkaKeyFormat format)
    {
        public string? TryFormat(byte[] key)
        {
            try
            {
                return format.TryFormat(key, out string? text) ? text : null;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A host's formatter threw: the key cannot be read as text, which is a fact about this record and not the end of the scan.
                return null;
            }
        }
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

    /// <summary>A value clause needed the value as text and it is binary or in the wire format.</summary>
    ValueNotText,

    /// <summary>A number in the value is too large to compare.</summary>
    NumberOutOfRange,

    /// <summary>The topic's decoder threw or refused the value.</summary>
    DecoderFailed,

    /// <summary>A CloudEvent's data_base64 is not valid base64.</summary>
    InvalidBase64,

    /// <summary>A redaction rule could not be applied to the value, so it cannot be searched.</summary>
    RedactionNotApplied,
}

/// <summary>The words for the reasons, as "N records could not be …".</summary>
internal static class KafkaUnknownReasonText
{
    /// <summary>Completes "{n} records …".</summary>
    /// <param name="reason">The reason.</param>
    public static string Describe(KafkaUnknownReason reason) => Describe(reason, one: false);

    /// <summary>Completes "1 record …" when <paramref name="one" />, and "{n} records …" otherwise.</summary>
    /// <param name="reason">The reason.</param>
    /// <param name="one">Whether exactly one record is described.</param>
    public static string Describe(KafkaUnknownReason reason, bool one) =>
        reason switch
        {
            KafkaUnknownReason.KeyNotText => "had a key that could not be read as text",
            KafkaUnknownReason.HeaderNotText => "had a header value that could not be read as text",
            KafkaUnknownReason.InvalidJson => "could not be decoded as JSON",
            KafkaUnknownReason.TooLarge => one ? "was larger than the decode limit" : "were larger than the decode limit",
            KafkaUnknownReason.NotACloudEvent => one ? "was not a CloudEvent although the topic declares them" : "were not CloudEvents although the topic declares them",
            KafkaUnknownReason.ValueNotText => "had a value that could not be read as text",
            KafkaUnknownReason.NumberOutOfRange => "had a number too large to compare",
            KafkaUnknownReason.DecoderFailed => "could not be decoded by the topic's decoder",
            KafkaUnknownReason.InvalidBase64 => "had CloudEvents data_base64 that is not valid base64",
            _ => one
                ? "could not be searched, because a redaction rule could not be applied to its value"
                : "could not be searched, because a redaction rule could not be applied to their value",
        };
}
