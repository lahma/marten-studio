using Bunit;

using DataStudio.Components.Layout;
using DataStudio.Components;
using DataStudio.Kafka.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Tests.Components;

/// <summary>
/// D34, D-SHELL 4.1: a module's scope gate is asked whenever a page of that module is on screen, however the visitor got there. These
/// tests render through <c>StudioRoutes</c>, as the studio does, because the race they hold is between the router's re-render of the page and
/// the layout's own location handler: the layout used to learn that a module's page had gone only from its handler, after the router had
/// already rendered the other module's page body in the Kafka module's ungated layout.
/// </summary>
/// <remarks>
/// The other module is <see cref="FakeHostModule" />, which this suite defines (a Kafka-only host has no database module): its page counts its bodies
/// in <see cref="FakeHostProbe" /> when it starts, so a count of zero says the body never ran.
/// </remarks>
public class StudioLayoutNavigationRaceTests
{
    private const string KafkaPage = "/studio/kafka";

    private const string HostPath = "studio/host/page";

    private static KafkaHarness Harness(string startAt)
    {
        KafkaHarness harness = new(null, null, [KafkaHarness.Primary, KafkaHarness.Secondary], withHostModule: true);
        harness.Context.Navigate(startAt);
        return harness;
    }

    private static int Bodies(KafkaHarness harness) => harness.Context.Services.GetRequiredService<FakeHostProbe>().Bodies;

    private static async Task SettleAsync(IRenderedComponent<StudioRoutes> routes) => await routes.InvokeAsync(() => { });

    [Fact]
    public async Task Going_from_a_kafka_page_to_a_host_page_renders_no_host_body_while_the_scope_gate_is_pending()
    {
        using KafkaHarness harness = Harness(KafkaPage);
        await harness.Context.ReadyAsync();

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForAssertion(() => routes.FindAll("#ms-cluster-select").Should().NotBeEmpty());
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        TaskCompletionSource<bool> gate = harness.Authorization.BlockHost();

        harness.Navigation.Simulate(HostPath, intercepted: true);
        await SettleAsync(routes);

        Bodies(harness).Should().Be(0, "the host's body is not rendered before the host's scope gate has answered");
        routes.FindAll(".ms-loading-spinner").Should().NotBeEmpty("the layout waits for the gate");
        routes.FindAll("#ms-cluster-select").Should().BeEmpty("the page on screen is a host page now");

        gate.SetResult(false);

        routes.WaitForAssertion(() => routes.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        Bodies(harness).Should().Be(0, "a scope the host's policy refuses is never read, however briefly");
    }

    [Fact]
    public async Task Going_from_a_kafka_page_to_a_host_page_the_policy_refuses_never_renders_the_host_body()
    {
        using KafkaHarness harness = Harness(KafkaPage);
        await harness.Context.ReadyAsync();

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        harness.Authorization.AllowHost(static _ => false);

        harness.Navigation.Simulate(HostPath, intercepted: true);

        routes.WaitForAssertion(() => routes.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        Bodies(harness).Should().Be(0, "the notice stands in the body's place from the first render, not after it");
    }

    /// <summary>
    /// The other half of the gate: once it has answered yes, the page's body runs. The catalog-load case belongs to the database module's own suite, which has
    /// a catalog to hold (this module's gate is a test's and has none); here the gate is held by the policy it asks.
    /// </summary>
    [Fact]
    public async Task A_host_page_runs_its_body_once_the_scope_gate_has_answered_yes()
    {
        using KafkaHarness harness = Harness(KafkaPage);
        await harness.Context.ReadyAsync();
        TaskCompletionSource<bool> gate = harness.Authorization.BlockHost();

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForAssertion(() => routes.Find("h1").TextContent.Should().Be("Kafka topics"));

        harness.Navigation.Simulate(HostPath, intercepted: true);
        await SettleAsync(routes);

        Bodies(harness).Should().Be(0, "the host's page is not rendered ungated while its gate has not answered");
        routes.FindAll(".ms-loading-spinner").Should().NotBeEmpty();

        gate.SetResult(true);

        routes.WaitForAssertion(() => Bodies(harness).Should().BeGreaterThan(0, "the gate answered yes, so the body runs"));
    }

    [Fact]
    public async Task Arriving_on_a_kafka_page_from_a_host_page_opens_the_cluster_the_address_names()
    {
        using KafkaHarness harness = Harness(KafkaPage);
        await harness.Context.ReadyAsync();

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        routes.WaitForAssertion(() => harness.State.ActiveCluster.Should().Be(KafkaHarness.Primary));
        harness.Navigation.Simulate(HostPath, intercepted: true);
        routes.WaitForAssertion(() => routes.FindAll("#ms-cluster-select").Should().BeEmpty());

        // A link on a host page to the second cluster. The state holds the first from before, and the shell must neither keep it nor rewrite
        // this address to it.
        harness.Navigation.Simulate("studio/kafka?cluster=secondary", intercepted: true);

        routes.WaitForAssertion(() => harness.State.ActiveCluster.Should().Be(KafkaHarness.Secondary));
        routes.WaitForAssertion(() => harness.Navigation.Uri.Should().EndWith("kafka?cluster=secondary"));
    }

    [Fact]
    public async Task Back_to_a_kafka_page_opens_the_cluster_its_address_names_and_not_the_one_the_state_last_held()
    {
        using KafkaHarness harness = Harness(KafkaPage + "?cluster=secondary");
        await harness.Context.ReadyAsync();

        IRenderedComponent<StudioRoutes> routes = harness.Context.Render<StudioRoutes>();
        routes.WaitForState(() => routes.FindComponent<StudioLayout>().Instance.PreferencesLoaded);
        routes.WaitForAssertion(() => harness.State.ActiveCluster.Should().Be(KafkaHarness.Secondary));

        // The visitor picks the first cluster, leaves for a host page, and Back returns to the entry that named the second.
        await routes.InvokeAsync(() => harness.State.SetClusterAsync("primary"));
        harness.Navigation.Simulate(HostPath, intercepted: true);
        routes.WaitForAssertion(() => routes.FindAll("#ms-cluster-select").Should().BeEmpty());
        harness.Navigation.Simulate("studio/kafka?cluster=secondary", intercepted: true);

        routes.WaitForAssertion(() => harness.State.ActiveCluster.Should().Be(KafkaHarness.Secondary));
        routes.WaitForAssertion(() => harness.Navigation.Uri.Should().EndWith("kafka?cluster=secondary"));
    }
}
