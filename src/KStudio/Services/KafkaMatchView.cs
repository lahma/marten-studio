using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;

namespace KStudio.Services;

/// <summary>One correlation chip of a result row (D-UI decision 15): a labelled value and the search a click writes into the box.</summary>
/// <param name="Name">The header or attribute name, or <c>trace</c>.</param>
/// <param name="Value">The whole value (the chip shows a cut of it; the whole is its title).</param>
/// <param name="Display">The chip's text: <c>{name} {value cut at 24 characters}</c>.</param>
/// <param name="Predicate">The predicate a click puts in the box, which does not run it.</param>
internal sealed record KafkaChip(string Name, string Value, string Display, string Predicate);

/// <summary>
/// What the result rows and the record detail derive from a <see cref="KafkaMatch" /> (D-UI decisions 13, 15 and 16). Everything here reads only the
/// match's already-redacted fields, so it inherits D40: a field that is masked, null, cut, or not text never becomes a chip or a copied predicate.
/// </summary>
internal static partial class KafkaMatchView
{
    /// <summary>The headers Wolverine writes, confirmed at <c>V6.61.0</c> in <c>EnvelopeConstants</c>; compared ignoring case.</summary>
    public static readonly IReadOnlyList<string> CorrelationHeaders = ["correlation-id", "conversation-id", "causation-id", "tenant-id", "saga-id"];

    /// <summary>The headers the engine reads a W3C trace context from (<c>HeaderDecoder.FindTrace</c>), compared ignoring case.</summary>
    private static readonly string[] TraceHeaders = ["traceparent", "ce_traceparent", "parent-id"];

    /// <summary>The CloudEvents attributes that become chips.</summary>
    public static readonly IReadOnlyList<string> CorrelationAttributes = ["subject", "id"];

    /// <summary>How much of a chip's value shows.</summary>
    public const int ChipValueCut = 24;

    /// <summary>How much of a key the results table shows.</summary>
    public const int KeyCut = 80;

    /// <summary>Whether a literal this long is read by the parser (<see cref="PredicateParser.MaxLiteralLength" />).</summary>
    /// <param name="text">The literal.</param>
    public static bool FitsLiteral(string text) => text.Length <= PredicateParser.MaxLiteralLength;

