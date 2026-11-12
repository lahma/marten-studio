using System.Text;

using AngleSharp.Dom;

using Bunit;

using KStudio.Components.Pages;
using KStudio.Core.Predicates;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace KStudio.Tests.Components;

/// <summary>The search page with values (D-PAYLOAD decisions 1, 4, 5 and 7.6): the help, the gates, the detail, compare and the pruned search.</summary>
public class KafkaSearchPagePayloadTests
{
    private static string Window(string topic) => $"&topic={topic}&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z";

    public static TheoryData<string> ValueExamples => new(KafkaSearchBody.ValueSyntaxExamples);

    [Theory]
    [MemberData(nameof(ValueExamples))]
    public void Every_value_example_parses(string example)
    {
        PredicateParseResult parsed = PredicateParser.Parse(example);

        parsed.IsValid.Should().BeTrue(string.Join("; ", parsed.Errors.Select(static x => x.Message)));
        PredicateTree.ReadsValues(parsed.Tree).Should().BeTrue("the help's value examples are value searches");
    }

    private static async Task<IRenderedComponent<KafkaSearchPage>> OpenAsync(KafkaSearchRig rig, string topic)
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test" + Window(topic), intercepted: false);
        IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();
        page.WaitForAssertion(() => page.FindAll("textarea.ms-k-predicate").Should().HaveCount(1));
        await page.FindAll("label.ms-k-topic-option").Single(x => x.TextContent.Trim() == topic).QuerySelector("input")!.ChangeAsync(new ChangeEventArgs { Value = true });
        return page;
    }

    private static async Task SearchForAsync(IRenderedComponent<KafkaSearchPage> page, string predicate)
    {
        await page.Find("textarea.ms-k-predicate").InputAsync(new ChangeEventArgs { Value = predicate });
        await page.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());
    }

    private static string[] Rows(IRenderedComponent<KafkaSearchPage> page) =>
        [.. page.FindAll("table.ms-k-matches > tbody > tr.ms-table-row").Select(static x => x.QuerySelectorAll("td")[2].TextContent.Trim())];

    private static void SeedJson(KafkaSearchRig rig, int count = 3)
    {
        rig.Topic("orders");

        for (int i = 0; i < count; i++)
        {
            rig.Produce("orders", 0, i, "k" + i, "{\"status\":\"" + (i == 2 ? "shipped" : "placed") + "\",\"customer\":{\"id\":\"customer-" + i + "\"}}");
        }
    }

    [Fact]
    public async Task The_value_syntax_is_help_only_with_ReadPayloads()
    {
        await using KafkaSearchRig on = new(payloads: true);
        SeedJson(on);
        await using KafkaSearchRig off = new(payloads: false);
        SeedJson(off);

        IRenderedComponent<KafkaSearchPage> withValues = await OpenAsync(on, "orders");
        IRenderedComponent<KafkaSearchPage> without = await OpenAsync(off, "orders");

        withValues.Find("details.ms-k-syntax").TextContent.Should().Contain("$.customer.id = 'customer-042'").And.Contain("$.total >= 100");
        without.Find("details.ms-k-syntax").TextContent.Should().Contain("value and $.json.paths need KafkaStudioOptions.Capabilities.ReadPayloads.").And.NotContain("$.total >= 100");
    }

    [Fact]
    public async Task A_value_search_finds_the_record_and_its_detail_shows_the_value()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        SeedJson(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, "orders");

        await SearchForAsync(page, "$.status = 'shipped'");
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 2"));
        await page.Find("table.ms-k-matches button.ms-btn-sm").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-k-match-detail [role=tree]").GetAttribute("aria-label").Should().Be("Record value"));
        page.Find(".ms-k-match-detail").TextContent.Should().Contain("customer-2");
        rig.Ring.Should().Contain(static x => x.Action == "ViewRecord" && x.Capability == "ReadPayloads" && x.Target == "orders/0/2");
        rig.Ring.Should().Contain(static x => x.Action == "SearchRecords" && x.Capability == "ReadPayloads" && x.Message!.EndsWith("; values read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_value_search_without_ReadPayloads_is_refused_with_the_option()
    {
        await using KafkaSearchRig rig = new(payloads: false);
        SeedJson(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, "orders");

        await SearchForAsync(page, "$.status = 'shipped'");

        page.WaitForAssertion(() => page.Find(".ms-k-note-warning").TextContent.Should().Contain("KafkaStudioOptions.Capabilities.ReadPayloads"));
        page.FindAll("table.ms-k-matches").Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_search_whose_topic_refuses_values_shows_the_detail_without_one()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Studio.Authorization.AllowKafka(static r => r.Capability != "ReadPayloads");
        SeedJson(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, "orders");

        await SearchForAsync(page, "key = 'k2'");
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 2"));
        await page.Find("table.ms-k-matches button.ms-btn-sm").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-k-match-detail .ms-k-value").TextContent.Trim().Should().Be("The value of this record is not available to you."));
        page.Markup.Should().NotContain("customer-2");
        rig.Ring.Should().NotContain(static x => x.Capability == "ReadPayloads", "nothing was read of a value, so nothing is recorded as read");
    }

    [Fact]
    public async Task At_most_two_records_can_be_compared_and_each_value_view_is_recorded_once()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        SeedJson(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, "orders");

        await SearchForAsync(page, "key ^= 'k'");
        page.WaitForAssertion(() => page.FindAll("table.ms-k-matches > tbody > tr.ms-table-row").Should().HaveCount(3));

        IElement[] boxes = [.. page.FindAll("table.ms-k-matches input[type=checkbox]")];
        await boxes[0].ChangeAsync(new ChangeEventArgs { Value = true });
        await page.FindAll("table.ms-k-matches input[type=checkbox]")[1].ChangeAsync(new ChangeEventArgs { Value = true });

        page.FindAll("table.ms-k-matches input[type=checkbox]").Count(static x => x.HasAttribute("disabled")).Should().Be(1, "the third is disabled once two are checked");

        page.Find(".ms-k-compare-bar button").TextContent.Should().Be("Compare 2 records");
        await page.Find(".ms-k-compare-bar button").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll(".ms-k-compare").Should().HaveCount(1));
        await page.Find(".ms-k-compare-header button").ClickAsync(new MouseEventArgs());
        await page.Find(".ms-k-compare-bar button").ClickAsync(new MouseEventArgs());

        rig.Ring.Count(static x => x.Action == "ViewRecord" && x.Capability == "ReadPayloads").Should().Be(2, "one value view per record in this circuit, however often it is compared");
    }

    [Fact]
    public async Task A_pruned_no_match_offers_Search_all_partitions_which_reads_every_partition()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("pruned", t => t.Partitioner(KafkaPartitioner.Murmur2)));
        rig.Topic("pruned", 6);

        for (int i = 0; i < 4_000; i++)
        {
            string key = "k-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            rig.Produce("pruned", KafkaPartitioner.Murmur2.Partition(Encoding.UTF8.GetBytes(key), 6), i / 6, key, new string('x', 100));
        }

        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, "pruned");

        await SearchForAsync(page, "key = 'k-9999'");
        page.WaitForAssertion(() => page.FindAll(".ms-k-result-banner").Should().HaveCount(1));

        page.Find("details.ms-k-verdicts li.ms-k-pruned").TextContent.Should().Contain("one partition: murmur2 maps the key to partition ").And.Contain(" of 6 (verified on ");
        page.Find(".ms-k-caveats").TextContent.Should().Contain("This search read only partition ").And.Contain(" of 6 of pruned (murmur2, verified on ");
        page.FindAll("details.ms-k-progress tbody tr").Should().HaveCount(1);
        rig.Ring.Single(static x => x.Action == "SearchRecords").Message.Should().Contain("; pruned to partition ");

        await page.FindAll(".ms-k-result-banner button").Single(static x => x.TextContent == "Search all partitions").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.FindAll("details.ms-k-progress tbody tr").Should().HaveCount(6));
        page.WaitForAssertion(() => page.FindAll(".ms-k-result-banner button").Should().NotContain(static x => x.TextContent == "Search all partitions"));
        rig.Ring.Count(static x => x.Action == "SearchRecords").Should().Be(2, "a new scan and a new audit entry");
    }
}
