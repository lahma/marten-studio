using JasperFx.Events.Projections;

using MartenStudio.Services.Events;

using PostgresStudio.Services.Relationships;

namespace MartenStudio.Tests.Events;

/// <summary>
/// 1.2, M6: the event flow's picture - which boxes and arrows a filter draws, in what order, with which ids - as a pure
/// function of the model.
/// </summary>
public class EventFlowDiagramTests
{
    private static readonly EventFlowHrefs Hrefs = new(
        static alias => "events/feed?types=" + alias + "&store=default",
        "events/feed?store=default",
        static _ => "projections?store=default",
        static alias => "documents/" + alias + "?store=default",
        static (schema, name) => "database/object?schema=" + schema + "&name=" + name + "&store=default",
        "events/streams?store=default");

    private static EventFlowDrawing Draw(EventFlowModel model, EventFlowFilter? filter = null) =>
        EventFlowDiagram.Project(model, filter ?? EventFlowFilter.None, Hrefs);

    private static string[] Nodes(EventFlowDrawing drawing) => [.. drawing.Drawn.Nodes.Select(static x => x.Key)];

    private static string[] Edges(EventFlowDrawing drawing) => [.. drawing.Drawn.Edges.Select(static x => x.Id)];

    [Fact]
    public void Unfiltered_it_draws_the_events_something_reads_the_sources_and_what_they_write()
    {
        EventFlowDrawing drawing = Draw(EventFlowFixtures.Sample());

        Nodes(drawing).Should().Equal(
            "e:cancelled", "e:iorder", "e:item_added", "e:order_placed", "e:shipped",
            "p:Daily", "p:Orders", "p:Tracker",
            "d:day", "d:order");
        Nodes(drawing).Should().NotContain("e:ledger_opened", "an event nothing reads is a chip under the picture, never a lone box");
        Nodes(drawing).Should().NotContain("e:archived", "Marten's own events are off until asked for");
        drawing.OverCap.Should().BeFalse();
        drawing.UnknownFilter.Should().BeFalse();
        drawing.SourceCount.Should().Be(3);
        drawing.Drawn.Summary.Should().Be("5 event types feed 3 projections and subscriptions, which write 2 document types, tables and live aggregates.");
    }

    [Fact]
    public void Marten_own_events_are_drawn_on_request_and_in_the_own_style()
    {
        EventFlowDrawing drawing = Draw(EventFlowFixtures.Sample(), new EventFlowFilter(null, null, IncludeOwn: true));

        Nodes(drawing).Should().Contain("e:archived");
        GraphDiagramNode own = drawing.Drawn.Nodes.Single(static x => x.Key == "e:archived");
        own.CssClass.Should().Be("ms-flow-node-own");
        own.Shape.Should().Be(GraphNodeShape.Pill);
        Edges(drawing).Should().Contain("r:archived|Orders");
    }

    [Fact]
    public void Event_types_are_pills_sources_are_rounded_documents_are_rounded_and_tables_are_square()
    {
        EventFlowModel model = EventFlowFixtures.Sample() with
        {
            Outputs = [.. EventFlowFixtures.Sample().Outputs, EventFlowFixtures.Table("public", "flow_table"), EventFlowFixtures.Live("Live")],
            Writes = [.. EventFlowFixtures.Sample().Writes, new("Tracker", "t:public.flow_table", null), new("Orders", "a:Sample.Live", null)],
        };

        EventFlowDrawing drawing = Draw(model);

        drawing.Drawn.Nodes.Single(static x => x.Key == "e:order_placed").Shape.Should().Be(GraphNodeShape.Pill);
        drawing.Drawn.Nodes.Single(static x => x.Key == "e:order_placed").CssClass.Should().BeNull();
        GraphDiagramNode source = drawing.Drawn.Nodes.Single(static x => x.Key == "p:Orders");
        source.Shape.Should().Be(GraphNodeShape.Rounded);
        source.CssClass.Should().Be("ms-flow-node-source");
        source.Subtitle.Should().Be("Single stream");
        source.Detail.Should().Be("Inline");
        source.Tooltip.Should().Be("Orders - Single stream · Inline");
        drawing.Drawn.Nodes.Single(static x => x.Key == "d:order").Shape.Should().Be(GraphNodeShape.Rounded);
        GraphDiagramNode table = drawing.Drawn.Nodes.Single(static x => x.Key == "t:public.flow_table");
        table.Shape.Should().Be(GraphNodeShape.Square);
        table.Href.Should().Be("database/object?schema=public&name=flow_table&store=default");
        GraphDiagramNode live = drawing.Drawn.Nodes.Single(static x => x.Key == "a:Sample.Live");
        live.CssClass.Should().Be("ms-flow-node-live");
        live.Href.Should().Be("events/streams?store=default");
        drawing.Drawn.Edges.Single(static x => x.Id == "w:Orders|a:Sample.Live").CssClass.Should().Be("ms-flow-edge-folds");
        drawing.Styles.Should().Equal("reads", "via", "writes", "folds");
    }

