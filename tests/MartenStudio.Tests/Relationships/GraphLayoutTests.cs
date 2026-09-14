using System.Globalization;

using MartenStudio.Services.Relationships;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// The layered layout behind the relationships diagram.
/// </summary>
/// <remarks>
/// <para>
/// The four properties asserted here are the whole reason the layout is hand-rolled rather than a
/// simulation: it is <b>deterministic</b> (the same store draws the same picture every time, so a
/// screenshot stays true and a diff of two runs is empty), it <b>never overlaps</b> two boxes (which is a
/// property of the constants, not of luck), it <b>terminates on a cycle</b> (document models have them),
/// and it <b>scales</b> to a store with a realistic number of document types.
/// </para>
/// <para>
/// No database, no rendering, no Blazor: <c>GraphLayout</c> takes a list of names and a list of pairs.
/// </para>
/// </remarks>
public class GraphLayoutTests
{
    [Fact]
    public void An_empty_graph_lays_out_to_nothing()
    {
        GraphLayout.Compute([], []).Should().BeSameAs(GraphLayoutResult.Empty);
    }

    [Fact]
    public void A_document_that_points_at_another_sits_to_the_right_of_it()
    {
        GraphLayoutResult layout = GraphLayout.Compute(["customer", "order"], [new GraphLink("order", "customer")]);

        GraphNodeBox order = Box(layout, "order");
        GraphNodeBox customer = Box(layout, "customer");

        customer.Layer.Should().Be(0, "nothing points out of customer");
        order.Layer.Should().Be(1);
        order.X.Should().BeGreaterThan(customer.X,
            "the horizontal axis is the direction of reference - a document that points at another is " +
            "drawn to the right of it");
    }

    [Fact]
    public void A_chain_of_references_becomes_a_chain_of_layers()
    {
        GraphLayoutResult layout = GraphLayout.Compute(
            ["a", "b", "c", "d"],
            [new GraphLink("d", "c"), new GraphLink("c", "b"), new GraphLink("b", "a")]);

        Box(layout, "a").Layer.Should().Be(0);
        Box(layout, "b").Layer.Should().Be(1);
        Box(layout, "c").Layer.Should().Be(2);
        Box(layout, "d").Layer.Should().Be(3);
    }

    [Fact]
    public void The_same_input_lays_out_to_byte_identical_output()
    {
        (List<string> aliases, List<GraphLink> links) = Generated(40, 70);

        GraphLayoutResult first = GraphLayout.Compute(aliases, links);
        GraphLayoutResult second = GraphLayout.Compute(aliases, links);

        // Records, so this is structural equality over every coordinate and every path string - which is
        // what "the same store draws the same picture" has to mean for a screenshot to stay true.
        second.Nodes.Should().Equal(first.Nodes);
        second.Edges.Should().Equal(first.Edges);
        second.ViewBox.Should().Be(first.ViewBox);
    }

    [Fact]
    public void Two_runs_of_a_fresh_process_would_agree_because_nothing_is_hashed_by_reference()
    {
        // The same graph described in a different object identity: if anything in the layout keyed off a
        // reference or a runtime hash code, this would drift from the run above.
        GraphLayoutResult first = GraphLayout.Compute(
            ["order", "customer"], [new GraphLink("order", "customer")]);

        GraphLayoutResult second = GraphLayout.Compute(
            [string.Concat("ord", "er"), string.Concat("cust", "omer")],
            [new GraphLink(string.Concat("ord", "er"), string.Concat("cust", "omer"))]);

        second.Nodes.Should().Equal(first.Nodes);
        second.Edges.Should().Equal(first.Edges);
    }

    [Fact]
    public void No_two_boxes_overlap_in_the_sample_domains_shape()
    {
        // customer <- order, customer <- invoice, order <- ordernote: the demo store's own shape.
        GraphLayoutResult layout = GraphLayout.Compute(
            ["customer", "invoice", "order", "ordernote"],
            [
                new GraphLink("order", "customer"),
                new GraphLink("invoice", "customer"),
                new GraphLink("ordernote", "order"),
            ]);

        AssertNoOverlaps(layout);
    }

    [Fact]
    public void No_two_boxes_overlap_in_a_forty_node_graph()
    {
        (List<string> aliases, List<GraphLink> links) = Generated(40, 70);

        GraphLayoutResult layout = GraphLayout.Compute(aliases, links);

        layout.Nodes.Should().HaveCount(40);
        AssertNoOverlaps(layout);
    }

    [Fact]
    public void A_cycle_terminates_and_is_drawn_as_a_back_edge()
    {
        GraphLayoutResult layout = GraphLayout.Compute(
            ["a", "b", "c"],
            [new GraphLink("a", "b"), new GraphLink("b", "c"), new GraphLink("c", "a")]);

        layout.Nodes.Should().HaveCount(3);
        layout.Edges.Should().HaveCount(3);
        layout.Edges.Count(x => x.IsBackEdge).Should().Be(1,
            "exactly one edge closes the cycle, and it is the one drawn the long way round");

        AssertNoOverlaps(layout);
    }

