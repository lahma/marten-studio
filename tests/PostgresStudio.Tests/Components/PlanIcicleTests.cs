using System.Diagnostics;

using AngleSharp.Dom;

using Bunit;

using Microsoft.AspNetCore.Components.Web;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Services.Query;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.0-P3B (issue #28): the icicle view as the visitor meets it - a button beside the tree and the node table, a frame for each
/// node with an accessible name that carries its value and share, one selected node shared with the tree, the keyboard, the
/// frames too narrow to draw merged into one that selects nothing, the labelled scroll region, and a plan of two thousand nodes.
/// </summary>
public class PlanIcicleTests
{
    private const string Join = "analyzed-join";

    private static async Task<PostgresStudioComponentContext> ContextAsync() => await new PostgresStudioComponentContext().ReadyAsync();

    private static IRenderedComponent<PlanView> Render(PostgresStudioComponentContext context, string json) =>
        context.Render<PlanView>(parameters => parameters.Add(x => x.Json, json).Add(x => x.Analyzer, true).Add(x => x.Label, "Query plan"));

    private static IRenderedComponent<PlanView> Icicle(PostgresStudioComponentContext context, string json)
    {
        IRenderedComponent<PlanView> view = Render(context, json);
        view.Find(".ms-plan-view-icicle").Click();
        return view;
    }

    private static IElement Frame(IRenderedComponent<PlanView> view, int node) =>
        view.Find(".ms-plan-icicle-frame[data-node='" + node + "']");

    private static string CostPlan(int children, double each) =>
        "[{\"Plan\": {\"Node Type\": \"Append\", \"Total Cost\": " + (children * each).ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", \"Plans\": [" + string.Join(",", Enumerable.Range(0, children).Select(_ => "{\"Node Type\": \"Seq Scan\", \"Total Cost\": " + each.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}")) + "]}}]";

    [Fact]
    public async Task The_Icicle_button_sits_beside_Tree_and_Node_table_and_says_which_view_is_pressed()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Render(context, PlanFixtures.Get(Join));

        view.FindAll(".ms-plan-view-switch button").Select(static x => x.TextContent.Trim()).Should().Equal("Tree", "Node table", "Icicle");
        view.Find(".ms-plan-view-tree").GetAttribute("aria-pressed").Should().Be("true");
        view.Find(".ms-plan-view-icicle").GetAttribute("aria-pressed").Should().Be("false");
        view.FindAll(".ms-plan-icicle").Should().BeEmpty("the tree is the default view");

        view.Find(".ms-plan-view-icicle").Click();

        view.Find(".ms-plan-view-icicle").GetAttribute("aria-pressed").Should().Be("true");
        view.Find(".ms-plan-view-tree").GetAttribute("aria-pressed").Should().Be("false");
        view.Find(".ms-plan-view-table").GetAttribute("aria-pressed").Should().Be("false");
        view.FindAll(".ms-plan-tree, .ms-plan-table").Should().BeEmpty("the icicle replaces both");
        view.FindAll(".ms-plan-icicle").Should().ContainSingle();

        view.Find(".ms-plan-view-table").Click();
        view.Find(".ms-plan-view-icicle").GetAttribute("aria-pressed").Should().Be("false");
        view.FindAll(".ms-plan-icicle").Should().BeEmpty();

        view.Find(".ms-plan-view-icicle").Click();
        view.Find(".ms-plan-view-tree").Click();
        view.Find(".ms-plan-view-tree").GetAttribute("aria-pressed").Should().Be("true");
        view.FindAll(".ms-plan-icicle").Should().BeEmpty();
    }

    [Fact]
    public async Task The_console_and_the_drill_in_have_no_icicle_button()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = context.Render<PlanView>(parameters => parameters.Add(x => x.Json, PlanFixtures.Get(Join)));

        view.FindAll(".ms-plan-view-icicle, .ms-plan-icicle").Should().BeEmpty();
    }

    [Fact]
    public async Task Every_node_is_a_focusable_frame_with_an_accessible_name_that_carries_its_value_and_share()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, PlanFixtures.Get(Join));

        QueryPlan plan = PlanFixtures.Plan(Join);
        IReadOnlyList<IElement> frames = view.FindAll(".ms-plan-icicle-frame");
        frames.Should().HaveCount(plan.NodeCount);
        frames.Should().OnlyContain(static x => x.GetAttribute("tabindex") == "0" && x.GetAttribute("role") == "button");

        IElement root = Frame(view, 0);
        root.GetAttribute("aria-label").Should().StartWith(plan.Root.Description + ": ").And.Contain(" ms with what is below it (100% of the plan), ").And.EndWith(" its own");
        root.QuerySelector("title")!.TextContent.Should().Be(root.GetAttribute("aria-label"), "the title says what the name does");

        IElement scan = Frame(view, 2);
        scan.GetAttribute("aria-label").Should().Contain("Seq Scan on orders o: 30 ms with what is below it").And.Contain("30 ms its own");
        view.Find(".ms-plan-icicle-legend").TextContent.Should().Contain("as wide as its time");
    }

    [Fact]
    public async Task A_plan_without_timing_is_drawn_by_cost_and_says_so()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, CostPlan(3, 10));

        view.Find(".ms-plan-icicle-legend").TextContent.Should().Contain("as wide as its estimated cost");
        Frame(view, 1).GetAttribute("aria-label").Should().Be("Seq Scan: 10.00 with what is below it (33% of the plan), 10.00 its own");
    }

    [Fact]
    public async Task The_frames_are_drawn_as_percentages_of_the_region_so_that_text_is_never_stretched()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, CostPlan(2, 50));

        view.Find(".ms-plan-icicle").GetAttribute("width").Should().Be("100%");
        IElement second = Frame(view, 2);
        second.QuerySelector("rect")!.GetAttribute("x").Should().Be("50%");
        second.QuerySelector("rect")!.GetAttribute("width").Should().Be("50%");
        Frame(view, 0).QuerySelector("rect")!.GetAttribute("width").Should().Be("100%");
        view.Find(".ms-plan-icicle").InnerHtml.Should().NotContain("transform", "nothing is scaled");
    }

    [Fact]
    public async Task A_wide_frame_is_labelled_and_a_narrow_one_keeps_its_name_and_title_only()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        // 94 of 100 is labelled; 5 and 1 are under the label width (0.06) but over the merge floor.
        IRenderedComponent<PlanView> view = Icicle(context, "[{\"Plan\": {\"Node Type\": \"Append\", \"Total Cost\": 100, \"Plans\": ["
            + "{\"Node Type\": \"Seq Scan\", \"Total Cost\": 94}, {\"Node Type\": \"Index Scan\", \"Total Cost\": 5}, {\"Node Type\": \"Sort\", \"Total Cost\": 1}]}}]");

        Frame(view, 1).QuerySelector("text")!.TextContent.Should().Be("Seq Scan");
        Frame(view, 2).QuerySelector("text").Should().BeNull();
        Frame(view, 2).GetAttribute("aria-label").Should().StartWith("Index Scan: ");
        Frame(view, 2).QuerySelector("title")!.TextContent.Should().StartWith("Index Scan: ");
        view.FindAll("clipPath").Should().HaveCount(2, "a clip for a label that is drawn, and none for one that is not");
        view.FindAll("clipPath").Select(static x => x.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_frames_own_part_is_a_bar_along_its_foot_for_a_node_that_has_one()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, CostPlan(1, 10));

        // The root costs nothing of its own; the scan is all its own.
        Frame(view, 0).QuerySelector(".ms-plan-icicle-own").Should().BeNull();
        Frame(view, 1).QuerySelector(".ms-plan-icicle-own")!.GetAttribute("width").Should().Be("100%");
    }

    [Fact]
    public async Task The_frames_are_in_a_labelled_scroll_region()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, PlanFixtures.Get(Join));

        IElement region = view.FindAll(".ms-table-scroll").Single(static x => x.QuerySelector(".ms-plan-icicle") is not null);
        region.GetAttribute("role").Should().Be("region");
        region.GetAttribute("aria-label").Should().Be("Query plan as an icicle");
        region.GetAttribute("tabindex").Should().Be("0");
    }

    [Fact]
    public async Task A_click_selects_the_node_in_the_tree_and_the_table_too()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, PlanFixtures.Get(Join));

        view.FindAll(".ms-plan-icicle-selected").Should().BeEmpty();
        Frame(view, 2).Click();

        view.FindAll(".ms-plan-icicle-selected").Should().ContainSingle().Which.GetAttribute("data-node").Should().Be("2");
        Frame(view, 2).GetAttribute("aria-pressed").Should().Be("true");
        Frame(view, 1).GetAttribute("aria-pressed").Should().Be("false");
        view.Find(".ms-plan-node-detail-title").TextContent.Should().Contain("Seq Scan on orders o");

        view.Find(".ms-plan-view-tree").Click();
        view.FindAll(".ms-plan-node-selected").Should().ContainSingle().Which.QuerySelector(".ms-plan-node-target")!.TextContent.Should().Be("on orders o");

        view.FindAll(".ms-plan-node")[4].Click();
        view.Find(".ms-plan-view-icicle").Click();
        view.Find(".ms-plan-icicle-selected").GetAttribute("data-node").Should().Be("4", "the tree's selection shows in the icicle");

        view.Find(".ms-plan-view-table").Click();
        view.Find(".ms-plan-row-selected .ms-plan-table-id").TextContent.Should().Be("4");
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    public async Task Enter_and_Space_select_the_focused_frame(string key)
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, PlanFixtures.Get(Join));

        Frame(view, 3).KeyDown(new KeyboardEventArgs { Key = key });

        view.Find(".ms-plan-icicle-selected").GetAttribute("data-node").Should().Be("3");
        view.Find(".ms-plan-view-tree").Click();
        view.FindAll(".ms-plan-node-selected").Should().ContainSingle();
    }

    [Fact]
    public async Task Other_keys_select_nothing()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, PlanFixtures.Get(Join));

        Frame(view, 3).KeyDown(new KeyboardEventArgs { Key = "a" });
        Frame(view, 3).KeyDown(new KeyboardEventArgs { Key = "Tab" });

        view.FindAll(".ms-plan-icicle-selected").Should().BeEmpty();
    }

    [Fact]
    public async Task The_arrow_keys_move_the_focus_to_the_parent_the_first_child_and_the_siblings()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        // Append (0) with two scans (1, 2) and a Sort (3) over a scan (4).
        IRenderedComponent<PlanView> view = Icicle(context, "[{\"Plan\": {\"Node Type\": \"Append\", \"Total Cost\": 40, \"Plans\": ["
            + "{\"Node Type\": \"Seq Scan\", \"Total Cost\": 10}, {\"Node Type\": \"Index Scan\", \"Total Cost\": 10},"
            + "{\"Node Type\": \"Sort\", \"Total Cost\": 20, \"Plans\": [{\"Node Type\": \"Seq Scan\", \"Total Cost\": 10}]}]}}]");

        // Each key is a request to focus a frame; the frame is found by its element reference, which bUnit hands to the interop call.
        async Task<int> FocusedAfterAsync(int from, string key)
        {
            int before = FocusCalls(context);
            Frame(view, from).KeyDown(new KeyboardEventArgs { Key = key });
            await Task.Yield();
            IReadOnlyList<JSRuntimeInvocation> calls = Focus(context);
            calls.Count.Should().Be(before + 1, "the key asked the browser to focus exactly one frame");
            return ReferencedNode(view, calls[^1]);
        }

        (await FocusedAfterAsync(0, "ArrowDown")).Should().Be(1, "the first child");
        (await FocusedAfterAsync(1, "ArrowRight")).Should().Be(2);
        (await FocusedAfterAsync(2, "ArrowRight")).Should().Be(3);
        (await FocusedAfterAsync(3, "ArrowLeft")).Should().Be(2);
        (await FocusedAfterAsync(3, "ArrowDown")).Should().Be(4);
        (await FocusedAfterAsync(4, "ArrowUp")).Should().Be(3, "the parent");
        (await FocusedAfterAsync(1, "ArrowUp")).Should().Be(0);

        int calls = FocusCalls(context);
        Frame(view, 0).KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        Frame(view, 0).KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        Frame(view, 1).KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        Frame(view, 3).KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        Frame(view, 4).KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        FocusCalls(context).Should().Be(calls, "the root has no parent, a first child no left sibling, a last no right one, a leaf no child");
        view.FindAll(".ms-plan-icicle-selected").Should().BeEmpty("moving the focus selects nothing");
    }

    private const string FocusIdentifier = "Blazor._internal.domWrapper.focus";

    private static IReadOnlyList<JSRuntimeInvocation> Focus(PostgresStudioComponentContext context) =>
        [.. context.JSInterop.Invocations[FocusIdentifier]];

    private static int FocusCalls(PostgresStudioComponentContext context) => Focus(context).Count;

    /// <summary>The node whose frame an interop focus call was made for: the call's first argument is the element reference the component captured for it.</summary>
    private static int ReferencedNode(IRenderedComponent<PlanView> view, JSRuntimeInvocation call)
    {
        string id = ((Microsoft.AspNetCore.Components.ElementReference) call.Arguments[0]!).Id;
        return view.FindComponent<PlanIcicle>().Instance.FrameReferences.Single(x => x.Value.Id == id).Key;
    }

    [Fact]
    public async Task Nodes_too_narrow_to_draw_are_one_focusable_frame_that_selects_nothing()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(
            context,
            "[{\"Plan\": {\"Node Type\": \"Append\", \"Total Cost\": 1000, \"Plans\": [{\"Node Type\": \"Seq Scan\", \"Total Cost\": 998}, {\"Node Type\": \"Index Scan\", \"Total Cost\": 1}, {\"Node Type\": \"Sort\", \"Total Cost\": 1}]}}]");

        IElement merged = view.Find(".ms-plan-icicle-merged");
        merged.GetAttribute("tabindex").Should().Be("0");
        merged.GetAttribute("aria-disabled").Should().Be("true");
        merged.GetAttribute("aria-label").Should().StartWith("2 small nodes: ");
        view.FindAll(".ms-plan-icicle-frame").Should().HaveCount(3, "the root, the scan and the merged frame");

        merged.Click();
        merged.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        merged.KeyDown(new KeyboardEventArgs { Key = " " });

        view.FindAll(".ms-plan-icicle-selected").Should().BeEmpty();
        view.FindAll(".ms-plan-node-detail").Should().BeEmpty();
        view.Find(".ms-plan-view-tree").Click();
        view.FindAll(".ms-plan-node-selected").Should().BeEmpty("nothing was selected");
    }

    [Fact]
    public async Task A_plan_with_nothing_to_weigh_says_so_in_place_of_frames()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, "[{\"Plan\": {\"Node Type\": \"Result\", \"Total Cost\": 0}}]");

        view.Find(".ms-plan-icicle-empty").TextContent.Should().Contain("no estimated cost to draw");
        view.FindAll(".ms-plan-icicle-frame").Should().BeEmpty();
    }

    [Fact]
    public async Task The_page_heat_colours_the_frames_and_the_name_still_carries_the_value()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Icicle(context, PlanFixtures.Get(Join));
        view.Find(".ms-plan-heat-select").Change("self-time");

        view.FindAll(".ms-plan-icicle-frame").Where(static x => x.ClassList.Any(static c => c.StartsWith("ms-plan-heat-", StringComparison.Ordinal)))
            .Should();

        view.Find(".ms-plan-heat-select").Change("none");
        view.FindAll(".ms-plan-icicle-frame").SelectMany(static x => x.ClassList).Should().NotContain(static c => c.StartsWith("ms-plan-heat-", StringComparison.Ordinal));
        view.FindAll(".ms-plan-icicle-frame").Should().OnlyContain(static x => x.GetAttribute("aria-label")!.Contains(" ms with what is below it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plan_of_two_thousand_nodes_renders_within_the_budget()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        // 400 leaves, each 1/400 of the plan - over the merge floor - under a chain of single-child nodes: all 2,000 nodes are drawn.
        List<string> chains = [];
        int nodes = 1;

        for (int i = 0; i < 400; i++)
        {
            int length = i == 399 ? 4 : 5;
            string chain = "{\"Node Type\": \"Seq Scan\", \"Total Cost\": 1}";

            for (int level = 1; level < length; level++)
            {
                chain = "{\"Node Type\": \"Sort\", \"Total Cost\": 1, \"Plans\": [" + chain + "]}";
            }

            chains.Add(chain);
            nodes += length;
        }

        nodes.Should().Be(QueryPlanReader.MaxNodes);
        string plan = "[{\"Plan\": {\"Node Type\": \"Append\", \"Total Cost\": 400, \"Plans\": [" + string.Join(",", chains) + "]}}]";

        Stopwatch watch = Stopwatch.StartNew();
        IRenderedComponent<PlanView> view = Icicle(context, plan);
        watch.Stop();

        view.FindAll(".ms-plan-icicle-frame").Should().HaveCount(QueryPlanReader.MaxNodes, "every node is wide enough to draw");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "a 2,000-node plan renders in a bUnit test within a generous bound");
    }
}
