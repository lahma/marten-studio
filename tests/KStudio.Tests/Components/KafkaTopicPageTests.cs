using System.Text;

using AngleSharp.Dom;

using Bunit;

using KStudio.Components.Pages;
using KStudio.Core.Wire;
using KStudio.Tests.Support;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KStudio.Tests.Components;

/// <summary>The topic overview (D-UI decision 8), rendered against the real engine over the fake broker.</summary>
public class KafkaTopicPageTests
{
    private static async Task<IRenderedComponent<KafkaTopicPage>> OpenAsync(KafkaCatalogRig rig, string topic, string cluster = "test")
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate($"kafka/topic?cluster={cluster}&topic={Uri.EscapeDataString(topic)}", intercepted: false);
        return rig.Studio.Context.Render<KafkaTopicPage>();
    }

    private static IEnumerable<string> Notes(IRenderedComponent<KafkaTopicPage> page) =>
        page.FindAll(".ms-k-note").Select(static x => x.TextContent.Trim());

    [Fact]
    public async Task A_missing_and_a_refused_topic_read_the_same()
    {
        await using KafkaCatalogRig rig = new(studio: o => o.ClusterAuthorizationPolicy = "kafka-topics");
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "secret");
        rig.Topic("secret", 1, 1);

        IRenderedComponent<KafkaTopicPage> missing = await OpenAsync(rig, "no-such-topic");
        missing.WaitForAssertion(() => missing.FindAll(".ms-empty").Should().HaveCount(1));
        string missingText = missing.Find(".ms-empty-description").TextContent;
        missing.Dispose();
        int callsAfterMissing = rig.BrokerCalls;

        IRenderedComponent<KafkaTopicPage> refused = await OpenAsync(rig, "secret");
        refused.WaitForAssertion(() => refused.FindAll(".ms-empty").Should().HaveCount(1));

        missingText.Should().Be("Topic no-such-topic was not found, or is not available to you.");
        refused.Find(".ms-empty-description").TextContent.Should().Be("Topic secret was not found, or is not available to you.");
        refused.Markup.Should().NotContain("Partitions", "a refused topic shows nothing about itself");
        rig.BrokerCalls.Should().Be(callsAfterMissing, "the policy refused the topic before any broker call was made for it");
    }

    [Fact]
    public async Task A_not_available_topic_is_not_polled_and_logs_9603_once()
    {
        CapturingLoggerProvider logs = new();
        await using KafkaCatalogRig rig = new(
            studio: o => o.ClusterAuthorizationPolicy = "kafka-topics",
            services: services => services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "secret");
        rig.Studio.Context.Options.RefreshInterval = TimeSpan.FromMilliseconds(50);
        rig.Topic("orders", 1, 1);

        // The home polls, and asks the policy about "orders" at every tick: that is the clock. "secret" is not in the cluster, so the home never asks about it.
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka?cluster=test", intercepted: false);
        IRenderedComponent<KafkaHome> home = rig.Studio.Context.Render<KafkaHome>();
        home.WaitForAssertion(() => home.Markup.Should().Contain("orders"));

        rig.Studio.Navigation.Simulate("kafka/topic?cluster=test&topic=secret", intercepted: false);
        IRenderedComponent<KafkaTopicPage> page = rig.Studio.Context.Render<KafkaTopicPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-empty").Should().HaveCount(1));

        int Asked() => rig.Studio.Authorization.KafkaCalls.Count(static x => x.Resource.Topic == "orders" && x.Resource.Capability is null);
        int before = Asked();
        home.WaitForAssertion(() => Asked().Should().BeGreaterThanOrEqualTo(before + 6));

        rig.Studio.Authorization.KafkaCalls.Count(static x => x.Resource.Topic == "secret").Should().Be(1, "a topic that is not available is read again only when the address or the cluster changes");
        logs.Entries.Count(static x => x.EventId.Id == 9603).Should().Be(1);
    }

    [Fact]
    public async Task Navigating_to_another_kafka_page_does_not_reload_the_topic()
    {
        await using KafkaCatalogRig rig = new(studio: o => o.ClusterAuthorizationPolicy = "kafka-topics");
        rig.Topic("orders", 1, 1);
        rig.Topic("other", 1, 1);
        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "orders");
        page.WaitForAssertion(() => page.FindAll("table.ms-k-partitions tbody tr").Should().HaveCount(1));
        int calls = rig.Studio.Authorization.KafkaCalls.Count();

        rig.Studio.Navigation.Simulate("kafka/settings?cluster=test&topic=other", intercepted: false);
        await page.InvokeAsync(static () => { });

        rig.Studio.Authorization.KafkaCalls.Count().Should().Be(calls, "the topic in an address that is not this page's is nobody's business here");
        page.Markup.Should().Contain("orders");
    }

    [Fact]
    public async Task An_invalid_name_makes_no_broker_call()
    {
        await using KafkaCatalogRig rig = new();

        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "not a topic/../name");

        page.WaitForAssertion(() => page.FindAll(".ms-empty").Should().HaveCount(1));
        page.Find(".ms-empty-description").TextContent.Should().Be("Topic not a topic/../name was not found, or is not available to you.");
        rig.BrokerCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_compacted_topic_warns()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("state", 1, new Dictionary<string, string> { ["cleanup.policy"] = "compact" }, 3);

        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "state");

        page.WaitForAssertion(() => Notes(page).Should().Contain("Compacted topic: a time-window search can miss superseded keys and sees tombstones"));
    }

    [Fact]
    public async Task Tiered_storage_is_flagged()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("cold", 1, new Dictionary<string, string> { ["remote.storage.enable"] = "true" }, 3);

        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "cold");

        page.WaitForAssertion(() => Notes(page).Should().Contain("Older records are in tiered storage: reading them is slower and may cost more"));
    }

    [Fact]
    public async Task A_withheld_column_has_one_notice()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("orders", 3, 4, 4, 4);
        rig.Cluster.OffsetFault = static (_, spec, _) => spec.Kind == WireOffsetSpecKind.MaxTimestamp
            ? new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Topic, "orders")
            : null;

        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "orders");

        page.WaitForAssertion(() => page.FindAll("table.ms-k-partitions tbody tr").Should().HaveCount(3));
        Notes(page).Where(static x => x.StartsWith("Not available", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Be("Not available: needs `DESCRIBE` on topic `orders`");
        page.FindAll("table.ms-k-partitions tbody tr").Select(static x => x.QuerySelectorAll("td")[9].TextContent.Trim()).Should().OnlyContain(static x => x == "—");
    }

    [Fact]
    public async Task The_lso_shows_only_below_the_high_watermark()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("tx", 2, 3, 3);
        long open = rig.Cluster.BeginTransaction();
        rig.Cluster.Produce("tx", 0, Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v"), null, KafkaCatalogRig.T0 + 10_000, open);

        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "tx");

        page.WaitForAssertion(() => page.FindAll("table.ms-k-partitions tbody tr").Should().HaveCount(2));
        IElement[] rows = [.. page.FindAll("table.ms-k-partitions tbody tr")];
        rows[0].QuerySelectorAll("td")[6].TextContent.Trim().Should().Be("3", "the open transaction holds the stable offset at 3 while the high watermark is 4");
        rows[1].QuerySelectorAll("td")[6].TextContent.Trim().Should().BeEmpty("a partition with nothing open has no LSO to show");
        Notes(page).Should().Contain("An open transaction holds offsets back: the last stable offset is below the high watermark on 1 partition.");
    }

    [Fact]
    public async Task An_empty_partition_says_empty_never_1970()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("sparse", 2, 3, 0);

        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "sparse");

        page.WaitForAssertion(() => page.FindAll("table.ms-k-partitions tbody tr").Should().HaveCount(2));
        IElement empty = page.FindAll("table.ms-k-partitions tbody tr")[1];
        empty.QuerySelectorAll("td")[8].TextContent.Trim().Should().Be("empty");
        empty.QuerySelectorAll("td")[9].TextContent.Trim().Should().Be("empty");
        page.Markup.Should().NotContain("1970");
        page.FindAll("table.ms-k-partitions tbody tr")[0].QuerySelectorAll("td")[8].TextContent.Should().Contain("0 · 2026-11-08 12:00:00");
    }

    [Fact]
    public async Task Group_lag_and_a_refused_group()
    {
        await using KafkaCatalogRig rig = new(
            engine: o => o.AddCluster("groups", new Confluent.Kafka.ClientConfig { BootstrapServers = "x:1" }, c => c.ConsumerGroup("billing").ConsumerGroup("denied-group")),
            cluster: "groups");
        rig.Topic("orders", 1, 10);
        rig.Cluster.SetGroup("billing", "Stable", 3);
        rig.Cluster.Commit("billing", "orders", 0, 4);
        rig.Cluster.Deny(KafkaAclOperation.Describe, KafkaResourceType.Group, "denied-group");

        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "orders", "groups");

        page.WaitForAssertion(() => page.FindAll("[role=region][aria-label='Consumer groups'] tbody tr").Should().HaveCount(2));
        IElement billing = page.FindAll("[role=region][aria-label='Consumer groups'] tbody tr")[0];
        billing.TextContent.Should().Contain("billing").And.Contain("Stable").And.Contain("3").And.Contain("~6");
        IElement denied = page.FindAll("[role=region][aria-label='Consumer groups'] tbody tr")[1];
        denied.TextContent.Should().Contain("denied-group").And.Contain("Not available: needs `DESCRIBE` on group `denied-group`");

        IElement lag = page.Find("details.ms-k-group-lag");
        lag.TextContent.Should().Contain("billing").And.Contain("4").And.Contain("6");
    }

    [Fact]
    public async Task No_declared_group_says_how_to_declare_one()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("orders", 1, 1);

        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "orders");

        page.WaitForAssertion(() => page.Markup.Should().Contain("No consumer groups are declared for this cluster: add KafkaClusterBuilder.ConsumerGroup(\"…\") to show their lag here."));
    }

    [Fact]
    public async Task Search_this_topic_is_a_link_or_the_disabled_notice()
    {
        await using KafkaCatalogRig off = new();
        off.Topic("orders", 1, 1);
        await using KafkaCatalogRig on = new(studio: o =>
        {
            o.Capabilities = KafkaStudioCapabilities.All();
            o.CapabilityAuthorizationPolicy = "kafka-search";
        });
        on.Topic("orders", 1, 1);

        IRenderedComponent<KafkaTopicPage> disabled = await OpenAsync(off, "orders");
        IRenderedComponent<KafkaTopicPage> enabled = await OpenAsync(on, "orders");

        disabled.WaitForAssertion(() => disabled.FindAll(".ms-capability-disabled").Should().HaveCount(1));
        disabled.Find(".ms-capability-disabled").TextContent.Should().Contain("search this topic is not enabled").And.Contain("KafkaStudioOptions.Capabilities.SearchRecords");
        disabled.FindAll("a[href*='kafka/search']").Should().BeEmpty();

        enabled.WaitForAssertion(() => enabled.FindAll("a[href*='kafka/search']").Should().HaveCount(1));
        enabled.Find("a[href*='kafka/search']").GetAttribute("href").Should().Contain("cluster=test").And.Contain("topic=orders");
    }

    [Fact]
    public async Task Throughput_says_measuring_until_two_samples()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("orders", 1, 10);

        IRenderedComponent<KafkaTopicPage> first = await OpenAsync(rig, "orders");
        first.WaitForAssertion(() => first.FindAll(".ms-stat-card").Should().HaveCount(5));
        StatValue(first, "Offsets per second").Should().Be("measuring…");

        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        for (int i = 0; i < 200; i++)
        {
            rig.Cluster.Produce("orders", 0, Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v"), null, KafkaCatalogRig.T0 + 100_000 + i);
        }

        IRenderedComponent<KafkaTopicPage> second = await OpenAsync(rig, "orders");
        second.WaitForAssertion(() => StatValue(second, "Offsets per second").Should().Be("~10/s"));
        StatValue(second, "Offset span").Should().Be("~210");
    }

    [Fact]
    public async Task The_stat_cards_show_partitions_retention_and_timestamp_type()
    {
        await using KafkaCatalogRig rig = new();
        rig.Topic("orders", 3, new Dictionary<string, string> { ["retention.ms"] = "604800000", ["retention.bytes"] = "1073741824", ["message.timestamp.type"] = "LogAppendTime" }, 1, 1, 1);

        IRenderedComponent<KafkaTopicPage> page = await OpenAsync(rig, "orders");

        page.WaitForAssertion(() => StatValue(page, "Partitions").Should().Be("3"));
        StatValue(page, "Retention").Should().Be("7 d · 1 GiB");
        StatValue(page, "Timestamp type").Should().Be("LogAppendTime");
    }

    private static string StatValue(IRenderedComponent<KafkaTopicPage> page, string title) =>
        page.FindAll(".ms-stat-card").Single(x => x.QuerySelector(".ms-stat-card-title")!.TextContent == title).QuerySelector(".ms-stat-card-value")!.TextContent.Trim();
}
