using Microsoft.Playwright;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// The shared menu and the rail's foot in a real browser (1.0-UIFIX): a menu opens at its own button, whatever the page's
/// scroll or the viewport's width, and the rail's credit sits at the rail's foot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a browser.</b> Where a popover lands is the browser's layout, not markup: the Snapshot menu once opened centred in
/// the viewport, over a finding's SQL, because its class had no placement rule - and every markup-level test of it passed.
/// Only a bounding box says where a menu really is.
/// </para>
/// <para>
/// The menu is measured against its own button, never against a pixel position: below it, within a few pixels, inside the
/// viewport, and - where the button is near the right edge - right-aligned with it.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class PostgresStudioMenuScenarioTests(BrowserSuiteFixture fixture)
{
    private const string HealthPath = "health?store=tenants&db=tenant_c";

    /// <summary>How far below its button a menu may start: the stylesheet's gap, and a few pixels of rounding.</summary>
    private const double GapTolerance = 12;

    [BrowserFact]
    public Task The_Snapshot_menu_opens_at_its_button_on_a_wide_screen_scrolled_or_not() =>
        BrowserScenario.RunAsync(fixture.Browser, "snapshot-menu-wide", fixture.Host, "admin", async studio =>
        {
            await OpenHealthAsync(studio);
            await AssertMenuAtItsButtonAsync(studio, scrolled: false);
            await AssertMenuAtItsButtonAsync(studio, scrolled: true);

            studio.AssertClean("opening the Snapshot menu at 1440 px");
        });

    [BrowserFact]
    public Task The_Snapshot_menu_opens_at_its_button_on_a_phone() =>
        BrowserScenario.RunAsync(
            fixture.Browser,
            "snapshot-menu-phone",
            fixture.Host,
            "admin",
            async studio =>
            {
                await OpenHealthAsync(studio);
                await AssertMenuAtItsButtonAsync(studio, scrolled: false);
                await AssertMenuAtItsButtonAsync(studio, scrolled: true);

                studio.AssertClean("opening the Snapshot menu at 390 px");
            },
            viewportWidth: 390);

    [BrowserFact]
    public Task The_copy_menu_of_a_database_object_opens_at_its_button() =>
        BrowserScenario.RunAsync(fixture.Browser, "object-copy-menu", fixture.Host, "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl("database/object?schema=quartz&name=qrtz_triggers"));
            await studio.WaitForCircuitAsync();

            ILocator button = page.Locator(".ms-db-copy-menu-button").First;
            await button.WaitForAsync();
            await button.ClickAsync();

            ILocator menu = page.Locator(".ms-db-copy-popover:popover-open");
            await menu.WaitForAsync();

            await AssertAtButtonAsync(page, button, menu, "the object's Copy menu");
            studio.AssertClean("opening the object's Copy menu");
        });

    [BrowserFact]
    public Task The_JSON_viewers_copy_menu_opens_at_the_row_that_asked_for_it() =>
        BrowserScenario.RunAsync(fixture.Browser, "json-copy-menu", fixture.Host, "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl(HealthPath));
            await studio.WaitForCircuitAsync();
            await page.Locator(".ms-snapshot-menu-button").ClickAsync();
            await page.Locator(".ms-snapshot-menu-popover .ms-menu-item", new PageLocatorOptions { HasTextString = "View JSON" }).ClickAsync();

            ILocator view = page.Locator(".ms-snapshot-view");
            await view.WaitForAsync();

            ILocator key = view.Locator("[popovertarget]").First;
            await key.WaitForAsync();
            await key.ClickAsync();

            ILocator menu = page.Locator(".ms-copy-menu:popover-open");
            await menu.WaitForAsync();

            await AssertAtButtonAsync(page, key, menu, "the JSON viewer's copy menu");
            studio.AssertClean("opening the JSON viewer's copy menu");
        });

    [BrowserFact]
    public Task The_rails_credit_stands_at_the_rails_foot_in_three_lines() =>
        BrowserScenario.RunAsync(fixture.Browser, "rail-credit", fixture.Host, "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl());
            await studio.WaitForCircuitAsync();

            ILocator credit = page.Locator(".ms-sidebar .ms-rail-credit");
            await credit.WaitForAsync();

            (await credit.Locator(".ms-rail-credit-line").AllTextContentsAsync()).Select(static x => x.Trim()).Should().HaveCount(3);

            Box rail = await BoxAsync(page.Locator(".ms-sidebar-inner"));
            Box foot = await BoxAsync(credit);
            Box list = await BoxAsync(page.Locator(".ms-sidebar-nav"));

            (rail.Bottom - foot.Bottom).Should().BeInRange(0, 24, "the credit stands at the rail's foot");
            (page.ViewportSize!.Height - rail.Bottom).Should().BeInRange(-1, 1, "the rail is as tall as the window, so its foot is the window's");
            foot.Top.Should().BeGreaterThanOrEqualTo(list.Bottom - 1, "the credit never covers the navigation");

            // Each line is a line of its own: three lines are three text rows, one below the other.
            ILocator lines = credit.Locator(".ms-rail-credit-line");
            Box first = await BoxAsync(lines.Nth(0));
            Box second = await BoxAsync(lines.Nth(1));
            Box third = await BoxAsync(lines.Nth(2));

            second.Top.Should().BeGreaterThanOrEqualTo(first.Bottom - 1);
            third.Top.Should().BeGreaterThanOrEqualTo(second.Bottom - 1);

            // On a long page the rail stays with the window, and so does its credit.
            await studio.GoAsync(fixture.Host.StudioUrl(HealthPath));
            await page.Locator("table.ms-health-check-table tbody tr").First.WaitForAsync();
            await page.EvaluateAsync("() => window.scrollTo(0, 1500)");

            Box scrolledFoot = await BoxAsync(page.Locator(".ms-sidebar .ms-rail-credit"));
            (page.ViewportSize!.Height - scrolledFoot.Bottom).Should().BeInRange(0, 24, "the credit stays at the foot of the window as the page scrolls");

            studio.AssertClean("looking at the rail's credit");
        });

    [BrowserFact]
    public Task The_rails_credit_does_not_cover_the_navigation_on_a_phone() =>
        BrowserScenario.RunAsync(
            fixture.Browser,
            "rail-credit-phone",
            fixture.Host,
            "admin",
            async studio =>
            {
                IPage page = studio.Page;

                await studio.GoAsync(fixture.Host.StudioUrl());
                await studio.WaitForCircuitAsync();
                await page.Locator(".ms-sidebar-nav .ms-nav-link").First.WaitForAsync();

                Box list = await BoxAsync(page.Locator(".ms-sidebar-nav"));
                ILocator credit = page.Locator(".ms-sidebar .ms-rail-credit");

                // The 56 px rail has no room for words: the credit is taken out of sight, clipped to a pixel, and
                // whatever is left of it is after the list.
                Box foot = await BoxAsync(credit);
                (foot.Width <= 2 || foot.Top >= list.Bottom - 1).Should().BeTrue(
                    "the credit is out of sight on the 56 px rail or after the list, never over it; it is " + foot + " and the list " + list);

                studio.AssertClean("looking at the rail on a phone");
            },
            viewportWidth: 390);

    private async Task OpenHealthAsync(StudioPage studio)
    {
        await studio.GoAsync(fixture.Host.StudioUrl(HealthPath));
        await studio.WaitForCircuitAsync();
        await studio.Page.Locator("table.ms-health-check-table tbody tr").First.WaitForAsync();
    }

    /// <summary>
    /// Opens the Snapshot menu and holds it to its button, then closes it by Escape and by a click elsewhere, each time with
    /// focus back on the button.
    /// </summary>
    private static async Task AssertMenuAtItsButtonAsync(StudioPage studio, bool scrolled)
    {
        IPage page = studio.Page;
        ILocator button = page.Locator(".ms-snapshot-menu-button");
        ILocator menu = page.Locator(".ms-snapshot-menu-popover");

        await page.EvaluateAsync("y => window.scrollTo(0, y)", scrolled ? 400 : 0);
        await button.ScrollIntoViewIfNeededAsync();

        await button.ClickAsync();
        await page.Locator(".ms-snapshot-menu-popover:popover-open").WaitForAsync();

        await AssertAtButtonAsync(page, button, menu, "the Snapshot menu" + (scrolled ? ", scrolled" : string.Empty));

        // The item details are the UI's font: the menu's own words are, and so is what they say under them.
        string label = await page.Locator(".ms-snapshot-menu-popover .ms-menu-item-label").First.EvaluateAsync<string>("e => getComputedStyle(e).fontFamily");
        string detail = await page.Locator(".ms-snapshot-menu-popover .ms-menu-item-detail").First.EvaluateAsync<string>("e => getComputedStyle(e).fontFamily");
        detail.Should().Be(label, "an item's second line is menu text, not code");

        // Escape closes it and leaves focus where the visitor started.
        await page.Keyboard.PressAsync("Escape");
        await page.Locator(".ms-snapshot-menu-popover:popover-open").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
        await FocusIsOnAsync(page, button);

        // A click anywhere else closes it.
        await button.ClickAsync();
        await page.Locator(".ms-snapshot-menu-popover:popover-open").WaitForAsync();
        await page.Mouse.ClickAsync(5, 5);
        await page.Locator(".ms-snapshot-menu-popover:popover-open").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    private static async Task FocusIsOnAsync(IPage page, ILocator button)
    {
        string? id = await button.GetAttributeAsync("id");

        // Focus moves in the same turn as the close for the browser, and a turn later for the circuit's own call.
        await page.WaitForFunctionAsync("id => document.activeElement && document.activeElement.id === id", id);
    }

    /// <summary>The menu's box against the button's: below it, close, inside the viewport, and touching it sideways.</summary>
    private static async Task AssertAtButtonAsync(IPage page, ILocator button, ILocator menu, string what)
    {
        Box anchor = await BoxAsync(button);
        Box popup = await BoxAsync(menu);

        PageViewportSizeResult viewport = page.ViewportSize!;

        popup.Left.Should().BeGreaterThanOrEqualTo(-1, what + " stays inside the viewport on the left");
        popup.Right.Should().BeLessThanOrEqualTo(viewport.Width + 1, what + " stays inside the viewport on the right");
        popup.Bottom.Should().BeLessThanOrEqualTo(viewport.Height + 1, what + " stays inside the viewport below");
        popup.Top.Should().BeGreaterThanOrEqualTo(-1, what + " stays inside the viewport above");

        // The menu is anchored to the button: below it, within a few pixels - or above it, if there is no room below.
        bool below = popup.Top >= anchor.Bottom - 1 && popup.Top - anchor.Bottom <= GapTolerance;
        bool above = popup.Bottom <= anchor.Top + 1 && anchor.Top - popup.Bottom <= GapTolerance;
        (below || above).Should().BeTrue(
            what + " opens against its button; the button is " + anchor + " and the menu " + popup);

        // Sideways, the menu and the button share an edge: right-aligned where there is room, left-aligned where not.
        bool rightAligned = Math.Abs(popup.Right - anchor.Right) <= 2;
        bool leftAligned = Math.Abs(popup.Left - anchor.Left) <= 2;
        (rightAligned || leftAligned).Should().BeTrue(
            what + " lines up with its button's right or left edge; the button is " + anchor + " and the menu " + popup);
    }

    private static async Task<Box> BoxAsync(ILocator locator)
    {
        LocatorBoundingBoxResult? box = await locator.BoundingBoxAsync();
        box.Should().NotBeNull("the element is on screen");

        return new Box(box!.X, box.Y, box.Width, box.Height);
    }

    private readonly record struct Box(double X, double Y, double Width, double Height)
    {
        public double Left => X;

        public double Right => X + Width;

        public double Top => Y;

        public double Bottom => Y + Height;

        public override string ToString() => $"[x {X:0.#}-{Right:0.#}, y {Y:0.#}-{Bottom:0.#}]";
    }
}
