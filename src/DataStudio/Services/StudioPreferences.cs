using System.Globalization;

using Microsoft.AspNetCore.Http;

namespace DataStudio.Services;

/// <summary>
/// What this circuit's visitor chose about how the studio looks and reads: the theme, the time zone every timestamp is spelt in, and
/// the clock "today" is taken from.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, a browser tab's like <c>StudioState</c>, and separate from it since the shell was split from the database: a
/// preference belongs to the shell and knows no store, database or tenant. The theme and the time zone are seeded from cookies in the
/// constructor rather than fetched from <c>localStorage</c> after the first render, so a dark studio renders dark on the server and
/// there is no flash of the light theme during prerendering.
/// </para>
/// </remarks>
internal sealed class StudioPreferences
{
    /// <summary>The cookie and <c>localStorage</c> key the theme is remembered under.</summary>
    public const string ThemePreferenceKey = "ms_theme";

    /// <summary>The cookie and <c>localStorage</c> key the time zone is remembered under.</summary>
    public const string TimeZonePreferenceKey = "ms_tz";

    private readonly TimeProvider clock;

    private string selectedTheme = "system";
    private string selectedTimeZoneId = StudioTimeZones.CurrentName(TimeZoneInfo.Local.Id);
    private long preferenceVersion;
    private long displayVersion;

    /// <param name="httpContextAccessor">The request that opened the circuit, for the remembered preferences.</param>
    /// <param name="timeProvider">
    /// The clock "today" is taken from when a moment is spelt (<see cref="Clock" />): the host's own <see cref="TimeProvider" /> when
    /// it registered one, the system's otherwise.
    /// </param>
    public StudioPreferences(
        IHttpContextAccessor httpContextAccessor,
        TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;

        HttpContext? httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        string? themeCookie = httpContext.Request.Cookies[ThemePreferenceKey];
        if (!string.IsNullOrWhiteSpace(themeCookie))
        {
            selectedTheme = NormalizeTheme(themeCookie);
        }

        string? timeZoneCookie = httpContext.Request.Cookies[TimeZonePreferenceKey];
        if (!string.IsNullOrWhiteSpace(timeZoneCookie))
        {
            selectedTimeZoneId = NormalizeTimeZoneId(timeZoneCookie);
        }
    }

    /// <summary>
    /// The name the layout cascades <see cref="DisplayVersion" /> under, and the one <c>StudioDisplayComponent</c> takes it by.
    /// </summary>
    public const string DisplayVersionName = "DataStudio.DisplayVersion";

    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string SelectedTheme
    {
        get => selectedTheme;
        set => Set(ref selectedTheme, NormalizeTheme(value));
    }

    /// <summary>The time zone every timestamp is rendered in.</summary>
    public string SelectedTimeZoneId
    {
        get => selectedTimeZoneId;
        set
        {
            string before = selectedTimeZoneId;
            Set(ref selectedTimeZoneId, NormalizeTimeZoneId(value));
            if (!string.Equals(before, selectedTimeZoneId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref displayVersion);
            }
        }
    }

    /// <summary>
    /// How many times the zone every time is spelt in has moved on this circuit, however it moved: a choice in Preferences, a stored
    /// zone read after the circuit started, or the browser's own (<see cref="TryUseBrowserTimeZone" />).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The layout cascades it (<see cref="DisplayVersionName" />) and a component that draws a time takes it
    /// (<c>StudioDisplayComponent</c>), so a zone change reaches every one of them in the one render pass it causes: they draw again from
    /// what they already hold. It is <em>not</em> <see cref="PreferenceVersion" />, which a browser default does not move and which
    /// settles a race over what the visitor chose.
    /// </para>
    /// <para>
    /// A zone change is a change of display and nothing else: it is not a change of scope, and nothing reads, invalidates, audits or
    /// rewrites an address because of it. There is no event for it on purpose - an event is something a page has to remember to
    /// subscribe to, and a page that took the notice as a reason to load again was the cost this replaced.
    /// </para>
    /// </remarks>
    public long DisplayVersion => Volatile.Read(ref displayVersion);

    /// <summary>
    /// The IANA id of the time zone this circuit's browser reported (<c>Europe/Helsinki</c>; under its current name, <c>Asia/Kolkata</c> for a browser that says <c>Asia/Calcutta</c>), or <see langword="null" /> until it has and where the
    /// visitor had chosen a zone of their own, which the browser is then not asked about. Never stored: only a choice in Preferences is.
    /// </summary>
    public string? BrowserTimeZoneId { get; private set; }

