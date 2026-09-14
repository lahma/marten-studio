using System.Globalization;

namespace MartenStudio.Services.Schema;

/// <summary>
/// The number formatting the Schema screen's tables share.
/// </summary>
/// <remarks>
/// Binary units (KiB, MiB) rather than decimal ones, because that is what <c>pg_size_pretty</c> and
/// every other Postgres tool use, and a size that disagreed with <c>\dt+</c> by 7% would be the sort of
/// difference somebody spends an afternoon on.
/// </remarks>
internal static class SchemaFormat
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary><paramref name="bytes" /> as a size a person reads.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "n/a";
        }

        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string formatted = unit == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);

        return formatted + " " + Units[unit];
    }

    /// <summary>A counter the statistics collector may have nothing for.</summary>
    public static string Count(long? value) =>
        value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>
    /// A <c>reltuples</c> estimate, prefixed with <c>~</c> (D8) and reported as unknown when Postgres
    /// has never analyzed the table.
    /// </summary>
    public static string Estimate(long value) =>
        value < 0 ? "not analyzed" : "~" + value.ToString("N0", CultureInfo.InvariantCulture);
}
