using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;

using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The stream detail page: the header, the timeline, the binary indicator, the metadata chips, the
/// time-travel panel and the gated archive action.
/// </summary>
public class StreamDetailTests
{
    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Without_an_id_the_page_says_so_rather_than_reading_anything()
    {
        using EventsComponentContext context = await NewContextAsync();

        IRenderedComponent<StreamDetail> page = context.Render<StreamDetail>();

        page.Find(".ms-empty-title").TextContent.Should().Be("No stream asked for");
        context.Data.StreamStates.Should().BeEmpty();
    }

    [Fact]
    public async Task The_header_carries_the_id_the_type_the_version_and_the_archived_banner()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream(archived: true, tenantId: "acme");

        IRenderedComponent<StreamDetail> page = Render(context);

        page.Markup.Should().Contain(StreamId);
        page.TextOfAll(".ms-event-chip").Should().Contain("Order");
        page.TextOfAll(".ms-event-flag").Should().Contain("version 3");
        page.TextOfAll(".ms-event-flag").Should().Contain("tenant acme");
        page.Find(".ms-alert-warning").TextContent.Should().Contain("This stream is archived.");
    }

    /// <summary>
    /// The binary payload is reported and never read: the column is not even in the select list, so what
    /// the card can say is how many bytes there are.
    /// </summary>
    [Fact]
    public async Task An_event_with_a_binary_payload_shows_the_indicator_and_no_body()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();
        context.Data.StreamWindows.Enqueue(new EventPage(
            [FakeEventDataService.Event(1, json: null, hasBinary: true, binaryLength: 4096)], 1, false));

        IRenderedComponent<StreamDetail> page = Render(context);

        page.Find(".ms-event-binary").TextContent.Should().Contain("binary payload, 4,096 bytes");
        page.Markup.Should().Contain("payload is binary");
    }

    [Fact]
    public async Task A_payload_whose_size_the_database_could_not_report_says_so_rather_than_zero()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();
        context.Data.StreamWindows.Enqueue(new EventPage(
            [FakeEventDataService.Event(1, json: null, hasBinary: true, binaryLength: null)], 1, false));

        Render(context).Find(".ms-event-binary").TextContent.Should().Contain("size unknown");
    }

    /// <summary>One chip per optional column the store actually has - discovered, not assumed.</summary>
    [Fact]
    public async Task Every_optional_column_the_store_has_becomes_a_metadata_chip()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();
        context.Data.StreamWindows.Enqueue(new EventPage(
            [
                FakeEventDataService.Event(1, metadata: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["correlation_id"] = "corr-7",
                    ["user_name"] = "ops",
                    ["headers"] = null,
                }),
            ],
            1,
            false));

        IRenderedComponent<StreamDetail> page = Render(context);

        page.TextOfAll(".ms-event-meta-key").Should().Contain("correlation_id").And.Contain("user_name");
        page.TextOfAll(".ms-event-meta-value").Should().Contain("corr-7").And.Contain("ops");

        // A column that exists but is null on this row is not a chip: an empty chip reads as a value.
        page.TextOfAll(".ms-event-meta-key").Should().NotContain("headers");
    }

    [Fact]
    public async Task A_skipped_event_wears_the_flag()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();
        context.Data.StreamWindows.Enqueue(new EventPage(
            [FakeEventDataService.Event(1, isSkipped: true)], 1, false));

        Render(context).FindAll(".ms-event-flag-skipped").Should().NotBeEmpty();
    }

    /// <summary>
    /// The picker falls back to the store's document types when no single-stream projection matches,
    /// and says which is which.
    /// </summary>
    [Fact]
    public async Task The_time_travel_picker_falls_back_to_the_stores_document_types()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();
        context.Data.Candidates.Add(
            new AggregateTypeCandidate(typeof(TimeTravelNote), "TimeTravelNote", "X.TimeTravelNote", AggregateCandidateSource.DocumentType));

        IRenderedComponent<StreamDetail> page = Render(context);

        page.TextOfAll("#ms-time-travel-type option").Should()
            .Equal("Pick an aggregate type", "TimeTravelNote");
    }

    [Fact]
    public async Task A_projection_candidate_says_that_it_came_from_a_projection()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();
        context.Data.Candidates.Add(new AggregateTypeCandidate(
            typeof(TimeTravelOrder), "TimeTravelOrder", "X.TimeTravelOrder", AggregateCandidateSource.SingleStreamProjection));

        Render(context).TextOfAll("#ms-time-travel-type option").Should()
            .Contain("TimeTravelOrder (single-stream projection)");
    }

    [Fact]
    public async Task With_no_candidate_at_all_the_panel_says_there_is_nothing_to_replay_into()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();

        Render(context).Find(".ms-time-travel").TextContent.Should()
            .Contain("nothing to replay this stream");
    }

    [Fact]
    public async Task Replaying_renders_the_aggregate_with_the_json_viewer()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();
        context.Data.Candidates.Add(new AggregateTypeCandidate(
            typeof(TimeTravelOrder), "TimeTravelOrder", "X.TimeTravelOrder", AggregateCandidateSource.SingleStreamProjection));
        context.Data.Snapshot = new AggregateSnapshot("TimeTravelOrder", 2, "{\"total\":42}", true);

        IRenderedComponent<StreamDetail> page = Render(context, "&agg=X.TimeTravelOrder&v=2");

        await page.Find(".ms-time-travel button.ms-button-primary").ClickAsync(new());

        page.Find(".ms-time-travel-json").TextContent.Should().Contain("total");
    }

    /// <summary>A replay that produced nothing is a fact, not an empty viewer.</summary>
    [Fact]
    public async Task A_replay_that_produced_nothing_says_so()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();
        context.Data.Candidates.Add(new AggregateTypeCandidate(
            typeof(TimeTravelOrder), "TimeTravelOrder", "X.TimeTravelOrder", AggregateCandidateSource.SingleStreamProjection));
        context.Data.Snapshot = AggregateSnapshot.NotFound("TimeTravelOrder", 2);

        IRenderedComponent<StreamDetail> page = Render(context, "&agg=X.TimeTravelOrder&v=2");

        await page.Find(".ms-time-travel button.ms-button-primary").ClickAsync(new());

        page.Find(".ms-time-travel").TextContent.Should().Contain("produced nothing");
    }

    // ------------------------------------------------------------------------------------------------
    // Archive
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Archiving_is_refused_in_the_ui_by_naming_the_option_that_would_enable_it()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.Data.StreamStates[StreamId] = Stream();

        IRenderedComponent<StreamDetail> page = Render(context);

        page.Find(".ms-capability-disabled").TextContent.Should()
            .Contain("MartenStudioOptions.Capabilities.ArchiveStreams");
        page.FindAll("button.ms-button-danger").Should().BeEmpty();
    }

    /// <summary>
    /// Type-to-confirm: the button does nothing until the stream's own id has been written out, which is
    /// what stops the wrong row being archived from a list of forty that look alike.
    /// </summary>
    [Fact]
    public async Task Archiving_asks_for_the_id_to_be_typed_before_it_will_do_anything()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.Data.StreamStates[StreamId] = Stream();

        IRenderedComponent<StreamDetail> page = Render(context);

        await page.Find("button.ms-button-danger").ClickAsync(new());

        page.Find(".ms-confirm-dialog").TextContent.Should().Contain("Type");
        page.Find(".ms-confirm-actions button.ms-button-danger").HasAttribute("disabled").Should().BeTrue();

        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = "not-the-id" });
        page.Find(".ms-confirm-actions button.ms-button-danger").HasAttribute("disabled").Should().BeTrue();

        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = StreamId });
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        context.Data.Archived.Should().Equal(StreamId);
    }

    [Fact]
    public async Task A_refused_archive_is_shown_on_the_page_and_not_only_in_a_toast()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.Data.StreamStates[StreamId] = Stream();
        context.Data.MutationFailure = new InvalidOperationException("the write policy said no");

        IRenderedComponent<StreamDetail> page = Render(context);

        await page.Find("button.ms-button-danger").ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = StreamId });
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());

        page.Find(".ms-error-alert").TextContent.Should().Contain("the write policy said no");
    }

    [Fact]
    public async Task An_already_archived_stream_offers_no_archive_button()
    {
        using EventsComponentContext context = await NewContextAsync();
        context.WithAllCapabilities();
        context.Data.StreamStates[StreamId] = Stream(archived: true);

        IRenderedComponent<StreamDetail> page = Render(context);

        page.FindAll("button.ms-button-danger").Should().BeEmpty();
        page.Markup.Should().Contain("already archived");
    }

    private static StreamState Stream(bool archived = false, string? tenantId = null) =>
        new(
            StreamId,
            Exists: true,
            "Order",
            3,
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            archived,
            tenantId);

    private static IRenderedComponent<StreamDetail> Render(EventsComponentContext context, string extra = "")
    {
        context.Navigate("marten/events/streams/s?id=" + StreamId + extra);
        return context.Render<StreamDetail>();
    }

    private static async Task<EventsComponentContext> NewContextAsync()
    {
        var context = new EventsComponentContext();
        await context.ReadyAsync();
        return context;
    }
}