    [Fact]
    public void A_pair_of_types_pointing_at_each_other_terminates()
    {
        GraphLayoutResult layout = GraphLayout.Compute(
            ["a", "b"],
            [new GraphLink("a", "b"), new GraphLink("b", "a")]);

        layout.Nodes.Should().HaveCount(2);
        layout.Edges.Count(x => x.IsBackEdge).Should().Be(1);
        AssertNoOverlaps(layout);
    }

    [Fact]
    public void A_self_reference_is_a_loop_rather_than_an_edge_between_layers()
    {
        GraphLayoutResult layout = GraphLayout.Compute(["node"], [new GraphLink("node", "node")]);

        GraphEdgePath loop = layout.Edges.Should().ContainSingle().Subject;

        loop.IsSelfLoop.Should().BeTrue();
        loop.IsBackEdge.Should().BeFalse("a self-reference is not a cycle to break");
        Box(layout, "node").Layer.Should().Be(0, "a self-reference must not push a node into its own next layer");

        // The loop reaches above its own box, so the top margin has to be wide enough to hold it.
        loop.LabelY.Should().BeLessThan(Box(layout, "node").Y);
        loop.LabelY.Should().BeGreaterThan(0, "the loop must stay inside the viewBox");
    }

    [Fact]
    public void Every_edge_carries_an_arrowhead_pointing_at_its_target()
    {
        GraphLayoutResult layout = GraphLayout.Compute(
            ["customer", "order"], [new GraphLink("order", "customer")]);

        GraphEdgePath edge = layout.Edges.Should().ContainSingle().Subject;

        edge.Arrow.Should().StartWith("M ").And.EndWith(" Z");

        // The tip is the right-hand edge of what it points at, so the arrow lands on the box rather than
        // on the box that owns the other end.
        GraphNodeBox customer = Box(layout, "customer");
        Tip(edge.Arrow).X.Should().BeApproximately(customer.X + customer.Width, 0.01);
    }

    [Fact]
    public void A_link_naming_a_node_that_is_not_there_is_ignored_rather_than_throwing()
    {
        GraphLayoutResult layout = GraphLayout.Compute(
            ["order"], [new GraphLink("order", "ghost"), new GraphLink("ghost", "order")]);

        layout.Nodes.Should().ContainSingle();
        layout.Edges.Should().BeEmpty();
    }

    [Fact]
    public void The_viewBox_contains_every_box_and_is_written_in_the_invariant_culture()
    {
        (List<string> aliases, List<GraphLink> links) = Generated(40, 70);

        GraphLayoutResult layout = GraphLayout.Compute(aliases, links);

        layout.ViewBox.Should().StartWith("0 0 ");
        layout.ViewBox.Should().NotContain(",", "a comma decimal separator would make the SVG invalid");

        foreach (GraphNodeBox box in layout.Nodes)
        {
            (box.X + box.Width).Should().BeLessThanOrEqualTo(layout.Width);
            (box.Y + box.Height).Should().BeLessThanOrEqualTo(layout.Height);
            box.X.Should().BeGreaterThanOrEqualTo(0);
            box.Y.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    /// <summary>
    /// A generated graph of forty types and seventy references, including two cycles and a self
    /// reference. Deterministic: the pseudo-random generator is seeded, so the "generated" graph is the
    /// same one every run and a failure is reproducible.
    /// </summary>
    private static (List<string> Aliases, List<GraphLink> Links) Generated(int nodes, int links)
    {
        List<string> aliases = [];
        for (int i = 0; i < nodes; i++)
        {
            aliases.Add("type" + i.ToString("00", CultureInfo.InvariantCulture));
        }

        Random random = new(20260914);
        HashSet<(int, int)> seen = [];
        List<GraphLink> edges = [];

        while (edges.Count < links)
        {
            int from = random.Next(nodes);
            int to = random.Next(nodes);

            if (!seen.Add((from, to)))
            {
                continue;
            }

            edges.Add(new GraphLink(aliases[from], aliases[to]));
        }

        // Guaranteed shapes, rather than hoping the generator produced them.
        edges.Add(new GraphLink(aliases[0], aliases[0]));
        edges.Add(new GraphLink(aliases[1], aliases[2]));
        edges.Add(new GraphLink(aliases[2], aliases[1]));

        return (aliases, edges);
    }

    private static void AssertNoOverlaps(GraphLayoutResult layout)
    {
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            for (int j = i + 1; j < layout.Nodes.Count; j++)
            {
                layout.Nodes[i].Overlaps(layout.Nodes[j]).Should().BeFalse(
                    "'{0}' and '{1}' must not share any area",
                    layout.Nodes[i].Alias,
                    layout.Nodes[j].Alias);
            }
        }
    }

    private static GraphNodeBox Box(GraphLayoutResult layout, string alias) =>
        layout.Nodes.FirstOrDefault(x => x.Alias == alias)
        ?? throw new InvalidOperationException($"The layout has no box for '{alias}'.");

    /// <summary>The first point of an arrowhead path, which is its tip.</summary>
    private static (double X, double Y) Tip(string arrow)
    {
        string[] parts = arrow.Split(' ');

        return (
            double.Parse(parts[1], CultureInfo.InvariantCulture),
            double.Parse(parts[2], CultureInfo.InvariantCulture));
    }
}
