using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using FlowPage = MartenStudio.Components.Pages.Events.EventFlow;

namespace MartenStudio.Tests.Events;

/// <summary>
/// 1.2, M6: Events › Event flow - the picture, the three tables beside it, the filters in the URL and the states in between.
/// </summary>
public class EventFlowPageTests
{
    private static IRenderedComponent<FlowPage> Render(StudioComponentContext context, string query = "")
    {
        context.Navigate("marten/events/flow" + query);

        return context.Render<FlowPage>();
    }

    private static StudioComponentContext NewContext(EventFlowModel? model = null)
    {
        var context = new StudioComponentContext();
        context.WithStores("default");
        context.EventData.EventFlow = new EventFlowResult(model ?? EventFlowFixtures.Sample(), null);

        return context;
    }

    [Fact]
    public void The_studios_router_reaches_the_page()
    {
        MartenSpine.Routes.Match("events/flow").Should().NotBeNull()
            .And.Subject.As<RouteData>().PageType.Should().Be<FlowPage>();
    }

    [Fact]
    public void The_picture_is_one_image_named_by_its_sentence_and_the_three_tables_are_in_labelled_regions()
    {
        using StudioComponentContext context = NewContext(EventFlowFixtures.Sample() with
        {
            TagTypes = [new EventTagTypeModel("StudentId", "student", typeof(Guid), "Student")],
        });

        IRenderedComponent<FlowPage> page = Render(context);

        IElement svg = page.Find("svg.ms-graph-svg");
        svg.GetAttribute("role").Should().Be("img");
        svg.GetAttribute("aria-label").Should().Be("5 event types feed 3 projections and subscriptions, which write 2 document types, tables and live aggregates.");
        page.Find(".ms-graph").GetAttribute("aria-label").Should().Be("Event flow diagram");

        page.ShouldPutEveryTableInALabelledScrollRegion();
        page.FindAll("table.ms-table").Should().HaveCount(3);
        page.FindAll(".ms-table-scroll").Select(static x => x.GetAttribute("aria-label")).Should().Contain(
            ["Projections and subscriptions table", "Event types table", "Tag types table"]);
    }

    [Fact]
    public void Every_arrow_of_the_picture_is_on_a_row_of_the_table_beside_it()
    {
        using StudioComponentContext context = NewContext();

        IRenderedComponent<FlowPage> page = Render(context, "?own=1");

        HashSet<string> listed = [.. page.FindAll("table.ms-flow-table [data-edge]").Select(static x => x.GetAttribute("data-edge")!)];
        List<string> drawn = [.. page.FindAll(".ms-graph-svg .ms-graph-edge").Select(static x => x.GetAttribute("data-edge")!)];

        drawn.Should().NotBeEmpty();
        drawn.Should().OnlyContain(x => listed.Contains(x), "the table is the accessible form of the picture");
    }

    [Fact]
    public void The_by_projection_table_says_what_each_source_reads_and_writes()
    {
        using StudioComponentContext context = NewContext();

        IRenderedComponent<FlowPage> page = Render(context);

        IElement daily = page.Find("tr[data-source='Daily']");
        daily.QuerySelector(".ms-flow-reads")!.TextContent.Should().Contain("shipped").And.Contain("(as IORDER)");
        daily.QuerySelector(".ms-flow-writes")!.TextContent.Should().Contain("day");
        daily.TextContent.Should().Contain("Multi stream · Async");

        IElement tracker = page.Find("tr[data-source='Tracker']");
        tracker.QuerySelector(".ms-flow-writes")!.TextContent.Trim().Should().Be("none listed", "it declares nothing it writes");
        page.Find("tr[data-source='Orders']").TextContent.Should().Contain("Single stream · Inline");
    }

    [Fact]
    public void Every_link_carries_the_scope_and_goes_where_the_picture_goes()
    {
        using StudioComponentContext context = NewContext();

        IRenderedComponent<FlowPage> page = Render(context);

        IReadOnlyList<IElement> links = page.FindAll("table.ms-flow-table a");
        links.Should().NotBeEmpty();
        links.Select(static x => x.GetAttribute("href")!).Should().OnlyContain(static x => x.Contains("store=default", StringComparison.Ordinal),
            "a link someone pastes has to reopen the same store (D9)");

        page.FindAll("a.ms-event-chip").Select(static x => x.GetAttribute("href")!).Should().OnlyContain(static x => x.StartsWith("events/feed?types=", StringComparison.Ordinal));
        page.Find("tr[data-source='Orders'] a.ms-flow-source-link").GetAttribute("href").Should().StartWith("projections");
        page.Find("tr[data-source='Orders'] a.ms-flow-show-flow").GetAttribute("href").Should().StartWith("events/flow?projection=Orders");
        page.Find("tr[data-source='Orders'] a.ms-flow-output-link").GetAttribute("href").Should().StartWith("documents/order");
    }

