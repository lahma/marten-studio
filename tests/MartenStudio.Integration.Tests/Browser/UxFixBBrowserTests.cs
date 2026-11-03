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

    /// <summary>
    /// The only request failure these Query tests allow: a <c>GET</c> of the studio's own page, under <c>/marten/query?</c>, that the
    /// browser cancelled (<c>net::ERR_ABORTED</c>). The studio's router is its own, so every address it writes is an
    /// enhanced-navigation fetch of the whole page, and on a slow machine one can be cancelled by the next, which shows only in the
    /// network panel: the page still ends at the right address with the right result, which these tests assert in full. Any other
    /// failed request - a script, a stylesheet, <c>_blazor</c>, another path, a non-GET, another error - still fails the test.
    /// Restore the strict "no failed request at all" assertion with the 1.2 navigation work, which removes these page fetches.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex OwnPageFetch =
        new(@"^GET https?://[^/\s]+/marten/query\?\S* net::ERR_ABORTED$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static void AssertOnlyTheOwnPageFetchWasCancelled(StudioPage studio, string because)
    {
        string[] other = [.. studio.FailedRequests.Where(static failed => !OwnPageFetch.IsMatch(failed))];

        other.Should().BeEmpty("only a cancelled GET of the studio's own /marten/query page may fail (" + because + "): " + string.Join(" | ", other));
    }

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

            // The case that aborted a request: edit the predicate and click Run at once. The box's `change` rewrites the address as
            // the click lands, and Run used to rewrite it again - a second enhanced navigation that aborted the first.
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

            AssertOnlyTheOwnPageFetchWasCancelled(studio, "refusing an events predicate and running one from the Order-by box");
            studio.AssertClean("refusing an events predicate and running one from the Order-by box");
        });
    }

    /// <summary>
    /// 1.1-FINAL-POLISH C1: typing a predicate, moving to the Order-by box and pressing Ctrl+Enter there pushed the address twice -
    /// once on the predicate's `change` (without the ordering), once for the run - and the first enhanced navigation was aborted.
    /// The address is now written once per Run.
    /// </summary>
    [BrowserFact]
    public async Task A_predicate_then_Ctrl_Enter_from_the_Order_by_box_writes_both_into_the_address()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "linq-single-push", fixture.Root, "admin", async studio =>
        {
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

            AssertOnlyTheOwnPageFetchWasCancelled(studio, "editing a predicate and running from the Order-by box");
            studio.AssertClean("editing a predicate and running from the Order-by box");
        });
    }

    /// <summary>
    /// 1.1-FINAL-POLISH C1, by construction rather than by timing: the first Run's navigation request is held in the browser, the
    /// second Run (Ctrl+Enter from the Order-by box) is asked for while it is in flight, and only then is the first let through.
    /// The second Run shows its result, and the address ends carrying the predicate and the ordering. The held request may be
    /// cancelled by the second navigation (<see cref="OwnPageFetch" />); nothing else may fail.
    /// </summary>
    [BrowserFact]
    public async Task A_second_Run_while_the_first_ones_navigation_is_in_flight_shows_the_second_result()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "linq-serialised-push", fixture.Root, "admin", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=linq&target=events"));
            await studio.Page.Locator("#ms-query-linq-where").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> held = new(TaskCreationOptions.RunContinuationsAsynchronously);

            await studio.Page.RouteAsync(
                static url => url.Contains("predicate=Version", StringComparison.Ordinal) && !url.Contains("orderby=", StringComparison.Ordinal),
                async route =>
                {
                    held.TrySetResult(true);
                    await release.Task;
                    await route.ContinueAsync();
                });

            try
            {
                await studio.Page.Locator("#ms-query-linq-where").FillAsync("Version > 0");
                await studio.Page.Locator(".ms-query-run").ClickAsync();
                await held.Task.WaitAsync(TimeSpan.FromSeconds(30));

                // The first navigation is in flight, and the first run has finished (a run in progress ignores the chord).
                await studio.Page.Locator(".ms-query-linq-events .ms-event-card").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
                await studio.Page.Locator(".ms-query-run").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
                await studio.Page.WaitForFunctionAsync(
                    "() => document.querySelector('.ms-query-run') !== null && !document.querySelector('.ms-query-run').disabled",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 30_000 });

                // The second Run is asked for now, while the first one's request is still held.
                await studio.Page.Locator("#ms-query-linq-orderby").FillAsync("Sequence");
                await studio.Page.Locator("#ms-query-linq-orderby").PressAsync("Control+Enter");
                await studio.Page.WaitForFunctionAsync(
                    "() => { const s = document.querySelector('.ms-query-linq-events .ms-event-card-seq'); return s !== null && s.textContent.trim() === '#1'; }",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 30_000 });

                release.TrySetResult(true);

                await studio.Page.WaitForFunctionAsync(
                    "() => window.location.search.includes('predicate=Version') && window.location.search.includes('orderby=Sequence')",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 30_000 });

                AssertOnlyTheOwnPageFetchWasCancelled(studio, "two Runs while the first one's navigation is held");
                studio.AssertClean("two Runs while the first one's navigation is in flight");
            }
            finally
            {
                // However the test ends, the held request is let go: a hold nobody releases would hang the page's disposal.
                release.TrySetResult(true);
            }
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
