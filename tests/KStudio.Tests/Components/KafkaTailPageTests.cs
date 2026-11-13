using Bunit;

using KStudio.Components.Pages;
using KStudio.Components.Shared;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Scanning;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using PostgresStudio.Services;
using PostgresStudio.Services.Live;

namespace KStudio.Tests.Components;

/// <summary>
/// The live tail page (D-M5 decisions 3.6 and 3.7, AGENTS.md D46), rendered against the real engine over the fake broker: only a click starts a tail, rows arrive
/// newest first, a pause frees the engine and a resume goes on from the exact cursor, a hidden tab pauses and a shown one resumes what it paused, a tail is one
/// audit entry however many segments it read in, and the disabled states name the setting a host would change.
/// </summary>
public class KafkaTailPageTests
{
    private const string Sentinel = "S3NT1NEL-0d77c2";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static KafkaSearchRig Rig(KafkaProfile? profile = null, bool tailOn = true, bool exportOn = false, bool searchOn = true, Action<KafkaEngineOptions>? engine = null) =>
        new(
            engine: engine,
            studio: o =>
            {
                o.Capabilities.LiveTail = tailOn;
                o.Capabilities.ExportRecords = exportOn;
            },
            searchOn: searchOn,
            traceCircuit: true,
            profile: profile);

    private static void Seed(KafkaSearchRig rig, string topic = "orders", int count = 3)
    {
        rig.Topic(topic);

        for (int i = 0; i < count; i++)
        {
            rig.Produce(topic, 0, i, "k" + i, "v" + i, ("correlation-id", "c-" + i));
        }
    }

    private static async Task<IRenderedComponent<KafkaTailPage>> OpenAsync(KafkaSearchRig rig, string query = "&topics=orders")
    {
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/tail?cluster=test" + query, intercepted: false);
        IRenderedComponent<KafkaTailPage> page = rig.Studio.Context.Render<KafkaTailPage>();
        page.WaitForAssertion(() => page.FindAll("textarea.ms-k-predicate, .ms-capability-disabled, .ms-k-tail-forbidden").Should().HaveCount(1), Wait);
        return page;
    }

    /// <summary>Clicks the button with this text. The handler of Start and Resume lasts as long as the tail reads: do not await it until the tail has ended.</summary>
    private static Task ClickAsync(IRenderedComponent<KafkaTailPage> page, string text) =>
        page.FindAll("button").Single(x => x.TextContent.Trim() == text).ClickAsync(new MouseEventArgs());

    private static string[] Rows(IRenderedComponent<KafkaTailPage> page) =>
        [.. page.FindAll("table.ms-k-tail-matches > tbody > tr.ms-table-row").Select(static x => x.QuerySelectorAll("td")[2].TextContent.Trim())];

    private static void WaitUntilLive(IRenderedComponent<KafkaTailPage> page) =>
        page.WaitForAssertion(() => page.Find(".ms-k-tail-chip-live").TextContent.Should().Be("1 live"), Wait);

    private static DotNetObjectReference<StudioLiveUpdates> TabWatch(KafkaSearchRig rig) =>
        (DotNetObjectReference<StudioLiveUpdates>) rig.Studio.Context.JSInterop.Invocations["martenStudio.visibility.watch"].Single().Arguments[1]!;

    /// <summary>Lets every handler of the tab's last change hear of it, and the renderer do what they queued.</summary>
    private static async Task SettleAsync(KafkaSearchRig rig, DotNetObjectReference<StudioLiveUpdates> watch)
    {
        await watch.Value.VisibilityChangesRaised.Bounded();
        await rig.Studio.Context.Renderer.Dispatcher.InvokeAsync(static () => { });
    }

    // ---- the disabled states

    [Fact]
    public async Task Without_the_capability_the_page_names_the_option_and_reads_nothing()
    {
        await using KafkaSearchRig rig = Rig(tailOn: false);
        Seed(rig);

        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        page.Find(".ms-capability-disabled").TextContent.Should().Contain("Live tail is not enabled").And.Contain("KafkaStudioOptions.Capabilities.LiveTail");
        page.FindAll("textarea").Should().BeEmpty();
        rig.BrokerCalls.Should().Be(0);
    }

