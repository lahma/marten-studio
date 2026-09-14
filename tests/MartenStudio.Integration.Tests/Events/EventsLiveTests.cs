using JasperFx.Events;

using MartenStudio.Services;
using MartenStudio.Services.Events;

using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// The Events area against a real Marten event store, driven through <see cref="IEventDataService" />
/// with a real <see cref="StudioScopeResolver" /> - so the scope resolution, the capability gate and the
/// audit entry are exercised rather than bypassed.
/// </summary>
/// <remarks>
/// The store, the schema and the seed are the class fixture's, built once for the whole class. Tests
/// that change something therefore pick a stream nothing else asserts about, and the assertions that
/// could see another test's write are written to be order-independent.
/// </remarks>
public class EventsLiveTests(EventsStoreFixture fixture) : IClassFixture<EventsStoreFixture>
{
    private EventsFixture Events => fixture.Events;

    private string Schema => fixture.Schema;

    // ------------------------------------------------------------------------------------------------
    // Shape
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The column set is discovered, not assumed: this store turned on correlation, causation, headers
    /// and event skipping, and every one of those is a column that another store would not have.
    /// </summary>
    [PostgresFact]
    public async Task The_shape_reports_what_this_stores_event_tables_actually_have()
    {
        EventStoreShape shape = await Events.Service.DescribeAsync(Events.Scope, TestContext.Current.CancellationToken);

        shape.Available.Should().BeTrue();
        shape.EventTablesExist.Should().BeTrue();
        shape.Schema.Should().Be(Schema);
        shape.StreamIdentity.Should().Be(StreamIdentity.AsGuid);
        shape.HasIsArchived.Should().BeTrue();
        shape.HasIsSkipped.Should().BeTrue("the store enabled event skipping");
        shape.HasBinaryData.Should().BeTrue("mt_events always has a bdata column");
        shape.OptionalColumns.Should().Contain("correlation_id").And.Contain("causation_id").And.Contain("headers");
        shape.OptionalColumns.Should().NotContain("user_name", "this store did not enable it");
    }

    // ------------------------------------------------------------------------------------------------
    // Streams
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_stream_list_pages_by_keyset_and_never_repeats_a_row()
    {
        IEventDataService service = Events.Service;
        CancellationToken token = TestContext.Current.CancellationToken;

        List<string> seen = [];
        StreamCursor? cursor = null;

        for (int page = 0; page < 10; page++)
        {
            StreamPage current = await service.ListStreamsAsync(
                Events.Scope,
                new StreamListRequest { PageSize = 7, Cursor = cursor, IncludeArchived = true },
                token);

            current.Error.Should().BeNull();
            seen.AddRange(current.Rows.Select(x => x.Id));

            if (!current.HasMore)
            {
                break;
            }

            cursor = current.NextCursor;
            cursor.Should().NotBeNull("a page that has more must say where the next one starts");
        }

        seen.Should().OnlyHaveUniqueItems();
        seen.Should().HaveCount(EventsFixture.SeededStreamCount);
    }

    /// <summary>
    /// Archived streams are hidden by default and shown when asked for, which is the raw builder's own
    /// flag rather than Marten's <c>MaybeArchived()</c> - the studio never goes through LINQ here.
    /// </summary>
    [PostgresFact]
    public async Task An_archived_stream_is_hidden_by_default_and_listed_when_asked_for()
    {
        IEventDataService service = Events.Service;
        CancellationToken token = TestContext.Current.CancellationToken;

        StreamPage hidden = await service.ListStreamsAsync(
            Events.Scope, new StreamListRequest { PageSize = 500 }, token);
        StreamPage shown = await service.ListStreamsAsync(
            Events.Scope, new StreamListRequest { PageSize = 500, IncludeArchived = true }, token);

        string archived = Events.ArchivedStreamId.ToString();

        hidden.Rows.Should().NotContain(x => x.Id == archived);
        shown.Rows.Should().Contain(x => x.Id == archived && x.IsArchived);

        // Order-independent: another test in this class archives a stream of its own, so the difference
        // is "at least one", not "exactly one".
        shown.Rows.Count.Should().BeGreaterThan(hidden.Rows.Count);
        hidden.Rows.Should().OnlyContain(x => !x.IsArchived);
    }

