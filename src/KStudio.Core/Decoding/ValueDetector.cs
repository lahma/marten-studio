using System.Text;
using System.Text.Json;
using System.Text.Unicode;

using KStudio.Core.Redaction;
using KStudio.Core.Wire;

namespace KStudio.Core.Decoding;

/// <summary>What kind of thing a record's value is (decision 8.2).</summary>
internal enum KafkaValueKind
{
    /// <summary>A null value: a deletion marker, never an empty string.</summary>
    Tombstone,

    /// <summary>A value of zero bytes, never an empty string.</summary>
    Empty,

    /// <summary>JSON.</summary>
    Json,

    /// <summary>UTF-8 text.</summary>
    Text,

    /// <summary>Anything else, shown as hex.</summary>
    Binary,

    /// <summary>A schema-registry framed value: a zero byte, a schema id, then the payload.</summary>
    ConfluentWireFormat,

    /// <summary>A value that should have been readable and was not (see the reason).</summary>
    Undecodable,
}

/// <summary>
/// A value as a match would keep it before any payload rule: the text cut to the match's budget. Nothing is ever deserialized into a CLR
/// type: JSON is text (and, transiently, an element) and a host decoder returns JSON or text that the engine parses itself. Under the
/// envelope grant only the kind, the length, the schema id and the CloudEvents attributes are filled in; the text and the preview are for a
/// visitor who may read payloads. The engine itself works on <see cref="RecordValue" />, which is whole.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="FormatName">How to name it: <c>JSON</c>, <c>CloudEvents (structured), data as JSON</c>, a decoder's name.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="Text">The JSON or text, up to the match cap; only under the payload grant.</param>
/// <param name="Truncated">Whether <paramref name="Text" /> or <paramref name="Preview" /> was cut.</param>
/// <param name="SchemaId">The schema id of a wire-format value.</param>
/// <param name="Preview">Hex of the first bytes of a binary or wire-format value; only under the payload grant.</param>
/// <param name="Reason">Why the value is undecodable.</param>
/// <param name="ReasonText">The reason a host decoder gave, when it refused.</param>
/// <param name="CloudEvent">The record's CloudEvents attributes, when it is one.</param>
internal sealed record KafkaDecodedValue(
    KafkaValueKind Kind,
    string FormatName,
    int Length,
    string? Text,
    bool Truncated,
    int? SchemaId,
    string? Preview,
    KafkaUndecodableReason? Reason,
    string? ReasonText,
    CloudEventView? CloudEvent);

/// <summary>Everything the detector needs to know about the topic and the scan.</summary>
/// <param name="Grant">What the visitor may read.</param>
/// <param name="Decoder">The topic's declared decoder, resolved; <see langword="null" /> when it declared none.</param>
/// <param name="MaxDecodeBytes">The largest value parsed.</param>
/// <param name="MaxMatchBytes">The most text or hex kept by <see cref="ValueDetector.Detect" />.</param>
/// <param name="Counters">Counts the decodes.</param>
/// <param name="OnDecoderFailed">Told when a host decoder throws; the exception's type name is all that is logged.</param>
/// <param name="Redactor">The topic's rules: a host decoder is handed only the headers none of them masks (decision 3.5). The deny-list alone when <see langword="null" />.</param>
internal sealed record ValueDetection(
    KafkaReadGrant Grant,
    IKafkaRecordDecoder? Decoder,
    int MaxDecodeBytes,
    int MaxMatchBytes,
    DecodeCounters Counters,
    Action<Exception>? OnDecoderFailed = null,
    IKafkaRedactor? Redactor = null);

/// <summary>What a visitor may read of a record (decision 10.1). KStudio computes it after its own authorization.</summary>
[Flags]
internal enum KafkaReadGrant
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Keys, headers, and the value's kind, length and CloudEvents attributes (the <c>SearchRecords</c> capability).</summary>
    Envelope = 1,

    /// <summary>The value itself (the <c>ReadPayloads</c> capability).</summary>
    Payload = 2,
}

