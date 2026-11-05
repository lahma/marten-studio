using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-UX-FIX, in a browser against the sample host: the sweep's layout defects (Projections at 1280 px, the Overview's tiles at 1440 and
/// 1280, Triggers names, Refresh beside the Mark chip), a typed confirmation reached from the keyboard, and Back after a link to a tenant.
/// </summary>
[Collection(BrowserSuite.Name)]
public class UxFixBrowserTests(BrowserSuiteFixture fixture)
{
    private static readonly PageWaitForFunctionOptions Wait = new() { Timeout = 30_000 };

    private Task RunAsync(string name, Func<StudioPage, Task> body) =>
        BrowserScenario.RunAsync(fixture.Browser, name, fixture.Root, "admin", body);

    /// <summary>
    /// Projections at 1280 px: Last progress and Actions (Rebuild, Start, Stop) are inside the table's scroll region, which has nothing
    /// to scroll to. The region would still be correct (D25); this is the table not needing it.
    /// </summary>
    [BrowserFact]
    public Task The_Projections_table_shows_Last_progress_and_Actions_inside_its_region_at_1280_px() =>
        ProjectionsFitAsync("uxfix-projections-1280", widerMetrics: false);

    /// <summary>
    /// The same, with every text run drawn about 5 % wider (a stand-in for ubuntu-latest's fonts, on which the fit had no margin): the
    /// table must hold with slack, so Actions wraps its buttons rather than running out of the region.
    /// </summary>
    [BrowserFact]
    public Task The_Projections_table_fits_its_region_at_1280_px_with_wider_fonts() =>
        ProjectionsFitAsync("uxfix-projections-1280-wide", widerMetrics: true);

    private Task ProjectionsFitAsync(string name, bool widerMetrics) =>
        RunAsync(name, async studio =>
        {
            await studio.Page.SetViewportSizeAsync(1280, 800);
            await studio.GoAsync(fixture.Root.StudioUrl("projections"));
            await studio.Page.Locator(".ms-projections-table tbody tr.ms-shard-row").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            if (widerMetrics)
            {
                await TableFit.WidenAsync(studio.Page);
            }

            TableFit.Report report = await TableFit.MeasureAsync(studio.Page, ".ms-projections-table");

            report.Outside().Should().BeEmpty("every column of the Projections table, Last progress and Actions included, is inside its region at 1280 px: " + report.Describe());

            string[] buttonsOutside = await studio.Page.EvaluateAsync<string[]>(
                """
                () => {
                    const region = document.querySelector('.ms-projections-table').closest('.ms-table-scroll').getBoundingClientRect();
                    return [...document.querySelectorAll('.ms-projections-table .ms-table-cell-actions button')]
                        .filter(b => { const r = b.getBoundingClientRect(); return r.right > region.right + 1 || r.left < region.left - 1; })
                        .map(b => b.textContent.trim());
                }
                """);

            buttonsOutside.Should().BeEmpty("Rebuild, Start and Stop are in view");
            report.Fits.Should().BeTrue("the region has nothing to scroll to: " + report.Describe());

            // Slack, not just a fit (1.2-FINAL-FIX-4): the table fitted with zero to spare on Windows and ran 12 px over on ubuntu's fonts.
            // With the stand-in's wider text the same table still has to fit; without it, it has room for a 5 % font difference twice over.
            report.Slack.Should().BeGreaterThanOrEqualTo(widerMetrics ? 0 : 60, "the table's least width leaves room for a different font: " + report.Describe());

            studio.AssertClean("the Projections page at 1280 px");
        });

    /// <summary>
    /// A typed confirmation from the keyboard (1.2-UX-FIX-FU): Confirm is aria-disabled, not disabled, until the phrase matches, so Tab
    /// lands on it whatever the round trip - typed and Tab pressed with no wait for it to enable - and a press on it before the phrase
    /// matches does nothing (bUnit proves that half). Nothing is confirmed.
    /// </summary>
    [BrowserFact]
    public Task Tab_after_typing_the_phrase_of_a_confirmation_lands_on_Confirm_with_no_wait() =>
        RunAsync("uxfix-confirm-tab", async studio =>
        {
            IPage page = studio.Page;
            await page.SetViewportSizeAsync(1280, 900);
            await studio.GoAsync(fixture.Root.StudioUrl("projections"));

            ILocator rebuild = page.Locator(".ms-projection-rebuild:not([disabled])").First;
            await rebuild.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            string name = (await rebuild.EvaluateAsync<string>("b => b.closest('tr').dataset.projection"));
            await rebuild.ClickAsync();

            await page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.id === 'ms-confirm-input'", null, Wait);

            const string FocusedText = "() => document.activeElement.textContent.trim()";

            // Before the match: Confirm is reachable by Tab, says it is disabled, and pressing it does nothing.
            await page.Keyboard.PressAsync("Tab");
            (await page.EvaluateAsync<string>(FocusedText)).Should().StartWith("Rebuild", "Tab from an empty box lands on Confirm");
            (await page.Locator(".ms-confirm-actions .ms-button-danger").GetAttributeAsync("aria-disabled")).Should().Be("true");

            // (That a press on Confirm before the phrase matches confirms nothing is proved in bUnit, where the owner can be counted.)
            await page.Keyboard.PressAsync("Shift+Tab");
            await page.WaitForFunctionAsync("() => document.activeElement && document.activeElement.id === 'ms-confirm-input'", null, Wait);

            // Typed, and Tab at once: no wait for Confirm to enable.
            await page.Keyboard.TypeAsync(name);
            await page.Keyboard.PressAsync("Tab");

            (await page.EvaluateAsync<string>(FocusedText)).Should().StartWith("Rebuild", "Tab after the last character lands on Confirm, never past it on Cancel");

            await page.Keyboard.PressAsync("Escape");
            await page.WaitForFunctionAsync("() => document.querySelector('dialog.ms-confirm-overlay') === null", null, Wait);

            studio.AssertClean("typing a confirmation's phrase and pressing Tab");
        });

