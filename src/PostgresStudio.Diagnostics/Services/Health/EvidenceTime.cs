using System.Globalization;

namespace PostgresStudio.Services.Health;

/// <summary>
/// The one spelling of a moment in health evidence, check sentences and report text: <c>yyyy-MM-dd HH:mm:ss UTC</c>.
/// Evidence is formatted once per sweep, for every visitor, so it is always UTC; a page that shows a time in the
/// visitor's selected zone says so beside it and never uses this.
/// </summary>
internal static class EvidenceTime
{
    /// <summary>The format string, so a test can hold every site to it.</summary>
    internal const string Format = "yyyy-MM-dd HH:mm:ss 'UTC'";

    /// <summary>A moment, in UTC: <c>2026-11-04 08:57:03 UTC</c>.</summary>
    /// <param name="at">The moment.</param>
    public static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>A moment, in UTC, or <paramref name="never" /> without one.</summary>
    /// <param name="at">The moment, if there was one.</param>
    /// <param name="never">What to print without a moment.</param>
    public static string Utc(DateTimeOffset? at, string never = "never") => at is { } value ? Utc(value) : never;
}