    [Fact]
    public async Task Without_SearchRecords_the_page_names_that_option()
    {
        await using KafkaSearchRig rig = Rig(searchOn: false);
        Seed(rig);

        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        page.Find(".ms-capability-disabled").TextContent.Should().Contain("KafkaStudioOptions.Capabilities.SearchRecords");
        page.FindAll("textarea").Should().BeEmpty();
    }

    [Fact]
    public async Task On_a_Production_cluster_the_page_says_the_profile_forbids_it_and_names_the_setting()
    {
        await using KafkaSearchRig rig = Rig(profile: KafkaProfile.Production);
        Seed(rig);

        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        page.Find(".ms-k-tail-forbidden").TextContent.Should().Be(KafkaStudioText.ProfileForbids(KafkaStudioText.LiveTailAction, rig.Entry));
        page.Find(".ms-k-tail-forbidden").TextContent.Should().Contain("KafkaClusterBuilder.Profile").And.Contain("even when they are granted");
        page.FindAll("textarea").Should().BeEmpty("there is no form: granting the capability is not enough");
    }

    // ---- the address

    [Fact]
    public async Task A_pasted_link_fills_the_form_and_never_starts_a_tail()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        string link = "&topics=orders&iso=uncommitted#q=" + Uri.EscapeDataString("header.correlation-id = 'c-1'");

        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig, link);

        page.WaitForAssertion(() => page.FindAll("label.ms-k-topic-option input:checked, label.ms-k-topic-option input[checked]").Should().HaveCount(1), Wait);
        page.Find("textarea.ms-k-predicate").GetAttribute("value").Should().Be("header.correlation-id = 'c-1'");
        page.Find(".ms-k-isolation input").HasAttribute("checked").Should().BeTrue();
        page.FindAll(".ms-k-tail-line").Should().BeEmpty();
        page.FindAll("table").Should().BeEmpty();
        page.Find(".ms-k-tail-actions button").TextContent.Should().Be("Start");
        rig.Ring.Should().BeEmpty("a pasted link spends nothing and holds no place");
        rig.CircuitLogs.Entries.Where(static x => x.EventId.Id is >= 9600 and <= 9609).Should().BeEmpty();
        rig.ListOffsetCalls.Should().Be(0, "a link makes no plan: planning reads the offsets");
    }

    // ---- running, pausing, resuming

    [Fact]
    public async Task A_tail_shows_what_arrives_newest_first_and_a_pause_and_a_resume_lose_nothing_and_it_is_one_entry()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig, count: 3);
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task start = ClickAsync(page, "Start");
        page.WaitForAssertion(() => page.Find(".ms-k-tail-line").TextContent.Should().StartWith("Live: reading orders from the end"), Wait);
        WaitUntilLive(page);
        Rows(page).Should().BeEmpty("a tail starts at the end: the three records already there are not read");
        page.Find(".ms-k-tail-line").TextContent.Should().Contain("pauses itself after 5 minutes");
        page.Find(".ms-k-tail-actions").TextContent.Should().Contain("Pause").And.Contain("Stop");

        rig.Produce("orders", 0, 10, "n1", "x");
        rig.Produce("orders", 0, 11, "n2", "x");
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 4", "0 / 3"), Wait);

        await ClickAsync(page, "Pause");
        page.WaitForAssertion(() => page.Find(".ms-k-tail-paused").TextContent.Should().Contain("Paused: you paused it. 2 records shown."), Wait);
        await start.Bounded();
        page.Find(".ms-k-tail-paused").TextContent.Should().Contain("Resume").And.Contain("Jump to now");
        rig.Ring.Should().BeEmpty("a pause writes nothing");

        // Produced while paused: the resume goes on from the exact cursor, so it is not missed.
        rig.Produce("orders", 0, 12, "n3", "x");
        Task resume = ClickAsync(page, "Resume");
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 5", "0 / 4", "0 / 3"), Wait);
        page.WaitForAssertion(() => page.Find(".ms-k-tail-line").TextContent.Should().Contain("on from where it paused"), Wait);

        await ClickAsync(page, "Stop");
        await resume.Bounded();

        page.Find(".ms-k-result-banner").TextContent.Should().Contain("Stopped by you.");
        StudioActionLogEntry entry = rig.Ring.Should().ContainSingle("a tail is one entry however many segments it read in").Subject;
        entry.Action.Should().Be("LiveTail");
        entry.Message.Should().Contain("2 segments").And.Contain("3 records").And.Contain("ended by Cancelled");
        rig.CircuitLogs.Entries.Select(static x => x.EventId.Id).Where(static x => x is 9607 or 9608).Should().Equal(9607, 9608);
    }

    [Fact]
    public async Task A_tail_the_cluster_stopped_answering_for_offers_Resume_from_its_exact_cursor()
    {
        // The first reader never sees a record or a partition end: the partition stays behind, makes no progress, and the tail stops Unavailable after the stall timeout.
        int opened = 0;
        await using KafkaSearchRig rig = new(
            engine: o => o.Limits.PartitionStallTimeout = TimeSpan.FromSeconds(1),
            studio: o => o.Capabilities.LiveTail = true,
            wrap: wire => new ScriptedWire(wire, options => Interlocked.Increment(ref opened) == 1 ? new ScriptedReader() : wire.OpenReader(options)));
        Seed(rig);
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task start = ClickAsync(page, "Start");
        page.WaitForAssertion(() => page.Find(".ms-k-tail-paused").TextContent.Should().Contain("Paused: the cluster stopped answering."), Wait);
        await start.Bounded();
        page.Find(".ms-k-tail-paused").TextContent.Should().Contain("Resume").And.Contain("Jump to now");
        rig.Ring.Should().BeEmpty("the tail is paused, not over: its one entry is written when it ends");

        rig.Produce("orders", 0, 10, "after", "x");
        Task resume = ClickAsync(page, "Resume");
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 3"), Wait);

        await ClickAsync(page, "Stop");
        await resume.Bounded();
        rig.Ring.Should().ContainSingle(static x => x.Action == "LiveTail").Which.Message.Should().Contain("2 segments");
    }

    [Fact]
    public async Task Jump_to_now_starts_again_from_the_end_and_says_how_many_offsets_it_skipped()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task start = ClickAsync(page, "Start");
        WaitUntilLive(page);
        await ClickAsync(page, "Pause");
        page.WaitForAssertion(() => page.FindAll(".ms-k-tail-paused").Should().HaveCount(1), Wait);
        await start.Bounded();

        for (int i = 0; i < 4; i++)
        {
            rig.Produce("orders", 0, 20 + i, "p" + i, "x");
        }

        Task jump = ClickAsync(page, "Jump to now");
        page.WaitForAssertion(() => page.Find(".ms-k-tail-line").TextContent.Should().Contain("from the end"), Wait);
        WaitUntilLive(page);
        page.Find(".ms-k-note[role=note]").TextContent.Should().Be("Skipped 4 offsets produced while paused.");
        page.Find(".ms-k-caveats").TextContent.Should().Contain("4 offsets produced while the tail was paused were skipped by jumping to now.");
        Rows(page).Should().BeEmpty("the four records produced while paused are not shown");

        rig.Produce("orders", 0, 30, "after", "x");
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 7"), Wait);

        await ClickAsync(page, "Stop");
        await jump.Bounded();
        rig.Ring.Should().ContainSingle().Which.Message.Should().Contain("jumped to now 1 time, skipping 4 offsets");
    }

    [Fact]
    public async Task A_hidden_tab_pauses_and_a_shown_tab_resumes_what_it_paused_and_a_visitors_pause_waits_for_the_visitor()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task start = ClickAsync(page, "Start");
        WaitUntilLive(page);
        DotNetObjectReference<StudioLiveUpdates> watch = TabWatch(rig);

        watch.Value.OnVisibilityChanged(true);
        page.WaitForAssertion(() => page.Find(".ms-k-tail-paused").TextContent.Should().Contain("Paused: the tab was hidden; it resumes when you come back."), Wait);
        await start.Bounded();

        rig.Produce("orders", 0, 10, "while-hidden", "x");
        watch.Value.OnVisibilityChanged(false);

        // Shown again: it goes on by itself from the cursor, and the record produced while it was hidden is there.
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 3"), Wait);
        page.WaitForAssertion(() => page.Find(".ms-k-tail-line").TextContent.Should().Contain("on from where it paused"), Wait);
        WaitUntilLive(page);

        // A visitor's pause stays a pause: hiding and showing the tab changes nothing.
        await ClickAsync(page, "Pause");
        page.WaitForAssertion(() => page.Find(".ms-k-tail-paused").TextContent.Should().Contain("Paused: you paused it."), Wait);
        watch.Value.OnVisibilityChanged(true);
        watch.Value.OnVisibilityChanged(false);
        await SettleAsync(rig, watch);
        page.Find(".ms-k-tail-paused").TextContent.Should().Contain("Paused: you paused it.").And.Contain("Resume");
        page.FindAll(".ms-k-tail-line").Should().BeEmpty("it did not read on");

        await ClickAsync(page, "Stop");
        page.WaitForAssertion(() => page.Find(".ms-k-result-banner").TextContent.Should().Contain("Stopped by you."), Wait);
        rig.Ring.Should().ContainSingle().Which.Message.Should().Contain("2 segments");
    }

    [Fact]
    public async Task The_line_that_counts_time_redraws_by_itself_and_the_page_does_not()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task start = ClickAsync(page, "Start");
        WaitUntilLive(page);
        IRenderedComponent<KafkaTailBody> body = page.FindComponent<KafkaTailBody>();
        IRenderedComponent<KafkaTailStatus> status = body.FindComponent<KafkaTailStatus>();
        int bodyRenders = body.Instance.Renders;
        int statusRenders = status.RenderCount;

        // The live line's own redraw, as the one-second tick does it. A batch may arrive meanwhile and draw the page once; the tick is what happens every time.
        for (int i = 0; i < 20; i++)
        {
            await status.Instance.RefreshAsync();
        }

        (status.RenderCount - statusRenders).Should().BeGreaterThanOrEqualTo(20, "the one-second tick redraws the live line");
        (body.Instance.Renders - bodyRenders).Should().BeLessThan(5, "and an idle tail renders nothing else: the tick does not draw the table");

        await ClickAsync(page, "Stop");
        await start.Bounded();
    }

    // ---- the session

    [Fact]
    public async Task A_new_Start_ends_the_tail_on_screen_and_each_session_is_one_entry()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task first = ClickAsync(page, "Start");
        WaitUntilLive(page);
        await ClickAsync(page, "Pause");
        page.WaitForAssertion(() => page.FindAll(".ms-k-tail-paused").Should().HaveCount(1), Wait);
        await first.Bounded();
        rig.Ring.Should().BeEmpty("the paused tail is still the session on screen");

        Task second = ClickAsync(page, "Start");
        page.WaitForAssertion(() => rig.Ring.Should().ContainSingle(static x => x.Action == "LiveTail", "the new Start ended the paused session"), Wait);
        WaitUntilLive(page);

        await ClickAsync(page, "Stop");
        await second.Bounded();
        rig.Ring.Count(static x => x.Action == "LiveTail").Should().Be(2);
        rig.CircuitLogs.Entries.Count(static x => x.EventId.Id == 9607).Should().Be(2);
        rig.CircuitLogs.Entries.Count(static x => x.EventId.Id == 9608).Should().Be(2);
    }

    [Fact]
    public async Task A_Start_that_is_refused_leaves_the_paused_tail_on_screen_and_resumable()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task first = ClickAsync(page, "Start");
        WaitUntilLive(page);
        rig.Produce("orders", 0, 10, "n1", "x");
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 3"), Wait);
        await ClickAsync(page, "Pause");
        page.WaitForAssertion(() => page.FindAll(".ms-k-tail-paused").Should().HaveCount(1), Wait);
        await first.Bounded();

        // The text no longer parses: the new Start is refused before it reads anything, and the tail on screen is as it was.
        await page.Find("textarea.ms-k-predicate").InputAsync(new ChangeEventArgs { Value = "key ^^ 'n'" });
        await ClickAsync(page, "Start");
        page.WaitForAssertion(() => page.Find(".ms-k-tail-form [role=alert]").TextContent.Should().Be(KafkaStudioText.PredicateInvalid), Wait);

        page.Find(".ms-k-tail-paused").TextContent.Should().Contain("Paused: you paused it. 1 record shown.").And.Contain("Resume");
        Rows(page).Should().Equal("0 / 3");
        rig.Ring.Count(static x => x.Action == "LiveTail" && x.Succeeded).Should().Be(0, "the tail on screen did not end");

        rig.Produce("orders", 0, 11, "n2", "x");
        Task resume = ClickAsync(page, "Resume");
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 4", "0 / 3"), Wait);
        await ClickAsync(page, "Stop");
        await resume.Bounded();
    }

    [Fact]
    public async Task Leaving_the_page_mid_tail_frees_the_place_and_writes_the_entry_once()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task start = ClickAsync(page, "Start");
        WaitUntilLive(page);
        KafkaScanLimiter limiter = rig.Engine.Provider.GetRequiredService<KafkaScanLimiter>();
        limiter.Running.Should().Be(1, "a tail holds a place while it reads");
        rig.Ring.Should().BeEmpty();

        await rig.Studio.Context.DisposeComponentsAsync();
        await start.Bounded();

        rig.Ring.Should().ContainSingle(static x => x.Action == "LiveTail").Which.Message.Should().Contain("ended by CallerGone");
        limiter.Running.Should().Be(0);
    }

    [Fact]
    public async Task A_tail_without_ReadPayloads_shows_envelopes_and_never_a_value()
    {
        await using KafkaSearchRig rig = Rig();
        rig.Topic("orders");
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task start = ClickAsync(page, "Start");
        WaitUntilLive(page);
        rig.Produce("orders", 0, 1, "k", "the value " + Sentinel);
        page.WaitForAssertion(() => Rows(page).Should().HaveCount(1), Wait);

        page.Markup.Should().NotContain(Sentinel);
        await ClickAsync(page, "Stop");
        await start.Bounded();
    }

    // ---- export

    [Fact]
    public async Task The_export_menu_is_bound_to_the_session_on_screen_and_not_to_the_newest_plan()
    {
        await using KafkaSearchRig rig = Rig(exportOn: true);
        Seed(rig);
        rig.Studio.Context.JSInterop.Setup<bool>("martenStudio.download.stream", static _ => true).SetResult(true);
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig, "&topics=orders#q=" + Uri.EscapeDataString("key ^= 'n'"));

        Task start = ClickAsync(page, "Start");
        WaitUntilLive(page);
        rig.Produce("orders", 0, 10, "n1", "x");
        rig.Produce("orders", 0, 11, "n2", "x");
        page.WaitForAssertion(() => Rows(page).Should().HaveCount(2), Wait);
        page.FindAll(".ms-k-export-menu").Should().BeEmpty("a running tail has no Export: the records change under it");
        await ClickAsync(page, "Pause");
        await start.Bounded();

        // A refused Start (a text that does not parse) replaces the latest plan and leaves the tail on screen as it was.
        await page.Find("textarea.ms-k-predicate").InputAsync(new ChangeEventArgs { Value = "key ^^ 'n'" });
        await ClickAsync(page, "Start");
        page.WaitForAssertion(() => page.FindAll(".ms-k-predicate-errors li").Should().NotBeEmpty(), Wait);
        Rows(page).Should().Equal("0 / 4", "0 / 3");

        page.WaitForAssertion(() => page.FindAll(".ms-k-export-popover .ms-menu-item").Should().HaveCount(2), Wait);
        await page.FindAll(".ms-k-export-popover .ms-menu-item")[0].ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => rig.Ring.Should().Contain(static x => x.Action == "ExportRecords"), Wait);
        StudioActionLogEntry export = rig.Ring.Single(static x => x.Action == "ExportRecords");
        export.Target.Should().Be("export of orders (live tail)");
        export.Message.Should().Contain("2 records").And.Contain("from a live tail").And.Contain("predicate: key ^= 'n'", "the session's own predicate, not the one that did not parse");
    }

    [Fact]
    public async Task Details_show_a_record_and_a_redacted_header_stays_masked()
    {
        await using KafkaSearchRig rig = Rig(engine: o => o.Topic("orders", t => t.Redact(r => r.Header("customer-email"))));
        rig.Topic("orders");
        IRenderedComponent<KafkaTailPage> page = await OpenAsync(rig);

        Task start = ClickAsync(page, "Start");
        WaitUntilLive(page);
        rig.Produce("orders", 0, 1, "k", "v", ("customer-email", Sentinel));
        page.WaitForAssertion(() => Rows(page).Should().HaveCount(1), Wait);

        await page.Find("table.ms-k-tail-matches button[aria-expanded]").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.FindAll(".ms-k-record-detail").Should().HaveCount(1), Wait);
        page.Markup.Should().Contain("redacted by header customer-email").And.NotContain(Sentinel);

        await ClickAsync(page, "Stop");
        await start.Bounded();
    }
}
