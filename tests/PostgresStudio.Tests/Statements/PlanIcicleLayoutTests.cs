using System.Text.Json;

using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 1.0-P3B (issue #28): the icicle view's layout - a frame as wide as its inclusive value, its own part the self time or self cost
/// the tree's bars and the speedscope export use, nodes too narrow to draw merged into one frame for each parent, and nothing at
/// all for a plan with nothing to weigh.
/// </summary>
public class PlanIcicleLayoutTests
{
    /// <summary>A plan by cost: node ids run in pre-order, so the root is 0.</summary>
    private static QueryPlan ByCost(string plan) => QueryPlanReader.Read("[{\"Plan\": " + plan + "}]").Plan!;

    private static string Node(string type, double total, params string[] children) =>
        "{\"Node Type\": \"" + type + "\", \"Total Cost\": " + total.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + (children.Length == 0 ? string.Empty : ", \"Plans\": [" + string.Join(",", children) + "]") + "}";

    private static QueryPlan Nested() => ByCost(Node("Append", 100, Node("Sort", 30, Node("Seq Scan", 10)), Node("Index Scan", 20)));

    private static IcicleFrame Frame(IReadOnlyList<IcicleFrame> frames, int id) => frames.Single(x => x.Id == id);

    [Fact]
    public void A_frame_is_as_wide_as_its_inclusive_value_and_carries_its_own_part()
    {
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(Nested());

        frames.Select(static x => x.Id).Should().Equal(0, 1, 2, 3);
        frames.Select(static x => x.Inclusive).Should().Equal(100, 30, 10, 20);
        frames.Select(static x => x.Own).Should().Equal(50, 20, 10, 20);
        frames.Select(static x => x.Width).Should().Equal(1, 0.3, 0.1, 0.2);
        frames.Select(static x => x.Depth).Should().Equal(0, 1, 2, 1);
        frames.Select(static x => x.ParentId).Should().Equal(null, 0, 1, 0);
        frames.Select(static x => x.Label).Should().Equal("Append", "Sort", "Seq Scan", "Index Scan");
        frames.Select(static x => x.Merged).Should().OnlyContain(static x => x == 0);
    }

    [Fact]
    public void Children_start_where_the_one_before_ended_and_the_first_where_its_parent_does()
    {
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(Nested());

        Frame(frames, 0).X.Should().Be(0);
        Frame(frames, 1).X.Should().Be(0);
        Frame(frames, 2).X.Should().Be(Frame(frames, 1).X, "a first child starts at its parent");
        Frame(frames, 3).X.Should().BeApproximately(Frame(frames, 1).X + Frame(frames, 1).Width, 1e-12, "the next sibling starts where the last ended");

        foreach (IcicleFrame frame in frames.Where(static x => x.ParentId is not null))
        {
            IcicleFrame parent = Frame(frames, frame.ParentId!.Value);
            frame.X.Should().BeGreaterThanOrEqualTo(parent.X);
            (frame.X + frame.Width).Should().BeLessThanOrEqualTo(parent.X + parent.Width + 1e-12, "a child is drawn under its parent");
        }
    }

    [Fact]
    public void Parents_come_before_their_children()
    {
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(Nested());

        foreach (IcicleFrame frame in frames.Where(static x => x.ParentId is not null))
        {
            frames.ToList().FindIndex(x => x.Id == frame.ParentId).Should().BeLessThan(frames.ToList().IndexOf(frame));
        }
    }

    [Fact]
    public void A_node_with_no_value_of_its_own_is_as_wide_as_what_is_below_it()
    {
        // The root's total is its only child's: it costs nothing itself, and is still the whole plan's width.
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(ByCost(Node("Limit", 100, Node("Seq Scan", 100))));

        Frame(frames, 0).Own.Should().Be(0);
        Frame(frames, 0).Inclusive.Should().Be(100);
        Frame(frames, 0).Width.Should().Be(1);
        Frame(frames, 1).Width.Should().Be(1);
    }

    [Fact]
    public void The_own_values_are_the_trees_own_self_cost_and_the_speedscope_exports_weights()
    {
        QueryPlan plan = Nested();
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(plan);

        using JsonDocument file = JsonDocument.Parse(PlanSpeedscope.Build(plan, "x", "e"));
        double[] weights = [.. file.RootElement.GetProperty("profiles").EnumerateArray().Single(static x => x.GetProperty("name").GetString() == "Self cost")
            .GetProperty("weights").EnumerateArray().Select(static x => x.GetDouble())];

        frames.Select(static x => x.Own).Should().Equal(weights, "one computation of a node's own value");
        frames.Select(static x => x.Own).Should().Equal(plan.Nodes.Select(static x => x.ExclusiveCost!.Value));
    }

    [Fact]
    public void A_timed_plan_is_weighed_by_time_and_a_plan_without_timing_by_cost()
    {
        QueryPlan timed = QueryPlanReader.Read(
            "[{\"Plan\": {\"Node Type\": \"Sort\", \"Total Cost\": 90, \"Actual Total Time\": 50, \"Actual Loops\": 1, \"Actual Rows\": 1, "
            + "\"Plans\": [{\"Node Type\": \"Seq Scan\", \"Total Cost\": 10, \"Actual Total Time\": 30, \"Actual Loops\": 1, \"Actual Rows\": 1}]}}]").Plan!;

        timed.Timed.Should().BeTrue();
        PlanIcicleLayout.Measure(timed).Should().Be("time");
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(timed);
        frames.Select(static x => x.Inclusive).Should().Equal(50, 30);
        frames.Select(static x => x.Own).Should().Equal([20d, 30d]); // the time of each node less the nodes below it, not its cost
        frames[1].Width.Should().BeApproximately(0.6, 1e-12);

        QueryPlan costed = Nested();
        PlanIcicleLayout.Measure(costed).Should().Be("estimated cost");
        PlanIcicleLayout.Of(costed).Select(static x => x.Inclusive).Should().Equal(100, 30, 10, 20);
    }

    [Fact]
    public void A_plan_with_nothing_to_weigh_has_no_frames()
    {
        PlanIcicleLayout.Of(ByCost(Node("Result", 0))).Should().BeEmpty();
        PlanIcicleLayout.Of(ByCost(Node("Append", 0, Node("Seq Scan", 0)))).Should().BeEmpty();
    }

    [Fact]
    public void A_value_that_is_not_finite_or_is_negative_reads_as_zero()
    {
        QueryPlan plan = Nested();
        PlanNode[] nodes = [.. plan.Nodes];
        nodes[0].ExclusiveCost = double.NaN;
        nodes[1].ExclusiveCost = -5;
        nodes[2].ExclusiveCost = double.PositiveInfinity;
        nodes[3].ExclusiveCost = null;

        PlanIcicleLayout.Of(plan).Should().BeEmpty("nothing is left to weigh");

        nodes[3].ExclusiveCost = 40;
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(plan);

        frames.Select(static x => x.Id).Should().Equal(0, 3); // a node with nothing of its own or below it is as wide as nothing, and is not drawn
        frames.Select(static x => x.Own).Should().Equal(0, 40);
        frames.Select(static x => x.Inclusive).Should().Equal(40, 40);
        frames.All(static x => double.IsFinite(x.Width) && double.IsFinite(x.X)).Should().BeTrue();
    }

    [Fact]
    public void Nodes_narrower_than_the_floor_are_merged_into_one_frame_for_their_parent()
    {
        // 998 of 1000 is drawn; the two children of 1 each are 0.001 wide - under 0.002 - and are one frame of 0.002.
        QueryPlan plan = ByCost(Node("Append", 1000, Node("Seq Scan", 998), Node("Index Scan", 1), Node("Bitmap Heap Scan", 1)));
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(plan);

        frames.Select(static x => x.Id).Should().Equal(0, 1, -1);

        IcicleFrame merged = Frame(frames, -1);
        merged.Merged.Should().Be(2);
        merged.Label.Should().Be("2 small nodes");
        merged.ParentId.Should().Be(0);
        merged.Depth.Should().Be(1);
        merged.Width.Should().BeApproximately(0.002, 1e-12);
        merged.X.Should().BeApproximately(0.998, 1e-12, "after the frame that was drawn");
        merged.Inclusive.Should().Be(2);
        PlanIcicleLayout.MinWidth.Should().Be(0.002);
    }

    [Fact]
    public void A_merged_frame_counts_the_nodes_below_a_narrow_one_and_says_one_node_in_the_singular()
    {
        QueryPlan below = ByCost(Node("Append", 1000, Node("Seq Scan", 998), Node("Sort", 1, Node("Index Scan", 1, Node("Seq Scan", 0)))));
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(below);
        IcicleFrame merged = frames.Single(static x => x.Merged > 0);

        merged.Merged.Should().Be(3, "the narrow Sort, and what is below it that is not drawn either");
        merged.Label.Should().Be("3 small nodes");

        QueryPlan single = ByCost(Node("Append", 1000, Node("Seq Scan", 998), Node("Index Scan", 1)));
        PlanIcicleLayout.Of(single).Single(static x => x.Merged > 0).Label.Should().Be("1 small node");
    }

    [Fact]
    public void Every_parent_with_narrow_children_gets_its_own_merged_frame_with_a_negative_id_of_its_own()
    {
        QueryPlan plan = ByCost(
            Node("Append", 2000,
                Node("Sort", 1000, Node("Seq Scan", 996), Node("Index Scan", 1), Node("Index Scan", 1)),
                Node("Hash", 996),
                Node("Index Scan", 1),
                Node("Index Scan", 1)));
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(plan);

        IcicleFrame[] merged = [.. frames.Where(static x => x.Merged > 0)];
        merged.Select(static x => x.Id).Should().Equal(-1, -2).And.OnlyHaveUniqueItems();
        merged.Select(static x => x.ParentId).Should().Equal(1, 0);
        merged.Select(static x => x.Merged).Should().Equal(2, 2);
        frames.Where(static x => x.Merged == 0).Select(static x => x.Id).Should().OnlyContain(static x => x >= 0);
    }

    [Fact]
    public void A_merged_frame_follows_its_siblings_so_the_row_stays_contiguous()
    {
        QueryPlan plan = ByCost(Node("Append", 1000, Node("Index Scan", 1), Node("Seq Scan", 500), Node("Hash", 497), Node("Index Scan", 1)));
        IReadOnlyList<IcicleFrame> frames = PlanIcicleLayout.Of(plan);

        IcicleFrame[] row = [.. frames.Where(static x => x.Depth == 1)];
        row.Select(static x => x.Id).Should().Equal(2, 3, -1);

        for (int i = 1; i < row.Length; i++)
        {
            row[i].X.Should().BeApproximately(row[i - 1].X + row[i - 1].Width, 1e-12);
        }

        (row[^1].X + row[^1].Width).Should().BeApproximately(0.999, 1e-12, "the root's own part, 1 of 1,000, is the width left over");
    }

    [Theory]
    [InlineData(12.5, true, "12.5 ms")]
    [InlineData(150, true, "150 ms")]
    [InlineData(0.0004, true, "0 ms")]
    [InlineData(1234.5, false, "1,234.50")]
    public void A_value_is_milliseconds_for_a_timed_plan_and_cost_units_otherwise(double value, bool timed, string text) =>
        PlanIcicleLayout.Value(value, timed).Should().Be(text);
}
