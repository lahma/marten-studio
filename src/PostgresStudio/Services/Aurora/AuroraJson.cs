using System.Globalization;
using System.Text.Json;

namespace PostgresStudio.Services.Aurora;

/// <summary>
/// Reads Aurora's function rows by column name, from the <c>to_jsonb(row)</c> text <see cref="PostgresStudio.Internal.Sql.AuroraQueries" />
/// returns: a column a release lacks is absent and reads as <see langword="null" />, never as an error or as another column's
/// value (B0: <c>aurora_replica_status()</c>'s column set varies by release).
/// </summary>
/// <remarks>
/// Parsing a catalog function's answer, not serializing anybody's document: the text is Postgres' own JSON of a record, read
/// with the BCL's <see cref="JsonDocument" /> and never bound to a type.
/// </remarks>
internal static class AuroraJson
{
    /// <summary>Each object of a JSON array - empty for <see langword="null" />, an empty array or anything that is not an array of objects.</summary>
    public static IReadOnlyList<JsonElement> Objects(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. document.RootElement.EnumerateArray().Where(static x => x.ValueKind == JsonValueKind.Object).Select(static x => x.Clone())];
    }

    /// <summary>One JSON object, or <see langword="null" /> for <see langword="null" /> or anything that is not an object.</summary>
    public static JsonElement? Object(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
    }

    /// <summary>
    /// The one number a function's row holds - the row itself when the function returned a scalar (<c>to_jsonb</c> of a
    /// number), its single (or first) column when it returned a record - or <see langword="null" />.
    /// </summary>
    public static double? Scalar(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return root.ValueKind switch
        {
            JsonValueKind.Number => root.GetDouble(),
            JsonValueKind.String => ParseNumber(root.GetString()),
            JsonValueKind.Object => root.EnumerateObject().Select(static x => NumberOf(x.Value)).FirstOrDefault(),
            _ => null,
        };
    }

    /// <summary>A column as a number - a JSON number, or a string Postgres wrote one in - or <see langword="null" />.</summary>
    public static double? Number(JsonElement row, string column) =>
        row.TryGetProperty(column, out JsonElement value) ? NumberOf(value) : null;

    /// <summary>A column as text - a string, or a number's or boolean's JSON spelling - or <see langword="null" />.</summary>
    public static string? Text(JsonElement row, string column) =>
        row.TryGetProperty(column, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>A <c>timestamptz</c> column as <c>to_jsonb</c> writes one (ISO 8601 with an offset), or <see langword="null" />.</summary>
    public static DateTimeOffset? Timestamp(JsonElement row, string column) =>
        Text(row, column) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
            ? at
            : null;

    private static double? NumberOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String => ParseNumber(value.GetString()),
        _ => null,
    };

    private static double? ParseNumber(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
}
