using Bunit;

using Confluent.Kafka;

using KStudio.Components.Layout;
using KStudio.Components.Pages;
using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Services;
using KStudio.Tests.Contract;
using KStudio.Tests.Support;
using KStudio.Tests.Support.FakeKafka;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Components;

/// <summary>
/// #73 and #72: a group a source paired with a topic's listener is read on that topic only, a cluster-wide group without a committed offset is
/// named on one muted line, and clusters that share their bootstrap servers say so, from configuration alone and without naming a cluster the
/// visitor may not see.
/// </summary>
public class KafkaWolverineDiscoveryNotesTests
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static KafkaDiscoveredTopic Listener(string name, string? group) =>
        new(name, KafkaTopicRole.Listener, ["Orders.PlaceOrder"], "SystemTextJsonSerializer", group, false, null, "Wolverine", "Wolverine listener");

    private static KafkaContributedCluster Cluster(string key, string servers, Action<KafkaClusterBuilder>? configure, string? tenant, params KafkaDiscoveredTopic[] topics)
    {
        KafkaClusterBuilder builder = new();
        builder.DisplayName("Wolverine");
        configure?.Invoke(builder);
        return new KafkaContributedCluster(key, tenant, new ConsumerConfig { BootstrapServers = servers }, new AdminClientConfig { BootstrapServers = servers }, builder, topics);
    }

    private static void Fill(FakeKafkaCluster cluster, string topic, int records)
    {
        cluster.CreateTopic(topic, 1);

        for (int i = 0; i < records; i++)
        {
            cluster.Produce(topic, 0, null, "v"u8.ToArray(), null, T0 + (i * 1000));
        }
    }

    private static EngineHarness Engine(FakeKafkaCluster cluster, params KafkaContributedCluster[] clusters) =>
        EngineHarness.ForWire(
            new FakeKafkaWire(cluster, EngineHarness.ClusterKey),
            new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero)),
            TimeSpan.FromSeconds(5),
            options => options.ClusterSources.Add(_ => new FakeClusterSource(new KafkaClusterContribution(clusters, []))));

    [Fact]
    public async Task Four_listeners_and_four_groups_give_each_topic_its_own_group_only()
    {
        FakeKafkaCluster cluster = new(new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero)));
        KafkaDiscoveredTopic[] topics = [.. Enumerable.Range(1, 4).Select(static i => Listener("t" + i, "g" + i))];

        foreach (KafkaDiscoveredTopic topic in topics)
        {
            Fill(cluster, topic.Name, 10);
            cluster.SetGroup(topic.ConsumerGroup!, "Stable", 1);
            cluster.Commit(topic.ConsumerGroup!, topic.Name, 0, 4);
        }

        await using EngineHarness engine = Engine(cluster, Cluster("wolverine", "broker:9092", null, null, topics));

        engine.Provider.GetRequiredService<KafkaClusterRegistry>().Get("wolverine").ConsumerGroups.Should().Equal("g1", "g2", "g3", "g4");

        foreach (KafkaDiscoveredTopic topic in topics)
        {
            int before = cluster.Calls.Count(static x => x.Call == WireCall.GroupOffsets);

            KafkaTopicOverview overview = (await engine.Catalog.GetTopicOverviewAsync("wolverine", new KafkaTopicName(topic.Name), Token)).Value;

            KafkaGroupOverview only = overview.Groups.Should().ContainSingle().Which;
            only.GroupId.Should().Be(topic.ConsumerGroup);
            only.ClusterWide.Should().BeFalse();
            only.Lag.Value.Single().Lag.Should().Be(6);
            (cluster.Calls.Count(static x => x.Call == WireCall.GroupOffsets) - before).Should().Be(1, "one (group, topic) pair is asked, not every group of the cluster");
        }
    }

    [Fact]
    public async Task A_hosts_cluster_wide_group_is_read_on_every_topic_beside_the_one_a_listener_names()
    {
        FakeKafkaCluster cluster = new(new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero)));
        Fill(cluster, "orders", 10);
        cluster.SetGroup("orders-app", "Stable", 1);
        cluster.Commit("orders-app", "orders", 0, 4);

        await using EngineHarness engine = Engine(
            cluster,
            Cluster("wolverine", "broker:9092", static b => b.ConsumerGroup("audit").ConsumerGroup("orders-app"), null, Listener("orders", "orders-app")));

        KafkaTopicOverview overview = (await engine.Catalog.GetTopicOverviewAsync("wolverine", new KafkaTopicName("orders"), Token)).Value;

        overview.Groups.Select(static x => (x.GroupId, x.ClusterWide)).Should().Equal(("audit", true), ("orders-app", false));
        overview.Groups[0].HasNoCommittedOffset.Should().BeTrue();
        overview.Groups[1].HasNoCommittedOffset.Should().BeFalse("a group a listener names is in the table as it was");
    }

    [Fact]
    public async Task Only_the_first_fifty_discovered_groups_by_name_are_read()
    {
        FakeKafkaCluster cluster = new(new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero)));
        KafkaDiscoveredTopic[] topics = [.. Enumerable.Range(0, 55).Select(static i => Listener("t" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), "g" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)))];

        foreach (KafkaDiscoveredTopic topic in topics)
        {
            Fill(cluster, topic.Name, 1);
        }

        await using EngineHarness engine = Engine(cluster, Cluster("wolverine", "broker:9092", null, null, topics));
        KafkaClusterRegistry registry = engine.Provider.GetRequiredService<KafkaClusterRegistry>();

        registry.Get("wolverine").ConsumerGroups.Should().HaveCount(50);

        (await engine.Catalog.GetTopicOverviewAsync("wolverine", new KafkaTopicName("t00"), Token)).Value.Groups.Should().ContainSingle();
        (await engine.Catalog.GetTopicOverviewAsync("wolverine", new KafkaTopicName("t54"), Token)).Value.Groups.Should().BeEmpty("its group is past the bound");
    }

    [Fact]
    public async Task A_cluster_wide_group_with_no_committed_offset_is_named_on_one_muted_line_and_not_in_the_table()
    {
        await using KafkaCatalogRig rig = new(
            engine: o => o.AddCluster("groups", new ClientConfig { BootstrapServers = "x:1" }, c => c.ConsumerGroup("billing").ConsumerGroup("audit").ConsumerGroup("ledger")),
            cluster: "groups");
        rig.Topic("orders", 1, 10);
        rig.Cluster.SetGroup("billing", "Stable", 3);
        rig.Cluster.Commit("billing", "orders", 0, 4);

        IRenderedComponent<KafkaTopicPage> page = await OpenTopicAsync(rig, "orders", "groups");

        page.WaitForAssertion(() => page.FindAll("[role=region][aria-label='Consumer groups'] tbody tr").Should().ContainSingle());
        page.Find("[role=region][aria-label='Consumer groups'] tbody tr").TextContent.Should().Contain("billing");
        page.Find(".ms-k-idle-groups").TextContent.Should().Be("Groups with no committed offset on this topic: audit, ledger");
        page.FindAll("details.ms-k-group-lag").Should().ContainSingle();
    }

    [Fact]
    public async Task A_group_a_listener_names_is_in_the_table_even_with_no_committed_offset_and_no_line_is_drawn()
    {
        await using KafkaCatalogRig rig = new(
            engine: o => o.ClusterSources.Add(_ => new FakeClusterSource(new KafkaClusterContribution([Cluster("wolverine", "broker:9092", null, null, Listener("orders", "orders-app"))], []))),
            cluster: "wolverine");
        rig.Topic("orders", 1, 10);

        IRenderedComponent<KafkaTopicPage> page = await OpenTopicAsync(rig, "orders", "wolverine");

        page.WaitForAssertion(() => page.FindAll("[role=region][aria-label='Consumer groups'] tbody tr").Should().ContainSingle());
        page.Find("[role=region][aria-label='Consumer groups'] tbody tr").TextContent.Should().Contain("orders-app");
        page.FindAll(".ms-k-idle-groups").Should().BeEmpty();
    }

    [Fact]
    public void Clusters_with_the_same_servers_are_marked_whatever_the_order_or_the_whitespace_and_never_by_connecting()
    {
        KafkaEngineOptions options = new();
        options.ClusterSources.Add(_ => new FakeClusterSource(new KafkaClusterContribution(
            [
                Cluster("wolverine", "b:9092, a:9092", null, null),
                Cluster("wolverine-besteffort", " a:9092,b:9092 ", null, null),
                Cluster("wolverine-other", "c:9092", null, null),
                Cluster("wolverine", "b:9092,a:9092", null, "acme"),
            ],
            [])));
        KafkaClusterRegistry registry = new(options, new ServiceCollection().BuildServiceProvider());

        registry.Get("wolverine").ServersKey.Should().Be("a:9092,b:9092");

        KafkaCatalog catalog = new(registry, null!, null!);
        Dictionary<string, KafkaClusterInfo> byKey = catalog.Clusters.ToDictionary(static x => x.Key, StringComparer.Ordinal);

        byKey["wolverine"].SameServersAs.Should().Equal("wolverine-besteffort");
        byKey["wolverine-besteffort"].SameServersAs.Should().Equal("wolverine", "wolverine/acme");
        byKey["wolverine-other"].SameServersAs.Should().BeEmpty();
        byKey["wolverine/acme"].SameServersAs.Should().Equal("wolverine-besteffort"); // another base cluster only: not its parent
    }

    [Fact]
    public async Task The_entries_carry_the_catalogs_same_servers_keys()
    {
        FakeKafkaCluster cluster = new(new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero)));
        await using EngineHarness engine = Engine(cluster, Cluster("wolverine", "a:9092,b:9092", null, null), Cluster("wolverine-besteffort", "b:9092,a:9092", null, null));

        KafkaClusterEntry[] entries = [.. new CatalogClusterEntrySource(engine.Catalog).Entries];

        entries.Single(static x => x.Key == "wolverine").SameServersAs.Should().Equal("wolverine-besteffort");
        entries.Single(static x => x.Key == "test").SameServersAs.Should().BeEmpty();
    }

    [Fact]
    public void The_selector_text_and_the_names_are_only_of_clusters_the_visitor_may_see()
    {
        KafkaClusterEntry first = new("wolverine", null, "Wolverine", null) { SameServersAs = ["wolverine-besteffort", "wolverine-hidden"] };
        KafkaClusterEntry second = new("wolverine-besteffort", null, "Wolverine · besteffort", null);

        first.SameServersNames([first, second]).Should().Equal("Wolverine · besteffort");
        first.SameServersNames([first]).Should().BeEmpty();
        KafkaStudioText.ClusterOptionText(first.Label, first.SameServersNames([first, second])).Should().Be("Wolverine (same servers as Wolverine · besteffort)");
        KafkaStudioText.ClusterOptionText("Wolverine", []).Should().Be("Wolverine");
        first.Equals(new KafkaClusterEntry("wolverine", null, "Wolverine", null) { SameServersAs = ["wolverine-besteffort", "wolverine-hidden"] }).Should().BeTrue("entries are rebuilt on every read");
    }

    [Fact]
    public async Task The_settings_page_names_the_visible_cluster_with_the_same_servers_and_never_the_servers()
    {
        await using KafkaCatalogRig rig = new(engine: o => o.AddCluster("wolverine", new ClientConfig { BootstrapServers = "x:1" }), cluster: "wolverine");
        rig.Studio.Entries.Entries =
        [
            new KafkaClusterEntry("wolverine", null, "Wolverine", null) { SameServersAs = ["wolverine-besteffort"] },
            new KafkaClusterEntry("wolverine-besteffort", null, "Wolverine · besteffort", null) { SameServersAs = ["wolverine"] },
        ];

        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/settings?cluster=wolverine", intercepted: false);
        IRenderedComponent<KafkaSettingsPage> settings = rig.Studio.Context.Render<KafkaSettingsPage>();

        settings.WaitForAssertion(() => settings.Find(".ms-k-same-servers").TextContent.Should().Be(
            "Same bootstrap servers as Wolverine · besteffort: probably one cluster registered twice. Declare it once."));
    }

    [Fact]
    public async Task The_settings_page_names_ExcludeBroker_only_when_one_of_the_clusters_is_a_named_broker()
    {
        await using KafkaCatalogRig rig = new(engine: o => o.AddCluster("wolverine", new ClientConfig { BootstrapServers = "x:1" }), cluster: "wolverine");
        rig.Studio.Entries.Entries =
        [
            new KafkaClusterEntry("wolverine", null, "Wolverine", null) { SameServersAs = ["wolverine-besteffort"] },
            new KafkaClusterEntry("wolverine-besteffort", null, "Wolverine · besteffort", null) { SameServersAs = ["wolverine"], CanBeLeftOut = true },
        ];

        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/settings?cluster=wolverine", intercepted: false);
        IRenderedComponent<KafkaSettingsPage> settings = rig.Studio.Context.Render<KafkaSettingsPage>();

        settings.WaitForAssertion(() => settings.Find(".ms-k-same-servers").TextContent.Should().Be(
            "Same bootstrap servers as Wolverine · besteffort: probably one cluster registered twice. KafkaWolverineOptions.ExcludeBroker leaves one out."));
    }

    [Fact]
    public async Task A_refused_named_broker_does_not_change_the_advice_two_visible_clusters_get()
    {
        await using KafkaCatalogRig rig = new(
            engine: o => o.AddCluster("wolverine", new ClientConfig { BootstrapServers = "x:1" }),
            studio: o => o.ClusterAuthorizationPolicy = "kafka-topics",
            cluster: "wolverine");
        rig.Studio.Entries.Entries =
        [
            new KafkaClusterEntry("wolverine", null, "Wolverine", null) { SameServersAs = ["twin", "wolverine-besteffort"] },
            new KafkaClusterEntry("twin", null, "Twin", null) { SameServersAs = ["wolverine", "wolverine-besteffort"] },
            new KafkaClusterEntry("wolverine-besteffort", null, "Wolverine · besteffort", null) { SameServersAs = ["wolverine", "twin"], CanBeLeftOut = true },
        ];
        rig.Studio.Authorization.AllowKafka(static resource => resource.Cluster != "wolverine-besteffort");

        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/settings?cluster=wolverine", intercepted: false);
        IRenderedComponent<KafkaSettingsPage> settings = rig.Studio.Context.Render<KafkaSettingsPage>();

        settings.WaitForAssertion(() => settings.Find(".ms-k-same-servers").TextContent.Should().Be(
            "Same bootstrap servers as Twin: probably one cluster registered twice. Declare it once."));
        settings.Markup.Should().NotContain("besteffort").And.NotContain("ExcludeBroker");
    }

    [Fact]
    public async Task A_refused_cluster_is_named_by_neither_the_selector_nor_the_settings_page_and_an_allowed_one_is_named_by_both()
    {
        await using KafkaCatalogRig rig = new(
            engine: o => o.AddCluster("wolverine", new ClientConfig { BootstrapServers = "x:1" }),
            studio: o => o.ClusterAuthorizationPolicy = "kafka-topics",
            cluster: "wolverine");
        rig.Studio.Entries.Entries =
        [
            new KafkaClusterEntry("wolverine", null, "Wolverine", null) { SameServersAs = ["wolverine-besteffort"] },
            new KafkaClusterEntry("wolverine-besteffort", null, "Wolverine · besteffort", null) { SameServersAs = ["wolverine"] },
        ];
        rig.Studio.Authorization.AllowKafka(static resource => resource.Cluster != "wolverine-besteffort");

        await rig.Studio.Context.ReadyAsync();
        await rig.Studio.State.EnsureInitializedAsync(Token);
        rig.Studio.State.AvailableClusters.Select(static x => x.Key).Should().Equal("wolverine");

        IRenderedComponent<ClusterSelector> selector = rig.Studio.Context.Render<ClusterSelector>();
        selector.WaitForAssertion(() => selector.Find(".ms-k-cluster-name").TextContent.Should().Be("Wolverine"));
        selector.Markup.Should().NotContain("besteffort").And.NotContain("same servers");

        rig.Studio.Navigation.Simulate("kafka/settings?cluster=wolverine", intercepted: false);
        IRenderedComponent<KafkaSettingsPage> settings = rig.Studio.Context.Render<KafkaSettingsPage>();
        settings.WaitForAssertion(() => settings.Markup.Should().Contain("Cluster settings"));
        settings.Markup.Should().NotContain("besteffort").And.NotContain("Same bootstrap servers");

        // Allowed, both are named: in the selector's option and on the Settings page.
        await using KafkaCatalogRig both = new(
            engine: o => o.AddCluster("wolverine", new ClientConfig { BootstrapServers = "x:1" }),
            studio: o => o.ClusterAuthorizationPolicy = "kafka-topics",
            cluster: "wolverine");
        both.Studio.Entries.Entries = rig.Studio.Entries.Entries;

        await both.Studio.Context.ReadyAsync();
        await both.Studio.State.EnsureInitializedAsync(Token);

        IRenderedComponent<ClusterSelector> open = both.Studio.Context.Render<ClusterSelector>();
        open.WaitForAssertion(() => open.FindAll("option").Select(static x => x.TextContent.Trim()).Should().Equal(
            "Wolverine (same servers as Wolverine · besteffort)",
            "Wolverine · besteffort (same servers as Wolverine)"));
    }

    [Fact]
    public async Task A_topic_nothing_reads_on_a_cluster_with_groups_says_no_group_reads_it_and_a_cluster_with_none_says_none_are_declared()
    {
        await using KafkaCatalogRig rig = new(
            engine: o => o.ClusterSources.Add(_ => new FakeClusterSource(new KafkaClusterContribution(
                [Cluster("wolverine", "broker:9092", null, null, Listener("orders", "orders-app"), Listener("out.v1", null))], []))),
            cluster: "wolverine");
        rig.Topic("out.v1", 1, 1);

        IRenderedComponent<KafkaTopicPage> page = await OpenTopicAsync(rig, "out.v1", "wolverine");

        page.WaitForAssertion(() => page.Markup.Should().Contain("No consumer group of this application reads this topic."));
        page.Markup.Should().NotContain("No consumer groups are declared for this cluster");
        page.FindAll(".ms-k-idle-groups").Should().BeEmpty();

        await using KafkaCatalogRig none = new(engine: o => o.ClusterSources.Add(_ => new FakeClusterSource(new KafkaClusterContribution(
            [Cluster("wolverine", "broker:9092", null, null, Listener("out.v1", null))], []))), cluster: "wolverine");
        none.Topic("out.v1", 1, 1);

        IRenderedComponent<KafkaTopicPage> empty = await OpenTopicAsync(none, "out.v1", "wolverine");

        empty.WaitForAssertion(() => empty.Markup.Should().Contain("No consumer groups are declared for this cluster"));
    }

    [Fact]
    public void A_cluster_a_source_lets_the_host_leave_out_marks_the_pair_in_the_catalog()
    {
        KafkaEngineOptions options = new();
        options.ClusterSources.Add(_ => new FakeClusterSource(new KafkaClusterContribution(
            [
                Cluster("wolverine", "a:9092", null, null),
                Cluster("wolverine-besteffort", "a:9092", null, null) with { CanBeLeftOut = true },
                Cluster("host-twin", "z:9092", null, null),
                Cluster("host-twin-2", "z:9092", null, null),
            ],
            [])));
        KafkaCatalog catalog = new(new KafkaClusterRegistry(options, new ServiceCollection().BuildServiceProvider()), null!, null!);
        Dictionary<string, KafkaClusterInfo> byKey = catalog.Clusters.ToDictionary(static x => x.Key, StringComparer.Ordinal);

        byKey["wolverine"].CanBeLeftOut.Should().BeFalse("each cluster carries its own fact, never its twin's");
        byKey["wolverine-besteffort"].CanBeLeftOut.Should().BeTrue();
        byKey["host-twin"].CanBeLeftOut.Should().BeFalse();
        byKey["host-twin-2"].CanBeLeftOut.Should().BeFalse();
    }

    private static async Task<IRenderedComponent<KafkaTopicPage>> OpenTopicAsync(KafkaCatalogRig rig, string topic, string cluster)
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate($"kafka/topic?cluster={cluster}&topic={Uri.EscapeDataString(topic)}", intercepted: false);
        return rig.Studio.Context.Render<KafkaTopicPage>();
    }
}
