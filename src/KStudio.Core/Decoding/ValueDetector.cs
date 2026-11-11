using System.Text;
using System.Text.Json;
using System.Text.Unicode;

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
/// A value as the engine understood it. Nothing is ever deserialized into a CLR type: JSON is text (and, transiently, an element) and a host
/// decoder returns JSON or text that the engine parses itself. Under the envelope grant only the kind, the length, the schema id and the
/// CloudEvents attributes are filled in; the text and the preview are for a visitor who may read payloads.
/// </summary>
/// <param name="Kind">What it is.</param>
/// <param name="FormatName">How to name it: <c>JSON</c>, <c>CloudEvents (structured)</c>, a decoder's name.</param>
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
/// <param name="MaxMatchBytes">The most text or hex kept.</param>
/// <param name="Counters">Counts the decodes.</param>
/// <param name="OnDecoderFailed">Told when a host decoder throws; the exception's type name is all that is logged.</param>
internal sealed record ValueDetection(
    KafkaReadGrant Grant,
    IKafkaRecordDecoder? Decoder,
    int MaxDecodeBytes,
    int MaxMatchBytes,
    DecodeCounters Counters,
    Action<Exception>? OnDecoderFailed = null);

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

    /// <summary>Detects what a record's value is, in the order the design states.</summary>
    /// <param name="record">The record.</param>
    /// <param name="detection">The topic and the scan.</param>
    public static KafkaDecodedValue Detect(WireRecord record, ValueDetection detection)
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
            CloudEventView view = CloudEventReader.Read(record, mode, detection.MaxDecodeBytes, detection.Counters);
            string? contentType = view.Events[0].Attributes.GetValueOrDefault("datacontenttype");
            KafkaDecodedValue data = DataOf(value, contentType, payload, detection);
            return data with { FormatName = "CloudEvents (binary), " + data.FormatName, CloudEvent = view };
        }

        // 3. CloudEvents structured: the attributes are the envelope's members, the value is the envelope.
        if (mode is CloudEventMode.Structured or CloudEventMode.StructuredBatch)
        {
            CloudEventView view = CloudEventReader.Read(record, mode, detection.MaxDecodeBytes, detection.Counters);

            if (view.Unreadable is { } unreadable)
            {
                return new KafkaDecodedValue(
                    KafkaValueKind.Undecodable, "CloudEvents (structured)", value.Length, null, false, null, null, unreadable, null, view);
            }

            (string? text, bool truncated) = payload ? KeepText(value, detection.MaxMatchBytes) : (null, false);
            return new KafkaDecodedValue(KafkaValueKind.Json, "CloudEvents (structured)", value.Length, text, truncated, null, null, null, null, view);
        }

        // 4. The topic's declared decoder, only for a visitor who may read payloads; a built-in one forces its format.
        if (detection.Decoder is { } decoder && payload)
        {
            KafkaDecodedValue? decoded = Declared(record, value, decoder, forced, detection);

            if (decoded is not null)
            {
                return decoded;
            }
        }

        // 5. The Confluent wire format: never shown as garbage.
        if (ConfluentWireFormat.TryRead(value, out int schemaId))
        {
            string? preview = payload ? HeaderDecoder.Hex(value.AsSpan(5)) : null;
            return new KafkaDecodedValue(
                KafkaValueKind.ConfluentWireFormat, ConfluentWireFormat.Describe(schemaId), value.Length, null, false, schemaId, preview, null, null, null);
        }

        return Generic(value, payload, detection);
    }

    private static KafkaDecodedValue? Declared(WireRecord record, byte[] value, IKafkaRecordDecoder decoder, KafkaBuiltInDecoder? forced, ValueDetection detection)
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

        try
        {
            KafkaHeader[] headers = [.. record.Headers.Select(static x => new KafkaHeader(x.Name, x.Value is null ? null : (ReadOnlyMemory<byte>?) x.Value))];
            result = decoder.Decode(new KafkaDecodeContext(record.Topic, record.Partition, record.Offset, value, headers));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            detection.OnDecoderFailed?.Invoke(exception);
            return new KafkaDecodedValue(
                KafkaValueKind.Undecodable, decoder.Name, value.Length, null, false, null, null, KafkaUndecodableReason.DecoderFailed, null, null);
        }

        string name = result.FormatName ?? decoder.Name;

        switch (result.Kind)
        {
            case KafkaDecodeResultKind.Json:
                return AsJson(result.JsonBytes.ToArray(), name, detection) with { Length = value.Length };
            case KafkaDecodeResultKind.Text:
                {
                    (string? text, bool truncated) = KeepText(Encoding.UTF8.GetBytes(result.TextValue!), detection.MaxMatchBytes);
                    return new KafkaDecodedValue(KafkaValueKind.Text, name, value.Length, text, truncated, null, null, null, null, null);
                }

            case KafkaDecodeResultKind.Undecodable:
                string reason = result.TextValue!.Length > 200 ? result.TextValue[..200] : result.TextValue;
                return new KafkaDecodedValue(
                    KafkaValueKind.Undecodable, decoder.Name, value.Length, null, false, null, null, KafkaUndecodableReason.DecoderRefused, reason, null);
            default:
                return null;
        }
    }

    private static KafkaDecodedValue Generic(byte[] value, bool payload, ValueDetection detection)
    {
        // 6. JSON: the first non-whitespace byte starts a JSON value and the whole value parses.
        if (StartsLikeJson(value))
        {
            if (value.Length > detection.MaxDecodeBytes)
            {
                return Undecodable(value.Length, "JSON", KafkaUndecodableReason.TooLarge);
            }

            if (ParsesAsJson(value))
            {
                (string? text, bool truncated) = payload ? KeepText(value, detection.MaxMatchBytes) : (null, false);
                return new KafkaDecodedValue(KafkaValueKind.Json, "JSON", value.Length, text, truncated, null, null, null, null, null);
            }
        }

        // 7. UTF-8 text with no control character other than tab, carriage return and line feed.
        if (IsText(value))
        {
            (string? text, bool truncated) = payload ? KeepText(value, detection.MaxMatchBytes) : (null, false);
            return new KafkaDecodedValue(KafkaValueKind.Text, "Text", value.Length, text, truncated, null, null, null, null, null);
        }

        // 8. Hex: everything else.
        return new KafkaDecodedValue(
            KafkaValueKind.Binary, "Binary", value.Length, null, false, null, payload ? HeaderDecoder.Hex(value) : null, null, null, null);
    }

    private static KafkaDecodedValue DataOf(byte[] value, string? contentType, bool payload, ValueDetection detection)
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

        return new KafkaDecodedValue(
            KafkaValueKind.Binary, "Binary", value.Length, null, false, null, payload ? HeaderDecoder.Hex(value) : null, null, null, null);
    }

    private static KafkaDecodedValue AsJson(byte[] value, string name, ValueDetection detection)
    {
        if (value.Length > detection.MaxDecodeBytes)
        {
            return Undecodable(value.Length, name, KafkaUndecodableReason.TooLarge);
        }

        if (!ParsesAsJson(value))
        {
            return Undecodable(value.Length, name, KafkaUndecodableReason.InvalidJson);
        }

        (string? text, bool truncated) = KeepText(value, detection.MaxMatchBytes);
        return new KafkaDecodedValue(KafkaValueKind.Json, name, value.Length, text, truncated, null, null, null, null, null);
    }

    private static KafkaDecodedValue AsText(byte[] value, string name, ValueDetection detection)
    {
        if (!Utf8.IsValid(value))
        {
            return Undecodable(value.Length, name, KafkaUndecodableReason.InvalidUtf8);
        }

        (string? text, bool truncated) = KeepText(value, detection.MaxMatchBytes);
        return new KafkaDecodedValue(KafkaValueKind.Text, name, value.Length, text, truncated, null, null, null, null, null);
    }

    private static KafkaDecodedValue Plain(KafkaValueKind kind, string name, int length) =>
        new(kind, name, length, null, false, null, null, null, null, null);

    private static KafkaDecodedValue Undecodable(int length, string name, KafkaUndecodableReason reason) =>
        new(KafkaValueKind.Undecodable, name, length, null, false, null, null, reason, null, null);

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

    private static bool ParsesAsJson(byte[] value)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
            return true;
        }
        catch (JsonException)
        {
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
