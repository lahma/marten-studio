using Bunit;

using MartenStudio.Services;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// A bUnit context for the projections screen: every service it injects, with the data service faked.
/// </summary>
/// <remarks>
/// Its own context rather than an addition to <c>StudioComponentContext</c>, for a mechanical reason -
/// bUnit's service provider is built the first time anything resolves from it, and that context resolves
/// several services in its own constructor, so a test cannot add one afterwards. Duplicating a dozen
/// registrations is cheaper than making the shared context take a parameter for every page.
/// </remarks>
internal sealed class ProjectionsTestContext : BunitContext
{
    public ProjectionsTestContext()
    {
        Options = new MartenStudioOptions
        {
            // The floor the options validation allows, so a test never waits five seconds for a tick it
            // does not want anyway.
            RefreshInterval = TimeSpan.FromSeconds(1)
        };

        ProjectionData = new FakeProjectionDataService();
        Catalog = new FakeStudioScopeCatalog().WithStore("default", "Default", databaseIdentities: "localhost.marten");

        // The page reaches for martenStudio.visibility.watch, which no test is about.
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        Services.AddSingleton<IOptions<MartenStudioOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        Services.AddSingleton<IStudioScopeCatalog>(Catalog);
        Services.AddSingleton<IAuthorizationService>(new TestStoreAuthorizationService());
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider());
        Services.AddSingleton<StudioAuthorization>();
        Services.AddSingleton<StudioState>();
        Services.AddSingleton<StudioCapabilityGuard>();
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<StudioActionLogService>();
        Services.AddSingleton<StudioActionLog>();
        Services.AddSingleton<IProjectionDataService>(ProjectionData);

        // The page builds its own polling loop from this, so the circuit's container never tracks one.
        Services.AddScoped<StudioLiveUpdatesFactory>();

        State = Services.GetRequiredService<StudioState>();
        State.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;
        Toasts = Services.GetRequiredService<ToastService>();
    }

    /// <summary>The options an application would have configured.</summary>
    public MartenStudioOptions Options { get; }

    /// <summary>What the page is given.</summary>
    public FakeProjectionDataService ProjectionData { get; }

    /// <summary>What the header is offered, which is also what settles the active scope.</summary>
    public FakeStudioScopeCatalog Catalog { get; }

    /// <summary>The circuit's scope state.</summary>
    public StudioState State { get; }

    /// <summary>The toasts an action raised.</summary>
    public ToastService Toasts { get; }

    /// <summary>Enables every mutating capability, the way a host opts in with one line.</summary>
    public ProjectionsTestContext WithAllCapabilities()
    {
        Options.Capabilities = MartenStudioCapabilities.All();
        return this;
    }

    /// <summary>Turns the master switch on, which turns every capability off.</summary>
    public ProjectionsTestContext WithReadOnly()
    {
        Options.Capabilities = MartenStudioCapabilities.All();
        Options.ReadOnly = true;
        return this;
    }

    /// <summary>Settles the active scope, which the page needs before it reads anything.</summary>
    public async Task<ProjectionsTestContext> ReadyAsync()
    {
        await State.EnsureInitializedAsync();
        return this;
    }
}
