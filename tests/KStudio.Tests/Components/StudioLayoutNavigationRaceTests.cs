using Bunit;

using KStudio.Tests.Support;

using PostgresStudio.Components;
using PostgresStudio.Components.Layout;

namespace KStudio.Tests.Components;

/// <summary>
/// D34, D-SHELL 4.1: the host's scope gate is asked whenever a page of the host studio is on screen, however the visitor got there. These
/// tests render through <c>StudioRoutes</c>, as the studio does, because the race they hold is between the router's re-render of the page and
/// the layout's own location handler: the layout used to learn that a module's page had gone only from its handler, after the router had
/// already rendered the host's page body in the module's ungated layout.
/// </summary>
/// <remarks>
/// The host page is <c>Database › Connection</c>; its body reads <see cref="FakeConnectionInsightService.Requests" /> when it starts, so an
/// empty list says the body never ran.
/// </remarks>
public class StudioLayoutNavigationRaceTests
{
    private const string KafkaPage = "/postgres/kafka";

    private const string HostPath = "postgres/database/connection";

    private static KafkaStudioHarness Harness(string startAt)
    {
        KafkaStudioHarness harness = new(null, KafkaStudioHarness.Primary, KafkaStudioHarness.Secondary);
        harness.Context.Navigate(startAt);
        return harness;
    }

    private static async Task SettleAsync(IRenderedComponent<StudioRoutes> routes) => await routes.InvokeAsync(() => { });

    [Fact]
    public async Task Going_from_a_kafka_page_to_a_host_page_renders_no_host_body_while_the_scope_gate_is_pending()
    {
        using KafkaStudioHarness harness = Harness(KafkaPage);
        await harness.Context.ReadyAsync();
        harness.Context.Options.SourceAuthorizationPolicy = "host-source";

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForAssertion(() => routes.FindAll("#ms-cluster-select").Should().NotBeEmpty());
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        TaskCompletionSource<bool> gate = harness.Authorization.BlockHost();

        harness.Navigation.Simulate(HostPath, intercepted: true);
        await SettleAsync(routes);

        harness.Context.Connection.Requests.Should().BeEmpty("the host's body is not rendered before the host's scope gate has answered");
        routes.FindAll(".ms-loading-spinner").Should().NotBeEmpty("the layout waits for the gate");
        routes.FindAll("#ms-cluster-select").Should().BeEmpty("the page on screen is a host page now");

        gate.SetResult(false);

        routes.WaitForAssertion(() => routes.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        harness.Context.Connection.Requests.Should().BeEmpty("a scope the host's policy refuses is never read, however briefly");
    }

    [Fact]
    public async Task Going_from_a_kafka_page_to_a_host_page_the_policy_refuses_never_renders_the_host_body()
    {
        using KafkaStudioHarness harness = Harness(KafkaPage);
        await harness.Context.ReadyAsync();
        harness.Context.Options.SourceAuthorizationPolicy = "host-source";

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        harness.Authorization.AllowHost(static _ => false);

        harness.Navigation.Simulate(HostPath, intercepted: true);

        routes.WaitForAssertion(() => routes.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        harness.Context.Connection.Requests.Should().BeEmpty("the notice stands in the body's place from the first render, not after it");
    }

    [Fact]
    public async Task A_rail_click_to_a_host_page_during_the_catalog_load_is_gated_too()
    {
        using KafkaStudioHarness harness = Harness(KafkaPage);
        harness.Context.Options.SourceAuthorizationPolicy = "host-source";

        // The catalog load asks the host's policy for every store, so it is pending for as long as the gate is held. The layout has not yet
        // subscribed to the location while it waits: it used to see nothing of a click in that time.
        TaskCompletionSource<bool> gate = harness.Authorization.BlockHost();
        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForAssertion(() => routes.Find("h1").TextContent.Should().Be("Kafka topics"));

        harness.Navigation.Simulate(HostPath, intercepted: true);
        await SettleAsync(routes);

        harness.Context.Connection.Requests.Should().BeEmpty("the host's page is not rendered ungated because its click arrived during the load");
        routes.FindAll(".ms-loading-spinner").Should().NotBeEmpty();

        gate.SetResult(true);

        routes.WaitForAssertion(() => harness.Context.Connection.Requests.Should().NotBeEmpty("the gate answered yes, so the body runs"));
    }

    [Fact]
    public async Task Arriving_on_a_kafka_page_from_a_host_page_opens_the_cluster_the_address_names()
    {
        using KafkaStudioHarness harness = Harness(KafkaPage);
        await harness.Context.ReadyAsync();

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        routes.WaitForAssertion(() => harness.State.ActiveCluster.Should().Be(KafkaStudioHarness.Primary));
        harness.Navigation.Simulate(HostPath, intercepted: true);
        routes.WaitForAssertion(() => routes.FindAll("#ms-cluster-select").Should().BeEmpty());

        // A link on a host page to the second cluster. The state holds the first from before, and the shell must neither keep it nor rewrite
        // this address to it.
        harness.Navigation.Simulate("postgres/kafka?cluster=secondary", intercepted: true);

        routes.WaitForAssertion(() => harness.State.ActiveCluster.Should().Be(KafkaStudioHarness.Secondary));
        routes.WaitForAssertion(() => harness.Navigation.Uri.Should().EndWith("kafka?cluster=secondary"));
    }

    [Fact]
    public async Task Back_to_a_kafka_page_opens_the_cluster_its_address_names_and_not_the_one_the_state_last_held()
    {
        using KafkaStudioHarness harness = Harness(KafkaPage + "?cluster=secondary");
        await harness.Context.ReadyAsync();

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        routes.WaitForAssertion(() => harness.State.ActiveCluster.Should().Be(KafkaStudioHarness.Secondary));

        // The visitor picks the first cluster, leaves for a host page, and Back returns to the entry that named the second.
        await routes.InvokeAsync(() => harness.State.SetClusterAsync("primary"));
        harness.Navigation.Simulate(HostPath, intercepted: true);
        routes.WaitForAssertion(() => routes.FindAll("#ms-cluster-select").Should().BeEmpty());
        harness.Navigation.Simulate("postgres/kafka?cluster=secondary", intercepted: true);

        routes.WaitForAssertion(() => harness.State.ActiveCluster.Should().Be(KafkaStudioHarness.Secondary));
        routes.WaitForAssertion(() => harness.Navigation.Uri.Should().EndWith("kafka?cluster=secondary"));
    }
}