    /// <summary>
    /// The Overview's tiles at 1440 and 1280 px: nothing a tile draws is wider than its card (the Health tile's "N suggestions" ran out of
    /// it at 1440).
    /// </summary>
    [BrowserFact]
    public Task No_tile_of_the_Overview_has_content_wider_than_its_card_at_1440_px() => OverviewTilesFitAsync(1440);

    [BrowserFact]
    public Task No_tile_of_the_Overview_has_content_wider_than_its_card_at_1280_px() => OverviewTilesFitAsync(1280);

    private Task OverviewTilesFitAsync(int width) =>
        RunAsync("uxfix-overview-tiles-" + width, async studio =>
        {
            IPage page = studio.Page;
            await page.SetViewportSizeAsync(width, 900);
            await studio.GoAsync(fixture.Root.StudioUrl());
            await page.Locator(".ms-overview-tiles .ms-stat-card").Nth(5).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            // The Health tile says "checking" until the advisor has answered; it is the one that overflowed, so it is measured answered.
            await page.WaitForFunctionAsync(
                "() => { const v = document.querySelector('.ms-overview-health-tile .ms-stat-card-value'); return v !== null && v.textContent.trim() !== 'checking'; }",
                null,
                Wait);

            string[] wide = await page.EvaluateAsync<string[]>(
                """
                () => [...document.querySelectorAll('.ms-overview-tiles .ms-stat-card')].flatMap(card => {
                    const box = card.getBoundingClientRect();
                    const problems = [];
                    if (card.scrollWidth > card.clientWidth + 1) problems.push(card.textContent.trim() + ': scrollWidth ' + card.scrollWidth + ' > ' + card.clientWidth);
                    for (const e of card.querySelectorAll('*')) {
                        const r = e.getBoundingClientRect();
                        if (r.width > 0 && (r.right > box.right + 0.5 || r.left < box.left - 0.5)) problems.push(card.textContent.trim() + ': ' + e.className + ' ' + Math.round(r.right) + ' > ' + Math.round(box.right));
                    }
                    return problems;
                })
                """);

            wide.Should().BeEmpty("no tile's content is wider than its card at " + width + " px");
            studio.AssertClean("the Overview at " + width + " px");
        });

    /// <summary>
    /// Maintenance › Triggers: a name wraps only after an underscore or a dot, never inside a word ("sweep_c / hild"), and the table
    /// still fits its region (POLISH's assertion, with the names now free to wrap at their <c>&lt;wbr&gt;</c>s).
    /// </summary>
    [BrowserFact]
    public Task A_table_name_on_the_Maintenance_page_breaks_only_after_an_underscore_or_a_dot() =>
        RunAsync("uxfix-maintenance-names", async studio =>
        {
            IPage page = studio.Page;
            await page.SetViewportSizeAsync(1280, 800);
            await studio.GoAsync(fixture.Root.StudioUrl("maintenance"));
            await page.Locator(".ms-maint-name").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            string[] broken = await page.EvaluateAsync<string[]>(
                """
                () => [...document.querySelectorAll('.ms-maint-name .ms-mono')].flatMap(el => {
                    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
                    const found = [];
                    let previous = null;
                    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                        for (let i = 0; i < node.data.length; i++) {
                            const range = document.createRange();
                            range.setStart(node, i);
                            range.setEnd(node, i + 1);
                            const rect = range.getBoundingClientRect();
                            if (previous && rect.height > 0 && rect.top > previous.rect.top + 2 && previous.ch !== '_' && previous.ch !== '.') {
                                found.push(el.textContent + ' breaks after "' + previous.ch + '"');
                            }
                            if (rect.height > 0) previous = { rect, ch: node.data[i] };
                        }
                    }
                    return found;
                })
                """);

            broken.Should().BeEmpty("a name breaks only where a <wbr> offers it");

            (await page.EvaluateAsync<bool>("() => { const r = document.querySelector('.ms-maint-table').closest('.ms-table-scroll'); return r.scrollWidth <= r.clientWidth + 1; }"))
                .Should().BeTrue("the columns stay visible at 1280 px");
            studio.AssertClean("the Maintenance page at 1280 px");
        });

