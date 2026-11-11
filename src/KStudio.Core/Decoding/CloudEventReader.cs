using System.Text;
using System.Text.Json;

using KStudio.Core.Wire;

namespace KStudio.Core.Decoding;

/// <summary>How a record carries a CloudEvent.</summary>
internal enum CloudEventMode
{
    /// <summary>It is not a CloudEvent.</summary>
    None,

    /// <summary>Binary mode: attributes are <c>ce_*</c> headers and the value is the data.</summary>
    Binary,

    /// <summary>Structured mode: the value is a JSON envelope, one event.</summary>
    Structured,

    /// <summary>Structured mode, a batch: the value is a JSON array of envelopes.</summary>
    StructuredBatch,
}

/// <summary>Why a value could not be read as it should have been (decision 8.4).</summary>
internal enum KafkaUndecodableReason
{
    /// <summary>Declared or detected JSON that does not parse.</summary>
    InvalidJson,

    /// <summary>Larger than <c>MaxDecodeBytes</c>.</summary>
    TooLarge,

    /// <summary>Not valid UTF-8.</summary>
    InvalidUtf8,

    /// <summary>Declared as CloudEvents but has no <c>specversion</c>.</summary>
    NotACloudEvent,

    /// <summary>A host decoder threw.</summary>
    DecoderFailed,

    /// <summary>A host decoder returned <c>Undecodable</c>.</summary>
    DecoderRefused,
}

/// <summary>One CloudEvent's attributes: name to text. <c>data</c> and <c>data_base64</c> are never among them.</summary>
/// <param name="Attributes">The attributes.</param>
internal sealed record KafkaCloudEvent(IReadOnlyDictionary<string, string> Attributes);

/// <summary>What the engine found when it looked for CloudEvents in a record.</summary>
/// <param name="Mode">How the record carries one.</param>
/// <param name="Events">The events: one, or the elements of a batch.</param>
/// <param name="Unreadable">Why the attributes could not be read, when they could not.</param>
internal sealed record CloudEventView(CloudEventMode Mode, IReadOnlyList<KafkaCloudEvent> Events, KafkaUndecodableReason? Unreadable)
{
    /// <summary>The record is not a CloudEvent.</summary>
    public static CloudEventView None { get; } = new(CloudEventMode.None, [], null);
}

/// <summary>
/// Reads CloudEvents attributes (decision 7.4 and 8.1). Binary mode reads <c>ce_*</c> headers. Structured mode walks the top-level members
/// of the JSON envelope and skips <c>data</c> and <c>data_base64</c> without materialising them, so under the envelope grant nothing of the
/// payload is read.
/// </summary>
internal static class CloudEventReader
{
    private const string BinaryMarker = "ce_specversion";
    private const string StructuredType = "application/cloudevents+json";
    private const string StructuredBatchType = "application/cloudevents-batch+json";

    /// <summary>Decides how the record carries a CloudEvent, without reading its value.</summary>
    /// <param name="record">The record.</param>
    /// <param name="declaresStructured">Whether the topic declared <c>KafkaDecoders.CloudEvents</c>.</param>
    public static CloudEventMode Classify(WireRecord record, bool declaresStructured)
    {
        string? contentType = null;

        foreach (WireHeader header in record.Headers)
        {
            if (header.Name.Equals(BinaryMarker, StringComparison.Ordinal) && header.Value is not null)
            {
                return CloudEventMode.Binary;
            }

            if (contentType is null && header.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase) && header.Value is not null
                && HeaderDecoder.TryText(header.Value, out string text))
            {
                contentType = text;
            }
        }

        if (contentType is not null && contentType.StartsWith(StructuredBatchType, StringComparison.OrdinalIgnoreCase))
        {
            return CloudEventMode.StructuredBatch;
        }

        if ((contentType is not null && contentType.StartsWith(StructuredType, StringComparison.OrdinalIgnoreCase)) || declaresStructured)
        {
            return CloudEventMode.Structured;
        }

