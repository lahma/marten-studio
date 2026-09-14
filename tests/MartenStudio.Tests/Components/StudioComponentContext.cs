using Bunit;

using MartenStudio.Internal;
using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Events;
using MartenStudio.Tests.Projections;
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
/// The data sources are hand-written fakes - <see cref="FakeStudioScopeCatalog" /> for the header's
/// listings, <see cref="FakeStoreInfoService" /> for the Overview's store facts, and the events,
/// projections and nav-indicator fakes that the shell itself now reaches for (the Overview's tiles and
/// the sidebar's badges). Everything else is the real service, because those are the ones the components
/// actually talk to and they are cheap. bUnit supplies the navigation manager and the JavaScript runtime.
/// An area with more of its own to add either passes a <c>configure</c> action or derives, as
/// <c>DocumentsComponentContext</c> does - there is one context class, not one per packet.
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
        EventData = new FakeEventDataService();
        ProjectionData = new FakeProjectionDataService();
        NavBadges = new FakeNavIndicatorService();
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

        // The area data services, faked. They are here rather than in a context of their own because
        // every one of them is now reachable from the shell: the Overview reads events and projections
        // for its tiles, and the sidebar reads both for its badges, so a layout test that did not have
        // them would fail on a component it is not about.
        Services.AddSingleton<IEventDataService>(EventData);
        Services.AddSingleton<IProjectionDataService>(ProjectionData);
        Services.AddSingleton<INavIndicatorService>(NavBadges);

        // Every live page builds its own loop from this, so the circuit's container never tracks one.
        Services.AddScoped<StudioLiveUpdatesFactory>();

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

    /// <summary>What the Overview page is given about the stores this application registered.</summary>
    public FakeStoreInfoService StoreInfo { get; }

    /// <summary>
    /// What the Events pages - and the Overview's event tiles - are given.
    /// </summary>
    /// <remarks>
    /// Named for its area rather than <c>Data</c>, because <c>DocumentsComponentContext</c> derives from
    /// this class and already has a <c>Data</c> of its own; two areas sharing one name on a base class is
    /// how a subclass silently starts hiding the wrong fake.
    /// </remarks>
    public FakeEventDataService EventData { get; }

    /// <summary>What the projections page - and the Overview's projection tiles - are given.</summary>
    public FakeProjectionDataService ProjectionData { get; }

    /// <summary>What the sidebar's badges are told.</summary>
    public FakeNavIndicatorService NavBadges { get; }

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

    /// <summary>Turns the master switch on, which turns every capability off however they were set.</summary>
    public StudioComponentContext WithReadOnly()
    {
        Options.Capabilities = MartenStudioCapabilities.All();
        Options.ReadOnly = true;
        return this;
    }

    /// <summary>
    /// Settles an active scope, the way the layout does on a real circuit.
    /// </summary>
    /// <remarks>
    /// A page rendered on its own never runs the layout, and every page that reads anything reads
    /// <see cref="StudioState.ActiveScope" /> first - so without this it renders its "nothing selected"
    /// state and the test is about nothing.
    /// </remarks>
    /// <param name="storeKey">The store to settle on.</param>
    public async Task<StudioComponentContext> ReadyAsync(string storeKey = "default")
    {
        Catalog.WithStore(storeKey, storeKey == "default" ? "Default" : storeKey, databaseIdentities: "localhost.marten");

        await State.EnsureInitializedAsync();

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