    /// <summary>
    /// Makes the browser's own time zone the circuit's, for a visitor who has stored none: the id the browser reported
    /// (<c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>), when this server knows it. Nothing is stored and no preference is counted as the
    /// visitor's (<see cref="PreferenceVersion" /> does not move): it is a default, replaced by any choice they make.
    /// </summary>
    /// <param name="ianaId">What the browser said, or <see langword="null" /> where it said nothing or the call failed.</param>
    /// <returns>Whether the circuit's zone is now the browser's. An id of an unexpected shape, or one this server does not know, is not used.</returns>
    public bool TryUseBrowserTimeZone(string? ianaId)
    {
        // The current name is asked for first: a host whose zone data has only the current names knows Asia/Kolkata and not the Asia/Calcutta a
        // browser says, and the spelling alone is no reason to refuse the browser's zone.
        if (!LooksLikeZoneId(ianaId) || !TimeZoneInfo.TryFindSystemTimeZoneById(StudioTimeZones.CurrentName(ianaId), out _))
        {
            return false;
        }

        // Under its current name (#92): Chromium says Asia/Calcutta, and the circuit's zone, the select's entry and its "this browser" mark are all Asia/Kolkata -
        // one zone under one name, so a spelling alone moves neither version.
        string name = StudioTimeZones.CurrentName(ianaId);

        BrowserTimeZoneId = name;
        if (!string.Equals(selectedTimeZoneId, name, StringComparison.Ordinal))
        {
            selectedTimeZoneId = name;
            Interlocked.Increment(ref displayVersion);
        }

        return true;
    }

    /// <summary>
    /// Whether a text from the browser has the shape of a zone id (<c>Europe/Helsinki</c>, <c>America/Argentina/Buenos_Aires</c>, <c>Etc/GMT+5</c>,
    /// <c>UTC</c>) before the host's zone database is asked about it: bounded, and letters, digits, <c>/ _ - +</c> and nothing else, with no path
    /// segment that climbs. The id is the visitor's own browser's and is used only to find a zone.
    /// </summary>
    private static bool LooksLikeZoneId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id.Contains("..", StringComparison.Ordinal) || id[0] == '/' || id[^1] == '/')
        {
            return false;
        }

        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('/' or '_' or '-' or '+'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// How many times <see cref="SelectedTheme" /> or <see cref="SelectedTimeZoneId" /> has actually
    /// moved on this circuit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sequence number rather than a timestamp, and it exists for exactly one reason: reading the
    /// browser's remembered preferences is two awaited JS round trips, and on a slow browser the visitor
    /// can pick a theme while those are in flight. Without a stamp taken before the awaits and checked
    /// after them, the answer that comes back - which was read <em>before</em> the click, so it is the
    /// preference the visitor has just moved away from - is written over the one they chose, and the
    /// picker snaps back on its own a moment after they used it.
    /// </para>
    /// <para>
    /// Only a real change counts. Re-selecting the theme that is already on changes nothing and needs no
    /// protecting, and counting it would make a stored time zone lose a race it never entered.
    /// </para>
    /// </remarks>
    public long PreferenceVersion => Volatile.Read(ref preferenceVersion);

    /// <summary>The clock a page takes "now" and "today" from: the host's <see cref="TimeProvider" /> if one is registered, else the system's.</summary>
    public TimeProvider Clock => clock;

    /// <summary><paramref name="value" /> in the selected time zone.</summary>
    public DateTimeOffset ConvertToSelectedTimeZone(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, ResolveSelectedTimeZone());

    /// <summary><paramref name="value" /> formatted in the selected time zone.</summary>
    public string FormatInSelectedTimeZone(DateTimeOffset value, string format = "u")
    {
        DateTimeOffset converted = ConvertToSelectedTimeZone(value);
        string outputFormat = string.Equals(format, "u", StringComparison.Ordinal) ? "yyyy-MM-dd HH:mm:ss zzz" : format;
        return converted.ToString(outputFormat, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc cref="FormatInSelectedTimeZone(DateTimeOffset, string)" />
    public string FormatInSelectedTimeZone(DateTimeOffset? value, string format = "u") =>
        value.HasValue ? FormatInSelectedTimeZone(value.Value, format) : "n/a";

    /// <summary>
    /// Assigns one preference and counts it, so <see cref="PreferenceVersion" /> says whether the visitor
    /// has expressed a preference since some earlier moment.
    /// </summary>
    private void Set(ref string field, string value)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        Interlocked.Increment(ref preferenceVersion);
    }

    private TimeZoneInfo ResolveSelectedTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(selectedTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string NormalizeTimeZoneId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeZoneInfo.Local.Id;
        }

        // A stored legacy spelling (Asia/Calcutta) is read as the current name (#92), so choosing or storing the same zone twice is no change;
        // and it is the current name that is looked for, which a host with only the current names in its zone data still finds.
        string candidate = StudioTimeZones.CurrentName(value.Trim());
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(candidate);
            return candidate;
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local.Id;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local.Id;
        }
    }

    private static string NormalizeTheme(string? value)
    {
        if (string.Equals(value, "light", StringComparison.OrdinalIgnoreCase))
        {
            return "light";
        }

        return string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "system";
    }
}
