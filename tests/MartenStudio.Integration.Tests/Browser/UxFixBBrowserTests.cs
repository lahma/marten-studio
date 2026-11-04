using Microsoft.Playwright;

using MartenStudio.SampleDomain;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// 1.1-UX-FIX-B, in a browser, against the sample host: the skip link is the first Tab stop and moves focus without
/// navigating, the LINQ tab over events runs from the Order-by box too and writes its address once per Run, and the
/// sample's typed streams show Compact - including the one that folds to nothing.
/// </summary>
[Collection(BrowserSuite.Name)]
public class UxFixBBrowserTests(BrowserSuiteFixture fixture)
{
    /// <summary>
    /// Focus is put back at the start of the document the way a fresh page load has it: <c>FocusOnNavigate</c> has put it on the
    /// page heading, and Chromium's sequential focus starting point would otherwise be there.
    /// </summary>
    private const string ResetFocus =
        "() => { document.activeElement && document.activeElement.blur(); document.body.setAttribute('tabindex', '-1'); "
        + "document.body.focus(); document.body.removeAttribute('tabindex'); }";

    [BrowserFact]
    public async Task The_skip_link_is_the_first_Tab_stop_shows_on_focus_and_moves_focus_to_the_content_without_navigating()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "skip-link", fixture.Root, "admin", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl());
            string before = studio.Page.Url;

            ILocator link = studio.Page.Locator("a.ms-skip-link");
            (await link.BoundingBoxAsync())!.Y.Should().BeLessThan(0, "it is off screen until it has focus");

            await studio.Page.EvaluateAsync(ResetFocus);
            await studio.Page.Keyboard.PressAsync("Tab");

            (await studio.Page.EvaluateAsync<string>("() => document.activeElement.className")).Should().Contain("ms-skip-link");
            (await studio.Page.EvaluateAsync<string>("() => document.activeElement.textContent")).Trim().Should().Be("Skip to content");
            (await link.BoundingBoxAsync())!.Y.Should().BeGreaterThanOrEqualTo(0, "it is drawn once it has focus");

            await studio.Page.Keyboard.PressAsync("Enter");

            (await studio.Page.EvaluateAsync<string>("() => document.activeElement.id")).Should().Be("ms-main-content");
            studio.Page.Url.Should().Be(before, "the link moves focus; the <base href> does not send it to the front page");

            // And the next Tab lands inside the content, not back in the navigation.
            await studio.Page.Keyboard.PressAsync("Tab");
            (await studio.Page.EvaluateAsync<bool>("() => document.querySelector('main').contains(document.activeElement)")).Should().BeTrue();

            studio.AssertClean("tabbing to the skip link and following it");
        });
    }

    [BrowserFact]
    public async Task A_refused_events_predicate_then_Ctrl_Enter_from_the_Order_by_box_runs_the_query()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "linq-events-refusal", fixture.Root, "admin", async studio =>
        {
            const string Refused = "IsArchived == false";

            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=linq&target=events&predicate=" + Uri.EscapeDataString(Refused)));

            ILocator withheld = studio.Page.Locator(".ms-query-linq-withheld");
            (await withheld.GetAttributeAsync("open")).Should().NotBeNull("the list of what is not offered is open without a click");
            (await withheld.InnerTextAsync()).Should().Contain("IsArchived").And.Contain("Archived selector");

            await studio.Page.Locator(".ms-query-run").ClickAsync();

            ILocator error = studio.Page.Locator(".ms-query-error-message").First;
            await error.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await error.InnerTextAsync()).Should().Contain("Archived selector", "the refusal carries the member's reason");

            // The case that aborted a request in 1.1: edit the predicate and click Run at once. The box's `change` rewrites the address as
            // the click lands, and Run used to rewrite it again - a second enhanced navigation that aborted the first. Since 1.2 an address
            // the page writes is a history update and nothing is fetched, which AssertNoPageFetch proves below.
            await studio.Page.Locator("#ms-query-linq-where").FillAsync("Version > 0");
            await studio.Page.Locator(".ms-query-run").ClickAsync();

            await studio.Page.Locator(".ms-query-linq-events .ms-event-card").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await studio.Page.WaitForFunctionAsync("() => window.location.search.includes('predicate=Version')", null, new PageWaitForFunctionOptions { Timeout = 30_000 });

            // Ctrl+Enter from the Order-by box runs the query, with the text the box holds.
            await studio.Page.Locator("#ms-query-linq-orderby").FillAsync("Sequence");
            await studio.Page.Locator("#ms-query-linq-orderby").PressAsync("Control+Enter");

            await studio.Page.WaitForFunctionAsync("() => window.location.search.includes('orderby=Sequence')", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
            await studio.Page.WaitForFunctionAsync(
                "() => { const s = document.querySelector('.ms-query-linq-events .ms-event-card-seq'); return s !== null && s.textContent.trim() === '#1'; }",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("refusing an events predicate and running one from the Order-by box");
            studio.AssertClean("refusing an events predicate and running one from the Order-by box");
        });
    }

    /// <summary>
    /// 1.1-FINAL-POLISH C1: typing a predicate, moving to the Order-by box and pressing Ctrl+Enter there pushed the address twice -
    /// once on the predicate's `change` (without the ordering), once for the run. The address is now written once per Run, which
    /// this proves in the browser: the page's history writes are recorded, exactly one address carries the predicate and it carries
    /// the ordering too, and (1.2) no page was fetched and no request failed.
    /// </summary>
    [BrowserFact]
    public async Task A_predicate_then_Ctrl_Enter_from_the_Order_by_box_writes_both_into_the_address()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "linq-single-push", fixture.Root, "admin", async studio =>
        {
            await studio.RecordHistoryWritesAsync();
            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=linq&target=events"));
            await studio.Page.Locator("#ms-query-linq-where").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await studio.Page.Locator("#ms-query-linq-where").FillAsync("Version > 0");
            await studio.Page.Locator("#ms-query-linq-orderby").FillAsync("Sequence");

            // The predicate lost focus on the way to the Order-by box: nothing was written to the address yet.
            studio.Page.Url.Should().NotContain("predicate=", "an edited box keeps its draft; the address follows the Run");

            await studio.Page.Locator("#ms-query-linq-orderby").PressAsync("Control+Enter");

            await studio.Page.WaitForFunctionAsync(
                "() => window.location.search.includes('predicate=Version') && window.location.search.includes('orderby=Sequence')",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });
            await studio.Page.Locator(".ms-query-linq-events .ms-event-card").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            IReadOnlyList<string> writes = [.. (await studio.HistoryWritesAsync()).Where(static x => x.Contains("predicate=", StringComparison.Ordinal))];

            writes.Distinct().Should().ContainSingle("the predicate reached the address in one address, not one before the run and one for it: " + string.Join(" | ", writes));
            writes[0].Should().Contain("orderby=Sequence", "that one address carries the ordering too");

            if (!StudioPage.InterceptionIsOff)
            {
                writes.Should().ContainSingle("the address is written once per Run: " + string.Join(" | ", writes));
            }

            studio.AssertNoPageFetch("editing a predicate and running from the Order-by box");
            studio.AssertClean("editing a predicate and running from the Order-by box");
        });
    }

    /// <summary>
    /// 1.1-FINAL-POLISH C1, by construction rather than by timing, and (1.2) as a count: two Runs in a row - the second asked for
    /// by Ctrl+Enter from the Order-by box without waiting for the first one's address - fetch no page at all. In 1.1 each Run's
    /// address was a fetch of the whole page, which the next could cancel, so this test held the first request in the browser; the
    /// studio now renders its own moves in the circuit, and a route on the page's address counts how many times the browser asked
    /// the server for it. The second Run shows its result and the address ends carrying the predicate and the ordering.
    /// </summary>
    [BrowserFact]
    public async Task Two_Runs_in_a_row_fetch_no_page_and_end_on_the_second()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "linq-serialised-push", fixture.Root, "admin", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=linq&target=events"));
            await studio.Page.Locator("#ms-query-linq-where").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            // Counts every request for the Query page's own address and lets it through: after the load above, any is a page fetch.
            int pageRequests = 0;
            await studio.Page.RouteAsync(
                static url => url.Contains("/marten/query", StringComparison.Ordinal),
                async route =>
                {
                    Interlocked.Increment(ref pageRequests);
                    await route.ContinueAsync();
                });

            await studio.Page.Locator("#ms-query-linq-where").FillAsync("Version > 0");
            await studio.Page.Locator(".ms-query-run").ClickAsync();

            // The second Run is asked for at once, without waiting for the first one's address to be reported back: a run in
            // progress ignores the chord, so the first Run has finished (its result is on screen and Run is enabled again).
            await studio.Page.Locator(".ms-query-linq-events .ms-event-card").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await studio.Page.WaitForFunctionAsync(
                "() => document.querySelector('.ms-query-run') !== null && !document.querySelector('.ms-query-run').disabled",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            await studio.Page.Locator("#ms-query-linq-orderby").FillAsync("Sequence");
            await studio.Page.Locator("#ms-query-linq-orderby").PressAsync("Control+Enter");
            await studio.Page.WaitForFunctionAsync(
                "() => { const s = document.querySelector('.ms-query-linq-events .ms-event-card-seq'); return s !== null && s.textContent.trim() === '#1'; }",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });
            await studio.Page.WaitForFunctionAsync(
                "() => window.location.search.includes('predicate=Version') && window.location.search.includes('orderby=Sequence')",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            if (!StudioPage.InterceptionIsOff)
            {
                Volatile.Read(ref pageRequests).Should().Be(0, "an address the page writes is a history update, not a request for the page");
            }

            studio.AssertNoPageFetch("two Runs in a row");
            studio.AssertClean("two Runs in a row");
        });
    }

    /// <summary>1.1-FINAL-POLISH C2: the list of members the events target withholds does not push the content wider than a phone.</summary>
    [BrowserFact]
    public async Task The_events_Not_offered_list_does_not_scroll_sideways_at_390_px()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "linq-withheld-390", fixture.Root, "admin", async studio =>
        {
            await studio.Page.SetViewportSizeAsync(390, 844);
            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=linq&target=events"));
            await studio.Page.Locator(".ms-query-linq-withheld").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            (await studio.Page.Locator(".ms-query-linq-withheld").GetAttributeAsync("open")).Should().NotBeNull();
            await studio.AssertNoSidewaysScrollAsync("the LINQ tab over events at 390 px");
            studio.AssertClean("the LINQ events list at 390 px");
        });
    }

    [BrowserFact]
    public async Task The_samples_typed_streams_offer_Compact_and_the_voided_one_says_it_folds_to_nothing()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "typed-compaction", fixture.Root, "admin", async studio =>
        {
            const string ConfirmButton = ".ms-confirm-actions button.ms-button-danger";

            // An untyped order: no button, and the sentence where it would be.
            await studio.GoAsync(fixture.Root.StudioUrl("events/streams/s?id=" + SampleDataSeeder.OrderStreamId(1)));
            await studio.Page.Locator(".ms-stream-compact-unavailable").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await studio.Page.Locator(".ms-stream-compact-unavailable").InnerTextAsync())
                .Should().Be("Compact is offered for streams of a known aggregate type; this stream has none.");
            (await studio.Page.Locator(".ms-stream-compact-button").CountAsync()).Should().Be(0);

            // A typed order that folds: the dialog's Compact stays blocked only until the id is typed.
            Guid folds = SampleDataSeeder.TypedOrderStreamId(1);
            await studio.GoAsync(fixture.Root.StudioUrl("events/streams/s?id=" + folds));
            await studio.Page.Locator(".ms-stream-compact-button").ClickAsync();
            await studio.Page.Locator(".ms-compact-facts").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await studio.Page.Locator(".ms-compact-fold-nothing").CountAsync()).Should().Be(0);

            // The voided one: its aggregate deletes itself, so the dialog says it folds to nothing before any click.
            Guid voided = SampleDataSeeder.TypedOrderStreamId(SampleDataSeeder.TypedOrderStreamCount);
            await studio.GoAsync(fixture.Root.StudioUrl("events/streams/s?id=" + voided));
            await studio.Page.Locator(".ms-stream-compact-button").ClickAsync();

            ILocator nothing = studio.Page.Locator(".ms-compact-fold-nothing");
            await nothing.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await nothing.InnerTextAsync()).Should().Contain("OrderSummary").And.Contain("nothing to compact");

            await studio.Page.Locator("#ms-confirm-input").FillAsync(voided.ToString());
            (await studio.Page.Locator(ConfirmButton).IsDisabledAsync()).Should().BeTrue("a stream that folds to nothing has nothing to compact");

            studio.AssertClean("opening the Compact dialog on the sample's typed streams");
        });
    }
}
