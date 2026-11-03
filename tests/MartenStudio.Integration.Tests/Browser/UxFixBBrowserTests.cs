using Microsoft.Playwright;

using MartenStudio.SampleDomain;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// 1.1-UX-FIX-B, in a browser, against the sample host: the skip link is the first Tab stop and moves focus without
/// navigating, the LINQ tab over events leaves nothing the browser gave up on and runs from the Order-by box too, and the
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
    public async Task A_refused_events_predicate_leaves_no_aborted_request_and_Ctrl_Enter_runs_from_the_Order_by_box()
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

            studio.FailedRequests.Should().BeEmpty("the studio's own URL rewrite must not cancel a request: " + string.Join(" | ", studio.FailedRequests));
            studio.AssertClean("refusing an events predicate and running one from the Order-by box");
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
