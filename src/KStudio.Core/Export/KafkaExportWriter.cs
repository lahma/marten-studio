using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using KStudio.Core.Decoding;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;

namespace KStudio.Core.Export;

/// <summary>The formats an export is written in (D-M5 decision 2.2).</summary>
internal enum KafkaExportFormat
{
    /// <summary>One JSON object per line: every field typed, headers and JSON values nested. The default.</summary>
    JsonLines,

    /// <summary>A spreadsheet's file: fixed columns, RFC 4180 quoting, a UTF-8 byte-order mark, and no cell that starts a formula.</summary>
    Csv,

    /// <summary>One record's value text alone, from the record page.</summary>
    Value,
}

/// <summary>What <see cref="KafkaExportWriter" /> wrote.</summary>
/// <param name="Format">The format.</param>
/// <param name="Content">The file, whole. Empty when <paramref name="Refusal" /> is set.</param>
/// <param name="Extension">The file name's extension, without the dot.</param>
/// <param name="ContentType">The MIME type.</param>
/// <param name="Written">How many records the file holds.</param>
/// <param name="LeftOut">How many records were left out at the size limit: the first that would not fit, and every one after it.</param>
/// <param name="IncludesValues">Whether any record in the file was read with the payload grant, so its value (or the rule that withheld it) is in the file.</param>
/// <param name="ValueTruncated">For <see cref="KafkaExportFormat.Value" />: whether the value text was cut at the match's cap.</param>
/// <param name="Refusal">Why nothing was written (a defect of the caller's or a value that has no text); <see langword="null" /> when a file was.</param>
internal sealed record KafkaExportFile(
    KafkaExportFormat Format,
    byte[] Content,
    string Extension,
    string ContentType,
    int Written,
    int LeftOut,
    bool IncludesValues,
    bool ValueTruncated,
    string? Refusal)
{
    /// <summary>The file's size in bytes.</summary>
    public long Bytes => Content.Length;
}

/// <summary>
/// Writes the records a search, a tail or the record page holds as a file (AGENTS.md D45, D-M5 decisions 2.2 to 2.4 and 2.6). It is pure: it reads only
/// <see cref="KafkaMatch" /> members, which hold no unredacted data (D40) and which only the match factory makes, so redaction in a file is the
/// factory's and not a second implementation; it reads nothing from Kafka, takes no clock and no policy, and writes nothing but the bytes it returns.
/// </summary>
internal static class KafkaExportWriter
{
    /// <summary>What a value that has no text to save says (D-M5 2.3).</summary>
    public const string NoValueText = "This value has no text to save";

    private static readonly UTF8Encoding Utf8 = new(false);

    // A cell is read by a person in a spreadsheet: the characters stay as they are (a header's ä is not \u00E4); the cell is quoted by RFC 4180, not by JSON.
    private static readonly JsonWriterOptions SpreadsheetOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static readonly string[] CsvColumns =
    [
        "topic", "partition", "offset", "timestamp", "timestamp_type", "key", "key_format", "headers", "value_format", "value_length", "value", "value_truncated", "redacted",
    ];

    /// <summary>Writes <paramref name="matches" /> in <paramref name="format" />, in the order topic (ordinal), partition, offset.</summary>
    /// <param name="matches">The records; any number, none for an empty file.</param>
    /// <param name="format">The format. <see cref="KafkaExportFormat.Value" /> takes exactly one record.</param>
    /// <param name="maxBytes">The most bytes the file may hold: whole rows are appended while the total stays at or below it.</param>
    public static KafkaExportFile Write(IReadOnlyList<KafkaMatch> matches, KafkaExportFormat format, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);

        KafkaMatch[] ordered = [.. matches.OrderBy(static x => x.Topic, StringComparer.Ordinal).ThenBy(static x => x.Partition).ThenBy(static x => x.Offset)];