    /// <summary>
    /// Refresh beside Mark's chip at 390 px: the chip and Clear wrap to two lines, and Refresh keeps its own height instead of
    /// stretching to the row's.
    /// </summary>
    [BrowserFact]
    public Task Refresh_beside_the_Mark_chip_keeps_its_own_height_at_390_px() =>
        RunAsync("uxfix-refresh-mark-390", async studio =>
        {
            IPage page = studio.Page;
            await page.SetViewportSizeAsync(390, 800);
            await studio.GoAsync(fixture.Root.StudioUrl("maintenance"));

            ILocator mark = page.Locator(".ms-mark button", new PageLocatorOptions { HasTextString = "Mark" }).First;
            await mark.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.WaitForFunctionAsync("() => { const b = document.querySelector('.ms-mark button'); return b !== null && !b.disabled; }", null, Wait);
            await mark.ClickAsync();
            await page.Locator(".ms-mark-chip").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            double[] heights = await page.EvaluateAsync<double[]>(
                """
                () => {
                    const refresh = [...document.querySelectorAll('.ms-maint-actions > button')].find(b => b.textContent.trim() === 'Refresh');
                    const mark = document.querySelector('.ms-mark');
                    return [refresh.getBoundingClientRect().height, mark.getBoundingClientRect().height];
                }
                """);

            heights[0].Should().BeLessThanOrEqualTo(40, "Refresh is one button's height, not the " + Math.Round(heights[1]) + " px of the Mark chip beside it");
            studio.AssertClean("marking the Maintenance page at 390 px");
        });

    /// <summary>
    /// A clicked link to a tenant moves the picker to it; Back then returns to an address that names no tenant. The address follows the
    /// scope on screen again, instead of being left naming none while the picker still shows the tenant (the sweep's D13). With
    /// <c>PostgresStudio.DisableNavigationInterception</c> on (<see cref="StudioPage.InterceptionOffVariable" />) the shell writes nothing
    /// on a Back (1.2-FINAL-FIX), so there the address stays as the entry Back returned to.
    /// </summary>
    [BrowserFact]
    public Task Back_after_a_link_to_a_tenant_rewrites_the_address_to_name_the_tenant_the_picker_shows() =>
        RunAsync("uxfix-back-tenant", async studio =>
        {
            IPage page = studio.Page;
            await studio.GoAsync(fixture.Root.StudioUrl("tenants"));

            ILocator link = page.Locator("main a[href*='tenant=acme']").First;
            await link.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await link.ClickAsync();

            await studio.WaitForLocationAsync("location.search.includes('tenant=acme') && !location.pathname.endsWith('/tenants')");
            await page.WaitForFunctionAsync("() => document.querySelector('#ms-tenant-select')?.value === 'acme'", null, Wait);

            await page.EvaluateAsync("() => history.back()");
            await studio.WaitForLocationAsync("location.pathname.endsWith('/tenants')");

            if (StudioPage.InterceptionIsOff)
            {
                // With the switch on (1.1's navigation) the shell writes nothing on a Back (1.2-FINAL-FIX): the address is the entry Back
                // returned to, and the pages that follow their address are the ones that act on it. The picker did not move.
                (await page.EvaluateAsync<string>("() => document.querySelector('#ms-tenant-select').value")).Should().Be("acme", "the picker did not move");
                (await page.EvaluateAsync<bool>("() => location.search.includes('tenant=acme')")).Should().BeFalse("the shell did not rewrite the address");
                studio.AssertClean("following a link to a tenant and moving Back, with navigation interception off");
                return;
            }

            // The entry Back returned to named no tenant. The scope on screen is still acme, so the address is written to say so.
            await studio.WaitForLocationAsync("location.pathname.endsWith('/tenants') && location.search.includes('tenant=acme')");
            (await page.EvaluateAsync<string>("() => document.querySelector('#ms-tenant-select').value")).Should().Be("acme", "the picker did not move");

            // And Forward, then Back again, agree the same way: address and picker never disagree at rest.
            await page.EvaluateAsync("() => history.forward()");
            await studio.WaitForLocationAsync("!location.pathname.endsWith('/tenants')");
            await page.EvaluateAsync("() => history.back()");
            await studio.WaitForLocationAsync("location.pathname.endsWith('/tenants') && location.search.includes('tenant=acme')");
            (await page.EvaluateAsync<string>("() => document.querySelector('#ms-tenant-select').value")).Should().Be("acme");

            studio.AssertNoPageFetch("following a link to a tenant and moving Back and Forward");
        });
}
