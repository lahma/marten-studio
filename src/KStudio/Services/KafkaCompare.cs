using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;

namespace KStudio.Services;

/// <summary>How one header or attribute compares across two records.</summary>
/// <param name="Name">The name, with <c> #2</c> for a second occurrence of the same header.</param>
/// <param name="Status">The sentence: <c>same</c>, <c>changed</c>, <c>only in 0/12</c>, or <c>masked (redacted by …)</c>.</param>
/// <param name="Left">The first record's value as shown, or <see langword="null" /> when it has none of this name.</param>
/// <param name="Right">The second record's value as shown, or <see langword="null" /> when it has none of this name.</param>
internal sealed record KafkaFieldComparison(string Name, string Status, string? Left, string? Right);

/// <summary>What the value section of a compare says.</summary>
internal enum KafkaValueComparisonKind
{
    /// <summary>A side's value is not the visitor's to see: the sentences say whose and why.</summary>
    NotShown,

    /// <summary>Nothing to diff: a sentence says why the two are not compared, or that they are the same.</summary>
    Sentence,

    /// <summary>Both are JSON, and the diff component draws them.</summary>
    Json,

    /// <summary>Both are text, drawn side by side.</summary>
    Text,
}

/// <summary>The value section of a compare, as data the component draws.</summary>
/// <param name="Kind">What to draw.</param>
/// <param name="Lines">The sentences, in order.</param>
/// <param name="MaskedPaths">The paths not compared because a rule masks them on either side.</param>
/// <param name="LeftJson">The first side's JSON, with the masked parts left out of both sides.</param>
/// <param name="RightJson">The second side's JSON, with the masked parts left out of both sides.</param>
/// <param name="LeftText">The first side's text, cut at 64 KiB.</param>
/// <param name="RightText">The second side's text, cut at 64 KiB.</param>
internal sealed record KafkaValueComparison(
    KafkaValueComparisonKind Kind,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> MaskedPaths,
    string? LeftJson = null,
    string? RightJson = null,
    string? LeftText = null,
    string? RightText = null);

/// <summary>
/// What a compare of two records says (D-PAYLOAD decision 5): a pure function of two in-memory matches, which read nothing. A masked part is listed and
/// never compared; a value that is cut, withheld or not the visitor's is a sentence and not a diff.
/// </summary>
internal static class KafkaCompare
{
    /// <summary>The most characters of a text value each side of a text compare shows.</summary>
    public const int TextCut = 64 * 1024;

    private const string Placeholder = "•••";

    /// <summary>How a record is named in a compare: <c>2/145</c>, with the topic when the two records are of different topics.</summary>
    /// <param name="record">The record.</param>
    /// <param name="other">The other record.</param>
    public static string Label(KafkaMatch record, KafkaMatch other)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(other);

