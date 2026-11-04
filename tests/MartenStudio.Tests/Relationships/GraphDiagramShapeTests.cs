using Bunit;

using MartenStudio.Tests.Components;

using PostgresStudio.Components.Pages.Relationships;
using PostgresStudio.Services.Relationships;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// 1.2, M6: the two neutral additions to the diagram (a pill shape and a per-node class) change nothing for a model that uses
/// neither, so Relationships and the neighbourhood graph are what they were.
/// </summary>
public class GraphDiagramShapeTests
{
    private static GraphDiagramNode Node(string key, GraphNodeShape shape, bool emphasis = false, string? cssClass = null) =>
        new(key, key, "subtitle", null, 100, shape, "#" + key, key, emphasis, cssClass);

    private static IRenderedComponent<GraphDiagram> Draw(StudioComponentContext context, params GraphDiagramNode[] nodes)
    {
        GraphDiagramModel model = new(nodes, [], "a picture");

        return context.Render<GraphDiagram>(parameters => parameters.Add(x => x.Model, model));
    }

    [Fact]
    public void A_pill_rounds_its_ends_by_half_its_height_and_wears_the_pill_class()
    {
        using var context = new StudioComponentContext();
        IRenderedComponent<GraphDiagram> picture = Draw(context, Node("e", GraphNodeShape.Pill));

        AngleSharp.Dom.IElement box = picture.Find(".ms-graph-node-box");
        double height = double.Parse(box.GetAttribute("height")!, System.Globalization.CultureInfo.InvariantCulture);

        box.GetAttribute("rx").Should().Be((height / 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        box.GetAttribute("ry").Should().Be(box.GetAttribute("rx"));
        picture.Find("a.ms-graph-node").ClassList.Should().Contain("ms-graph-node-pill");
        picture.FindAll("circle.ms-graph-node-dot").Should().ContainSingle("a pill's marker is the circle");
    }

    [Fact]
    public void A_css_class_is_appended_to_the_nodes_class_attribute()
    {
        using var context = new StudioComponentContext();
        IRenderedComponent<GraphDiagram> picture = Draw(context, 
            Node("a", GraphNodeShape.Rounded, cssClass: "ms-flow-node-source"),
            Node("b", GraphNodeShape.Pill, emphasis: true, cssClass: "ms-flow-node-own"));

        picture.FindAll("a.ms-graph-node").Select(static x => x.GetAttribute("class")).Should().Equal(
            "ms-graph-node ms-flow-node-source",
            "ms-graph-node ms-graph-node-pill ms-graph-node-emphasis ms-flow-node-own");
    }

    [Fact]
    public void A_model_that_sets_neither_renders_exactly_the_classes_and_radii_it_always_did()
    {
        using var context = new StudioComponentContext();
        IRenderedComponent<GraphDiagram> picture = Draw(context, 
            Node("document", GraphNodeShape.Rounded),
            Node("table", GraphNodeShape.Square),
            Node("centre", GraphNodeShape.Rounded, emphasis: true));

        IReadOnlyList<AngleSharp.Dom.IElement> nodes = picture.FindAll("a.ms-graph-node");

        nodes[0].GetAttribute("class").Should().Be("ms-graph-node ");
        nodes[0].QuerySelector(".ms-graph-node-box")!.GetAttribute("rx").Should().Be("8");
        nodes[1].GetAttribute("class").Should().Be("ms-graph-node ms-graph-node-table");
        nodes[1].QuerySelector(".ms-graph-node-box")!.GetAttribute("rx").Should().Be("0");
        nodes[2].GetAttribute("class").Should().Be("ms-graph-node  ms-graph-node-emphasis");
    }
}
