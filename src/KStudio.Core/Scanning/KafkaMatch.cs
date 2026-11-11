using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Wire;

namespace KStudio.Core.Scanning;

/// <summary>
/// A key, header value or attribute as a match keeps it: text or hex, capped, or <c>null</c>, or masked. A masked field keeps no
/// unredacted copy and not even its length (decision 10.2).
/// </summary>
/// <param name="Text">The text (or <c>0x…</c> hex); <see langword="null" /> for a null or masked field.</param>
/// <param name="RedactedBy">The rule that masked it.</param>
/// <param name="Truncated">Whether <paramref name="Text" /> was cut at the cap.</param>
/// <param name="ByteLength">The field's length in bytes before the cap; 0 when masked.</param>
/// <param name="IsNull">Whether the value is null.</param>
internal readonly record struct KafkaMatchField(string? Text, string? RedactedBy, bool Truncated, int ByteLength, bool IsNull)
{
    /// <summary>A null value.</summary>
    public static KafkaMatchField Null { get; } = new(null, null, false, 0, true);

    /// <summary>Whether a rule masked the value.</summary>
    public bool IsRedacted => RedactedBy is not null;

    /// <summary>A masked value.</summary>
    /// <param name="rule">The rule's name.</param>
    public static KafkaMatchField Masked(string rule) => new(null, rule, false, 0, false);

    /// <summary>How the field reads on a screen.</summary>
    public string Display => RedactedBy is not null ? "••• (redacted by " + RedactedBy + ")" : IsNull ? "null" : Text ?? string.Empty;
}

/// <summary>A header of a match.</summary>
/// <param name="Name">The header's name.</param>
/// <param name="Value">Its value.</param>
internal sealed record KafkaMatchHeader(string Name, KafkaMatchField Value);

/// <summary>
/// A record that matched, as every sink sees it (decision 9.8). It has no unredacted member, holds capped copies and never the wire
/// record's arrays, and only <see cref="KafkaMatchFactory" /> makes one (a convention test holds that).
/// </summary>
internal sealed class KafkaMatch
{
    internal KafkaMatch()
    {
    }

    /// <summary>The topic.</summary>
    public required string Topic { get; init; }

    /// <summary>The partition.</summary>
    public required int Partition { get; init; }

    /// <summary>The offset.</summary>
    public required long Offset { get; init; }

    /// <summary>The record's timestamp.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Whether the timestamp is the producer's or the broker's.</summary>
    public required WireTimestampType TimestampType { get; init; }

    /// <summary>The leader epoch, when known.</summary>
    public int? LeaderEpoch { get; init; }

    /// <summary>The key.</summary>
    public required KafkaMatchField Key { get; init; }

    /// <summary>How the topic's keys are written; "(assumed)" when the host declared nothing.</summary>
    public required string KeyFormat { get; init; }

    /// <summary>The headers, at most 64.</summary>
    public required IReadOnlyList<KafkaMatchHeader> Headers { get; init; }

    /// <summary>How many headers beyond the first 64 were left out.</summary>
    public int HeadersOmitted { get; init; }

    /// <summary>What kind of thing the value is.</summary>
    public required KafkaValueKind ValueKind { get; init; }

    /// <summary>The value's format: <c>JSON</c>, <c>CloudEvents (structured)</c>, a decoder's name.</summary>
    public required string ValueFormat { get; init; }

    /// <summary>The value's length in bytes.</summary>
    public required int ValueLength { get; init; }

    /// <summary>The schema id of a wire-format value.</summary>
    public int? SchemaId { get; init; }

    /// <summary>Why the value could not be decoded, when it could not.</summary>
    public KafkaUndecodableReason? UndecodableReason { get; init; }

    /// <summary>The CloudEvents attributes, when the record is one.</summary>
    public IReadOnlyList<KafkaMatchHeader> CloudEventAttributes { get; init; } = [];

    /// <summary>How the record carries its CloudEvent.</summary>
    public CloudEventMode CloudEventMode { get; init; }

    /// <summary>The W3C trace context in the headers, when there is one.</summary>
    public KafkaTraceContext? Trace { get; init; }

    /// <summary>The value's text, up to the match cap: only for a visitor who may read payloads, and never when a rule could not be applied to it.</summary>
    public string? ValueText { get; init; }

    /// <summary>Hex of a binary value's first bytes: only for a visitor who may read payloads.</summary>
    public string? ValuePreview { get; init; }

    /// <summary>Whether <see cref="ValueText" /> or <see cref="ValuePreview" /> was cut at the cap; the record detail reads it whole.</summary>
    public bool ValueTruncated { get; init; }

