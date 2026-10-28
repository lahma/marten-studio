using Microsoft.Extensions.DependencyInjection;

using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Services;

/// <summary>
/// Issue #38 for <c>AddMartenStudio()</c>: a host's own server-side Blazor registration, and the hub options it tuned,
/// survive the studio's registration in either order. The scenarios are shared with PostgresStudio's suite
/// (<see cref="HostHubOptionsScenarios" />), which holds the registration both studios share to the same assertions.
/// </summary>
public class HostHubOptionsTests
{
    private static void AddStudio(IServiceCollection services) => services.AddMartenStudio();

    [Fact]
    public void The_hosts_hub_options_survive_a_studio_registered_after_its_Blazor() =>
        HostHubOptionsScenarios.The_host_registers_Blazor_first(AddStudio);

    [Fact]
    public void The_hosts_hub_options_survive_a_studio_registered_before_its_Blazor() =>
        HostHubOptionsScenarios.The_host_registers_Blazor_after_the_studio(AddStudio);

    [Fact]
    public void Registering_the_studio_twice_does_not_reset_them() =>
        HostHubOptionsScenarios.Registering_the_studio_twice_keeps_the_hosts_options(AddStudio);

    [Fact]
    public void A_host_with_no_Blazor_of_its_own_gets_the_defaults_and_one_setup() =>
        HostHubOptionsScenarios.A_host_with_no_Blazor_gets_a_studio_on_the_defaults(AddStudio);

    [Fact]
    public void Other_options_the_registration_repeats_keep_the_hosts_values() =>
        HostHubOptionsScenarios.Other_options_the_call_repeats_keep_the_hosts_values(AddStudio);

    /// <summary>Anti-vacuity: the framework call the studio used to make does lose the host's options on this ASP.NET Core.</summary>
    [Fact]
    public void Without_the_guard_the_same_host_loses_them() =>
        HostHubOptionsScenarios.The_unguarded_call_still_loses_them();
}