    [Fact]
    public void A_projection_filter_draws_that_source_what_it_reads_and_what_it_writes()
    {
        EventFlowDrawing drawing = Draw(EventFlowFixtures.Sample(), new EventFlowFilter("daily", null, false));

        Nodes(drawing).Should().Equal("e:cancelled", "e:iorder", "e:item_added", "e:order_placed", "e:shipped", "p:Daily", "d:day");
        Edges(drawing).Should().Contain("r:shipped|Daily").And.Contain("w:Daily|d:day");
        Edges(drawing).Should().NotContain("r:shipped|Orders");
    }

    [Fact]
    public void An_event_type_filter_draws_that_type_every_source_that_reads_it_and_their_outputs()
    {
        EventFlowDrawing drawing = Draw(EventFlowFixtures.Sample(), new EventFlowFilter(null, "shipped", false));

        Nodes(drawing).Should().Equal("e:shipped", "p:Daily", "p:Orders", "p:Tracker", "d:day", "d:order");
        Edges(drawing).Should().BeEquivalentTo(["r:shipped|Orders", "r:shipped|Daily", "r:shipped|Tracker", "w:Orders|d:order", "w:Daily|d:day"]);
    }

    [Fact]
    public void Both_filters_draw_the_intersection()
    {
        EventFlowDrawing drawing = Draw(EventFlowFixtures.Sample(), new EventFlowFilter("Orders", "shipped", false));

        Nodes(drawing).Should().Equal("e:shipped", "p:Orders", "d:order");

        Draw(EventFlowFixtures.Sample(), new EventFlowFilter("Orders", "ledger_opened", false)).Drawn.Nodes
            .Select(static x => x.Key).Should().Equal(["e:ledger_opened"], "nothing reads it, so only the type the filter named");
    }

    [Theory]
    [InlineData("nobody", null)]
    [InlineData(null, "no_such_event")]
    [InlineData("Orders", "no_such_event")]
    public void A_value_this_store_does_not_have_draws_nothing_and_says_so(string? projection, string? type)
    {
        EventFlowDrawing drawing = Draw(EventFlowFixtures.Sample(), new EventFlowFilter(projection, type, false));

        drawing.UnknownFilter.Should().BeTrue();
        drawing.Drawn.Nodes.Should().BeEmpty();
    }

    [Fact]
    public void A_via_edge_is_dashed_and_labelled_with_the_declared_base_type()
    {
        EventFlowDrawing drawing = Draw(EventFlowFixtures.Sample());

        GraphDiagramEdge via = drawing.Drawn.Edges.Single(static x => x.Id == "r:shipped|Daily");
        via.CssClass.Should().Be("ms-flow-edge-via");
        via.Label.Should().Be("as IORDER");
        drawing.Drawn.Edges.Single(static x => x.Id == "r:shipped|Orders").Label.Should().BeNull("only a via edge and a subclass write carry a label");
    }

    [Fact]
    public void A_source_that_declares_nothing_hangs_off_the_every_event_box()
    {
        EventFlowModel model = new(
            [EventFlowFixtures.Event("a"), EventFlowFixtures.Event("b")],
            [EventFlowFixtures.Source("Bare", FlowSourceKind.Custom, input: FlowInput.Everything), EventFlowFixtures.Source("Named")],
            [new("a", "Named", null)],
            [],
            [],
            [],
            null);

        EventFlowDrawing drawing = Draw(model);

        Nodes(drawing).Should().Equal("e:*", "e:a", "p:Bare", "p:Named");
        GraphDiagramNode every = drawing.Drawn.Nodes[0];
        every.Title.Should().Be("Every event type");
        every.CssClass.Should().Be("ms-flow-node-every");
        Edges(drawing).Should().BeEquivalentTo(["r:*|Bare", "r:a|Named"]);
        drawing.Drawn.Edges.Single(static x => x.Id == "r:*|Bare").CssClass.Should().Be("ms-flow-edge-every");
        drawing.Styles.Should().Equal("reads", "every");
        drawing.Drawn.Nodes.Single(static x => x.Key == "p:Bare").Detail.Should().Be("Async · every event");
    }

    [Fact]
    public void A_type_filter_keeps_a_source_that_reads_everything_and_the_every_event_box()
    {
        EventFlowModel model = new(
            [EventFlowFixtures.Event("a"), EventFlowFixtures.Event("b")],
            [EventFlowFixtures.Source("Bare", FlowSourceKind.Custom, input: FlowInput.Everything), EventFlowFixtures.Source("Named")],
            [new("a", "Named", null)],
            [],
            [],
            [],
            null);

        EventFlowDrawing drawing = Draw(model, new EventFlowFilter(null, "b", false));

        Nodes(drawing).Should().Equal("e:*", "e:b", "p:Bare");
    }

