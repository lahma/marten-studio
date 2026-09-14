using Bunit;

using MartenStudio.Internal;
using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
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
    /// it, so a page test that needs its own area's <c>Fake*DataService</c> has to be able to add one
    /// while the collection is still open. Nothing in this constructor resolves anything (every property
    /// below is resolved on first use), so the hook and a derived context both still work. Every page
    /// area needs exactly this, which is why it is a parameter here rather than a duplicate of this class
    /// per area.
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

        // The time zone is pinned here rather than by resolving the state in this constructor: bUnit
        // seals its service provider the moment anything is resolved from it, and a derived context - one
        // per area, each with its own fake data service - has to be able to add services after this
        // constructor has run.
        Services.AddSingleton(static provider =>
        {
            StudioState state = ActivatorUtilities.CreateInstance<StudioState>(provider);
            state.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;
            return state;
        });

        Services.AddSingleton<StudioCapabilityGuard>();
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<StudioActionLogService>();
        Services.AddSingleton<StudioActionLog>();

        // What the layout reads to decide whether to draw the "served to anyone" banner. The real
        // singleton, so a test says what it says by handing it real endpoint metadata. Constructed
        // here rather than resolved, so it does not seal the service provider either.
        MappedEndpoints = new MartenStudioMappedEndpoints();
        Services.AddSingleton(MappedEndpoints);

        // AddRazorComponents() would register these; a bUnit context has neither, and the layout carries
        // the prerendered theme and time zone across to the circuit through them.
        Services.AddSingleton<ComponentStatePersistenceManager>();
        Services.AddSingleton(static provider =>
            provider.GetRequiredService<ComponentStatePersistenceManager>().State);

        configure?.Invoke(Services);
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
    /// <remarks>
    /// Resolved on first use, not in the constructor: see the note there about bUnit's service provider.
    /// </remarks>
    public StudioState State => Services.GetRequiredService<StudioState>();

    /// <summary>What the startup guard observed about the mapping, as the layout reads it.</summary>
    public MartenStudioMappedEndpoints MappedEndpoints { get; }

    public ToastService Toasts => Services.GetRequiredService<ToastService>();

    public StudioActionLog ActionLog => Services.GetRequiredService<StudioActionLog>();

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

    /// <summary>
    /// Says the studio was mapped with <c>AllowAnonymous()</c>, the way the startup guard finds out:
    /// by looking at a finished page endpoint's metadata.
    /// </summary>
    public StudioComponentContext ServedAnonymously()
    {
        RouteEndpointBuilder endpoint = new(
            static _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/marten"),
            order: 0);

        endpoint.Metadata.Add(new MartenStudioEndpointMarker("Marten Studio", "(remedies)", isPage: true));
        endpoint.Metadata.Add(new AllowAnonymousAttribute());

        MappedEndpoints.ObserveAnonymousPages([endpoint.Build()]);
        return this;
    }

    /// <summary>
    /// What the prerender scope would have handed the circuit's scope, restored before anything renders.
    /// </summary>
    /// <remarks>
    /// The circuit's DI scope is not the request's and has no <c>HttpContext</c>, so the cookie that
    /// themed the prerendered page is invisible to it; persisted component state is how the answer
    /// crosses. Restored through <see cref="ComponentStatePersistenceManager" /> rather than poked in,
    /// so the test exercises the same path the framework uses.
    /// </remarks>
    public StudioComponentContext WithPersistedState(string key, object value)
    {
        ComponentStatePersistenceManager manager = Services.GetRequiredService<ComponentStatePersistenceManager>();
        TestPersistentComponentStateStore store = new();
        store.Seed(key, value);
        manager.RestoreStateAsync(store).GetAwaiter().GetResult();
        return this;
    }
}
