namespace PostgresStudio.Services.Live;

/// <summary>How the pages spell a moment of a since-mark: the visitor's selected time zone, and the date when it is not today (1.2-H-A-FIX).</summary>
internal static class SinceMarkTime
{
    /// <summary>
    /// <paramref name="at" /> in the selected zone as "14:02" (or "14:02:05" with <paramref name="seconds" />), preceded by its date
    /// ("2026-10-26 14:02") when that is not today's date in that zone.
    /// </summary>
    /// <param name="preferences">The circuit's preferences, which know the selected zone.</param>
    /// <param name="at">The moment.</param>
    /// <param name="seconds">Whether to spell seconds.</param>
    /// <param name="now">The moment "today" is taken from; the preferences' <see cref="StudioPreferences.Clock" /> when <see langword="null" />.</param>
    public static string When(StudioPreferences preferences, DateTimeOffset at, bool seconds = false, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        string time = preferences.FormatInSelectedTimeZone(at, seconds ? "HH:mm:ss" : "HH:mm");
        bool today = preferences.ConvertToSelectedTimeZone(at).Date == preferences.ConvertToSelectedTimeZone(now ?? preferences.Clock.GetUtcNow()).Date;

        return today ? time : preferences.FormatInSelectedTimeZone(at, "yyyy-MM-dd") + " " + time;
    }
}
