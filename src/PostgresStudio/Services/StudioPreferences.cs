using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

/// <summary>
/// What this circuit's visitor chose about how the studio looks and reads: the theme, the time zone every timestamp is spelt in, and
/// the clock "today" is taken from.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, a browser tab's like <see cref="StudioState" />, and separate from it since the shell was split from the database: a
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

    private readonly ILogger<StudioPreferences> logger;
    private readonly TimeProvider clock;

    private string selectedTheme = "system";
    private string selectedTimeZoneId = TimeZoneInfo.Local.Id;
    private long preferenceVersion;

    /// <param name="httpContextAccessor">The request that opened the circuit, for the remembered preferences.</param>
    /// <param name="timeProvider">
    /// The clock "today" is taken from when a moment is spelt (<see cref="Clock" />): the host's own <see cref="TimeProvider" /> when
    /// it registered one, the system's otherwise.
    /// </param>
    /// <param name="logger">Where a page's failed preference-change handler goes; optional so that a test can build the preferences by hand.</param>
    public StudioPreferences(
        IHttpContextAccessor httpContextAccessor,
        TimeProvider? timeProvider = null,
        ILogger<StudioPreferences>? logger = null)
    {
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<StudioPreferences>.Instance;
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
    /// Raised by <see cref="NotifyChangedAsync" /> after a preference moved, so every subscribed page draws again.
    /// </summary>
    /// <remarks>
    /// A <see cref="Func{TResult}" /> returning a <see cref="Task" /> rather than an <see cref="EventHandler" />: a handler that has to
    /// re-query cannot be synchronous, and the <c>async void</c> handler an <c>EventHandler</c> forces is the one shape whose exception
    /// kills a Blazor circuit silently (AGENTS.md hard rule 6).
    /// </remarks>
    public event Func<Task>? Changed;

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
        set => Set(ref selectedTimeZoneId, NormalizeTimeZoneId(value));
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

    /// <summary>
    /// Tells every subscriber a preference moved.
    /// </summary>
    /// <remarks>
    /// The invocation list is walked by hand so that one page throwing does not stop the next page from being told: a page that never
    /// hears of a time-zone change keeps spelling its moments in the old one.
    /// </remarks>
    public async Task NotifyChangedAsync()
    {
        Delegate[] handlers = Changed?.GetInvocationList() ?? [];
        foreach (Delegate handler in handlers)
        {
            try
            {
                await ((Func<Task>) handler)().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "A studio page failed to handle a preference change");
            }
        }
    }

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

        string candidate = value.Trim();
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
