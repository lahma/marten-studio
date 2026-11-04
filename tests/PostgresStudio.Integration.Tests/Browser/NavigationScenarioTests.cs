using Microsoft.Playwright;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-NAV: navigation inside Postgres Studio in a real browser. The studio intercepts its own navigation - a link, Back and
/// Forward and every address a page writes are rendered by the circuit - so a move fetches no page and cancels nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every pushing page proves it the same way:</b> act, wait for the address the act writes (a condition on
/// <c>window.location</c> read in the page, never <c>WaitForURLAsync</c>), wait for the content that address implies, then
/// <see cref="StudioPage.AssertNoPageFetch" />: no request that asked for <c>blazor-enhanced-nav=on</c> and no failed request. That
/// is a count, not a timing. A scenario starts only once <c>StudioPage.GoAsync</c> has seen <c>data-ms-interactive</c>, so a click
/// is never earlier than interception.
/// </para>
/// <para>
/// With the AppContext switch <c>PostgresStudio.DisableNavigationInterception</c> on (the suite is run once that way,
/// <see cref="StudioPage.InterceptionOffVariable" />) the studio navigates as 1.1 did and <see cref="StudioPage.AssertNoPageFetch" />
/// accepts a page fetch; the three scenarios about what interception itself does are skipped there.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class NavigationScenarioTests(BrowserSuiteFixture fixture)
{
    private const string SkippedWhenOff = "navigation interception is switched off in this run (1.1's navigation), and this scenario is about what interception does";

    private Task RunAsync(string name, string user, Func<StudioPage, Task> body) =>
        BrowserScenario.RunAsync(fixture.Browser, name, fixture.Host, user, body);

    private static ILocator Rail(IPage page, string label) =>
        page.Locator("a.ms-nav-link:has(.ms-nav-link-text:text-is('" + label + "'))");

    /// <summary>The first data row's text, for telling one page of rows from another.</summary>
    private static async Task<string> FirstRowAsync(IPage page)
    {
        ILocator first = page.Locator(".ms-row-grid tbody tr.ms-row-item").First;
        await first.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        return (await first.InnerTextAsync()).Trim();
    }

    private static async Task GoBackAsync(IPage page) => await page.EvaluateAsync("() => history.back()");

    private static async Task GoForwardAsync(IPage page) => await page.EvaluateAsync("() => history.forward()");

    [BrowserFact]
    public async Task The_SQL_console_Run_writes_the_statement_and_keeps_the_database_without_fetching_a_page()
    {
        await RunAsync("nav-sql-run", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl("query?store=tenants&db=tenant_b"));

            ILocator editor = page.Locator("#ms-query-sql");
            await editor.WaitForAsync();
            await editor.FillAsync("select count(*) as customers from public.customers");
            await editor.BlurAsync();
            await page.Locator(".ms-query-panel-sql .ms-query-run").ClickAsync();

            await studio.WaitForLocationAsync("location.search.includes('sql=') && location.search.includes('db=tenant_b')");
            await page.Locator("table.ms-query-grid tbody tr").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("running a statement in the SQL console");
            studio.AssertClean("running a statement in the SQL console");
        });
    }

    [BrowserFact]
    public async Task The_Plan_page_writes_its_mode_options_and_run_without_fetching_a_page()
    {
        await RunAsync("nav-plan", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl("plan?store=tenants&db=tenant_b&sql="
                + Uri.EscapeDataString("select c.name from public.customers c order by c.name")));
            await page.Locator("#ms-plan-sql").WaitForAsync();

            await page.Locator("input[name='ms-plan-mode'][value='analyze']").CheckAsync();
            await studio.WaitForLocationAsync("location.search.includes('mode=analyze') && location.search.includes('db=tenant_b')");

            await page.Locator("#ms-plan-option-settings").CheckAsync();
            await studio.WaitForLocationAsync("location.search.includes('settings')");

            await page.Locator(".ms-query-run").ClickAsync();
            await page.Locator(".ms-plan-tree").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("changing the Plan page's mode and options and running it");
            studio.AssertClean("changing the Plan page's mode and options and running it");
        });
    }

    [BrowserFact]
    public async Task Relationships_schema_chips_write_the_address_without_fetching_a_page()
    {
        await RunAsync("nav-relationships", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl("relationships"));
            await page.Locator("svg.ms-graph-svg").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            ILocator chips = page.Locator(".ms-graph-schema-chip");
            await chips.Nth(1).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await chips.Nth(1).ClickAsync();
            await studio.WaitForLocationAsync("location.search.includes('schema=')");
            await page.Locator(".ms-graph-schema-chip").Nth(1).And(page.Locator("[aria-pressed='true']")).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await page.Locator(".ms-graph-schema-chip").First.ClickAsync();
            await studio.WaitForLocationAsync("!location.search.includes('schema=')");
            await page.Locator(".ms-graph-schema-chip").First.And(page.Locator("[aria-pressed='true']")).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("choosing a schema chip on Relationships");
            studio.AssertClean("choosing a schema chip on Relationships");
        });
    }

    [BrowserFact]
    public async Task The_database_browser_filter_and_kind_tabs_write_the_address_without_fetching_a_page()
    {
        await RunAsync("nav-database-objects", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl("database"));
            await page.Locator("a.ms-db-name[title='quartz.qrtz_triggers']").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            ILocator filter = page.Locator(".ms-db-toolbar input.ms-db-name-filter");
            await filter.FillAsync("qrtz_trig");
            await filter.PressAsync("Enter");

            await studio.WaitForLocationAsync("location.search.includes('q=qrtz_trig')");
            await page.WaitForFunctionAsync(
                "() => { const names = [...document.querySelectorAll('a.ms-db-name')]; return names.length > 0 && names.every(a => a.title.includes('qrtz_trig')); }",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            // A kind tab is a link: it is rendered by the circuit too.
            await page.Locator("nav.ms-db-kind-tabs a").Nth(1).ClickAsync();
            await studio.WaitForLocationAsync("location.search.includes('kind=')");
            await page.Locator("nav.ms-db-kind-tabs a[aria-current='page']").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("filtering the database browser and moving to another kind of object");
            studio.AssertClean("filtering the database browser and moving to another kind of object");
        });
    }

    [BrowserFact]
    public async Task A_tables_rows_page_filters_pages_and_opens_a_row_with_Enter_without_fetching_a_page()
    {
        await RunAsync("nav-table-rows", "admin", async studio =>
        {
            IPage page = studio.Page;

            // Tenant A's orders: 160 rows, so there is a second page at the default page size of 50.
            await studio.GoAsync(fixture.Host.StudioUrl("database/object?schema=public&name=orders&tab=rows&store=tenants&db=tenant_a"));
            string firstPage = await FirstRowAsync(page);

            await page.Locator(".ms-pager button", new PageLocatorOptions { HasTextString = "Next" }).ClickAsync();
            await studio.WaitForLocationAsync("location.search.includes('cursor=') || location.search.includes('page=')");
            await page.WaitForFunctionAsync(
                "first => { const r = document.querySelector('.ms-row-grid tbody tr.ms-row-item'); return r !== null && r.innerText.trim() !== first; }",
                firstPage,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            // A filter starts the walk again, from the top.
            await page.Locator("#ms-row-filter-input").FillAsync("status = shipped");
            await page.Locator(".ms-row-filter button[type=submit]").ClickAsync();
            await studio.WaitForLocationAsync("location.search.includes('q=') && !location.search.includes('cursor=')");
            await page.WaitForFunctionAsync(
                "() => { const rows = [...document.querySelectorAll('.ms-row-grid tbody tr.ms-row-item')]; return rows.length > 0 && rows.every(r => r.innerText.includes('shipped')); }",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            // Enter on a focused row opens its detail (RowGrid's NavigateTo), and Back returns to the filtered rows.
            ILocator row = page.Locator(".ms-row-grid tbody tr.ms-row-item").First;
            await row.FocusAsync();
            await row.PressAsync("Enter");
            await studio.WaitForLocationAsync("location.pathname.endsWith('/database/row')");
            await page.Locator("h1.ms-page-title").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await GoBackAsync(page);
            await studio.WaitForLocationAsync("location.pathname.endsWith('/database/object') && location.search.includes('q=')");
            await page.WaitForFunctionAsync(
                "() => document.querySelectorAll('.ms-row-grid tbody tr.ms-row-item').length > 0",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("paging, filtering and opening a row in a table's rows");
            studio.AssertClean("paging, filtering and opening a row in a table's rows");
        });
    }

    /// <remarks>
    /// Between two pages that run no live loop of their own (the database browser and Relationships): leaving a page that does -
    /// Sessions, the Overview - while its first render is still starting the loop trips <c>StudioLiveUpdates.StartAsync</c> against
    /// its <c>DisposeAsync</c> (a <c>NullReferenceException</c> that ends the circuit). That race is older than interception and is
    /// reported with this packet; it is not what this scenario is about.
    /// </remarks>
    [BrowserFact]
    public async Task A_rail_click_renders_in_the_circuit_changes_the_title_focuses_the_heading_and_Back_and_Forward_land_on_the_right_page()
    {
        await RunAsync("nav-rail-back-forward", "admin", async studio =>
        {
            IPage page = studio.Page;

            // A short viewport, so the tables list scrolls: a move to another page starts at the top.
            await page.SetViewportSizeAsync(StudioPage.ViewportWidth, 400);
            await studio.GoAsync(fixture.Host.StudioUrl("database"));
            await page.Locator("h1.ms-page-title", new PageLocatorOptions { HasTextString = "Database" }).WaitForAsync();
            await page.Locator("a.ms-db-name[title='quartz.qrtz_triggers']").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            string listTitle = await page.TitleAsync();

            await page.EvaluateAsync("() => window.scrollTo(0, 200)");
            await page.WaitForFunctionAsync("() => window.scrollY > 0", null, new PageWaitForFunctionOptions { Timeout = 30_000 });

            await Rail(page, "Relationships").ClickAsync();
            await studio.WaitForLocationAsync("location.pathname.endsWith('/relationships')");
            await page.Locator("h1.ms-page-title", new PageLocatorOptions { HasTextString = "Relationships" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await page.WaitForFunctionAsync("title => document.title !== title", listTitle, new PageWaitForFunctionOptions { Timeout = 30_000 });
            (await page.TitleAsync()).Should().Contain("Relationships", "<PageTitle> follows a move the circuit renders");

            await page.WaitForFunctionAsync(
                "() => document.activeElement !== null && document.activeElement.tagName === 'H1'",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });
            await page.WaitForFunctionAsync("() => window.scrollY === 0", null, new PageWaitForFunctionOptions { Timeout = 30_000 });

            await GoBackAsync(page);
            await studio.WaitForLocationAsync("!location.pathname.endsWith('/relationships')");
            await page.Locator("a.ms-db-name[title='quartz.qrtz_triggers']").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await page.TitleAsync()).Should().Be(listTitle);

            await GoForwardAsync(page);
            await studio.WaitForLocationAsync("location.pathname.endsWith('/relationships')");
            await page.Locator("h1.ms-page-title", new PageLocatorOptions { HasTextString = "Relationships" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("moving by the rail, Back and Forward");
            studio.AssertClean("moving by the rail, Back and Forward");
        });
    }

    [BrowserFact]
    public async Task A_link_inside_the_mount_that_is_not_a_page_is_loaded_by_the_browser_once()
    {
        Assert.SkipWhen(StudioPage.InterceptionIsOff, SkippedWhenOff);

        await RunAsync("nav-force-load-file", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl());

            List<string> documents = [];
            void OnRequest(object? sender, IRequest request)
            {
                if (request.ResourceType == "document")
                {
                    lock (documents)
                    {
                        documents.Add(request.Url);
                    }
                }
            }

            string file = fixture.Host.StudioUrl("_content/PostgresStudio/favicon.svg");
            page.Request += OnRequest;

            try
            {
                // Outside the root component's region, so Blazor does not own it; the click reaches the framework's own delegator.
                await page.EvaluateAsync("() => { const a = document.createElement('a'); a.id = 'ms-nav-probe'; a.setAttribute('href', '_content/PostgresStudio/favicon.svg'); a.textContent = 'probe'; document.body.appendChild(a); }");
                await page.Locator("#ms-nav-probe").ClickAsync();

                await page.WaitForFunctionAsync(
                    "() => document.contentType === 'image/svg+xml'",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 30_000 });
                page.Url.Should().Be(file);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
            finally
            {
                page.Request -= OnRequest;
            }

            string[] toTheFile;
            lock (documents)
            {
                toTheFile = [.. documents.Where(x => string.Equals(x, file, StringComparison.Ordinal))];
            }

            toTheFile.Should().ContainSingle("the browser loaded the file once: the circuit did not fetch it as a page first and no loop followed");
            studio.AssertClean("following a link to a file under the mount");
        });
    }

    [BrowserFact]
    public async Task A_link_to_an_address_under_the_mount_that_does_not_exist_loads_the_hosts_404_once()
    {
        Assert.SkipWhen(StudioPage.InterceptionIsOff, SkippedWhenOff);

        await RunAsync("nav-force-load-404", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl());

            string missing = fixture.Host.StudioUrl("no-such-page");
            studio.ExpectStatus(missing, 404);

            List<string> documents = [];
            void OnRequest(object? sender, IRequest request)
            {
                if (request.ResourceType == "document")
                {
                    lock (documents)
                    {
                        documents.Add(request.Url);
                    }
                }
            }

            page.Request += OnRequest;

            try
            {
                await page.EvaluateAsync("() => { const a = document.createElement('a'); a.id = 'ms-nav-probe'; a.setAttribute('href', 'no-such-page'); a.textContent = 'probe'; document.body.appendChild(a); }");
                await page.Locator("#ms-nav-probe").ClickAsync();

                await studio.WaitForLocationAsync("location.pathname.endsWith('/no-such-page')");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
            finally
            {
                page.Request -= OnRequest;
            }

            string[] toTheAddress;
            lock (documents)
            {
                toTheAddress = [.. documents.Where(x => string.Equals(x, missing, StringComparison.Ordinal))];
            }

            toTheAddress.Should().ContainSingle("one document request, the host's 404, and nothing after it: the studio did not load it again");
            studio.AssertClean("following a link to an unknown address under the mount");
        });
    }

    [BrowserFact]
    public async Task A_link_out_of_the_mount_loads_normally()
    {
        await RunAsync("nav-out-of-mount", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl());

            await page.EvaluateAsync("() => { const a = document.createElement('a'); a.id = 'ms-nav-probe'; a.setAttribute('href', '/'); a.textContent = 'probe'; document.body.appendChild(a); }");
            await page.Locator("#ms-nav-probe").ClickAsync();

            // The sample's landing page, outside the studio's mount.
            await page.Locator("p", new PageLocatorOptions { HasTextString = "Signed in as" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            page.Url.Should().Be(fixture.Host.Url("/"));

            studio.AssertClean("following a link out of the mount");
        });
    }
}
