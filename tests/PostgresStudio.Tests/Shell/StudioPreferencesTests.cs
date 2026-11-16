using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services;
using PostgresStudio.Services.Settings;
using PostgresStudio.Tests.Components;

namespace PostgresStudio.Tests.Shell;

/// <summary>
/// <see cref="StudioPreferences" />: what moved out of <see cref="StudioState" /> when the shell's preferences became their own
/// scoped service (shell A1), and the one relay that keeps a time-zone change reaching every page that listens to the scope.
/// </summary>
public sealed class StudioPreferencesTests
{
    private static StudioPreferences WithCookies(string? theme, string? timeZone, TimeProvider? clock = null)
    {
        DefaultHttpContext http = new();
        List<string> cookies = [];
        if (theme is not null)
        {
            cookies.Add(StudioPreferences.ThemePreferenceKey + "=" + theme);
        }

        if (timeZone is not null)
        {
            cookies.Add(StudioPreferences.TimeZonePreferenceKey + "=" + Uri.EscapeDataString(timeZone));
        }

        http.Request.Headers.Cookie = string.Join("; ", cookies);
        return new StudioPreferences(new HttpContextAccessor { HttpContext = http }, clock);
    }

    [Fact]
    public void The_theme_and_the_time_zone_are_seeded_from_the_request_cookies()
    {
        StudioPreferences preferences = WithCookies("dark", "Asia/Kolkata");

        preferences.SelectedTheme.Should().Be("dark");
        preferences.SelectedTimeZoneId.Should().Be("Asia/Kolkata");
        preferences.PreferenceVersion.Should().Be(0, "seeding is not a preference the visitor expressed");
    }

    [Fact]
    public void A_cookie_naming_nothing_known_falls_back_to_the_system_theme_and_the_machines_zone()
    {
        StudioPreferences preferences = WithCookies("neon", "Not/AZone");

        preferences.SelectedTheme.Should().Be("system");
        preferences.SelectedTimeZoneId.Should().Be(TimeZoneInfo.Local.Id);
    }

    [Fact]
    public void Without_a_request_the_defaults_stand()
    {
        StudioPreferences preferences = new(new HttpContextAccessor());

        preferences.SelectedTheme.Should().Be("system");
        preferences.SelectedTimeZoneId.Should().Be(TimeZoneInfo.Local.Id);
        preferences.Clock.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void Only_a_real_change_counts_as_a_preference_the_visitor_expressed()
    {
        StudioPreferences preferences = new(new HttpContextAccessor());

        preferences.SelectedTheme = "system";
        preferences.PreferenceVersion.Should().Be(0, "the theme is already system");

        preferences.SelectedTheme = "DARK";
        preferences.SelectedTheme.Should().Be("dark");
        preferences.PreferenceVersion.Should().Be(1);

        preferences.SelectedTheme = "dark";
        preferences.PreferenceVersion.Should().Be(1, "re-selecting the theme that is on changes nothing");

        preferences.SelectedTimeZoneId = "UTC";
        preferences.PreferenceVersion.Should().Be(TimeZoneInfo.Local.Id == "UTC" ? 1 : 2);
    }

    [Fact]
    public void A_moment_is_spelt_in_the_selected_zone()
    {
        StudioPreferences preferences = new(new HttpContextAccessor()) { SelectedTimeZoneId = "Asia/Kolkata" };
        DateTimeOffset moment = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        preferences.FormatInSelectedTimeZone(moment).Should().Be("2026-10-26 17:30:00 +05:30");
        preferences.FormatInSelectedTimeZone(moment, "HH:mm").Should().Be("17:30");
        preferences.FormatInSelectedTimeZone((DateTimeOffset?) null).Should().Be("n/a");
        preferences.ConvertToSelectedTimeZone(moment).Offset.Should().Be(TimeSpan.FromMinutes(330));
    }

    [Fact]
    public async Task Every_subscriber_hears_of_a_change_even_when_one_throws_and_the_failure_is_logged()
    {
        ListLogger logger = new();
        StudioPreferences preferences = new(new HttpContextAccessor(), null, logger);
        List<string> told = [];

        preferences.Changed += () => { told.Add("first"); return Task.CompletedTask; };
        preferences.Changed += () => throw new InvalidOperationException("a page broke");
        preferences.Changed += () => { told.Add("third"); return Task.CompletedTask; };

        await preferences.NotifyChangedAsync();

        told.Should().Equal("first", "third");
        logger.Messages.Should().ContainSingle().Which.Should().Be("A studio page failed to handle a preference change");
    }

    [Fact]
    public async Task A_change_on_a_circuit_that_never_resolved_the_scope_state_throws_nothing()
    {
        StudioPreferences preferences = new(new HttpContextAccessor());

        preferences.SelectedTimeZoneId = "Asia/Kolkata";
        Func<Task> notify = preferences.NotifyChangedAsync;

        await notify.Should().NotThrowAsync("nothing subscribes until the scope state is built");
    }

    [Fact]
    public async Task A_time_zone_change_still_reaches_a_page_subscribed_to_the_scope_changed_event()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        int pages = 0;
        context.State.ScopeChanged += () => { pages++; return Task.CompletedTask; };

        context.Preferences.SelectedTimeZoneId = "Asia/Kolkata";
        await context.Preferences.NotifyChangedAsync();

        pages.Should().Be(1, "the state relays the preferences' change to ScopeChanged, as the layout's State.NotifyAsync() did");
    }

    [Fact]
    public async Task A_page_that_throws_on_a_time_zone_change_does_not_stop_the_next_page()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        bool second = false;
        context.State.ScopeChanged += () => throw new InvalidOperationException("a page broke");
        context.State.ScopeChanged += () => { second = true; return Task.CompletedTask; };

        await context.Preferences.NotifyChangedAsync();

        second.Should().BeTrue();
    }

    [Fact]
    public void The_shell_settings_are_the_studios_settings_and_spell_the_master_switch_as_the_host_does()
    {
        using PostgresStudioComponentContext context = new();

        IShellSettings shell = context.Services.GetRequiredService<IShellSettings>();

        shell.Should().BeSameAs(context.Services.GetRequiredService<IStudioSettings>(), "one object, two interfaces (D30)");
        shell.ReadOnlyOptionName.Should().Be("PostgresStudioOptions.ReadOnly");
        shell.ProductName.Should().Be("Postgres Studio");
    }

    /// <summary>Shell A1 follow-up F4: the one-object guarantee holds on a container the real <c>AddPostgresStudio()</c> builds, not only on a test context.</summary>
    [Fact]
    public void AddPostgresStudio_registers_one_settings_object_under_both_interfaces()
    {
        ServiceCollection services = new();
        services.AddPostgresStudio();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IShellSettings>().Should().BeSameAs(provider.GetRequiredService<IStudioSettings>());
    }

    private sealed class ListLogger : ILogger<StudioPreferences>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
