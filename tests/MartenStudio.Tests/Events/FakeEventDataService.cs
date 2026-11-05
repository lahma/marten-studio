using System.Collections.Concurrent;

using JasperFx.Events;

using MartenStudio.Services.Events;

// JasperFx.Events has a StreamState of its own - Marten's FetchStreamStateAsync result. The studio's is
// the page's record and carries the tenant as well; wherever both namespaces are in scope the alias says
// which one is meant rather than leaving it to the reader.
using StreamState = MartenStudio.Services.Events.StreamState;
using PostgresStudio.Services;

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
internal sealed partial class FakeEventDataService : IEventDataService
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

    /// <summary>What a streams list asked for one stream id answers, whatever the id, when set: the rows of every tenant that holds it.</summary>
    public IReadOnlyList<StreamRow>? StreamsForId { get; set; }

    /// <summary>When set, the whole page a streams list asked for one stream id answers (a failed one, for Find by id).</summary>
    public StreamPage? StreamPageForId { get; set; }

    /// <summary>The scope of every streams list, newest last.</summary>
    public List<StudioScope> StreamRequestScopes { get; } = [];

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

    /// <summary>
    /// Gates, one per store key: the first counts read for that store takes its gate and waits on it
    /// before it answers. A read for a store with no gate answers immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What this is for is the Overview's scope race. Two runs of <c>LoadScopeRegionsAsync</c> overlap
    /// whenever the selector moves while a read is in flight, and the only way to test <em>which</em> one
    /// lands is to decide the order they finish in. <see cref="Counts" /> is captured when the call is
    /// made rather than when it returns, so a test can change the answer while a call is held and the
    /// held call still returns what the scope it was asked for would have reported.
    /// </para>
    /// <para>
    /// Keyed by the store, not a queue of "the next read": which of two overlapping runs reaches the fake
    /// first is the scheduler's to decide, and on a loaded machine the run for the scope the visitor just
    /// came back to could take the gate meant for the one it outran - and the test then waited on a gate
    /// only its own later line released, for ever (1.2-HANG). Concurrent, because the held call and the
    /// one that overtakes it run on different threads. A test waits for the held read to be asked
    /// (<see cref="CountsReadStarted" />) before it moves the scope on, and releases its gate in a
    /// <c>finally</c>.
    /// </para>
    /// </remarks>
    public ConcurrentDictionary<string, TaskCompletionSource> CountsGates { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, TaskCompletionSource> countsReadsStarted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Completes when a counts read for <paramref name="storeKey" /> has been asked - and, if a gate is set for it, is about to wait
    /// on it - so a test waits on the read it holds rather than on a poll of the page.
    /// </summary>
    /// <param name="storeKey">The store whose read to wait for.</param>
    public Task CountsReadStarted(string storeKey) => CountsReadSignal(storeKey).Task;

    private TaskCompletionSource CountsReadSignal(string storeKey) =>
        countsReadsStarted.GetOrAdd(storeKey, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

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

    /// <summary>The scope each skip was asked in, in order.</summary>
    public List<StudioScope> SkipScopes { get; } = [];

    /// <summary>The scope each rewind was asked in, in order.</summary>
    public List<StudioScope> RewindScopes { get; } = [];

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
        StreamRequestScopes.Add(scope);

        if (request.StreamId is { Length: > 0 } && StreamPageForId is not null)
        {
            return Task.FromResult(StreamPageForId);
        }

        if (request.StreamId is { Length: > 0 } && StreamsForId is not null)
        {
            // Find by id (1.2): the same id can be a stream in more than one tenant.
            return Task.FromResult(new StreamPage(StreamsForId, null, false));
        }

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

    /// <summary>
    /// Gates on the stream read, by id: a read of a gated id waits until the test completes the gate - which is how a
    /// test holds a page between an id changing and the new stream's answer.
    /// </summary>
    public ConcurrentDictionary<string, TaskCompletionSource> StreamGates { get; } = new(StringComparer.Ordinal);

    public async Task<StreamState> GetStreamAsync(
        StudioScope scope,
        string streamId,
        CancellationToken cancellationToken = default)
    {
        if (StreamGates.TryGetValue(streamId, out TaskCompletionSource? gate))
        {
            // With the token, as the counts gate: a gate nobody completes ends with the page that waited on it.
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return StreamStates.TryGetValue(streamId, out StreamState? state)
            ? state
            : StreamState.Missing(streamId);
    }

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

    public async Task<EventStoreCounts> GetEventStoreCountsAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        EventStoreCounts answer = Counts;

        // The gate is taken before the signal, so a test that sees the signal knows its gate is the one the read waits on.
        CountsGates.TryRemove(scope.StoreKey, out TaskCompletionSource? gate);
        CountsReadSignal(scope.StoreKey).TrySetResult();

        if (gate is not null)
        {
            // With the token: a held gate that nobody completes must end when the caller's region is
            // cancelled, the way a real read does, rather than hanging the test host until it is killed.
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return answer;
    }

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

    /// <summary>A gate the next replay waits on, when set - how a test holds a replay while the page moves on.</summary>
    public TaskCompletionSource? ReplayGate { get; set; }

    public async Task<AggregateSnapshot> AggregateAtVersionAsync(
        StudioScope scope,
        string streamId,
        string aggregateTypeFullName,
        long version,
        CancellationToken cancellationToken = default)
    {
        AggregateSnapshot answer = Snapshot ?? AggregateSnapshot.NotFound(aggregateTypeFullName, version);

        if (ReplayGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return answer;
    }

    /// <summary>What the stepper is handed, by the version asked for; <see cref="Step" /> answers any other.</summary>
    public Dictionary<long, StreamStepWindow> StepWindows { get; } = [];

    /// <summary>What the stepper is handed when <see cref="StepWindows" /> has nothing for the version.</summary>
    public StreamStepWindow? Step { get; set; }

    /// <summary>The steps asked for, as "aggregate@version", newest last.</summary>
    public List<string> StepRequests { get; } = [];

    /// <summary>What the next-change search answers.</summary>
    public StepSearch? Search { get; set; }

    /// <summary>The searches asked for, as "aggregate@after:path", newest last.</summary>
    public List<string> SearchRequests { get; } = [];

    public Task<StreamStepWindow> StepAsync(
        StudioScope scope,
        string streamId,
        string aggregateTypeFullName,
        long version,
        CancellationToken cancellationToken = default)
    {
        StepRequests.Add(aggregateTypeFullName + "@" + version.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return Task.FromResult(
            StepWindows.TryGetValue(version, out StreamStepWindow? window)
                ? window
                : Step ?? StreamStepWindow.NotFound(aggregateTypeFullName, version, AggregateMissingReason.StreamMissing));
    }

    public Task<StepSearch> FindNextChangeAsync(
        StudioScope scope,
        string streamId,
        string aggregateTypeFullName,
        long afterVersion,
        string path,
        CancellationToken cancellationToken = default)
    {
        SearchRequests.Add(
            aggregateTypeFullName + "@" + afterVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + path);

        return Task.FromResult(Search ?? new StepSearch { Path = path, AfterVersion = afterVersion });
    }

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

    /// <summary>The scope every event lookup by sequence was asked with, newest last.</summary>
    public List<StudioScope> EventBySequenceScopes { get; } = [];

    public Task<EventRow?> GetEventBySequenceAsync(
        StudioScope scope,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        EventBySequenceScopes.Add(scope);
        return Task.FromResult(EventBySequence);
    }

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
        SkipScopes.Add(scope);
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
        RewindScopes.Add(scope);
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
