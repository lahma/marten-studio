using Microsoft.Playwright;

using MartenStudio.SampleDomain;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// 1.2-NAV: navigation inside Marten Studio in a real browser. The studio intercepts its own navigation - a link, Back and
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
/// accepts a page fetch; the scenarios about what interception itself does are skipped there.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class NavigationScenarioTests(BrowserSuiteFixture fixture)
{
    private const string SkippedWhenOff = "navigation interception is switched off in this run (1.1's navigation), and this scenario is about what interception does";

    /// <summary>A page with nothing left loading and either a table, a timeline or an empty state drawn.</summary>
    private const string Settled =
        "() => document.querySelector('.ms-loading-spinner') === null && document.querySelector('main table, main .ms-empty, main .ms-timeline') !== null";

    private Task RunAsync(string name, string user, Func<StudioPage, Task> body) =>
        BrowserScenario.RunAsync(fixture.Browser, name, fixture.Root, user, body);

    private static ILocator Rail(IPage page, string label) =>
        page.Locator("a.ms-nav-link:has(.ms-nav-link-text:text-is('" + label + "'))");

    private static async Task GoBackAsync(IPage page) => await page.EvaluateAsync("() => history.back()");

    private static async Task GoForwardAsync(IPage page) => await page.EvaluateAsync("() => history.forward()");

    private static async Task SettledAsync(IPage page) =>
        await page.WaitForFunctionAsync(Settled, null, new PageWaitForFunctionOptions { Timeout = 30_000 });

    /// <summary>The id of the first document of the list on screen.</summary>
    private static async Task<string> FirstDocumentAsync(IPage page)
    {
        ILocator first = page.Locator(".ms-doc-table tbody tr.ms-doc-row a.ms-doc-id").First;
        await first.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        return (await first.InnerTextAsync()).Trim();
    }

    /// <summary>Waits until the list's first document is <paramref name="id" />.</summary>
    private static async Task WaitForFirstDocumentAsync(IPage page, string id) =>
        await page.WaitForFunctionAsync(
            "id => { const a = document.querySelector('.ms-doc-table tbody tr.ms-doc-row a.ms-doc-id'); return a !== null && a.textContent.trim() === id; }",
            id,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

    /// <summary>Waits until the list's first document is something other than <paramref name="id" />, and returns it.</summary>
    private static async Task<string> WaitForOtherFirstDocumentAsync(IPage page, string id)
    {
        await page.WaitForFunctionAsync(
            "id => { const a = document.querySelector('.ms-doc-table tbody tr.ms-doc-row a.ms-doc-id'); return a !== null && a.textContent.trim() !== id; }",
            id,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        return await FirstDocumentAsync(page);
    }

    [BrowserFact]
    public async Task Documents_search_and_next_page_write_the_address_and_Back_Back_Forward_land_on_each_state()
    {
        await RunAsync("nav-documents", "admin", async studio =>
        {
            IPage page = studio.Page;

            // Five to a page, highest id first: the seeder writes 25 customers (the list starts at 25), and the nine whose e-mail
            // contains "customer0" start at 09 and run over two pages, so each state has a first row of its own.
            await studio.GoAsync(fixture.Root.StudioUrl("documents/customer?size=5"));
            string everyone = await FirstDocumentAsync(page);

            await page.Locator("#ms-doc-search-input").FillAsync("Email ~ customer0");
            await page.Locator(".ms-doc-search button[type=submit]").ClickAsync();
            await studio.WaitForLocationAsync("location.search.includes('q=')");
            string searched = await WaitForOtherFirstDocumentAsync(page, everyone);

            await page.Locator(".ms-pager button", new PageLocatorOptions { HasTextString = "Next" }).ClickAsync();
            await studio.WaitForLocationAsync("location.search.includes('cursor=') || (new URLSearchParams(location.search).get('page') ?? '1') !== '1'");
            await WaitForOtherFirstDocumentAsync(page, searched);

            // Back to the search's first page, back to the unsearched list, and forward to the search again.
            await GoBackAsync(page);
            await studio.WaitForLocationAsync("location.search.includes('q=') && !location.search.includes('cursor=') && (new URLSearchParams(location.search).get('page') ?? '1') === '1'");
            await WaitForFirstDocumentAsync(page, searched);

            await GoBackAsync(page);
            await studio.WaitForLocationAsync("!location.search.includes('q=')");
            await WaitForFirstDocumentAsync(page, everyone);

            await GoForwardAsync(page);
            await studio.WaitForLocationAsync("location.search.includes('q=')");
            await WaitForFirstDocumentAsync(page, searched);

            studio.AssertNoPageFetch("searching and paging the documents and moving Back and Forward through it");
            studio.AssertClean("searching and paging the documents and moving Back and Forward through it");
        });
    }

    [BrowserFact]
    public async Task A_document_opens_steps_to_its_neighbour_and_goes_back_to_its_list_without_fetching_a_page()
    {
        await RunAsync("nav-document-detail", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl("documents/customer?size=5"));
            string opened = await FirstDocumentAsync(page);
            await page.Locator(".ms-doc-table tbody tr.ms-doc-row a.ms-doc-id").First.ClickAsync();

            await studio.WaitForLocationAsync("location.pathname.endsWith('/doc')");
            await page.Locator(".ms-doc-detail-page nav.ms-breadcrumb .ms-mono", new PageLocatorOptions { HasTextString = opened }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            // The next document on the list the visitor came from: a link, rendered by the circuit.
            await page.Locator("a[aria-label='Next document']:not(.ms-btn-disabled)").ClickAsync();
            await page.WaitForFunctionAsync(
                "id => { const e = document.querySelector('.ms-doc-detail-page nav.ms-breadcrumb .ms-mono'); return e !== null && e.textContent.trim() !== id; }",
                opened,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            await page.Locator("nav.ms-breadcrumb a").Nth(1).ClickAsync();
            await studio.WaitForLocationAsync("location.pathname.endsWith('/documents/customer')");
            await FirstDocumentAsync(page);

            studio.AssertNoPageFetch("opening a document, stepping to its neighbour and going back to the list");
            studio.AssertClean("opening a document, stepping to its neighbour and going back to the list");
        });
    }

    [BrowserFact]
    public async Task The_events_feed_time_range_writes_the_address_without_fetching_a_page()
    {
        await RunAsync("nav-feed", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl("events/feed"));
            ILocator range = page.Locator("#ms-feed-range");
            await range.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await SettledAsync(page);

            string current = await range.InputValueAsync();
            string[] values = await page.Locator("#ms-feed-range option").EvaluateAllAsync<string[]>("options => options.map(o => o.value)");
            string next = values.First(x => x.Length > 0 && !string.Equals(x, current, StringComparison.Ordinal));

            await range.SelectOptionAsync(next);
            await studio.WaitForLocationAsync("location.search.includes('range=" + Uri.EscapeDataString(next) + "')");
            await SettledAsync(page);

            studio.AssertNoPageFetch("changing the feed's time range");
            studio.AssertClean("changing the feed's time range");
        });
    }

    [BrowserFact]
    public async Task The_streams_filter_writes_the_address_without_fetching_a_page()
    {
        await RunAsync("nav-streams", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl("events/streams"));
            await SettledAsync(page);

            ILocator type = page.Locator("#ms-streams-type");
            await type.FillAsync("Order");
            await type.PressAsync("Enter");

            await studio.WaitForLocationAsync("location.search.includes('type=Order')");
            await SettledAsync(page);

            studio.AssertNoPageFetch("filtering the streams by aggregate type");
            studio.AssertClean("filtering the streams by aggregate type");
        });
    }

    [BrowserFact]
    public async Task The_dead_letters_filter_writes_the_address_without_fetching_a_page()
    {
        await RunAsync("nav-dead-letters", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl("events/dead-letters"));
            ILocator projection = page.Locator("#ms-dl-projection");
            await projection.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await projection.FillAsync("Shipment");
            await projection.PressAsync("Enter");

            await studio.WaitForLocationAsync("location.search.includes('projection=Shipment')");
            await SettledAsync(page);

            studio.AssertNoPageFetch("filtering the dead letters by projection");
            studio.AssertClean("filtering the dead letters by projection");
        });
    }

    [BrowserFact]
    public async Task A_streams_timeline_click_moves_the_time_travel_panel_and_the_stepper_takes_the_next_step_without_fetching_a_page()
    {
        await RunAsync("nav-stream-detail-stepper", "admin", async studio =>
        {
            IPage page = studio.Page;

            // A typed order stream: three events, and an aggregate type to replay them into.
            await studio.GoAsync(fixture.Root.StudioUrl("events/streams/s?id=" + SampleDataSeeder.TypedOrderStreamId(1)));
            await page.Locator("#ms-time-travel-type").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.Locator("#ms-time-travel-type").SelectOptionAsync(new SelectOptionValue { Index = 1 });
            await studio.WaitForLocationAsync("location.search.includes('agg=')");

            // The oldest event on the timeline: it is not the version the panel opens on.
            ILocator oldest = page.Locator(".ms-timeline-item:not(.ms-timeline-item-selected) .ms-event-card-version").Last;
            await oldest.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            string label = (await oldest.InnerTextAsync()).Trim();
            string version = label.TrimStart('v');
            await oldest.ClickAsync();

            await studio.WaitForLocationAsync("location.search.includes('v=" + version + "')");
            await page.Locator(".ms-timeline-item-selected .ms-event-card-version", new PageLocatorOptions { HasTextString = label }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            // The step-through link is a link: the stepper opens in the circuit and Next takes the next step.
            await page.Locator("a.ms-time-travel-step").ClickAsync();
            await studio.WaitForLocationAsync("location.pathname.endsWith('/events/streams/step')");
            ILocator next = page.Locator(".ms-stepper-nav button", new PageLocatorOptions { HasTextString = "Next" });
            await next.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.WaitForFunctionAsync(
                "() => { const b = [...document.querySelectorAll('.ms-stepper-nav button')].find(x => x.textContent.trim() === 'Next'); return b !== undefined && !b.disabled; }",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            string before = await page.Locator(".ms-stepper-position").InnerTextAsync();
            await next.ClickAsync();
            await page.WaitForFunctionAsync(
                "before => { const p = document.querySelector('.ms-stepper-position'); return p !== null && p.textContent.trim() !== before.trim(); }",
                before,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("selecting an event on a stream's timeline and stepping through it");
            studio.AssertClean("selecting an event on a stream's timeline and stepping through it");
        });
    }

    [BrowserFact]
    public async Task Relationships_views_and_schema_chips_write_the_address_without_fetching_a_page()
    {
        await RunAsync("nav-relationships", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl("relationships"));
            await page.Locator("svg.ms-graph-svg").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            // The view that is not on: the address changes with it.
            string before = await page.EvaluateAsync<string>("() => location.search");
            await page.Locator(".ms-graph-views button[aria-pressed='false']").First.ClickAsync();
            await page.WaitForFunctionAsync("before => location.search !== before", before, new PageWaitForFunctionOptions { Timeout = 30_000 });

            // Tables is the view with schema chips.
            await page.Locator(".ms-graph-views button", new PageLocatorOptions { HasTextString = "Tables" }).ClickAsync();
            await studio.WaitForLocationAsync("location.search.includes('view=')");
            ILocator chips = page.Locator(".ms-graph-schema-chip");
            await chips.Nth(1).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await chips.Nth(1).ClickAsync();
            await studio.WaitForLocationAsync("location.search.includes('schema=')");
            await page.Locator(".ms-graph-schema-chip").Nth(1).And(page.Locator("[aria-pressed='true']")).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await page.Locator(".ms-graph-schema-chip").First.ClickAsync();
            await studio.WaitForLocationAsync("!location.search.includes('schema=')");

            studio.AssertNoPageFetch("choosing a relationships view and a schema chip");
            studio.AssertClean("choosing a relationships view and a schema chip");
        });
    }

    [BrowserFact]
    public async Task The_database_browsers_owner_and_name_filters_write_the_address_without_fetching_a_page()
    {
        await RunAsync("nav-database-objects", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl("database"));
            await page.Locator("a.ms-db-name[title='studio_sample.mt_doc_customer']").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await page.Locator(".ms-db-owner-filter button[aria-pressed='false']").First.ClickAsync();
            await studio.WaitForLocationAsync("location.search.includes('owner=')");
            await page.Locator(".ms-db-owner-filter button[aria-pressed='true']").Nth(0).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            ILocator filter = page.Locator(".ms-db-toolbar input.ms-db-name-filter");
            await filter.FillAsync("mt_doc_cust");
            await filter.PressAsync("Enter");
            await studio.WaitForLocationAsync("location.search.includes('q=mt_doc_cust')");
            await page.WaitForFunctionAsync(
                "() => { const names = [...document.querySelectorAll('a.ms-db-name')]; return names.length > 0 && names.every(a => a.title.includes('mt_doc_cust')); }",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("choosing an owner and filtering the database browser by name");
            studio.AssertClean("choosing an owner and filtering the database browser by name");
        });
    }

    /// <remarks>
    /// Between two pages that run no live loop of their own (Streams and Event types): leaving a page that does - the Overview, the Feed
    /// - while its first render is still starting the loop trips <c>StudioLiveUpdates.StartAsync</c> against its <c>DisposeAsync</c> (a
    /// <c>NullReferenceException</c> that ends the circuit). That race is older than interception and is reported with this packet; it
    /// is not what this scenario is about.
    /// </remarks>
    [BrowserFact]
    public async Task A_rail_click_renders_in_the_circuit_changes_the_title_focuses_the_heading_and_Back_and_Forward_land_on_the_right_page()
    {
        await RunAsync("nav-rail-back-forward", "admin", async studio =>
        {
            IPage page = studio.Page;

            // A short viewport, so the streams list scrolls: a move to another page starts at the top.
            await page.SetViewportSizeAsync(StudioPage.ViewportWidth, 400);
            await studio.GoAsync(fixture.Root.StudioUrl("events/streams"));
            await page.Locator("h1.ms-page-title", new PageLocatorOptions { HasTextString = "Streams" }).WaitForAsync();
            await SettledAsync(page);

            string streamsTitle = await page.TitleAsync();

            await page.EvaluateAsync("() => window.scrollTo(0, 200)");
            await page.WaitForFunctionAsync("() => window.scrollY > 0", null, new PageWaitForFunctionOptions { Timeout = 30_000 });

            await Rail(page, "Event types").ClickAsync();
            await studio.WaitForLocationAsync("location.pathname.endsWith('/events/types')");
            await page.Locator("h1.ms-page-title", new PageLocatorOptions { HasTextString = "Event types" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            await page.WaitForFunctionAsync("title => document.title !== title", streamsTitle, new PageWaitForFunctionOptions { Timeout = 30_000 });
            (await page.TitleAsync()).Should().Contain("Event types", "<PageTitle> follows a move the circuit renders");

            await page.WaitForFunctionAsync(
                "() => document.activeElement !== null && document.activeElement.tagName === 'H1'",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });
            await page.WaitForFunctionAsync("() => window.scrollY === 0", null, new PageWaitForFunctionOptions { Timeout = 30_000 });

            await GoBackAsync(page);
            await studio.WaitForLocationAsync("!location.pathname.endsWith('/events/types')");
            await page.Locator("h1.ms-page-title", new PageLocatorOptions { HasTextString = "Streams" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await page.TitleAsync()).Should().Be(streamsTitle);

            await GoForwardAsync(page);
            await studio.WaitForLocationAsync("location.pathname.endsWith('/events/types')");
            await page.Locator("h1.ms-page-title", new PageLocatorOptions { HasTextString = "Event types" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("moving by the rail, Back and Forward");
            studio.AssertClean("moving by the rail, Back and Forward");
        });
    }

    [BrowserFact]
    public async Task Configurations_Database_Connection_link_lands_on_the_Marten_section_once_it_is_drawn()
    {
        await RunAsync("nav-connection-fragment", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl("config"));
            await page.Locator("a", new PageLocatorOptions { HasTextString = "Database › Connection" }).First.ClickAsync();

            await studio.WaitForLocationAsync("location.pathname.endsWith('/database/connection') && location.hash === '#conn-marten'");
            await page.WaitForFunctionAsync(
                "() => { const e = document.getElementById('conn-marten'); if (e === null) { return false; } const top = e.getBoundingClientRect().top; return top >= -2 && top < window.innerHeight; }",
                null,
                new PageWaitForFunctionOptions { Timeout = 30_000 });

            studio.AssertNoPageFetch("following Configuration's link to the Connection page's Marten section");
            studio.AssertClean("following Configuration's link to the Connection page's Marten section");
        });
    }

    [BrowserFact]
    public async Task A_link_inside_the_mount_that_is_not_a_page_is_loaded_by_the_browser_once()
    {
        Assert.SkipWhen(StudioPage.InterceptionIsOff, SkippedWhenOff);

        await RunAsync("nav-force-load-file", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Root.StudioUrl());

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

            string file = fixture.Root.StudioUrl("_content/PostgresStudio/favicon.svg");
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

            await studio.GoAsync(fixture.Root.StudioUrl());

            string missing = fixture.Root.StudioUrl("no-such-page");
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

            await studio.GoAsync(fixture.Root.StudioUrl());

            await page.EvaluateAsync("() => { const a = document.createElement('a'); a.id = 'ms-nav-probe'; a.setAttribute('href', '/'); a.textContent = 'probe'; document.body.appendChild(a); }");
            await page.Locator("#ms-nav-probe").ClickAsync();

            // The sample's landing page, outside the studio's mount.
            await page.Locator("p", new PageLocatorOptions { HasTextString = "Signed in as" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            page.Url.Should().Be(fixture.Root.Url("/"));

            studio.AssertClean("following a link out of the mount");
        });
    }
}
