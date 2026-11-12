using Bunit;

using KStudio.Components.Pages;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

namespace KStudio.Tests.Components;

/// <summary>The record page (D-UI decision 14), rendered against the real engine over the fake broker.</summary>
public class KafkaRecordPageTests
{
    private static void Seed(KafkaSearchRig rig, string topic, int count)
    {
        rig.Topic(topic);

        for (int i = 0; i < count; i++)
        {
            rig.Produce(topic, 0, i, "k" + i, "v" + i, ("correlation-id", "c-" + i));
        }
    }

    private static async Task<IRenderedComponent<KafkaRecordPage>> OpenAsync(KafkaSearchRig rig, string query)
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/record?cluster=test&" + query, intercepted: false);
        return rig.Studio.Context.Render<KafkaRecordPage>();
    }

    [Fact]
    public async Task An_invalid_link_makes_no_broker_call()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 3);

        IRenderedComponent<KafkaRecordPage> page = await OpenAsync(rig, "topic=orders&partition=-1&offset=abc");

        page.WaitForAssertion(() => page.Find(".ms-empty-description").TextContent.Should().Be("This record link is not valid."));
        rig.BrokerCalls.Should().Be(0);
    }

    [Fact]
    public async Task No_read_during_prerender()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 3);
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/record?cluster=test&topic=orders&partition=0&offset=1", intercepted: false);

        // The frame releases reads only after its first render has been delivered to a live circuit; before that the body does not exist.
        rig.BrokerCalls.Should().Be(0);

        IRenderedComponent<KafkaRecordPage> page = rig.Studio.Context.Render<KafkaRecordPage>();

        page.WaitForAssertion(() => page.Markup.Should().Contain("Neighbours"));
        rig.BrokerCalls.Should().BeGreaterThan(0, "the read happened after Ready");
    }

    [Fact]
    public async Task The_record_and_its_neighbours_are_drawn_and_the_current_offset_is_marked()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 20);

        IRenderedComponent<KafkaRecordPage> page = await OpenAsync(rig, "topic=orders&partition=0&offset=10");

        page.WaitForAssertion(() => page.FindAll("table.ms-k-neighbours tbody tr").Should().HaveCount(11));
        AngleSharp.Dom.IElement[] marked = [.. page.FindAll("table.ms-k-neighbours tbody tr[aria-current='true']")];
        marked.Should().ContainSingle().Which.QuerySelector("a")!.TextContent.Should().Be("10");
        page.FindAll("table.ms-k-neighbours tbody tr").Select(static x => x.QuerySelector("a")!.TextContent).Should().Equal("5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15");
        page.Find("table.ms-k-neighbours tbody tr a").GetAttribute("href").Should().Contain("kafka/record").And.Contain("offset=5");
        page.Markup.Should().Contain("Search around this record");
        page.Find("a.ms-button").GetAttribute("href").Should().Contain("kafka/search").And.Contain("topic=orders").And.Contain("from=2026-11-08T11%3A55%3A10Z");
    }

    [Theory]
    [InlineData("topic=orders&partition=0&offset=99", "Offset 99 has not been written yet: orders/0 ends at offset 3.")]
    [InlineData("topic=orders&partition=7&offset=1", "Partition 7 of topic orders was not found, or this application's credentials cannot describe it.")]
    public async Task Each_status_shows_its_text(string query, string text)
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 3);

        IRenderedComponent<KafkaRecordPage> page = await OpenAsync(rig, query);

        page.WaitForAssertion(() => page.Find(".ms-k-note").TextContent.Trim().Should().Be(text));
        page.Markup.Should().NotContain("ms-k-record-detail");
    }

    [Fact]
    public async Task A_log_start_above_the_offset_says_the_offset_is_gone()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 10);
        rig.Cluster.DeleteRecords("orders", 0, 5);

        IRenderedComponent<KafkaRecordPage> page = await OpenAsync(rig, "topic=orders&partition=0&offset=2");

        page.WaitForAssertion(() => page.Find(".ms-k-note").TextContent.Trim()
            .Should().Be("Offset 2 is gone: the earliest offset orders/0 still holds is 5 (retention or DeleteRecords removed the rest)."));
    }

    [Fact]
    public async Task A_refused_topic_reads_as_missing()
    {
        await using KafkaSearchRig rig = new();
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "secret");
        Seed(rig, "secret", 3);

        IRenderedComponent<KafkaRecordPage> page = await OpenAsync(rig, "topic=secret&partition=0&offset=1");

        page.WaitForAssertion(() => page.Find(".ms-empty-description").TextContent.Should().Be("Topic secret was not found, or is not available to you."));
        page.Markup.Should().NotContain("k1");
    }

    [Fact]
    public async Task The_view_is_audited_once_per_circuit()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 3);

        IRenderedComponent<KafkaRecordPage> first = await OpenAsync(rig, "topic=orders&partition=0&offset=1");
        first.WaitForAssertion(() => first.Markup.Should().Contain("ms-k-record-detail"));
        first.Dispose();

        IRenderedComponent<KafkaRecordPage> second = await OpenAsync(rig, "topic=orders&partition=0&offset=1");
        second.WaitForAssertion(() => second.Markup.Should().Contain("ms-k-record-detail"));

        rig.Ring.Where(static x => x.Action == "ViewRecord" && x.Succeeded).Should().ContainSingle();
    }

    [Fact]
    public async Task Capability_off_shows_the_disabled_notice_naming_the_option_and_reads_nothing()
    {
        await using KafkaSearchRig rig = new(searchOn: false);
        Seed(rig, "orders", 3);
        int callsBefore = rig.BrokerCalls;

        IRenderedComponent<KafkaRecordPage> page = await OpenAsync(rig, "topic=orders&partition=0&offset=1");

        page.WaitForAssertion(() => page.Markup.Should().Contain("KafkaStudioOptions.Capabilities.SearchRecords"));
        rig.BrokerCalls.Should().Be(callsBefore);
        page.Markup.Should().Contain("open a record is not enabled");
    }

    [Fact]
    public async Task A_gap_is_a_row_of_its_own()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 3);
        _ = KafkaRecordReadStatus.NoRecordAtOffset;

        // Offset 1 is beyond the end of a two-record topic that has a transaction marker there.
        long open = rig.Cluster.BeginTransaction();
        rig.Cluster.Produce("orders", 0, [1], [2], null, KafkaSearchRig.T0.ToUnixTimeMilliseconds() + 10_000, open);
        rig.Cluster.EndTransaction("orders", 0, open, commit: false);

        IRenderedComponent<KafkaRecordPage> page = await OpenAsync(rig, "topic=orders&partition=0&offset=3");

        page.WaitForAssertion(() => page.Markup.Should().Contain(KafkaStudioText.NoRecordAtThisOffset));
        page.Find(".ms-k-note").TextContent.Should().Contain("No record at offset 3");
    }
}
