using Bunit;

using MartenStudio.Components.Pages;
using MartenStudio.Components.Pages.Query;
using MartenStudio.Services.Query;
using MartenStudio.Services.Sources;
using MartenStudio.Tests.Components;

using MartenQueryBody = MartenStudio.Components.Pages.Query.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// PS-1d: the SQL console is a shared panel over a shared service, and the Overview and Query routes are shared
/// pages whose bodies the studio's descriptor names.
/// </summary>
/// <remarks>
/// <see cref="QueryPageTests" /> drives the console as MartenStudio's Query page hosts it, and every one of its SQL
/// tests is unchanged; these are the shapes that page does not show - the panel alone, as a studio with no document
/// types renders it, the state the page keeps across a tab switch, and the two routed shells.
/// </remarks>
public class SqlConsolePanelTests
{
    private static StudioComponentContext CreateContext(FakeQueryService service, bool runSql = true)
    {
        var context = new StudioComponentContext();
        context.WithStores("default");
        context.Options.Capabilities.RunSql = runSql;
        context.Services.AddFallbackServiceProvider(new QueryServices(service));
        return context;
    }

    /// <summary>The fake, as the Query service and as the SQL console.</summary>
    private sealed class QueryServices(FakeQueryService instance) : IServiceProvider
    {
        public object? GetService(Type requested) =>
            requested == typeof(IQueryService) || requested == typeof(ISqlConsoleService) ? instance : null;
    }

    [Fact]
    public void The_panel_alone_runs_a_statement_through_the_console_seam_and_draws_the_grid()
    {
        FakeQueryService service = new() { SqlResult = FakeQueryService.OneRow() };
        using StudioComponentContext context = CreateContext(service);

        var panel = context.Render<SqlConsolePanel>();

        panel.Find("#ms-query-sql").Change("select id, doc, note from x");
        panel.Find(".ms-query-run").Click();

        service.Statements.Should().ContainSingle().Which.Should().Be("select id, doc, note from x");
        panel.TextOfAll(".ms-query-grid-type").Should().Equal("uuid", "jsonb", "text");
        panel.Find(".ms-query-editor-ok").TextContent.Should().Contain("1 rows");
    }

    [Fact]
    public void The_panel_alone_names_the_option_when_the_console_is_off()
    {
        using StudioComponentContext context = CreateContext(new FakeQueryService(), runSql: false);

        var panel = context.Render<SqlConsolePanel>();

        panel.Find(".ms-capability-disabled").TextContent.Should().Contain("MartenStudioOptions.Capabilities.RunSql");
        panel.Find(".ms-query-sql-warning").TextContent.Should().Be(SqlConsolePanel.RunSqlWarning);
        panel.FindAll("#ms-query-sql").Should().BeEmpty();
    }

    /// <summary>
    /// The page keeps the console's state, so leaving the SQL tab for the where clause and coming back finds the
    /// statement and its result where they were - as it did when the console was part of the page.
    /// </summary>
    [Fact]
    public void The_query_page_keeps_the_console_result_across_a_tab_switch()
    {
        FakeQueryService service = new FakeQueryService().WithType("person");
        service.SqlResult = FakeQueryService.OneRow();

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<MartenQueryBody>();
        page.Find("#ms-query-mode-sql").Click();
        page.Find("#ms-query-sql").Change("select 1");
        page.Find(".ms-query-run").Click();
        page.FindAll("table.ms-query-grid").Should().ContainSingle();

        page.Find("#ms-query-mode-marten").Click();
        page.FindAll("table.ms-query-grid").Should().BeEmpty("the where clause tab is on screen");

        page.Find("#ms-query-mode-sql").Click();

        page.FindAll("table.ms-query-grid").Should().ContainSingle("the result survived the tab switch");
        page.Find("#ms-query-sql").GetAttribute("value").Should().Be("select 1");
        service.Statements.Should().ContainSingle("coming back does not run anything again");
    }

    /// <summary>The shared query route renders the descriptor's body - MartenStudio's three-mode page.</summary>
    [Fact]
    public void The_shared_query_page_renders_the_descriptors_query_body()
    {
        using StudioComponentContext context = CreateContext(new FakeQueryService().WithType("person"));

        var page = context.Render<QueryPage>();

        page.FindComponent<MartenQueryBody>().Should().NotBeNull();
        page.Find("#ms-query-mode-marten").GetAttribute("aria-selected").Should().Be("true");
    }

    /// <summary>A deep link still reaches the body: query parameters are supplied to it through the shared page.</summary>
    [Fact]
    public void A_deep_link_reaches_the_body_through_the_shared_query_page()
    {
        using StudioComponentContext context = CreateContext(new FakeQueryService().WithType("person"));
        context.Navigate("marten/query?mode=sql&sql=select+1");

        var page = context.Render<QueryPage>();

        page.Find("#ms-query-mode-sql").GetAttribute("aria-selected").Should().Be("true");
        page.Find("#ms-query-sql").GetAttribute("value").Should().Be("select 1");
    }

    /// <summary>The shared root route renders the descriptor's body - MartenStudio's overview.</summary>
    [Fact]
    public void The_shared_root_page_renders_the_descriptors_overview_body()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore(documentTypes: 7, postgresVersion: "17.2");

        var page = context.Render<StudioHome>();

        page.FindComponent<Overview>().Should().NotBeNull();
        page.StatCardValue("Document types").Should().Be("7");
    }
}