        return CloudEventMode.None;
    }

    /// <summary>Reads the attributes of a record.</summary>
    /// <param name="record">The record.</param>
    /// <param name="mode">How <see cref="Classify" /> said it is carried.</param>
    /// <param name="maxDecodeBytes">The largest structured value that is parsed.</param>
    /// <param name="counters">Counts the decode.</param>
    public static CloudEventView Read(WireRecord record, CloudEventMode mode, int maxDecodeBytes, DecodeCounters counters)
    {
        switch (mode)
        {
            case CloudEventMode.Binary:
                counters.CloudEventBinary++;
                return ReadBinary(record);
            case CloudEventMode.Structured or CloudEventMode.StructuredBatch:
                counters.CloudEventStructured++;
                return ReadStructured(record, mode, maxDecodeBytes);
            default:
                return CloudEventView.None;
        }
    }

    private static CloudEventView ReadBinary(WireRecord record)
    {
        Dictionary<string, string> attributes = new(StringComparer.Ordinal);

        foreach (WireHeader header in record.Headers)
        {
            if (header.Value is null || !HeaderDecoder.TryText(header.Value, out string text))
            {
                continue;
            }

            if (header.Name.StartsWith("ce_", StringComparison.Ordinal) && header.Name.Length > 3)
            {
                attributes[header.Name[3..]] = text;
            }
            else if (header.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            {
                attributes.TryAdd("datacontenttype", text);
            }
        }

        return new CloudEventView(CloudEventMode.Binary, [new KafkaCloudEvent(attributes)], null);
    }

    private static CloudEventView ReadStructured(WireRecord record, CloudEventMode mode, int maxDecodeBytes)
    {
        if (record.Value is null)
        {
            return new CloudEventView(mode, [], null);
        }

        if (record.Value.Length > maxDecodeBytes)
        {
            return new CloudEventView(mode, [], KafkaUndecodableReason.TooLarge);
        }

        try
        {
            Utf8JsonReader reader = new(record.Value, new JsonReaderOptions { MaxDepth = 64 });
            List<KafkaCloudEvent> events = [];

            if (!reader.Read())
            {
                return new CloudEventView(mode, [], KafkaUndecodableReason.InvalidJson);
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                events.Add(ReadEnvelope(ref reader));
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        return new CloudEventView(mode, [], KafkaUndecodableReason.NotACloudEvent);
                    }

                    events.Add(ReadEnvelope(ref reader));
                }
            }
            else
            {
                return new CloudEventView(mode, [], KafkaUndecodableReason.NotACloudEvent);
            }

            // An envelope without a specversion is not a CloudEvent, whatever the topic declared.
            if (events.Any(static x => !x.Attributes.ContainsKey("specversion")))
            {
                return new CloudEventView(mode, [], KafkaUndecodableReason.NotACloudEvent);
            }

            return new CloudEventView(mode, events, null);
        }
        catch (JsonException)
        {
            return new CloudEventView(mode, [], KafkaUndecodableReason.InvalidJson);
        }
    }

    private static KafkaCloudEvent ReadEnvelope(ref Utf8JsonReader reader)
    {
        Dictionary<string, string> attributes = new(StringComparer.Ordinal);

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? string.Empty;
            reader.Read();

            if (name is "data" or "data_base64")
            {
                reader.Skip();
                continue;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    attributes[name] = reader.GetString() ?? string.Empty;
                    break;
                case JsonTokenType.Number:
                    // The reader is over one array, so a value is never split across segments.
                    attributes[name] = Encoding.UTF8.GetString(reader.ValueSpan);
                    break;
                case JsonTokenType.True:
                    attributes[name] = "true";
                    break;
                case JsonTokenType.False:
                    attributes[name] = "false";
                    break;
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    reader.Skip();
                    break;
            }
        }

        return new KafkaCloudEvent(attributes);
    }
}
