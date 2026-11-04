using AngleSharp.Dom;

using Bunit;

using MartenStudio.Tests.Support;

using PostgresStudio.Components.Layout;
using PostgresStudio.Components.Pages;
using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Components.Pages.Health;
using PostgresStudio.Components.Pages.Relationships;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

using DetailPage = PostgresStudio.Components.Pages.Database.RowDetail;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// Every core page renders under PostgresStudio's own wiring - its settings, descriptor and source provider over one
/// <c>NpgsqlDataSource</c> - with no MartenStudio type anywhere: the shell, the Overview, Query, the database browser,
/// Sessions, Relationships, Health and Activity.
/// </summary>
public class CorePageSmokeTests
{
    private const string Row =
        "postgres/database/row?schema=quartz&name=qrtz_triggers&key.sched_name=QuartzScheduler&key.trigger_name=trigger-00&key.trigger_group=DEFAULT";

    [Fact]
    public async Task The_scope_is_the_one_data_source_named_default()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        context.State.ActiveScope.Should().Be(new PostgresStudio.Services.StudioScope("default", "default", null));
        context.State.AvailableStores.Should().ContainSingle().Which.DisplayName.Should().Be("Default");
        context.State.AvailableDatabases.Should().ContainSingle().Which.Name.Should().Be("app");
    }

    [Fact]
    public async Task The_rail_ends_with_the_product_its_licence_and_its_author_a_line_each()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var rail = context.Render<NavMenu>();

        var credit = rail.Find(".ms-rail-credit");
        var lines = credit.QuerySelectorAll(".ms-rail-credit-line").Select(static x => x.TextContent.Trim()).ToList();
        lines.Should().HaveCount(3, "the product and its version, the licence and the author, a line each");
        lines[0].Should().StartWith("Postgres Studio ").And.NotContain("MIT").And.NotContain("Marko");
        lines[1].Should().Be("MIT licensed");
        lines[2].Should().Be("by Marko Lahma");
        rail.Find(".ms-sidebar-inner").LastElementChild!.ClassList.Should().Contain("ms-rail-credit", "the credit is the last thing in the rail's column, after the list");
        credit.QuerySelectorAll("a").Select(static a => a.GetAttribute("href")).Should().Contain(
            "https://github.com/lahma/marten-studio/blob/main/LICENSE");
    }

    [Fact]
    public async Task The_shell_starts_with_a_skip_link_to_the_content_region()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, static builder => builder.AddContent(0, "the page")));

        IElement first = layout.FindAll("a[href], button, input, select, textarea, [tabindex]:not([tabindex='-1'])")
            .First(static x => !x.HasAttribute("disabled"));
        first.ClassList.Should().Contain("ms-skip-link");
        first.TextContent.Trim().Should().Be("Skip to content");
        first.GetAttribute("href").Should().Be("#ms-main-content");

        layout.Find("main").Id.Should().Be("ms-main-content");
        layout.Find("main").GetAttribute("tabindex").Should().Be("-1");
    }

    [Fact]
    public async Task The_overview_reads_once_interactive_and_shows_the_server_the_tables_and_the_connections()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var page = context.Render<StudioHome>();

        page.WaitForAssertion(() => page.FindAll(".ms-pg-overview-facts").Should().ContainSingle());
        page.Find(".ms-page-title").TextContent.Should().Be("Overview");
        page.Find(".ms-pg-overview-facts").TextContent.Should().Contain("PostgreSQL 17.4").And.Contain("self-hosted");

        string[] tiles = [.. page.FindAll(".ms-stat-card-title").Select(static x => x.TextContent)];
        tiles.Should().Equal("PostgreSQL", "Databases", "Schemas", "Tables", "Estimated size", "Connections", "Health");

        page.FindAll(".ms-pg-overview-table tbody tr").Should().NotBeEmpty("the fake catalog has tables with estimates");
        page.Find(".ms-pg-overview-connections").TextContent.Should().Contain("idle").And.Contain("2 sessions");

        context.Sessions.SummaryRequests.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Query_is_the_sql_console_alone_with_its_title_and_deep_link()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().WithAllCapabilities().ReadyAsync();
        context.Navigate("postgres/query?sql=" + Uri.EscapeDataString("select 1"));

        var page = context.Render<QueryPage>();

        page.WaitForAssertion(() => page.FindAll("#ms-query-sql").Should().ContainSingle());
        page.Find(".ms-page-title").TextContent.Should().Be("Query");
        page.FindAll(".ms-query-modes").Should().BeEmpty("there is no Marten mode and no LINQ mode");
        page.Find("#ms-query-sql").GetAttribute("value").Should().Be("select 1", "the deep link put the statement into the editor");
    }

    /// <summary>1.2-NAV N6a: the Run writes the scope from the state, even when the address it was reached by had none.</summary>
    [Fact]
    public async Task Query_writes_the_scope_with_the_statement_even_when_the_address_had_none()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().WithAllCapabilities().ReadyAsync();
        context.CurrentUri.Should().NotContain("db=", "the premise: the address names no scope");
        var page = context.Render<QueryPage>();
        page.WaitForAssertion(() => page.FindAll("#ms-query-sql").Should().ContainSingle());

        page.Find("#ms-query-sql").Change("select 1");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());

        page.WaitForAssertion(() => context.CurrentUri.Should().Contain("sql=select%201"));
        (string? source, string? database, _) = PostgresStudio.Components.StudioScopeQuery.Read(
            context.CurrentUri, PostgresStudio.Services.Postgres.PostgresStudioDescriptor.Instance.SourceQueryParameter);
        source.Should().Be(context.State.ActiveScope!.StoreKey);
        database.Should().Be(context.State.ActiveScope!.DatabaseId);
    }

    [Fact]
    public async Task Query_without_RunSql_names_PostgresStudios_own_option()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var page = context.Render<QueryPage>();

        page.Markup.Should().Contain("PostgresStudioOptions.Capabilities.RunSql").And.NotContain("MartenStudioOptions");
    }

    [Fact]
    public async Task The_database_browser_renders()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Navigate("postgres/database");

        var page = context.Render<DatabaseObjects>();

        page.WaitForAssertion(() => page.FindAll(".ms-page-title").Should().NotBeEmpty());
        page.FindAll(".ms-error-alert").Should().BeEmpty();
    }

    [Fact]
    public async Task An_objects_page_renders()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Navigate("postgres/database/object?schema=legacy&name=orders");

        var page = context.Render<ObjectDetail>();

        page.WaitForAssertion(() => page.Find(".ms-db-object-title").TextContent.Should().Contain("orders"));
    }

    [Fact]
    public async Task A_rows_page_renders()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Navigate(Row);

        var page = context.Render<DetailPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-row-kv .ms-row-kv-row").Should().HaveCount(FakeTableRows.Columns.Count));
    }

    /// <summary>
    /// The one option the two studios spell differently: a value cut at the cap names PostgresStudio's own option. (The
    /// same sentence names <c>MartenStudioOptions.MaxInlineDocumentBytes</c> in MartenStudio, pinned by its
    /// <c>RowDetailPageTests</c>.)
    /// </summary>
    [Fact]
    public async Task A_value_cut_at_the_cap_names_MaxInlineValueBytes()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        TableRowDetail detail = FakeTableRows.Detail();
        List<SqlCell> cells = [.. detail.Cells];
        int calendar = FakeTableRows.Columns.ToList().FindIndex(static x => x.Name == "calendar_name");
        cells[calendar] = new SqlCell(new string('b', 400) + "…", SqlCellKind.Text, true, 900_000);
        context.TableRows.Detail = detail with { Cells = cells };
        context.TableRows.Cell = new TableCellValue { Found = true, Text = new string('b', 600), FullLength = 900_000, Cap = 512 * 1024, Truncated = true };
        context.Navigate(Row);

        var page = context.Render<DetailPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-row-kv-row").Should().NotBeEmpty());

        IElement row = page.FindAll(".ms-row-kv-row")[calendar];
        row.QuerySelector("button.ms-row-show-all")!.Click();

        row = page.FindAll(".ms-row-kv-row")[calendar];
        row.QuerySelector(".ms-row-value-note")!.TextContent.Should().Contain("(PostgresStudioOptions.MaxInlineValueBytes)");
    }

    [Fact]
    public async Task Sessions_renders_the_summary()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var page = context.Render<Sessions>();

        page.WaitForAssertion(() => page.FindAll(".ms-sessions-stats").Should().ContainSingle());
        page.Find(".ms-page-title").TextContent.Should().Be("Sessions");
    }

    [Fact]
    public async Task Relationships_renders()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var page = context.Render<Relationships>();

        page.WaitForAssertion(() => page.Find(".ms-page-title").TextContent.Should().Be("Relationships"));
    }

    [Fact]
    public async Task Health_renders()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-page-title").TextContent.Should().Be("Health"));
    }

    [Fact]
    public async Task Activity_renders()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var page = context.Render<Activity>();

        page.WaitForAssertion(() => page.Find(".ms-page-title").TextContent.Should().Be("Activity"));
    }

    [Fact]
    public async Task The_sidebar_is_PostgresStudios_and_names_no_Marten_screen()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var menu = context.Render<NavMenu>();

        string[] links = [.. menu.FindAll(".ms-nav-link-text").Select(static x => x.TextContent)];
        links.Should().Equal(
            "Overview", "Plan", "Tables", "Sessions", "Connection", "Relationships", "Health", "Statements", "Maintenance", "Storage", "Replication", "Server", "Activity");
    }

    /// <summary>
    /// Issue #37: Query is a door to the SQL console, so a host with <c>RunSql</c> off - the default, and most hosts' choice - does
    /// not offer it to its administrators; a visitor who reaches it by address reads the notice. Plan stays (1.0-P3A): pasting a
    /// plan reads nothing and is every visitor's.
    /// </summary>
    [Fact]
    public async Task Query_is_absent_from_the_rail_while_RunSql_is_off_and_Plan_stays()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Options.Capabilities.RunSql.Should().BeFalse();

        var menu = context.Render<NavMenu>();

        menu.FindAll("a.ms-nav-link").Select(static x => x.GetAttribute("href")?.Split('?')[0]).Should().NotContain("query").And.Contain("plan");
        menu.FindAll(".ms-nav-link-text").Select(static x => x.TextContent).Should().NotContain("Query").And.StartWith(["Overview", "Plan", "Tables"]);
    }

    [Fact]
    public async Task Query_and_Plan_are_in_the_rail_with_RunSql_on_in_their_place()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Options.Capabilities.RunSql = true;

        var menu = context.Render<NavMenu>();

        menu.FindAll(".ms-nav-link-text").Select(static x => x.TextContent).Should().StartWith(["Overview", "Query", "Plan", "Tables"]);
        menu.FindAll("a.ms-nav-link").Select(static x => x.GetAttribute("href")?.Split('?')[0]).Should().Contain(["query", "plan"]);
    }

    /// <summary>The master switch turns every capability off, so it takes Query with it whatever <c>RunSql</c> says; Plan stays.</summary>
    [Fact]
    public async Task Query_goes_with_the_master_switch_even_with_RunSql_set_and_Plan_stays()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().WithAllCapabilities().ReadyAsync();
        context.Options.ReadOnly = true;
        context.Options.Capabilities.RunSql.Should().BeTrue();

        var menu = context.Render<NavMenu>();

        menu.FindAll(".ms-nav-link-text").Select(static x => x.TextContent).Should().NotContain("Query").And.Contain(["Plan", "Tables"]);
    }

    /// <summary>
    /// With a second source the source picker is drawn, and it reads "Source", so a host source's database picker beside
    /// it is the only one called "Database".
    /// </summary>
    [Fact]
    public async Task With_a_second_source_the_source_picker_reads_Source()
    {
        await using PostgresStudioComponentContext context = new();
        context.Options.AddDataSource("reporting", _ => context.DataSource);
        await context.ReadyAsync();

        var selector = context.Render<ScopeSelector>();

        selector.Find("label[for=ms-store-select]").TextContent.Should().Be("Source");
        selector.Find("#ms-store-select").GetAttribute("aria-label").Should().Be("Source");
        selector.FindAll("#ms-store-select option").Select(static x => x.TextContent.Trim()).Should().Equal("Default", "reporting");
    }

    [Fact]
    public async Task On_Sessions_only_the_Sessions_entry_is_lit()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Navigate("postgres/database/sessions");

        var menu = context.Render<NavMenu>();

        menu.FindAll("a.ms-nav-active .ms-nav-link-text").Select(static x => x.TextContent).Should().Equal("Sessions");
    }

    [Fact]
    public async Task The_capability_chip_offers_four_reads_and_no_mutating_operation()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var banner = context.Render<CapabilityBanner>();

        banner.Find("button.ms-chip").TextContent.Trim().Should().Be("0 / 4 capabilities");
        banner.FindAll(".ms-popover-title").Select(static x => x.TextContent).Should().Equal("Reads authorized like writes");
        banner.FindAll(".ms-capability-name").Select(static x => x.TextContent)
            .Should().Equal("RunSql", "BrowseDatabase", "ViewDefinitions", "ViewDatabaseActivity");
        banner.FindAll(".ms-capability-option").Select(static x => x.TextContent)
            .Should().OnlyContain(static x => x.StartsWith("PostgresStudioOptions.Capabilities.", StringComparison.Ordinal));
        banner.Markup.Should().Contain("PostgresStudioCapabilities.All()").And.NotContain("Marten");
    }
}