    [Fact]
    public void A_composites_name_selects_its_members_and_the_composite_is_never_a_box()
    {
        EventFlowModel model = new(
            [EventFlowFixtures.Event("a"), EventFlowFixtures.Event("b")],
            [
                EventFlowFixtures.Source("Bundle", FlowSourceKind.Composite),
                EventFlowFixtures.Source("First", composite: "Bundle"),
                EventFlowFixtures.Source("Second", composite: "Bundle"),
                EventFlowFixtures.Source("Other"),
            ],
            [new("a", "First", null), new("b", "Second", null), new("a", "Other", null)],
            [],
            [],
            [],
            null);

        Nodes(Draw(model)).Should().NotContain("p:Bundle");
        Nodes(Draw(model, new EventFlowFilter("bundle", null, false))).Should().Equal("e:a", "e:b", "p:First", "p:Second");
    }

    [Fact]
    public void Writes_to_one_output_through_several_subclasses_are_one_arrow_with_one_label()
    {
        EventFlowModel model = new(
            [EventFlowFixtures.Event("a")],
            [EventFlowFixtures.Source("Fleet")],
            [new("a", "Fleet", null)],
            [new("Fleet", "d:vehicle", "Car"), new("Fleet", "d:vehicle", "Truck")],
            [EventFlowFixtures.Document("vehicle", "Vehicle")],
            [],
            null);

        EventFlowDrawing drawing = Draw(model);

        GraphDiagramEdge write = drawing.Drawn.Edges.Single(static x => x.Id == "w:Fleet|d:vehicle");
        write.Label.Should().Be("as Car, Truck");
        write.CssClass.Should().Be("ms-flow-edge-writes");
    }

    [Fact]
    public void Over_the_cap_the_picture_is_flagged_and_the_filter_that_narrows_it_is_not()
    {
        FlowEventType[] events = [.. Enumerable.Range(0, 58).Select(static i => EventFlowFixtures.Event("e" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)))];
        EventFlowModel model = new(
            events,
            [EventFlowFixtures.Source("S1")],
            [.. events.Select(static x => new FlowRead(x.Key, "S1", null))],
            [],
            [],
            [],
            null);

        EventFlowDrawing all = Draw(model);
        all.DrawnNodes.Should().Be(58 + 1);
        all.OverCap.Should().BeFalse("59 boxes are drawn");

        EventFlowModel more = model with
        {
            EventTypes = [.. events, EventFlowFixtures.Event("x1"), EventFlowFixtures.Event("x2")],
            Reads = [.. model.Reads, new FlowRead("x1", "S1", null), new FlowRead("x2", "S1", null)],
        };

        EventFlowDrawing over = Draw(more);
        over.DrawnNodes.Should().Be(61, "60 event types and one source");
        over.OverCap.Should().BeTrue("61 boxes is one more than the diagram draws");

        Draw(more, new EventFlowFilter(null, "x1", false)).OverCap.Should().BeFalse();
    }

    [Fact]
    public void Edge_ids_are_stable_and_every_edge_names_boxes_that_are_drawn()
    {
        EventFlowDrawing drawing = Draw(EventFlowFixtures.Sample(), new EventFlowFilter(null, null, true));

        HashSet<string> nodes = [.. Nodes(drawing)];
        drawing.Drawn.Edges.Should().OnlyContain(x => nodes.Contains(x.From) && nodes.Contains(x.To));
        Edges(drawing).Should().OnlyHaveUniqueItems();
        Edges(drawing).Should().OnlyContain(static x => x.StartsWith("r:", StringComparison.Ordinal) || x.StartsWith("w:", StringComparison.Ordinal));
        EventFlowDiagram.ReadEdgeId("k", "S").Should().Be("r:k|S");
        EventFlowDiagram.EveryEdgeId("S").Should().Be("r:*|S");
        EventFlowDiagram.WriteEdgeId("S", "d:x").Should().Be("w:S|d:x");
    }

    [Fact]
    public void Nodes_come_in_the_layouts_tie_breaking_order()
    {
        EventFlowModel model = new(
            [EventFlowFixtures.Event("beta"), EventFlowFixtures.Event("Alpha")],
            [EventFlowFixtures.Source("zeta"), EventFlowFixtures.Source("Mu", lifecycle: ProjectionLifecycle.Inline)],
            [new("beta", "zeta", null), new("Alpha", "Mu", null)],
            [new("zeta", "d:b", null), new("Mu", "d:a", null)],
            [EventFlowFixtures.Document("b", "B"), EventFlowFixtures.Document("a", "A")],
            [],
            null);

        Nodes(Draw(model)).Should().Equal("e:Alpha", "e:beta", "p:Mu", "p:zeta", "d:a", "d:b");
    }

    [Fact]
    public void The_links_are_the_ones_the_page_handed_in()
    {
        EventFlowDrawing drawing = Draw(EventFlowFixtures.Sample());

        drawing.Drawn.Nodes.Single(static x => x.Key == "e:shipped").Href.Should().Be("events/feed?types=shipped&store=default");
        drawing.Drawn.Nodes.Single(static x => x.Key == "p:Orders").Href.Should().Be("projections?store=default");
        drawing.Drawn.Nodes.Single(static x => x.Key == "d:order").Href.Should().Be("documents/order?store=default");
    }
}
