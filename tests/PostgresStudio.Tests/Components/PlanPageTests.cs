using System.Text.Json;

using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Query;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// Query › Plan (X3, issue #28) as the visitor meets it: the console's own disabled state without <c>RunSql</c>; the editor, the
/// modes, the options and the values; a run drawn as the tree and the node table with one selected node between them, sorted,
/// heat-coloured and with its critical path marked; a pasted plan drawn with no database; the address; and the exports.
/// </summary>
public class PlanPageTests
{
    private const string Join = "analyzed-join";

    private static async Task<PostgresStudioComponentContext> ReadyAsync(bool runSql = true)
    {
        PostgresStudioComponentContext context = new();

        if (runSql)
        {
            context.WithAllCapabilities();
        }

        return await context.ReadyAsync();
    }

    private static PlanRunResult Ran(string fixture = Join, int major = 17, IReadOnlyList<string>? types = null) => new()
    {
        PlanJson = PlanFixtures.Get(fixture),
        Duration = TimeSpan.FromMilliseconds(12),
        Options = "analyze, buffers, format json",
        ServerMajor = major,
        ParameterTypes = types ?? [],
    };

    private static async Task<IRenderedComponent<PlanPage>> RanAsync(PostgresStudioComponentContext context, string statement = "select o.id from orders o", PlanRunResult? result = null)
    {
        context.SqlConsole.NextPlan = result ?? Ran();

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change(statement);
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());
        page.WaitForAssertion(() => page.FindAll(".ms-plan-tree").Should().ContainSingle());

