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

    /// <summary>The headers, at most 64 and within the match's budget.</summary>
    public required IReadOnlyList<KafkaMatchHeader> Headers { get; init; }

    /// <summary>How many headers beyond the first 64, or beyond the match's budget, were left out.</summary>
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

    /// <summary>How many attributes beyond the first 64, or beyond the match's budget, were left out.</summary>
    public int CloudEventAttributesOmitted { get; init; }

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

    /// <summary>Whether <see cref="ValueText" /> is JSON (written compactly when a rule masked part of it) or text.</summary>
    public KafkaValueTextKind? ValueTextKind { get; init; }

    /// <summary>The nodes a JSON-path rule masked in the value, at most 64; the text holds the mask in their place.</summary>
    public IReadOnlyList<KafkaMaskedPath> MaskedPaths { get; init; } = [];

    /// <summary>How many masked nodes are not in <see cref="MaskedPaths" />.</summary>
    public int MaskedPathsOmitted { get; init; }

    /// <summary>The distinct names of the rules that masked something in the value: a path, <c>pattern phone</c>.</summary>
    public IReadOnlyList<string> ValueMaskedBy { get; init; } = [];

    /// <summary>Whether the value shown is a CloudEvent's data and not the record's value.</summary>
    public bool ValueIsCloudEventData { get; init; }
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

    /// <summary>The most CloudEvents attributes kept; the count of the rest is kept.</summary>
    public const int MaxAttributes = 64;

    /// <summary>The most characters of a header's or an attribute's name kept.</summary>
    public const int MaxNameChars = 256;

    /// <summary>Makes the match of a record that satisfied the predicate.</summary>
    /// <param name="record">The record; it is not kept.</param>
    /// <param name="topic">The topic's context: its redactor, key format and decoder.</param>
    /// <param name="grant">What the visitor may read.</param>
    /// <param name="limits">The scan's caps.</param>
    /// <param name="counters">Counts the decodes.</param>
    /// <param name="onDecoderFailed">Told when a host decoder throws.</param>
    /// <param name="evaluated">The record as the predicate judged it: its value, and the payload rules applied to it, are reused when it read them, so that a host decoder runs once per matching record.</param>
    /// <param name="focusValueBytes">For the one record a record read was asked for: the value part may hold this many bytes whatever is left of the match's budget.</param>
    /// <param name="onRedactionFailed">Told when a payload rule could not be applied.</param>
    public static KafkaMatch Create(
        WireRecord record,
        TopicScanContext topic,
        KafkaReadGrant grant,
        KafkaScanLimits limits,
        DecodeCounters counters,
        Action<Exception>? onDecoderFailed = null,
        RecordEvaluationContext? evaluated = null,
        int focusValueBytes = 0,
        Action<RedactedValue>? onRedactionFailed = null)
    {
        ArgumentNullException.ThrowIfNull(topic);
        IKafkaRedactor redactor = topic.Redactor;
        KafkaKeyFormat keyFormat = topic.Descriptor.KeyFormat;

        // One budget for everything the match keeps, in UTF-8 bytes (decision 9.8): the key, the headers, the attributes and then the value,
        // each cut with its count of what was left out. What a match holds is therefore at most c, whatever the record carries.
        int budget = limits.MaxMatchBytes;

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
            key = Capture(keyFormat.TryFormat(record.Key, out string? formatted) && formatted is not null ? Encoding.UTF8.GetBytes(formatted) : null, record.Key, Math.Min(MaxKeyBytes, budget));
            budget -= Spent(key);
        }

        // The headers.
        List<KafkaMatchHeader> headers = [];
        int omitted = 0;

        foreach (WireHeader header in record.Headers)
        {
            string name = CutName(header.Name, MaxNameChars);
            int nameCost = Encoding.UTF8.GetByteCount(name);

            if (headers.Count >= MaxHeaders || nameCost > budget)
            {
                omitted++;
                continue;
            }

            KafkaMatchField value = header.Value is null ? KafkaMatchField.Null
                : redactor.ForHeader(header.Name) is { } rule ? KafkaMatchField.Masked(rule.Name)
                : Capture(null, header.Value, Math.Min(MaxHeaderBytes, budget - nameCost));

            headers.Add(new KafkaMatchHeader(name, value));
            budget -= nameCost + Spent(value);
        }

        // The value: its kind and length always, its CloudEvents attributes, and its text only under the payload grant. The value is whole
        // here; the payload rules see all of it, and only then is it cut to what the match may keep.
        RecordValue decoded = evaluated?.ComputedValue
            ?? ValueDetector.DetectValue(
                record,
                new ValueDetection(grant, topic.Decoder, limits.MaxDecodeBytes, Math.Max(0, budget), counters, onDecoderFailed, redactor));

        RedactedValue? redacted = null;

        if (grant.HasFlag(KafkaReadGrant.Payload) && redactor.HasPayloadRules
            && (evaluated is null || !evaluated.TryGetComputedRedaction(decoded, out redacted)))
        {
            redacted = PayloadRedactor.Apply(decoded, redactor, PayloadRedactor.Budget);

            if (redacted is not null)
            {
                counters.ValueRedacted++;

                if (redacted.WithheldBy is not null)
                {
                    onRedactionFailed?.Invoke(redacted);
                }
            }
        }

        List<KafkaMatchHeader> attributes = [];
        int attributesOmitted = 0;

        if (decoded.CloudEvent is { } view)
        {
            foreach (KafkaCloudEvent cloudEvent in view.Events.Take(1))
            {
                foreach (KeyValuePair<string, string> attribute in cloudEvent.Attributes.OrderBy(static x => x.Key, StringComparer.Ordinal))
                {
                    string name = CutName(attribute.Key, MaxNameChars);
                    int nameCost = Encoding.UTF8.GetByteCount(name);

                    if (attributes.Count >= MaxAttributes || nameCost > budget)
                    {
                        attributesOmitted++;
                        continue;
                    }

                    KafkaMatchField value = redactor.ForCloudEvent(attribute.Key) is { } rule
                        ? KafkaMatchField.Masked(rule.Name)
                        : Capture(Encoding.UTF8.GetBytes(attribute.Value), null, Math.Min(MaxAttributeBytes, budget - nameCost));
                    attributes.Add(new KafkaMatchHeader(name, value));
                    budget -= nameCost + Spent(value);
                }
            }
        }

        // The value gets what is left, or the focus budget when the record is the one a record read was asked for.
        string? text = null;
        string? preview = decoded.Preview;
        bool truncated = decoded.Cut;
        string? withheld = null;
        KafkaValueTextKind? textKind = null;
        int valueBudget = Math.Max(Math.Max(0, budget), focusValueBytes);

        if (redacted is { WithheldBy: { } by })
        {
            // A rule that could not be applied withholds the value whole: no text, no preview, no length of any part.
            withheld = by;
            preview = null;
            truncated = false;
        }
        else if (redacted is not null)
        {
            textKind = redacted.TextKind;
            text = redacted.Text;

            if (text is not null && Encoding.UTF8.GetByteCount(text) > valueBudget)
            {
                text = Cut(text, valueBudget);
                truncated = true;
            }
        }
        else if (decoded.Utf8 is { } bytes)
        {
            textKind = decoded.Kind == KafkaValueKind.Json ? KafkaValueTextKind.Json : KafkaValueTextKind.Text;
            (text, bool cut) = ValueDetector.KeepText(bytes, valueBudget);
            truncated |= cut;
        }

        if (preview is not null && Encoding.UTF8.GetByteCount(preview) > valueBudget)
        {
            preview = Cut(preview, valueBudget);
            truncated = true;
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
            ValueTextKind = textKind,
            MaskedPaths = redacted?.MaskedPaths ?? [],
            MaskedPathsOmitted = redacted?.MaskedPathsOmitted ?? 0,
            ValueMaskedBy = redacted?.MaskedBy ?? [],
            ValueIsCloudEventData = decoded.IsCloudEventData,
            CloudEventAttributes = attributes,
            CloudEventAttributesOmitted = attributesOmitted,
            CloudEventMode = decoded.CloudEvent?.Mode ?? CloudEventMode.None,
            Trace = HeaderDecoder.FindTrace(record.Headers.Where(x => redactor.ForHeader(x.Name) is null).Select(static x => KeyValuePair.Create(x.Name, x.Value))),
            ValueText = text,
            ValuePreview = preview,
            ValueTruncated = truncated,
            ValueWithheldBy = withheld,
        };
    }

    /// <summary>A name cut at <paramref name="maxChars" /> characters, not in the middle of a surrogate pair.</summary>
    private static string CutName(string name, int maxChars)
    {
        if (name.Length <= maxChars)
        {
            return name;
        }

        int length = char.IsHighSurrogate(name[maxChars - 1]) ? maxChars - 1 : maxChars;
        return name[..length];
    }

    /// <summary>The bytes a kept field spends of the match's budget: its text in UTF-8 (a masked or null field keeps none).</summary>
    private static int Spent(KafkaMatchField field) => field.Text is { } text ? Encoding.UTF8.GetByteCount(text) : 0;

    /// <summary>At most <paramref name="maxBytes" /> bytes of UTF-8 of <paramref name="text" />, cut between characters.</summary>
    private static string Cut(string text, int maxBytes)
    {
        if (text.Length <= maxBytes / 3 || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        int used = 0;
        int index = 0;

        while (index < text.Length)
        {
            int length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            int bytes = Encoding.UTF8.GetByteCount(text.AsSpan(index, length));

            if (used + bytes > maxBytes)
            {
                break;
            }

            used += bytes;
            index += length;
        }

        return text[..index];
    }

    /// <summary>A capped copy: <paramref name="textBytes" /> when the field is text, else hex of <paramref name="raw" />; at most <paramref name="max" /> bytes of text.</summary>
    private static KafkaMatchField Capture(byte[]? textBytes, byte[]? raw, int max)
    {
        max = Math.Max(0, max);

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

        string hex = HeaderDecoder.Hex(bytes);
        bool hexCut = bytes.Length > HeaderDecoder.MaxHexBytes;

        if (hex.Length > max)
        {
            hex = hex[..max];
            hexCut = true;
        }

        return new KafkaMatchField(hex, null, hexCut, bytes.Length, false);
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
