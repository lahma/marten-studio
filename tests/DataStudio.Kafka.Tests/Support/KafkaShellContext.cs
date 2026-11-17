using Bunit;

using DataStudio.Marten.Tests.Support;
using DataStudio.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataStudio.Kafka.Tests.Support;

/// <summary>
/// A bUnit context for a host that added the shell and the Kafka module and nothing else: the container is the one
/// <c>AddDataStudio().AddKafka()</c> leaves, so every shell and module service is the real registration, with the engine's and the visitor's seams
/// replaced by the doubles a test names. There is no database module here, no Npgsql and no Postgres assembly: this suite's output folder holds none,
/// which <c>KafkaOnlyHostTests</c> proves.
/// </summary>
internal sealed class KafkaShellContext : BunitContext
{
    /// <summary>The shell's options, as a host's <c>AddDataStudio(options => ...)</c> would set them; read live, so a test may set one after the context is built.</summary>
    public DataStudioOptions ShellOptions { get; } = new();

    /// <summary>The circuit's visitor.</summary>
    public TestAuthenticationStateProvider AuthenticationState { get; } = new();

    /// <summary>The module's options, which the module's own services read live.</summary>
    public KafkaOptions Options { get; }

    /// <summary>
    /// Builds the context. <paramref name="options" /> is the module's options object; <paramref name="replaceServices" /> runs after the real
    /// registration and the shell's doubles, so it may replace any service.
    /// </summary>
    /// <param name="options">The module's options, as the test configured them.</param>
    /// <param name="withHostModule">Whether <see cref="FakeHostModule" /> is added before the Kafka module.</param>
    /// <param name="replaceServices">Replaces services in the container the real registration built.</param>
    public KafkaShellContext(KafkaOptions options, bool withHostModule, Action<IServiceCollection> replaceServices)
    {
        Options = options;
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));

        // What bUnit itself registered (its JS runtime, its navigation manager, its component activator): the framework registration the shell makes
        // would replace each with the server's own, and the test's own doubles are the ones that must answer.
        Dictionary<Type, ServiceDescriptor> bunit = Services
            .GroupBy(static x => x.ServiceType)
            .Where(static x => !x.Key.IsGenericTypeDefinition)
            .ToDictionary(static x => x.Key, static x => x.Last());

        // What a host's container holds before the studio is added and the shell's registration (the framework's Razor components) reads.
        WebApplicationBuilder host = WebApplication.CreateBuilder();
        Services.AddSingleton<IConfiguration>(host.Configuration);
        Services.AddSingleton<IHostEnvironment>(host.Environment);
        Services.AddSingleton<IWebHostEnvironment>(host.Environment);

        // The real registration: the shell's services, the module and its engine.
        DataStudioBuilder studio = Services.AddDataStudio();

        if (withHostModule)
        {
            studio.AddFakeHost();
        }

        studio.AddKafka();

        foreach (ServiceDescriptor descriptor in bunit.Values)
        {
            Services.Add(descriptor);
        }

        // The circuit is the one root here: what the shell scopes to a circuit is one instance of the context.
        Services.AddSingleton<StudioActivity>();
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<StudioLiveUpdatesFactory>();

        // The options objects the test holds, read live by everything that asks for them.
        Services.AddSingleton<IOptions<DataStudioOptions>>(Microsoft.Extensions.Options.Options.Create(ShellOptions));
        Services.AddSingleton<IOptions<KafkaOptions>>(Microsoft.Extensions.Options.Options.Create(options));

        // The visitor.
        Services.AddSingleton<AuthenticationStateProvider>(AuthenticationState);

        // The circuit's clock and zone: UTC, so that a time on a page reads the same wherever the suite runs.
        Services.AddSingleton(static provider =>
        {
            StudioPreferences preferences = ActivatorUtilities.CreateInstance<StudioPreferences>(provider);
            preferences.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;
            return preferences;
        });

        // The circuit's own state: one root is the one circuit here, so what the real registration scopes is a singleton of the context.
        Services.AddSingleton<ComponentStatePersistenceManager>();
        Services.AddSingleton(static provider => provider.GetRequiredService<ComponentStatePersistenceManager>().State);

        replaceServices(Services);
    }

    /// <summary>The module's cluster state, settled: the circuit has asked for its clusters once.</summary>
    public async Task<KafkaShellContext> ReadyAsync()
    {
        await Services.GetRequiredService<DataStudio.Kafka.Services.KafkaState>().EnsureInitializedAsync();
        return this;
    }

    /// <summary>Moves the circuit's address.</summary>
    /// <param name="relativeUri">An address relative to the base, or an absolute one.</param>
    public KafkaShellContext Navigate(string relativeUri)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(relativeUri);
        return this;
    }

    /// <summary>The circuit's address.</summary>
    public string CurrentUri => Services.GetRequiredService<NavigationManager>().Uri;

    /// <summary>The ring's neutral way in and out: what the Activity page reads through.</summary>
    public StudioActivity ActivityRing => Services.GetRequiredService<StudioActivity>();

    /// <summary>The circuit's preferences (theme, time zone, clock), as the components see them.</summary>
    public StudioPreferences Preferences => Services.GetRequiredService<StudioPreferences>();

    /// <summary>The shell's ring of recent actions.</summary>
    public StudioActionLogService ActionLog => Services.GetRequiredService<StudioActionLogService>();
}
