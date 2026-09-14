using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The global feed: the filters that reach the service, the keyset paging, and follow mode's pill.
/// </summary>
public class FeedTests
{
    [Fact]
    public async Task Every_filter_in_the_url_reaches_the_service()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.Shape = context.Data.Shape with { HasIsSkipped = true };
        context.Navigate(
            "marten/events/feed?types=OrderPlaced,OrderShipped&stream=abc&range=1h&archived=1&skipped=0&after=500");

        context.Render<Feed>();

        EventFeedRequest request = context.Data.FeedRequests[^1];
        request.EventTypes.Should().Equal("OrderPlaced", "OrderShipped");
        request.StreamId.Should().Be("abc");
        request.IncludeArchived.Should().BeTrue();
        request.IncludeSkipped.Should().BeFalse();
        request.AfterSequence.Should().Be(500);
        request.From.Should().NotBeNull();
    }

    [Fact]
    public async Task Skipped_events_are_included_by_default_because_leaving_them_out_hides_a_problem()
    {
        using EventsComponentContext context = await NewContextAsync();

        context.Render<Feed>();

        context.Data.FeedRequests[^1].IncludeSkipped.Should().BeTrue();
        context.Data.FeedRequests[^1].IncludeArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Each_card_links_to_its_stream_and_to_the_feed_filtered_to_its_type()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.Feed = new EventPage([FakeEventDataService.Event(7, streamId: "order-17")], 7, false);

        IRenderedComponent<Feed> page = context.Render<Feed>();

        page.FindAll("a").Select(x => x.GetAttribute("href")).Should()
            .Contain(x => x!.StartsWith("marten/events/streams/s?id=order-17", StringComparison.Ordinal))
            .And.Contain(x => x!.StartsWith("marten/events/feed?types=OrderPlaced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_binary_payload_is_reported_in_the_feed_too()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.Feed = new EventPage(
            [FakeEventDataService.Event(7, json: null, hasBinary: true, binaryLength: 12)], 7, false);

        context.Render<Feed>().Find(".ms-event-binary").TextContent.Should().Contain("binary payload, 12 bytes");
    }

    [Fact]
    public async Task The_older_link_carries_the_last_sequence_as_the_keyset_cursor()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.Feed = new EventPage(
            [FakeEventDataService.Event(9), FakeEventDataService.Event(8)], 8, HasMore: true);

        IRenderedComponent<Feed> page = context.Render<Feed>();

        page.FindAll("nav.ms-pagination a").Select(x => x.GetAttribute("href")).Should()
            .Contain(x => x!.Contains("after=8", StringComparison.Ordinal));
    }

    /// <summary>Follow mode has no meaning on a page whose top is fixed by a cursor.</summary>
    [Fact]
    public async Task Follow_is_unavailable_once_the_reader_has_paged_back()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Navigate("marten/events/feed?after=500&follow=1");

        IRenderedComponent<Feed> page = context.Render<Feed>();

        page.Find(".ms-follow-bar input").HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-follow-state").TextContent.Should().Contain("only available on the newest page");
    }

    [Fact]
    public async Task Follow_off_says_so_and_polls_nothing()
    {
        using EventsComponentContext context = await NewContextAsync();

        IRenderedComponent<Feed> page = context.Render<Feed>();

        page.Find(".ms-follow-state").TextContent.Should().Contain("not following");
        page.FindAll(".ms-follow-pill").Should().BeEmpty();
    }

    /// <summary>
    /// Follow mode polls the high-water mark, reads only the delta, and offers it as a pill rather than
    /// moving the page under whoever is reading it.
    /// </summary>
    [Fact]
    public async Task Following_offers_new_events_as_a_pill_and_prepends_them_only_when_asked()
    {
        using EventsComponentContext context = await NewContextAsync();

        // A short interval so the test is about the behaviour rather than about waiting. Options
        // validation (one second minimum) runs in AddMartenStudio, which a component test does not use.
        context.Options.RefreshInterval = TimeSpan.FromMilliseconds(20);

        context.Data.Feed = new EventPage([FakeEventDataService.Event(10)], 10, false);
        context.Data.HighestSequences.Enqueue(10);
        context.Data.HighestSequences.Enqueue(12);
        context.Data.FeedDeltas.Enqueue(new EventPage(
            [FakeEventDataService.Event(12), FakeEventDataService.Event(11)], 11, false));

        context.Navigate("marten/events/feed?follow=1");

        IRenderedComponent<Feed> page = context.Render<Feed>();

        page.WaitForAssertion(
            () => page.Find(".ms-follow-pill").TextContent.Trim().Should().Be("2 new events"),
            TimeSpan.FromSeconds(10));

        // Nothing moved until the pill was clicked.
        page.FindAll(".ms-event-card").Should().HaveCount(1);

        await page.Find(".ms-follow-pill").ClickAsync(new());

        page.FindAll(".ms-event-card").Should().HaveCount(3);
        page.FindAll(".ms-follow-pill").Should().BeEmpty();

        // The delta read asked for everything after the newest sequence the page already had.
        context.Data.FeedRequests.Should().Contain(x => x.SinceSequence == 10);
    }

    [Fact]
    public async Task A_failed_feed_read_renders_the_sql_state()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.Feed = EventPage.Failed(new EventDataError("cancelled", "57014", true));

        context.Render<Feed>().Find(".ms-error-alert").TextContent.Should().Contain("[57014] cancelled");
    }

    /// <summary>
    /// The type multi-select offers what the store registers plus whatever this page actually shows, so
    /// an unregistered type in the table is still selectable without a full scan.
    /// </summary>
    [Fact]
    public async Task The_type_filter_offers_the_registered_types_and_the_ones_on_this_page()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.TypesWithoutCounts = new EventTypeList(
            [new EventTypeInfo("OrderPlaced", "X.OrderPlaced", true)], false);
        context.Data.Feed = new EventPage([FakeEventDataService.Event(1, type: "LegacyThingHappened")], 1, false);

        IRenderedComponent<Feed> page = context.Render<Feed>();

        page.TextOfAll(".ms-event-filter-types .ms-event-chip").Should()
            .Contain("OrderPlaced").And.Contain("LegacyThingHappened");
    }

    [Fact]
    public async Task The_skipped_switch_is_offered_only_where_the_store_records_skipped_events()
    {
        using EventsComponentContext without = await NewContextAsync();
        without.Render<Feed>().TextOfAll(".ms-event-filter-checkbox").Should().NotContain("Include skipped");

        using EventsComponentContext with = await NewContextAsync();
        with.Data.Shape = with.Data.Shape with { HasIsSkipped = true };

        with.Render<Feed>().TextOfAll(".ms-event-filter-checkbox").Should().Contain("Include skipped");
    }

    private static async Task<EventsComponentContext> NewContextAsync()
    {
        var context = new EventsComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