    /// <summary>The rule that made the engine withhold the value (decision 10.2: fail closed).</summary>
    public string? ValueWithheldBy { get; init; }
}

/// <summary>Everything the engine knows about one topic of a scan.</summary>
internal sealed class TopicScanContext
{
    /// <summary>Creates a topic's context.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="descriptor">What the host declared.</param>
    /// <param name="redactor">Its redaction rules.</param>
    /// <param name="predicate">The predicate compiled for it.</param>
    /// <param name="decoder">Its declared decoder, resolved.</param>
    public TopicScanContext(string topic, KafkaTopicDescriptor descriptor, IKafkaRedactor redactor, CompiledPredicate predicate, IKafkaRecordDecoder? decoder)
    {
        Topic = topic;
        Descriptor = descriptor;
        Redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        Predicate = predicate;
        Decoder = decoder;
    }

    /// <summary>The topic.</summary>
    public string Topic { get; }

    /// <summary>What the host declared.</summary>
    public KafkaTopicDescriptor Descriptor { get; }

    /// <summary>The redaction rules. There is always one: no match exists without it.</summary>
    public IKafkaRedactor Redactor { get; }

    /// <summary>The predicate.</summary>
    public CompiledPredicate Predicate { get; }

    /// <summary>The declared decoder.</summary>
    public IKafkaRecordDecoder? Decoder { get; }

    /// <summary>Whether the topic declared structured CloudEvents.</summary>
    public bool DeclaresCloudEvents => KafkaDecoders.BuiltInKind(Decoder) == KafkaBuiltInDecoder.CloudEvents;
}

/// <summary>
/// The only way to a <see cref="KafkaMatch" /> (decision 10.2): redaction runs here, before the match exists, so no sink can see a record
/// that was not redacted. Fields are capped (key 1 KiB, a header value 4 KiB, 64 headers, an attribute 1 KiB, a value at the match cap)
/// and copied, so a kept match never pins a large value.
/// </summary>
internal static class KafkaMatchFactory
{
    /// <summary>The most bytes of a key kept.</summary>
    public const int MaxKeyBytes = 1_024;

    /// <summary>The most bytes of a header value kept.</summary>
    public const int MaxHeaderBytes = 4_096;

    /// <summary>The most headers kept.</summary>
    public const int MaxHeaders = 64;

    /// <summary>The most bytes of a CloudEvents attribute kept.</summary>
    public const int MaxAttributeBytes = 1_024;

