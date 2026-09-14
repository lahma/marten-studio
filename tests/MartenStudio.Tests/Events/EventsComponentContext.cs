using Bunit;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Events;

/// <summary>
/// A bUnit context for the Events pages: the real studio services, the shared test doubles, and a fake
/// <see cref="IEventDataService" />, with a scope the pages can be about.
/// </summary>
/// <remarks>
/// <para>
/// It is a sibling of <c>StudioComponentContext</c> rather than a subclass of it, for a reason worth
/// writing down: bUnit's service provider refuses a registration once anything has been resolved from it,
/// and the shared context resolves <see cref="StudioState" /> in its own constructor - so a subclass can
/// never add a service of its own. Copying the twelve registrations is the smaller price, and it also
/// keeps this packet out of a file the Documents packet is adding its own fakes to.
/// </para>
/// <para>
/// The time zone is pinned to UTC for the same reason the shared context pins it: every timestamp the
/// pages render goes through <c>StudioState.FormatInSelectedTimeZone</c>, which would otherwise make the
/// expected markup depend on the machine.
/// </para>
/// </remarks>
internal sealed class EventsComponentContext : BunitContext
{
    /// <param name="jsRuntime">
    /// A JavaScript runtime of the test's own, for the tests that are about what happens when the browser
    /// is not there. Left null, the pages talk to bUnit's loose runtime, which answers everything.
    /// Registered <em>after</em> the base constructor has added bUnit's own, so this one wins - which is
    /// only possible because the registration happens here, before anything resolves from the provider.
    /// </param>
    public EventsComponentContext(Microsoft.JSInterop.IJSRuntime? jsRuntime = null)
    {
        Options = new MartenStudioOptions();
        Catalog = new FakeStudioScopeCatalog();
        Data = new FakeEventDataService();
        AuthorizationService = new TestStoreAuthorizationService();
        AuthenticationState = new TestAuthenticationStateProvider();

        // The copy badge, the visibility watcher and the scroll helper all reach for JavaScript, and a
        // strict runtime would fail every page for a call no test is about.
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        Services.AddSingleton<IOptions<MartenStudioOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        Services.AddSingleton<IStudioScopeCatalog>(Catalog);
        Services.AddSingleton<IAuthorizationService>(AuthorizationService);
        Services.AddSingleton<AuthenticationStateProvider>(AuthenticationState);
        Services.AddSingleton<StudioAuthorization>();
        Services.AddSingleton<StudioState>();
        Services.AddSingleton<StudioCapabilityGuard>();
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<StudioActionLogService>();
        Services.AddSingleton<StudioActionLog>();
        Services.AddSingleton<IEventDataService>(Data);

        if (jsRuntime is not null)
        {
            Services.AddSingleton(jsRuntime);
        }
    }

    /// <summary>The options an application would have configured.</summary>
    public MartenStudioOptions Options { get; }

    /// <summary>What the header is offered.</summary>
    public FakeStudioScopeCatalog Catalog { get; }

    /// <summary>What the Events pages are given.</summary>
    public FakeEventDataService Data { get; }

    /// <summary>The policy engine, and the record of what it was asked.</summary>
    public TestStoreAuthorizationService AuthorizationService { get; }

    /// <summary>Who the circuit belongs to.</summary>
    public TestAuthenticationStateProvider AuthenticationState { get; }

    /// <summary>The circuit's scope state, as the pages see it.</summary>
    public StudioState State => Services.GetRequiredService<StudioState>();

    /// <summary>Where the browser is now.</summary>
    public string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;

    /// <summary>Enables every mutating capability, the way a host opts in with one line.</summary>
    public EventsComponentContext WithAllCapabilities()
    {
        Options.Capabilities = MartenStudioCapabilities.All();
        return this;
    }

    /// <summary>Puts the browser on <paramref name="relativeUri" /> before a page is rendered.</summary>
    public EventsComponentContext Navigate(string relativeUri)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(relativeUri);
        return this;
    }

    /// <summary>
    /// Settles an active scope, the way the layout does on a real circuit. A page rendered on its own
    /// never runs the layout, and every Events page reads <see cref="StudioState.ActiveScope" />.
    /// </summary>
    public async Task<EventsComponentContext> ReadyAsync(string storeKey = "default")
    {
        Catalog.WithStore(storeKey, storeKey == "default" ? "Default" : storeKey, databaseIdentities: "localhost.marten");

        StudioState state = State;
        state.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;

        await state.EnsureInitializedAsync();

        return this;
    }
}
