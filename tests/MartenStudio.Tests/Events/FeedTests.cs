using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

using Microsoft.JSInterop;

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
            .Contain(x => x!.StartsWith("events/streams/s?id=order-17", StringComparison.Ordinal))
            .And.Contain(x => x!.StartsWith("events/feed?types=OrderPlaced", StringComparison.Ordinal));
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

    /// <summary>
    /// The pill counts what it is holding, and says so when that is not all of it. A burst larger than
    /// the buffer used to render as a flat "500 new events", which is a number the reader would use to
    /// decide they had seen everything.
    /// </summary>
    [Fact]
    public async Task The_pill_says_five_hundred_plus_once_the_buffer_has_had_to_drop_events()
    {
        using EventsComponentContext context = await NewContextAsync();

        context.Options.RefreshInterval = TimeSpan.FromMilliseconds(20);

        context.Data.Feed = new EventPage([FakeEventDataService.Event(10)], 10, false);
        context.Data.HighestSequences.Enqueue(10);
        context.Data.HighestSequences.Enqueue(1_000);

        // 501 is one more than the buffer holds: newest first, the way the builder returns them.
        List<EventRow> burst = [];
        for (long sequence = 511; sequence > 10; sequence--)
        {
            burst.Add(FakeEventDataService.Event(sequence));
        }

        burst.Should().HaveCount(501);
        context.Data.FeedDeltas.Enqueue(new EventPage(burst, 11, false));

        context.Navigate("marten/events/feed?follow=1");

        IRenderedComponent<Feed> page = context.Render<Feed>();

        page.WaitForAssertion(
            () => page.Find(".ms-follow-pill").TextContent.Trim().Should().Be("500+ new events"),
            TimeSpan.FromSeconds(10));

        page.Find(".ms-follow-pill").GetAttribute("title").Should().Contain("dropped");

        // Showing them clears the flag as well as the buffer: the next pill counts from scratch.
        await page.Find(".ms-follow-pill").ClickAsync(new());

        page.FindAll(".ms-follow-pill").Should().BeEmpty();
    }

    /// <summary>
    /// A closed browser tab is the normal way this page ends, and closing one throws
    /// <see cref="Microsoft.JSInterop.JSDisconnectedException" /> out of <c>visibility.unwatch</c>. It
    /// derives from <see cref="Exception" /> and <em>not</em> from
    /// <see cref="Microsoft.JSInterop.JSException" />, so the <c>catch (JSException)</c> ladder that used
    /// to stand here let it straight through <c>DisposeAsync</c> - which then never disposed the
    /// <c>DotNetObjectReference</c> that pins this component, and never cancelled the page's token.
    /// </summary>
    [Fact]
    public async Task Disposal_survives_a_circuit_that_is_already_gone_and_still_releases_everything()
    {
        var js = new DisconnectingJsRuntime();

        using var context = new EventsComponentContext(js);
        await context.ReadyAsync();

        // An empty feed on purpose. Every JSON body on the page brings a JsonView, whose own interop
        // still catches by exception type and therefore lets a JSDisconnectedException escape its render
        // - which is the same finding in a component this packet does not own, and would fail this test
        // for a reason that is not what it is about.
        IRenderedComponent<Feed> page = context.Render<Feed>();

        js.DotNetReference.Should().BeOfType<DotNetObjectReference<Feed>>(
            "the page watches the tab on its first render");

        Func<Task> dispose = async () => await page.Instance.DisposeAsync();

        await dispose.Should().NotThrowAsync();

        js.TriedToUnwatch.Should().BeTrue("the watch token is handed back before the reference is dropped");

        // Keyed on the token, not on the reference: Blazor marshals a DotNetObjectReference as an id and
        // the browser makes a new wrapper for every call, so a watcher keyed on one can never be removed.
        js.WatchToken.Should().NotBeNullOrWhiteSpace();
        js.UnwatchToken.Should().Be(js.WatchToken);

        // A disposed DotNetObjectReference throws from Value; an undisposed one would answer the page.
        var reference = (DotNetObjectReference<Feed>) js.DotNetReference!;
        Action read = () => _ = reference.Value;
        read.Should().Throw<ObjectDisposedException>("the reference must be released however interop went");

        PageCancellation(page.Instance).IsCancellationRequested.Should().BeTrue();

        // Blazor disposes a component once; bUnit disposes it again when the context goes. Neither may
        // throw, which is why disposal is idempotent - Cancel on a disposed CTS is an ObjectDisposedException.
        await dispose.Should().NotThrowAsync();
    }

    /// <summary>
    /// The page's own <see cref="CancellationTokenSource" />, read by reflection.
    /// </summary>
    /// <remarks>
    /// There is no other way to see it: it is a private field of a component and it has no observable
    /// effect once every read it governs has finished. It is nevertheless exactly what this test is about
    /// - "disposal completed" is not the same claim as "disposal finished its job" - so the reflection is
    /// the honest way to assert it rather than a reason to assert something weaker.
    /// </remarks>
    private static CancellationTokenSource PageCancellation(Feed page) =>
        (CancellationTokenSource) typeof(Feed)
            .GetField("pageCancellation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(page)!;

    private static async Task<EventsComponentContext> NewContextAsync()
    {
        var context = new EventsComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