    /// <summary>The correlation chips of a match, in the order of D-UI decision 15.</summary>
    /// <param name="match">The match.</param>
    public static IReadOnlyList<KafkaChip> Chips(KafkaMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        List<KafkaChip> chips = [];

        foreach (string wanted in CorrelationHeaders)
        {
            KafkaMatchHeader? header = match.Headers.FirstOrDefault(x => string.Equals(x.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (header is not null && header.Value.IsLiteral && FitsLiteral(header.Value.Text!) && PredicateName(header.Name))
            {
                chips.Add(Chip(header.Name, header.Value.Text!, "header." + header.Name));
            }
        }

        foreach (string wanted in CorrelationAttributes)
        {
            KafkaMatchHeader? attribute = match.CloudEventAttributes.FirstOrDefault(x => string.Equals(x.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (attribute is not null && attribute.Value.IsLiteral && FitsLiteral(attribute.Value.Text!))
            {
                chips.Add(Chip(attribute.Name, attribute.Value.Text!, "ce." + attribute.Name));
            }
        }

        // The trace chip names the header the trace came from - traceparent, ce_traceparent or Wolverine's parent-id - and only one that was kept, unmasked
        // and in full: a chip for a header that is not there would be a search that finds nothing.
        if (match.Trace is { } trace)
        {
            string prefix = "00-" + trace.TraceId;
            KafkaMatchHeader? source = match.Headers.FirstOrDefault(x =>
                TraceHeaders.Contains(x.Name, StringComparer.OrdinalIgnoreCase)
                && x.Value.IsLiteral
                && x.Value.Text!.StartsWith(prefix, StringComparison.Ordinal)
                && PredicateName(x.Name));

            if (source is not null)
            {
                string value = trace.TraceId;
                chips.Add(new KafkaChip("trace", value, "trace " + Cut(value, 6), "header." + source.Name + " ^= " + PredicateFormatter.QuoteText(prefix)));
            }
        }

        return chips;
    }

    /// <summary>The predicate that finds this key, or <see langword="null" /> when copying it would copy a different value (null, masked, cut, not text, too long).</summary>
    /// <param name="match">The match.</param>
    public static string? KeyPredicate(KafkaMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return match.Key.IsLiteral && FitsLiteral(match.Key.Text!) ? "key = " + PredicateFormatter.QuoteText(match.Key.Text!) : null;
    }

    /// <summary>The text a mask writes in place of a part of a value; a literal containing it is never a faithful copy.</summary>
    public const string MaskText = KafkaMatchMasks.MaskPrefix;

    /// <summary>Whether the match holds a value that can be shown or copied: the payload grant read it, and no rule withheld it.</summary>
    /// <param name="match">The match.</param>
    public static bool HasShowableValue(KafkaMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return match.ValueWithheldBy is null && (match.ValueText is not null || match.ValuePreview is not null);
    }

    /// <summary>
    /// The predicate that finds this value, <c>value = '…'</c>, for a <b>text</b> value only: an exact match on a whole JSON text depends on its spacing.
    /// <see langword="null" /> when the value is JSON, cut, withheld, masked, empty, or longer than a search literal.
    /// </summary>
    /// <param name="match">The match.</param>
    public static string? ValuePredicate(KafkaMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        if (match.ValueKind != KafkaValueKind.Text
            || match.ValueTextKind != KafkaValueTextKind.Text
            || match.ValueWithheldBy is not null
            || match.ValueTruncated
            || match.ValueMaskedBy.Count > 0
            || match.ValueText is not { Length: > 0 } text
            || !FitsLiteral(text)
            || text.Contains(MaskText, StringComparison.Ordinal))
        {
            return null;
        }

        return "value = " + PredicateFormatter.QuoteText(text);
    }

    /// <summary>
    /// The predicate that finds a JSON leaf of the value shown, <c>$.a.b = 'x'</c>: <see langword="null" /> unless the node is a scalar the grammar can write,
    /// its path is one the grammar can write, the value is whole and the node is not at or under a masked path. A CloudEvent's value is its data, so its
    /// paths are data paths, which is what <c>$</c> addresses.
    /// </summary>
    /// <param name="match">The match.</param>
    /// <param name="segments">The node's path: a member name, or <see langword="null" /> and an index.</param>
    /// <param name="valueJson">The node's value as JSON.</param>
    public static string? LeafPredicate(KafkaMatch match, IReadOnlyList<(string? Key, int Index)> segments, string valueJson)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(segments);

        if (match.ValueTextKind != KafkaValueTextKind.Json || match.ValueWithheldBy is not null || match.ValueTruncated)
        {
            return null;
        }

        if (segments.Count > JsonPathExpression.MaxSteps || KafkaMatchMasks.IsMasked(match, segments))
        {
            return null;
        }

        List<JsonPathStep> steps = [];

        foreach ((string? key, int index) in segments)
        {
            if (key is null)
            {
                steps.Add(new JsonIndexStep(index));
            }
            else if (key.Length is > 0 and <= JsonPathExpression.MaxMemberLength)
            {
                steps.Add(new JsonMemberStep(key));
            }
            else
            {
                return null;
            }
        }

        string? literal = Literal(valueJson);
        return literal is null ? null : JsonPathExpression.Format(steps) + " = " + literal;
    }

    private static string? Literal(string valueJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(valueJson);
            JsonElement node = document.RootElement;

            switch (node.ValueKind)
            {
                case JsonValueKind.String:
                    string? text = node.GetString();
                    return text is not null && FitsLiteral(text) && !text.Contains(MaskText, StringComparison.Ordinal) ? PredicateFormatter.QuoteText(text) : null;
                case JsonValueKind.Number:
                    string raw = node.GetRawText();
                    return IsNumberLiteral(raw) && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? raw : null;
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                case JsonValueKind.Null:
                    return "null";
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The most bytes of a binary value the page draws as hex.</summary>
    public const int MaxHexBytes = 64 * 1024;

    /// <summary>The most characters of a text value the page puts in its markup.</summary>
    public const int MaxTextChars = 256 * 1024;

    /// <summary>
    /// A hex dump of the engine's preview (<c>0x</c> and lower-case hex): sixteen bytes a line, the offset, the bytes and the printable ASCII, at most
    /// <see cref="MaxHexBytes" /> bytes. <see cref="string.Empty" /> for a preview that is not hex.
    /// </summary>
    /// <param name="preview">The match's <see cref="KafkaMatch.ValuePreview" />.</param>
    /// <param name="shown">How many bytes were drawn.</param>
    public static string HexDump(string preview, out int shown)
    {
        ArgumentNullException.ThrowIfNull(preview);

        string hex = preview.StartsWith("0x", StringComparison.Ordinal) ? preview[2..] : preview;
        int bytes = Math.Min(hex.Length / 2, MaxHexBytes);
        shown = 0;

        if (bytes == 0 || !hex.All(static c => char.IsAsciiHexDigit(c)))
        {
            return string.Empty;
        }

        StringBuilder dump = new();

        for (int line = 0; line < bytes; line += 16)
        {
            int count = Math.Min(16, bytes - line);
            dump.Append(line.ToString("x4", CultureInfo.InvariantCulture)).Append("  ");
            StringBuilder ascii = new();

            for (int i = 0; i < 16; i++)
            {
                if (i < count)
                {
                    byte value = Convert.ToByte(hex.Substring((line + i) * 2, 2), 16);
                    dump.Append(value.ToString("x2", CultureInfo.InvariantCulture)).Append(' ');
                    ascii.Append(value is >= 0x20 and < 0x7f ? (char) value : '.');
                }
                else
                {
                    dump.Append("   ");
                }
            }

            dump.Append(' ').Append(ascii).Append('\n');
        }

        shown = bytes;
        return dump.ToString();
    }

    /// <summary>The value's text as it is shown, UTF-8 bytes of it; used for "Shown N of M".</summary>
    /// <param name="text">The text.</param>
    public static int Utf8Length(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>Whether <paramref name="raw" /> is a number as the search grammar lexes one.</summary>
    private static bool IsNumberLiteral(string raw) => NumberShape().IsMatch(raw);

    [GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberShape();

    /// <summary>Whether the key is a text that is only too long for a search literal: the page says why it does not offer the predicate.</summary>
    /// <param name="match">The match.</param>
    public static bool KeyTooLongForPredicate(KafkaMatch match) => match.Key.IsLiteral && !FitsLiteral(match.Key.Text!);

    /// <summary>
    /// Whether the key, or a header or attribute a chip would be made of, is a faithful text that is only too long for a search literal: the detail says
    /// why it offers no predicate for it, instead of leaving the item out in silence.
    /// </summary>
    /// <param name="match">The match.</param>
    public static bool HasLiteralTooLong(KafkaMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return KeyTooLongForPredicate(match)
            || match.Headers.Where(x => CorrelationHeaders.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).Any(static x => x.Value.IsLiteral && !FitsLiteral(x.Value.Text!))
            || match.CloudEventAttributes.Where(x => CorrelationAttributes.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).Any(static x => x.Value.IsLiteral && !FitsLiteral(x.Value.Text!));
    }

    /// <summary>The results table's key cell text: <see cref="KafkaMatchField.Display" />, cut at <see cref="KeyCut" /> characters.</summary>
    /// <param name="field">The key.</param>
    public static string KeyDisplay(KafkaMatchField field) => field.IsRedacted || field.IsNull ? field.Display : Cut(field.Text ?? string.Empty, KeyCut);

    /// <summary>The Type column: the CloudEvents <c>type</c> when present and unmasked, else Wolverine's <c>message-type</c> header when present and unmasked, else the value's format.</summary>
    /// <param name="match">The match.</param>
    public static string TypeOf(KafkaMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        KafkaMatchHeader? type = match.CloudEventAttributes.FirstOrDefault(static x => string.Equals(x.Name, "type", StringComparison.OrdinalIgnoreCase));
        if (type is { Value: { IsRedacted: false, IsNull: false, Text: { } cloud } })
        {
            return cloud;
        }

        KafkaMatchHeader? message = match.Headers.FirstOrDefault(static x => string.Equals(x.Name, "message-type", StringComparison.OrdinalIgnoreCase));
        if (message is { Value: { IsRedacted: false, IsNull: false, Text: { } wolverine } })
        {
            return wolverine;
        }

        return match.ValueFormat;
    }

    /// <summary>
    /// The kcat command that reads this record, with no credential in it. <c>bootstrap.servers</c> is the settings reader's row and only when its
    /// exposure is <c>Shown</c>; otherwise it is <c>'$BOOTSTRAP'</c>. <c>security.protocol</c> and <c>sasl.mechanism</c> follow when shown. Every value is
    /// in single quotes, with <c>'</c> written as <c>'\''</c>.
    /// </summary>
    /// <param name="topic">The topic.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="offset">The offset.</param>
    /// <param name="settings">The cluster's settings as the reader shows them, or <see langword="null" /> when they are not known.</param>
    public static string Kcat(string topic, int partition, long offset, KafkaClusterSettingsView? settings)
    {
        string Shown(string key) =>
            settings?.Host.FirstOrDefault(x => x.Key == key && x.Exposure == KafkaSettingExposure.Shown) is { Shown.Length: > 0 } row ? row.Shown : string.Empty;

        string bootstrap = Shown("bootstrap.servers");
        StringBuilder command = new("kcat -C -b ");
        command.Append(bootstrap.Length == 0 ? KafkaStudioText.KcatBootstrapVariable : Quote(bootstrap));
        command.Append(" -t ").Append(Quote(topic));
        command.Append(CultureInfo.InvariantCulture, $" -p {partition} -o {offset} -c 1 -J");

        foreach (string key in new[] { "security.protocol", "sasl.mechanism" })
        {
            if (Shown(key) is { Length: > 0 } value)
            {
                command.Append(" -X ").Append(key).Append('=').Append(Quote(value));
            }
        }

        return command.Append('\n').Append(KafkaStudioText.KcatCredentialsHint).ToString();
    }

    /// <summary>A shell word in single quotes, <c>'</c> written <c>'\''</c>.</summary>
    /// <param name="value">The value.</param>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>A value cut at <paramref name="length" /> characters (never inside a surrogate pair), with an ellipsis when cut.</summary>
    /// <param name="value">The value.</param>
    /// <param name="length">The most characters kept.</param>
    public static string Cut(string value, int length)
    {
        if (value.Length <= length)
        {
            return value;
        }

        int end = char.IsHighSurrogate(value[length - 1]) ? length - 1 : length;
        return value[..end] + "…";
    }

    private static KafkaChip Chip(string name, string value, string field) =>
        new(name, value, name + " " + Cut(value, ChipValueCut), field + " = " + PredicateFormatter.QuoteText(value));

    /// <summary>Whether a header name is one the grammar's <c>header.&lt;name&gt;</c> can address.</summary>
    private static bool PredicateName(string name) => name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
