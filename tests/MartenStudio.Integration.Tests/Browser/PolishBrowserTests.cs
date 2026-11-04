using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-POLISH, in a browser at 1280 px against the sample host: Maintenance's Triggers table has no column clipped, a LINQ
/// refusal's caret echo has the width the SQL console's has, and LINQ mode keeps its recent queries in the browser, apart from
/// the other two modes'.
/// </summary>
[Collection(BrowserSuite.Name)]
public class PolishBrowserTests(BrowserSuiteFixture fixture)
{
    /// <summary>What the excerpt keeps, in pixels: 18rem at the browser's default 16 px, less a pixel or two of rounding.</summary>
    private const int ExcerptFloor = 286;

    /// <summary>
    /// The Triggers table at 1280 px: the name column no longer takes the room of the columns after it, so on the sample (PostgreSQL
    /// 17: eight columns) the region has nothing to scroll and every column, the last included, is inside it. A region that
    /// scrolled would still be correct (D25); this is the table not needing it.
    /// </summary>
    [BrowserFact]
    public async Task The_Triggers_table_fits_its_region_at_1280_px()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "polish-triggers-1280", fixture.Root, "admin", async studio =>
        {
            await studio.Page.SetViewportSizeAsync(1280, 800);
            await studio.GoAsync(fixture.Root.StudioUrl("maintenance"));
            await studio.Page.Locator(".ms-maint-table").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            string[] clipped = await studio.Page.EvaluateAsync<string[]>(
                """
                () => {
                    const table = document.querySelector('.ms-maint-table');
                    const region = table.closest('.ms-table-scroll').getBoundingClientRect();
                    return [...table.querySelectorAll('thead th')]
                        .filter(th => { const r = th.getBoundingClientRect(); return r.left < region.left - 1 || r.right > region.right + 1; })
                        .map(th => th.textContent.trim());
                }
                """);

            clipped.Should().BeEmpty("every column of the Triggers table is inside its scroll region at 1280 px");

            (await studio.Page.EvaluateAsync<bool>(
                "() => { const r = document.querySelector('.ms-maint-table').closest('.ms-table-scroll'); return r.scrollWidth <= r.clientWidth + 1; }"))
                .Should().BeTrue("the region has nothing to scroll to");

            (await studio.Page.EvaluateAsync<double>(
                "() => document.querySelector('.ms-maint-name').getBoundingClientRect().width"))
                .Should().BeLessThanOrEqualTo(15 * 16 + 20, "the name column stops at 15rem, whatever the identifier");

            studio.AssertClean("the Maintenance page at 1280 px");
        });
    }

    /// <summary>
    /// A LINQ refusal's excerpt - the Order-by's and the predicate's - keeps the width the SQL console's has, at 1280 px with the
    /// saved-queries drawer beside the editor, instead of shrinking to what the message leaves.
    /// </summary>
    [BrowserFact]
    public async Task A_LINQ_refusal_s_caret_echo_is_as_wide_as_the_SQL_consoles_at_1280_px()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "polish-caret-1280", fixture.Root, "admin", async studio =>
        {
            await studio.Page.SetViewportSizeAsync(1280, 800);

            await studio.GoAsync(fixture.Root.StudioUrl(
                "query?mode=linq&target=events&predicate=" + Uri.EscapeDataString("Version > 0") + "&orderby=" + Uri.EscapeDataString("NoSuchMember desc")));
            await studio.Page.Locator("#ms-query-linq-where").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await studio.Page.Locator(".ms-query-run").ClickAsync();
            ILocator order = studio.Page.Locator(".ms-query-linq-order-error .ms-query-caret");
            await order.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await order.BoundingBoxAsync())!.Width.Should().BeGreaterThanOrEqualTo(ExcerptFloor, "the Order-by's excerpt is not squeezed by its message");

            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=linq&target=events&predicate=" + Uri.EscapeDataString("NoSuchMember > 0")));
            await studio.Page.Locator("#ms-query-linq-where").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await studio.Page.Locator(".ms-query-run").ClickAsync();
            ILocator predicate = studio.Page.Locator(".ms-query-editor-status .ms-query-caret");
            await predicate.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await predicate.BoundingBoxAsync())!.Width.Should().BeGreaterThanOrEqualTo(ExcerptFloor, "the predicate's excerpt is not squeezed by its message");

            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=sql&sql=" + Uri.EscapeDataString("select * from nosuchtable_xyz where a = 1")));
            await studio.Page.Locator(".ms-query-run").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await studio.Page.Locator(".ms-query-run").First.ClickAsync();
            ILocator console = studio.Page.Locator(".ms-query-error .ms-query-caret");
            await console.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await console.BoundingBoxAsync())!.Width.Should().BeGreaterThanOrEqualTo(ExcerptFloor, "the console's own excerpt is the width the LINQ ones now have");

            studio.AssertClean("refusals at 1280 px");
        });
    }

    /// <summary>
    /// LINQ mode's drawer: a run that worked is at the head of its recent list, the list survives a reload (it lives in the browser's
    /// <c>localStorage</c>), picking it puts the predicate and the ordering back, and the SQL console's drawer does not show it.
    /// </summary>
    [BrowserFact]
    public async Task A_LINQ_run_is_kept_in_its_own_recent_list_and_comes_back_when_picked()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "polish-linq-recent", fixture.Root, "admin", async studio =>
        {
            await studio.Page.SetViewportSizeAsync(1280, 800);
            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=linq&target=events"));
            await studio.Page.Locator("#ms-query-linq-where").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await studio.Page.Locator("#ms-query-linq-where").FillAsync("Version > 0");
            await studio.Page.Locator("#ms-query-linq-orderby").FillAsync("Sequence");
            await studio.Page.Locator("#ms-query-linq-orderby").PressAsync("Control+Enter");

            ILocator recent = studio.Page.Locator(".ms-query-drawer-recent .ms-query-drawer-pick");
            await recent.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await recent.First.TextContentAsync())!.Trim().Should().Be("Version > 0");

            // A reload: the list is read back from the browser, and picking the entry fills both boxes.
            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=linq&target=events"));
            await studio.Page.Locator("#ms-query-linq-where").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await recent.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await recent.First.ClickAsync();

            await studio.Page.WaitForFunctionAsync(
                "() => document.querySelector('#ms-query-linq-where').value === 'Version > 0' && document.querySelector('#ms-query-linq-orderby').value === 'Sequence'",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            // The SQL console keeps a list of its own: the LINQ entry is not in it.
            await studio.GoAsync(fixture.Root.StudioUrl("query?mode=sql"));
            await studio.Page.Locator(".ms-query-drawer").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await studio.Page.Locator(".ms-query-drawer-recent .ms-query-drawer-pick").CountAsync()).Should().Be(0);

            studio.AssertClean("a LINQ run kept in its recent list");
        });
    }
}
