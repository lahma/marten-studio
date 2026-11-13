using System.Text.Json;
using System.Web;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-SCOPE-3 in a real browser: the header's Find box. <c>/</c> and Ctrl+K go to it (and do not while a character is being typed or a
/// dialog is open), Enter on one match opens it, several matches are a list the arrow keys walk and Escape leaves, and the box holds
/// its place in the header at 1440 px and 390 px - where it is a magnifier and a popover - without the page scrolling sideways.
/// </summary>
[Collection(BrowserSuite.Name)]
public class FindBrowserTests(BrowserSuiteFixture fixture)
{
    private static readonly PageWaitForFunctionOptions Wait = new() { Timeout = 30_000 };

    private Task RunAsync(string name, Func<StudioPage, Task> body) =>
        BrowserScenario.RunAsync(fixture.Browser, name, fixture.Root, "admin", body);

    /// <summary>The id of the first row of a collection, read off its list as a visitor would.</summary>
    private async Task<string> FirstIdOfAsync(StudioPage studio, string collection)
    {
        await studio.GoAsync(fixture.Root.StudioUrl("documents/" + collection));

        ILocator ids = studio.Page.Locator(".ms-doc-table tbody tr.ms-doc-row a.ms-doc-id");
        await ids.First.WaitForAsync();

        return (await ids.First.GetAttributeAsync("title"))!;
    }

    private static Task<bool> FocusIsInTheBoxAsync(IPage page) =>
        page.EvaluateAsync<bool>("() => document.activeElement && document.activeElement.id === 'ms-find-input'");

    /// <summary>The box is enhanced once the circuit has rendered it and script has been given its field.</summary>
    private static async Task WaitForTheBoxAsync(StudioPage studio)
    {
        await studio.WaitForInteractiveAsync();
        await studio.Page.WaitForFunctionAsync("() => { const f = document.getElementById('ms-find-input'); return !!f && !!f.msFindHandlers; }", null, Wait);
    }

    [BrowserFact]
    public Task Slash_and_Ctrl_K_focus_the_box_and_one_match_opens_when_Enter_is_pressed() =>
        RunAsync("marten-find-keys-and-open", async studio =>
        {
            IPage page = studio.Page;
            string id = await FirstIdOfAsync(studio, "customer");

            await studio.GoAsync(fixture.Root.StudioUrl());
            await WaitForTheBoxAsync(studio);

            await page.Locator("body").PressAsync("/");
            (await FocusIsInTheBoxAsync(page)).Should().BeTrue("/ goes to the box");

            await page.Locator("a.ms-nav-link").First.FocusAsync();
            await page.Keyboard.PressAsync("Control+k");
            (await FocusIsInTheBoxAsync(page)).Should().BeTrue("Ctrl+K goes to the box");

            await page.Locator("#ms-find-input").FillAsync(id);
            await page.Keyboard.PressAsync("Enter");

            await page.WaitForURLAsync(static url => url.Contains("documents/customer/doc", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });
            HttpUtility.ParseQueryString(new Uri(page.Url).Query)["id"].Should().Be(id);
            (await page.Locator("#ms-find-input").InputValueAsync()).Should().Be(id, "the value stays in the box");

            studio.AssertNoPageFetch("opening a find result");
            studio.AssertClean("finding a document by its id");
        });

    [BrowserFact]
    public Task A_slash_that_is_a_character_being_typed_or_that_meets_a_dialog_does_not_move_the_focus() =>
        RunAsync("marten-find-keys-ignored", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl());
            await WaitForTheBoxAsync(studio);

            // In the box itself it is the character.
            await page.Locator("#ms-find-input").FocusAsync();
            await page.Keyboard.TypeAsync("a/b");
            (await page.Locator("#ms-find-input").InputValueAsync()).Should().Be("a/b");

            // With the shortcut sheet open, neither key leaves it.
            await page.Locator("a.ms-nav-link").First.FocusAsync();
            await page.Keyboard.PressAsync("Shift+Slash");
            await page.WaitForFunctionAsync("() => { const d = document.getElementById('ms-shortcuts'); return d !== null && d.open; }", null, Wait);

