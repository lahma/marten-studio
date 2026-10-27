using AngleSharp.Dom;

using Bunit;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Services.Query;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// X1's tree over three of the PROBE packet's fixtures - PostgreSQL 14's plain plan, 18's analyzed one and 19's release
/// candidate's - and over what no fixture holds: every node with its type and target, its rows, its share, its buffers, 18's
/// keys only where printed, the insights under the tree, and the tree in a labelled scroll region (D25).
/// </summary>
public class PlanViewTests
{
    private static async Task<PostgresStudioComponentContext> ContextAsync() => await new PostgresStudioComponentContext().ReadyAsync();

    private static IRenderedComponent<PlanView> Render(PostgresStudioComponentContext context, string json, string? caption = null) =>
        context.Render<PlanView>(parameters => parameters.Add(x => x.Json, json).Add(x => x.Caption, caption).Add(x => x.Label, "Query plan"));

    /// <summary>Each node's head, its parts joined by a space: Blazor drops the whitespace between them, and the head's gap draws it.</summary>
    private static IReadOnlyList<string> Heads(IRenderedComponent<PlanView> view) =>
        [.. view.FindAll(".ms-plan-node-head").Select(static x => string.Join(' ', x.Children.Select(static c => Normal(c.TextContent))))];

    private static IReadOnlyList<string> Facts(IElement node) =>
        [.. node.QuerySelectorAll(".ms-plan-fact").Select(static x => x.TextContent)];

    private static string Normal(string text) => string.Join(' ', text.Split((char[]) [' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public async Task PostgreSQL_14s_plain_plan_is_a_tree_of_estimates_and_cost_shares()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = Render(context, ExplainFixtures.Get("14-nestloop-plain"));

        Heads(view).Should().Equal("Nested Loop P05", "Seq Scan on u", "Index Scan using t_pkey on t");
        view.FindAll(".ms-plan-children").Should().ContainSingle("the loop's two children are one level below it");

        IReadOnlyList<IElement> nodes = view.FindAll(".ms-plan-node");
        Facts(nodes[1]).Should().Equal("~7,668 rows estimated", "cost 0.00...944.00");
        nodes[1].QuerySelector(".ms-plan-share-value")!.TextContent.Should().EndWith("of cost");
        nodes[1].QuerySelector(".ms-plan-details dl")!.TextContent.Should().Contain("Filter").And.Contain("(v < 2)");
        view.FindAll(".ms-plan-fact").Should().NotContain(static x => x.TextContent.Contains("returned", StringComparison.Ordinal), "nothing ran");
        view.FindAll(".ms-plan-fact").Should().NotContain(static x => x.TextContent.Contains("index search", StringComparison.Ordinal), "14 prints no Index Searches");

        view.Find(".ms-plan-summary").TextContent.Should().Contain("EXPLAIN").And.Contain("estimated cost 17,170.75").And.Contain("3 nodes");
        view.Find(".ms-plan-legend").TextContent.Should().Contain("estimated cost").And.Contain("planner's estimates");

        IElement region = view.Find(".ms-table-scroll");
        region.GetAttribute("role").Should().Be("region");
        region.GetAttribute("aria-label").Should().Be("Query plan");
        region.GetAttribute("tabindex").Should().Be("0");
        region.QuerySelector(".ms-plan-tree").Should().NotBeNull("the tree is inside the region");

        IElement insight = view.Find(".ms-plan-insight");
        insight.ClassList.Should().Contain("ms-plan-insight-info");
        insight.QuerySelector(".ms-plan-insight-code")!.GetAttribute("href").Should().EndWith("/docs/health/plans.md#p05-nested-loop-over-a-large-outer-side");
        insight.QuerySelector(".ms-plan-insight-code")!.GetAttribute("target").Should().Be("_blank");
        insight.QuerySelector(".ms-plan-insight-message")!.TextContent.Should().Contain("an estimated 7,668 rows from Seq Scan on u");

        view.Find(".ms-plan-not-judged").TextContent.Should().Contain("P01 Row estimate off tenfold or more").And.Contain("P08");
        view.Find(".ms-plan-raw summary").TextContent.Should().Contain("JSON");
    }

    [Fact]
    public async Task PostgreSQL_18s_analyzed_plan_draws_rows_loops_time_shares_buffers_and_index_searches()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = Render(context, ExplainFixtures.Get("18-nestloop-analyze"));

        Heads(view).Should().Equal("Nested Loop P05", "Seq Scan on u", "Index Scan using t_pkey on t");

        IReadOnlyList<IElement> nodes = view.FindAll(".ms-plan-node");
        Facts(nodes[2]).Should().Contain(["~1 rows estimated", "1 rows returned × 7,693 loops", "buffers: shared hit 30,772", "7,693 index searches"]);
        Facts(nodes[1]).Should().Contain("buffers: shared hit 319");
        nodes[0].QuerySelector(".ms-plan-share-value")!.TextContent.Should().EndWith("of time");
        Facts(nodes[0]).Should().Contain(static x => x.EndsWith("of its own", StringComparison.Ordinal));

        view.Find(".ms-plan-summary").TextContent.Should().Contain("EXPLAIN ANALYZE").And.Contain("planned in 1.5 ms").And.Contain("ran in 18.2 ms");
        view.Find(".ms-plan-legend").TextContent.Should().Contain("share of the plan's time").And.Contain("per loop");
        view.FindAll(".ms-plan-not-judged").Should().BeEmpty("an analyzed plan is judged by every insight");
        view.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Equal("P05");
        view.FindAll(".ms-chip").Should().NotContain(static x => x.TextContent == "disabled", "18 prints Disabled: false on every node, and false draws nothing");
    }

    [Fact]
    public async Task PostgreSQL_19s_analyzed_cte_draws_its_subplan_and_where_it_kept_its_rows()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = Render(context, ExplainFixtures.Get("19rc1-cte-analyze"), "A caption of the caller's.");

        Heads(view).Should().Equal("CTE Scan on x", "CTE x HashAggregate", "Seq Scan on t");
        Facts(view.FindAll(".ms-plan-node")[0]).Should().Contain(["1,000 rows returned", "kept in memory, at most 56.0 KiB"]);
        view.Find(".ms-plan-caption").TextContent.Should().Be("A caption of the caller's.");
        view.Find(".ms-plan-insights-none").TextContent.Should().Be("None of the plan insights fired on this plan.");
    }

