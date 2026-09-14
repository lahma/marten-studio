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
}
