using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MartenStudio.Services.Json;

/// <summary>
/// Rewrites a JSON document into one canonical shape, so that two documents can be compared for what
/// they say rather than for how they were written.
/// </summary>
/// <remarks>
/// <para>
/// Two things are normalised, and both of them are things jsonb does not preserve. Object members are
/// sorted, because Postgres stores a jsonb object as a sorted map and the order a document went in with
/// is simply not the order it comes back out with. Numbers are reduced to their value, because a
/// serializer that writes <c>1.0</c> where the last one wrote <c>1</c> has not changed the document, and
/// a round-trip diff that reports it as a change is a diff nobody will read twice.
/// </para>
/// <para>
/// Whitespace disappears on its own: everything is re-emitted compactly.
/// </para>
/// </remarks>
internal static class JsonCanonicalizer
{
    /// <summary>The longest run of characters a plain (non-exponential) canonical number may use.</summary>
    /// <remarks>
    /// Only ever reached by padding zeros, so the cap decides between <c>0.0000000000000000000000000001</c>
    /// and <c>1E-400</c> - never between keeping a digit and losing one. It is a function of the stripped
    /// digits and the exponent alone, which is what keeps the canonical form unique.
    /// </remarks>
    private const int MaxPlainLength = 40;

    /// <summary>An exponent beyond this is not worth canonicalizing; the token is handed back as it is.</summary>
    private const long MaxExponent = 1_000_000_000L;

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 64 };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Canonicalizes <paramref name="json"/>; throws <see cref="JsonException"/> if it is not JSON.</summary>
    public static JsonNode? Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json, ParseOptions);
        return Canonicalize(document.RootElement);
    }

    /// <summary>Canonicalizes <paramref name="json"/>, or returns <see langword="false"/> if it is not JSON.</summary>
    public static bool TryCanonicalize(string? json, out JsonNode? node, out string? error)
    {
        node = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            node = Canonicalize(json);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Canonicalizes one element of a parsed document.</summary>
    public static JsonNode? Canonicalize(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new JsonObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    // A document may legally repeat a member name; keep the last, which is what every
                    // deserializer this studio talks to would have kept.
                    result[property.Name] = Canonicalize(property.Value);
                }

                return result;

            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(Canonicalize(item));
                }

                return array;

            case JsonValueKind.Number:
                return JsonNode.Parse(NormalizeNumber(element.GetRawText()));

            case JsonValueKind.String:
                return JsonValue.Create(element.GetString());

            case JsonValueKind.True:
                return JsonValue.Create(true);

            case JsonValueKind.False:
                return JsonValue.Create(false);

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }

    /// <summary>The canonical text of a canonicalized node; <c>null</c> for a JSON null.</summary>
    public static string ToCanonicalString(JsonNode? node) =>
        node is null ? "null" : node.ToJsonString(WriteOptions);

    /// <summary>Whether two canonicalized nodes say the same thing.</summary>
    public static bool ValueEquals(JsonNode? left, JsonNode? right) =>
        string.Equals(ToCanonicalString(left), ToCanonicalString(right), StringComparison.Ordinal);

    /// <summary>
    /// The canonical text of a number: <c>1.0</c>, <c>1</c> and <c>1e0</c> all come back as <c>1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here goes anywhere near <see cref="double"/>, and no CLR numeric type is asked to hold the
    /// value. Postgres stores a jsonb number as <c>numeric</c>, which has neither <see cref="decimal"/>'s
    /// 28-digit ceiling nor <see cref="double"/>'s range, so a stored document may perfectly legally hold
    /// <c>1e400</c>, a 33-digit integer or <c>1e-30</c>. Rounding any of those to fit a CLR type is how a
    /// round-trip differ ends up saying "nothing is lost" about a document that lost something - and this
    /// method is the one place that decision is made.
    /// </para>
    /// <para>
    /// So the token is canonicalized as text: a sign, the significant digits, and a power of ten. Two
    /// spellings of the same number produce the same text, every digit survives, and the result is still
    /// a legal JSON number so it can be parsed straight back into a node.
    /// </para>
    /// </remarks>
    internal static string NormalizeNumber(string raw)
    {
        if (!TryReadNumber(raw, out var negative, out var digits, out var exponent))
        {
            // Not a number this method can take apart. Handing the token back unchanged can only ever
            // make two documents look different, never the same, which is the safe direction for a
            // report about what a save would lose.
            return raw;
        }

        return digits.Length == 0 ? "0" : Compose(negative, digits, exponent);
    }

    /// <summary>
    /// Takes a JSON number token apart into a sign, its significant digits and a power of ten, with
    /// leading and trailing zeros removed so that every spelling of one value arrives here the same.
    /// </summary>
    private static bool TryReadNumber(string raw, out bool negative, out string digits, out long exponent)
    {
        negative = false;
        digits = string.Empty;
        exponent = 0;

        var text = raw.AsSpan().Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var index = 0;
        if (text[0] is '+' or '-')
        {
            // JSON has no leading '+', but this method is also called directly with hand-written text.
            negative = text[0] == '-';
            index = 1;
        }

        var significand = new StringBuilder(text.Length);
        var integerDigits = 0;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            significand.Append(text[index]);
            integerDigits++;
            index++;
        }

        var fractionDigits = 0;
        if (index < text.Length && text[index] == '.')
        {
            index++;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                significand.Append(text[index]);
                fractionDigits++;
                index++;
            }
        }

        if (integerDigits + fractionDigits == 0)
        {
            return false;
        }

        long written = 0;
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = false;
            if (index < text.Length && text[index] is '+' or '-')
            {
                exponentNegative = text[index] == '-';
                index++;
            }

            var exponentDigits = 0;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                if (written <= MaxExponent)
                {
                    written = (written * 10) + (text[index] - '0');
                }

                exponentDigits++;
                index++;
            }

            if (exponentDigits == 0 || written > MaxExponent)
            {
                return false;
            }

            if (exponentNegative)
            {
                written = -written;
            }
        }

        if (index != text.Length)
        {
            return false;
        }

        exponent = written - fractionDigits;

        var all = significand.ToString();
        var start = 0;
        while (start < all.Length && all[start] == '0')
        {
            start++;
        }

        var end = all.Length;
        while (end > start && all[end - 1] == '0')
        {
            end--;
            exponent++;
        }

        if (start >= end)
        {
            // Every digit was a zero. decimal keeps both a scale and a sign on zero, so -0.0, 0.000 and
            // 0e9 are one value spelled four ways; there is exactly one canonical zero and it is unsigned.
            negative = false;
            digits = string.Empty;
            exponent = 0;
            return true;
        }

        digits = all[start..end];
        return true;
    }

    /// <summary>Writes the digits and the power of ten back out as the one canonical JSON number.</summary>
    private static string Compose(bool negative, string digits, long exponent)
    {
        var sign = negative ? "-" : string.Empty;

        if (exponent == 0)
        {
            return sign + digits;
        }

        if (exponent > 0)
        {
            if (digits.Length + exponent <= MaxPlainLength)
            {
                return sign + digits + new string('0', (int)exponent);
            }
        }
        else
        {
            var scale = (int)Math.Min(-exponent, int.MaxValue);
            if (scale < digits.Length)
            {
                return sign + digits[..(digits.Length - scale)] + "." + digits[(digits.Length - scale)..];
            }

            if (scale + 2 <= MaxPlainLength)
            {
                return sign + "0." + new string('0', scale - digits.Length) + digits;
            }
        }

        // A number whose plain form would be mostly padding zeros. The exponent chosen is the one that
        // leaves a single digit in front of the point, so this form is unique too.
        var adjusted = exponent + digits.Length - 1;
        var mantissa = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
        return sign + mantissa + "E" + (adjusted < 0 ? "-" : "+") +
            Math.Abs(adjusted).ToString(CultureInfo.InvariantCulture);
    }
}
