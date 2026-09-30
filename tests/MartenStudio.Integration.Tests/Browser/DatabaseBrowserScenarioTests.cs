using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// The database browser in a real browser: a Quartz.NET table read and its keys followed, the gate said
/// rather than hidden, a sub-path mount that keeps every link under itself, and Marten's own tables never
/// read raw.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these four.</b> The service and the components are proved without a browser elsewhere, against
/// a live Postgres and in bUnit. What neither can show is the screen as a person walks it: a list whose
/// links lead somewhere under the mount path, a filter typed into a box that reaches the server over the
/// circuit and comes back as a chip, a key cell that opens a row whose references resolve, and a page that
/// draws all of that without growing a sideways scrollbar or writing to the console.
/// </para>
/// <para>
/// <b>Against the sample's own demo schemas.</b> <c>quartz</c> is Quartz.NET's real job store and
/// <c>legacy</c> a hand-made one, both in the sample's <c>BrowsableSchemas</c>; <c>studio_sample</c> is the
/// Marten store's own schema, with one ordinary table (<c>app_settings</c>) the host keeps beside its
/// documents. <see cref="SampleHost.WaitForSeedAsync" /> waits for <c>quartz.qrtz_triggers</c> to have rows,
/// so no scenario here meets them half made.
/// </para>
/// <para>
/// <b>What the sample's policies decide.</b> <c>admin</c> and <c>ops</c> carry <c>studio:write</c>, which is
/// what <c>MartenStudioOptions.WriteAuthorizationPolicy</c> asks for, and <c>BrowseDatabase</c> is asked of
/// that policy (D27); <c>viewer</c> carries <c>studio:read</c> alone, so the policy refuses it - and a
/// refused visitor sees the structure of the store's own schemas and nothing of any other.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class DatabaseBrowserScenarioTests(BrowserSuiteFixture fixture)
{
    /// <summary>The Quartz.NET table every scenario here reads: composite keys, ticks, and five keys in and out.</summary>
    private const string QuartzTriggers = "quartz.qrtz_triggers";

    /// <summary>A filter most of the seeded Quartz triggers match, and a few - paused, blocked, in error - do not.</summary>
    private const string WaitingFilter = "trigger_state = WAITING";

    /// <summary>The value in <see cref="WaitingFilter" />, which is a parameter and must never be in the SQL text.</summary>
    private const string WaitingValue = "WAITING";

    /// <summary>The object page of <see cref="QuartzTriggers" />.</summary>
    private const string TriggersObject = "database/object?schema=quartz&name=qrtz_triggers";

    // ------------------------------------------------------------------------------------------------
    // 1. A Quartz.NET table, read and followed
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// As <c>admin</c>: the browser lists the <c>quartz</c> schema and says whose each table is,
    /// <c>qrtz_triggers</c>' rows render with NULLs and tick timestamps read as dates, a filter reaches the
    /// server as a parameter, a key cell opens the row with its reference to <c>qrtz_job_details</c>
    /// resolved, and the object's Relationships tab draws exactly the edges its table lists.
    /// </summary>
    [BrowserFact]
    public async Task The_database_browser_reads_a_quartz_table_and_follows_its_keys()
    {
        SampleHost host = fixture.Root;

        await BrowserScenario.RunAsync(fixture.Browser, "database-browser", host, "admin", async studio =>
        {
            IPage page = studio.Page;

            // The browser, over every schema this visitor may see.
            await studio.GoAsync(host.StudioUrl("database"));

            ILocator triggersRow = RelationRow(page, QuartzTriggers);
            await triggersRow.WaitForAsync();

            IReadOnlyList<string> schemas = await page.Locator(".ms-db-rail-schemas .ms-db-rail-name").AllTextContentsAsync();
            schemas.Select(static x => x.Trim()).Should().Contain(
                "quartz", "quartz is in the sample's BrowsableSchemas and admin passes the write policy BrowseDatabase is asked of");

            (await triggersRow.Locator(".ms-db-cell-owner .ms-db-hint").InnerTextAsync()).Trim().Should().Be(
                "Quartz.NET",
                "a qrtz_ table in a schema Marten does not own is recognised by name, and said to be a guess");

            // Marten's own document tables are listed quieter, and their owner badge is a link to where the
            // rows are really read - never a raw read here.
            ILocator documentBadge = page.Locator("table.ms-db-relations tbody tr.ms-db-row-marten a.ms-db-owner-document").First;
            await documentBadge.WaitForAsync();
            (await documentBadge.GetAttributeAsync("href")).Should().Contain(
                "documents/", "a document table's badge links to its collection in Documents");

            await studio.AssertNoSidewaysScrollAsync("the database browser");

            // qrtz_triggers. Its Rows tab is the default, because this visitor may read them.
            await triggersRow.Locator("a.ms-db-name").ClickAsync();
            await page.WaitForURLAsync(static url =>
                url.Contains("/database/object", StringComparison.Ordinal)
                && url.Contains("name=qrtz_triggers", StringComparison.Ordinal));

            ILocator rows = page.Locator(".ms-row-grid tbody tr.ms-row-item");
            await rows.First.WaitForAsync();
            (await rows.CountAsync()).Should().BeGreaterThan(0, "the sample seeds Quartz triggers");

            (await page.Locator(".ms-row-grid tbody .ms-query-null").CountAsync()).Should().BeGreaterThan(
                0, "a trigger with no description or no end time has NULL cells, drawn as NULL rather than as empty text");

            ILocator nextFireTime = page.Locator(".ms-row-grid thead th")
                .Filter(new LocatorFilterOptions { Has = page.Locator(".ms-row-col-name", new PageLocatorOptions { HasTextString = "next_fire_time" }) });
            (await nextFireTime.Locator(".ms-row-date-toggle").CountAsync()).Should().Be(
                1, "next_fire_time is a bigint of .NET ticks whose name says it is a time, so its header offers the date hint");

            ILocator hints = page.Locator(".ms-row-grid tbody .ms-row-date-hint");
            (await hints.CountAsync()).Should().BeGreaterThan(0, "the ticks are read as dates, under the number");
            (await hints.First.InnerTextAsync()).Should().StartWith("≈", "the hint says it is a reading, not the value");

            await studio.AssertNoSidewaysScrollAsync("qrtz_triggers' Rows tab, thirty columns wide");

            (await ColumnTextsAsync(page, "trigger_state")).Should().Contain(
                x => !string.Equals(x, WaitingValue, StringComparison.Ordinal),
                "the demo seeds paused, blocked and failed triggers too, so the unfiltered page is not all waiting");

            // A filter, typed into the box and sent over the circuit. The box commits on change, which is a
            // blur, so the value leaves it before the submit rather than as a side effect of the click.
            await page.FillAsync("#ms-row-filter-input", WaitingFilter);
            await page.Locator("#ms-row-filter-input").BlurAsync();
            await page.Locator(".ms-row-filter button[type=submit]").ClickAsync();

            // The chip is drawn from the page the filter read, in the same render that clears the busy state,
            // so once it is there the grid below it is the filtered one.
            ILocator chips = page.Locator(".ms-row-filter-strip .ms-row-chip");
            await chips.First.WaitForAsync();
            await page.Locator(".ms-row-tab[data-state='loaded'][aria-busy='false']").WaitForAsync();
            page.Url.Should().Contain("q=", "the filter is in the address bar, so a pasted link reopens it (D9)");

            (await chips.CountAsync()).Should().Be(1, "one term, one chip");
            (await chips.First.Locator(".ms-row-chip-text").InnerTextAsync()).Should().Contain(
                "trigger_state", "the chip is the term the studio understood");

            await rows.First.WaitForAsync();
            IReadOnlyList<string> states = await ColumnTextsAsync(page, "trigger_state");
            states.Should().NotBeEmpty().And.OnlyContain(
                static x => x == WaitingValue, "the filter reached the server: every row it returned is waiting");

            // Show SQL: the statement that ran, naming the relation through the quoting builder, with the
            // value as a parameter - a filter value is somebody's data, and the statement goes into tickets.
            ILocator disclosure = page.Locator(".ms-row-filter-strip details.ms-sql-disclosure");
            await disclosure.Locator("summary").ClickAsync();
            await disclosure.Locator("pre").WaitForAsync();

            string sql = await disclosure.Locator("pre").TextContentAsync() ?? string.Empty;
            sql.Should().Contain("\"quartz\".\"qrtz_triggers\"", "the relation is quoted by the builder, schema and all");
            sql.Should().Contain("\"trigger_state\"", "the filtered column is quoted by the builder too");
            sql.Should().NotContain(WaitingValue, "a filter value is a parameter, never text in the statement");

            // A key cell opens the row, and its references resolve.
            await page.Locator(".ms-row-grid tbody a.ms-row-open-link").First.ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("/database/row", StringComparison.Ordinal));

            ILocator references = page.Locator(".ms-row-detail-references section.ms-row-refs[aria-label='References']");
            await references.WaitForAsync();

            ILocator jobDetails = references.Locator("li.ms-row-ref")
                .Filter(new LocatorFilterOptions { Has = page.Locator(".ms-graph-table-chip-name", new PageLocatorOptions { HasTextString = "qrtz_job_details" }) });
            (await jobDetails.CountAsync()).Should().Be(1, "qrtz_triggers has one foreign key, to the job the trigger fires");
            (await jobDetails.GetAttributeAsync("class")).Should().Contain(
                "ms-row-ref-present", "every seeded trigger's job exists, and Quartz.NET's key is a validated one");
            (await jobDetails.Locator(".ms-badge", new LocatorLocatorOptions { HasTextString = "missing" }).CountAsync()).Should().Be(
                0, "a present parent is linked, never badged missing");
            (await references.Locator(".ms-row-ref-missing").CountAsync()).Should().Be(0);
            (await jobDetails.Locator("a.ms-row-ref-key").CountAsync()).Should().Be(
                1, "the key references the parent's row key, so it links to the parent row itself");

            await studio.AssertNoSidewaysScrollAsync("a qrtz_triggers row's detail page");

            // Back on the object page, the Relationships tab: its mini-graph and its table agree.
            await page.Locator(".ms-row-detail-actions a", new PageLocatorOptions { HasTextString = "Back to the rows" }).ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("/database/object", StringComparison.Ordinal));
            await rows.First.WaitForAsync();

            await page.Locator(".ms-db-object-tabs a", new PageLocatorOptions { HasTextString = "Relationships" }).ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("tab=relationships", StringComparison.Ordinal));
            await page.Locator(".ms-neighbourhood svg.ms-graph-svg").WaitForAsync();

            // Scoped to the diagram's own svg: the legend beside it draws sample arrows with the same classes.
            await BrowserScenario.AssertTheTableListsTheDiagramAsync(
                page.Locator(".ms-neighbourhood svg.ms-graph-svg .ms-graph-edge"),
                page.Locator(".ms-neighbourhood .ms-graph-table tbody tr.ms-graph-row"),
                "qrtz_triggers' Relationships tab");

            await studio.AssertNoSidewaysScrollAsync("qrtz_triggers' Relationships tab");

            studio.AssertClean("reading quartz.qrtz_triggers, filtering it, opening a row and its relationships");
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 2. The gate is said, not hidden
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// As <c>viewer</c>, whom the sample's write policy refuses <c>BrowseDatabase</c>: the browser says which
    /// policy shut the gate, <c>quartz.qrtz_triggers</c> is refused with the same sentence rather than drawn,
    /// and a non-Marten table in the store's own schema keeps its structure tabs while its Rows tab names the
    /// policy - with nothing on any of the pages erroring.
    /// </summary>
    /// <remarks>
    /// The gate is closed for the whole database, so <c>quartz</c> is not a schema this visitor is shown at
    /// all: its object page is the neutral panel, with the policy's sentence in it, and there are no tabs to
    /// render. The structure that <em>does</em> stay visible is the store's own schemas' (D27), which is why
    /// the Rows-refused-but-structure-shown half is asserted on <c>studio_sample.app_settings</c>.
    /// </remarks>
    [BrowserFact]
    public async Task A_viewer_is_told_which_gate_keeps_the_rows_closed()
    {
        SampleHost host = fixture.Root;

        await BrowserScenario.RunAsync(fixture.Browser, "database-viewer", host, "viewer", async studio =>
        {
            IPage page = studio.Page;

            // The browser: the notice names the policy, and quartz is a count rather than a name.
            await studio.GoAsync(host.StudioUrl("database"));

            ILocator notice = page.Locator(".ms-db-access[data-refusal='WritePolicy']");
            await notice.WaitForAsync();
            (await notice.InnerTextAsync()).Should().Contain(
                "MartenStudioOptions.WriteAuthorizationPolicy", "a refusal names the exact thing a host would change (D4)");

            await page.Locator(".ms-db-rail-schemas .ms-db-rail-name").First.WaitForAsync();
            IReadOnlyList<string> schemas = await page.Locator(".ms-db-rail-schemas .ms-db-rail-name").AllTextContentsAsync();
            schemas.Select(static x => x.Trim()).Should().NotContain("quartz", "a schema outside the gate is never named");

            (await page.Locator(".ms-db-rail-withheld").InnerTextAsync()).Should().Contain(
                "your account may not browse them", "the rail counts what it does not show, and says why");

            // qrtz_triggers' Rows tab, by a pasted link: the neutral panel, with the policy's own sentence.
            await studio.GoAsync(host.StudioUrl(TriggersObject + "&tab=rows"));

            ILocator panel = page.Locator(".ms-db-not-found");
            await panel.WaitForAsync();
            (await panel.InnerTextAsync()).Should().Contain(
                "MartenStudioOptions.WriteAuthorizationPolicy", "the page says which gate is shut rather than only that it is");
            (await page.Locator(".ms-db-object-tabs").CountAsync()).Should().Be(
                0, "nothing of a schema this visitor is not shown is drawn, not even its structure");
            (await page.Locator(".ms-row-grid").CountAsync()).Should().Be(0);

            await studio.AssertNoSidewaysScrollAsync("the refused qrtz_triggers page");

            // An ordinary table in the store's own schema: its structure is there, its rows are refused, and
            // the refusal names the policy.
            await studio.GoAsync(host.StudioUrl("database/object?schema=studio_sample&name=app_settings&tab=rows"));

            ILocator card = page.Locator(".ms-db-rows-card[data-refusal='WritePolicy']");
            await card.WaitForAsync();

            string refusal = await card.InnerTextAsync();
            refusal.Should().Contain("Your account may not read rows here");
            refusal.Should().Contain("MartenStudioOptions.WriteAuthorizationPolicy", "the card names the policy that said no");
            (await page.Locator(".ms-row-grid").CountAsync()).Should().Be(0, "no row of it is read for this visitor");

            ILocator tabs = page.Locator(".ms-db-object-tabs a");
            (await tabs.CountAsync()).Should().Be(6, "every structure tab is still offered beside the refused Rows tab");

            await tabs.Filter(new LocatorFilterOptions { HasTextString = "Columns" }).ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("tab=columns", StringComparison.Ordinal));

            ILocator columns = page.Locator("table.ms-db-columns tbody tr");
            await columns.First.WaitForAsync();
            (await columns.CountAsync()).Should().Be(5, "app_settings has five columns, and its structure is the store's own");

            await tabs.Filter(new LocatorFilterOptions { HasTextString = "Keys" }).ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("tab=keys", StringComparison.Ordinal));
            await page.Locator("table.ms-db-constraints tbody tr").First.WaitForAsync();

            await studio.AssertNoSidewaysScrollAsync("app_settings' structure tabs");

            studio.AssertClean("browsing the database as a viewer the write policy refuses");
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 3. The sub-path mount
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// On the host mounted at <c>/ops/marten</c>, the browser, an object page, a row-detail link and the
    /// relationship links all resolve under the mount path, and every one of them answers.
    /// </summary>
    /// <remarks>
    /// Every link the database browser draws is relative to the studio-rooted <c>&lt;base href&gt;</c>
    /// (D11), so a link built from a path rather than through the link builder would resolve to the
    /// application root, and would 404 only here. Each is asserted resolved and then followed; "no 404" is
    /// <see cref="StudioPage.AssertClean" />, which fails on any response of 400 or more.
    /// </remarks>
    [BrowserFact]
    public async Task A_sub_path_mount_keeps_every_database_link_under_its_path()
    {
        SampleHost host = fixture.SubPathHost;
        string root = host.Url(BrowserSuiteFixture.SubPath + "/");

        await BrowserScenario.RunAsync(fixture.Browser, "database-sub-path", host, "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(host.StudioUrl("database?schema=quartz"));

            ILocator triggers = page.Locator("table.ms-db-relations tbody a.ms-db-name[title='" + QuartzTriggers + "']");
            await triggers.WaitForAsync();

            (await StudioPage.ResolvedHrefAsync(triggers)).Should().StartWith(
                root + "database/object?", "an object link resolves under the mount path");

            IReadOnlyList<ILocator> kindTabs = await page.Locator(".ms-db-kind-tabs a").AllAsync();
            kindTabs.Should().NotBeEmpty("the browser offers its kinds as tabs");

            foreach (ILocator tab in kindTabs)
            {
                (await StudioPage.ResolvedHrefAsync(tab)).Should().StartWith(root + "database?", "a kind tab stays on the mounted browser");
            }

            // The object page.
            await triggers.ClickAsync();
            await page.WaitForURLAsync(url => url.StartsWith(root + "database/object?", StringComparison.Ordinal));

            ILocator keyLink = page.Locator(".ms-row-grid tbody a.ms-row-open-link").First;
            await keyLink.WaitForAsync();
            (await StudioPage.ResolvedHrefAsync(keyLink)).Should().StartWith(
                root + "database/row?", "a row-detail link resolves under the mount path");

            // The row detail it leads to.
            await keyLink.ClickAsync();
            await page.WaitForURLAsync(url => url.StartsWith(root + "database/row?", StringComparison.Ordinal));
            await page.Locator(".ms-row-detail-references section.ms-row-refs[aria-label='References']").WaitForAsync();

            ILocator parent = page.Locator(".ms-row-detail-references a.ms-row-ref-table").First;
            (await StudioPage.ResolvedHrefAsync(parent)).Should().StartWith(root + "database/object?");

            // The object page's Relationships tab: the tab link, and the diagram's node links.
            await page.Locator(".ms-row-detail-actions a", new PageLocatorOptions { HasTextString = "Back to the rows" }).ClickAsync();
            await page.WaitForURLAsync(url => url.StartsWith(root + "database/object?", StringComparison.Ordinal));

            ILocator relationshipsTab = page.Locator(".ms-db-object-tabs a", new PageLocatorOptions { HasTextString = "Relationships" });
            (await StudioPage.ResolvedHrefAsync(relationshipsTab)).Should().StartWith(root + "database/object?");

            await relationshipsTab.ClickAsync();
            await page.WaitForURLAsync(url => url.StartsWith(root, StringComparison.Ordinal)
                && url.Contains("tab=relationships", StringComparison.Ordinal));

            ILocator nodes = page.Locator(".ms-neighbourhood svg.ms-graph-svg a.ms-graph-node");
            await nodes.First.WaitForAsync();
            foreach (ILocator node in await nodes.AllAsync())
            {
                (await StudioPage.ResolvedHrefAsync(node)).Should().StartWith(root, "a diagram node's link resolves under the mount path");
            }

            // The Relationships screen, from the navigation.
            ILocator navigation = page.Locator("a.ms-nav-link", new PageLocatorOptions { HasTextString = "Relationships" });
            (await StudioPage.ResolvedHrefAsync(navigation)).Should().StartWith(root + "relationships");

            await navigation.ClickAsync();
            await page.WaitForURLAsync(url => url.StartsWith(root + "relationships", StringComparison.Ordinal));
            await page.Locator("svg.ms-graph-svg").WaitForAsync();

            ILocator tableNode = page.Locator("svg.ms-graph-svg a.ms-graph-node-table").First;
            await tableNode.WaitForAsync();
            (await StudioPage.ResolvedHrefAsync(tableNode)).Should().StartWith(
                root + "database/object?", "a table node on the Relationships screen opens its object page under the mount path");

            studio.WebSockets.Should().Contain(
                x => x.Contains(BrowserSuiteFixture.SubPath + "/_blazor", StringComparison.Ordinal),
                "the circuit is the mounted one throughout");

            studio.AssertClean("walking the database browser mounted at " + BrowserSuiteFixture.SubPath);
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 4. Marten's tables are not read raw
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>studio_sample.mt_doc_customer</c>'s object page opens on its structure, and its Rows tab is the
    /// "Browsed in Documents" card with a link to the collection - never a grid of raw rows, which would show
    /// every tenant's and the soft-deleted ones as if they were live.
    /// </summary>
    [BrowserFact]
    public async Task A_marten_document_table_is_browsed_in_documents_and_never_read_raw()
    {
        SampleHost host = fixture.Root;

        await BrowserScenario.RunAsync(fixture.Browser, "database-marten-table", host, "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(host.StudioUrl("database/object?schema=studio_sample&name=mt_doc_customer"));

            ILocator active = page.Locator(".ms-db-object-tabs a[aria-current='page']");
            await active.WaitForAsync();
            (await active.InnerTextAsync()).Trim().Should().StartWith(
                "Columns", "a relation whose rows are not read here opens on its structure, never on a refusal");

            await page.Locator(".ms-db-object-tabs a", new PageLocatorOptions { HasTextString = "Rows" }).ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("tab=rows", StringComparison.Ordinal));

            ILocator card = page.Locator(".ms-db-rows-card[data-refusal='MartenOwned']");
            await card.WaitForAsync();
            (await card.Locator(".ms-db-rows-card-title").InnerTextAsync()).Should().StartWith("Browsed in Documents");

            ILocator open = card.Locator("a.ms-btn-primary");
            (await open.GetAttributeAsync("href")).Should().Contain(
                "documents/customer", "the card links to the collection, where tenancy, soft delete and the serializer apply");

            (await page.Locator(".ms-row-grid").CountAsync()).Should().Be(0, "a Marten document table is never read raw");
            (await page.Locator(".ms-db-rows-slot").CountAsync()).Should().Be(0, "the row grid's slot is not even rendered");

            await studio.AssertNoSidewaysScrollAsync("mt_doc_customer's Rows tab");

            // And the link goes where it says.
            await open.ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("/documents/customer", StringComparison.Ordinal));
            await page.Locator(".ms-doc-table tbody tr.ms-doc-row").First.WaitForAsync();

            studio.AssertClean("opening a Marten document table in the database browser");
        });
    }

    /// <summary>
    /// Every cell of one column of the row grid, as text, top to bottom - found by the column's header, so
    /// the column chooser or the key-first order moving it does not move the answer.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ColumnTextsAsync(IPage page, string column)
    {
        string[]? texts = await page.EvaluateAsync<string[]?>(
            """
            column => {
                const heads = [...document.querySelectorAll('.ms-row-grid thead th')];
                const index = heads.findIndex(th => th.querySelector('.ms-row-col-name')?.textContent.trim() === column);
                if (index < 0) {
                    return null;
                }
                return [...document.querySelectorAll('.ms-row-grid tbody tr.ms-row-item')]
                    .map(tr => (tr.children[index]?.textContent ?? '').trim());
            }
            """,
            column);

        texts.Should().NotBeNull("the row grid has a " + column + " column");

        return texts!;
    }

    /// <summary>The browser grid's row for one relation, by its qualified name.</summary>
    private static ILocator RelationRow(IPage page, string qualified) =>
        page.Locator("table.ms-db-relations tbody tr")
            .Filter(new LocatorFilterOptions { Has = page.Locator("a.ms-db-name[title='" + qualified + "']") });
}