        return format switch
        {
            KafkaExportFormat.JsonLines => WriteRows(ordered, format, maxBytes, "jsonl", "application/x-ndjson", header: null, JsonLine),
            KafkaExportFormat.Csv => WriteRows(ordered, format, maxBytes, "csv", "text/csv", Bom.Concat(Utf8.GetBytes(string.Join(",", CsvColumns) + "\r\n")).ToArray(), CsvRow),
            KafkaExportFormat.Value => WriteValue(ordered, maxBytes),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not an export format."),
        };
    }

    private static KafkaExportFile WriteRows(
        KafkaMatch[] ordered,
        KafkaExportFormat format,
        long maxBytes,
        string extension,
        string contentType,
        byte[]? header,
        Func<KafkaMatch, byte[]> row)
    {
        // The rows are kept as they are made and copied once into a buffer of exactly the file's size, so that the peak is twice the file (the rows and
        // the buffer, never a growing stream's old and new arrays together) and the file is handed over without a further copy.
        List<byte[]> rows = [];
        long length = header?.Length ?? 0;
        bool values = false;

        foreach (KafkaMatch match in ordered)
        {
            byte[] bytes = row(match);

            // The first row that would not fit, and every one after it, is left out and counted: a file never holds a cut row.
            if (length + bytes.Length > maxBytes)
            {
                break;
            }

            rows.Add(bytes);
            length += bytes.Length;
            values |= match.ReadWithPayload;
        }

        byte[] content = new byte[length];
        int at = 0;

        if (header is not null)
        {
            header.CopyTo(content, 0);
            at = header.Length;
        }

        foreach (byte[] bytes in rows)
        {
            bytes.CopyTo(content, at);
            at += bytes.Length;
        }

        return new KafkaExportFile(format, content, extension, contentType, rows.Count, ordered.Length - rows.Count, values, false, null);
    }

    private static KafkaExportFile WriteValue(KafkaMatch[] ordered, long maxBytes)
    {
        if (ordered.Length != 1)
        {
            return Refused("A value is saved from one record.");
        }

        KafkaMatch match = ordered[0];

        // A match read without the payload grant holds no value, whatever the visitor's grant is now; a withheld value is withheld in a file too.
        if (!match.ReadWithPayload || match.ValueWithheldBy is not null || match.ValueText is not { } text)
        {
            return Refused(NoValueText);
        }

        bool json = match.ValueTextKind == KafkaValueTextKind.Json;
        string extension = json ? "json" : "txt";
        string contentType = json ? "application/json" : "text/plain";
        byte[] content = Utf8.GetBytes(text);

        // A value over the limit is refused by name, so that the visitor learns which setting to raise; it is never an empty file.
        if (content.Length > maxBytes)
        {
            return Refused(string.Create(
                CultureInfo.InvariantCulture,
                $"This value is {KafkaRecordRead.Bytes(content.Length)}, more than KafkaStudioOptions.Limits.MaxExportBytes ({KafkaRecordRead.Bytes(maxBytes)})"));
        }

        return new KafkaExportFile(KafkaExportFormat.Value, content, extension, contentType, 1, 0, true, match.ValueTruncated, null);
    }

    private static KafkaExportFile Refused(string reason) =>
        new(KafkaExportFormat.Value, [], "txt", "text/plain", 0, 0, false, false, reason);

    // ------------------------------------------------------------------------------------------------
    // JSON Lines
    // ------------------------------------------------------------------------------------------------

    private static byte[] JsonLine(KafkaMatch match)
    {
        ArrayBufferWriter<byte> buffer = new(1024);

        using (Utf8JsonWriter json = new(buffer))
        {
            WriteObject(json, match);
        }

        buffer.GetSpan(1)[0] = (byte) '\n';
        buffer.Advance(1);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteObject(Utf8JsonWriter json, KafkaMatch match)
    {
        json.WriteStartObject();
        json.WriteString("topic", match.Topic);
        json.WriteNumber("partition", match.Partition);
        json.WriteNumber("offset", match.Offset);
        WriteTimestamp(json, match);
        json.WriteString("timestampType", match.TimestampType.ToString());

        json.WritePropertyName("key");
        WriteField(json, match.Key);

        if (match.Key.Truncated)
        {
            json.WriteBoolean("keyTruncated", true);
        }

        json.WriteString("keyFormat", match.KeyFormat);

        json.WritePropertyName("headers");
        WriteHeaders(json, match);

        if (match.HeadersOmitted > 0)
        {
            json.WriteNumber("headersOmitted", match.HeadersOmitted);
        }

        if (match.CloudEventMode != CloudEventMode.None || match.CloudEventAttributes.Count > 0)
        {
            json.WriteStartObject("cloudEvent");
            json.WriteString("mode", match.CloudEventMode.ToString());
            json.WritePropertyName("attributes");
            WriteAttributes(json, match.CloudEventAttributes);
            json.WriteEndObject();
        }

        if (match.Trace is { } trace)
        {
            json.WriteStartObject("trace");
            json.WriteString("traceId", trace.TraceId);
            json.WriteString("spanId", trace.SpanId);
            json.WriteEndObject();
        }

        json.WriteString("valueFormat", match.ValueFormat);
        json.WriteNumber("valueLength", match.ValueLength);

        // The envelope grant ends the row's value here; a match read without the payload grant is an envelope row whatever the visitor may do now.
        if (match.ReadWithPayload)
        {
            WriteValue(json, match);
        }

        string[] redacted = Redacted(match);

        if (redacted.Length > 0)
        {
            json.WriteStartArray("redacted");

            foreach (string item in redacted)
            {
                json.WriteStringValue(item);
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    private static void WriteTimestamp(Utf8JsonWriter json, KafkaMatch match)
    {
        // A record that carries no timestamp has none to write: the epoch the engine keeps in its place would read as 1970.
        if (match.TimestampType == WireTimestampType.NotAvailable)
        {
            json.WriteNull("timestamp");
            return;
        }

        json.WriteString("timestamp", TimestampText(match));
    }

    private static string TimestampText(KafkaMatch match) =>
        match.TimestampType == WireTimestampType.NotAvailable
            ? string.Empty
            : match.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void WriteField(Utf8JsonWriter json, KafkaMatchField field)
    {
        if (field.IsRedacted)
        {
            json.WriteStringValue(field.Display);
        }
        else if (field.IsNull)
        {
            json.WriteNullValue();
        }
        else
        {
            json.WriteStringValue(field.Text ?? string.Empty);
        }
    }

    private static void WriteHeaders(Utf8JsonWriter json, KafkaMatch match)
    {
        json.WriteStartArray();

        foreach (KafkaMatchHeader header in match.Headers)
        {
            json.WriteStartObject();
            json.WriteString("name", header.Name);
            json.WritePropertyName("value");
            WriteField(json, header.Value);

            if (header.Value.Truncated)
            {
                json.WriteBoolean("truncated", true);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteAttributes(Utf8JsonWriter json, IReadOnlyList<KafkaMatchHeader> attributes)
    {
        json.WriteStartArray();

        foreach (KafkaMatchHeader attribute in attributes)
        {
            json.WriteStartObject();
            json.WriteString("name", attribute.Name);
            json.WritePropertyName("value");
            WriteField(json, attribute.Value);

            if (attribute.Value.Truncated)
            {
                json.WriteBoolean("truncated", true);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter json, KafkaMatch match)
    {
        if (match.ValueWithheldBy is { } rule)
        {
            json.WriteString("valueWithheldBy", rule);
        }
        else if (match.ValueText is { } text)
        {
            json.WritePropertyName("value");

            // Re-emitted compact: a producer's newlines inside a JSON value must never end a line, or forge one.
            if (match.ValueTextKind == KafkaValueTextKind.Json && !match.ValueTruncated && TryParse(text) is { } document)
            {
                using (document)
                {
                    document.RootElement.WriteTo(json);
                }
            }
            else
            {
                json.WriteStringValue(text);
            }
        }
        else if (match.ValuePreview is { } preview)
        {
            json.WriteString("value", preview);
        }
        else if (match.ValueKind == KafkaValueKind.Tombstone)
        {
            json.WriteNull("value");
        }

        if (match.ValueTruncated && match.ValueWithheldBy is null)
        {
            json.WriteBoolean("valueTruncated", true);
        }

        if (match.ValueIsCloudEventData)
        {
            json.WriteBoolean("valueIsCloudEventData", true);
        }
    }

    /// <summary>The text parsed as exactly one JSON value, or <see langword="null" /> when it is not (the caller disposes it).</summary>
    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text of a JSON value written compactly with the characters kept as they are; <see langword="null" /> when the text is not JSON.</summary>
    private static string? Compact(string text)
    {
        if (TryParse(text) is not { } document)
        {
            return null;
        }

        using (document)
        {
            ArrayBufferWriter<byte> buffer = new(text.Length);

            using (Utf8JsonWriter json = new(buffer, SpreadsheetOptions))
            {
                document.RootElement.WriteTo(json);
            }

            return Utf8.GetString(buffer.WrittenSpan);
        }
    }

    /// <summary>What was masked on this row: the key, each header and attribute by name, and the rules that masked part of the value.</summary>
    private static string[] Redacted(KafkaMatch match)
    {
        List<string> redacted = [];

        if (match.Key.IsRedacted)
        {
            redacted.Add("key");
        }

        redacted.AddRange(match.Headers.Where(static x => x.Value.IsRedacted).Select(static x => "header:" + x.Name));
        redacted.AddRange(match.CloudEventAttributes.Where(static x => x.Value.IsRedacted).Select(static x => "ce:" + x.Name));

        if (match.ReadWithPayload)
        {
            redacted.AddRange(match.ValueMaskedBy);
        }

        return [.. redacted];
    }

    // ------------------------------------------------------------------------------------------------
    // CSV
    // ------------------------------------------------------------------------------------------------

    private static byte[] CsvRow(KafkaMatch match)
    {
        ArrayBufferWriter<byte> buffer = new(256);

        using (Utf8JsonWriter json = new(buffer, SpreadsheetOptions))
        {
            WriteHeaders(json, match);
        }

        string headers = Utf8.GetString(buffer.WrittenSpan);

        // The value, as text: a JSON value compact, a binary one as the preview's hex; empty under the envelope or when the engine withheld it.
        string value = match.ReadWithPayload && match.ValueWithheldBy is null ? CsvValue(match) : string.Empty;
        bool truncated = match.ReadWithPayload && match.ValueWithheldBy is null && match.ValueTruncated;

        string[] cells =
        [
            match.Topic,
            match.Partition.ToString(CultureInfo.InvariantCulture),
            match.Offset.ToString(CultureInfo.InvariantCulture),
            TimestampText(match),
            match.TimestampType.ToString(),
            match.Key.IsNull ? string.Empty : match.Key.Display,
            match.KeyFormat,
            headers,
            match.ValueFormat,
            match.ValueLength.ToString(CultureInfo.InvariantCulture),
            value,
            truncated ? "true" : "false",
            string.Join("; ", Redacted(match)),
        ];

        StringBuilder line = new();

        for (int index = 0; index < cells.Length; index++)
        {
            if (index > 0)
            {
                line.Append(',');
            }

            line.Append(CsvCell(cells[index]));
        }

        line.Append("\r\n");
        return Utf8.GetBytes(line.ToString());
    }

    /// <summary>A JSON value compact on one line (a newline inside it is not the producer's to put in a cell); any other text as it is.</summary>
    private static string CsvValue(KafkaMatch match)
    {
        if (match.ValueText is { } text)
        {
            return match.ValueTextKind == KafkaValueTextKind.Json && !match.ValueTruncated ? Compact(text) ?? text : text;
        }

        return match.ValuePreview ?? string.Empty;
    }

    /// <summary>
    /// One cell: a cell that starts with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage return is prefixed with an apostrophe (OWASP's rule
    /// against CSV injection, because every key, header and value is whatever a producer wrote), and a cell with a comma, a quote, CR or LF is quoted with
    /// its quotes doubled (RFC 4180).
    /// </summary>
    internal static string CsvCell(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }
}
