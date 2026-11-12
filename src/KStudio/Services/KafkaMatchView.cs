using System.Globalization;
using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Predicates;
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
internal static class KafkaMatchView
{
    /// <summary>The headers Wolverine writes, confirmed at <c>V6.61.0</c> in <c>EnvelopeConstants</c>; compared ignoring case.</summary>
    public static readonly IReadOnlyList<string> CorrelationHeaders = ["correlation-id", "conversation-id", "tenant-id", "saga-id"];

    /// <summary>The headers the engine reads a W3C trace context from (<c>HeaderDecoder.FindTrace</c>), compared ignoring case.</summary>
    private static readonly string[] TraceHeaders = ["traceparent", "ce_traceparent", "parent-id"];

    /// <summary>The CloudEvents attributes that become chips.</summary>
    public static readonly IReadOnlyList<string> CorrelationAttributes = ["subject", "id"];

    /// <summary>How much of a chip's value shows.</summary>
    public const int ChipValueCut = 24;

    /// <summary>How much of a key the results table shows.</summary>
    public const int KeyCut = 80;

    /// <summary>
    /// Whether a field's text is a faithful rendering of its bytes as a search literal would compare them: not null, not masked, not cut, and text
    /// (a binary header is kept as hex, whose length is never its byte length).
    /// </summary>
    /// <param name="field">The field.</param>
    public static bool IsLiteral(KafkaMatchField field) =>
        !field.IsNull && !field.IsRedacted && !field.Truncated && field.Text is { } text && Encoding.UTF8.GetByteCount(text) == field.ByteLength;

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
            if (header is not null && IsLiteral(header.Value) && FitsLiteral(header.Value.Text!) && PredicateName(header.Name))
            {
                chips.Add(Chip(header.Name, header.Value.Text!, "header." + header.Name));
            }
        }

        foreach (string wanted in CorrelationAttributes)
        {
            KafkaMatchHeader? attribute = match.CloudEventAttributes.FirstOrDefault(x => string.Equals(x.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (attribute is not null && IsLiteral(attribute.Value) && FitsLiteral(attribute.Value.Text!))
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
                && IsLiteral(x.Value)
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

        return IsLiteral(match.Key) && FitsLiteral(match.Key.Text!) ? "key = " + PredicateFormatter.QuoteText(match.Key.Text!) : null;
    }

    /// <summary>Whether the key is a text that is only too long for a search literal: the page says why it does not offer the predicate.</summary>
    /// <param name="match">The match.</param>
    public static bool KeyTooLongForPredicate(KafkaMatch match) => IsLiteral(match.Key) && !FitsLiteral(match.Key.Text!);

    /// <summary>
    /// Whether the key, or a header or attribute a chip would be made of, is a faithful text that is only too long for a search literal: the detail says
    /// why it offers no predicate for it, instead of leaving the item out in silence.
    /// </summary>
    /// <param name="match">The match.</param>
    public static bool HasLiteralTooLong(KafkaMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return KeyTooLongForPredicate(match)
            || match.Headers.Where(x => CorrelationHeaders.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).Any(static x => IsLiteral(x.Value) && !FitsLiteral(x.Value.Text!))
            || match.CloudEventAttributes.Where(x => CorrelationAttributes.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).Any(static x => IsLiteral(x.Value) && !FitsLiteral(x.Value.Text!));
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
