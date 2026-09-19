using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The event-types screen: the list, the on-demand counts, and the two flags that make it useful -
/// a type in the table the store does not know, and a type the store knows that has no events.
/// </summary>
public class EventTypesTests
{
    [Fact]
    public async Task The_types_are_listed_without_running_the_aggregate()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithoutCounts = new EventTypeList(
            [
                new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true),
                new EventTypeInfo("OrderShipped", "Sample.OrderShipped", true),
            ],
            false);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        context.EventData.CountsRequested.Should().BeFalse("counting is a full scan and is behind the button");
        page.TextOfAll(".ms-event-chip").Should().Equal("OrderPlaced", "OrderShipped");
        page.Markup.Should().Contain("not counted");
    }

    [Fact]
    public async Task Loading_counts_asks_for_them_and_shows_the_sequence_range()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true)], false);
        context.EventData.TypesWithCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true, 1234, 1, 9000)], true);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        await page.Find(".ms-page-header button").ClickAsync(new());

        context.EventData.CountsRequested.Should().BeTrue();
        page.Markup.Should().Contain("1,234").And.Contain("#1").And.Contain("#9000");
    }

    /// <summary>
    /// The two flags the screen exists for: a type nobody registered, and a registered type nothing ever
    /// appended. The second is only meaningful once the counts have been loaded.
    /// </summary>
    [Fact]
    public async Task Unregistered_types_and_registered_types_with_no_events_are_both_flagged()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithCounts = new EventTypeList(
            [
                new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true, 12, 1, 12),
                new EventTypeInfo("OrderCancelled", "Sample.OrderCancelled", true, 0),
                new EventTypeInfo("LegacyThingHappened", null, false, 3, 4, 6),
            ],
            true);
        context.EventData.TypesWithoutCounts = context.EventData.TypesWithCounts with { CountsLoaded = false };

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        await page.Find(".ms-page-header button").ClickAsync(new());

        page.TextOfAll("tbody .ms-event-flag").Should().Contain("no events").And.Contain("not registered");
        page.FindAll(".ms-event-chip-unregistered").Should().HaveCount(1);
    }

    [Fact]
    public async Task Clicking_a_type_goes_to_the_feed_filtered_to_it()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true)], false);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.Find("a.ms-event-chip").GetAttribute("href").Should()
            .StartWith("events/feed?types=OrderPlaced");
    }

    [Fact]
    public async Task A_failed_read_renders_the_sql_state()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithoutCounts = EventTypeList.Failed(
            new EventDataError("permission denied for table mt_events", "42501", false));

        context.Render<EventTypes>().Find(".ms-error-alert").TextContent.Should()
            .Contain("[42501] permission denied");
    }

    /// <summary>
    /// The screen lists at most two thousand types, and a capped list that does not say so is
    /// indistinguishable from a complete one - which is the same failure as drawing "could not count" as
    /// zero. Before this the cap was silent.
    /// </summary>
    [Fact]
    public async Task A_list_that_was_cut_short_says_so()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true, 12, 1, 12)],
            CountsLoaded: true,
            Error: null,
            IsTruncated: true);
        context.EventData.TypesWithoutCounts = context.EventData.TypesWithCounts with { CountsLoaded = false };

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.Find(".ms-alert-warning").TextContent.Should().Contain("not complete");
    }

    [Fact]
    public async Task A_complete_list_says_nothing_about_being_cut_short()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true)], false);

        context.Render<EventTypes>().FindAll(".ms-alert-warning").Should().BeEmpty();
    }

    [Fact]
    public async Task A_store_with_no_event_types_says_so()
    {
        using StudioComponentContext context = await NewContextAsync();

        context.Render<EventTypes>().Find(".ms-empty-title").TextContent.Should().Be("No event types");
    }

    // ---------------------------------------------------------------------------------------------
    // The width of it
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The .NET type column is the reason this table was 1691px wide inside a 1200px column with four of
    /// its six columns beyond the edge: a closed generic's assembly-qualified name is 170 characters of
    /// nested qualification. The cell shows the C# name and keeps the recorded one in its
    /// <c>title</c> - the raw value is what somebody pasting it into a search box needs, and it is one
    /// hover away rather than one column of table width away.
    /// </summary>
    [Fact]
    public async Task The_dot_net_type_cell_reads_as_C_sharp_and_keeps_the_recorded_name_in_its_title()
    {
        const string Recorded =
            "JasperFx.Events.Compacted`1[[MartenStudio.SampleDomain.Events.DailySales, "
            + "MartenStudio.SampleDomain, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null]], "
            + "JasperFx.Events";

        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("Compacted<DailySales>", Recorded, true)], false);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        var cell = page.Find(".ms-type-name");
        cell.TextContent.Trim().Should().Be("JasperFx.Events.Compacted<MartenStudio.SampleDomain.Events.DailySales>");
        cell.GetAttribute("title").Should().Be(Recorded);
    }

    [Fact]
    public async Task A_type_the_store_recorded_no_dot_net_name_for_is_a_dash()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("legacy_thing", null, false)], false);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.Find(".ms-type-name").TextContent.Trim().Should().Be("-");
    }

    [Fact]
    public async Task The_table_is_in_a_scroll_region_rather_than_being_clipped()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true)], false);

        context.Render<EventTypes>().ShouldPutEveryTableInALabelledScrollRegion();
    }

    private static async Task<StudioComponentContext> NewContextAsync()
    {
        var context = new StudioComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
