using Bunit;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Components;

/// <summary>
/// A bUnit context carrying everything the studio's components inject, so a page can be rendered by
/// naming it and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The two data sources are hand-written fakes - <see cref="FakeStudioScopeCatalog" /> for the header's
/// listings and <see cref="FakeStoreInfoService" /> for the Overview's facts. Everything else is the
/// real service, because those are the ones the components actually talk to and they are cheap. bUnit
/// supplies the navigation manager and the JavaScript runtime.
/// </para>
/// <para>
/// The time zone is pinned to UTC: <see cref="StudioState" /> formats every timestamp in the selected
/// zone, which otherwise is the machine's and makes rendered text machine-dependent.
/// </para>
/// </remarks>
internal class StudioComponentContext : BunitContext
{
    /// <summary>The policy name <see cref="WithPolicy" /> configures.</summary>
    public const string StorePolicyName = "MartenStoreOwner";

    /// <param name="configure">
    /// Extra registrations, applied after the studio's own and before anything is resolved.
    /// </param>
    /// <remarks>
    /// The hook exists because bUnit locks its service provider the first time anything is resolved from
    /// it, and this constructor resolves three services at the end - so a page test that needs its own
    /// area's <c>Fake*DataService</c> has no way to add one afterwards. Every page area needs exactly
    /// this, which is why it is a parameter here rather than a duplicate of this class per area.
    /// </remarks>
    public StudioComponentContext(Action<IServiceCollection>? configure = null)
    {
        Options = new MartenStudioOptions();
        Catalog = new FakeStudioScopeCatalog();
        StoreInfo = new FakeStoreInfoService();
        AuthorizationService = new TestStoreAuthorizationService();
        AuthenticationState = new TestAuthenticationStateProvider();

        // CopyBadge and the theme picker reach for JavaScript, so a strict runtime would fail every page
        // that renders one for a call no test is about.
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        Services.AddSingleton<IOptions<MartenStudioOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        Services.AddSingleton<IStudioScopeCatalog>(Catalog);
        Services.AddSingleton<IStoreInfoService>(StoreInfo);
        Services.AddSingleton<IAuthorizationService>(AuthorizationService);
        Services.AddSingleton<AuthenticationStateProvider>(AuthenticationState);
        Services.AddSingleton<StudioAuthorization>();
        Services.AddSingleton<StudioState>();
        Services.AddSingleton<StudioCapabilityGuard>();
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<StudioActionLogService>();
        Services.AddSingleton<StudioActionLog>();

        configure?.Invoke(Services);

        State = Services.GetRequiredService<StudioState>();
        State.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;
        Toasts = Services.GetRequiredService<ToastService>();
        ActionLog = Services.GetRequiredService<StudioActionLog>();
    }

    /// <summary>The options an application would have configured.</summary>
    public MartenStudioOptions Options { get; }

    /// <summary>What the header is offered.</summary>
    public FakeStudioScopeCatalog Catalog { get; }

    /// <summary>What the Overview page is given.</summary>
    public FakeStoreInfoService StoreInfo { get; }

    /// <summary>The policy engine, and the record of what it was asked.</summary>
    public TestStoreAuthorizationService AuthorizationService { get; }

    /// <summary>Who the circuit belongs to.</summary>
    public TestAuthenticationStateProvider AuthenticationState { get; }

    /// <summary>The circuit's scope state, as the components see it.</summary>
    public StudioState State { get; }

    public ToastService Toasts { get; }

    public StudioActionLog ActionLog { get; }

    /// <summary>Where the browser is now.</summary>
    public string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;

    /// <summary>
    /// Turns the per-store policy on and says which stores the visitor passes for. Every other store is
    /// one they may not see.
    /// </summary>
    public StudioComponentContext WithPolicy(params string[] allowedStoreKeys)
    {
        Options.StoreAuthorizationPolicy = StorePolicyName;
        AuthorizationService.AllowStores(allowedStoreKeys);
        return this;
    }

    /// <summary>Adds a store to the header's listing, with the databases named.</summary>
    public StudioComponentContext WithStores(params string[] storeKeys)
    {
        foreach (string key in storeKeys)
        {
            Catalog.WithStore(key, key == "default" ? "Default" : key, databaseIdentities: "localhost.marten");
        }

        return this;
    }

    /// <summary>Enables every mutating capability, the way a host opts in with one line.</summary>
    public StudioComponentContext WithAllCapabilities()
    {
        Options.Capabilities = MartenStudioCapabilities.All();
        return this;
    }

    /// <summary>Puts the browser on <paramref name="relativeUri" /> before a page is rendered.</summary>
    public StudioComponentContext Navigate(string relativeUri)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(relativeUri);
        return this;
    }
}
