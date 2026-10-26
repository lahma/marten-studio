using System.Globalization;

using Microsoft.Playwright;

using PostgresStudio.SampleData;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// Postgres Studio in a real browser, over <c>samples/PostgresStudio.Sample</c> on real Kestrel: sign-in, the Overview, the
/// database browser on the admin database and then a tenant database, a row read the gate refuses a viewer, the SQL
/// console, Health - and every core page walked on both kinds of source without a single write.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the sample's policies decide.</b> <c>admin</c> holds every capability's scope; <c>viewer</c> holds
/// <c>studio:read</c> alone, so the write policy - which Postgres Studio asks about each capability by name - refuses it
/// <c>BrowseDatabase</c> and <c>RunSql</c>. The sample names <c>public</c>, <c>quartz</c>, <c>legacy</c> and
/// <c>studio_sample</c> in <c>BrowsableSchemas</c>, so their structure is shown to anyone signed in and their rows only
/// past the gate.
/// </para>
/// <para>
/// Every scenario asserts a clean page - no console error or warning, no uncaught exception, no response of 400 or more
/// (<see cref="StudioPage.AssertClean" />) - because a Blazor Server page that has lost its circuit still renders.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class PostgresStudioScenarioTests(BrowserSuiteFixture fixture)
{
    private const string TriggersObject = "database/object?schema=quartz&name=qrtz_triggers";

    [BrowserFact]
    public async Task An_admin_signs_in_and_the_Overview_shows_the_server_and_the_admin_databases_tables()
    {
        await RunAsync("overview", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl());

            (await page.Locator("h1.ms-page-title").InnerTextAsync()).Trim().Should().Be("Overview");
            (await page.Locator(".ms-sidebar-brand-text").InnerTextAsync()).Trim().Should().Be("Postgres Studio", "PostgresStudioOptions.Title's default");

            ILocator server = page.Locator(".ms-pg-overview-server");
            await server.WaitForAsync();
            await page.Locator(".ms-overview-tiles .ms-stat-card-value").First.WaitForAsync();

            page.Url.Should().Contain("store=default", "the admin database is the application's unkeyed data source, listed first");

            await studio.AssertNoSidewaysScrollAsync("the Overview");
            studio.AssertClean("opening the Overview as admin");
        });
    }

    [BrowserFact]
    public async Task The_database_browser_reads_a_quartz_table_on_the_admin_database_and_then_a_tenant_databases_own()
    {
        await RunAsync("database-browser", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl("database"));

            ILocator triggers = RelationRow(page, "quartz.qrtz_triggers");
            await triggers.WaitForAsync();

            (await SchemasAsync(page)).Should().Contain(["quartz", "legacy", "studio_sample"], "each is named in the sample's BrowsableSchemas");
            (await triggers.Locator(".ms-db-cell-owner .ms-db-hint").InnerTextAsync()).Trim().Should().Be("Quartz.NET");

            await triggers.Locator("a.ms-db-name").ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("name=qrtz_triggers", StringComparison.Ordinal));

            ILocator rows = page.Locator(".ms-row-grid tbody tr.ms-row-item");
            await rows.First.WaitForAsync();
            (await rows.CountAsync()).Should().BePositive("the sample seeds Quartz triggers");

            // A tenant database: the host source's tenant_a, by a pasted link.
            await studio.GoAsync(fixture.Host.StudioUrl("database?store=tenants&db=tenant_a"));

            ILocator customers = RelationRow(page, "public.customers");
            await customers.WaitForAsync();
            (await SchemasAsync(page)).Should().Equal(["public"], "a tenant database holds the tenant's tables and nothing of the admin database's");
            (await page.Locator("a.ms-db-name[title='quartz.qrtz_triggers']").CountAsync()).Should().Be(0);

            await customers.Locator("a.ms-db-name").ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("name=customers", StringComparison.Ordinal) && url.Contains("db=tenant_a", StringComparison.Ordinal));

            await rows.First.WaitForAsync();
            (await rows.CountAsync()).Should().BePositive("tenant_a's customers are read through the host's own data source for it");

            await studio.AssertNoSidewaysScrollAsync("a tenant table's Rows tab");
            studio.AssertClean("browsing the admin database and a tenant database as admin");
        });
    }

    [BrowserFact]
    public async Task A_viewer_is_refused_a_tables_rows_and_told_which_policy_said_no_while_its_structure_stays()
    {
        await RunAsync("database-viewer", "viewer", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl(TriggersObject + "&tab=rows"));

            ILocator card = page.Locator(".ms-db-rows-card[data-refusal='WritePolicy']");
            await card.WaitForAsync();

            string refusal = await card.InnerTextAsync();
            refusal.Should().Contain("PostgresStudioOptions.WriteAuthorizationPolicy", "the card names the exact thing a host would change (D4)");
            refusal.Should().NotContain("MartenStudioOptions", "Postgres Studio's words, not Marten Studio's");
            (await page.Locator(".ms-row-grid").CountAsync()).Should().Be(0, "no row of it is read for this visitor");

            ILocator tabs = page.Locator(".ms-db-object-tabs a");
            await tabs.Filter(new LocatorFilterOptions { HasTextString = "Columns" }).ClickAsync();
            await page.WaitForURLAsync(static url => url.Contains("tab=columns", StringComparison.Ordinal));
            await page.Locator("table.ms-db-columns tbody tr").First.WaitForAsync();

            studio.AssertClean("reading a refused table as a viewer");
        });
    }

    [BrowserFact]
    public async Task The_SQL_console_runs_a_statement_against_a_tenant_database()
    {
        await RunAsync("sql-console", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl("query?store=tenants&db=tenant_b"));

            ILocator editor = page.Locator("#ms-query-sql");
            await editor.WaitForAsync();
            await editor.FillAsync("select count(*) as customers from public.customers");

            // The editor commits on change, which is a blur, so the statement leaves it before the click.
            await editor.BlurAsync();
            await page.Locator(".ms-query-panel-sql .ms-query-run").ClickAsync();

            ILocator cell = page.Locator("table.ms-query-grid tbody tr").First.Locator("td").Last;
            await cell.WaitForAsync();

            (await cell.InnerTextAsync()).Trim().Should().Be(
                TenantDemo.SizeOf("tenant_b").Customers.ToString(CultureInfo.InvariantCulture),
                "the statement ran on tenant_b's own database");

            studio.AssertClean("running SQL against a tenant database");
        });
    }

    [BrowserFact]
    public async Task Health_reports_what_it_checked_on_a_tenant_database()
    {
        await RunAsync("health", "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl("health?store=tenants&db=tenant_c"));

            ILocator checkedRows = page.Locator("table.ms-health-check-table tbody tr.ms-health-check-checked");
            await checkedRows.First.WaitForAsync();
            (await checkedRows.CountAsync()).Should().BeGreaterThan(5, "the core checks ran against tenant_c");

            (await page.Locator("table.ms-health-check-table").InnerTextAsync()).Should().Contain("T03").And.Contain("C01");

            await studio.AssertNoSidewaysScrollAsync("Health");
            studio.AssertClean("opening Health on a tenant database");
        });
    }

    /// <summary>
    /// Hard rule 14's analogue in the browser: every core page, on the admin database and on a tenant database, and the
    /// sample's four databases are exactly as they were before anybody opened the studio.
    /// </summary>
    [BrowserFact]
    public async Task Every_core_page_on_the_admin_database_and_on_a_tenant_database_writes_nothing()
    {
        await RunAsync("first-navigation", "admin", async studio =>
        {
            IPage page = studio.Page;

            // Each scope with a table of its own whose object page and rows are opened too.
            foreach ((string scope, string table) in new[] { ("store=default&db=default", TriggersObject), ("store=tenants&db=tenant_a", "database/object?schema=public&name=customers") })
            {
                foreach ((string path, string ready) in Pages.Append((table + "&tab=rows", ".ms-row-grid tbody tr.ms-row-item")))
                {
                    string url = fixture.Host.StudioUrl(path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + scope);
                    await studio.GoAsync(url);
                    await page.Locator(ready).First.WaitForAsync();
                }
            }

            studio.AssertClean("walking every core page");
        });

        fixture.Before.ShouldBeUnchangedIn(await ServerSnapshot.ReadAsync(fixture.Databases, BrowserSuiteFixture.SnapshotPrefix, TestContext.Current.CancellationToken));
    }

    /// <summary>Every core page, and what says it has read what it reads.</summary>
    private static readonly (string Path, string Ready)[] Pages =
    [
        ("", ".ms-pg-overview-server"),
        ("database", ".ms-db-rail-schemas .ms-db-rail-name"),
        ("database/sessions", ".ms-sessions-stats"),
        ("relationships", ".ms-graph-toolbar, .ms-schema-note, .ms-alert"),
        ("health", "table.ms-health-check-table tbody tr"),
        ("query", "#ms-query-sql"),
        ("activity", "h1.ms-page-title"),
    ];

    private Task RunAsync(string name, string user, Func<StudioPage, Task> body) =>
        BrowserScenario.RunAsync(fixture.Browser, name, fixture.Host, user, body);

    private static async Task<IReadOnlyList<string>> SchemasAsync(IPage page)
    {
        await page.Locator(".ms-db-rail-schemas .ms-db-rail-name").First.WaitForAsync();

        // The rail's first entry is every schema at once; the rest are the schemas.
        return [.. (await page.Locator(".ms-db-rail-schemas .ms-db-rail-name").AllTextContentsAsync())
            .Select(static x => x.Trim())
            .Where(static x => x != "All schemas")];
    }

    /// <summary>The browser grid's row for one relation, by its qualified name.</summary>
    private static ILocator RelationRow(IPage page, string qualified) =>
        page.Locator("table.ms-db-relations tbody tr")
            .Filter(new LocatorFilterOptions { Has = page.Locator("a.ms-db-name[title='" + qualified + "']") });
}