    [Fact]
    public void The_by_event_type_table_lists_Martens_own_events_last_under_their_own_heading()
    {
        using StudioComponentContext context = NewContext();

        IRenderedComponent<FlowPage> page = Render(context);

        IReadOnlyList<IElement> rows = page.FindAll("table[aria-label], .ms-table-scroll[aria-label='Event types table'] tbody tr");
        rows[^1].GetAttribute("data-event").Should().Be("archived");
        page.Find(".ms-flow-group-row").TextContent.Should().Contain("Marten's own events");
        page.Find("tr[data-event='ledger_opened']").TextContent.Should().Contain("nothing reads it");
        page.Find("tr[data-event='shipped']").TextContent.Should().Contain("Daily").And.Contain("(as IORDER)").And.Contain("Tracker");
    }

    [Fact]
    public void Event_types_nothing_reads_are_chips_under_the_picture_that_link_to_the_feed()
    {
        using StudioComponentContext context = NewContext();

        IRenderedComponent<FlowPage> page = Render(context);

        IElement chip = page.Find(".ms-flow-unconsumed a.ms-event-chip");
        chip.TextContent.Should().Be("ledger_opened");
        chip.GetAttribute("href").Should().StartWith("events/feed?types=ledger_opened");
    }

    [Fact]
    public void When_some_source_reads_everything_the_unread_list_is_one_honest_sentence()
    {
        EventFlowModel model = EventFlowFixtures.Sample() with
        {
            Sources = [.. EventFlowFixtures.Sample().Sources, EventFlowFixtures.Source("Bare", FlowSourceKind.Custom, input: FlowInput.Everything)],
        };
        using StudioComponentContext context = NewContext(model);

        IRenderedComponent<FlowPage> page = Render(context);

        page.Find(".ms-flow-unconsumed").TextContent.Should().Contain("Bare declares no event types, so the daemon feeds it every event, and no event type goes unread.");
        page.FindAll(".ms-flow-unconsumed a.ms-event-chip").Should().BeEmpty();
        page.Find("tr[data-source='Bare'] .ms-flow-reads").TextContent.Should().Contain("every event type");
        page.Find("tr[data-event='ledger_opened']").TextContent.Should().Contain("every source that declares none");
    }

    // ---------------------------------------------------------------------------------------------
    // The filters
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_projection_in_the_url_selects_the_option_and_narrows_the_picture()
    {
        using StudioComponentContext context = NewContext();

        IRenderedComponent<FlowPage> page = Render(context, "?projection=Daily");

        page.Find("#ms-flow-projection").GetAttribute("value").Should().Be("Daily");
        page.FindAll(".ms-graph-svg .ms-graph-node.ms-flow-node-source").Should().ContainSingle();
        page.FindAll("tr[data-source]").Should().HaveCount(3, "the tables still list everything");
    }

    [Fact]
    public void The_event_type_in_the_url_selects_the_option_and_narrows_the_picture()
    {
        using StudioComponentContext context = NewContext();

        IRenderedComponent<FlowPage> page = Render(context, "?type=shipped");

        page.Find("#ms-flow-type").GetAttribute("value").Should().Be("shipped");
        page.FindAll(".ms-graph-svg .ms-flow-node-source").Should().HaveCount(3);
        page.FindAll(".ms-graph-svg .ms-graph-node").Should().HaveCount(6);
    }

    [Fact]
    public async Task Choosing_a_projection_navigates_to_the_link_that_carries_it_and_the_scope()
    {
        using StudioComponentContext context = NewContext();
        IRenderedComponent<FlowPage> page = Render(context);

        await page.Find("#ms-flow-projection").ChangeAsync(new ChangeEventArgs { Value = "Orders" });

        context.Services.GetRequiredService<NavigationManager>().Uri.Should()
            .Contain("events/flow?projection=Orders").And.Contain("store=default");
        page.FindAll(".ms-graph-svg .ms-flow-node-source").Should().ContainSingle();
    }

    [Fact]
    public async Task Choosing_an_event_type_and_ticking_Martens_own_events_navigate_too()
    {
        using StudioComponentContext context = NewContext();
        IRenderedComponent<FlowPage> page = Render(context);

        await page.Find("#ms-flow-type").ChangeAsync(new ChangeEventArgs { Value = "shipped" });
        context.Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("type=shipped");

        await page.Find(".ms-flow-own-toggle input").ChangeAsync(new ChangeEventArgs { Value = true });
        context.Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("own=1");
    }