    /// <summary>
    /// <c>mt_streams.type</c> holds Marten's <em>alias</em> for the aggregate, not its CLR name: a stream
    /// started as <c>OrderSummary</c> reads back as <c>order_summary</c>. The prefix filter therefore
    /// matches what the column holds, which is what the screen shows.
    /// </summary>
    [PostgresFact]
    public async Task The_type_prefix_filter_matches_the_aggregate_alias_the_column_holds()
    {
        IEventDataService service = Events.Service;
        CancellationToken token = TestContext.Current.CancellationToken;

        StreamPage matching = await service.ListStreamsAsync(
            Events.Scope,
            new StreamListRequest { TypePrefix = "order_", PageSize = 500, IncludeArchived = true },
            token);

        matching.Rows.Should().NotBeEmpty();
        matching.Rows.Should().OnlyContain(x => x.AggregateType == "order_summary");

        StreamPage none = await service.ListStreamsAsync(
            Events.Scope,
            new StreamListRequest { TypePrefix = "Invoice", PageSize = 500, IncludeArchived = true },
            token);

        none.Rows.Should().BeEmpty();
    }

    /// <summary>A prefix that contains LIKE metacharacters is a prefix, not a pattern.</summary>
    [PostgresFact]
    public async Task A_type_prefix_full_of_like_metacharacters_matches_nothing_rather_than_everything()
    {
        StreamPage page = await Events.Service.ListStreamsAsync(
            Events.Scope,
            new StreamListRequest { TypePrefix = "%", PageSize = 500, IncludeArchived = true },
            TestContext.Current.CancellationToken);

        page.Error.Should().BeNull();
        page.Rows.Should().BeEmpty("'%' is escaped, so it is a literal percent sign");
    }

    [PostgresFact]
    public async Task The_recently_active_ordering_puts_the_newest_appended_stream_first()
    {
        StreamPage page = await Events.Service.ListStreamsAsync(
            Events.Scope,
            new StreamListRequest { Order = StreamListOrder.RecentlyActive, PageSize = 5, IncludeArchived = true },
            TestContext.Current.CancellationToken);

        page.Error.Should().BeNull();
        page.Rows.Should().NotBeEmpty();
        page.Rows[0].Id.Should().Be(Events.StreamIds[^1].ToString(), "the seed wrote the streams in order");
    }

    [PostgresFact]
    public async Task One_stream_reads_back_with_its_version_and_its_timestamps()
    {
        Guid id = Events.StreamIds[0];

        StreamState state = await Events.Service.GetStreamAsync(
            Events.Scope, id.ToString(), TestContext.Current.CancellationToken);

        state.Exists.Should().BeTrue();
        state.Id.Should().Be(id.ToString());
        state.Version.Should().BeGreaterThan(0);
        state.Created.Should().NotBeNull();
        state.LastEvent.Should().NotBeNull();
        state.IsArchived.Should().BeFalse();
    }

