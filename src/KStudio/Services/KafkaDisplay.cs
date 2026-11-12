using System.Globalization;

namespace KStudio.Services;

/// <summary>
/// How the Kafka pages write numbers: one place, so that the home, the topic page and the pages after them say a span, a retention and a size the
/// same way. Culture-invariant: the studio's pages are English, and a table of offsets must read the same on every host.
/// </summary>
internal static class KafkaDisplay
{
    /// <summary>The sentence of a column's header that holds an offset span: it is never a count.</summary>
    public const string OffsetSpanTitle = "An offset span, not a count: compaction and transaction markers leave gaps";

    /// <summary>An offset span, shown with <c>~</c> (D8): <c>~1,234,567</c>.</summary>
    /// <param name="span">The span.</param>
    public static string Span(long span) => "~" + Number(span);

    /// <summary>
    /// A count with its noun in the right number: <c>1 match</c>, <c>2 matches</c>, <c>1,200 records</c>. Every sentence the Kafka pages build around a
    /// count says it through this, so that no page writes "1 records".
    /// </summary>
    /// <param name="value">The count.</param>
    /// <param name="singular">The noun for exactly one.</param>
    /// <param name="plural">The noun for any other count; the singular and an <c>s</c> when omitted.</param>
    public static string Count(long value, string singular, string? plural = null) =>
        Number(value) + " " + (value == 1 ? singular : plural ?? singular + "s");

    /// <summary>The verb form that agrees with a count: <c>was</c> for exactly one, <c>were</c> otherwise.</summary>
    /// <param name="value">The count.</param>
    /// <param name="singular">The form for exactly one.</param>
    /// <param name="plural">The form for any other count.</param>
    public static string Agree(long value, string singular, string plural) => value == 1 ? singular : plural;

    /// <summary>A number with thousands separators: <c>1,234,567</c>.</summary>
    /// <param name="value">The number.</param>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// A retention in milliseconds as the largest whole unit: <c>7 d</c>, <c>12 h</c>, <c>30 min</c>, <c>15 s</c>; <c>-1</c> is <c>unlimited</c>; anything
    /// that is not a whole second is in milliseconds.
    /// </summary>
    /// <param name="milliseconds">The retention, or -1.</param>
    public static string Retention(long milliseconds)
    {
        if (milliseconds < 0)
        {
            return "unlimited";
        }

        if (milliseconds == 0)
        {
            return "0 ms";
        }

        return milliseconds switch
        {
            _ when milliseconds % 86_400_000 == 0 => Number(milliseconds / 86_400_000) + " d",
            _ when milliseconds % 3_600_000 == 0 => Number(milliseconds / 3_600_000) + " h",
            _ when milliseconds % 60_000 == 0 => Number(milliseconds / 60_000) + " min",
            _ when milliseconds % 1_000 == 0 => Number(milliseconds / 1_000) + " s",
            _ => Number(milliseconds) + " ms",
        };
    }

    /// <summary>A size in bytes in binary units: <c>512 B</c>, <c>512 KiB</c>, <c>1.4 GiB</c>; a negative size is <c>unlimited</c>.</summary>
    /// <param name="bytes">The size, or -1.</param>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "unlimited";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? Number(bytes) + " B"
            : value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>The topic's key format as the home and the topic page show it: the declared name, or the assumption.</summary>
    /// <param name="format">The format, declared or the default.</param>
    /// <param name="declared">Whether the host declared it.</param>
    public static string KeyFormat(KafkaKeyFormat format, bool declared)
    {
        ArgumentNullException.ThrowIfNull(format);

        return declared ? format.Name : "UTF-8 text (assumed)";
    }
}
