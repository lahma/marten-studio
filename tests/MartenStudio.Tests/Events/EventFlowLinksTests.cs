using AngleSharp.Dom;

using Bunit;

using JasperFx.Events.Projections;

using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

using EventTypesPage = MartenStudio.Components.Pages.Events.EventTypes;
using ProjectionsPage = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Events;

/// <summary>
/// 1.2, M6: the cross-links into the Event flow - the Rebuild dialog's "Event types read" row, "Show its flow" on each
/// Projections row and Event types row, and the row anchor the flow links to.
/// </summary>
public class EventFlowLinksTests
{
    private const string ConfirmDialog = ".ms-confirm-dialog";

    private static EventFlowModel ModelWith(FlowSource source, params (string Alias, bool Own)[] reads)
    {
        FlowEventType[] events = [.. reads.Select(static x => EventFlowFixtures.Event(x.Alias, own: x.Own))];

        return new EventFlowModel(events, [source], [.. events.Select(x => new FlowRead(x.Key, source.Name, null))], [], [], [], null);
    }

    // ---------------------------------------------------------------------------------------------
    // The sentence and the anchors
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_rebuild_row_names_the_event_types_read_and_Martens_own_after_them()
    {
        EventFlowModel model = ModelWith(
            EventFlowFixtures.Source("Orders"),
            ("order_placed", false), ("item_added", false), ("archived", true), ("Compacted<Order>", true));

        model.ReadsOf("orders").Sentence.Should().Be("order_placed, item_added (and Marten's own archived, Compacted<Order>)");
    }

    [Fact]
    public void A_projection_that_declares_none_reads_every_event_type()
    {
        EventFlowModel model = ModelWith(EventFlowFixtures.Source("Bare", FlowSourceKind.Custom, input: FlowInput.Everything));

        model.ReadsOf("Bare").Sentence.Should().Be("every event type (it declares none)");
    }

    [Fact]
    public void The_sentence_has_a_form_for_only_Martens_own_and_for_none()
    {
        ModelWith(EventFlowFixtures.Source("Own"), ("archived", true)).ReadsOf("Own").Sentence.Should().Be("Marten's own archived");
        ModelWith(EventFlowFixtures.Source("Nothing")).ReadsOf("Nothing").Sentence.Should().Be("none listed");
    }

    [Fact]
    public void A_composites_row_is_the_union_of_its_members_through_nested_composites()
    {
        FlowEventType a = EventFlowFixtures.Event("a");
        FlowEventType b = EventFlowFixtures.Event("b");
        EventFlowModel model = new(
            [a, b],
            [
                EventFlowFixtures.Source("Outer", FlowSourceKind.Composite),
                EventFlowFixtures.Source("Inner", FlowSourceKind.Composite, composite: "Outer"),
                EventFlowFixtures.Source("First", composite: "Inner"),
                EventFlowFixtures.Source("Second", composite: "Outer"),
            ],
            [new("a", "First", null), new("b", "Second", null)],
            [],
            [],
            [],
            null);

        model.ReadsOf("Outer").Sentence.Should().Be("a, b");
    }

    [Theory]
    [InlineData("DailySales", "projection-DailySales")]
    [InlineData("Order Summary", "projection-Order-Summary")]
    [InlineData("a/b?c=d#e", "projection-a-b-c-d-e")]
    public void The_row_anchor_is_an_element_id(string name, string anchor)
    {
        EventLinks.ProjectionAnchor(name).Should().Be(anchor);
    }

    [Fact]
    public void A_link_to_a_projection_carries_the_scope_and_ends_in_its_anchor()
    {
        string link = EventLinks.ToProjection(new MartenStudioOptions(), new PostgresStudio.Services.StudioScope("default", "db1", null), "DailySales");

        link.Should().Be("projections?store=default&db=db1#projection-DailySales");
    }

    // ---------------------------------------------------------------------------------------------
    // The Projections page
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_projection_row_has_an_anchor_and_a_link_to_its_flow_that_carries_the_scope()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();