    [PostgresFact]
    public async Task A_stream_that_is_not_there_is_a_value_rather_than_an_exception()
    {
        StreamState state = await Events.Service.GetStreamAsync(
            Events.Scope, Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        state.Exists.Should().BeFalse();
        state.Error.Should().BeNull();
    }

    /// <summary>A half-pasted GUID is a message, not an exception.</summary>
    [PostgresFact]
    public async Task An_unparseable_stream_id_is_an_error_value_on_the_timeline()
    {
        EventPage page = await Events.Service.GetStreamEventsAsync(
            Events.Scope, "not-a-guid", 0, 50, TestContext.Current.CancellationToken);

        page.Error.Should().NotBeNull();
        page.Error!.Message.Should().Contain("not a GUID");
        page.Rows.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // Timeline
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_streams_timeline_comes_back_in_version_order_with_its_metadata()
    {
        Guid id = Events.StreamIds[2];

        EventPage page = await Events.Service.GetStreamEventsAsync(
            Events.Scope, id.ToString(), 0, 50, TestContext.Current.CancellationToken);

        page.Error.Should().BeNull();
        page.Rows.Should().NotBeEmpty();
        page.Rows.Select(x => x.Version).Should().BeInAscendingOrder();
        page.Rows[0].EventType.Should().Be("order_placed");
        page.Rows[0].StreamId.Should().Be(id.ToString());
        page.Rows[0].DotNetType.Should().NotBeNullOrWhiteSpace();
        page.Rows[0].Json.Should().Contain("customer-2");

        // The metadata the store turned on, read off the row rather than guessed at.
        page.Rows[0].Metadata.Should().ContainKey("correlation_id");
        page.Rows[0].Metadata["correlation_id"].Should().Be(Events.CorrelationId);
        page.Rows[0].Metadata.Should().ContainKey("headers");

        // bdata is never selected: what comes back is whether there is one, not what it is.
        page.Rows[0].HasBinary.Should().BeFalse();
    }

    [PostgresFact]
    public async Task The_timeline_window_loads_more_from_the_version_it_reached()
    {
        Guid id = Events.StreamIds[2];
        CancellationToken token = TestContext.Current.CancellationToken;

        EventPage first = await Events.Service.GetStreamEventsAsync(Events.Scope, id.ToString(), 0, 1, token);

        first.Rows.Should().ContainSingle();
        first.HasMore.Should().BeTrue();

        EventPage second = await Events.Service.GetStreamEventsAsync(
            Events.Scope, id.ToString(), first.Rows[^1].Version, 50, token);

        second.Rows.Should().NotBeEmpty();
        second.Rows[0].Version.Should().Be(first.Rows[^1].Version + 1);
    }

    // ------------------------------------------------------------------------------------------------
    // Feed
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_feed_keysets_on_the_sequence_and_never_repeats_an_event()
    {
        IEventDataService service = Events.Service;
        CancellationToken token = TestContext.Current.CancellationToken;

        List<long> seen = [];
        long? after = null;

        for (int page = 0; page < 20; page++)
        {
            EventPage current = await service.GetFeedAsync(
                Events.Scope, new EventFeedRequest { PageSize = 13, AfterSequence = after, IncludeArchived = true }, token);

            current.Error.Should().BeNull();
            seen.AddRange(current.Rows.Select(x => x.Sequence));

            if (!current.HasMore)
            {
                break;
            }

            after = current.NextSequence;
        }

        seen.Should().OnlyHaveUniqueItems();
        seen.Should().BeInDescendingOrder();
    }

    [PostgresFact]
    public async Task The_feed_filters_by_several_event_types_at_once()
    {
        EventPage page = await Events.Service.GetFeedAsync(
            Events.Scope,
            new EventFeedRequest { EventTypes = ["order_placed", "order_shipped"], PageSize = 500, IncludeArchived = true },
            TestContext.Current.CancellationToken);

        page.Rows.Should().NotBeEmpty();
        page.Rows.Should().OnlyContain(x => x.EventType == "order_placed" || x.EventType == "order_shipped");
        page.Rows.Should().Contain(x => x.EventType == "order_shipped");
    }

    [PostgresFact]
    public async Task Archived_events_are_out_of_the_feed_unless_asked_for()
    {
        IEventDataService service = Events.Service;
        CancellationToken token = TestContext.Current.CancellationToken;
        string archived = Events.ArchivedStreamId.ToString();

        EventPage hidden = await service.GetFeedAsync(
            Events.Scope, new EventFeedRequest { StreamId = archived, PageSize = 100 }, token);
        EventPage shown = await service.GetFeedAsync(
            Events.Scope, new EventFeedRequest { StreamId = archived, PageSize = 100, IncludeArchived = true }, token);

        hidden.Rows.Should().BeEmpty();
        shown.Rows.Should().NotBeEmpty();
        shown.Rows.Should().OnlyContain(x => x.IsArchived);
    }

    [PostgresFact]
    public async Task A_time_range_that_has_not_happened_yet_returns_nothing()
    {
        EventPage page = await Events.Service.GetFeedAsync(
            Events.Scope,
            new EventFeedRequest { From = DateTimeOffset.UtcNow.AddDays(1), PageSize = 100 },
            TestContext.Current.CancellationToken);

        page.Error.Should().BeNull();
        page.Rows.Should().BeEmpty();
    }

    /// <summary>Follow mode's delta read: everything after a sequence the page already has.</summary>
    [PostgresFact]
    public async Task The_delta_read_returns_only_what_is_newer_than_the_sequence_it_is_given()
    {
        IEventDataService service = Events.Service;
        CancellationToken token = TestContext.Current.CancellationToken;

        EventPage newest = await service.GetFeedAsync(
            Events.Scope, new EventFeedRequest { PageSize = 5, IncludeArchived = true }, token);

        long floor = newest.Rows[^1].Sequence;

        EventPage delta = await service.GetFeedAsync(
            Events.Scope,
            new EventFeedRequest { SinceSequence = floor, PageSize = 100, IncludeArchived = true },
            token);

        delta.Rows.Should().NotBeEmpty();
        delta.Rows.Should().OnlyContain(x => x.Sequence > floor);
        delta.HasMore.Should().BeFalse("a delta is everything there is, not a page of it");
    }

    [PostgresFact]
    public async Task The_high_water_mark_comes_from_Marten_and_matches_the_newest_event()
    {
        IEventDataService service = Events.Service;
        CancellationToken token = TestContext.Current.CancellationToken;

        long? highest = await service.GetHighestSequenceAsync(Events.Scope, token);

        EventPage newest = await service.GetFeedAsync(
            Events.Scope, new EventFeedRequest { PageSize = 1, IncludeArchived = true }, token);

        highest.Should().NotBeNull();
        highest.Should().BeGreaterThanOrEqualTo(newest.Rows[0].Sequence);
    }

    // ------------------------------------------------------------------------------------------------
    // Event types
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_registered_event_types_are_listed_without_counting_anything()
    {
        EventTypeList list = await Events.Service.ListEventTypesAsync(
            Events.Scope, withCounts: false, TestContext.Current.CancellationToken);

        list.Error.Should().BeNull();
        list.CountsLoaded.Should().BeFalse();
        list.Types.Should().Contain(x => x.Name == "order_placed" && x.IsRegistered);
        list.Types.Should().OnlyContain(x => x.Count == null, "nothing was counted");
    }

    [PostgresFact]
    public async Task Loading_counts_reports_the_counts_and_the_sequence_range()
    {
        EventTypeList list = await Events.Service.ListEventTypesAsync(
            Events.Scope, withCounts: true, TestContext.Current.CancellationToken);

        list.CountsLoaded.Should().BeTrue();

        EventTypeInfo placed = list.Types.Single(x => x.Name == "order_placed");
        placed.Count.Should().Be(EventsFixture.SeededStreamCount);
        placed.FirstSequence.Should().BeGreaterThan(0);
        placed.LastSequence.Should().BeGreaterThanOrEqualTo(placed.FirstSequence!.Value);

        // A registered type nothing ever appended reports zero rather than "unknown".
        list.Types.Single(x => x.Name == "poison_pill").Count.Should().Be(0);
    }

    // ------------------------------------------------------------------------------------------------
    // Time travel
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_registered_single_stream_projection_is_offered_as_a_candidate()
    {
        IReadOnlyList<AggregateTypeCandidate> candidates = await Events.Service.ListAggregateCandidatesAsync(
            Events.Scope, TestContext.Current.CancellationToken);

        candidates.Should().Contain(x =>
            x.Type == typeof(OrderSummary) && x.Source == AggregateCandidateSource.SingleStreamProjection);
    }

    /// <summary>
    /// The whole point of the panel: version 1 of a stream is not version 3 of it.
    /// </summary>
    [PostgresFact]
    public async Task Replaying_a_stream_to_a_version_shows_what_it_looked_like_then()
    {
        // Index 3 gets one OrderPlaced and two ItemAdded (3 % 3 == 0 gives one line; pick one with more).
        Guid id = Events.StreamIds[2];
        string aggregate = typeof(OrderSummary).FullName!;
        CancellationToken token = TestContext.Current.CancellationToken;

        AggregateSnapshot atOne = await Events.Service.AggregateAtVersionAsync(
            Events.Scope, id.ToString(), aggregate, 1, token);
        AggregateSnapshot atEnd = await Events.Service.AggregateAtVersionAsync(
            Events.Scope, id.ToString(), aggregate, 0, token);

        atOne.Error.Should().BeNull();
        atOne.Found.Should().BeTrue();
        atOne.Json.Should().Contain("customer-2");
        atOne.Json.Should().Contain("\"LineCount\":0", "at version 1 no line had been added yet")
            .And.NotBeNull();

        atEnd.Found.Should().BeTrue();
        atEnd.Json.Should().NotBe(atOne.Json, "the aggregate grew");
    }

    /// <summary>
    /// A browser can send any string; only the store's own types are replayable, and nothing is ever
    /// resolved with <c>Type.GetType</c>.
    /// </summary>
    [PostgresFact]
    public async Task A_type_name_the_store_does_not_know_is_refused()
    {
        AggregateSnapshot snapshot = await Events.Service.AggregateAtVersionAsync(
            Events.Scope,
            Events.StreamIds[0].ToString(),
            "System.IO.FileInfo",
            1,
            TestContext.Current.CancellationToken);

        snapshot.Error.Should().NotBeNull();
        snapshot.Error!.Message.Should().Contain("not one of this store's aggregate types");
    }

    // ------------------------------------------------------------------------------------------------
    // Archive
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Archiving_is_refused_at_the_service_when_the_capability_is_off()
    {
        Guid id = Events.StreamIds[1];

        Func<Task> archive = () => Events.ReadOnlyService.ArchiveStreamAsync(
            Events.Scope, id.ToString(), TestContext.Current.CancellationToken);

        (await archive.Should().ThrowAsync<StudioCapabilityDeniedException>())
            .Which.Message.Should().Contain("MartenStudioOptions.Capabilities.ArchiveStreams");

        StreamState after = await Events.Service.GetStreamAsync(
            Events.Scope, id.ToString(), TestContext.Current.CancellationToken);

        after.IsArchived.Should().BeFalse("a refusal must not have changed anything");
    }

    [PostgresFact]
    public async Task Archiving_marks_the_stream_and_writes_an_audit_entry()
    {
        Guid id = Events.StreamIds[3];
        CancellationToken token = TestContext.Current.CancellationToken;

        await Events.Service.ArchiveStreamAsync(Events.Scope, id.ToString(), token);

        StreamState after = await Events.Service.GetStreamAsync(Events.Scope, id.ToString(), token);
        after.IsArchived.Should().BeTrue();

        Events.Audit.GetLatest().Should().Contain(x =>
            x.Action == "Archive stream" && x.Target == id.ToString() && x.Succeeded);
    }

    [PostgresFact]
    public async Task A_refused_archive_is_audited_too()
    {
        Guid id = Events.StreamIds[4];

        Func<Task> archive = () => Events.ReadOnlyService.ArchiveStreamAsync(
            Events.Scope, id.ToString(), TestContext.Current.CancellationToken);

        await archive.Should().ThrowAsync<StudioCapabilityDeniedException>();

        // Recorded in the read-only container's own ring - an audit that only records what worked cannot
        // answer the question people ask after an incident, which is what someone tried.
        Events.ReadOnlyAudit.GetLatest().Should().Contain(x =>
            x.Action == "Archive stream" && x.Target == id.ToString() && !x.Succeeded
            && x.Capability == nameof(StudioCapability.ArchiveStreams));
    }

    // ------------------------------------------------------------------------------------------------
    // Skipping
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Marking_an_event_skipped_writes_the_column_and_the_feed_can_hide_it()
    {
        IEventDataService service = Events.Service;
        CancellationToken token = TestContext.Current.CancellationToken;

        EventPage timeline = await service.GetStreamEventsAsync(
            Events.Scope, Events.StreamIds[5].ToString(), 0, 50, token);

        long sequence = timeline.Rows[^1].Sequence;

        await service.SkipEventAsync(Events.Scope, sequence, token);

        EventRow? skipped = await service.GetEventBySequenceAsync(Events.Scope, sequence, token);
        skipped.Should().NotBeNull();
        skipped!.IsSkipped.Should().BeTrue();

        EventPage without = await service.GetFeedAsync(
            Events.Scope,
            new EventFeedRequest { StreamId = Events.StreamIds[5].ToString(), IncludeSkipped = false, PageSize = 100 },
            token);

        without.Rows.Should().NotContain(x => x.Sequence == sequence);
    }

    [PostgresFact]
    public async Task Skipping_is_refused_at_the_service_when_the_capability_is_off()
    {
        Func<Task> skip = () => Events.ReadOnlyService.SkipEventAsync(
            Events.Scope, 1, TestContext.Current.CancellationToken);

        await skip.Should().ThrowAsync<StudioCapabilityDeniedException>();
    }

    // ------------------------------------------------------------------------------------------------
    // Dead letters, when there are none
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A store whose daemon never failed has no dead letters, and that is a real zero rather than a
    /// failure to count - the two are drawn differently.
    /// </summary>
    [PostgresFact]
    public async Task A_store_with_no_failures_counts_zero_dead_letters_rather_than_failing_to_count()
    {
        long? count = await Events.Service.CountDeadLettersAsync(Events.Scope, TestContext.Current.CancellationToken);

        count.Should().Be(0);

        DeadLetterPage page = await Events.Service.ListDeadLettersAsync(
            Events.Scope, new DeadLetterQuery(), TestContext.Current.CancellationToken);

        page.Error.Should().BeNull();
        page.Rows.Should().BeEmpty();
    }
}
