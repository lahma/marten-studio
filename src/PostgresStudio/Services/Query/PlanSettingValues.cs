using System.Globalization;
using System.Text.RegularExpressions;

using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Services.Query;

/// <summary>
/// 0.10-B (issue #28): the Plan page's check of a setting's value before it is sent, by the setting's type and range as
/// <c>pg_settings</c> gives them - a convenience for the visitor, never the boundary: the service refuses a name that is not on the
/// allow-list, and Postgres has the last word on a value, which it reads as a parameter. Pure.
/// </summary>
internal static partial class PlanSettingValues
{
    private static readonly string[] BooleanWords = ["on", "off", "true", "false", "yes", "no", "1", "0"];

    /// <summary>Why <paramref name="value" /> is not a value <paramref name="setting" /> takes, or <see langword="null" /> when it is.</summary>
    /// <param name="setting">What the server says of the setting.</param>
    /// <param name="value">What was typed.</param>
    public static string? Validate(PlanSettingRow setting, string? value)
    {
        ArgumentNullException.ThrowIfNull(setting);

        string text = value?.Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            return "Give it a value.";
        }

        if (text.Length > PlanSettingsQueries.MaxValueCharacters)
        {
            return "At most " + PlanSettingsQueries.MaxValueCharacters.ToString(CultureInfo.InvariantCulture) + " characters.";
        }

        switch (setting.VarType)
        {
            case "bool":
                return BooleanWords.Contains(text, StringComparer.OrdinalIgnoreCase) ? null : "on or off.";

            case "integer" when MemoryBytes(setting.Unit) is { } unitBytes:
            {
                Match match = Memory().Match(text);

                if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
                {
                    return "A size such as 64MB, or a number of " + setting.Unit + ".";
                }

                double bytes = number * SuffixBytes(match.Groups[2].Value, unitBytes);
                return Range(bytes / unitBytes, setting, " " + setting.Unit);
            }

            case "integer":
                return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole)
                    ? Range(whole, setting, setting.Unit is { } unit ? " " + unit : string.Empty)
                    : "A whole number.";

            case "real":
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) && double.IsFinite(real)
                    ? Range(real, setting, string.Empty)
                    : "A number.";

            default:
                return "The Plan page does not change this kind of setting.";
        }
    }

    /// <summary>
    /// <see cref="Validate" />, and then the ceiling the service holds the memory settings to - the page says it before anything is
    /// sent, as the service would after.
    /// </summary>
    public static string? ValidateWithCeiling(PlanSettingRow setting, string? value) =>
        Validate(setting, value) ?? (value is null ? null : PlanSettingsQueries.Ceiling(setting.Name, value)?.Message);

    /// <summary>What a person should type for <paramref name="setting" />, in a few words, for the box's hint.</summary>
    public static string Hint(PlanSettingRow setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        string range = setting.MinValue is { } min && setting.MaxValue is { } max && setting.VarType is "integer" or "real" && MemoryBytes(setting.Unit) is null
            ? ", " + min + " to " + max
            : string.Empty;

        return setting.VarType switch
        {
            "bool" => "on or off",
            "integer" when MemoryBytes(setting.Unit) is not null => "a size, as 64MB",
            "integer" => "a whole number" + range,
            "real" => "a number" + range,
            _ => string.Empty,
        };
    }

    private static string? Range(double value, PlanSettingRow setting, string unit)
    {
        if (double.TryParse(setting.MinValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double min) && value < min)
        {
            return "At least " + setting.MinValue + unit + ".";
        }

        if (double.TryParse(setting.MaxValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double max) && value > max)
        {
            return "At most " + setting.MaxValue + unit + ".";
        }

        return null;
    }

    /// <summary>The bytes in one of a memory setting's units, or <see langword="null" /> when the unit is not a size.</summary>
    private static long? MemoryBytes(string? unit) => unit switch
    {
        "B" => 1,
        "kB" => 1024,
        "8kB" => 8 * 1024,
        "MB" => 1024 * 1024,
        _ => null,
    };

    private static long SuffixBytes(string suffix, long defaultBytes) => suffix switch
    {
        "B" => 1,
        "kB" => 1024,
        "MB" => 1024L * 1024,
        "GB" => 1024L * 1024 * 1024,
        "TB" => 1024L * 1024 * 1024 * 1024,
        _ => defaultBytes,
    };

    [GeneratedRegex("^([0-9]{1,15})\\s*(B|kB|MB|GB|TB)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Memory();
}
