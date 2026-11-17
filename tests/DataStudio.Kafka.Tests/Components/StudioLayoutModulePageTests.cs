using Bunit;

using DataStudio.Components.Layout;
using DataStudio.Components;
using DataStudio.Kafka.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Find;

namespace DataStudio.Kafka.Tests.Components;

/// <summary>
/// D34: on a module's page the layout draws the module's cluster selector and no Find box, skips the host's scope gate and the host's address
/// rewrite - keyed on the page the route table matches - and the host's scope waits, untouched, for the way back. Rendered through
/// <c>StudioRoutes</c>, as the studio renders its pages: the layout learns which page is on screen from the router's render.
/// </summary>
public class StudioLayoutModulePageTests
{
    /// <summary>A Find contributor that draws the box: the header has a scope and a kind to search.</summary>
    private sealed class AnyFind : IFindContributor
    {
        public int Order => 0;

        public string Noun => "Anything";

        public Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(FindContribution.NotApplicable);
    }

    private static KafkaStudioHarness Harness(Action<KafkaOptions>? configure = null) =>
        new(configure, extra: static services => services.AddSingleton<IFindContributor>(new AnyFind()), clusters: [KafkaStudioHarness.Primary, KafkaStudioHarness.Secondary]);

    private static async Task<IRenderedComponent<StudioRoutes>> RenderAtAsync(KafkaStudioHarness harness, string path)
    {
        await harness.Context.ReadyAsync();
        harness.Context.Navigate(path);

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        return routes;
    }

    private static string Heading(IRenderedComponent<StudioRoutes> routes)
    {
        var headings = routes.FindAll("h1");
        return headings.Count == 0 ? string.Empty : headings[0].TextContent.Trim();
    }

    [Fact]
    public async Task A_kafka_page_draws_the_cluster_selector_and_no_find_box()
    {
        using KafkaStudioHarness harness = Harness();

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/kafka");

        layout.WaitForAssertion(() => layout.FindAll("#ms-cluster-select option").Select(static x => x.TextContent.Trim()).Should().Equal("Primary", "Secondary"));
        layout.FindAll("#ms-store-select, #ms-database-select").Should().BeEmpty("the host's selector gives way to the module's");
        layout.FindAll(".ms-header-find").Should().BeEmpty("a Find contributor searches the host's scope, which a Kafka page is not about");
        layout.WaitForAssertion(() => Heading(layout).Should().Be("Kafka topics"));
    }

    [Fact]
    public async Task A_host_page_still_draws_the_find_box_and_not_the_cluster_selector()
    {
        using KafkaStudioHarness harness = Harness();

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/postgres/health");

        layout.FindAll(".ms-header-find").Should().NotBeEmpty();
        layout.FindAll("#ms-cluster-select").Should().BeEmpty();
    }

    [Fact]
    public async Task A_kafka_page_renders_under_a_store_policy_that_refuses_every_store()
    {
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();
        harness.Context.Options.SourceAuthorizationPolicy = "host-source";
        harness.Authorization.AllowHost(static _ => false);
        harness.Authorization.Calls.Clear();

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/kafka");

        layout.WaitForAssertion(() => Heading(layout).Should().Be("Kafka topics"));
        layout.FindAll(".ms-empty-title").Should().BeEmpty();
        harness.Authorization.HostCalls.Should().BeEmpty("the host's gate is not asked on a module's page");
    }

    /// <summary>
    /// Shell A2b: a module that has no <c>ScopeGate</c> (KStudio's) has its page's body drawn as it is - whatever the database's gate has
    /// answered or not, which on a Kafka page it never does. What a shell with no root, or a module with no gate under a root, should do is phase
    /// B's; this pins what it does today.
    /// </summary>
    [Fact]
    public async Task A_module_with_no_scope_gate_draws_its_page_body_ungated()
    {
        using KafkaStudioHarness harness = Harness();
        harness.Descriptor.Modules.Single(static x => x.Key == "kafka").ScopeGate.Should().BeNull("the premise: Data Studio's module has no gate");

        DatabaseScopeAccess access = harness.Context.Services.GetRequiredService<DatabaseScopeAccess>();
        access.Set(ScopeAccess.Denied);

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/kafka");

        layout.WaitForAssertion(() => Heading(layout).Should().Be("Kafka topics"));
        layout.FindAll(".ms-loading-spinner, .ms-empty-title").Should().BeEmpty("no gate drew a spinner or a refusal in front of the body");
        access.Value.Should().NotBe(ScopeAccess.Granted, "the database's host never answered on a module's page");
    }

    [Fact]
    public async Task A_host_page_with_the_same_policy_still_shows_ScopeNotAuthorized()
    {
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();
        harness.Context.Options.SourceAuthorizationPolicy = "host-source";
        harness.Authorization.AllowHost(static _ => false);

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/postgres/health");

        layout.WaitForAssertion(() => layout.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        Heading(layout).Should().NotBe("Activity", "the bypass is keyed on the page's assembly, not global");
    }

    [Fact]
    public async Task A_kafka_page_never_writes_store_db_or_tenant_into_the_address()
    {
        using KafkaStudioHarness harness = Harness();

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/kafka");

        layout.WaitForAssertion(() => harness.Context.CurrentUri.Should().Contain("cluster=primary"));
        harness.Context.CurrentUri.Should().NotContain("store=").And.NotContain("db=").And.NotContain("tenant=");
    }

    [Fact]
    public async Task An_address_naming_a_host_scope_on_a_kafka_page_does_not_move_it()
    {
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();
        StudioScope? before = harness.Context.State.ActiveScope;

        await RenderAtAsync(harness, "/studio/kafka?store=other&db=other&tenant=x");

        harness.Context.State.ActiveScope.Should().Be(before);
    }

    [Fact]
    public async Task Leaving_a_kafka_page_restores_the_host_scope_in_the_address_and_asks_the_gate_again()
    {
        using KafkaStudioHarness harness = Harness();
        await harness.Context.ReadyAsync();
        harness.Context.Options.SourceAuthorizationPolicy = "host-source";
        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/kafka");
        StudioScope? before = harness.Context.State.ActiveScope;
        harness.Authorization.HostCalls.Should().BeEmpty("the premise: the host's gate was not asked on the module's page");

        harness.Navigation.Simulate("studio/postgres/health", intercepted: true);

        layout.WaitForAssertion(() => harness.Context.CurrentUri.Should().Contain("store=default"));
        harness.Context.State.ActiveScope.Should().Be(before, "the host's scope waited in memory");
        layout.WaitForAssertion(() => Heading(layout).Should().Be("Health"));
        harness.Authorization.HostCalls.Should().NotBeEmpty("the host's scope gate is asked again on the way back");
    }

    [Fact]
    public async Task A_click_from_a_host_page_to_a_kafka_page_does_not_move_the_host_scope()
    {
        using KafkaStudioHarness harness = Harness();
        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/postgres/health");
        StudioScope? before = harness.Context.State.ActiveScope;

        harness.Navigation.Simulate("studio/kafka?store=elsewhere&tenant=t", intercepted: true);

        layout.WaitForAssertion(() => layout.FindAll("#ms-cluster-select").Should().NotBeEmpty());
        harness.Context.State.ActiveScope.Should().Be(before);
    }
}
