namespace DataStudio.Kafka.Services;

/// <summary>The text of a date-and-time box (<c>&lt;input type="datetime-local"&gt;</c>) as the browser itself holds it.</summary>
/// <remarks>
/// A browser holds such a box's value to the minute (<c>2026-11-08T11:00</c>), and that is what the renderer records the box as holding
/// when its <c>change</c> arrives; the value the event hands the component has seconds added (<c>2026-11-08T11:00:00</c>). A component
/// that kept the longer text would be drawn from a value the box does not hold, so every render would send the box a value again, which
/// over a slow connection lands after the visitor's next keys (xvfv). The pages keep the browser's form, and parse that.
/// </remarks>
internal static class KafkaLocalInput
{
    /// <summary>The length of <c>yyyy-MM-ddTHH:mm</c>.</summary>
    private const int ToTheMinute = 16;

    /// <summary>
    /// <paramref name="value" /> to the minute when it is a date and time with seconds (or a fraction of them) after it, and
    /// <paramref name="value" /> itself otherwise: an empty box, a value already to the minute, and anything that is not a date and time are kept.
    /// </summary>
    /// <param name="value">What the box's <c>change</c> event carried.</param>
    public static string Minutes(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length > ToTheMinute && value[10] == 'T' && value[13] == ':' && value[ToTheMinute] == ':'
            ? value[..ToTheMinute]
            : value;
    }
}
