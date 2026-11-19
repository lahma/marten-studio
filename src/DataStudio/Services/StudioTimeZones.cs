using System.Globalization;
using System.Text.RegularExpressions;

namespace DataStudio.Services;

/// <summary>
/// How a time zone is named wherever the studio lists or labels one (#80): by its IANA id (<c>Europe/Helsinki</c>), never by the Windows id a Windows
/// host's <see cref="TimeZoneInfo" /> carries (<c>FLE Standard Time</c>), which reads as a display name it is not.
/// </summary>
/// <remarks>
/// <para>
/// A Windows id is converted with <see cref="TimeZoneInfo.TryConvertWindowsIdToIanaId(string, string?, out string?)" />, asking for the host's region first (a
/// Finnish host's <c>FLE Standard Time</c> is <c>Europe/Helsinki</c>, where the id alone is the list's default, <c>Europe/Kyiv</c>) and for the id alone
/// next. An id that cannot be converted, or whose conversion this platform cannot find a zone for, keeps its own id: naming a zone is never a reason to fail.
/// Everything here is pure over its arguments, so a test drives it with a Windows id and an IANA id on any operating system.
/// </para>
/// <para>
/// <b>Current names (#92).</b> CLDR's Windows-to-IANA table still spells some zones the way tzdata did before it renamed them (<c>Asia/Calcutta</c>, <c>Europe/Kiev</c>),
/// and a browser may report one (Chromium says <c>Asia/Calcutta</c>). <see cref="CurrentName(string)" /> is the one function that turns such a spelling into the
/// current one, from a closed table, and only where the host can find a zone by the current name; <see cref="IanaId(string, string?)" /> applies it to every id it
/// returns, so the list, the select's value, the label of the selected zone and a module's own zone label cannot differ. A stored or reported legacy spelling still works
/// and is the same entry as the current one.
/// </para>
/// <para>
/// <b>Zones with no IANA name (#92).</b> A Windows zone the conversion has no IANA name for (<c>British Columbia Standard Time</c>) is listed after the IANA ids, named by
/// its display name, with the id it can be read back by as the option's value; the two obsolete ones (<see cref="ObsoleteWindowsIds" />) are not listed at all, and are
/// offered only to the visitor whose stored or browser zone is one of them.
/// </para>
/// </remarks>
internal static partial class StudioTimeZones
{
    /// <summary>
    /// CLDR's legacy IANA spellings and the names tzdata renamed them to (#92): a closed table, which changes rarely. Every pair is one of tzdata's <c>backward</c> links.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyToCurrent = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Africa/Asmera"] = "Africa/Asmara",
        ["America/Buenos_Aires"] = "America/Argentina/Buenos_Aires",
        ["America/Catamarca"] = "America/Argentina/Catamarca",
        ["America/Coral_Harbour"] = "America/Atikokan",
        ["America/Cordoba"] = "America/Argentina/Cordoba",
        ["America/Godthab"] = "America/Nuuk",
        ["America/Indianapolis"] = "America/Indiana/Indianapolis",
        ["America/Jujuy"] = "America/Argentina/Jujuy",
        ["America/Louisville"] = "America/Kentucky/Louisville",
        ["America/Mendoza"] = "America/Argentina/Mendoza",
        ["Asia/Calcutta"] = "Asia/Kolkata",
        ["Asia/Katmandu"] = "Asia/Kathmandu",
        ["Asia/Rangoon"] = "Asia/Yangon",
        ["Asia/Saigon"] = "Asia/Ho_Chi_Minh",
        ["Atlantic/Faeroe"] = "Atlantic/Faroe",
        ["Europe/Kiev"] = "Europe/Kyiv",
        ["Pacific/Enderbury"] = "Pacific/Kanton",
        ["Pacific/Ponape"] = "Pacific/Pohnpei",
        ["Pacific/Truk"] = "Pacific/Chuuk",
    };

    /// <summary>
    /// The Windows zones Microsoft keeps only for old data, whose display names end in "- Old" (#92). Not listed; a fixed set of ids, since the display name is localized.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ObsoleteWindowsIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "Kamchatka Standard Time",
        "Mid-Atlantic Standard Time",
    };

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
            return CurrentName(id);
        }

        string? iana = null;
        bool converted = !string.IsNullOrEmpty(region) && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, region, out iana);
        converted = converted || TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out iana);

        return converted && !string.IsNullOrEmpty(iana) && Resolves(iana) ? CurrentName(iana) : id;
    }

    /// <summary>
    /// The current IANA name of <paramref name="id" /> (#92): the name tzdata renamed it to when <paramref name="id" /> is one of CLDR's legacy spellings
    /// (<c>Asia/Calcutta</c> is <c>Asia/Kolkata</c>) and the host can find a zone by the current name, and <paramref name="id" /> itself otherwise.
    /// </summary>
    /// <param name="id">A zone id: an IANA name in either spelling, a Windows id, or anything else.</param>
    public static string CurrentName(string id) => CurrentName(id, Resolves);

    /// <summary><see cref="CurrentName(string)" /> with the host's answer to "can a zone be found by this name?" given, so a test drives both answers.</summary>
    /// <param name="id">A zone id.</param>
    /// <param name="resolves">Whether the host can find a zone by a name.</param>
    public static string CurrentName(string id, Func<string, bool> resolves)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(resolves);

        return LegacyToCurrent.TryGetValue(id, out string? current) && resolves(current) ? current : id;
    }

    /// <summary>
    /// Whether <paramref name="id" /> is a Windows zone id with no IANA name (#92): it has a space (no IANA name does, and every Windows id but <c>UTC</c> does), and
    /// <see cref="IanaId(string, string?)" /> could not convert it, which is what a zone <see cref="Listed(string?)" /> after the IANA ids is.
    /// </summary>
    /// <param name="id">The id a zone is listed under.</param>
    public static bool IsUnconvertedWindowsId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return id.Contains(' ', StringComparison.Ordinal) && !id.Contains('/', StringComparison.Ordinal);
    }

    /// <summary>
    /// What a zone is called in the select (#92): its IANA id, or - for a Windows zone with no IANA name - its display name without the <c>(UTC+hh:mm) </c> the
    /// system writes in front of it, which the studio's own offset replaces; the id when the display name says nothing else.
    /// </summary>
    /// <param name="id">The id the zone is listed under.</param>
    /// <param name="zone">The zone.</param>
    public static string NameOf(string id, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(zone);

        if (!IsUnconvertedWindowsId(id))
        {
            return id;
        }

        string name = OffsetPrefix().Replace(zone.DisplayName ?? string.Empty, string.Empty).Trim();

        return name.Length == 0 ? id : name;
    }

    /// <summary>
    /// What a zone is called in a sentence, a tooltip or beside a time (zn52): <see cref="NameOf(string, TimeZoneInfo)" /> of its IANA id, so
    /// the Preferences list, a module's own zone label and a tooltip that says which zone a time is shown in all use one name. A Windows zone with no
    /// IANA name is its display name; one with no display name either keeps its id.
    /// </summary>
    /// <param name="zone">The zone.</param>
    /// <param name="region">The two-letter region the conversion asks for first, or <see langword="null" />.</param>
    public static string NameOf(TimeZoneInfo zone, string? region)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return NameOf(IanaId(zone.Id, region), zone);
    }

    [GeneratedRegex(@"^\(UTC(?:[+-]\d{1,2}:\d{2})?\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPrefix();

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

        return Label(IanaId(zone.Id, region), zone, at);
    }

    /// <summary>
    /// <see cref="Label(TimeZoneInfo, DateTimeOffset, string?)" /> for a zone whose name is already known - the id <see cref="Listed(string?)" /> holds it under -
    /// so a list drawn on every render converts nothing again.
    /// </summary>
    /// <param name="name">The name the zone is listed under (<see cref="IanaId(string, string?)" /> of its id).</param>
    /// <param name="zone">The zone.</param>
    /// <param name="at">The moment the offset is read at.</param>
    public static string Label(string name, TimeZoneInfo zone, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(zone);

        string offset = Offset(zone, at);

        return string.Equals(name, offset, StringComparison.Ordinal) ? name : name + " (" + offset + ")";
    }

    /// <summary>One entry of the Preferences time-zone select: the id it holds, its zone, and whether it is the zone this browser reported.</summary>
    /// <param name="Id">The id the option holds and a choice stores.</param>
    /// <param name="Zone">The zone.</param>
    /// <param name="IsBrowser">Whether this is the browser's own zone, which the option says so beside its name.</param>
    public readonly record struct ZoneOption(string Id, TimeZoneInfo Zone, bool IsBrowser)
    {
        /// <summary>What the option is called (<see cref="NameOf(string, TimeZoneInfo)" />): its id, or the display name of a Windows zone that has no IANA name.</summary>
        public string Name => NameOf(Id, Zone);
    }

    /// <summary>
    /// The options the Preferences select draws: the host's listed zones, and in front of them (the selected one first) the circuit's zone and the browser's
    /// when the list does not hold them under their own ids. A browser says <c>Europe/Helsinki</c> where a Windows host in another region lists the same
    /// zone as <c>Europe/Kiev</c>, and a stored id may be one the list lacks, so the select never shows an option that is not the zone in use, and the
    /// browser's zone is always there to go back to.
    /// </summary>
    /// <param name="listed">The host's zones (<see cref="Listed(string?)" />).</param>
    /// <param name="selected">The circuit's zone: the id <see cref="IanaId(string, string?)" /> gives it and the zone, or <see langword="null" /> for none.</param>
    /// <param name="browser">The browser's zone under the id it reported, or <see langword="null" /> when it reported none.</param>
    public static IReadOnlyList<ZoneOption> Options(IReadOnlyList<(string Id, TimeZoneInfo Zone)> listed, (string Id, TimeZoneInfo Zone)? selected, (string Id, TimeZoneInfo Zone)? browser)
    {
        ArgumentNullException.ThrowIfNull(listed);

        HashSet<string> known = new(listed.Select(static x => x.Id), StringComparer.Ordinal);
        List<ZoneOption> options = [];

        foreach ((string Id, TimeZoneInfo Zone)? extra in new (string Id, TimeZoneInfo Zone)?[] { selected, browser })
        {
            if (extra is { } zone && known.Add(zone.Id))
            {
                options.Add(new ZoneOption(zone.Id, zone.Zone, browser is { } b && string.Equals(b.Id, zone.Id, StringComparison.Ordinal)));
            }
        }

        foreach ((string id, TimeZoneInfo zone) in listed)
        {
            options.Add(new ZoneOption(id, zone, browser is { } b && string.Equals(b.Id, id, StringComparison.Ordinal)));
        }

        return options;
    }

    /// <summary>The words that mark the browser's own zone in the Preferences select.</summary>
    public const string BrowserMark = ", this browser";

    /// <summary>
    /// The zones the host knows, each under the id a visitor sees and stores (<see cref="IanaId(string, string?)" />) and with no id twice: the IANA ids in the order of
    /// those ids, then - after them, in the order of their names - the Windows zones that have no IANA name, under the id the host reads them back by (#92). The two
    /// obsolete Windows zones (<see cref="ObsoleteWindowsIds" />) are left out.
    /// </summary>
    /// <param name="region">The two-letter region the conversion asks for first, or <see langword="null" />.</param>
    public static IReadOnlyList<(string Id, TimeZoneInfo Zone)> Listed(string? region) => Listed(TimeZoneInfo.GetSystemTimeZones(), region);

    /// <summary><see cref="Listed(string?)" /> over the zones given, so a test lists custom zones on any host.</summary>
    /// <param name="zones">The zones the host knows.</param>
    /// <param name="region">The two-letter region the conversion asks for first, or <see langword="null" />.</param>
    public static IReadOnlyList<(string Id, TimeZoneInfo Zone)> Listed(IEnumerable<TimeZoneInfo> zones, string? region)
    {
        ArgumentNullException.ThrowIfNull(zones);

        Dictionary<string, TimeZoneInfo> byId = new(StringComparer.Ordinal);
        foreach (TimeZoneInfo zone in zones)
        {
            if (!ObsoleteWindowsIds.Contains(zone.Id))
            {
                byId.TryAdd(IanaId(zone.Id, region), zone);
            }
        }

        return
        [
            .. byId.Where(static x => !IsUnconvertedWindowsId(x.Key)).OrderBy(static x => x.Key, StringComparer.Ordinal).Select(static x => (x.Key, x.Value)),
            .. byId.Where(static x => IsUnconvertedWindowsId(x.Key))
                .OrderBy(static x => NameOf(x.Key, x.Value), StringComparer.Ordinal)
                .ThenBy(static x => x.Key, StringComparer.Ordinal)
                .Select(static x => (x.Key, x.Value)),
        ];
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
