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
        links.Should().Equal("Overview", "Query", "Tables", "Sessions", "Relationships", "Health", "Replication", "Activity");
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
    public async Task The_capability_chip_offers_three_reads_and_no_mutating_operation()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var banner = context.Render<CapabilityBanner>();

        banner.Find("button.ms-chip").TextContent.Trim().Should().Be("0 / 3 capabilities");
        banner.FindAll(".ms-popover-title").Select(static x => x.TextContent).Should().Equal("Reads authorized like writes");
        banner.FindAll(".ms-capability-name").Select(static x => x.TextContent).Should().Equal("RunSql", "BrowseDatabase", "ViewDatabaseActivity");
        banner.FindAll(".ms-capability-option").Select(static x => x.TextContent)
            .Should().OnlyContain(static x => x.StartsWith("PostgresStudioOptions.Capabilities.", StringComparison.Ordinal));
        banner.Markup.Should().Contain("PostgresStudioCapabilities.All()").And.NotContain("Marten");
    }
}
