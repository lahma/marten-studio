namespace PostgresStudio.Services.Live;

/// <summary>How the pages spell a moment of a since-mark: the visitor's selected time zone, and the date when it is not today (1.2-H-A-FIX).</summary>
internal static class SinceMarkTime
{
    /// <summary>
    /// <paramref name="at" /> in the selected zone as "14:02" (or "14:02:05" with <paramref name="seconds" />), preceded by its date
    /// ("2026-10-26 14:02") when that is not today's date in that zone.
    /// </summary>
    /// <param name="state">The circuit's state, which knows the selected zone.</param>
    /// <param name="at">The moment.</param>
    /// <param name="seconds">Whether to spell seconds.</param>
    /// <param name="now">The moment "today" is taken from; the clock when <see langword="null" />.</param>
    public static string When(StudioState state, DateTimeOffset at, bool seconds = false, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        string time = state.FormatInSelectedTimeZone(at, seconds ? "HH:mm:ss" : "HH:mm");
        bool today = state.ConvertToSelectedTimeZone(at).Date == state.ConvertToSelectedTimeZone(now ?? DateTimeOffset.UtcNow).Date;

        return today ? time : state.FormatInSelectedTimeZone(at, "yyyy-MM-dd") + " " + time;
    }
}
