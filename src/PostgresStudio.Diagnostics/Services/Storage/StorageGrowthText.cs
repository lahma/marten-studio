using System.Globalization;

using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Storage;

/// <summary>The words for a growth the statistics window cannot yet estimate, said alike by the Storage page and the diagnostics API.</summary>
internal static class StorageGrowthText
{
    /// <summary>What the growth tiles say while there is no growth: why, and how long there is to go.</summary>
    public static string Pending(StatisticsWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.LooksReset)
        {
            return "the statistics look reset since the last check (" + (window.Signal ?? window.ReasonText)
                + "): growth is estimated again once they have counted for a day";
        }

        return "needs a day of statistics: " + HoursSoFar(window.Length) + " so far";
    }

    /// <summary>"under an hour", "1 h", "17 h".</summary>
    private static string HoursSoFar(TimeSpan length) => length < TimeSpan.FromHours(1)
        ? "under an hour"
        : ((int) length.TotalHours).ToString(CultureInfo.InvariantCulture) + " h";
}