/// <summary>The value-side detection order of decision 8.1.</summary>
internal static class ValueDetector
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Detects what a record's value is and cuts its text to <see cref="ValueDetection.MaxMatchBytes" />: the value as a match would keep it
    /// before any payload rule.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="detection">The topic and the scan.</param>
    public static KafkaDecodedValue Detect(WireRecord record, ValueDetection detection)
    {
        RecordValue value = DetectValue(record, detection);
        (string? text, bool truncated) = value.Utf8 is { } bytes ? KeepText(bytes, detection.MaxMatchBytes) : (null, false);

        return new KafkaDecodedValue(
            value.Kind, value.FormatName, value.Length, text, truncated || value.Cut, value.SchemaId, value.Preview, value.Reason, value.ReasonText, value.CloudEvent);
    }

    /// <summary>Detects what a record's value is, in the order the design states, and keeps all of it (up to <c>MaxDecodeBytes</c>).</summary>
    /// <param name="record">The record.</param>
    /// <param name="detection">The topic and the scan.</param>
    /// <param name="knownView">The record's CloudEvents attributes when something already read them, so that they are not read twice.</param>
    public static RecordValue DetectValue(WireRecord record, ValueDetection detection, CloudEventView? knownView = null)
    {
        // 1. A null value is a tombstone and a zero-length one is empty: neither is ever an empty string.
        if (record.Value is null)
        {
            return Plain(KafkaValueKind.Tombstone, "Tombstone", 0);
        }

        byte[] value = record.Value;

        if (value.Length == 0)
        {
            return Plain(KafkaValueKind.Empty, "Empty (0 bytes)", 0);
        }

        detection.Counters.ValueDetected++;
        bool payload = detection.Grant.HasFlag(KafkaReadGrant.Payload);
        KafkaBuiltInDecoder? forced = KafkaDecoders.BuiltInKind(detection.Decoder);
        CloudEventMode mode = CloudEventReader.Classify(record, forced == KafkaBuiltInDecoder.CloudEvents);

        // 2. CloudEvents binary: the value is the event's data, typed by its content-type header.
        if (mode == CloudEventMode.Binary)
        {
            CloudEventView view = knownView ?? CloudEventReader.Read(record, mode, detection.MaxDecodeBytes, detection.Counters);
            string? contentType = view.Events[0].Attributes.GetValueOrDefault("datacontenttype");
            RecordValue data = DataOf(value, contentType, payload, detection);
            return data with { FormatName = "CloudEvents (binary), " + data.FormatName, CloudEvent = view, IsCloudEventData = true };
        }

        // 3. CloudEvents structured: the attributes are the envelope's members, and under the payload grant the value is the event's data.
        if (mode is CloudEventMode.Structured or CloudEventMode.StructuredBatch)
        {
            CloudEventView view = knownView ?? CloudEventReader.Read(record, mode, detection.MaxDecodeBytes, detection.Counters);

            if (view.Unreadable is { } unreadable)
            {
                return new RecordValue
                {
                    Kind = KafkaValueKind.Undecodable,
                    FormatName = "CloudEvents (structured)",
                    Length = value.Length,
                    Reason = unreadable,
                    CloudEvent = view,
                };
            }

            if (!payload)
            {
                return new RecordValue { Kind = KafkaValueKind.Json, FormatName = "CloudEvents (structured)", Length = value.Length, CloudEvent = view };
            }

            IReadOnlyList<RecordValue> parts = CloudEventReader.ReadData(record, detection.MaxDecodeBytes, detection.Counters);

            if (parts.Count == 0)
            {
                return NoData(value.Length) with { CloudEvent = view };
            }

            return parts[0] with { CloudEvent = view, IsCloudEventData = true, More = [.. parts.Skip(1)] };
        }

        // 4. The topic's declared decoder, only for a visitor who may read payloads; a built-in one forces its format.
        if (detection.Decoder is { } decoder && payload)
        {
            RecordValue? decoded = Declared(record, value, decoder, forced, detection);

            if (decoded is not null)
            {
                return decoded;
            }
        }

        // 5. The Confluent wire format: never shown as garbage.
        if (ConfluentWireFormat.TryRead(value, out int schemaId))
        {
            return new RecordValue
            {
                Kind = KafkaValueKind.ConfluentWireFormat,
                FormatName = ConfluentWireFormat.Describe(schemaId),
                Length = value.Length,
                SchemaId = schemaId,
                Preview = payload ? HeaderDecoder.Hex(value.AsSpan(5)) : null,
            };
        }

        return Generic(value, payload, detection);
    }

    /// <summary>A CloudEvent without data: it has no value, and every clause on the value is false.</summary>
    /// <param name="length">The envelope's length.</param>
    internal static RecordValue NoData(int length) =>
        new()
        {
            Kind = KafkaValueKind.Empty,
            FormatName = "CloudEvents (structured), no data",
            Length = length,
            NoData = true,
            IsCloudEventData = true,
        };

    private static RecordValue? Declared(WireRecord record, byte[] value, IKafkaRecordDecoder decoder, KafkaBuiltInDecoder? forced, ValueDetection detection)
    {
        switch (forced)
        {
            case KafkaBuiltInDecoder.Json:
                return AsJson(value, "JSON", detection);
            case KafkaBuiltInDecoder.Text:
                return AsText(value, "Text", detection);
            case KafkaBuiltInDecoder.CloudEvents:
                return null;
        }

        KafkaDecodeResult result;
        IKafkaRedactor redactor = detection.Redactor ?? KafkaRedactor.DenyListOnly;

        try
        {
            // A header a rule masks is never handed over: a decoder that echoed one into its text would put a masked value into a match.
            KafkaHeader[] headers =
            [
                .. record.Headers
                    .Where(x => redactor.ForHeader(x.Name) is null)
                    .Select(static x => new KafkaHeader(x.Name, x.Value is null ? null : (ReadOnlyMemory<byte>?) x.Value)),
            ];
            result = decoder.Decode(new KafkaDecodeContext(record.Topic, record.Partition, record.Offset, value, headers));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            detection.OnDecoderFailed?.Invoke(exception);
            return Undecodable(value.Length, decoder.Name, KafkaUndecodableReason.DecoderFailed);
        }

        string name = result.FormatName ?? decoder.Name;

        switch (result.Kind)
        {
            case KafkaDecodeResultKind.Json:
                return AsJson(result.JsonBytes.ToArray(), name, detection) with { Length = value.Length };
            case KafkaDecodeResultKind.Text:
                {
                    byte[] bytes = CutBytes(Encoding.UTF8.GetBytes(result.TextValue!), detection.MaxDecodeBytes, out bool cut);
                    return new RecordValue { Kind = KafkaValueKind.Text, FormatName = name, Length = value.Length, Utf8 = bytes, Cut = cut };
                }

            case KafkaDecodeResultKind.Undecodable:
                string reason = result.TextValue!.Length > 200 ? result.TextValue[..200] : result.TextValue;
                return Undecodable(value.Length, decoder.Name, KafkaUndecodableReason.DecoderRefused) with { ReasonText = reason };
            default:
                return null;
        }
    }

    private static RecordValue Generic(byte[] value, bool payload, ValueDetection detection)
    {
        bool looksLikeJson = false;

        // 6. JSON: the first non-whitespace byte starts a JSON value and the whole value parses.
        if (StartsLikeJson(value))
        {
            if (value.Length > detection.MaxDecodeBytes)
            {
                return Undecodable(value.Length, "JSON", KafkaUndecodableReason.TooLarge);
            }

            // Under the envelope grant the parse only decides the kind, so no copy of the document is kept.
            if (TryParseJson(value, detection.Counters, payload, out JsonElement element))
            {
                return new RecordValue
                {
                    Kind = KafkaValueKind.Json,
                    FormatName = "JSON",
                    Length = value.Length,
                    Utf8 = payload ? value : null,
                    Json = payload ? element : null,
                };
            }

            looksLikeJson = true;
        }

        // 7. UTF-8 text with no control character other than tab, carriage return and line feed.
        if (IsText(value))
        {
            bool cut = false;

            return new RecordValue
            {
                Kind = KafkaValueKind.Text,
                FormatName = "Text",
                Length = value.Length,
                Utf8 = payload ? CutBytes(value, detection.MaxDecodeBytes, out cut) : null,
                Cut = cut,
                LooksLikeJson = looksLikeJson,
            };
        }

        // 8. Hex: everything else.
        return new RecordValue { Kind = KafkaValueKind.Binary, FormatName = "Binary", Length = value.Length, Preview = payload ? HeaderDecoder.Hex(value) : null };
    }

    private static RecordValue DataOf(byte[] value, string? contentType, bool payload, ValueDetection detection)
    {
        if (contentType is not null && contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            // Under the envelope grant the header decides: the value is not inspected.
            return payload ? AsJson(value, "JSON", detection) : Plain(KafkaValueKind.Json, "JSON", value.Length);
        }

        if (contentType is not null && contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return payload ? AsText(value, "Text", detection) : Plain(KafkaValueKind.Text, "Text", value.Length);
        }

        return new RecordValue { Kind = KafkaValueKind.Binary, FormatName = "Binary", Length = value.Length, Preview = payload ? HeaderDecoder.Hex(value) : null };
    }

    /// <summary>JSON bytes read as JSON: parsed under the depth cap, kept whole up to <c>MaxDecodeBytes</c>.</summary>
    /// <param name="value">The bytes.</param>
    /// <param name="name">The format's name.</param>
    /// <param name="detection">The scan.</param>
    internal static RecordValue AsJson(byte[] value, string name, ValueDetection detection)
    {
        if (value.Length > detection.MaxDecodeBytes)
        {
            return Undecodable(value.Length, name, KafkaUndecodableReason.TooLarge);
        }

        if (!TryParseJson(value, detection.Counters, keep: true, out JsonElement element))
        {
            return Undecodable(value.Length, name, KafkaUndecodableReason.InvalidJson);
        }

        return new RecordValue { Kind = KafkaValueKind.Json, FormatName = name, Length = value.Length, Utf8 = value, Json = element };
    }

    /// <summary>UTF-8 bytes read as text, cut at <c>MaxDecodeBytes</c> between characters.</summary>
    /// <param name="value">The bytes.</param>
    /// <param name="name">The format's name.</param>
    /// <param name="detection">The scan.</param>
    internal static RecordValue AsText(byte[] value, string name, ValueDetection detection)
    {
        if (!Utf8.IsValid(value))
        {
            return Undecodable(value.Length, name, KafkaUndecodableReason.InvalidUtf8);
        }

        byte[] bytes = CutBytes(value, detection.MaxDecodeBytes, out bool cut);
        return new RecordValue { Kind = KafkaValueKind.Text, FormatName = name, Length = value.Length, Utf8 = bytes, Cut = cut };
    }

    private static RecordValue Plain(KafkaValueKind kind, string name, int length) =>
        new() { Kind = kind, FormatName = name, Length = length };

    /// <summary>A value that could not be decoded, and why.</summary>
    /// <param name="length">The value's length.</param>
    /// <param name="name">The format's name.</param>
    /// <param name="reason">Why.</param>
    internal static RecordValue Undecodable(int length, string name, KafkaUndecodableReason reason) =>
        new() { Kind = KafkaValueKind.Undecodable, FormatName = name, Length = length, Reason = reason };

    private static bool StartsLikeJson(byte[] value)
    {
        foreach (byte b in value)
        {
            if (b is (byte) ' ' or (byte) '\t' or (byte) '\r' or (byte) '\n')
            {
                continue;
            }

            return b is (byte) '{' or (byte) '[' or (byte) '"' or (byte) '-' or (byte) 't' or (byte) 'f' or (byte) 'n' || (b >= (byte) '0' && b <= (byte) '9');
        }

        return false;
    }

    /// <summary>Parses JSON under the depth cap and keeps a standalone copy of the root: no document is held.</summary>
    /// <param name="value">The bytes.</param>
    /// <param name="counters">Counts the parse.</param>
    /// <param name="keep">Whether the caller wants the element; when it only wants to know that the bytes are JSON, nothing is copied.</param>
    /// <param name="element">The root, when the bytes are JSON and <paramref name="keep" />.</param>
    internal static bool TryParseJson(byte[] value, DecodeCounters counters, bool keep, out JsonElement element)
    {
        counters.ValueParsed++;

        try
        {
            using JsonDocument document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
            element = keep ? document.RootElement.Clone() : default;
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static bool IsText(byte[] value)
    {
        foreach (byte b in value)
        {
            if (b < 0x20 && b is not ((byte) '\t' or (byte) '\r' or (byte) '\n'))
            {
                return false;
            }
        }

        return Utf8.IsValid(value);
    }

    /// <summary>At most <paramref name="max" /> bytes of <paramref name="value" />, not ending inside a multi-byte character; the same array when it fits.</summary>
    /// <param name="value">The UTF-8 bytes.</param>
    /// <param name="max">The most bytes kept.</param>
    /// <param name="cut">Whether anything was left out.</param>
    internal static byte[] CutBytes(byte[] value, int max, out bool cut)
    {
        if (value.Length <= max)
        {
            cut = false;
            return value;
        }

        int length = max;

        // Do not cut in the middle of a multi-byte character: back up over continuation bytes.
        while (length > 0 && (value[length] & 0xC0) == 0x80)
        {
            length--;
        }

        cut = true;
        return value.AsSpan(0, length).ToArray();
    }

    /// <summary>Keeps at most <paramref name="max" /> bytes of valid UTF-8 as text, cutting at a character boundary.</summary>
    /// <param name="value">The bytes.</param>
    /// <param name="max">The most bytes kept.</param>
    internal static (string Text, bool Truncated) KeepText(byte[] value, int max)
    {
        if (value.Length <= max)
        {
            return (StrictOrLossy(value), false);
        }

        int length = max;

        // Do not cut in the middle of a multi-byte character: back up over continuation bytes.
        while (length > 0 && (value[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return (StrictOrLossy(value.AsSpan(0, length)), true);
    }

    private static string StrictOrLossy(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