    [Fact]
    public void A_name_this_store_does_not_have_says_so_and_still_lists_everything()
    {
        using StudioComponentContext context = NewContext();

        IRenderedComponent<FlowPage> page = Render(context, "?projection=Nobody");

        page.Find(".ms-empty-title").TextContent.Should().Be("No projection or event type of that name in this store");
        page.FindAll("svg.ms-graph-svg").Should().BeEmpty();
        page.FindAll("tr[data-source]").Should().HaveCount(3);
    }

    // ---------------------------------------------------------------------------------------------
    // The states
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Over_the_cap_the_picture_is_replaced_by_a_prompt_and_the_tables_stay()
    {
        FlowEventType[] events = [.. Enumerable.Range(0, 61).Select(static i => EventFlowFixtures.Event("e" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)))];
        EventFlowModel model = new(
            events,
            [EventFlowFixtures.Source("Big")],
            [.. events.Select(static x => new FlowRead(x.Key, "Big", null))],
            [],
            [],
            [],
            null);
        using StudioComponentContext context = NewContext(model);

        IRenderedComponent<FlowPage> page = Render(context);

        page.FindAll("svg.ms-graph-svg").Should().BeEmpty();
        page.Find(".ms-graph-toomany").TextContent.Should().Contain("62 boxes are too many to draw - pick a projection or an event type.");
        page.FindAll("tr[data-event]").Should().HaveCount(61);
        page.FindAll("tr[data-source]").Should().HaveCount(1);
    }

    [Fact]
    public void A_store_with_nothing_says_so()
    {
        using StudioComponentContext context = NewContext(EventFlowModel.None);

        Render(context).Find(".ms-empty-title").TextContent.Should().Be("This store has no event types or projections");
    }

    [Fact]
    public void Event_types_with_no_projection_are_listed_unread_and_say_none_is_registered()
    {
        using StudioComponentContext context = NewContext(new EventFlowModel([EventFlowFixtures.Event("lonely")], [], [], [], [], [], null));

        IRenderedComponent<FlowPage> page = Render(context);

        page.Markup.Should().Contain("No projection or subscription is registered");
        page.Find("tr[data-event='lonely']").TextContent.Should().Contain("nothing reads it");
        page.FindAll("svg.ms-graph-svg").Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_read_shows_the_error_and_Retry_reads_again()
    {
        using StudioComponentContext context = NewContext();
        context.EventData.EventFlow = EventFlowResult.Failed("the store is not available");

        IRenderedComponent<FlowPage> page = Render(context);

        page.Find(".ms-error-alert").TextContent.Should().Contain("the store is not available");
        int reads = context.EventData.EventFlowReads;

        context.EventData.EventFlow = new EventFlowResult(EventFlowFixtures.Sample(), null);
        await page.Find(".ms-error-alert button").ClickAsync(new());

        context.EventData.EventFlowReads.Should().Be(reads + 1);
        page.FindAll(".ms-error-alert").Should().BeEmpty();
        page.FindAll("svg.ms-graph-svg").Should().ContainSingle();
    }

    [Fact]
    public void A_store_whose_subscriptions_cannot_be_read_says_so()
    {
        using StudioComponentContext context = NewContext(EventFlowFixtures.Sample() with { SubscriptionsUnavailable = EventFlowGraph.SubscriptionsUnavailableText });

        Render(context).Find(".ms-graph-withheld").TextContent.Should().Be("Subscriptions could not be read from this store's options.");
    }

    [Fact]
    public void The_page_reads_for_the_active_scope()
    {
        using StudioComponentContext context = NewContext();

        Render(context);

        context.EventData.EventFlowScopes.Should().NotBeEmpty();
        context.EventData.EventFlowScopes.Should().OnlyContain(static x => x.StoreKey == "default");
    }

    [Fact]
    public async Task Refresh_reads_again_and_keeps_the_page_on_screen_while_it_does()
    {
        using StudioComponentContext context = NewContext();
        IRenderedComponent<FlowPage> page = Render(context);
        int reads = context.EventData.EventFlowReads;

        EventFlowModel sample = EventFlowFixtures.Sample();
        context.EventData.EventFlow = new EventFlowResult(
            sample with { Sources = [.. sample.Sources.Where(static x => x.Name != "Tracker")] },
            null);

        await page.Find(".ms-graph-actions button").ClickAsync(new());

        context.EventData.EventFlowReads.Should().Be(reads + 1);
        page.FindAll("tr[data-source]").Select(static x => x.GetAttribute("data-source")).Should().Equal("Daily", "Orders");
        page.FindAll(".ms-loading-spinner").Should().BeEmpty();
    }

    /// <summary>
    /// bUnit patches its DOM from the new markup, so an element's identity says nothing about Blazor's keys; a keyed row is a
    /// component, and a component instance that survives a reorder is the proof that it was matched by its key and not by position.
    /// </summary>
    [Fact]
    public async Task Rows_keep_their_instances_when_the_model_is_reordered()
    {
        using StudioComponentContext context = NewContext();
        IRenderedComponent<FlowPage> page = Render(context);

        Dictionary<string, EventFlowRow> before = Rows(page);
        before.Keys.Should().Contain(["s:Orders", "s:Daily", "e:shipped", "e:archived"]);

        EventFlowModel sample = EventFlowFixtures.Sample();
        context.EventData.EventFlow = new EventFlowResult(
            sample with { Sources = [.. sample.Sources.Reverse()], EventTypes = [.. sample.EventTypes.Reverse()] },
            null);

        await page.Find(".ms-graph-actions button").ClickAsync(new());

        Dictionary<string, EventFlowRow> after = Rows(page);
        after.Keys.Should().BeEquivalentTo(before.Keys);

        foreach ((string key, EventFlowRow row) in before)
        {
            after[key].Should().BeSameAs(row, key + " is matched by its key, not by its place in the list");
        }
    }

    private static Dictionary<string, EventFlowRow> Rows(IRenderedComponent<FlowPage> page) =>
        page.FindComponents<EventFlowRow>().Select(static x => x.Instance).ToDictionary(
            static x => x.Source is { Length: > 0 } source ? "s:" + source : "e:" + x.Event,
            static x => x,
            StringComparer.Ordinal);

    [Fact]
    public void A_type_the_registry_has_not_met_is_marked_with_why()
    {
        EventFlowModel sample = EventFlowFixtures.Sample();
        using StudioComponentContext context = NewContext(sample with
        {
            EventTypes = [.. sample.EventTypes, EventFlowFixtures.Event("fresh") with { NotRegistered = true }],
        });

        IRenderedComponent<FlowPage> page = Render(context);

        IElement mark = page.Find("tr[data-event='fresh'] .ms-flow-unregistered");
        mark.TextContent.Should().Be("(not registered yet)");
        mark.GetAttribute("title").Should().Contain("registry grows as events are appended or read");
        page.FindAll("tr[data-event='shipped'] .ms-flow-unregistered").Should().BeEmpty();
    }

    [Fact]
    public void What_a_source_reads_and_what_it_writes_keep_their_notes_apart()
    {
        EventFlowModel sample = EventFlowFixtures.Sample();
        FlowSource broken = EventFlowFixtures.Source("Broken", FlowSourceKind.Custom, input: FlowInput.Everything) with
        {
            Note = "The studio cannot read which event types this declares.",
            WritesNote = "could not describe what it writes: InvalidOperationException",
        };
        using StudioComponentContext context = NewContext(sample with { Sources = [.. sample.Sources, broken] });

        IRenderedComponent<FlowPage> page = Render(context);

        IElement row = page.Find("tr[data-source='Broken']");
        row.QuerySelector(".ms-flow-reads")!.TextContent.Should().Contain("cannot read which event types").And.NotContain("could not describe what it writes");
        row.QuerySelector(".ms-flow-writes")!.TextContent.Should().Contain("could not describe what it writes: InvalidOperationException")
            .And.NotContain("none listed");
    }

    [Fact]
    public void A_store_whose_document_types_cannot_be_read_says_so_once()
    {
        using StudioComponentContext context = NewContext(EventFlowFixtures.Sample() with { DocumentTypesUnavailable = EventFlowGraph.DocumentTypesUnavailableText });

        Render(context).FindAll(".ms-graph-withheld").Select(static x => x.TextContent).Should().Equal(EventFlowGraph.DocumentTypesUnavailableText);
    }

    [Fact]
    public void A_live_aggregate_and_a_flat_table_link_where_the_picture_links_them()
    {
        EventFlowModel sample = EventFlowFixtures.Sample();
        EventFlowModel model = sample with
        {
            Outputs = [.. sample.Outputs, EventFlowFixtures.Table("public", "flow_table"), EventFlowFixtures.Live("Live")],
            Writes = [.. sample.Writes, new("Tracker", "t:public.flow_table", null), new("Orders", "a:Sample.Live", null)],
        };
        using StudioComponentContext context = NewContext(model);

        IRenderedComponent<FlowPage> page = Render(context);

        page.Find("tr[data-source='Tracker'] a.ms-flow-output-link").GetAttribute("href").Should().Contain("schema=public").And.Contain("name=flow_table");
        page.Find("tr[data-source='Orders'] .ms-flow-writes").TextContent.Should().Contain("(folded live)");
        page.Find("tr[data-source='Tracker'] .ms-flow-writes").TextContent.Should().Contain("(table)");
    }
}
