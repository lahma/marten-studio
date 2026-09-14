using JasperFx.Events;

using MartenStudio.Services;
using MartenStudio.Services.Events;

// JasperFx.Events has a StreamState of its own - Marten's FetchStreamStateAsync result. The studio's is
// the page's record and carries the tenant as well; wherever both namespaces are in scope the alias says
// which one is meant rather than leaving it to the reader.
using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Tests.Events;

/// <summary>
/// What the Events pages are given, without a Postgres anywhere.
/// </summary>
/// <remarks>
/// Hand-written rather than a mocking framework's call-matching DSL (the package budget says so, and
/// means it): this is the whole reason <see cref="IEventDataService" /> is a seam. It records what each
/// page asked for, so a test can assert that the feed's filters reached the service rather than only that
/// the markup changed.
/// </remarks>
internal sealed class FakeEventDataService : IEventDataService
{
    /// <summary>What <see cref="DescribeAsync" /> answers.</summary>
    public EventStoreShape Shape { get; set; } = new()
    {
        StreamIdentity = StreamIdentity.AsGuid,
        Schema = "studio_events",
        EventTablesExist = true,
        HasIsArchived = true,
        OptionalColumns = ["mt_dotnet_type"],
    };

    /// <summary>What the streams list answers.</summary>
    public StreamPage Streams { get; set; } = StreamPage.Empty;

    /// <summary>What the stream header answers, by stream id.</summary>
    public Dictionary<string, StreamState> StreamStates { get; } = new(StringComparer.Ordinal);

    /// <summary>The windows the timeline is handed, in order.</summary>
    public Queue<EventPage> StreamWindows { get; } = new();

    /// <summary>What the feed answers.</summary>
    public EventPage Feed { get; set; } = EventPage.Empty;

    /// <summary>What follow mode's delta reads answer, in order.</summary>
    public Queue<EventPage> FeedDeltas { get; } = new();

    /// <summary>What the high-water poll answers, in order; the last one repeats.</summary>
    public Queue<long?> HighestSequences { get; } = new();

    /// <summary>What the event-types screen answers before counts are asked for.</summary>
    public EventTypeList TypesWithoutCounts { get; set; } = new([], false);

    /// <summary>What it answers once they are.</summary>
    public EventTypeList TypesWithCounts { get; set; } = new([], true);

    /// <summary>The aggregate types the time-travel picker is offered.</summary>
    public List<AggregateTypeCandidate> Candidates { get; } = [];

    /// <summary>What a replay answers.</summary>
    public AggregateSnapshot? Snapshot { get; set; }

    /// <summary>What the dead-letter list answers.</summary>
    public DeadLetterPage DeadLetters { get; set; } = DeadLetterPage.Empty;

    /// <summary>What the dead-letter count answers.</summary>
    public long? DeadLetterCount { get; set; }

    /// <summary>
    /// What the dead-letter count throws instead of answering.
    /// </summary>
    /// <remarks>
    /// The real service answers <see langword="null" /> rather than throwing, so this is only for the
    /// tests about a caller that must survive one that does not - the sidebar's badges, above all.
    /// </remarks>
    public Exception? CountFailure { get; set; }

    /// <summary>How many times the dead letters were counted, for the caching tests.</summary>
    public int DeadLetterCountReads { get; private set; }

    /// <summary>What the Overview's stream and event tiles are told.</summary>
    public EventStoreCounts Counts { get; set; } = new(
        EventStoreCount.Estimate(1_200), EventStoreCount.Estimate(9_400), TablesExist: true, Error: null);

    /// <summary>What the Overview's streams panel is given.</summary>
    public RecentStreams Recent { get; set; } = RecentStreams.Empty;

    /// <summary>The rewinds that were asked for, as "projection/sequence".</summary>
    public List<string> Rewinds { get; } = [];

    /// <summary>What an event lookup by sequence answers.</summary>
    public EventRow? EventBySequence { get; set; }

    /// <summary>Thrown by every mutating call, when set - the capability and policy refusals.</summary>
    public Exception? MutationFailure { get; set; }

    /// <summary>The requests the feed made, newest last.</summary>
    public List<EventFeedRequest> FeedRequests { get; } = [];

    /// <summary>The requests the streams list made.</summary>
    public List<StreamListRequest> StreamRequests { get; } = [];

    /// <summary>The dead-letter queries.</summary>
    public List<DeadLetterQuery> DeadLetterQueries { get; } = [];

    /// <summary>The streams that were archived.</summary>
    public List<string> Archived { get; } = [];

    /// <summary>The dead letters that were discarded.</summary>
    public List<Guid> Discarded { get; } = [];

    /// <summary>The event sequences that were marked skipped.</summary>
    public List<long> Skipped { get; } = [];

    /// <summary>Whether counts were asked for.</summary>
    public bool CountsRequested { get; private set; }

    public Task<EventStoreShape> DescribeAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(Shape);

    public Task<StreamPage> ListStreamsAsync(
        StudioScope scope,
        StreamListRequest request,
        CancellationToken cancellationToken = default)
    {
        StreamRequests.Add(request);

        if (request.StreamId is { Length: > 0 } id)
        {
            // The header read goes through the same method with a single-row request.
            StreamRow? row = Streams.Rows.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            return Task.FromResult(row is null
                ? StreamPage.Empty
                : new StreamPage([row], null, false));
        }

        return Task.FromResult(Streams);
    }

