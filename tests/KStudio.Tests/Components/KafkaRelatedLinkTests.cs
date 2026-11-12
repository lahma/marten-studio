using Bunit;

using KStudio.Components.Pages;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Web;

namespace KStudio.Tests.Components;

/// <summary>How the rest of KStudio reaches related records, and the seed's address (decision 3.1 of D-CORR): a link, never a scan.</summary>
public class KafkaRelatedLinkTests
{
    private const string Window = "&topic=orders&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z";

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_record_detail_offers_related_records_as_a_link_to_the_anchors_permalink_parts()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k", "v", ("correlation-id", "c-1"));
        KafkaMatch match = ((KafkaRecordPageResult.Read) await rig.Records().ReadAsync(rig.Entry, "orders", 0, 0, Ct)).Value.Record!;

        IRenderedComponent<KStudio.Components.Shared.KafkaRecordDetail> detail = rig.Studio.Context.Render<KStudio.Components.Shared.KafkaRecordDetail>(p => p
            .Add(x => x.Model, match)
            .Add(x => x.Cluster, rig.Entry.Key));

        string href = detail.Find("a.ms-k-related-link").GetAttribute("href")!;
        href.Should().Contain("kafka/related?cluster=test").And.Contain("topic=orders").And.Contain("partition=0").And.Contain("offset=0");
        href.Should().NotContain("c-1", "the anchor's address carries no record data");
        detail.Find("a.ms-k-related-link").TextContent.Should().Be("Related records");
        rig.BrokerCalls.Should().BeGreaterThan(0);
        rig.Ring.Where(static x => x.Action == "SearchRecords").Should().BeEmpty("a link runs nothing");
    }

    [Fact]
    public async Task A_search_result_row_has_a_related_link()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k", "v", ("correlation-id", "c-1"));
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test" + Window, intercepted: false);
        Bunit.IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();
        page.WaitForAssertion(() => page.FindAll("textarea.ms-k-predicate").Should().HaveCount(1));

        await page.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.FindAll("table.ms-k-matches > tbody > tr.ms-table-row").Should().HaveCount(1));
        page.Find("table.ms-k-matches a.ms-k-related-link").GetAttribute("href").Should().Contain("kafka/related?cluster=test").And.Contain("topic=orders").And.Contain("offset=0");
        page.FindAll("details.ms-k-duplicates").Should().BeEmpty("one record is no duplicate");
    }

    [Fact]
    public async Task Search_results_show_possible_duplicates_when_two_records_carry_one_identity()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v", ("id", "env-1"));
        rig.Produce("orders", 0, 11, "k2", "v", ("id", "env-1"));
        rig.Produce("orders", 0, 12, "k3", "v", ("id", "env-2"));
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test" + Window, intercepted: false);
        Bunit.IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();
        page.WaitForAssertion(() => page.FindAll("textarea.ms-k-predicate").Should().HaveCount(1));

        await page.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.FindAll("details.ms-k-duplicates").Should().HaveCount(1));
        page.Find("details.ms-k-duplicates summary").TextContent.Should().Be("Possible duplicates: 1 group");
        page.Find("details.ms-k-duplicates > p").TextContent.Should().Be("Compared among the 3 records found. A copy outside these results is not seen.");
        page.FindAll("details.ms-k-duplicates tbody tr").Should().HaveCount(2);
    }

    [Fact]
    public void A_seed_is_read_from_the_fragment_and_never_from_the_query()
    {
        KafkaRelatedSeed? document = KafkaAddress.ReadSeed("http://h/kafka/related?cluster=a&v=nope#link=document&type=Order&store=main&v=ORD%2F1", 1_000);
        KafkaRelatedSeed? stream = KafkaAddress.ReadSeed("http://h/kafka/related?cluster=a#link=stream&v=7c3f", 1_000);

        document.Should().Be(new KafkaRelatedSeed("document", "Order", "main", "ORD/1"));
        document!.CorrelationKind.Should().Be("document:Order");
        stream.Should().Be(new KafkaRelatedSeed("stream", null, null, "7c3f"));
        stream!.CorrelationKind.Should().Be("stream");
        KafkaAddress.ReadSeed("http://h/kafka/related?cluster=a&v=nope&link=document&type=Order", 1_000).Should().BeNull("a query string value is not a seed");
    }

    [Theory]
    [InlineData("#link=document&v=1")]
    [InlineData("#link=document&type=Order")]
    [InlineData("#link=table&type=Order&v=1")]
    [InlineData("#link=stream&v=")]
    [InlineData("#v=1")]
    [InlineData("")]
    public void A_fragment_that_names_no_value_is_no_seed(string fragment)
    {
        KafkaAddress.ReadSeed("http://h/kafka/related?cluster=a" + fragment, 1_000).Should().BeNull();
    }

    [Fact]
    public void A_seed_longer_than_the_bound_is_cut_one_past_it_so_the_service_can_refuse_it()
    {
        KafkaRelatedSeed seed = KafkaAddress.ReadSeed("http://h/kafka/related#link=stream&v=" + new string('x', 5_000), KafkaCorrelationService.MaxSeedValueLength)!;

        seed.Value.Length.Should().Be(KafkaCorrelationService.MaxSeedValueLength + 1);
    }
}
