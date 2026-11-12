using Bunit;

using KStudio.Components.Pages;
using KStudio.Core.Clusters;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Components;

/// <summary>
/// What a cluster source told KStudio, shown (decision 5 of D-CORR, UI-E): a topic page's facts from Wolverine, and the Settings page's section with the
/// notes of what could not be listed. A host's own registration draws neither.
/// </summary>
public class KafkaDiscoveredFactsTests
{
    private static KafkaCatalogRig Rig(params KafkaSourceNote[] notes) => Rig(null, notes);

    private static KafkaCatalogRig Rig(Action<KafkaStudioOptions>? studio, params KafkaSourceNote[] notes) =>
        new(
            engine: o => o.ClusterSources.Add(_ => new FakeClusterSource(new KafkaClusterContribution(
                [FakeClusterSource.Cluster("wolverine", null, FakeClusterSource.Topic("orders", KafkaTopicRole.ListenerAndSender), FakeClusterSource.Topic("wolverine-dlq", KafkaTopicRole.DeadLetter))],
                notes))),
            studio: studio,
            cluster: "wolverine");

    private static async Task<IRenderedComponent<KafkaTopicPage>> OpenTopicAsync(KafkaCatalogRig rig, string topic, string cluster)
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate($"kafka/topic?cluster={cluster}&topic={Uri.EscapeDataString(topic)}", intercepted: false);
        return rig.Studio.Context.Render<KafkaTopicPage>();
    }

    private static async Task<IRenderedComponent<KafkaSettingsPage>> OpenSettingsAsync(KafkaCatalogRig rig, string cluster)
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate($"kafka/settings?cluster={cluster}", intercepted: false);
        return rig.Studio.Context.Render<KafkaSettingsPage>();
    }

    [Fact]
    public async Task A_discovered_topic_shows_its_role_message_types_group_serializer_and_source()
    {
        await using KafkaCatalogRig rig = Rig();
        rig.Topic("orders", 1, 3);

        IRenderedComponent<KafkaTopicPage> page = await OpenTopicAsync(rig, "orders", "wolverine");

        page.WaitForAssertion(() => page.FindAll(".ms-k-discovered").Should().ContainSingle());
        page.Find(".ms-k-discovered h2").TextContent.Should().Be("Discovered from Wolverine");

        string[] facts = [.. page.FindAll(".ms-k-discovered-facts dt").Select(static x => x.TextContent)];
        facts.Should().Equal("Role", "Message types", "Consumer group", "Serializer", "Source");

        string text = page.Find(".ms-k-discovered-facts").TextContent;
        text.Should().Contain("Listener and sender").And.Contain("Orders.PlaceOrder").And.Contain("namespace Orders.Events")
            .And.Contain("orders-app").And.Contain("SystemTextJsonSerializer");
        page.Find(".ms-k-topic-description").TextContent.Should().Be("Wolverine listener");
    }

    [Fact]
    public async Task The_dead_letter_topic_reads_as_one()
    {
        await using KafkaCatalogRig rig = Rig();
        rig.Topic("wolverine-dlq", 1, 1);

        IRenderedComponent<KafkaTopicPage> page = await OpenTopicAsync(rig, "wolverine-dlq", "wolverine");

        page.WaitForAssertion(() => page.FindAll(".ms-k-discovered").Should().ContainSingle());
        page.Find(".ms-k-discovered-facts").TextContent.Should().Contain("Dead letters");
    }

    [Fact]
    public async Task A_topic_the_host_declared_and_no_source_found_draws_no_discovered_section()
    {
        await using KafkaCatalogRig rig = new(engine: static o => o.Topic("orders", static t => t.Description("Orders")));
        rig.Topic("orders", 1, 3);

        IRenderedComponent<KafkaTopicPage> page = await OpenTopicAsync(rig, "orders", "test");

        page.WaitForAssertion(() => page.Markup.Should().Contain("Partitions"));
        page.FindAll(".ms-k-discovered").Should().BeEmpty();
    }

    [Fact]
    public async Task The_settings_page_says_who_listed_the_cluster_and_what_could_not_be_listed()
    {
        await using KafkaCatalogRig rig = Rig(
            new KafkaSourceNote("a topic", "it is on another cluster", "The topic 'billing' is on another cluster, so it is not listed here."),
            new KafkaSourceNote("tenant clusters", "not ready", "Tenant clusters are listed once the application has started."));

        IRenderedComponent<KafkaSettingsPage> page = await OpenSettingsAsync(rig, "wolverine");

        page.WaitForAssertion(() => page.FindAll(".ms-k-discovery").Should().ContainSingle());
        page.Find(".ms-k-discovery h2").TextContent.Should().Be("Discovered from Wolverine");
        page.Find(".ms-k-discovery-listed").TextContent.Should().Be("This cluster was listed from the application's Wolverine configuration.");
        page.FindAll(".ms-k-discovery-notes li").Select(static x => x.TextContent).Should().Equal(
            "The topic 'billing' is on another cluster, so it is not listed here.",
            "Tenant clusters are listed once the application has started.");
    }

    [Fact]
    public async Task A_hosts_own_cluster_with_nothing_discovered_draws_no_section()
    {
        await using KafkaCatalogRig rig = new();

        IRenderedComponent<KafkaSettingsPage> page = await OpenSettingsAsync(rig, "test");

        page.WaitForAssertion(() => page.FindAll("[aria-label='Your settings'] tbody tr").Should().NotBeEmpty());
        page.FindAll(".ms-k-discovery").Should().BeEmpty();
    }

    [Fact]
    public async Task The_settings_notes_are_asked_for_under_the_cluster_gate()
    {
        await using KafkaCatalogRig rig = Rig(static o => o.ClusterAuthorizationPolicy = "kafka-topics", new KafkaSourceNote("a topic", "x", "The topic 'billing' was not listed."));
        rig.Studio.Authorization.AllowKafka(static resource => resource.Cluster != "wolverine");
        KafkaCatalogService catalog = rig.Studio.Context.Services.GetRequiredService<KafkaCatalogService>();

        KafkaDiscoveryView? view = await catalog.GetDiscoveryAsync(new KafkaClusterEntry("wolverine", null, "Wolverine", null), Xunit.TestContext.Current.CancellationToken);

        view.Should().BeNull("a visitor the cluster policy refuses is told nothing about what its source listed");
    }
}