    public Task<StreamState> GetStreamAsync(
        StudioScope scope,
        string streamId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(StreamStates.TryGetValue(streamId, out StreamState? state)
            ? state
            : StreamState.Missing(streamId));

    public Task<EventPage> GetStreamEventsAsync(
        StudioScope scope,
        string streamId,
        long afterVersion,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(StreamWindows.Count > 0 ? StreamWindows.Dequeue() : EventPage.Empty);

    public Task<EventPage> GetFeedAsync(
        StudioScope scope,
        EventFeedRequest request,
        CancellationToken cancellationToken = default)
    {
        FeedRequests.Add(request);

        if (request.SinceSequence is not null)
        {
            return Task.FromResult(FeedDeltas.Count > 0 ? FeedDeltas.Dequeue() : EventPage.Empty);
        }

        return Task.FromResult(Feed);
    }

    public Task<long?> GetHighestSequenceAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        if (HighestSequences.Count == 0)
        {
            return Task.FromResult<long?>(null);
        }

        // The last answer repeats, so a poll that ticks more often than the test cares about is stable.
        long? next = HighestSequences.Count == 1 ? HighestSequences.Peek() : HighestSequences.Dequeue();
        return Task.FromResult(next);
    }

    public Task<EventStoreCounts> GetEventStoreCountsAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Counts);

    public Task<RecentStreams> GetRecentStreamsAsync(
        StudioScope scope,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Recent);

    public Task<EventTypeList> ListEventTypesAsync(
        StudioScope scope,
        bool withCounts,
        CancellationToken cancellationToken = default)
    {
        CountsRequested |= withCounts;
        return Task.FromResult(withCounts ? TypesWithCounts : TypesWithoutCounts);
    }

    public Task<IReadOnlyList<AggregateTypeCandidate>> ListAggregateCandidatesAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AggregateTypeCandidate>>(Candidates);

    public Task<AggregateSnapshot> AggregateAtVersionAsync(
        StudioScope scope,
        string streamId,
        string aggregateTypeFullName,
        long version,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Snapshot ?? AggregateSnapshot.NotFound(aggregateTypeFullName, version));

    public Task<DeadLetterPage> ListDeadLettersAsync(
        StudioScope scope,
        DeadLetterQuery query,
        CancellationToken cancellationToken = default)
    {
        DeadLetterQueries.Add(query);
        return Task.FromResult(DeadLetters);
    }

    public Task<long?> CountDeadLettersAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        DeadLetterCountReads++;

        return CountFailure is null
            ? Task.FromResult(DeadLetterCount)
            : Task.FromException<long?>(CountFailure);
    }

    public Task<EventRow?> GetEventBySequenceAsync(
        StudioScope scope,
        long sequence,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(EventBySequence);

    public Task ArchiveStreamAsync(StudioScope scope, string streamId, CancellationToken cancellationToken = default)
    {
        if (MutationFailure is not null)
        {
            return Task.FromException(MutationFailure);
        }

        Archived.Add(streamId);
        return Task.CompletedTask;
    }

    public Task DiscardDeadLetterAsync(StudioScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        if (MutationFailure is not null)
        {
            return Task.FromException(MutationFailure);
        }

        Discarded.Add(id);
        return Task.CompletedTask;
    }

    public Task SkipEventAsync(StudioScope scope, long sequence, CancellationToken cancellationToken = default)
    {
        if (MutationFailure is not null)
        {
            return Task.FromException(MutationFailure);
        }

        Skipped.Add(sequence);
        return Task.CompletedTask;
    }

    public Task RewindSubscriptionAsync(
        StudioScope scope,
        string projectionName,
        long eventSequence,
        CancellationToken cancellationToken = default)
    {
        if (MutationFailure is not null)
        {
            return Task.FromException(MutationFailure);
        }

        Rewinds.Add(projectionName + "/" + eventSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    /// <summary>An event row with everything a card can draw.</summary>
    public static EventRow Event(
        long sequence,
        long version = 1,
        string type = "OrderPlaced",
        string streamId = "11111111-1111-1111-1111-111111111111",
        string? json = "{\"total\":10}",
        bool hasBinary = false,
        long? binaryLength = null,
        bool isArchived = false,
        bool isSkipped = false,
        IReadOnlyDictionary<string, string?>? metadata = null) =>
        new(
            sequence,
            Guid.NewGuid(),
            streamId,
            version,
            type,
            new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero).AddSeconds(sequence),
            null,
            "Sample.Domain." + type,
            isArchived,
            isSkipped,
            hasBinary,
            binaryLength,
            json,
            metadata ?? new Dictionary<string, string?>(StringComparer.Ordinal));

    /// <summary>A stream row.</summary>
    public static StreamRow Stream(
        string id,
        string? type = "Order",
        long version = 3,
        bool archived = false,
        string? tenantId = null) =>
        new(
            id,
            type,
            version,
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            archived,
            tenantId);

    /// <summary>A dead letter row.</summary>
    public static DeadLetterRow DeadLetter(
        Guid id,
        long sequence = 42,
        string projection = "OrderSummary",
        string shard = "OrderSummary:All",
        string exceptionType = "System.DivideByZeroException") =>
        new(
            id,
            projection,
            shard,
            new DateTimeOffset(2026, 9, 14, 9, 30, 0, TimeSpan.Zero),
            exceptionType,
            "Attempted to divide by zero.",
            sequence,
            "*DEFAULT*");
}