        string place = record.Partition.ToString(CultureInfo.InvariantCulture) + "/" + record.Offset.ToString(CultureInfo.InvariantCulture);
        return string.Equals(record.Topic, other.Topic, StringComparison.Ordinal) ? place : record.Topic + " " + place;
    }

    /// <summary>Compares the headers, or the attributes, of two records by name and occurrence.</summary>
    /// <param name="left">The first record's fields.</param>
    /// <param name="right">The second record's fields.</param>
    /// <param name="leftLabel">The first record's label.</param>
    /// <param name="rightLabel">The second record's label.</param>
    public static IReadOnlyList<KafkaFieldComparison> Fields(
        IReadOnlyList<KafkaMatchHeader> left,
        IReadOnlyList<KafkaMatchHeader> right,
        string leftLabel,
        string rightLabel)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        List<(string Name, KafkaMatchField? Left, KafkaMatchField? Right)> rows = [];
        Dictionary<string, int> index = new(StringComparer.Ordinal);

        foreach ((string key, KafkaMatchField value) in Occurrences(left))
        {
            index[key] = rows.Count;
            rows.Add((key, value, null));
        }

        foreach ((string key, KafkaMatchField value) in Occurrences(right))
        {
            if (index.TryGetValue(key, out int at))
            {
                rows[at] = (rows[at].Name, rows[at].Left, value);
            }
            else
            {
                rows.Add((key, null, value));
            }
        }

        List<KafkaFieldComparison> result = [];

        foreach ((string name, KafkaMatchField? a, KafkaMatchField? b) in rows)
        {
            string shownName = name.Replace("\u0001", " #", StringComparison.Ordinal);
            string status;

            if (a is null)
            {
                status = $"only in {rightLabel}";
            }
            else if (b is null)
            {
                status = $"only in {leftLabel}";
            }
            else if (a.Value.IsRedacted || b.Value.IsRedacted)
            {
                // Never "same" or "changed": a side that is masked says nothing about the other.
                status = $"masked (redacted by {a.Value.RedactedBy ?? b.Value.RedactedBy})";
            }
            else if (a.Value.IsNull != b.Value.IsNull || !string.Equals(a.Value.Text, b.Value.Text, StringComparison.Ordinal) || a.Value.ByteLength != b.Value.ByteLength)
            {
                status = "changed";
            }
            else
            {
                status = a.Value.Truncated || b.Value.Truncated ? "not compared (cut)" : "same";
            }

            result.Add(new KafkaFieldComparison(shownName, status, a?.Display, b?.Display));
        }

        return result;
    }

    private static IEnumerable<(string Key, KafkaMatchField Value)> Occurrences(IReadOnlyList<KafkaMatchHeader> fields)
    {
        Dictionary<string, int> seen = new(StringComparer.Ordinal);

        foreach (KafkaMatchHeader field in fields)
        {
            int n = seen.TryGetValue(field.Name, out int before) ? before + 1 : 1;
            seen[field.Name] = n;

            // The key carries the occurrence; \u0001 is turned into " #n" for display, after the keys have been matched.
            yield return (n == 1 ? field.Name : field.Name + "\u0001" + n.ToString(CultureInfo.InvariantCulture), field.Value);
        }
    }

    /// <summary>The value section.</summary>
    /// <param name="left">The first record.</param>
    /// <param name="right">The second record.</param>
    /// <param name="leftAccess">How the first record's value reads to this visitor.</param>
    /// <param name="rightAccess">How the second record's value reads to this visitor.</param>
    public static KafkaValueComparison Value(KafkaMatch left, KafkaMatch right, KafkaValueAccess leftAccess, KafkaValueAccess rightAccess)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        string a = Label(left, right);
        string b = Label(right, left);

        // The visitor's own access: a value that is not theirs reads as it does everywhere else, and nothing is compared.
        List<string> unavailable = [];

        foreach ((string label, KafkaValueAccess access) in new[] { (a, leftAccess), (b, rightAccess) })
        {
            string? sentence = access switch
            {
                KafkaValueAccess.CapabilityOff => "Reading values is not enabled.",
                KafkaValueAccess.PolicyRefused => KafkaStudioText.ValueNotAvailable,
                KafkaValueAccess.SearchWithoutValues => KafkaStudioText.ValueNotReadBySearch,
                _ => null,
            };

            if (sentence is not null)
            {
                unavailable.Add($"{label}: {sentence}");
            }
        }

        if (unavailable.Count > 0)
        {
            return new KafkaValueComparison(KafkaValueComparisonKind.NotShown, unavailable, []);
        }

        foreach ((KafkaMatch record, string label) in new[] { (left, a), (right, b) })
        {
            if (record.ValueWithheldBy is { } rule)
            {
                return Sentence($"Not compared: the value of {label} is withheld by rule {rule}.");
            }

            if (record.ValueKind == KafkaValueKind.Undecodable)
            {
                string why = record.UndecodableReason is { } reason ? KafkaStudioText.UndecodableReason(reason) : "the reason is not known";
                return Sentence($"Not compared: the value of {label} could not be decoded: {why}.");
            }
        }

        if (left.ValueKind != right.ValueKind && !(IsText(left) && IsText(right)))
        {
            return Sentence($"The values are of different kinds: {Kind(left)} and {Kind(right)}.");
        }

        if (left.ValueKind is KafkaValueKind.Tombstone or KafkaValueKind.Empty)
        {
            return Sentence("The values are the same.");
        }

        foreach ((KafkaMatch record, string label) in new[] { (left, a), (right, b) })
        {
            if (record.ValueTruncated)
            {
                string size = KafkaDisplay.Bytes(record.ValueText is { } text ? KafkaMatchView.Utf8Length(text) : record.ValuePreview?.Length / 2 ?? 0);
                return Sentence($"The value of {label} was cut at {size}, so the values are not compared. Open each record to compare them whole.");
            }
        }

        List<string> masked = [.. left.MaskedPaths.Concat(right.MaskedPaths).Select(static x => x.Text).Distinct(StringComparer.Ordinal)];
        int omitted = left.MaskedPathsOmitted + right.MaskedPathsOmitted;
        List<string> notes = [];

        if (masked.Count > 0 || omitted > 0)
        {
            string listed = string.Join(", ", masked) + (omitted > 0 ? (masked.Count > 0 ? " and " : string.Empty) + $"{KafkaDisplay.Number(omitted)} more" : string.Empty);
            notes.Add($"Not compared, masked: {listed}");
        }

        foreach (string pattern in left.ValueMaskedBy.Concat(right.ValueMaskedBy).Where(static x => x.StartsWith("pattern ", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            notes.Add($"Text matching {pattern} is masked on both sides: the values may differ there.");
        }

        if (left.ValueTextKind == KafkaValueTextKind.Json && right.ValueTextKind == KafkaValueTextKind.Json
            && left.ValueText is { } leftJson && right.ValueText is { } rightJson)
        {
            (string? leftKept, string? rightKept) = Without(leftJson, rightJson, [.. left.MaskedPaths, .. right.MaskedPaths]);

            if (leftKept is null || rightKept is null)
            {
                return new KafkaValueComparison(KafkaValueComparisonKind.Sentence, ["The values could not be compared as JSON.", .. notes], masked);
            }

            return new KafkaValueComparison(KafkaValueComparisonKind.Json, notes, masked, LeftJson: leftKept, RightJson: rightKept);
        }

        if (left.ValueText is { } leftText && right.ValueText is { } rightText)
        {
            bool same = string.Equals(leftText, rightText, StringComparison.Ordinal);

            return new KafkaValueComparison(
                KafkaValueComparisonKind.Text,
                [same ? "The values are the same." : "The values differ.", .. notes],
                masked,
                LeftText: Cut(leftText),
                RightText: Cut(rightText));
        }

        if (left.ValuePreview is { } leftHex && right.ValuePreview is { } rightHex)
        {
            return Sentence(string.Equals(leftHex, rightHex, StringComparison.Ordinal) ? "The values are the same." : "The values differ.");
        }

        return Sentence("The values are of different kinds: " + Kind(left) + " and " + Kind(right) + ".");
    }

    private static KafkaValueComparison Sentence(string text) => new(KafkaValueComparisonKind.Sentence, [text], []);

    private static bool IsText(KafkaMatch record) => record.ValueKind is KafkaValueKind.Json or KafkaValueKind.Text && record.ValueText is not null && record.ValueTextKind == KafkaValueTextKind.Text;

    private static string Cut(string text) => text.Length <= TextCut ? text : text[..TextCut];

    private static string Kind(KafkaMatch record) => record.ValueKind switch
    {
        KafkaValueKind.Tombstone => "a tombstone",
        KafkaValueKind.Empty => "an empty value",
        KafkaValueKind.Json => "JSON",
        KafkaValueKind.Text => "text",
        KafkaValueKind.Binary => "binary",
        KafkaValueKind.ConfluentWireFormat => "Confluent wire format",
        _ => record.ValueKind.ToString(),
    };

    /// <summary>
    /// Both documents with every masked node taken out: an object member is removed from both, and an array element is set to a constant in both, so that
    /// the differ reports nothing at or under a masked path (both sides read <c>••• (redacted by …)</c> there, which it would call the same).
    /// </summary>
    private static (string? Left, string? Right) Without(string left, string right, IReadOnlyList<KafkaMaskedPath> masked)
    {
        try
        {
            JsonNode? a = JsonNode.Parse(left);
            JsonNode? b = JsonNode.Parse(right);

            foreach (KafkaMaskedPath path in masked.OrderByDescending(static x => x.Steps.Count))
            {
                Mask(a, path.Steps);
                Mask(b, path.Steps);
            }

            return (a?.ToJsonString(), b?.ToJsonString());
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static void Mask(JsonNode? root, IReadOnlyList<JsonPathStep> steps)
    {
        JsonNode? current = root;

        for (int i = 0; i < steps.Count && current is not null; i++)
        {
            bool last = i == steps.Count - 1;

            switch (steps[i])
            {
                case JsonMemberStep member when current is JsonObject obj:
                    string? name = obj.Select(static x => x.Key).FirstOrDefault(key => string.Equals(key, member.Name, StringComparison.OrdinalIgnoreCase));

                    if (name is null)
                    {
                        return;
                    }

                    if (last)
                    {
                        obj.Remove(name);
                        return;
                    }

                    current = obj[name];
                    break;
                case JsonIndexStep index when current is JsonArray array:
                    if (index.Index < 0 || index.Index >= array.Count)
                    {
                        return;
                    }

                    if (last)
                    {
                        array[index.Index] = JsonValue.Create(Placeholder);
                        return;
                    }

                    current = array[index.Index];
                    break;
                default:
                    return;
            }
        }
    }
}