        return page;
    }

    // ---- The gate ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_RunSql_the_page_shows_the_consoles_disabled_state_naming_the_property_and_nothing_to_plan_with()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync(runSql: false);

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        page.Find(".ms-capability-disabled").TextContent.Should().Contain("Planning a statement is not enabled").And.Contain("PostgresStudioOptions.Capabilities.RunSql");
        page.Find(".ms-query-sql-warning").TextContent.Should().Be(SqlConsolePanel.RunSqlWarning);
        page.FindAll("#ms-plan-sql, .ms-plan-paste, .ms-plan-modes, .ms-plan-options").Should().BeEmpty("a pasted plan is behind the same gate");
        context.SqlConsole.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadOnly_says_so_in_the_disabled_state()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.ReadOnly = true;
        await context.ReadyAsync();

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        page.Find(".ms-capability-disabled").TextContent.Should().Contain("ReadOnly");
        page.FindAll("#ms-plan-sql").Should().BeEmpty();
    }

    [Fact]
    public async Task A_write_policy_that_refuses_the_visitor_says_so_beside_the_editor_and_disables_it()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = "write-policy";
        await context.ReadyAsync();
        context.AuthorizationService.Allow(static resource => resource.Capability is null);

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        page.WaitForAssertion(() => page.Find(".ms-write-refusal").TextContent.Should().NotBeEmpty());
        page.Find(".ms-query-run").HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-plan-paste-draw").HasAttribute("disabled").Should().BeTrue("a pasted plan is behind the same gate");
    }

    // ---- The editor, the modes and the options -------------------------------------------------------------------------

    [Fact]
    public async Task The_page_offers_the_editor_three_modes_seven_options_and_a_paste_box_and_runs_nothing_on_its_own()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        page.Find(".ms-page-title").TextContent.Should().Be("Plan");
        page.FindAll(".ms-plan-mode").Select(static x => x.TextContent.Trim()).Should().Equal("Explain", "Explain analyze", "Generic plan");
        page.FindAll(".ms-plan-option").Select(static x => x.TextContent.Trim()).Should().Equal("BUFFERS", "TIMING", "VERBOSE", "SETTINGS", "WAL", "MEMORY", "SERIALIZE");
        page.Find(".ms-query-run").TextContent.Trim().Should().Be("Explain");
        page.FindAll("#ms-plan-paste").Should().ContainSingle();
        page.FindAll(".ms-query-editor-hint kbd").Select(static x => x.TextContent).Should().Equal("Ctrl", "Enter", "Esc");
        page.FindAll(".ms-btn").Should().NotContain(static x => x.TextContent.Contains("Save", StringComparison.Ordinal), "the Plan page has no saved queries");
        context.SqlConsole.Plans.Should().BeEmpty();
    }

    [Fact]
    public async Task The_options_that_count_what_a_statement_does_need_Explain_analyze_and_say_why()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        foreach (string option in new[] { "buffers", "timing", "wal", "serialize" })
        {
            IElement box = page.Find("#ms-plan-option-" + option);
            box.HasAttribute("disabled").Should().BeTrue("{0} needs ANALYZE", option);
            box.ParentElement!.GetAttribute("title").Should().Contain("needs Explain analyze");
            box.HasAttribute("checked").Should().BeFalse("a disabled option is never drawn on");
        }

        foreach (string option in new[] { "verbose", "settings", "memory" })
        {
            page.Find("#ms-plan-option-" + option).HasAttribute("disabled").Should().BeFalse();
        }

        page.FindAll("input[name=ms-plan-mode]")[1].Change(true);

        page.Find("#ms-plan-option-buffers").HasAttribute("disabled").Should().BeFalse();
        page.Find("#ms-plan-option-buffers").HasAttribute("checked").Should().BeTrue("BUFFERS is on by default");
        page.Find("#ms-plan-option-timing").HasAttribute("checked").Should().BeTrue("and so is TIMING");
        page.Find("#ms-plan-option-wal").HasAttribute("checked").Should().BeFalse();
        page.Find(".ms-query-run").TextContent.Trim().Should().Be("Explain analyze");
    }

    [Fact]
    public async Task A_generic_plan_needs_a_statement_with_placeholders()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        page.FindAll("input[name=ms-plan-mode]")[2].HasAttribute("disabled").Should().BeTrue();
        page.FindAll(".ms-plan-mode")[2].GetAttribute("title").Should().Contain("$1, $2 placeholders");

        page.Find("#ms-plan-sql").Change("select * from t where id = $1");

        page.FindAll("input[name=ms-plan-mode]")[2].HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task A_statement_with_placeholders_gets_a_box_for_each_with_a_NULL_choice_and_the_values_go_to_the_console()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.NextPlan = Ran("p09-fires", types: ["integer", "text"]);

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select * from t where a = $1 and b = $2");

        page.FindAll(".ms-plan-param").Should().HaveCount(2);
        page.Find(".ms-plan-params legend").TextContent.Should().Contain("$1 to $2");

        page.Find("#ms-plan-param-1").Change("42");
        page.Find("#ms-plan-param-2").Change("two");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());
        page.WaitForAssertion(() => context.SqlConsole.Plans.Should().ContainSingle());

        PlanRunRequest request = context.SqlConsole.Plans[0].Request;
        request.Statement.Should().Be("select * from t where a = $1 and b = $2");
        request.Mode.Should().Be(PlanMode.Plan);
        request.Values.Should().Equal("42", "two");

        // What Postgres inferred is shown beside each box, once it said.
        page.WaitForAssertion(() => page.FindAll(".ms-plan-param-type").Select(static x => x.TextContent).Should().Equal("integer", "text"));

        // NULL is a value of its own, and not the text "NULL".
        page.FindAll(".ms-plan-param-null input")[1].Change(true);
        page.Find("#ms-plan-param-2").HasAttribute("disabled").Should().BeTrue();
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());
        page.WaitForAssertion(() => context.SqlConsole.Plans.Should().HaveCount(2));
        context.SqlConsole.Plans[1].Request.Values.Should().Equal("42", null);
    }

    [Fact]
    public async Task The_generic_plan_sends_no_values()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.NextPlan = Ran("p09-fires");

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select $1");
        page.Find("#ms-plan-param-1").Change("never sent");
        page.FindAll("input[name=ms-plan-mode]")[2].Change(true);

        page.Find("#ms-plan-param-1").HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-plan-params-note").TextContent.Should().Contain("no values");

        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());
        page.WaitForAssertion(() => context.SqlConsole.Plans.Should().ContainSingle());

        context.SqlConsole.Plans[0].Request.Mode.Should().Be(PlanMode.Generic);
        context.SqlConsole.Plans[0].Request.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task After_a_server_older_than_17_answers_MEMORY_and_SERIALIZE_are_disabled_with_the_release_and_the_options_it_left_out_are_named()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        PlanRunResult result = Ran(major: 16) with { LeftOut = ["MEMORY (needs PostgreSQL 17)"] };

        IRenderedComponent<PlanPage> page = await RanAsync(context, result: result);

        page.Find("#ms-plan-option-memory").HasAttribute("disabled").Should().BeTrue();
        page.Find("#ms-plan-option-memory").ParentElement!.GetAttribute("title").Should().Contain("PostgreSQL 16").And.Contain("needs 17");
        page.Find(".ms-plan-left-out").TextContent.Should().Contain("PostgreSQL 16").And.Contain("MEMORY (needs PostgreSQL 17)");
    }

    // ---- The address (D9) ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_link_reopens_the_page_with_the_editor_the_mode_and_the_options_filled_and_does_not_run()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.Navigate("postgres/plan?sql=" + Uri.EscapeDataString("select 1 from t where a = $1") + "&mode=analyze&options=verbose,wal");

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        page.Find("#ms-plan-sql").GetAttribute("value").Should().Be("select 1 from t where a = $1");
        page.FindAll("input[name=ms-plan-mode]").Select(static x => x.HasAttribute("checked")).Should().Equal(false, true, false);
        page.Find("#ms-plan-option-verbose").HasAttribute("checked").Should().BeTrue();
        page.Find("#ms-plan-option-wal").HasAttribute("checked").Should().BeTrue();
        page.Find("#ms-plan-option-buffers").HasAttribute("checked").Should().BeFalse("the address named the options that are on");
        page.FindAll(".ms-plan-param").Should().HaveCount(1, "the statement's placeholder has its box");
        page.Find(".ms-query-run").TextContent.Trim().Should().Be("Explain analyze");

        context.SqlConsole.Plans.Should().BeEmpty("a link never runs: a run needs a click");
        page.FindAll(".ms-plan-tree").Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_writes_the_statement_the_mode_and_the_options_into_the_address_but_never_the_plan_or_a_value()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.NextPlan = Ran("p09-fires");

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select * from t where a = $1");
        page.Find("#ms-plan-param-1").Change("a secret value");
        page.FindAll("input[name=ms-plan-mode]")[1].Change(true);
        page.Find("#ms-plan-option-verbose").Change(true);
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());
        page.WaitForAssertion(() => page.FindAll(".ms-plan-tree").Should().ContainSingle());

        string uri = context.CurrentUri;

        uri.Should().Contain("sql=select%20%2A%20from%20t%20where%20a%20%3D%20%241").And.Contain("mode=analyze").And.Contain("options=buffers%2Ctiming%2Cverbose");
        uri.Should().NotContain("secret").And.NotContain("Node%20Type");
    }

    [Fact]
    public async Task The_default_mode_and_options_are_not_written_into_the_address()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();

        IRenderedComponent<PlanPage> page = await RanAsync(context);

        context.CurrentUri.Should().Contain("sql=").And.NotContain("mode=").And.NotContain("options=");
    }

    // ---- A run drawn ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_asks_the_console_for_the_plan_and_draws_the_summary_strip_the_tree_and_the_insights()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();

        IRenderedComponent<PlanPage> page = await RanAsync(context);

        PlanRunRequest request = context.SqlConsole.Plans.Single().Request;
        request.Statement.Should().Be("select o.id from orders o");
        request.Mode.Should().Be(PlanMode.Plan);
        request.Options.Should().Be(new PlanOptions());

        Dictionary<string, string> strip = page.FindAll(".ms-plan-strip-item")
            .ToDictionary(static x => x.QuerySelector("dt")!.TextContent, static x => x.QuerySelector("dd")!.TextContent);

        strip["Planning time"].Should().Be("0.9 ms");
        strip["Execution time"].Should().Be("59.5 ms");
        strip["Triggers"].Should().Be("1.3 ms");
        strip["Rows returned"].Should().Be("1,000");
        strip["Cache hit ratio"].Should().Be("87%");
        strip["JIT"].Should().BeEmpty("Postgres printed no JIT, and a blank is a blank");

        page.FindAll(".ms-plan-node").Should().HaveCount(5);
        page.Find(".ms-query-editor-ok").TextContent.Should().Be("Planned in 12 ms; nothing ran.");
        page.Find(".ms-plan-caption").TextContent.Should().Contain("Planned with EXPLAIN (ANALYZE, BUFFERS)");
        page.Find(".ms-plan-summary").TextContent.Should().Contain("EXPLAIN ANALYZE").And.Contain("5 nodes");
        page.Find(".ms-plan-settings summary").TextContent.Should().Contain("2 planner settings differ");
    }

    [Fact]
    public async Task A_plan_that_fires_P09_links_to_Maintenance_for_the_scope_on_screen()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();

        IRenderedComponent<PlanPage> page = await RanAsync(context, result: Ran("p09-fires"));

        page.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Contain("P09");
        page.Find(".ms-plan-insight-page").GetAttribute("href").Should().StartWith("maintenance?").And.Contain("store=default");
        page.Find(".ms-plan-insight-page").TextContent.Should().Contain("Maintenance");
    }

    [Fact]
    public async Task A_guard_refusal_is_the_editors_error_with_a_caret_at_the_character()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.NextPlan = PlanRunResult.Refused(new SqlRejection("DisallowedStatement", "The console only runs select.", "delete", 0), 0);

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("delete from t");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());

        page.WaitForAssertion(() => page.Find(".ms-query-error-message").TextContent.Should().Be("The console only runs select."));
        page.FindAll(".ms-plan-tree").Should().BeEmpty();
    }

    [Fact]
    public async Task A_Postgres_error_points_into_the_typed_statement_not_the_statement_it_was_sent_in()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();

        // "select nope from t": sent behind "explain (format json) " (22 characters), so Postgres counts from the 8th of 30.
        int sent = "explain (format json) ".Length + "select ".Length + 1;
        context.SqlConsole.NextPlan = new PlanRunResult
        {
            Error = new SqlError("42703", "column \"nope\" does not exist", sent, null, null),
            ErrorOffset = "explain (format json) ".Length,
        };

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select nope from t");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());

        page.WaitForAssertion(() => page.Find(".ms-query-caret").TextContent.Should().EndWith("       ^"));
    }

    [Fact]
    public async Task An_error_in_another_command_than_the_statement_points_at_no_character_of_it()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.NextPlan = new PlanRunResult { Error = new SqlError("22P02", "invalid input syntax for type integer: \"x\"", 14, null, null), ErrorOffset = -1 };

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select $1::int");
        page.Find("#ms-plan-param-1").Change("x");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());

        page.WaitForAssertion(() => page.FindAll(".ms-query-error").Should().ContainSingle());
        page.FindAll(".ms-query-caret").Should().BeEmpty();
    }

    [Fact]
    public async Task A_plan_larger_than_the_studio_draws_says_so_and_draws_none()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        context.SqlConsole.NextPlan = new PlanRunResult { PlanUnavailable = QueryPlanReader.TooLarge(9_000_000) };

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        page.Find("#ms-plan-sql").Change("select 1");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());

        page.WaitForAssertion(() => page.Find(".ms-plan-unavailable").TextContent.Should().Contain("9,000,000 characters"));
    }

    // ---- The two views, one selected node, and the rest of the toolbar ----------------------------------------------------

    [Fact]
    public async Task The_node_table_has_a_row_for_every_node_and_the_columns_the_plan_printed()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context);

        page.Find(".ms-plan-view-table").Click();

        page.FindAll(".ms-plan-tree").Should().BeEmpty("the table replaces the tree");
        page.Find(".ms-table-scroll").GetAttribute("aria-label").Should().Be("Plan nodes");
        page.Find(".ms-plan-table").ClassList.Should().Contain("ms-table");

        page.FindAll(".ms-plan-table thead th").Select(static x => x.TextContent.Trim()).Should().Equal(
            "Node", "Self time", "Rows", "Estimated", "Misestimate", "Loops", "Shared hit", "Shared read", "Temp read", "Temp written", "I/O time");

        IReadOnlyList<IElement> rows = page.FindAll(".ms-plan-row");
        rows.Should().HaveCount(5);

        IReadOnlyList<string> cells = [.. rows[2].QuerySelectorAll("td").Select(static x => x.TextContent.Trim())];
        cells.Should().Equal("30 ms", "100,000", "100,000", "×1", "1", "1,900", "350", "0", "0", "4 ms");
        rows[2].QuerySelector("th")!.TextContent.Should().Contain("Seq Scan").And.Contain("on orders o");
        rows[0].QuerySelectorAll("td")[3].TextContent.Trim().Should().Be("×10");
        page.Find(".ms-plan-view-table").GetAttribute("aria-pressed").Should().Be("true");
        page.Find(".ms-plan-view-tree").GetAttribute("aria-pressed").Should().Be("false");
    }

    [Fact]
    public async Task A_click_on_a_header_sorts_the_table_and_a_second_click_reverses_it_with_aria_sort_saying_so()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context);
        page.Find(".ms-plan-view-table").Click();

        static string[] Ids(IRenderedComponent<PlanPage> p) => [.. p.FindAll(".ms-plan-row .ms-plan-table-id").Select(static x => x.TextContent)];
        static IElement Header(IRenderedComponent<PlanPage> p, string name) => p.FindAll(".ms-plan-table thead th").Single(x => x.TextContent.Trim() == name);

        Ids(page).Should().Equal("0", "1", "2", "3", "4");
        Header(page, "Node").GetAttribute("aria-sort").Should().Be("ascending", "the table starts in the plan's own order, which is the Node column ascending");
        Header(page, "Self time").GetAttribute("aria-sort").Should().Be("none");

        Header(page, "Self time").QuerySelector("button")!.Click();

        Ids(page).Should().Equal("2", "4", "0", "3", "1");
        Header(page, "Self time").GetAttribute("aria-sort").Should().Be("descending");
        Header(page, "Rows").GetAttribute("aria-sort").Should().Be("none");
        Header(page, "Node").GetAttribute("aria-sort").Should().Be("none");

        Header(page, "Self time").QuerySelector("button")!.Click();

        Ids(page).Should().Equal("1", "0", "3", "4", "2");
        Header(page, "Self time").GetAttribute("aria-sort").Should().Be("ascending");

        Header(page, "Shared read").QuerySelector("button")!.Click();
        Ids(page).Should().Equal("2", "4", "0", "1", "3");

        Header(page, "Node").QuerySelector("button")!.Click();
        Ids(page).Should().Equal("0", "1", "2", "3", "4");
        Header(page, "Node").GetAttribute("aria-sort").Should().Be("ascending");
    }

    [Fact]
    public async Task One_selected_node_is_shared_by_the_table_and_the_tree_in_both_directions()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context);

        // Nothing is selected, and no detail is drawn.
        page.FindAll(".ms-plan-node-selected, .ms-plan-node-detail").Should().BeEmpty();

        // The table, by its button: selects the node.
        page.Find(".ms-plan-view-table").Click();
        page.FindAll(".ms-plan-row")[2].QuerySelector(".ms-plan-pick")!.Click();

        page.FindAll(".ms-plan-row-selected").Should().ContainSingle().Which.QuerySelector(".ms-plan-table-id")!.TextContent.Should().Be("2");
        page.Find(".ms-plan-row-selected").GetAttribute("aria-current").Should().Be("true");
        page.Find(".ms-plan-node-detail-title").TextContent.Should().Contain("Seq Scan on orders o");

        // The tree shows the same node selected.
        page.Find(".ms-plan-view-tree").Click();

        IElement selected = page.FindAll(".ms-plan-node-selected").Should().ContainSingle().Subject;
        selected.QuerySelector(".ms-plan-node-type")!.TextContent.Should().Be("Seq Scan");
        selected.QuerySelector(".ms-plan-node-target")!.TextContent.Should().Be("on orders o");
        selected.GetAttribute("aria-current").Should().Be("true");

        // The tree, by its card: selects another, and the table follows.
        page.FindAll(".ms-plan-node")[4].Click();
        page.Find(".ms-plan-node-selected .ms-plan-node-target").TextContent.Should().Be("on customers c");
        page.Find(".ms-plan-view-table").Click();
        page.Find(".ms-plan-row-selected .ms-plan-table-id").TextContent.Should().Be("4");

        // The tree's keyboard button selects too, and the selection is cleared by the detail's own button.
        page.Find(".ms-plan-view-tree").Click();
        page.FindAll(".ms-plan-node-pick")[1].Click();
        page.Find(".ms-plan-node-selected .ms-plan-node-type").TextContent.Should().Be("Hash Join");

        page.Find(".ms-plan-node-detail-close").Click();
        page.FindAll(".ms-plan-node-selected, .ms-plan-node-detail").Should().BeEmpty();
    }

    [Fact]
    public async Task The_selected_nodes_every_key_is_listed_by_group_with_what_it_means()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context);

        page.FindAll(".ms-plan-node")[0].Click();

        IReadOnlyList<string> groups = [.. page.FindAll(".ms-plan-key-group-title").Select(static x => x.TextContent)];
        groups.Should().Equal("The node", "Estimate", "Actual", "Buffers", "I/O and WAL", "Sort, hash and memoize");

        IElement sortMethod = page.FindAll(".ms-plan-key-list .ms-plan-detail").Single(static x => x.QuerySelector("dt")!.TextContent == "Sort Method");
        sortMethod.QuerySelector(".ms-plan-detail-value")!.TextContent.Should().Be("quicksort");
        sortMethod.QuerySelector(".ms-plan-key-help")!.TextContent.Should().Contain("How the sort ran");

        page.FindAll(".ms-plan-key-list dt").Select(static x => x.TextContent).Should().Contain(["Node Type", "Startup Cost", "Actual Rows", "Shared Hit Blocks", "I/O Read Time", "Sort Space Used"]);
    }

    [Fact]
    public async Task The_heat_select_colours_each_nodes_bar_and_prints_its_value_as_text()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context);

        page.FindAll(".ms-plan-heat-line, .ms-plan-heat-legend").Should().BeEmpty("no heat until one is asked for");
        page.Find(".ms-plan-heat-select").GetAttribute("value").Should().Be("none");
        page.Find(".ms-plan-heat-select").QuerySelectorAll("option").Select(static x => x.TextContent).Should().Equal("None", "Self time", "Row misestimate", "Buffers read", "Temp I/O");

        page.Find(".ms-plan-heat-select").Change("self-time");

        page.Find(".ms-plan-heat-legend").TextContent.Should().Contain("own time");
        IReadOnlyList<IElement> nodes = page.FindAll(".ms-plan-node");
        nodes[2].ClassList.Should().Contain("ms-plan-heat-4", "the slowest node is as hot as it goes");
        nodes[2].QuerySelector(".ms-plan-bar")!.ClassList.Should().Contain("ms-plan-heat-4");
        nodes[2].QuerySelector(".ms-plan-heat-line")!.TextContent.Trim().Should().Be("Self time: 30 ms of its own", "colour is never the only signal");
        nodes[1].ClassList.Should().Contain("ms-plan-heat-1");

        page.Find(".ms-plan-heat-select").Change("misestimate");
        page.FindAll(".ms-plan-node")[0].QuerySelector(".ms-plan-heat-line")!.TextContent.Should().Contain("×10 off");
        page.FindAll(".ms-plan-node")[2].ClassList.Should().NotContain(static x => x.StartsWith("ms-plan-heat-", StringComparison.Ordinal), "an exact estimate is not hot");

        // The table carries the same heat in a column of its own.
        page.Find(".ms-plan-view-table").Click();
        page.Find(".ms-plan-table-heat-head").TextContent.Should().Be("Row misestimate");
        page.FindAll(".ms-plan-table-heat .ms-plan-heat-chip")[0].TextContent.Should().Be("×10 off");

        page.Find(".ms-plan-heat-select").Change("none");
        page.FindAll(".ms-plan-table-heat-head").Should().BeEmpty();
    }

    [Fact]
    public async Task The_critical_path_toggle_marks_the_chain_in_the_tree_and_the_table()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context);

        page.FindAll(".ms-plan-critical, .ms-plan-critical-flag").Should().BeEmpty();

        page.Find(".ms-plan-critical-input").Change(true);

        page.FindAll(".ms-plan-node.ms-plan-critical").Should().HaveCount(3);
        page.FindAll(".ms-plan-node.ms-plan-critical .ms-plan-node-type").Select(static x => x.TextContent).Should().Equal("Sort", "Hash Join", "Seq Scan");
        page.FindAll(".ms-plan-critical-flag").Should().HaveCount(3).And.OnlyContain(static x => x.TextContent == "critical");
        page.Find(".ms-plan-critical-legend").TextContent.Should().Contain("largest inclusive time");

        page.Find(".ms-plan-view-table").Click();
        page.FindAll(".ms-plan-row.ms-plan-critical .ms-plan-table-id").Select(static x => x.TextContent).Should().Equal("0", "1", "2");

        page.Find(".ms-plan-critical-input").Change(false);
        page.FindAll(".ms-plan-critical, .ms-plan-critical-flag").Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_plan_forgets_the_selection_of_the_last_but_keeps_the_view_the_visitor_chose()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context);
        page.Find(".ms-plan-view-table").Click();
        page.FindAll(".ms-plan-row")[1].QuerySelector(".ms-plan-pick")!.Click();
        page.FindAll(".ms-plan-node-detail").Should().ContainSingle();

        context.SqlConsole.NextPlan = Ran("p10-fires");
        await page.InvokeAsync(() => page.Find(".ms-query-run").Click());

        page.WaitForAssertion(() => page.FindAll(".ms-plan-row").Should().HaveCount(1));
        page.FindAll(".ms-plan-node-detail, .ms-plan-row-selected").Should().BeEmpty();
    }

    // ---- Paste a plan --------------------------------------------------------------------------------------------------

    private static async Task PasteAsync(PostgresStudioComponentContext context, IRenderedComponent<PlanPage> page, string text)
    {
        JSRuntimeInvocationHandler<int> sending = context.JSInterop.Setup<int>("martenStudio.query.sendInChunks", static _ => true);

        PlanPaste paste = page.FindComponent<PlanPaste>().Instance;
        page.Find(".ms-plan-paste-draw").Click();

        // What the browser's helper does, in chunks that are each a message the circuit accepts.
        await page.InvokeAsync(() => paste.BeginPaste());

        for (int i = 0; i < text.Length; i += 1000)
        {
            int start = i;
            await page.InvokeAsync(() => paste.PasteChunk(text.Substring(start, Math.Min(1000, text.Length - start))));
        }

        sending.SetResult(text.Length);
    }

    [Fact]
    public async Task A_pasted_plan_is_drawn_with_a_tree_a_table_and_a_summary_and_nothing_goes_to_the_database()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        string plan = PlanFixtures.Get(Join);
        plan.Length.Should().BeGreaterThan(5000, "a plan past what one chunk carries, so the hand-over is exercised");

        await PasteAsync(context, page, plan);

        page.WaitForAssertion(() => page.FindAll(".ms-plan-tree").Should().ContainSingle());
        page.FindAll(".ms-plan-node").Should().HaveCount(5);
        page.Find(".ms-plan-strip").TextContent.Should().Contain("59.5 ms");
        page.Find(".ms-plan-caption").TextContent.Should().Be(PlanPage.PastedCaption).And.Contain("not available");

        page.Find(".ms-plan-view-table").Click();
        page.FindAll(".ms-plan-row").Should().HaveCount(5);

        context.SqlConsole.Plans.Should().BeEmpty("a pasted plan reads nothing and runs nothing");
        context.Services.GetRequiredService<PostgresStudio.Services.StudioActionLogService>().GetLatest().Should().BeEmpty("a pasted plan is not audited");
    }

    [Fact]
    public async Task Text_that_is_not_a_plan_says_so_under_the_editor_and_draws_nothing()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        await PasteAsync(context, page, "[\"not a plan\"]");

        page.WaitForAssertion(() => page.Find(".ms-query-error-message").TextContent.Should().StartWith(PlanPage.PasteUnreadable).And.Contain(QueryPlanReader.NotAPlan));
        page.FindAll(".ms-plan-tree").Should().BeEmpty();
    }

    [Fact]
    public async Task Pasting_nothing_says_there_is_nothing_to_draw()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        context.JSInterop.Setup<int>("martenStudio.query.sendInChunks", static _ => true).SetResult(0);
        page.Find(".ms-plan-paste-draw").Click();

        page.WaitForAssertion(() => page.Find(".ms-plan-paste-error").TextContent.Should().Contain("nothing to draw"));
    }

    [Fact]
    public async Task Text_longer_than_the_reader_reads_is_refused_by_the_browsers_helper_and_the_page_says_so()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        context.JSInterop.Setup<int>("martenStudio.query.sendInChunks", static _ => true).SetResult(PlanPaste.PastedTooLarge);
        page.Find(".ms-plan-paste-draw").Click();

        page.WaitForAssertion(() => page.Find(".ms-plan-paste-error").TextContent.Should().Contain("more than the 4,194,304 the studio draws"));
    }

    [Fact]
    public async Task The_paste_box_is_not_bound_so_a_plan_never_travels_in_one_message()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        IElement textArea = page.Find("#ms-plan-paste");

        textArea.Attributes.Select(static x => x.Name).Should().NotContain(static x => x.StartsWith("blazor:on", StringComparison.Ordinal),
            "a change event carrying a plan past 32 KB would end the circuit; the text goes over in chunks");
        page.Find(".ms-plan-paste-help").TextContent.Should().Contain("nothing is sent to the database").And.Contain("not available for it");
    }

    // ---- Export --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Copy_as_Markdown_puts_the_summary_and_the_node_table_on_the_clipboard()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context);
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", static _ => true).SetResult(true);

        page.Find(".ms-plan-copy-markdown").Click();

        page.WaitForAssertion(() => page.Find(".ms-plan-export-status").TextContent.Should().Be("Copied the plan as Markdown."));

        string copied = (string) context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Single().Arguments[0]!;

        copied.Should().Be(PlanTable.Markdown(PlanFixtures.Plan(Join)));
        copied.Should().StartWith("**EXPLAIN ANALYZE** - 5 nodes").And.Contain("| 2 | &emsp;&emsp;Seq Scan on orders o | 30 ms |");
    }

    [Fact]
    public async Task Copy_JSON_and_Download_plan_json_hand_the_plan_to_the_browser_whole()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context);
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", static _ => true).SetResult(true);
        context.JSInterop.Setup<bool>("martenStudio.download.text", static _ => true).SetResult(true);

        page.Find(".ms-plan-copy-json").Click();
        page.Find(".ms-plan-download").Click();

        page.WaitForAssertion(() => page.Find(".ms-plan-export-status").TextContent.Should().Be("Saved plan.json."));

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Single().Arguments[0].Should().Be(PlanFixtures.Get(Join));

        IReadOnlyList<object?> download = context.JSInterop.Invocations["martenStudio.download.text"].Single().Arguments;
        download.Should().Equal("plan.json", "application/json", PlanFixtures.Get(Join));
        page.Find(".ms-plan-download").TextContent.Should().Be("Download plan.json");
    }

    [Fact]
    public async Task Download_for_speedscope_saves_plan_speedscope_json_in_speedscopes_format_named_for_the_statement()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = await RanAsync(context, "select o.id\nfrom orders o");
        context.JSInterop.Setup<bool>("martenStudio.download.text", static _ => true).SetResult(true);

        page.Find(".ms-plan-download-speedscope").TextContent.Should().Be("Download for speedscope");
        page.Find(".ms-plan-export-note").TextContent.Should().Be("Open it at speedscope.app (drag the file in; it stays in your browser).");
        page.FindAll(".ms-plan-export a[href]").Select(static x => x.GetAttribute("href")!).Should().OnlyContain(
            static x => x.StartsWith("data:", StringComparison.Ordinal), "no link to the viewer and no endpoint: the only anchor is the raw JSON's own data: download");
        page.Find(".ms-plan-export").InnerHtml.Should().NotContain("profileURL").And.NotContain("https://");

        page.Find(".ms-plan-download-speedscope").Click();

        page.WaitForAssertion(() => page.Find(".ms-plan-export-status").TextContent.Should().Be("Saved plan.speedscope.json."));

        IReadOnlyList<object?> download = context.JSInterop.Invocations["martenStudio.download.text"].Single().Arguments;
        download[0].Should().Be("plan.speedscope.json");
        download[1].Should().Be("application/json");

        JsonElement file = JsonDocument.Parse((string) download[2]!).RootElement;
        file.GetProperty("$schema").GetString().Should().Be("https://www.speedscope.app/file-format-schema.json");
        file.GetProperty("name").GetString().Should().Be("select o.id", "the statement's first line");
        file.GetProperty("exporter").GetString().Should().StartWith("Postgres Studio ");
        file.GetProperty("shared").GetProperty("frames").GetArrayLength().Should().Be(5);
        file.GetProperty("profiles").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).Should().Equal("Self time", "Self cost", "Shared blocks read");
    }

    [Fact]
    public async Task A_pasted_plan_is_exported_for_speedscope_as_a_pasted_plan()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();
        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();
        context.JSInterop.Setup<bool>("martenStudio.download.text", static _ => true).SetResult(true);

        await PasteAsync(context, page, PlanFixtures.Get(Join));
        page.WaitForAssertion(() => page.FindAll(".ms-plan-tree").Should().ContainSingle());

        page.Find(".ms-plan-download-speedscope").Click();
        page.WaitForAssertion(() => context.JSInterop.Invocations["martenStudio.download.text"].Should().ContainSingle());

        JsonElement file = JsonDocument.Parse((string) context.JSInterop.Invocations["martenStudio.download.text"].Single().Arguments[2]!).RootElement;
        file.GetProperty("name").GetString().Should().Be("Pasted plan");
    }

    // ---- The console's plan view is what it was ------------------------------------------------------------------------

    [Fact]
    public async Task The_console_view_has_none_of_the_analyzers_controls_and_nodes_that_do_not_react_to_a_click()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();

        IRenderedComponent<PlanView> view = context.Render<PlanView>(parameters => parameters.Add(x => x.Json, PlanFixtures.Get(Join)));

        view.FindAll(".ms-plan-controls, .ms-plan-strip, .ms-plan-export, .ms-plan-node-pick, .ms-plan-node-detail").Should().BeEmpty();
        view.FindAll(".ms-plan-node").Should().OnlyContain(static x => !x.Attributes.Any(a => a.Name.StartsWith("blazor:", StringComparison.Ordinal)), "no round trip for a click that does nothing");
        view.Find(".ms-plan-summary").TextContent.Should().Contain("ran in 59.5 ms");
        view.FindAll(".ms-plan-raw").Should().ContainSingle();
    }

    [Fact]
    public async Task The_consoles_SQL_panel_offers_Open_in_Plan_with_the_statement_in_the_address()
    {
        await using PostgresStudioComponentContext context = await ReadyAsync();

        IRenderedComponent<SqlConsolePanel> panel = context.Render<SqlConsolePanel>();
        panel.FindAll(".ms-query-open-plan-link").Should().BeEmpty("there is no statement to carry yet");

        panel.Find("#ms-query-sql").Change("select * from t where a = $1");

        IElement link = panel.Find(".ms-query-open-plan-link");
        link.TextContent.Should().Be("Open in Plan");
        link.GetAttribute("href").Should().Be("plan?sql=select%20*%20from%20t%20where%20a%20%3D%20$1&store=default&db=default");
    }
}
