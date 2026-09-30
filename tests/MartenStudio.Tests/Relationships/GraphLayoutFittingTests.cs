using MartenStudio.Services.Relationships;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// What DB-6 added to the layout: boxes fitted to the longest title, names cut past the widest box, a
/// layer gap that widens for edge labels, and keys compared ordinally.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="GraphLayoutTests" /> on purpose: that file's checked-in coordinates are the
/// proof that a store of short aliases still draws exactly the picture it always did, and it is held
/// unchanged so that proof means something.
/// </remarks>
public class GraphLayoutFittingTests
{
    /// <summary>
    /// Seventeen characters is what a box has always held, so a store of short aliases keeps the exact
    /// geometry the checked-in coordinates pin; a longer title widens every box, up to the maximum.
    /// </summary>
    [Fact]
    public void The_box_width_fits_the_longest_title_between_the_default_and_the_maximum()
    {
        GraphLayout.FitNodeWidth(["customer", "order"]).Should().Be(GraphLayout.NodeWidth);
        GraphLayout.FitNodeWidth([new string('a', 17)]).Should().Be(GraphLayout.NodeWidth,
            "seventeen characters is exactly what the default box holds");

        GraphLayout.FitNodeWidth(["qrtz_job_details", "qrtz_simprop_triggers"]).Should().Be(208,
            "twenty-one characters at eight units each, plus the marker and the padding");

        GraphLayout.FitNodeWidth([new string('x', 80)]).Should().Be(GraphLayout.MaxNodeWidth);
        GraphLayout.FitNodeWidth([]).Should().Be(GraphLayout.NodeWidth);

        GraphLayout.TitleCapacity(GraphLayout.NodeWidth).Should().Be(17, "where the diagram has always cut an alias");
        GraphLayout.SubtitleCapacity(GraphLayout.NodeWidth).Should().Be(22, "where it has always cut a type name");
        GraphLayout.TitleCapacity(GraphLayout.FitNodeWidth(["qrtz_simprop_triggers"])).Should()
            .BeGreaterThanOrEqualTo("qrtz_simprop_triggers".Length, "the longest Quartz.NET table fits without an ellipsis");
    }

    [Fact]
    public void A_fitted_width_is_the_width_of_every_box_and_still_never_overlaps()
    {
        List<string> ids = [.. Enumerable.Range(0, 30).Select(static i => "table_" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture))];
        List<GraphLink> links = [.. Enumerable.Range(1, 29).Select(i => new GraphLink(ids[i], ids[i / 2]))];

        GraphLayoutResult layout = GraphLayout.Compute(ids, links, nodeWidth: 240);

        layout.Nodes.Should().HaveCount(30).And.OnlyContain(x => x.Width == 240);
        AssertNoOverlaps(layout);

        foreach (GraphNodeBox box in layout.Nodes)
        {
            (box.X + box.Width).Should().BeLessThanOrEqualTo(layout.Width);
        }
    }

    [Fact]
    public void A_width_or_gap_outside_the_range_is_clamped_to_it()
    {
        GraphLayout.Compute(["a"], [], nodeWidth: 10).Nodes.Single().Width.Should().Be(GraphLayout.NodeWidth);
        GraphLayout.Compute(["a"], [], nodeWidth: 9000).Nodes.Single().Width.Should().Be(GraphLayout.MaxNodeWidth);

        GraphLayoutResult wide = GraphLayout.Compute(["a", "b"], [new GraphLink("b", "a")], layerGap: 9000);
        (Box(wide, "b").X - Box(wide, "a").X - GraphLayout.NodeWidth).Should().Be(GraphLayout.MaxLayerGap);
    }

    /// <summary>
    /// Past the widest box a title is cut at the end with an ellipsis, to exactly the characters that fit;
    /// the whole name is the box's tooltip.
    /// </summary>
    [Fact]
    public void A_name_past_the_widest_box_is_cut_at_the_end_with_an_ellipsis()
    {
        const string name = "qrtz_a_table_name_somebody_made_far_too_long_to_draw";
        int capacity = GraphLayout.TitleCapacity(GraphLayout.MaxNodeWidth);

        string cut = GraphLayout.Ellipsis(name, capacity);

        cut.Should().HaveLength(capacity);
        cut.Should().EndWith("…");
        cut.Should().StartWith(name[..(capacity - 1)]);

        GraphLayout.Ellipsis("short", capacity).Should().Be("short", "a name that fits is left alone");
    }

    /// <summary>
    /// Two quoted identifiers that differ only by case are two tables in Postgres, so they are two nodes
    /// here - a case-insensitive key would have folded them into one box and drawn the key between them as
    /// a loop.
    /// </summary>
    [Fact]
    public void Two_keys_that_differ_only_by_case_are_two_nodes()
    {
        GraphLayoutResult layout = GraphLayout.Compute(
            ["legacy\0Orders", "legacy\0orders"],
            [new GraphLink("legacy\0Orders", "legacy\0orders")]);

        layout.Nodes.Should().HaveCount(2);
        GraphEdgePath edge = layout.Edges.Should().ContainSingle().Subject;
        edge.IsSelfLoop.Should().BeFalse("they are two tables, and the key runs between them");
        AssertNoOverlaps(layout);
    }

    [Fact]
    public void The_layer_gap_widens_only_for_labels_and_only_as_far_as_the_maximum()
    {
        GraphLayout.FitLayerGap([null, null]).Should().Be(GraphLayout.LayerGap,
            "a picture with nothing written on its edges keeps the geometry it always had");

        GraphLayout.FitLayerGap(["id"]).Should().Be(GraphLayout.LayerGap);

        double gap = GraphLayout.FitLayerGap(["job_name, job_group"]);
        gap.Should().BeGreaterThan(GraphLayout.LayerGap);
        GraphLayout.LabelCapacity(gap).Should().BeGreaterThanOrEqualTo("job_name, job_group".Length);

        GraphLayout.FitLayerGap([new string('c', 200)]).Should().Be(GraphLayout.MaxLayerGap);
    }

    [Fact]
    public void A_wider_gap_moves_the_second_layer_right_by_exactly_the_difference()
    {
        GraphLayoutResult narrow = GraphLayout.Compute(["customer", "order"], [new GraphLink("order", "customer")]);
        GraphLayoutResult wide = GraphLayout.Compute(
            ["customer", "order"], [new GraphLink("order", "customer")], layerGap: GraphLayout.LayerGap + 50);

        Box(wide, "customer").X.Should().Be(Box(narrow, "customer").X);
        Box(wide, "order").X.Should().Be(Box(narrow, "order").X + 50);
    }

    private static void AssertNoOverlaps(GraphLayoutResult layout)
    {
        for (int i = 0; i < layout.Nodes.Count; i++)
        {
            for (int j = i + 1; j < layout.Nodes.Count; j++)
            {
                layout.Nodes[i].Overlaps(layout.Nodes[j]).Should().BeFalse(
                    "'{0}' and '{1}' must not share any area", layout.Nodes[i].Alias, layout.Nodes[j].Alias);
            }
        }
    }

    private static GraphNodeBox Box(GraphLayoutResult layout, string key) =>
        layout.Nodes.FirstOrDefault(x => x.Alias == key)
        ?? throw new InvalidOperationException($"The layout has no box for '{key}'.");
}
