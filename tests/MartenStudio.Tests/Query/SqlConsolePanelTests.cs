using Bunit;

using MartenStudio.Components.Pages;
using MartenStudio.Services.Query;
using PostgresStudio.Services.Sources;
using MartenStudio.Tests.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

using MartenQueryBody = MartenStudio.Components.Pages.Query.Query;
using PostgresStudio.Components.Pages;
using PostgresStudio.Components.Pages.Query;

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
    /// <remarks>
    /// Every click after the run is awaited and every assertion after one waits for its render: a click that lands
    /// while the renderer is busy is queued rather than run, and <c>Click()</c> returns before it has run (the remarks on
    /// <see cref="QueryPageTests.The_stats_panel_has_a_tab_for_the_sql_the_plan_and_the_messages" />). The console's
    /// ticker no longer renders after a run has ended, which is what used to make the renderer busy here.
    /// </remarks>
    [Fact]
    public async Task The_query_page_keeps_the_console_result_across_a_tab_switch()
    {
        FakeQueryService service = new FakeQueryService().WithType("person");
        service.SqlResult = FakeQueryService.OneRow();

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<MartenQueryBody>();
        await page.Find("#ms-query-mode-sql").ClickAsync(new MouseEventArgs());
        page.WaitForElement("#ms-query-sql");
        await page.Find("#ms-query-sql").ChangeAsync(new ChangeEventArgs { Value = "select 1" });
        await page.Find(".ms-query-run").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll("table.ms-query-grid").Should().ContainSingle());

        await page.Find("#ms-query-mode-marten").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll("table.ms-query-grid").Should().BeEmpty("the where clause tab is on screen"));

        await page.Find("#ms-query-mode-sql").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.FindAll("table.ms-query-grid").Should().ContainSingle("the result survived the tab switch"));
        page.Find("#ms-query-sql").GetAttribute("value").Should().Be("select 1");
        service.Statements.Should().ContainSingle("coming back does not run anything again");
    }

    /// <summary>
    /// The deviation PS-1d declared, held to: leaving the SQL tab while a statement runs cancels it through the token
    /// the service was handed - the console belongs to the tab, as it belonged to the page - and coming back says so.
    /// </summary>
    [Fact]
    public async Task Leaving_the_sql_tab_cancels_a_running_statement_and_coming_back_says_so()
    {
        FakeQueryService service = new FakeQueryService().WithType("person");
        service.Gate = new TaskCompletionSource();

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<MartenQueryBody>();
        await page.Find("#ms-query-mode-sql").ClickAsync(new MouseEventArgs());
        page.WaitForElement("#ms-query-sql");

        var editor = page.FindComponent<QueryEditor>();
        Task running = page.InvokeAsync(() => editor.Instance.RunAsync("select pg_sleep(60)"));
        page.WaitForAssertion(() => page.Find(".ms-query-running").Should().NotBeNull());

        await page.Find("#ms-query-mode-marten").ClickAsync(new MouseEventArgs());
        await running;

        service.Cancelled.Should().BeTrue("leaving the tab cancels the token the console handed the service");
        service.Statements.Should().ContainSingle();

        await page.Find("#ms-query-mode-sql").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-query-editor-ok").TextContent.Should().Contain("Cancelled."));
        page.FindAll(".ms-query-running").Should().BeEmpty("nothing is running any more");
        page.Find("#ms-query-sql").GetAttribute("value").Should().Be("select pg_sleep(60)");
    }

    /// <summary>Esc cancels a running statement through the token the service was handed, as it does a where clause.</summary>
    [Fact]
    public async Task Esc_cancels_a_running_statement_through_a_real_cancellation_token()
    {
        FakeQueryService service = new() { Gate = new TaskCompletionSource() };
        using StudioComponentContext context = CreateContext(service);

        var panel = context.Render<SqlConsolePanel>();
        var editor = panel.FindComponent<QueryEditor>();

        Task running = panel.InvokeAsync(() => editor.Instance.RunAsync("select pg_sleep(60)"));
        panel.WaitForAssertion(() => panel.Find(".ms-query-running").Should().NotBeNull());

        await panel.InvokeAsync(() => editor.Instance.CancelAsync());
        await running;

        service.Cancelled.Should().BeTrue("the token the service was handed is the one Esc cancels");
        panel.WaitForAssertion(() => panel.Find(".ms-query-editor-ok").TextContent.Should().Contain("Cancelled."));
        panel.FindAll(".ms-query-running").Should().BeEmpty();
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
