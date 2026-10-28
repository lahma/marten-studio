using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Support;

/// <summary>
/// Issue #38, once for both studios: a host that already runs Blazor Server and tuned its circuit's hub options keeps
/// them when it registers a studio, in either order. Each studio's suite calls these with its own <c>Add*Studio()</c>,
/// so the one assertion holds both registrations (and, through them, the one shared <c>StudioCoreRegistration</c>).
/// </summary>
/// <remarks>
/// The scenarios name no studio type, so the file is compiled into both fast suites (PostgresStudio.Tests links it).
/// <c>ComponentHub</c> is internal to the framework: a host cannot name <c>HubOptions&lt;ComponentHub&gt;</c> either, which is
/// why the issue's host found the loss through a test that resolves it by reflection, as this does.
/// </remarks>
internal static class HostHubOptionsScenarios
{
    /// <summary>What the host sets: well off the defaults (30 s and 32 KB).</summary>
    public static readonly TimeSpan HostClientTimeout = TimeSpan.FromMinutes(10);

    /// <summary>What the host sets for the largest message a client may send.</summary>
    public const long HostMaximumReceiveMessageSize = 128 * 1024;

    private static readonly Type ComponentHub = typeof(CircuitOptions).Assembly
        .GetType("Microsoft.AspNetCore.Components.Server.ComponentHub", throwOnError: true)!;

    /// <summary>The call the issue's host makes, plus a protocol of its own after the framework's.</summary>
    public static void TuneLikeAHost(IServiceCollection services) =>
        services.AddServerSideBlazor().AddHubOptions(static o =>
        {
            o.ClientTimeoutInterval = HostClientTimeout;
            o.MaximumReceiveMessageSize = HostMaximumReceiveMessageSize;
            o.SupportedProtocols!.Add("json");
        });

    /// <summary>The circuit's hub options as a container built from <paramref name="services" /> resolves them.</summary>
    public static HubOptions ResolveCircuitHubOptions(IServiceCollection services)
    {
        Type holder = typeof(IOptions<>).MakeGenericType(typeof(HubOptions<>).MakeGenericType(ComponentHub));

        using ServiceProvider provider = services.BuildServiceProvider();
        object options = provider.GetRequiredService(holder);

        return (HubOptions)holder.GetProperty(nameof(IOptions<object>.Value))!.GetValue(options)!;
    }

    /// <summary>How many <c>HubOptionsSetup&lt;ComponentHub&gt;</c> the container holds: what the issue's duplicate is.</summary>
    public static int ComponentHubSetups(IServiceCollection services)
    {
        Type setup = typeof(HubOptionsSetup<>).MakeGenericType(ComponentHub);
        return services.Count(x => x.ImplementationType == setup);
    }

    /// <summary>The host registers Blazor Server and tunes it, then the studio: the case the issue is about.</summary>
    public static void The_host_registers_Blazor_first(Action<IServiceCollection> addStudio)
    {
        ServiceCollection services = new();
        TuneLikeAHost(services);
        addStudio(services);

        AssertTheHostsOptions(ResolveCircuitHubOptions(services));
        ComponentHubSetups(services).Should().Be(1, "the framework's setup is registered once, the host's, however often it is asked for");
    }

    /// <summary>The studio registers first and the host after it: the host's registration is last, so it wins by order.</summary>
    public static void The_host_registers_Blazor_after_the_studio(Action<IServiceCollection> addStudio)
    {
        ServiceCollection services = new();
        addStudio(services);
        TuneLikeAHost(services);

        AssertTheHostsOptions(ResolveCircuitHubOptions(services));
    }

    /// <summary>The studio registered twice, the second call after the host's tuning: still the host's options, still one setup.</summary>
    public static void Registering_the_studio_twice_keeps_the_hosts_options(Action<IServiceCollection> addStudio)
    {
        ServiceCollection services = new();
        TuneLikeAHost(services);
        addStudio(services);
        addStudio(services);

        AssertTheHostsOptions(ResolveCircuitHubOptions(services));
        ComponentHubSetups(services).Should().Be(1);
    }

    /// <summary>A host with no Blazor of its own gets the studio's circuit on the framework's defaults, set up once.</summary>
    public static void A_host_with_no_Blazor_gets_a_studio_on_the_defaults(Action<IServiceCollection> addStudio)
    {
        ServiceCollection services = new();
        addStudio(services);

        HubOptions options = ResolveCircuitHubOptions(services);

        (options.ClientTimeoutInterval ?? TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30), "null is the connection's 30 s");
        options.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(15));
        options.HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(15));
        options.MaximumReceiveMessageSize.Should().Be(32 * 1024);
        options.SupportedProtocols.Should().Equal("blazorpack");
        ComponentHubSetups(services).Should().Be(1);
    }

    /// <summary>
    /// The framework's own <c>AddRazorComponents().AddInteractiveServerComponents()</c> - what the studio called before the
    /// fix - after the same tuning: the host's options are gone. Without this the scenarios above would pass on a
    /// framework that no longer loses them, and say nothing.
    /// </summary>
    public static void The_unguarded_call_still_loses_them()
    {
        ServiceCollection services = new();
        TuneLikeAHost(services);
        services.AddRazorComponents().AddInteractiveServerComponents();

        ComponentHubSetups(services).Should().Be(2, "the framework registers its setup again, and that is the bug");

        HubOptions options = ResolveCircuitHubOptions(services);
        options.ClientTimeoutInterval.Should().NotBe(HostClientTimeout, "the second setup copied the global defaults over the host's");
        options.MaximumReceiveMessageSize.Should().Be(32 * 1024);
        options.SupportedProtocols.Should().Equal("blazorpack");
    }

    /// <summary>
    /// The same loss in another option the call repeats: <c>AddSignalR()</c> resets <c>WebSocketOptions.KeepAliveInterval</c>
    /// to zero, after a host that set it. The guard is not about hub options alone.
    /// </summary>
    public static void Other_options_the_call_repeats_keep_the_hosts_values(Action<IServiceCollection> addStudio)
    {
        ServiceCollection services = new();
        services.AddSignalR();
        services.Configure<WebSocketOptions>(static o => o.KeepAliveInterval = TimeSpan.FromSeconds(45));
        addStudio(services);

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<WebSocketOptions>>().Value.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(45));
    }

    private static void AssertTheHostsOptions(HubOptions options)
    {
        options.ClientTimeoutInterval.Should().Be(HostClientTimeout);
        options.MaximumReceiveMessageSize.Should().Be(HostMaximumReceiveMessageSize);

        // The framework selects blazorpack in a lambda it registers with every call; the host's addition after the first
        // one survives only if the second call's lambda, which clears the list, did not run after it.
        options.SupportedProtocols.Should().Equal("blazorpack", "json");
    }
}
