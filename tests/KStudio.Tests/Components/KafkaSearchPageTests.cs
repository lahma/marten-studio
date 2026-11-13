using System.Text.RegularExpressions;

using AngleSharp.Dom;

using Bunit;

using KStudio.Components.Pages;
using KStudio.Components.Shared;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Scanning;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

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
        await rig.Studio.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);

        // A prerendering request has no fragment: a browser never sends one.
        KafkaAddress.ReadPredicate("http://localhost/postgres/kafka/search?cluster=test&topic=orders", 4_096).Should().BeNull();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test&topic=orders#q=" + Uri.EscapeDataString("key = 'k1'"), intercepted: false);

        // The body is rendered with the frame's Ready still pending: it shows nothing, reads nothing and has not looked at the address.
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IRenderedComponent<KafkaSearchBody> body = rig.Studio.Context.Render<KafkaSearchBody>(p => p.AddCascadingValue(KafkaPageFrame.ReadyName, (Task) ready.Task));

        body.FindAll("textarea").Should().BeEmpty("nothing is drawn before Ready");
        rig.BrokerCalls.Should().Be(0, "nothing is read before Ready");

        ready.SetResult();

        body.WaitForAssertion(() => body.Find("textarea.ms-k-predicate").GetAttribute("value").Should().Be("key = 'k1'"));
        rig.BrokerCalls.Should().BeGreaterThan(0, "the topic list is read after Ready");
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
        rig.ListOffsetCalls.Should().Be(0, "a pasted link makes no plan: planning resolves the window with list-offsets calls");
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
        page.Find(".ms-k-predicate-errors").TextContent.Should().MatchRegex(@"^Position \d+: ").And.NotMatchRegex("(?i)position.*position");
        rig.Ring.Should().ContainSingle().Which.Message.Should().Be("position 1, redacted field operator", "the page says 'see above'; the audit says the cause");
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

        // What is already in the box is wrapped, so that its "or" does not swallow the chip.
        await TypeAsync(page, "key = 'k0' or key = 'k1'");
        await page.FindAll("button.ms-k-chip").First(static x => x.TextContent.Contains("c-1", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());

        page.Find("textarea.ms-k-predicate").GetAttribute("value").Should().Be("(key = 'k0' or key = 'k1') and header.correlation-id = 'c-1'");
    }

    [Fact]
    public async Task Typing_does_not_render_the_whole_page()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);
        IRenderedComponent<KafkaSearchBody> body = page.FindComponent<KafkaSearchBody>();
        int renders = body.RenderCount;

        await TypeAsync(page, "key = 'k");
        await TypeAsync(page, "key = 'k1'");

        body.RenderCount.Should().Be(renders, "the browser shows what was typed; the page is not rendered again for each keystroke");

        await SearchAsync(page);
        page.WaitForAssertion(() => Rows(page).Should().ContainSingle());
    }

    [Fact]
    public async Task The_topic_list_offers_only_the_topics_the_visitor_may_search()
    {
        await using KafkaSearchRig rig = new();
        rig.Studio.Authorization.AllowKafka(static resource => !(resource.Topic == "readonly.v1" && resource.Capability == "SearchRecords"));
        Seed(rig, "orders");
        Seed(rig, "readonly.v1");

        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig);

        page.WaitForAssertion(() => page.FindAll("label.ms-k-topic-option").Select(static x => x.TextContent.Trim()).Should().Equal("orders"));
    }

    [Fact]
    public async Task The_pressed_buttons_say_so_and_the_uncommitted_help_is_the_engines_sentence()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);
        await SearchAsync(page);
        page.WaitForAssertion(() => Rows(page).Should().HaveCount(3));

        page.FindAll(".ms-k-sort button").Select(static x => x.GetAttribute("aria-pressed")).Should().Equal("true", "false", "false");
        await page.FindAll(".ms-k-sort button")[1].ClickAsync(new MouseEventArgs());
        page.FindAll(".ms-k-sort button").Select(static x => x.GetAttribute("aria-pressed")).Should().Equal("false", "true", "false");
        page.Markup.Should().NotContain("ms-btn-active");
        page.Markup.Should().Contain(new KStudio.Core.Windows.WindowNote.IncludesUncommitted().Text);
    }

    [Fact]
    public async Task Leaving_the_page_mid_scan_records_it_once_and_frees_the_owners_slot()
    {
        ScriptedReader reader = new();
        int opened = 0;
        await using KafkaSearchRig rig = new(wrap: wire => new ScriptedWire(wire, options => Interlocked.Increment(ref opened) == 1 ? wire.OpenReader(options) : reader));
        Seed(rig);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);
        page.WaitForAssertion(() => page.FindAll("label.ms-k-topic-option input").Should().HaveCount(1));
        KafkaScanLimiter limiter = rig.Engine.Provider.GetRequiredService<KafkaScanLimiter>();

        // The scripted reader never answers: the scan runs until it is stopped.
        Task click = page.Find(".ms-k-search-actions button").ClickAsync(new MouseEventArgs());
        ScanFixture.Until(() => reader.Reads > 0, "the scan is reading");
        limiter.Running.Should().Be(1);
        rig.Ring.Should().BeEmpty("a scan is recorded when it ends");

        await rig.Studio.Context.DisposeComponentsAsync();
        await click.Bounded();

        rig.Ring.Count(static x => x.Action == "SearchRecords").Should().Be(1, "the disposal records the scan, once");
        limiter.Running.Should().Be(0);
        KafkaScanOwner owner = await rig.Studio.Context.Services.GetRequiredService<KafkaOwnerKey>().OwnerAsync();
        limiter.TryAcquire(owner.Key, out IDisposable? lease).Should().BeNull("the owner's slot is free");
        lease?.Dispose();
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

        page.WaitForAssertion(() => page.Find(".ms-k-result-banner").TextContent.Should().Contain("No match in the 1 record read from 1 partition."));
        page.Find(".ms-k-caveats").TextContent.Should().Contain("What this result does not cover").And.Contain("Compacted topic: a time-window search can miss superseded keys and sees tombstones");
        Regex.Count(page.Markup, "Compacted topic: a time-window search can miss superseded keys and sees tombstones", RegexOptions.None, TimeSpan.FromSeconds(5)).Should().Be(1, "a caveat is drawn once, not in the plan's notes and again in the result's");
        page.Markup.Should().NotContain("not found");
    }

    [Fact]
    public async Task Continue_appears_only_with_a_cursor_and_replaces_the_rows()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Limits.MaxMatches = 2);
        Seed(rig, "orders", 5);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);

        await SearchAsync(page);
        page.WaitForAssertion(() => page.Find(".ms-k-result-banner").TextContent.Should().Contain("Stopped at 2 matches, the most a search keeps here. Continue reads on from where it stopped."));
        Rows(page).Should().Equal("0 / 0", "0 / 1");
        page.FindAll(".ms-k-continued-showing").Should().BeEmpty("a first run has nothing earlier to say");

        await page.Find(".ms-k-result-banner button").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 2", "0 / 3"));
        page.Markup.Should().Contain("Continued after offsets read by the previous search (");

        // #76: the table is per run and says so, instead of looking like data loss.
        page.Find(".ms-k-continued-showing").TextContent.Should().Be("Showing matches 3-4 of this search; the earlier 2 were cleared from the page. Export before Continue to keep them.");

        await page.Find(".ms-k-result-banner button").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 4"));
        page.Find(".ms-k-continued-showing").TextContent.Should().Be("Showing match 5 of this search; the earlier 4 were cleared from the page. Export before Continue to keep them.", "the earlier runs add up");
        page.FindAll(".ms-k-result-banner button").Should().BeEmpty("a completed search has no cursor to continue from");
        rig.Ring.Count(static x => x.Action == "SearchRecords" && x.Succeeded).Should().Be(3, "each scan is a new audit entry");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continue_resumes_from_the_cursor_with_or_without_a_confirmation(bool confirmation)
    {
        // UI-VERIFY found Continue drawing the first run's rows again when the estimate needs a confirmation.
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Limits.MaxMatches = 2;
            o.Limits.ConfirmAboveRecords = confirmation ? 1 : 1_000_000;
        });
        Seed(rig, "orders", 7);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);

        async Task RunAsync(IElement button)
        {
            await button.ClickAsync(new MouseEventArgs());

            if (confirmation)
            {
                page.WaitForAssertion(() => page.FindAll("dialog").Should().HaveCount(1));

                // While the confirmation waits, nothing else may plan or continue and replace the plan it is about to start.
                page.Find(".ms-k-search-actions button").HasAttribute("disabled").Should().BeTrue();
                page.FindAll(".ms-k-result-banner button").Should().OnlyContain(static x => x.HasAttribute("disabled"));
                await page.Find("dialog .ms-confirm-actions button.ms-button-danger").ClickAsync(new MouseEventArgs());
            }
        }

        await RunAsync(page.Find(".ms-k-search-actions button"));
        page.WaitForAssertion(() => page.Find(".ms-k-result-banner").TextContent.Should().Contain("Stopped at 2 matches"));
        string[] first = Rows(page);
        first.Should().Equal("0 / 0", "0 / 1");

        await RunAsync(page.Find(".ms-k-result-banner button"));
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 2", "0 / 3"));
        Rows(page).Intersect(first).Should().BeEmpty("a continued scan never shows a record of the first one again");

        await RunAsync(page.Find(".ms-k-result-banner button"));
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 4", "0 / 5"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continuing_over_several_partitions_reads_every_record_exactly_once(bool confirmation)
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Limits.MaxMatches = 4;
            o.Limits.ConfirmAboveRecords = confirmation ? 1 : 1_000_000;
        });
        rig.Topic("orders", 3);

        for (int partition = 0; partition < 3; partition++)
        {
            for (int i = 0; i < 9; i++)
            {
                rig.Produce("orders", partition, i, "k" + i, "v", ("correlation-id", "c-" + i));
            }
        }

        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);
        await TypeAsync(page, "key ^= 'k'");
        List<string> seen = [];

        async Task ClickAsync(IElement button)
        {
            await button.ClickAsync(new MouseEventArgs());

            if (confirmation)
            {
                page.WaitForAssertion(() => page.FindAll("dialog").Should().HaveCount(1));
                await page.Find("dialog .ms-confirm-actions button.ms-button-danger").ClickAsync(new MouseEventArgs());
            }
        }

        await ClickAsync(page.Find(".ms-k-search-actions button"));

        for (int guard = 0; guard < 12; guard++)
        {
            page.WaitForAssertion(() => page.FindAll(".ms-k-result-banner").Should().HaveCount(1));
            string[] rows = Rows(page);
            rows.Intersect(seen).Should().BeEmpty("a continued scan never shows a record of an earlier one again");
            seen.AddRange(rows);

            if (page.FindAll(".ms-k-result-banner button").Count == 0)
            {
                break;
            }

            await ClickAsync(page.Find(".ms-k-result-banner button"));
        }

        seen.Should().HaveCount(27).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_continue_that_waits_for_its_confirmation_has_not_continued_anything_yet()
    {
        // UI-VERIFY pressed Continue, saw "Continued after offsets read by the previous search" over the same rows and took the page for hung: the line was
        // drawn when the continued scan was planned, while its confirmation dialog - which the scan waits for - was open and the rows were still the first scan's.
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Limits.MaxMatches = 2;
            o.Limits.ConfirmAboveRecords = 1;
        });
        rig.Topic("orders", 3);

        for (int partition = 0; partition < 3; partition++)
        {
            for (int i = 0; i < 6; i++)
            {
                rig.Produce("orders", partition, i, "k" + i, "v");
            }
        }

        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);
        await TypeAsync(page, "key ^= 'k'");
        await SearchAsync(page);
        page.WaitForAssertion(() => page.FindAll("dialog").Should().HaveCount(1));
        await page.Find("dialog .ms-confirm-actions button.ms-button-danger").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.Find(".ms-k-result-banner").TextContent.Should().Contain("Stopped at 2 matches"));
        string[] first = Rows(page);

        // Partition 0 was started and stopped inside; the others were never reached. Both are the engine's NotReached, and the page tells them apart.
        string[] states = [.. page.FindAll("details.ms-k-progress tbody tr").Select(static x => x.QuerySelectorAll("td")[7].TextContent.Trim())];
        states.Should().HaveCount(3).And.OnlyContain(static x => x == "stopped before its end" || x == "not reached");
        states[0].Should().Be("stopped before its end", "partition 0 was read and the scan stopped inside it");
        page.Find(".ms-k-caveats").TextContent.Should().Contain("orders/0: stopped before its end");

        await page.Find(".ms-k-result-banner button").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll("dialog").Should().HaveCount(1));

        page.Markup.Should().NotContain("Continued after", "nothing has continued while the confirmation waits");
        Rows(page).Should().Equal(first, "the rows are still the first scan's until the continued scan starts");

        await page.Find("dialog .ms-confirm-actions button.ms-button-danger").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => Rows(page).Should().NotIntersectWith(first));
        page.Markup.Should().Contain("Continued after offsets read by the previous search (2 records).");
    }

    [Fact]
    public async Task A_cancelled_continue_leaves_no_continued_line()
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Limits.MaxMatches = 2;
            o.Limits.ConfirmAboveRecords = 1;
        });
        Seed(rig, "orders", 5);
        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);
        await SearchAsync(page);
        page.WaitForAssertion(() => page.FindAll("dialog").Should().HaveCount(1));
        await page.Find("dialog .ms-confirm-actions button.ms-button-danger").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.Find(".ms-k-result-banner").TextContent.Should().Contain("Stopped at 2 matches"));

        await page.Find(".ms-k-result-banner button").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll("dialog").Should().HaveCount(1));
        await page.Find("dialog .ms-confirm-actions button:not(.ms-button-danger)").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll("dialog").Should().BeEmpty());

        page.Markup.Should().NotContain("Continued after");
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
    public async Task Capability_off_shows_one_neutral_sentence_and_no_option_name()
    {
        await using KafkaSearchRig rig = new(searchOn: false);
        Seed(rig);
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test", intercepted: false);

        IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();

        page.WaitForAssertion(() => page.Markup.Should().Contain("Searching records is not available here.").And.NotContain("KafkaStudioOptions"));
        page.FindAll("textarea").Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOnly_shows_the_same_neutral_sentence()
    {
        await using KafkaSearchRig rig = new();
        rig.Studio.Context.Options.ReadOnly = true;
        await rig.Studio.Context.ReadyAsync();
        rig.Studio.Navigation.Simulate("kafka/search?cluster=test", intercepted: false);

        IRenderedComponent<KafkaSearchPage> page = rig.Studio.Context.Render<KafkaSearchPage>();

        page.WaitForAssertion(() => page.Find(".ms-capability-disabled").TextContent.Should().Contain("Searching records is not available here.").And.NotContain("ReadOnly"));
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

        page.Find(".ms-k-topic-count").TextContent.Should().Be("20 of 20 topics selected (the most one search reads)", "#77: not a bare '1 of 20'");
        page.FindAll("label.ms-k-topic-option input[disabled]").Should().HaveCount(2);
    }

    [Fact]
    public async Task The_predicate_box_takes_the_focus_once_when_the_form_is_first_drawn_and_never_again()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Limits.MaxMatches = 2);
        Seed(rig, "orders", 5);

        IRenderedComponent<KafkaSearchPage> page = await OpenAsync(rig, Window);

        static bool Focus(Bunit.JSRuntimeInvocation x) => x.Identifier == "Blazor._internal.domWrapper.focus";
        page.WaitForAssertion(() => rig.Studio.Context.JSInterop.Invocations.Count(Focus).Should().Be(1, "the box is focused on the first render of the form"));

        await TypeAsync(page, "key ^= 'k'");
        await SearchAsync(page);
        page.WaitForAssertion(() => page.Find(".ms-k-result-banner").TextContent.Should().Contain("Stopped at 2 matches"));
        await page.Find(".ms-k-result-banner button").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => Rows(page).Should().Equal("0 / 2", "0 / 3"));

        rig.Studio.Context.JSInterop.Invocations.Count(Focus).Should().Be(1, "later renders, a search and a Continue leave the focus where the visitor put it");
    }

    [Fact]
    public void Each_stop_reason_shows_its_sentence()
    {
        Func<KafkaScanStopReason, string> expected = static reason => reason switch
        {
            KafkaScanStopReason.Completed => "Read every offset in the window: 1,200 records, 2 KiB, 3 matches.",
            KafkaScanStopReason.MatchLimit => "Stopped at 500 matches, the most a search keeps here. Continue reads on from where it stopped.",
            KafkaScanStopReason.ByteBudget => "Stopped after reading 2 KiB, this search's budget. Continue reads on from where it stopped.",
            KafkaScanStopReason.RecordBudget => "Stopped after 1,200 records, this search's budget. Continue reads on from where it stopped.",
            KafkaScanStopReason.OwnerBudget => "Stopped: you have read your hourly allowance. Continue reads on from where it stopped, once the allowance frees up.",
            KafkaScanStopReason.Deadline => "Stopped after 2 min, the longest a search runs. Continue reads on from where it stopped.",
            KafkaScanStopReason.Cancelled => "Stopped by you. Continue reads on from where it stopped.",
            KafkaScanStopReason.Shutdown => "Stopped: the application is shutting down.",
            KafkaScanStopReason.NotAuthorized => "Nothing was read: every partition refused READ.",
            KafkaScanStopReason.Unavailable => "Stopped: the cluster stopped answering.",
            KafkaScanStopReason.Failed => "The search failed. The application's log has the reason.",
            _ => throw new InvalidOperationException(reason.ToString()),
        };

        foreach (KafkaScanStopReason reason in Enum.GetValues<KafkaScanStopReason>().Where(static x => x is not (KafkaScanStopReason.CallerGone or KafkaScanStopReason.Paused)))
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
