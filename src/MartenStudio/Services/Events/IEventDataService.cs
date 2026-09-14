namespace MartenStudio.Services.Events;

/// <summary>
/// What the Events screens read and change.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes a <see cref="StudioScope" /> and resolves it through
/// <see cref="StudioScopeResolver" /> before it touches anything (D5, D12). None accepts an
/// <c>IDocumentStore</c>, an <c>IMartenDatabase</c> or a connection: an already-resolved object is one
/// that has passed authorization, and a method that accepted one could be handed something resolved for
/// a different visitor.
/// </para>
/// <para>
/// Reads return their failures as values - <see cref="EventDataError" /> on the page's own record - so a
/// database that is down breaks one region and never navigation (plan §4.8). The three mutating methods
/// throw instead: <see cref="StudioCapabilityDeniedException" /> when the capability is off and
/// <see cref="StudioNotAuthorizedException" /> when the write policy refuses, both after the audit entry
/// has been written.
/// </para>
/// <para>
/// An interface, so the component tests can hand a page its facts without a Postgres.
/// </para>
/// </remarks>
internal interface IEventDataService
{
    /// <summary>
    /// What this store's event tables actually look like: the stream identity, the schema and which of
    /// the optional columns exist.
    /// </summary>
    Task<EventStoreShape> DescribeAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>One page of the stream list.</summary>
    Task<StreamPage> ListStreamsAsync(
        StudioScope scope,
        StreamListRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>The header facts about one stream, or a value saying it is not there.</summary>
    Task<StreamState> GetStreamAsync(
        StudioScope scope,
        string streamId,
        CancellationToken cancellationToken = default);

    /// <summary>One version window of a stream's events, oldest first.</summary>
    Task<EventPage> GetStreamEventsAsync(
        StudioScope scope,
        string streamId,
        long afterVersion,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>One page of the global feed, newest first.</summary>
    Task<EventPage> GetFeedAsync(
        StudioScope scope,
        EventFeedRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The highest event sequence in the scope's database, which is what follow mode polls.
    /// </summary>
    /// <returns>The high-water sequence, or <see langword="null" /> when it could not be read.</returns>
    Task<long?> GetHighestSequenceAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// How many streams and events this database holds, for the Overview's tiles.
    /// </summary>
    /// <remarks>
    /// Estimates by default (D8), and never through <c>FetchEventStoreStatistics</c>, which applies the
    /// event store's migration before it counts - see <see cref="EventStoreCounts" />.
    /// </remarks>
    Task<EventStoreCounts> GetEventStoreCountsAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The newest streams, with the store's append mode so the panel can name itself honestly.
    /// </summary>
    /// <param name="scope">The store, database and tenant.</param>
    /// <param name="take">How many rows to read. Capped by the service.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<RecentStreams> GetRecentStreamsAsync(
        StudioScope scope,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every event type the store knows, plus every type seen in <c>mt_events</c> that it does not.
    /// </summary>
    /// <param name="scope">The store, database and tenant.</param>
    /// <param name="withCounts">
    /// Whether to run the <c>group by type</c> aggregate. Off by default and behind a button: it is a
    /// full scan of the events table (D8's reasoning, applied to events).
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<EventTypeList> ListEventTypesAsync(
        StudioScope scope,
        bool withCounts,
        CancellationToken cancellationToken = default);

    /// <summary>The aggregate types this store's streams can be replayed into.</summary>
    Task<IReadOnlyList<AggregateTypeCandidate>> ListAggregateCandidatesAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>Replays one stream into one aggregate type as it stood at a version.</summary>
    Task<AggregateSnapshot> AggregateAtVersionAsync(
        StudioScope scope,
        string streamId,
        string aggregateTypeFullName,
        long version,
        CancellationToken cancellationToken = default);

    /// <summary>One page of dead letters.</summary>
    Task<DeadLetterPage> ListDeadLettersAsync(
        StudioScope scope,
        DeadLetterQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many dead letters there are, for the badge. Zero and "could not tell" are different answers,
    /// so a failure is <see langword="null" /> rather than zero.
    /// </summary>
    Task<long?> CountDeadLettersAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>The event a dead letter is about, so its body can be shown beside the exception.</summary>
    Task<EventRow?> GetEventBySequenceAsync(
        StudioScope scope,
        long sequence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives one stream: <c>ArchiveStreams</c>, the write policy, <c>session.Events.ArchiveStream</c>
    /// and <c>SaveChangesAsync</c>, audited either way.
    /// </summary>
    /// <exception cref="StudioCapabilityDeniedException">The capability is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The write policy refused this scope.</exception>
    Task ArchiveStreamAsync(StudioScope scope, string streamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes one dead letter document. The projection is not touched: this only forgets the record of
    /// the failure.
    /// </summary>
    /// <exception cref="StudioCapabilityDeniedException">The capability is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The write policy refused this scope.</exception>
    Task DiscardDeadLetterAsync(StudioScope scope, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one event as skipped, so the daemon steps over it on the next attempt
    /// (<c>IMartenDatabase.MarkEventsAsSkipped</c>).
    /// </summary>
    /// <remarks>
    /// Only possible on a store that enabled event skipping: the call writes <c>mt_events.is_skipped</c>,
    /// and a store without that column has no way to express the idea. The page asks
    /// <see cref="EventStoreShape.HasIsSkipped" /> before offering the action, and this refuses with the
    /// same explanation when something asks anyway.
    /// </remarks>
    /// <exception cref="StudioCapabilityDeniedException">The capability is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The write policy refused this scope.</exception>
    /// <exception cref="InvalidOperationException">This store does not record skipped events.</exception>
    Task SkipEventAsync(StudioScope scope, long sequence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewinds one subscription so that the daemon re-applies <paramref name="eventSequence" /> and
    /// everything after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The third dead-letter action the design names, and the only one that needs a running daemon: it
    /// goes through <c>DaemonAccessor</c> to the coordinator the host registered and never through
    /// <c>store.BuildProjectionDaemonAsync()</c> (AGENTS.md hard rule 11). A process that hosts no daemon
    /// gets <see cref="MartenStudio.Services.Projections.StudioDaemonNotHostedException" />, which is a
    /// value the page renders rather than a fault.
    /// </para>
    /// <para>
    /// This re-runs every event from <paramref name="eventSequence" /> to the high-water mark through that
    /// projection, and Marten deletes the projection's existing dead letters at or above the floor on the
    /// way - so the record being looked at disappears whether or not the replay succeeds.
    /// </para>
    /// </remarks>
    /// <param name="scope">The store, database and tenant.</param>
    /// <param name="projectionName">The projection or subscription to rewind, as the dead letter names it.</param>
    /// <param name="eventSequence">
    /// The sequence to re-apply. The floor handed to Marten is one <em>below</em> this, because the
    /// progression row records the last sequence the shard has already done.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="StudioCapabilityDeniedException">The capability is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The write policy refused this scope.</exception>
    /// <exception cref="MartenStudio.Services.Projections.StudioDaemonNotHostedException">
    /// No async daemon is hosted in this process.
    /// </exception>
    Task RewindSubscriptionAsync(
        StudioScope scope,
        string projectionName,
        long eventSequence,
        CancellationToken cancellationToken = default);
}