            await page.Keyboard.PressAsync("Slash");
            await page.Keyboard.PressAsync("Control+k");
            (await FocusIsInTheBoxAsync(page)).Should().BeFalse("a dialog that is open keeps the keyboard");
            (await page.EvaluateAsync<bool>("() => document.activeElement.closest('#ms-shortcuts') !== null")).Should().BeTrue();

            studio.AssertClean("pressing / where it is not a shortcut");
        });

    [BrowserFact]
    public Task Several_matches_are_a_list_the_arrow_keys_walk_and_Escape_leaves() =>
        RunAsync("marten-find-list-keys", async studio =>
        {
            IPage page = studio.Page;

            // An order's summary document and its stream share an id, and both are worth offering.
            string id = await FirstIdOfAsync(studio, "ordersummary");

            await studio.GoAsync(fixture.Root.StudioUrl());
            await WaitForTheBoxAsync(studio);
            string before = page.Url;

            await page.Locator("#ms-find-input").FillAsync(id);
            await page.Keyboard.PressAsync("Enter");

            ILocator hits = page.Locator(".ms-find-panel a.ms-find-hit");
            await hits.First.WaitForAsync();
            (await hits.CountAsync()).Should().BeGreaterThanOrEqualTo(2, "the document and its stream");
            (await page.Locator(".ms-find-status").InnerTextAsync()).Should().MatchRegex("^\\d+ results$");
            page.Url.Should().Be(before, "a list is for the visitor to choose from");

            await page.Keyboard.PressAsync("ArrowDown");
            (await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('href')")).Should().Be(await hits.Nth(0).GetAttributeAsync("href"));
            await page.Keyboard.PressAsync("ArrowDown");
            (await page.EvaluateAsync<string>("() => document.activeElement.getAttribute('href')")).Should().Be(await hits.Nth(1).GetAttributeAsync("href"));
            await page.Keyboard.PressAsync("ArrowUp");
            await page.Keyboard.PressAsync("ArrowUp");
            (await FocusIsInTheBoxAsync(page)).Should().BeTrue("ArrowUp from the first result returns to the box");

            await page.Keyboard.PressAsync("ArrowDown");
            await page.Keyboard.PressAsync("Escape");
            (await FocusIsInTheBoxAsync(page)).Should().BeTrue("Escape on a result returns to the box");
            await page.Locator(".ms-find-panel").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 30_000 });
            (await page.Locator("#ms-find-input").InputValueAsync()).Should().Be(id, "Escape closes the panel and keeps the text");

            // A result is a link the router follows, not a page load.
            await page.Keyboard.PressAsync("Enter");
            await hits.First.WaitForAsync();
            await hits.Nth(1).ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("events/streams/s", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });
            studio.AssertNoPageFetch("following a find result");
            studio.AssertClean("walking the find results");
        });

    [BrowserFact]
    public Task A_value_that_matches_nothing_says_so_and_offers_the_other_stores_when_there_are_any() =>
        RunAsync("marten-find-nothing", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl());
            await WaitForTheBoxAsync(studio);

            await page.Locator("#ms-find-input").FillAsync("no-such-id-anywhere-0001");
            await page.Keyboard.PressAsync("Enter");

            ILocator panel = page.Locator("[data-ms-find-panel]");
            await panel.WaitForAsync();
            (await page.Locator(".ms-find-status").InnerTextAsync()).Should().Be("Nothing found");
            (await panel.InnerTextAsync()).Should().Contain("matched \"no-such-id-anywhere-0001\"").And.Contain("Searched:");

            await page.Keyboard.PressAsync("Escape");
            await panel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 30_000 });
            (await FocusIsInTheBoxAsync(page)).Should().BeTrue();

            studio.AssertClean("finding nothing");
        });

    [BrowserFact]
    public Task At_1440_px_the_box_is_in_the_header_and_the_magnifier_is_not() =>
        RunAsync("marten-find-wide", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl());
            await WaitForTheBoxAsync(studio);

            (await page.Locator(".ms-header-find #ms-find-input").IsVisibleAsync()).Should().BeTrue();
            (await page.Locator(".ms-find-toggle").IsVisibleAsync()).Should().BeFalse("the magnifier is for the narrow screens");

            double[] header = await page.EvaluateAsync<double[]>(
                "() => { const h = document.querySelector('.ms-header').getBoundingClientRect(); const f = document.querySelector('.ms-header-find').getBoundingClientRect(); return [h.height, f.left, f.right, document.documentElement.clientWidth]; }");
            header[0].Should().BeLessThan(100, "the header stays one row beside the box");
            header[2].Should().BeLessThanOrEqualTo(header[3]);

            await studio.AssertNoSidewaysScrollAsync("the header with the Find box at 1440 px");
            studio.AssertClean("showing the Find box in the header");
        });

    [BrowserFact]
    public Task At_390_px_the_box_is_a_magnifier_that_opens_a_popover_and_the_header_keeps_its_two_rows() =>
        RunAsync("marten-find-phone", async studio =>
        {
            IPage page = studio.Page;
            await page.SetViewportSizeAsync(390, 844);

            await studio.GoAsync(fixture.Root.StudioUrl());
            await WaitForTheBoxAsync(studio);

            (await page.Locator(".ms-find-toggle").IsVisibleAsync()).Should().BeTrue();
            (await page.Locator("#ms-find-input").IsVisibleAsync()).Should().BeFalse("collapsed, the box is not in the header");

            string layout = await page.EvaluateAsync<string>(
                """
                () => JSON.stringify({
                  header: Math.round(document.querySelector('.ms-header').getBoundingClientRect().height),
                  parts: [...document.querySelectorAll('.ms-header-left > *, .ms-header-find > .ms-find-toggle, .ms-header-right > *, .ms-scope-selector > *')].map(e => {
                    const r = e.getBoundingClientRect();
                    return e.className.toString().split(' ')[0] + ':' + Math.round(r.left) + '-' + Math.round(r.right) + ' y' + Math.round(r.top) + '+' + Math.round(r.height);
                  }),
                })
                """);
            bool[] fits = await page.EvaluateAsync<bool[]>(
                """
                () => {
                  const width = document.documentElement.clientWidth;
                  const outside = [...document.querySelectorAll('.ms-header *')].filter(e => {
                    const r = e.getBoundingClientRect();
                    return r.width > 0 && r.height > 0 && getComputedStyle(e).position !== 'absolute' && (r.right > width + 0.5 || r.left < -0.5);
                  });
                  const doc = document.documentElement;
                  return [doc.scrollWidth <= doc.clientWidth, outside.length === 0];
                }
                """);
            int headerHeight = JsonElement.Parse(layout).GetProperty("header").GetInt32();
            headerHeight.Should().BeLessThan(100, "the scope on one row and the environment, the magnifier and the actions on one more: " + layout);
            fits[0].Should().BeTrue("the page does not scroll sideways at 390 px: " + layout);
            fits[1].Should().BeTrue("and the magnifier does not push anything out of the header: " + layout);

            // The key opens it, as the button does.
            await page.Locator("a.ms-nav-link").First.FocusAsync();
            await page.Keyboard.PressAsync("Slash");
            await page.WaitForFunctionAsync("() => document.getElementById('ms-find-popover').matches(':popover-open')", null, Wait);
            (await FocusIsInTheBoxAsync(page)).Should().BeTrue("/ opens the popover and focuses the box");

            string id = await FirstIdOfAsync(studio, "customer");
            await studio.GoAsync(fixture.Root.StudioUrl());
            await WaitForTheBoxAsync(studio);

            await page.Locator(".ms-find-toggle").ClickAsync();
            await page.WaitForFunctionAsync("() => document.getElementById('ms-find-popover').matches(':popover-open')", null, Wait);
            await page.Locator("#ms-find-input").FillAsync(id);
            await page.Keyboard.PressAsync("Enter");

            await page.WaitForURLAsync(static url => url.Contains("documents/customer/doc", StringComparison.Ordinal), new PageWaitForURLOptions { Timeout = 30_000 });
            await page.WaitForFunctionAsync("() => !document.getElementById('ms-find-popover').matches(':popover-open')", null, Wait);

            await studio.AssertNoSidewaysScrollAsync("the page after a find at 390 px");
            studio.AssertClean("using the Find popover at 390 px");
        });
}
