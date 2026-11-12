using System.Globalization;
using System.Text.Json;

using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;

namespace KStudio.Core.Correlation;

/// <summary>
/// Reads an anchor's correlation values (D42, decision 1.4 of D-CORR), pure: it reads only the anchor's already-redacted fields, and a
/// field is a key only if it is a literal the visitor sees whole. A masked field is never a correlation key and never shows (D40).
/// </summary>
internal static class CorrelationExtractor
{
    /// <summary>The most values of one kind.</summary>
    public const int MaxPerKind = 10;

    /// <summary>The most values in all.</summary>
    public const int MaxValues = 24;

    /// <summary>The most occurrences of one header read.</summary>
    public const int MaxHeaderOccurrences = 4;

    private static readonly string[] TraceHeaders = ["traceparent", "ce_traceparent", "parent-id"];

    /// <summary>Extracts the values of <paramref name="fields" /> from <paramref name="anchor" />.</summary>
    /// <param name="anchor">The record the visitor is looking at.</param>
    /// <param name="fields">The correlation fields of the anchor's topic.</param>
    public static CorrelationKeySet Extract(KafkaMatch anchor, IReadOnlyList<CorrelationField> fields)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(fields);

        List<CorrelationValue> found = [];
        bool wantsTrace = false;

        foreach (CorrelationField field in fields)
        {
            if (field.IsTrace)
            {
                wantsTrace = true;
                continue;
            }

            switch (field.Source)
            {
                case CorrelationSource.Header:
                    ReadHeader(anchor, field, found);
                    break;
                case CorrelationSource.CloudEvent:
                    ReadAttribute(anchor, field, found);
                    break;
                case CorrelationSource.Key:
                    Add(found, field, anchor.Key.Text, anchor.Key.IsLiteral, CorrelationValueForm.Text);
                    break;
                default:
                    ReadPath(anchor, field, found);
                    break;
            }
        }

        if (wantsTrace && TraceId(anchor) is { } trace)
        {
            found.Add(new CorrelationValue(CorrelationField.TraceKind, trace, CorrelationValueForm.Text, "trace"));
        }

