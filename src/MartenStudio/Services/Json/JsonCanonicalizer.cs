using System.Globalization;
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
    internal static string NormalizeNumber(string raw)
    {
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            if (value == 0m)
            {
                // decimal keeps both a scale and a sign on zero, so -0.0 and 0.000 are the same value
                // spelled three ways. There is one canonical zero.
                return "0";
            }

            var text = value.ToString(CultureInfo.InvariantCulture);
            if (text.Contains('.', StringComparison.Ordinal))
            {
                text = text.TrimEnd('0').TrimEnd('.');
            }

            return text.Length == 0 || text == "-" ? "0" : text;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var wide))
        {
            return wide.ToString("R", CultureInfo.InvariantCulture);
        }

        return raw;
    }
}
