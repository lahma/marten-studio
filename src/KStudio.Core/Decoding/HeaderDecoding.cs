using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KStudio.Core.Decoding;

/// <summary>
/// Counts the real decodes a scan makes, by kind (decision 7.5). A decode is the work of turning bytes into text or a tree; comparing bytes
/// is not one. The counters are how tests prove that clauses run cheapest first and that a record decided by its key is never parsed.
/// </summary>
internal sealed class DecodeCounters
{
    /// <summary>Keys formatted as text (<c>~</c> on a key, or <c>any</c>).</summary>
    public long KeyText { get; set; }

    /// <summary>Header values decoded as text.</summary>
    public long HeaderText { get; set; }

    /// <summary>Records read as binary-mode CloudEvents.</summary>
    public long CloudEventBinary { get; set; }

    /// <summary>Records whose value was parsed as a structured CloudEvent.</summary>
    public long CloudEventStructured { get; set; }

    /// <summary>Values parsed or inspected to find their kind (only for matches).</summary>
    public long ValueDetected { get; set; }

    /// <summary>The sum of every kind.</summary>
    public long Total => KeyText + HeaderText + CloudEventBinary + CloudEventStructured + ValueDetected;

    /// <summary>A copy, for a result.</summary>
    public DecodeCounters Copy() => (DecodeCounters) MemberwiseClone();
}

/// <summary>A W3C trace context found in a header (decision 8.5).</summary>
/// <param name="TraceId">The 32-hex-digit trace id.</param>
/// <param name="SpanId">The 16-hex-digit span id.</param>
internal sealed record KafkaTraceContext(string TraceId, string SpanId);

/// <summary>Turns header values into text for display and comparison (decision 8.5).</summary>
internal static partial class HeaderDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The most bytes of a hex rendering kept.</summary>
    public const int MaxHexBytes = 64;

    /// <summary>Header values as text: strict UTF-8 with no C0 control other than tab (carriage return and line feed are controls too, and a header is one line).</summary>
    /// <param name="value">The value's bytes.</param>
    /// <param name="text">The text, when the bytes are text.</param>
    public static bool TryText(ReadOnlySpan<byte> value, out string text)
    {
        text = string.Empty;

        foreach (byte b in value)
        {
            if (b < 0x20 && b != (byte) '\t')
            {
                return false;
            }
        }

        try
        {
            text = StrictUtf8.GetString(value);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary><c>0x</c> and the lower-case hex of at most <see cref="MaxHexBytes" /> bytes.</summary>
    /// <param name="value">The bytes.</param>
    public static string Hex(ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> shown = value.Length > MaxHexBytes ? value[..MaxHexBytes] : value;
        return "0x" + Convert.ToHexStringLower(shown);
    }

    /// <summary>Text for a header value: the text, else the hex; <see langword="null" /> for a null value.</summary>
    /// <param name="value">The value.</param>
    public static string? Display(byte[]? value) => value is null ? null : TryText(value, out string text) ? text : Hex(value);

    /// <summary>Finds a W3C trace context (<c>traceparent</c>, <c>ce_traceparent</c>, or Wolverine's <c>parent-id</c> when it has that form).</summary>
    /// <param name="headers">The record's headers.</param>
    public static KafkaTraceContext? FindTrace(IEnumerable<KeyValuePair<string, byte[]?>> headers)
    {
        foreach (KeyValuePair<string, byte[]?> header in headers)
        {
            if (header.Value is null
                || !(header.Key.Equals("traceparent", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("ce_traceparent", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("parent-id", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (header.Value.Length == 55 && TryText(header.Value, out string text) && TraceParent().Match(text) is { Success: true } match)
            {
                string trace = match.Groups[1].Value;
                string span = match.Groups[2].Value;

                if (!IsAllZero(trace) && !IsAllZero(span))
                {
                    return new KafkaTraceContext(trace, span);
                }
            }
        }

        return null;
    }

    private static bool IsAllZero(string hex) => hex.AsSpan().IndexOfAnyExcept('0') < 0;

    [GeneratedRegex("^00-([0-9a-f]{32})-([0-9a-f]{16})-[0-9a-f]{2}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TraceParent();
}

/// <summary>The Confluent wire format: a zero byte, a four-byte big-endian schema id, then the payload.</summary>
internal static class ConfluentWireFormat
{
    /// <summary>Whether a value starts like the Confluent wire format, and its schema id.</summary>
    /// <param name="value">The value.</param>
    /// <param name="schemaId">The schema id.</param>
    public static bool TryRead(ReadOnlySpan<byte> value, out int schemaId)
    {
        schemaId = 0;

        if (value.Length < 5 || value[0] != 0)
        {
            return false;
        }

        schemaId = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(value.Slice(1, 4));
        return true;
    }

    /// <summary>How the format reads in the match.</summary>
    /// <param name="schemaId">The schema id.</param>
    public static string Describe(int schemaId) => string.Create(CultureInfo.InvariantCulture, $"Confluent wire format, schema id {schemaId}");
}
