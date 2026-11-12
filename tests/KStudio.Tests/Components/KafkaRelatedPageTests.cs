using AngleSharp.Dom;

using Bunit;

using KStudio.Components.Pages;
using KStudio.Components.Shared;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Web;

namespace KStudio.Tests.Components;

/// <summary>
/// The related-records page (D42, decision 3 of D-CORR), rendered against the real engine over the fake broker: nothing is read while prerendering,
/// the seed's fragment is read only once the circuit is interactive, a masked value is never drawn, and the scan starts only when the visitor presses
/// the button.
/// </summary>
public class KafkaRelatedPageTests
{
    private const string Sentinel = "S3NT1NEL-6a2f91";

    /// <summary>The seeded records are all inside this window; the default one is 30 minutes around the anchor, which also holds them.</summary>
    private const string Window = "&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z";

    private static Action<KafkaEngineOptions> Declare => o =>
    {
        o.Topic("orders.in", _ => { });
        o.Topic("orders.out", _ => { });
    };

    private static void Seed(KafkaSearchRig rig)
    {
        rig.Topic("orders.in");
        rig.Topic("orders.out");
        rig.Produce("orders.in", 0, 10, "k", "v", ("correlation-id", "c-1"), ("id", "m-1"));
        rig.Produce("orders.out", 0, 20, "k", "v", ("correlation-id", "c-1"), ("conversation-id", "m-1"));
        rig.Produce("orders.out", 0, 30, "k", "v", ("correlation-id", "c-2"));
    }

