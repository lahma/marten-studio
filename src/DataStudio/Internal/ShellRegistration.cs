using DataStudio.Services;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DataStudio.Internal;

/// <summary>
/// What a host that adds the shell gets, registered by <c>AddDataStudio()</c>: the shell's settings, the composed descriptor, the Blazor
/// registration, the startup authorization guard, the preferences and the Activity ring, toasts and the live-update loop. Everything is a
/// <c>TryAdd</c>, so an application that registered its own first is the one that answers, and calling it twice registers one shell.
/// </summary>
internal static class ShellRegistration
{
    /// <summary>Registers the shell's services.</summary>
    /// <param name="services">The application's service collection.</param>
    public static void Add(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The shell's own settings: one object, read live, over the shell's options (a module's settings answer its shell members from it).
        services.TryAddSingleton<IShellSettings, ShellSettings>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DataStudioOptions>, DataStudioOptionsValidator>());
        services.TryAddSingleton<ShellRegistrationMarker>();

        // The shell's descriptor: every registered module composed, when first resolved (D34), after every Add*(), so registration is
        // order-independent.
        services.TryAddSingleton(static provider => StudioModuleComposition.Compose(provider.GetServices<IStudioModule>()));

        // Refuses to start an application whose mapped studio nothing authorizes. Registered here rather
        // than at the map site, because a hosted service added to a built application is too late.
        services.TryAddSingleton<StudioMappedEndpoints>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, StudioEndpointAuthorizationGuard>());

        // Not a bare AddRazorComponents().AddInteractiveServerComponents(): called after a host's own AddServerSideBlazor()
        // that would put the framework's hub-options setup in the container twice, the second after the host's
        // Configure<HubOptions<ComponentHub>>, and reset the host's circuit options to the defaults (issue #38).
        ServerSideBlazorRegistration.Add(services);

        // Deliberately no AddSignalR().AddJsonProtocol(...) and no JsonStringEnumConverter: those options
        // belong to the whole application's SignalR and its own hubs must keep their own format
        // (there is no studio hub in v1 - plan D10).
        services.AddHttpContextAccessor();

        // The ring of recent actions is the process's; who is driving a circuit, what it prefers and what it has been told are the circuit's.
        services.TryAddSingleton<StudioActionLogService>();
        services.TryAddScoped<StudioPreferences>();
        services.TryAddScoped<StudioActivity>();
        services.TryAddScoped<ToastService>();

        // The words the live-update loop logs in: the shell's own, unless a module registered its own (it replaces this one, in either order).
        services.TryAddSingleton(static provider => ShellLiveLog.Create(provider.GetService<TimeProvider>() ?? TimeProvider.System));

        // A factory rather than the loop itself: StudioLiveUpdates is IAsyncDisposable, and a transient
        // registration would leave the circuit's container tracking one instance per page visit for the
        // life of the circuit. The page owns the loop it creates.
        services.TryAddScoped<StudioLiveUpdatesFactory>();
    }
}

/// <summary>Says that <c>AddDataStudio()</c> ran in this container, so that a map call without it can name the call that is missing.</summary>
internal sealed class ShellRegistrationMarker;
