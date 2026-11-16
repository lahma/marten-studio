using Bunit;

using DataStudio.Components;
using DataStudio.Components.Layout;
using DataStudio.Kafka.Tests.Support;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataStudio.Kafka.Tests.Components;

/// <summary>
/// Decision 8 of v2 step 4, on the shell's components: a shell whose only module keeps its own segment (Kafka) has no root module. Its front
/// address leads to the module's home on the circuit as the mapping does for a document load, its rail ends with the shell's own System section
/// holding Activity, and the capability chip lists the module's capabilities and nothing else. The module is the real one over the harness's fakes;
/// the database module the harness registers is taken out.
/// </summary>
public class KafkaOnlyShellTests
{
    private static KafkaStudioHarness Harness() => new(
        configure: null,
        extra: static services =>
        {
            services.RemoveAll<IStudioModule>();
            services.AddSingleton<IStudioModule, DataStudio.Kafka.Services.KafkaStudioModule>();
        },
        clusters: [KafkaStudioHarness.Primary]);

    [Fact]
    public async Task The_shell_has_no_root_and_the_first_module_is_Kafkas()
    {
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();

        StudioShellDescriptor descriptor = harness.Context.Services.GetRequiredService<StudioShellDescriptor>();

        descriptor.Root.Should().BeNull();
        descriptor.First!.Key.Should().Be("kafka");
        descriptor.Navigation.Select(static x => x.Title).Should().Equal("Kafka", "System");
    }

    [Theory]
    [InlineData("http://localhost/studio", "http://localhost/kafka")]
    [InlineData("http://localhost/studio/", "http://localhost/kafka")]
    [InlineData("http://localhost/studio?cluster=primary", "http://localhost/kafka?cluster=primary")]
    public async Task The_front_address_is_replaced_by_the_first_modules_home_with_the_query_it_came_with(string front, string home)
    {
        // The home is the module's route relative to the document base: the harness's base is the host root, where a circuit's is the mount.
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();
        harness.Navigation.Simulate(front, intercepted: false);
        harness.Navigation.Requests.Clear();

        _ = harness.Context.Render<StudioRoutes>();

        (string Uri, Microsoft.AspNetCore.Components.NavigationOptions Options) request = harness.Navigation.Requests.Should().ContainSingle().Which;
        request.Uri.Should().Be(home);
        request.Options.ReplaceHistoryEntry.Should().BeTrue("the front address is not a page to go Back to");
        request.Options.ForceLoad.Should().BeFalse();
    }

    [Fact]
    public async Task A_navigation_to_the_front_address_on_the_circuit_leads_to_the_home_too()
    {
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();
        harness.Navigation.Simulate("http://localhost/studio/kafka", intercepted: false);
        _ = harness.Context.Render<StudioRoutes>();
        harness.Navigation.Requests.Clear();

        harness.Navigation.Simulate("http://localhost/studio", intercepted: true);

        harness.Navigation.Requests.Should().ContainSingle().Which.Uri.Should().Be("http://localhost/kafka", "relative to the harness's base, the host root");
    }

    [Fact]
    public async Task Any_other_address_the_router_does_not_know_is_still_not_found_and_is_not_redirected()
    {
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();
        harness.Navigation.Simulate("http://localhost/studio/no-such-page", intercepted: false);
        harness.Navigation.Requests.Clear();

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();

        harness.Navigation.Requests.Should().BeEmpty();
        routes.Find("h1").TextContent.Should().Be("Not found");
    }

    [Fact]
    public async Task The_rail_closes_with_the_shells_System_section_holding_Activity_and_names_no_database_page()
    {
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();

        IRenderedComponent<NavMenu> rail = harness.Context.Render<NavMenu>();

        rail.FindAll(".ms-sidebar-section").Select(static x => x.TextContent.Trim()).Should().Equal("Kafka", "System");
        rail.FindAll("a.ms-nav-link .ms-nav-link-text").Select(static x => x.TextContent.Trim())
            .Should().Equal("Topics", "Cluster settings", "Activity");
        rail.FindAll("a.ms-nav-link").Select(static x => x.GetAttribute("href")).Should().Contain("activity");
    }

    [Fact]
    public async Task The_capability_chip_lists_the_modules_capabilities_and_no_database_group()
    {
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();

        IRenderedComponent<CapabilityBanner> chip = harness.Context.Render<CapabilityBanner>();

        chip.FindAll(".ms-popover-title").Select(static x => x.TextContent.Trim()).Should().ContainSingle()
            .Which.Should().Contain("Kafka");
        chip.FindAll(".ms-capability-name").Select(static x => x.TextContent.Trim())
            .Should().Equal("SearchRecords", "ReadPayloads", "ExportRecords", "LiveTail");
    }
}