    [Fact]
    public async Task A_spill_flags_its_node_and_says_so_under_the_tree()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = Render(context, ExplainFixtures.Get("19rc1-sort-spill-analyze"));

        view.FindAll(".ms-plan-node-flag").Select(static x => x.TextContent).Should().Equal("P02");
        view.Find(".ms-plan-insight").ClassList.Should().Contain("ms-plan-insight-warning");
        view.Find(".ms-plan-insight-remedy").TextContent.Should().Contain("work_mem");
        Facts(view.FindAll(".ms-plan-node")[1]).Should().Contain("buffers: shared hit 2,705, temp read 8,240, temp written 8,707");
        view.FindAll(".ms-plan-node")[1].QuerySelector(".ms-plan-details dl")!.TextContent.Should()
            .Contain("external merge, 8.0 MiB of disk").And.Contain("on disk, up to 8.2 MiB each");
    }

    [Fact]
    public async Task A_node_a_planner_setting_disabled_and_one_that_never_ran_say_so()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = Render(context, """
            [{"Plan": {"Node Type": "Append", "Actual Rows": 1, "Actual Loops": 1, "Plans": [
              {"Node Type": "Seq Scan", "Relation Name": "t", "Disabled": true, "Actual Rows": 1, "Actual Loops": 1},
              {"Node Type": "Index Scan", "Index Name": "i", "Relation Name": "t", "Actual Rows": 0, "Actual Loops": 0}]}}]
            """);

        view.FindAll(".ms-plan-chip").Select(static x => x.TextContent).Should().Equal("disabled", "never executed");
        view.FindAll(".ms-plan-node-idle").Should().ContainSingle();
    }

    [Fact]
    public async Task Text_that_is_not_a_plan_says_so_and_still_offers_what_it_was_given()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = Render(context, "[\"not a plan\"]");

        view.Find(".ms-plan-unreadable").TextContent.Should().Be(QueryPlanReader.NotAPlan);
        view.FindAll(".ms-plan-tree").Should().BeEmpty();
        view.FindAll(".ms-plan-raw").Should().ContainSingle();
    }

    [Fact]
    public async Task A_plan_handed_over_read_draws_the_tree_and_never_offers_raw_json()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        QueryPlan plan = QueryPlanReader.Read(ExplainFixtures.Get("16-seqfilter-plain")).Plan!;

        IRenderedComponent<PlanView> view = context.Render<PlanView>(parameters => parameters.Add(x => x.Plan, plan).Add(x => x.Json, "[]"));

        Heads(view).Should().Equal("Seq Scan on t P07");
        view.FindAll(".ms-plan-raw").Should().BeEmpty("a plan a service read and masked is never offered raw");
    }

    [Fact]
    public async Task A_plan_larger_than_the_drawn_bound_draws_its_first_nodes_and_says_how_many()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        string children = string.Join(",", Enumerable.Range(0, PlanView.MaxDrawnNodes + 50).Select(static _ => "{\"Node Type\": \"Seq Scan\", \"Total Cost\": 1}"));

        IRenderedComponent<PlanView> view = Render(context, "[{\"Plan\": {\"Node Type\": \"Append\", \"Total Cost\": 1, \"Plans\": [" + children + "]}}]");

        view.FindAll(".ms-plan-node").Should().HaveCount(PlanView.MaxDrawnNodes);
        view.Find(".ms-plan-summary").TextContent.Should().Contain("451 nodes, the first 400 drawn");
    }
}
