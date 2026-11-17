using Bunit;

using DataStudio.Components.Layout;
using DataStudio.Components;
using DataStudio.Kafka.Tests.Support;

namespace DataStudio.Kafka.Tests.Components;

/// <summary>
/// D34: on a module's page the layout draws the module's cluster selector and not another module's header search, skips another module's scope gate and
/// its address handling - keyed on the page the route table matches. Rendered through <c>StudioRoutes</c>, as the studio renders its pages: the layout
/// learns which page is on screen from the router's render. The other module is <see cref="FakeHostModule" />, which this suite defines (a Kafka-only
/// host has no database module); what the database module's own scope does beside Kafka is not testable here and is the Postgres suite's.
/// </summary>
public class StudioLayoutModulePageTests
{
    private static KafkaHarness Harness(bool withHostModule, Action<KafkaOptions>? configure = null) =>
        new(configure, extra: null, clusters: [KafkaHarness.Primary, KafkaHarness.Secondary], withHostModule: withHostModule);

    private static async Task<IRenderedComponent<StudioRoutes>> RenderAtAsync(KafkaHarness harness, string path)
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
        using KafkaHarness harness = Harness(withHostModule: true);

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/kafka");

        layout.WaitForAssertion(() => layout.FindAll("#ms-cluster-select option").Select(static x => x.TextContent.Trim()).Should().Equal("Primary", "Secondary"));
        layout.FindAll(".ms-header-find").Should().BeEmpty("another module's search is about that module's scope, which a Kafka page is not about");
        layout.WaitForAssertion(() => Heading(layout).Should().Be("Kafka topics"));
    }

    [Fact]
    public async Task A_kafka_only_host_draws_no_find_box_on_a_kafka_page()
    {
        using KafkaHarness harness = Harness(withHostModule: false);

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/kafka");

        layout.WaitForAssertion(() => Heading(layout).Should().Be("Kafka topics"));
        layout.FindAll(".ms-header-find").Should().BeEmpty("the Kafka module has no header search, and no other module is composed");
    }

    [Fact]
    public async Task A_page_of_the_other_module_still_draws_its_search_and_not_the_cluster_selector()
    {
        using KafkaHarness harness = Harness(withHostModule: true);

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/" + FakeHostModule.PageAddress);

        layout.WaitForAssertion(() => layout.FindAll(".ms-header-find").Should().NotBeEmpty());
        layout.FindAll("#ms-cluster-select").Should().BeEmpty();
    }

    [Fact]
    public async Task A_kafka_page_renders_under_a_policy_that_refuses_the_other_modules_scope()
    {
        using KafkaHarness harness = Harness(withHostModule: true);
        await harness.Context.ReadyAsync();
        harness.Authorization.AllowHost(static _ => false);
        harness.Authorization.Calls.Clear();

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/kafka");

        layout.WaitForAssertion(() => Heading(layout).Should().Be("Kafka topics"));
        layout.FindAll(".ms-empty-title").Should().BeEmpty();
        harness.Authorization.HostCalls.Should().BeEmpty("another module's gate is not asked on a module's page");
    }

    /// <summary>
    /// Shell A2b: a module that has no <c>ScopeGate</c> (the Kafka module) has its page's body drawn as it is - whatever another module's gate has
    /// answered or not, which on a Kafka page it never does.
    /// </summary>
    [Fact]
    public async Task A_module_with_no_scope_gate_draws_its_page_body_ungated()
    {
        using KafkaHarness harness = Harness(withHostModule: true);
        harness.Descriptor.Modules.Single(static x => x.Key == "kafka").ScopeGate.Should().BeNull("the premise: Data Studio's Kafka module has no gate");
        harness.Authorization.AllowHost(static _ => false);

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/kafka");

        layout.WaitForAssertion(() => Heading(layout).Should().Be("Kafka topics"));
        layout.FindAll(".ms-loading-spinner, .ms-empty-title").Should().BeEmpty("no gate drew a spinner or a refusal in front of the body");
    }

    [Fact]
    public async Task A_page_of_the_other_module_with_the_same_policy_still_shows_its_refusal()
    {
        using KafkaHarness harness = Harness(withHostModule: true);
        await harness.Context.ReadyAsync();
        harness.Authorization.AllowHost(static _ => false);

        IRenderedComponent<StudioRoutes> layout = await RenderAtAsync(harness, "/studio/" + FakeHostModule.PageAddress);

        layout.WaitForAssertion(() => layout.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        Heading(layout).Should().NotBe("Kafka topics", "the bypass is keyed on the page's assembly, not global");
    }

    [Fact]
    public async Task A_kafka_page_never_writes_store_db_or_tenant_into_the_address()
    {
        using KafkaHarness harness = Harness(withHostModule: false);

        await RenderAtAsync(harness, "/studio/kafka");

        harness.Context.CurrentUri.Should().Contain("cluster=primary");
        harness.Context.CurrentUri.Should().NotContain("store=").And.NotContain("db=").And.NotContain("tenant=");
    }

    [Fact]
    public async Task An_address_naming_another_modules_scope_on_a_kafka_page_is_left_alone()
    {
        using KafkaHarness harness = Harness(withHostModule: true);

        await RenderAtAsync(harness, "/studio/kafka?store=other&db=other&tenant=x");

        harness.Context.CurrentUri.Should().Contain("store=other&db=other&tenant=x", "the shell does not rewrite an address it is not about");
    }
}
