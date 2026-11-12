using System.Text.RegularExpressions;

using AngleSharp.Dom;

using Bunit;

using KStudio.Components.Pages;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace KStudio.Tests.Components;

/// <summary>The search page (D-UI decisions 10 to 13), rendered against the real engine over the fake broker.</summary>
public class KafkaSearchPageTests
{
    private const string Sentinel = "S3NT1NEL-5e0b77";

    /// <summary>The seeded records are all inside this window; a relative one would be the last hour of the real clock.</summary>
    private const string Window = "&topic=orders&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z";

    private static async Task<IRenderedComponent<KafkaSearchPage>> OpenAsync(KafkaSearchRig rig, string query = "")
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test" + query, intercepted: false);
        IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();
        page.WaitForAssertion(() => page.FindAll("textarea.ms-k-predicate").Should().HaveCount(1));
        return page;
    }

    private static void Seed(KafkaSearchRig rig, string topic = "orders", int count = 3)
    {
        rig.Topic(topic);

        for (int i = 0; i < count; i++)
        {
            rig.Produce(topic, 0, i, "k" + i, "v" + i, ("correlation-id", "c-" + i), ("customer-email", Sentinel + i));
        }
    }

    private static async Task CheckAsync(IRenderedComponent<KafkaSearchPage> page, string topic)
    {
        IElement box = page.FindAll("label.ms-k-topic-option").Single(x => x.TextContent.Trim() == topic).QuerySelector("input")!;
        await box.ChangeAsync(new ChangeEventArgs { Value = true });
    }

    private static Task TypeAsync(IRenderedComponent<KafkaSearchPage> page, string text) =>
        page.Find("textarea.ms-k-predicate").InputAsync(new ChangeEventArgs { Value = text });

    private static Task SearchAsync(IRenderedComponent<KafkaSearchPage> page) =>
        page.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());

    private static string[] Rows(IRenderedComponent<KafkaSearchPage> page) =>
        [.. page.FindAll("table.ms-k-matches > tbody > tr.ms-table-row").Select(static x => x.QuerySelectorAll("td")[2].TextContent.Trim())];

    [Fact]
    public async Task The_fragment_fills_the_predicate_after_Ready_and_not_before()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig);
        await rig.Studio.Context.ReadyAsync();

        // A prerendering request has no fragment: a browser never sends one.
        KafkaAddress.ReadPredicate("http://localhost/postgres/kafka/search?cluster=test&topic=orders", 4_096).Should().BeNull();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test&topic=orders#q=" + Uri.EscapeDataString("key = 'k1'"), intercepted: false);
        rig.BrokerCalls.Should().Be(0, "nothing is read before the frame is Ready");

        IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();

        page.WaitForAssertion(() => page.Find("textarea.ms-k-predicate").GetAttribute("value").Should().Be("key = 'k1'"));
    }

    [Fact]
    public async Task A_pasted_link_fills_the_form_and_does_not_run()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig);
        string link = "&topic=orders&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z&iso=uncommitted#q=" + Uri.EscapeDataString("header.correlation-id = 'c-1'");

        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, link);

        page.WaitForAssertion(() => page.FindAll("label.ms-k-topic-option input:checked, label.ms-k-topic-option input[checked]").Should().HaveCount(1));
        page.Find("textarea.ms-k-predicate").GetAttribute("value").Should().Be("header.correlation-id = 'c-1'");
        page.Find("input[type=datetime-local]").GetAttribute("value").Should().Be("2026-11-08T11:00");
        page.Markup.Should().Contain("11:00–13:00 UTC");
        rig.Ring.Should().BeEmpty("a pasted link never spends a budget or a confirmation on the visitor's behalf");
        page.FindAll("table.ms-k-matches").Should().BeEmpty();
        page.Find(".ms-k-search-actions button").TextContent.Should().Be("Search");
    }

    [Fact]
    public async Task A_refused_topic_in_the_link_is_dropped_with_the_notice()
    {
        await using KafkaSearchRig rig = new();
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "secret");
        Seed(rig, "orders");
        Seed(rig, "secret");

        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, "&topic=orders&topic=secret");

        page.WaitForAssertion(() => page.Markup.Should().Contain("This link named 1 topic(s) you cannot search here; the search below leaves them out."));
        page.FindAll("label.ms-k-topic-option").Select(static x => x.TextContent.Trim()).Should().Equal("orders");
        page.Find(".ms-k-topic-count").TextContent.Should().Contain("1 of 20");
    }

    [Fact]
    public async Task Errors_show_positions_and_no_literal()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("orders", t => t.Redact(r => r.Header("customer-email").Searchable())));
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, "&topic=orders");

        await TypeAsync(page, $"header.customer-email ~ '{Sentinel}'");
        await SearchAsync(page);

        page.WaitForAssertion(() => page.FindAll(".ms-k-predicate-errors li").Should().NotBeEmpty());
        page.Find(".ms-k-predicate-errors").TextContent.Should().Contain("Position ").And.NotContain(Sentinel);
        page.Find(".ms-k-predicate-errors").InnerHtml.Should().NotContain(Sentinel);
        page.Find(".ms-k-predicate-marks").GetAttribute("aria-hidden").Should().Be("true");
        page.Find(".ms-k-predicate-marks mark").Should().NotBeNull();
        page.FindAll("table.ms-k-matches").Should().BeEmpty();
        rig.Ring.Should().ContainSingle().Which.Message.Should().Be(KafkaStudioText.PredicateInvalid);
    }

    [Fact]
    public async Task Verdicts_and_notes_show_before_the_scan()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Limits.ConfirmAboveRecords = 1);
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);

        await TypeAsync(page, "header.correlation-id = 'c-1'");
        await SearchAsync(page);

        page.WaitForAssertion(() => page.FindAll("dialog").Should().HaveCount(1));
        page.Find("details.ms-k-verdicts").TextContent.Should().Contain("orders · header.correlation-id:");
        page.Find(".ms-k-estimate").TextContent.Should().Contain("offsets");
        page.FindAll("table.ms-k-matches").Should().BeEmpty("nothing is read before the visitor confirms");
        rig.Ring.Should().BeEmpty();
    }

    [Fact]
    public async Task Above_the_threshold_the_dialog_asks_first()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Limits.ConfirmAboveRecords = 1);
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);

        await SearchAsync(page);

        page.WaitForAssertion(() => page.Find("dialog h3").TextContent.Should().Be("Read about 3 offsets?"));
        page.Find("dialog p").TextContent.Should().Contain("will be read from the cluster, which counts against your hourly allowance").And.Contain("may cost money on a managed cluster.");
        rig.Ring.Should().BeEmpty();

        await page.Find("dialog .ms-confirm-actions button.ms-button-danger").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => Rows(page).Should().HaveCount(3));
        rig.Ring.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_at_the_dialog_records_not_confirmed()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Limits.ConfirmAboveRecords = 1);
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);
        await SearchAsync(page);
        page.WaitForAssertion(() => page.FindAll("dialog").Should().HaveCount(1));

        await page.Find("dialog .ms-confirm-actions button:not(.ms-button-danger)").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.FindAll("dialog").Should().BeEmpty());
        rig.Ring.Should().ContainSingle().Which.Should().Match<PostgresStudio.Services.StudioActionLogEntry>(static x => !x.Succeeded && x.Message == "not confirmed");
        Rows(page).Should().BeEmpty();
    }

    [Fact]
    public async Task The_address_gets_the_audit_text_in_the_fragment()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("orders", t => t.Redact(r => r.Header("customer-email").Searchable())));
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);

        await TypeAsync(page, $"header.customer-email = '{Sentinel}1'");
        await SearchAsync(page);

        page.WaitForAssertion(() => Rows(page).Should().HaveCount(1));
        (string uri, NavigationOptions options) = rig.Studio.Navigation.Requests.Last();
        options.ReplaceHistoryEntry.Should().BeTrue();
        uri.Should().NotContain(Sentinel);
        Uri.UnescapeDataString(uri).Should().Contain("#q=header.customer-email = <redacted>", "a redacted literal is written <redacted> (%3Credacted%3E in the fragment)");
    }

    [Fact]
    public async Task The_address_holds_the_absolute_window()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, "&topic=orders");

        await SearchAsync(page);

        page.WaitForAssertion(() => rig.Studio.Navigation.Requests.Should().NotBeEmpty());
        string uri = rig.Studio.Navigation.Requests.Last().Uri;
        Regex.IsMatch(uri, @"[?&]from=\d{4}-\d{2}-\d{2}T\d{2}%3A\d{2}%3A\d{2}Z", RegexOptions.None, TimeSpan.FromSeconds(5)).Should().BeTrue(uri);
        Regex.IsMatch(uri, @"[&]to=\d{4}-\d{2}-\d{2}T\d{2}%3A\d{2}%3A\d{2}Z", RegexOptions.None, TimeSpan.FromSeconds(5)).Should().BeTrue(uri);
        uri.Should().Contain("cluster=test").And.Contain("topic=orders").And.NotContain("#q=", "no predicate, no fragment");
    }

    [Fact]
    public async Task Batches_append_rows_with_keys()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);

        await SearchAsync(page);

        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 0", "0 / 1", "0 / 2"));
        page.FindAll("table.ms-k-matches > tbody > tr.ms-table-row").Select(static x => x.QuerySelectorAll("td")[3].TextContent.Trim()).Should().Equal("k0", "k1", "k2");
        page.FindAll(".ms-stat-grid .ms-stat-card, .ms-stat-grid > *").Should().NotBeEmpty();
        page.FindAll("button.ms-k-chip").Select(static x => x.TextContent.Trim()).Should().Contain("correlation-id c-0");
        page.Markup.Should().Contain("Read every offset in the window: 3 records,");
    }

    [Fact]
    public async Task A_chip_fills_the_box_and_does_not_run()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);
        await SearchAsync(page);
        page.WaitForAssertion(() => Rows(page).Should().HaveCount(3));
        int entries = rig.Ring.Count;

        await page.FindAll("button.ms-k-chip").First(static x => x.TextContent.Contains("c-2", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());

        page.Find("textarea.ms-k-predicate").GetAttribute("value").Should().Be("header.correlation-id = 'c-2'");
        rig.Ring.Count.Should().Be(entries, "a chip fills the box; the visitor runs the search");
    }

    [Fact]
    public async Task A_no_match_shows_its_count_and_every_caveat()
    {
        await using KafkaSearchRig rig = new();
        rig.Cluster.CreateTopic("state", 1, new Dictionary<string, string> { ["cleanup.policy"] = "compact" });
        rig.Produce("state", 0, 1, "k1", "v1", ("correlation-id", "c-1"));
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, "&topic=state&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z");

        await TypeAsync(page, "key = 'nope'");
        await SearchAsync(page);

        page.WaitForAssertion(() => page.Find(".ms-k-result-banner").TextContent.Should().Contain("No match in the 1 records read from 1 partitions."));
        page.Find(".ms-k-caveats").TextContent.Should().Contain("What this result does not cover").And.Contain("Compacted topic: a time-window search can miss superseded keys and sees tombstones");
        page.Markup.Should().NotContain("not found");
    }

    [Fact]
    public async Task Continue_appears_only_with_a_cursor_and_replaces_the_rows()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Limits.MaxMatches = 2);
        Seed(rig, "orders", 5);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);

        await SearchAsync(page);
        page.WaitForAssertion(() => page.Find(".ms-k-result-banner").TextContent.Should().Contain("Stopped at 2 matches, the most a search keeps (KafkaStudioOptions.Limits.MaxMatches). Continue reads on from where it stopped."));
        Rows(page).Should().Equal("0 / 0", "0 / 1");

        await page.Find(".ms-k-result-banner button").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 2", "0 / 3"));
        page.Markup.Should().Contain("Continued after offsets read by the previous search (");

        await page.Find(".ms-k-result-banner button").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 4"));
        page.FindAll(".ms-k-result-banner button").Should().BeEmpty("a completed search has no cursor to continue from");
        rig.Ring.Count(static x => x.Action == "SearchRecords" && x.Succeeded).Should().Be(3, "each scan is a new audit entry");
    }

    [Fact]
    public async Task Leaving_the_page_after_a_scan_records_it_once()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);
        await SearchAsync(page);
        page.WaitForAssertion(() => Rows(page).Should().HaveCount(3));

        page.Dispose();

        rig.Ring.Count(static x => x.Action == "SearchRecords").Should().Be(1);
    }

    [Fact]
    public async Task Capability_off_shows_the_disabled_notice_naming_the_option()
    {
        await using KafkaSearchRig rig = new(searchOn: false);
        Seed(rig);
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test", intercepted: false);

        IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();

        page.WaitForAssertion(() => page.Markup.Should().Contain("KafkaStudioOptions.Capabilities.SearchRecords"));
        page.FindAll("textarea").Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOnly_shows_the_hosts_option()
    {
        await using KafkaSearchRig rig = new();
        rig.Studio.Context.Options.ReadOnly = true;
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test", intercepted: false);

        IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();

        page.WaitForAssertion(() => page.Find(".ms-capability-disabled").TextContent.Should().Contain("ReadOnly").And.Contain("turns every capability off"));
    }

    [Fact]
    public async Task More_than_twenty_topics_cannot_be_checked()
    {
        await using KafkaSearchRig rig = new();
        for (int i = 0; i < 22; i++)
        {
            rig.Topic("t" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        }

        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig);
        page.WaitForAssertion(() => page.FindAll("label.ms-k-topic-option").Should().HaveCount(22));
        for (int i = 0; i < 20; i++)
        {
            // The handlers are re-created by every render: find the box again each time.
            await page.FindAll("label.ms-k-topic-option input")[i].ChangeAsync(new ChangeEventArgs { Value = true });
        }

        page.Find(".ms-k-topic-count").TextContent.Should().Contain("20 of 20");
        page.FindAll("label.ms-k-topic-option input[disabled]").Should().HaveCount(2);
    }

    [Fact]
    public void Each_stop_reason_shows_its_sentence()
    {
        Func<KafkaScanStopReason, string> expected = static reason => reason switch
        {
            KafkaScanStopReason.Completed => "Read every offset in the window: 1,200 records, 2 KiB, 3 matches.",
            KafkaScanStopReason.MatchLimit => "Stopped at 500 matches, the most a search keeps (KafkaStudioOptions.Limits.MaxMatches). Continue reads on from where it stopped.",
            KafkaScanStopReason.ByteBudget => "Stopped after reading 2 KiB, this search's budget (KafkaStudioOptions.Limits.MaxBytesPerScan). Continue reads on from where it stopped.",
            KafkaScanStopReason.RecordBudget => "Stopped after 1,200 records, this search's budget (KafkaStudioOptions.Limits.MaxRecordsPerScan). Continue reads on from where it stopped.",
            KafkaScanStopReason.OwnerBudget => "Stopped: you have read your hourly allowance. Continue reads on from where it stopped, once the allowance frees up.",
            KafkaScanStopReason.Deadline => "Stopped after 2 min, the longest a search runs (KafkaStudioOptions.Limits.MaxScanDuration). Continue reads on from where it stopped.",
            KafkaScanStopReason.Cancelled => "Stopped by you. Continue reads on from where it stopped.",
            KafkaScanStopReason.Shutdown => "Stopped: the application is shutting down.",
            KafkaScanStopReason.NotAuthorized => "Nothing was read: every partition refused READ.",
            KafkaScanStopReason.Unavailable => "Stopped: the cluster stopped answering.",
            KafkaScanStopReason.Failed => "The search failed. The application's log has the reason.",
            _ => throw new InvalidOperationException(reason.ToString()),
        };

        foreach (KafkaScanStopReason reason in Enum.GetValues<KafkaScanStopReason>().Where(static x => x != KafkaScanStopReason.CallerGone))
        {
            KafkaScanResult result = new(
                Guid.Empty,
                reason,
                TimeSpan.FromSeconds(3),
                [],
                1_200,
                2_048,
                3,
                0,
                0,
                0,
                0,
                new Dictionary<KafkaUnknownReason, long>(),
                [],
                [],
                [],
                new KafkaScanCursor([]),
                new DecodeCounters(),
                new DecodeCounters());

            KafkaSearchText.Stop(result, new KStudio.KafkaScanLimits()).Should().Be(expected(reason), reason.ToString());
        }
    }
}