    /// <summary>Makes the match of a record that satisfied the predicate.</summary>
    /// <param name="record">The record; it is not kept.</param>
    /// <param name="topic">The topic's context: its redactor, key format and decoder.</param>
    /// <param name="grant">What the visitor may read.</param>
    /// <param name="limits">The scan's caps.</param>
    /// <param name="counters">Counts the decodes.</param>
    /// <param name="onDecoderFailed">Told when a host decoder throws.</param>
    public static KafkaMatch Create(
        WireRecord record,
        TopicScanContext topic,
        KafkaReadGrant grant,
        KafkaScanLimits limits,
        DecodeCounters counters,
        Action<Exception>? onDecoderFailed = null)
    {
        ArgumentNullException.ThrowIfNull(topic);
        IKafkaRedactor redactor = topic.Redactor;
        KafkaKeyFormat keyFormat = topic.Descriptor.KeyFormat;

        // The key.
        KafkaMatchField key;

        if (record.Key is null)
        {
            key = KafkaMatchField.Null;
        }
        else if (redactor.ForKey() is { } keyRule)
        {
            key = KafkaMatchField.Masked(keyRule.Name);
        }
        else
        {
            key = Capture(keyFormat.TryFormat(record.Key, out string? formatted) && formatted is not null ? Encoding.UTF8.GetBytes(formatted) : null, record.Key, MaxKeyBytes);
        }

        // The headers.
        List<KafkaMatchHeader> headers = [];
        int omitted = 0;

        foreach (WireHeader header in record.Headers)
        {
            if (headers.Count >= MaxHeaders)
            {
                omitted++;
                continue;
            }

            KafkaMatchField value = header.Value is null ? KafkaMatchField.Null
                : redactor.ForHeader(header.Name) is { } rule ? KafkaMatchField.Masked(rule.Name)
                : Capture(null, header.Value, MaxHeaderBytes);

            headers.Add(new KafkaMatchHeader(header.Name.Length > 256 ? header.Name[..256] : header.Name, value));
        }

        // The value: its kind and length always, its CloudEvents attributes, and its text only under the payload grant.
        KafkaDecodedValue decoded = ValueDetector.Detect(
            record,
            new ValueDetection(grant, topic.Decoder, limits.MaxDecodeBytes, limits.MaxMatchBytes, counters, onDecoderFailed));

        List<KafkaMatchHeader> attributes = [];

        if (decoded.CloudEvent is { } view)
        {
            foreach (KafkaCloudEvent cloudEvent in view.Events.Take(1))
            {
                foreach (KeyValuePair<string, string> attribute in cloudEvent.Attributes.OrderBy(static x => x.Key, StringComparer.Ordinal))
                {
                    KafkaMatchField value = redactor.ForCloudEvent(attribute.Key) is { } rule
                        ? KafkaMatchField.Masked(rule.Name)
                        : Capture(Encoding.UTF8.GetBytes(attribute.Value), null, MaxAttributeBytes);
                    attributes.Add(new KafkaMatchHeader(attribute.Key, value));
                }
            }
        }

        string? text = decoded.Text;
        string? preview = decoded.Preview;
        bool truncated = decoded.Truncated;
        string? withheld = null;

        // A structured envelope holds the attributes a rule masks: the value cannot be kept whole, so it is not kept (fail closed).
        if ((text is not null || preview is not null)
            && decoded.CloudEvent is { Mode: CloudEventMode.Structured or CloudEventMode.StructuredBatch }
            && redactor.AnyCloudEventRule() is { } envelopeRule)
        {
            text = null;
            preview = null;
            truncated = false;
            withheld = envelopeRule.Name;
        }

        return new KafkaMatch
        {
            Topic = record.Topic,
            Partition = record.Partition,
            Offset = record.Offset,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, record.TimestampMs)),
            TimestampType = record.TimestampType,
            LeaderEpoch = record.LeaderEpoch,
            Key = key,
            KeyFormat = keyFormat.Name + (topic.Descriptor.KeyFormatDeclared ? string.Empty : " (assumed)"),
            Headers = headers,
            HeadersOmitted = omitted,
            ValueKind = decoded.Kind,
            ValueFormat = decoded.FormatName,
            ValueLength = decoded.Length,
            SchemaId = decoded.SchemaId,
            UndecodableReason = decoded.Reason,
            CloudEventAttributes = attributes,
            CloudEventMode = decoded.CloudEvent?.Mode ?? CloudEventMode.None,
            Trace = HeaderDecoder.FindTrace(record.Headers.Select(static x => KeyValuePair.Create(x.Name, x.Value))),
            ValueText = text,
            ValuePreview = preview,
            ValueTruncated = truncated,
            ValueWithheldBy = withheld,
        };
    }

    /// <summary>A capped copy: <paramref name="textBytes" /> when the field is text, else hex of <paramref name="raw" />.</summary>
    private static KafkaMatchField Capture(byte[]? textBytes, byte[]? raw, int max)
    {
        if (textBytes is not null)
        {
            (string text, bool cut) = ValueDetector.KeepText(textBytes, max);
            return new KafkaMatchField(text, null, cut, textBytes.Length, false);
        }

        byte[] bytes = raw ?? [];

        if (HeaderDecoder.TryText(bytes, out string headerText))
        {
            (string text, bool cut) = ValueDetector.KeepText(Encoding.UTF8.GetBytes(headerText), max);
            return new KafkaMatchField(text, null, cut, bytes.Length, false);
        }

        return new KafkaMatchField(HeaderDecoder.Hex(bytes), null, bytes.Length > HeaderDecoder.MaxHexBytes, bytes.Length, false);
    }
}

/// <summary>The matches a scan kept: append-only, at most <c>MaxMatches</c>, read by index range (decision 9.8).</summary>
internal sealed class KafkaMatchStore(int capacity)
{
    private readonly Lock gate = new();
    private readonly List<KafkaMatch> matches = [];

    /// <summary>The most matches kept.</summary>
    public int Capacity { get; } = capacity;

    /// <summary>How many are kept.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return matches.Count;
            }
        }
    }

    /// <summary>Whether the store is full.</summary>
    public bool IsFull => Count >= Capacity;

    /// <summary>Appends a match; false when the store is full.</summary>
    /// <param name="match">The match.</param>
    public bool TryAdd(KafkaMatch match)
    {
        lock (gate)
        {
            if (matches.Count >= Capacity)
            {
                return false;
            }

            matches.Add(match);
            return true;
        }
    }

    /// <summary>The matches from an index on.</summary>
    /// <param name="from">The first index.</param>
    public IReadOnlyList<KafkaMatch> From(int from)
    {
        lock (gate)
        {
            return from >= matches.Count ? [] : [.. matches.Skip(from)];
        }
    }
}
