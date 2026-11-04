using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using MartenStudio.Components.Pages.Projections;

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
        context.EventData.EventFlowReads.Should().Be(1, "it comes from the flow's cached model, read once for the one dialog opened");
        context.EventData.EventFlowScopes.Single().Should().Be(context.Services.GetRequiredService<PostgresStudio.Services.StudioState>().ActiveScope, "the scope the page is on");
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
    public async Task A_flow_link_is_drawn_only_when_the_page_gave_one()
    {
        await using StudioComponentContext context = new StudioComponentContext();

        foreach (string? href in new string?[] { null, string.Empty })
        {
            IRenderedComponent<ProjectionRow> row = context.Render<ProjectionRow>(parameters => parameters
                .Add(x => x.Name, "DailySales")
                .Add(x => x.Lifecycle, "Async")
                .Add(x => x.FlowHref, href));

            row.FindAll("a.ms-projection-flow-link").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Opening_the_projections_page_does_not_read_the_event_flow()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();
        page.WaitForElement("tr[data-projection='DailySales']");

        context.EventData.EventFlowReads.Should().Be(0, "the flow is read when a Rebuild dialog opens, never to draw the rows");
    }

    [Fact]
    public async Task The_row_header_is_named_by_the_projection_and_its_lifecycle_and_the_link_names_what_it_is_for()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();

        IElement row = page.Find("tr[data-projection='DailySales']");
        IElement header = row.QuerySelector("th")!;
        string[] named = header.GetAttribute("aria-labelledby")!.Split(' ');
        named.Should().HaveCount(2);
        named.Select(id => row.QuerySelector("#" + id)!.TextContent.Trim()).Should().Equal("DailySales", "Async");
        row.QuerySelector("a.ms-projection-flow-link")!.GetAttribute("aria-label").Should().Be("Show its flow: DailySales");
        header.QuerySelector("a.ms-projection-flow-link").Should().NotBeNull("inline after the badges, and outside the header's name");
    }

    [Fact]
    public async Task The_page_scrolls_once_to_the_row_an_address_fragment_names_after_the_rows_are_drawn()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();
        context.Navigate("marten/projections#projection-DailySales");

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();
        page.WaitForElement("tr[data-projection='DailySales']");

        page.WaitForAssertion(() => context.JSInterop.Invocations["martenStudio.scroll.intoView"].Should().ContainSingle()
            .Which.Arguments.Should().Equal("projection-DailySales", "start"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#other-DailySales")]
    public async Task An_address_that_names_no_projection_row_scrolls_nothing(string fragment)
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();
        context.Navigate("marten/projections" + fragment);

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();
        page.WaitForElement("tr[data-projection='DailySales']");

        context.JSInterop.Invocations["martenStudio.scroll.intoView"].Should().BeEmpty();
    }

    [Fact]
    public void The_fragment_is_prefix_checked_and_bounded()
    {
        EventLinks.ProjectionFragment("http://x/marten/projections#projection-A%20b").Should().Be("projection-A b");
        EventLinks.ProjectionFragment("http://x/marten/projections#finding-a").Should().BeNull();
        EventLinks.ProjectionFragment("http://x/marten/projections").Should().BeNull();
        EventLinks.ProjectionFragment("http://x/marten/projections#projection-" + new string('a', 300)).Should().BeNull();
    }

    [Fact]
    public void A_flow_link_for_one_projection_filters_by_its_name_alone()
    {
        EventLinks.ToFlow(new MartenStudioOptions(), null, "Orders").Should().Be("events/flow?projection=Orders");
    }

    [Fact]
    public async Task A_composites_member_links_to_the_composites_row_and_shards_have_no_row_of_their_own()
    {
        EventFlowModel model = new(
            [EventFlowFixtures.Event("a")],
            [
                EventFlowFixtures.Source("Outer", FlowSourceKind.Composite),
                EventFlowFixtures.Source("Inner", FlowSourceKind.Composite, composite: "Outer"),
                EventFlowFixtures.Source("Member", composite: "Inner"),
                EventFlowFixtures.Source("Plain"),
            ],
            [new("a", "Member", null), new("a", "Plain", null)],
            [],
            [],
            [],
            null);
        await using StudioComponentContext context = new StudioComponentContext();
        context.EventData.EventFlow = new EventFlowResult(model, null);
        await context.ReadyAsync();
        context.Navigate("marten/events/flow");

        IRenderedComponent<MartenStudio.Components.Pages.Events.EventFlow> page = context.Render<MartenStudio.Components.Pages.Events.EventFlow>();

        page.Find("tr[data-source='Member'] a.ms-flow-source-link").GetAttribute("href").Should().EndWith("#projection-Outer");
        page.Find("tr[data-source='Inner'] a.ms-flow-source-link").GetAttribute("href").Should().EndWith("#projection-Outer");
        page.Find("tr[data-source='Plain'] a.ms-flow-source-link").GetAttribute("href").Should().EndWith("#projection-Plain");
        page.Find("tr[data-source='Outer'] a.ms-flow-source-link").GetAttribute("href").Should().EndWith("#projection-Outer");
    }

    // ---------------------------------------------------------------------------------------------
    // The sentence's edges
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_shared_alias_is_named_once()
    {
        FlowReads reads = new(FlowInput.Declared, ["a", "a", "b"], []);

        reads.Sentence.Should().Be("a, b");
    }

    [Fact]
    public void A_long_list_is_cut_at_twenty_with_how_many_more()
    {
        string[] many = [.. Enumerable.Range(0, 25).Select(static i => "e" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))];

        string sentence = new FlowReads(FlowInput.Declared, many, ["archived"]).Sentence;

        sentence.Should().StartWith("e00, e01").And.Contain("e19").And.NotContain("e20").And.Contain("and 5 more").And.EndWith("(and Marten's own archived)");
    }

    [Fact]
    public void A_composite_with_a_member_that_declares_none_says_the_member_does()
    {
        EventFlowModel model = new(
            [EventFlowFixtures.Event("a")],
            [
                EventFlowFixtures.Source("Bundle", FlowSourceKind.Composite),
                EventFlowFixtures.Source("Bare", FlowSourceKind.Custom, input: FlowInput.Everything, composite: "Bundle"),
            ],
            [],
            [],
            [],
            [],
            null);

        model.ReadsOf("Bundle").Sentence.Should().Be("every event type (a member declares none)");
        model.ReadsOf("Bare").Sentence.Should().Be("every event type (it declares none)");
    }
}