    private static async Task<IRenderedComponent<KafkaRelatedPage>> OpenAsync(KafkaSearchRig rig, string query, string fragment = "")
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/related?cluster=test" + query + fragment, intercepted: false);
        IRenderedComponent<KafkaRelatedPage> page = rig.Studio.Context.Render<KafkaRelatedPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-k-related-lead, .ms-empty, .ms-k-note").Should().NotBeEmpty());
        return page;
    }

    private static Task SearchAsync(IRenderedComponent<KafkaRelatedPage> page) =>
        page.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());

    private static string[] Rows(IRenderedComponent<KafkaRelatedPage> page) =>
        [.. page.FindAll("table.ms-k-related-matches > tbody > tr.ms-table-row").Select(static x => x.QuerySelectorAll("td")[1].TextContent.Trim() + " " + x.QuerySelectorAll("td")[2].TextContent.Trim())];

    [Fact]
    public async Task Prerendering_reads_nothing_and_the_seeds_fragment_is_read_only_after_Ready()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("orders.in", t => t.Correlation(c => c.Key().Document("Order"))));
        rig.Topic("orders.in");
        await rig.Studio.Context.ReadyAsync();
        await rig.Studio.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);

        // A prerendering request has no fragment: a browser never sends one.
        KafkaAddress.ReadSeed("http://localhost/postgres/kafka/related?cluster=test", 1_000).Should().BeNull();
        rig.Studio.Navigation.Simulate("kafka/related?cluster=test#link=document&type=Order&v=ORD-1", intercepted: false);

        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IRenderedComponent<KafkaRelatedBody> body = rig.Studio.Context.Render<KafkaRelatedBody>(p => p.AddCascadingValue(KafkaPageFrame.ReadyName, (Task) ready.Task));

        body.FindAll(".ms-k-keys").Should().BeEmpty("nothing is drawn before Ready");
        body.Markup.Should().NotContain("ORD-1");
        rig.BrokerCalls.Should().Be(0, "nothing is read before Ready");

        ready.SetResult();

        body.WaitForAssertion(() => body.Find(".ms-k-keys").TextContent.Should().Contain("document:Order").And.Contain("ORD-1"));
        rig.Ring.Should().BeEmpty("opening a seed searches nothing and opens no record");
    }

    [Fact]
    public async Task An_anchor_shows_its_keys_and_each_topic_and_runs_nothing_until_the_button()
    {
        await using KafkaSearchRig rig = new(engine: Declare);
        Seed(rig);

        IRenderedComponent<KafkaRelatedPage> page = await OpenAsync(rig, "&topic=orders.in&partition=0&offset=0" + Window);

        page.WaitForAssertion(() => page.FindAll(".ms-k-lane").Should().HaveCount(2));
        string keys = page.Find(".ms-k-keys").TextContent;
        keys.Should().Contain("correlation").And.Contain("c-1").And.Contain("message").And.Contain("m-1");
        page.FindAll(".ms-k-lane-head strong").Select(static x => x.TextContent.Trim()).Should().Equal("orders.in", "orders.out");
        page.FindAll(".ms-k-lane-query pre").Should().HaveCount(2);
        page.Find(".ms-k-lane-query pre").TextContent.Should().Contain("header.correlation-id = 'c-1'");
        page.FindAll("table.ms-k-related-matches").Should().BeEmpty("nothing is searched until the button");
        rig.Ring.Where(static x => x.Action == "SearchRecords").Should().BeEmpty();
        page.Find(".ms-k-search-actions button").TextContent.Should().Be("Search related records");

        await SearchAsync(page);

        page.WaitForAssertion(() => Rows(page).Should().Equal("orders.in 0 / 0", "orders.out 0 / 0"));
        IElement[] rows = [.. page.FindAll("table.ms-k-related-matches > tbody > tr.ms-table-row")];
        rows[0].GetAttribute("aria-current").Should().Be("true");
        rows[0].QuerySelectorAll("td")[4].TextContent.Trim().Should().Be("this record");
        rows[1].QuerySelectorAll("td")[4].TextContent.Should().Contain("correlation (header.correlation-id)").And.Contain("message (header.conversation-id)");
        page.Find(".ms-k-strip-summary").TextContent.Should().Be("2 records on 2 topics from 12:00:10 to 12:00:20 UTC");
        page.FindAll(".ms-k-strip-dot").Should().HaveCount(2);
        page.Find(".ms-k-strip-lanes").GetAttribute("aria-hidden").Should().Be("true");
        rig.Ring.Where(static x => x.Action == "SearchRecords").Should().ContainSingle().Which.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task The_keys_panel_and_everything_after_it_never_show_a_masked_value()
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Topic("orders.in", t => t.Redact(r => r.Header("correlation-id").Searchable()));
            o.Topic("orders.out", _ => { });
        });
        rig.Topic("orders.in");
        rig.Topic("orders.out");
        rig.Produce("orders.in", 0, 10, "k", "v", ("correlation-id", Sentinel), ("id", "m-1"));
        rig.Produce("orders.out", 0, 20, "k", "v", ("correlation-id", "c-9"), ("conversation-id", "m-1"));

        IRenderedComponent<KafkaRelatedPage> page = await OpenAsync(rig, "&topic=orders.in&partition=0&offset=0" + Window);
        page.WaitForAssertion(() => page.FindAll(".ms-k-lane").Should().HaveCount(2));

        page.Markup.Should().NotContain(Sentinel);
        page.Find(".ms-k-keys").TextContent.Should().Contain("m-1").And.NotContain("correlation");

        await SearchAsync(page);
        page.WaitForAssertion(() => Rows(page).Should().NotBeEmpty());

        page.Markup.Should().NotContain(Sentinel);
        rig.Ring.Select(static x => x.Message + x.Target).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_key_the_topics_format_cannot_hold_is_a_note_in_its_lane()
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Topic("orders.in", _ => { });
            o.Topic("orders.out", t => t.Key(KafkaKeyFormat.Int64BigEndian).Correlation(c => c.Key().As("correlation")));
        });
        Seed(rig);

        IRenderedComponent<KafkaRelatedPage> page = await OpenAsync(rig, "&topic=orders.in&partition=0&offset=0" + Window);
        page.WaitForAssertion(() => page.FindAll(".ms-k-lane").Should().HaveCount(2));

        IElement lane = page.FindAll(".ms-k-lane").Single(static x => x.QuerySelector("strong")!.TextContent.Trim() == "orders.out");
        lane.QuerySelectorAll(".ms-k-lane-note").Select(static x => x.TextContent.Trim())
            .Should().Contain("key: 1 value cannot be held in this topic's key format and was not searched.");
        page.FindAll(".ms-k-lane").Single(static x => x.QuerySelector("strong")!.TextContent.Trim() == "orders.in").QuerySelectorAll(".ms-k-lane-note").Should().BeEmpty();
    }

    [Fact]
    public async Task A_record_with_no_usable_key_offers_nothing_to_run()
    {
        await using KafkaSearchRig rig = new(engine: Declare);
        Seed(rig);
        rig.Produce("orders.in", 0, 40, "k", "v");

        IRenderedComponent<KafkaRelatedPage> page = await OpenAsync(rig, "&topic=orders.in&partition=0&offset=1" + Window);

        page.Find(".ms-k-keys").TextContent.Should().Contain(KafkaStudioText.RelatedNoKeys);
        page.FindAll(".ms-k-search-actions button").Should().BeEmpty();
        page.FindAll(".ms-k-lane").Should().BeEmpty();
    }

    [Fact]
    public async Task A_link_naming_nothing_says_so()
    {
        await using KafkaSearchRig rig = new(engine: Declare);
        Seed(rig);

        IRenderedComponent<KafkaRelatedPage> page = await OpenAsync(rig, "");

        page.Find(".ms-empty").TextContent.Should().Contain(KafkaStudioText.RelatedLinkInvalid);
        rig.BrokerCalls.Should().Be(0);
    }

    [Fact]
    public async Task More_than_twenty_declared_topics_say_so_and_offer_a_picker()
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            for (int i = 0; i < 21; i++)
            {
                o.Topic("t" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), _ => { });
            }
        });

        for (int i = 0; i < 21; i++)
        {
            rig.Topic("t" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        }

        rig.Produce("t00", 0, 10, "k", "v", ("correlation-id", "c-1"));

        IRenderedComponent<KafkaRelatedPage> page = await OpenAsync(rig, "&topic=t00&partition=0&offset=0" + Window);

        page.WaitForAssertion(() => page.Markup.Should().Contain("1 more declared topic was not searched; choose the topics to search."));
        page.FindAll(".ms-k-related-topics label.ms-k-topic-option").Should().HaveCount(21);
        page.FindAll(".ms-k-related-topics input[checked], .ms-k-related-topics input:checked").Should().HaveCount(20);
    }

    [Fact]
    public async Task A_refused_declared_topic_is_not_a_lane_and_is_named_nowhere()
    {
        await using KafkaSearchRig rig = new(engine: Declare);
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "orders.out");
        Seed(rig);

        IRenderedComponent<KafkaRelatedPage> page = await OpenAsync(rig, "&topic=orders.in&partition=0&offset=0" + Window);
        page.WaitForAssertion(() => page.FindAll(".ms-k-lane").Should().HaveCount(1));

        page.Markup.Should().NotContain("orders.out");
    }

    [Fact]
    public async Task Continue_keeps_each_topics_own_search_and_records_a_second_scan()
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Topic("orders.in", _ => { });
            o.Topic("orders.out", _ => { });
            o.Limits.MaxMatches = 1;
        });
        Seed(rig);

        IRenderedComponent<KafkaRelatedPage> page = await OpenAsync(rig, "&topic=orders.in&partition=0&offset=0" + Window);
        await SearchAsync(page);
        page.WaitForAssertion(() => page.FindAll(".ms-k-result-banner button").Should().ContainSingle());

        await page.Find(".ms-k-result-banner button").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => rig.Ring.Count(static x => x.Action == "SearchRecords").Should().BeGreaterThanOrEqualTo(2));
        page.Markup.Should().Contain("Continued after offsets read by the previous search");
        rig.Ring.Where(static x => x.Action == "SearchRecords").Should().OnlyContain(static x => x.Message!.Contains("orders.in, orders.out: "), "each scan's entry carries the search of every topic, grouped where it is shared");
    }

    [Fact]
    public async Task Duplicates_under_the_results_are_hidden_until_there_is_a_group()
    {
        await using KafkaSearchRig rig = new(engine: Declare);
        Seed(rig);
        IRenderedComponent<KafkaRelatedPage> page = await OpenAsync(rig, "&topic=orders.in&partition=0&offset=0" + Window);
        await SearchAsync(page);
        page.WaitForAssertion(() => Rows(page).Should().HaveCount(2));
        page.FindAll("details.ms-k-duplicates").Should().BeEmpty("no two records on one topic carry one identity");

        await using KafkaSearchRig again = new(engine: Declare);
        Seed(again);
        again.Produce("orders.out", 0, 25, "k", "v", ("correlation-id", "c-1"), ("id", "dup-1"));
        again.Produce("orders.out", 0, 26, "k", "v", ("correlation-id", "c-1"), ("id", "dup-1"));
        IRenderedComponent<KafkaRelatedPage> second = await OpenAsync(again, "&topic=orders.in&partition=0&offset=0" + Window);
        await SearchAsync(second);

        second.WaitForAssertion(() => second.FindAll("details.ms-k-duplicates").Should().HaveCount(1));
        second.Find("details.ms-k-duplicates summary").TextContent.Should().Be("Possible duplicates: 1 group");
        second.Find("details.ms-k-duplicates").HasAttribute("open").Should().BeFalse("the panel is collapsed by default");
        second.FindAll("details.ms-k-duplicates a").Select(static x => x.GetAttribute("href")).Should().OnlyContain(static x => x!.Contains("kafka/record") && x.Contains("topic=orders.out"));
    }
}
