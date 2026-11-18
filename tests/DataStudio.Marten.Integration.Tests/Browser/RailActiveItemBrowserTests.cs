using Microsoft.Playwright;

namespace DataStudio.Marten.Integration.Tests.Browser;

/// <summary>
/// #83: the shell's rail shows the entry the page marks as active. The Marten rail is longer than a short window, so the shell's own
/// Activity entry, the last of it, starts out of view; the rail brings it in view inside its own scroll box and moves nothing else.
/// </summary>
[Collection(BrowserSuite.Name)]
public class RailActiveItemBrowserTests(BrowserSuiteFixture fixture)
{
    /// <summary>Where the active entry is in the rail's scroll box, as distances from its visible top and bottom edges.</summary>
    private sealed record Placement(bool Found, bool Scrollable, double FromTop, double FromBottom, double RailScrollTop, double PageScrollY);

    private const string Measure =
        """
        () => {
            const rail = document.querySelector('.ms-sidebar-inner');
            const item = rail === null ? null : rail.querySelector('.ms-nav-active');
            if (rail === null || item === null) { return { found: false, scrollable: false, fromTop: 0, fromBottom: 0, railScrollTop: 0, pageScrollY: window.scrollY }; }
            const box = rail.getBoundingClientRect();
            const top = box.top + rail.clientTop;
            const bottom = top + rail.clientHeight;
            const at = item.getBoundingClientRect();
            return { found: true, scrollable: rail.scrollHeight > rail.clientHeight, fromTop: at.top - top, fromBottom: bottom - at.bottom, railScrollTop: rail.scrollTop, pageScrollY: window.scrollY };
        }
        """;

    private static async Task<Placement> PlacementAsync(IPage page)
    {
        System.Text.Json.JsonElement raw = await page.EvaluateAsync<System.Text.Json.JsonElement>(Measure);
        return new Placement(
            raw.GetProperty("found").GetBoolean(),
            raw.GetProperty("scrollable").GetBoolean(),
            raw.GetProperty("fromTop").GetDouble(),
            raw.GetProperty("fromBottom").GetDouble(),
            raw.GetProperty("railScrollTop").GetDouble(),
            raw.GetProperty("pageScrollY").GetDouble());
    }

    private static async Task WaitForActiveInViewAsync(IPage page) =>
        await page.WaitForFunctionAsync(
            "() => { const m = (" + Measure.Trim() + ")(); return m.found && m.fromTop >= -1 && m.fromBottom >= -1; }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

    [BrowserFact]
    public async Task The_active_entry_of_a_page_late_in_the_rail_is_in_view_after_load_and_after_a_click_and_the_page_never_scrolls()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "rail-active-item", fixture.Root, "admin", async studio =>
        {
            IPage page = studio.Page;

            // A window short enough that the Marten groups push the Studio group, with Activity, out of view.
            await page.SetViewportSizeAsync(1280, 500);

            // Not GoAsync: it waits for a store in the address, and the shell's own Activity page has no scope to carry.
            await page.GotoAsync(fixture.Root.StudioUrl("activity"), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await studio.WaitForCircuitAsync();
            await studio.WaitForInteractiveAsync();
            await page.Locator("a.ms-nav-link.ms-nav-active").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await WaitForActiveInViewAsync(page);

            Placement loaded = await PlacementAsync(page);
            loaded.Scrollable.Should().BeTrue("the Marten rail does not fit a 500 px window, which is what makes the entry start out of view");
            loaded.RailScrollTop.Should().BeGreaterThan(0, "the rail scrolled to the entry");
            loaded.PageScrollY.Should().Be(0, "only the rail's own scroll box moved");

            // To the top of the rail and back to a page whose entry is in view there: the click itself must not scroll the rail.
            await page.EvaluateAsync("() => { document.querySelector('.ms-sidebar-inner').scrollTop = 0; }");
            ILocator streams = page.Locator("a.ms-nav-link:has(.ms-nav-link-text:text-is('Streams'))");
            await streams.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await streams.ClickAsync();
            await studio.WaitForLocationAsync("location.pathname.endsWith('/events/streams')");
            await WaitForActiveInViewAsync(page);

            Placement afterClick = await PlacementAsync(page);
            afterClick.PageScrollY.Should().Be(0);

            // The rail is scrolled by hand to the foot and the visitor clicks an entry that is in view there: the rail stays where it is.
            await page.EvaluateAsync("() => { const rail = document.querySelector('.ms-sidebar-inner'); rail.scrollTop = rail.scrollHeight; }");
            Placement scrolled = await PlacementAsync(page);
            await page.Locator("a.ms-nav-link:has(.ms-nav-link-text:text-is('Activity'))").ClickAsync();
            await studio.WaitForLocationAsync("location.pathname.endsWith('/activity')");
            await WaitForActiveInViewAsync(page);

            Placement atFoot = await PlacementAsync(page);
            atFoot.RailScrollTop.Should().Be(scrolled.RailScrollTop, "an entry already in view does not move the rail");
            atFoot.PageScrollY.Should().Be(0);

            studio.AssertNoPageFetch("moving by the rail");
            studio.AssertClean("moving by the rail");
        });
    }
}
