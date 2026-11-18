using System.Globalization;

namespace DataStudio.Services;

/// <summary>
/// How a time zone is named wherever the studio lists or labels one (#80): by its IANA id (<c>Europe/Helsinki</c>), never by the Windows id a Windows
/// host's <see cref="TimeZoneInfo" /> carries (<c>FLE Standard Time</c>), which reads as a display name it is not.
/// </summary>
/// <remarks>
/// A Windows id is converted with <see cref="TimeZoneInfo.TryConvertWindowsIdToIanaId(string, string?, out string?)" />, asking for the host's region first (a
/// Finnish host's <c>FLE Standard Time</c> is <c>Europe/Helsinki</c>, where the id alone is the list's default, <c>Europe/Kiev</c>) and for the id alone
/// next. An id that cannot be converted, or whose conversion this platform cannot find a zone for, keeps its own id: naming a zone is never a reason to fail.
/// Everything here is pure over its arguments, so a test drives it with a Windows id and an IANA id on any operating system.
/// </remarks>
internal static class StudioTimeZones
{
    private static readonly Lazy<string?> CurrentRegionName = new(ReadCurrentRegion);

    /// <summary>The two-letter region of the host's culture, which picks the IANA zone of a Windows id that several share, or <see langword="null" /> when the culture names none.</summary>
    public static string? CurrentRegion => CurrentRegionName.Value;

    /// <summary>
    /// The IANA id of <paramref name="id" />: itself when it is one already (it has a <c>/</c>, or the platform knows it as an IANA id - <c>UTC</c>), the converted
    /// id when it is a Windows id the platform can convert and find a zone for, and itself otherwise.
    /// </summary>
    /// <param name="id">A zone id as <see cref="TimeZoneInfo.Id" /> or a stored preference carries it.</param>
    /// <param name="region">The two-letter region to ask the conversion for first, or <see langword="null" /> for the id's default.</param>
    public static string IanaId(string id, string? region)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (id.Length == 0 || id.Contains('/', StringComparison.Ordinal) || TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
        {
            return id;
        }

        string? iana = null;
        bool converted = !string.IsNullOrEmpty(region) && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, region, out iana);
        converted = converted || TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out iana);

        return converted && !string.IsNullOrEmpty(iana) && Resolves(iana) ? iana : id;
    }

    /// <summary><see cref="IanaId(string, string?)" /> for the host's region.</summary>
    /// <param name="id">A zone id.</param>
    public static string IanaId(string id) => IanaId(id, CurrentRegion);

    /// <summary>The offset at <paramref name="at" /> as it is written beside a name: <c>UTC</c> at none, otherwise <c>UTC+02:00</c>.</summary>
    /// <param name="zone">The zone.</param>
    /// <param name="at">The moment the offset is read at.</param>
    public static string Offset(TimeZoneInfo zone, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(zone);

        TimeSpan offset = zone.GetUtcOffset(at);

        return offset == TimeSpan.Zero
            ? "UTC"
            : "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A zone by name, as a list offers it: <c>Europe/Helsinki (UTC+02:00)</c>. A zone whose name is its offset (<c>UTC</c>) is that word alone, and a zone
    /// that has no IANA id here is named by its own id, so the list stays one a visitor can read.
    /// </summary>
    /// <param name="zone">The zone.</param>
    /// <param name="at">The moment the offset is read at.</param>
    /// <param name="region">The two-letter region the conversion asks for first, or <see langword="null" />.</param>
    public static string Label(TimeZoneInfo zone, DateTimeOffset at, string? region)
    {
        ArgumentNullException.ThrowIfNull(zone);

        string name = IanaId(zone.Id, region);
        string offset = Offset(zone, at);

        return string.Equals(name, offset, StringComparison.Ordinal) ? name : name + " (" + offset + ")";
    }

    /// <summary>
    /// The zones the host knows, each under the id a visitor sees and stores (<see cref="IanaId(string, string?)" />) and with no id twice, in the order of those ids.
    /// </summary>
    /// <param name="region">The two-letter region the conversion asks for first, or <see langword="null" />.</param>
    public static IReadOnlyList<(string Id, TimeZoneInfo Zone)> Listed(string? region)
    {
        Dictionary<string, TimeZoneInfo> byId = new(StringComparer.Ordinal);
        foreach (TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones())
        {
            byId.TryAdd(IanaId(zone.Id, region), zone);
        }

        return [.. byId.OrderBy(static x => x.Key, StringComparer.Ordinal).Select(static x => (x.Key, x.Value))];
    }

    private static bool Resolves(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static string? ReadCurrentRegion()
    {
        try
        {
            string region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return region.Length == 2 && !string.Equals(region, "IV", StringComparison.Ordinal) ? region : null;
        }
        catch (Exception e) when (e is ArgumentException or CultureNotFoundException)
        {
            return null;
        }
    }
}