        return Bounded(found);
    }

    /// <summary>
    /// The trace id of the anchor, only when the header it came from was kept, unmasked and whole (the chip rule of D-UI decision 15): a
    /// search for a trace the anchor does not show would find a record the visitor could not have seen the link of.
    /// </summary>
    private static string? TraceId(KafkaMatch anchor)
    {
        if (anchor.Trace is not { } trace)
        {
            return null;
        }

        string prefix = "00-" + trace.TraceId;

        foreach (KafkaMatchHeader header in anchor.Headers)
        {
            if (TraceHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase)
                && header.Value.IsLiteral
                && header.Value.Text!.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trace.TraceId;
            }
        }

        return null;
    }

    private static void ReadHeader(KafkaMatch anchor, CorrelationField field, List<CorrelationValue> found)
    {
        int read = 0;

        foreach (KafkaMatchHeader header in anchor.Headers)
        {
            if (!string.Equals(header.Name, field.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (++read > MaxHeaderOccurrences)
            {
                break;
            }

            Add(found, field, header.Value.Text, header.Value.IsLiteral, CorrelationValueForm.Text);
        }
    }

    private static void ReadAttribute(KafkaMatch anchor, CorrelationField field, List<CorrelationValue> found)
    {
        foreach (KafkaMatchHeader attribute in anchor.CloudEventAttributes)
        {
            if (string.Equals(attribute.Name, field.Name, StringComparison.OrdinalIgnoreCase))
            {
                Add(found, field, attribute.Value.Text, attribute.Value.IsLiteral, CorrelationValueForm.Text);
                return;
            }
        }
    }

    private static void ReadPath(KafkaMatch anchor, CorrelationField field, List<CorrelationValue> found)
    {
        // A value is read only if this match was made under the payload grant, is JSON, and shows its whole text with nothing masked that the
        // match cannot name: with some masked paths left out, no node can be proved unmasked.
        if (field.Path is not { } path
            || !anchor.ReadWithPayload
            || anchor.ValueText is not { } text
            || anchor.ValueTextKind != KafkaValueTextKind.Json
            || anchor.ValueWithheldBy is not null
            || anchor.ValueTruncated
            || anchor.MaskedPathsOmitted > 0)
        {
            return;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            List<(string? Key, int Index)> segments = [];
            Walk(anchor, path.Steps, 0, document.RootElement, segments, field, found);
        }
    }

    private static void Walk(
        KafkaMatch anchor,
        IReadOnlyList<JsonPathStep> steps,
        int index,
        JsonElement node,
        List<(string? Key, int Index)> segments,
        CorrelationField field,
        List<CorrelationValue> found)
    {
        if (index == steps.Count)
        {
            if (!KafkaMatchMasks.IsMasked(anchor, segments))
            {
                AddScalar(found, field, node);
            }

            return;
        }

        switch (steps[index])
        {
            case JsonMemberStep member when node.ValueKind == JsonValueKind.Object:
                // A search compares member names exactly.
                foreach (JsonProperty property in node.EnumerateObject())
                {
                    if (string.Equals(property.Name, member.Name, StringComparison.Ordinal))
                    {
                        segments.Add((property.Name, 0));
                        Walk(anchor, steps, index + 1, property.Value, segments, field, found);
                        segments.RemoveAt(segments.Count - 1);
                    }
                }

                break;

            case JsonIndexStep position when node.ValueKind == JsonValueKind.Array && position.Index < node.GetArrayLength():
                segments.Add((null, position.Index));
                Walk(anchor, steps, index + 1, node[position.Index], segments, field, found);
                segments.RemoveAt(segments.Count - 1);
                break;

            case JsonWildcardStep when node.ValueKind == JsonValueKind.Array:
                int element = 0;

                foreach (JsonElement child in node.EnumerateArray())
                {
                    segments.Add((null, element));
                    Walk(anchor, steps, index + 1, child, segments, field, found);
                    segments.RemoveAt(segments.Count - 1);
                    element++;
                }

                break;
        }
    }

    private static void AddScalar(List<CorrelationValue> found, CorrelationField field, JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                Add(found, field, node.GetString(), true, CorrelationValueForm.Text);
                break;
            case JsonValueKind.Number:
                Add(found, field, node.GetRawText(), true, CorrelationValueForm.Number);
                break;
            case JsonValueKind.True:
                Add(found, field, "true", true, CorrelationValueForm.Bool);
                break;
            case JsonValueKind.False:
                Add(found, field, "false", true, CorrelationValueForm.Bool);
                break;
        }
    }

    private static void Add(List<CorrelationValue> found, CorrelationField field, string? text, bool literal, CorrelationValueForm form)
    {
        // A search literal is at most 1,000 characters, and a text that holds a mask is not a faithful copy of anything.
        if (!literal || text is null || text.Length == 0 || text.Length > PredicateParser.MaxLiteralLength || text.Contains(KafkaMatchMasks.MaskPrefix, StringComparison.Ordinal))
        {
            return;
        }

        found.Add(new CorrelationValue(field.Kind, text, form, field.Grammar));
    }

    /// <summary>Drops repeats, then the values past 10 of a kind and past 24 in all, in field order, and says how many.</summary>
    private static CorrelationKeySet Bounded(List<CorrelationValue> found)
    {
        List<CorrelationValue> kept = [];
        Dictionary<string, int> perKind = new(StringComparer.Ordinal);
        Dictionary<string, int> droppedPerKind = new(StringComparer.Ordinal);
        int droppedTotal = 0;

        foreach (CorrelationValue value in found)
        {
            if (kept.Any(x => x.Kind == value.Kind && x.Value == value.Value))
            {
                continue;
            }

            perKind.TryGetValue(value.Kind, out int count);

            if (count >= MaxPerKind)
            {
                droppedPerKind[value.Kind] = droppedPerKind.GetValueOrDefault(value.Kind) + 1;
                continue;
            }

            if (kept.Count >= MaxValues)
            {
                droppedTotal++;
                continue;
            }

            perKind[value.Kind] = count + 1;
            kept.Add(value);
        }

        List<string> notes = [];

        foreach ((string kind, int dropped) in droppedPerKind)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{dropped} more `{kind}` {(dropped == 1 ? "value was" : "values were")} not used."));
        }

        if (droppedTotal > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{droppedTotal} more {(droppedTotal == 1 ? "value was" : "values were")} not used."));
        }

        return new CorrelationKeySet(kept, notes);
    }
}
