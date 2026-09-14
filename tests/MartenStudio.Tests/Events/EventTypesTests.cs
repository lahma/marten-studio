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
        using EventsComponentContext context = await NewContextAsync();
        context.Data.TypesWithoutCounts = new EventTypeList(
            [
                new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true),
                new EventTypeInfo("OrderShipped", "Sample.OrderShipped", true),
            ],
            false);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        context.Data.CountsRequested.Should().BeFalse("counting is a full scan and is behind the button");
        page.TextOfAll(".ms-event-chip").Should().Equal("OrderPlaced", "OrderShipped");
        page.Markup.Should().Contain("not counted");
    }

    [Fact]
    public async Task Loading_counts_asks_for_them_and_shows_the_sequence_range()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true)], false);
        context.Data.TypesWithCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true, 1234, 1, 9000)], true);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        await page.Find(".ms-page-header button").ClickAsync(new());

        context.Data.CountsRequested.Should().BeTrue();
        page.Markup.Should().Contain("1,234").And.Contain("#1").And.Contain("#9000");
    }

    /// <summary>
    /// The two flags the screen exists for: a type nobody registered, and a registered type nothing ever
    /// appended. The second is only meaningful once the counts have been loaded.
    /// </summary>
    [Fact]
    public async Task Unregistered_types_and_registered_types_with_no_events_are_both_flagged()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.TypesWithCounts = new EventTypeList(
            [
                new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true, 12, 1, 12),
                new EventTypeInfo("OrderCancelled", "Sample.OrderCancelled", true, 0),
                new EventTypeInfo("LegacyThingHappened", null, false, 3, 4, 6),
            ],
            true);
        context.Data.TypesWithoutCounts = context.Data.TypesWithCounts with { CountsLoaded = false };

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        await page.Find(".ms-page-header button").ClickAsync(new());

        page.TextOfAll("tbody .ms-event-flag").Should().Contain("no events").And.Contain("not registered");
        page.FindAll(".ms-event-chip-unregistered").Should().HaveCount(1);
    }

    [Fact]
    public async Task Clicking_a_type_goes_to_the_feed_filtered_to_it()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "Sample.OrderPlaced", true)], false);

        IRenderedComponent<EventTypes> page = context.Render<EventTypes>();

        page.Find("a.ms-event-chip").GetAttribute("href").Should()
            .StartWith("events/feed?types=OrderPlaced");
    }

    [Fact]
    public async Task A_failed_read_renders_the_sql_state()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.TypesWithoutCounts = EventTypeList.Failed(
            new EventDataError("permission denied for table mt_events", "42501", false));

        context.Render<EventTypes>().Find(".ms-error-alert").TextContent.Should()
            .Contain("[42501] permission denied");
    }

    [Fact]
    public async Task A_store_with_no_event_types_says_so()
    {
        using EventsComponentContext context = await NewContextAsync();

        context.Render<EventTypes>().Find(".ms-empty-title").TextContent.Should().Be("No event types");
    }

    private static async Task<EventsComponentContext> NewContextAsync()
    {
        var context = new EventsComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