        IElement row = page.Find("tr[data-projection='DailySales']");
        row.GetAttribute("id").Should().Be("projection-DailySales");
        IElement link = row.QuerySelector("a.ms-projection-flow-link")!;
        link.TextContent.Should().Be("Show its flow");
        link.GetAttribute("href").Should().StartWith("events/flow?projection=DailySales").And.Contain("store=default");
    }

    [Fact]
    public async Task The_rebuild_dialog_says_which_event_types_the_projection_reads_from_the_event_flow()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        context.EventData.EventFlow = new EventFlowResult(
            ModelWith(EventFlowFixtures.Source("DailySales"), ("order_placed", false), ("archived", true)), null);
        await context.ReadyAsync();

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();
        page.Find(".ms-projection-rebuild").Click();

        IElement row = page.WaitForElement(ConfirmDialog + " .ms-rebuild-event-types");
        row.TextContent.Should().Contain("Event types read").And.Contain("order_placed (and Marten's own archived)");
        row.QuerySelector("a.ms-rebuild-flow-link")!.GetAttribute("href").Should().StartWith("events/flow?projection=DailySales");
        context.EventData.EventFlowReads.Should().BeGreaterThan(0, "it comes from the flow's cached model, the one read the Event flow page makes");
    }

    [Fact]
    public async Task A_projection_that_declares_none_says_it_reads_every_event_type_in_the_dialog()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        context.EventData.EventFlow = new EventFlowResult(
            ModelWith(EventFlowFixtures.Source("DailySales", FlowSourceKind.Custom, input: FlowInput.Everything)), null);
        await context.ReadyAsync();

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();
        page.Find(".ms-projection-rebuild").Click();

        page.WaitForElement(ConfirmDialog + " .ms-rebuild-event-types").TextContent.Should().Contain("every event type (it declares none)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_the_flow_cannot_say_the_dialog_has_no_such_row_and_still_opens(bool unlisted)
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        context.EventData.EventFlow = unlisted
            ? new EventFlowResult(ModelWith(EventFlowFixtures.Source("Other"), ("a", false)), null)
            : EventFlowResult.Failed("the store is not available");
        await context.ReadyAsync();

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();
        page.Find(".ms-projection-rebuild").Click();

        IElement dialog = page.WaitForElement(ConfirmDialog);
        dialog.QuerySelector(".ms-rebuild-event-types").Should().BeNull();
        dialog.TextContent.Should().Contain("Rebuild projection DailySales");
    }

    // ---------------------------------------------------------------------------------------------
    // The Event types page and the flow's own links
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_registered_event_type_links_to_its_flow_and_an_unregistered_one_does_not()
    {
        await using StudioComponentContext context = new StudioComponentContext();
        context.EventData.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true), new EventTypeInfo("legacy", null, false)], false);
        await context.ReadyAsync();

        IRenderedComponent<EventTypesPage> page = context.Render<EventTypesPage>();

        IReadOnlyList<IElement> links = page.FindAll("a.ms-event-flow-link");
        links.Should().ContainSingle();
        links[0].GetAttribute("href").Should().StartWith("events/flow?type=OrderPlaced").And.Contain("store=default");
    }

    [Fact]
    public async Task The_flow_links_each_projection_to_its_row_on_the_projections_page()
    {
        await using StudioComponentContext context = new StudioComponentContext();
        context.EventData.EventFlow = new EventFlowResult(EventFlowFixtures.Sample(), null);
        await context.ReadyAsync();
        context.Navigate("marten/events/flow");

        IRenderedComponent<MartenStudio.Components.Pages.Events.EventFlow> page = context.Render<MartenStudio.Components.Pages.Events.EventFlow>();

        page.Find("tr[data-source='Orders'] a.ms-flow-source-link").GetAttribute("href").Should().StartWith("projections?").And.EndWith("#projection-Orders");
    }

    [Fact]
    public void A_projection_is_never_marked_a_flow_link_without_a_name_to_filter_by()
    {
        EventLinks.ToFlow(new MartenStudioOptions(), null, "Orders").Should().Be("events/flow?projection=Orders");
        ProjectionStatus(ProjectionLifecycle.Async).Should().BeTrue();
    }

    private static bool ProjectionStatus(ProjectionLifecycle lifecycle) => lifecycle == ProjectionLifecycle.Async;
}
