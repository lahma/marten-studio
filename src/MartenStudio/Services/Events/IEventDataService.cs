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
/// database that is down breaks one region and never navigation (plan §4.8). The mutating methods
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

    /// <summary>
    /// One page of dead letters - or, when <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> hides
    /// <c>DeadLetterEvent</c> (or throws for it), <see cref="DeadLetterPage.Hidden" />, read from nothing.
    /// </summary>
    Task<DeadLetterPage> ListDeadLettersAsync(
        StudioScope scope,
        DeadLetterQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How many dead letters there are, for the badge. Zero and "no number" are different answers, so
    /// a failure, a refusal and a hidden <c>DeadLetterEvent</c> are all <see langword="null" /> rather than zero.
    /// </summary>
    /// <remarks>
    /// The number is the whole database's: <c>mt_doc_deadletterevent</c> is single-tenanted on every
    /// store and has no <c>tenant_id</c> column to narrow by. A scope pinned to a tenant therefore gets
    /// <see langword="null" /> and no badge, because the only count available is of other tenants'
    /// failures as much as their own — see <see cref="ListDeadLettersAsync" />, which narrows on the
    /// document's own <c>TenantId</c> property and so shows something different.
    /// </remarks>
    Task<long?> CountDeadLettersAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>The event a dead letter is about, so its body can be shown beside the exception.</summary>
    Task<EventRow?> GetEventBySequenceAsync(
        StudioScope scope,
        long sequence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every DCB tag type this store registers: how tags are kept, each tag table's estimate, and whether the
    /// feed's <c>tag:</c> filter on it is index-backed.
    /// </summary>
    /// <remarks>
    /// Estimates only (D8), and none for a tenant-scoped visitor on a conjoined store, whose whole-table
    /// <c>reltuples</c> would be every tenant's tags (<see cref="EventTagText.CrossTenantEstimateNote" />).
    /// Read from <c>StoreOptions</c>, <c>information_schema</c>, <c>pg_class</c> and <c>pg_indexes</c>; it
    /// creates nothing.
    /// </remarks>
    Task<EventTagTypeList> ListTagTypesAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tags of these events, for a page whose tags were deferred because reading them scans the tag
    /// tables (<see cref="EventTagLoadState.Deferred" />).
    /// </summary>
    /// <remarks>
    /// Narrowed to the scope's tenant in the statement itself, so sequences handed in from anywhere return
    /// only tags the scope may see. At most <c>EventTagQueries.MaxSequences</c> events are looked up, under
    /// the studio's <c>QueryTimeout</c>.
    /// </remarks>
    Task<EventTagRead> GetEventTagsAsync(
        StudioScope scope,
        IReadOnlyList<long> sequences,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The streams whose natural key equals what was typed into the Streams box: a primary-key lookup on
    /// <c>mt_natural_key_&lt;aggregate&gt;</c> for every aggregate with a <c>NaturalKeyDefinition</c>.
    /// </summary>
    /// <param name="scope">The store, database and tenant; a tenant narrows the lookup to its own streams.</param>
    /// <param name="value">What was typed.</param>
    /// <param name="includeArchived">Whether archived streams are found too.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// A lookup table that is declared but not in the database is skipped rather than created (hard rule 14).
    /// Archival is read off <c>mt_streams</c>, as Marten's own natural-key fetch does (#5344).
    /// </remarks>
    Task<NaturalKeyLookup> FindStreamsByNaturalKeyAsync(
        StudioScope scope,
        string value,
        bool includeArchived,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether one stream can be compacted from here, and every fact the confirmation dialog states.
    /// </summary>
    /// <remarks>
    /// A read: it changes nothing and needs no capability. It takes the stream the page has already read with
    /// <see cref="GetStreamAsync" /> rather than reading it a second time; what it offers is decided again,
    /// from a fresh read, by <see cref="CompactStreamAsync" />, which is the refusal (hard rule 5).
    /// </remarks>
    Task<CompactionPlan> DescribeCompactionAsync(
        StudioScope scope,
        StreamState stream,
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
    /// <exception cref="InvalidOperationException">
    /// The host hides <c>DeadLetterEvent</c> (<see cref="MartenStudioOptions.IsDocumentTypeVisible" />); audited as refused.
    /// </exception>
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
    /// <para>
    /// <b>It is not limited to the tenant in <paramref name="scope" />.</b> Marten rewrites every shard's
    /// progression row of the projection and deletes its dead letters at or above the floor for every
    /// tenant, and the daemon re-runs every tenant's events from there. So a scope with a tenant is also
    /// authorized for its database as a whole - <c>(store, database, null)</c> - unless the database is
    /// exclusively that tenant's, and a refusal carries that tenant-less scope.
    /// </para>
    /// <para>
    /// <paramref name="projectionName" /> is checked against the store's own async projections once the
    /// scope is authorized and before anything is touched, and an unknown one is refused with a
    /// <see cref="KeyNotFoundException" /> that repeats only the name it was given. Marten's own check
    /// names every projection the store has in its message, and that message would reach the audit entry
    /// and the screen.
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
    /// <exception cref="StudioNotAuthorizedException">
    /// A policy refused this scope, or the database as a whole that the rewind reaches.
    /// </exception>
    /// <exception cref="MartenStudio.Services.Projections.StudioDaemonNotHostedException">
    /// No async daemon is hosted in this process.
    /// </exception>
    /// <exception cref="KeyNotFoundException">
    /// This store registers no async projection by that name.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The host hides <c>DeadLetterEvent</c>, which the rewind deletes from; audited as refused, before anything else is asked.
    /// </exception>
    Task RewindSubscriptionAsync(
        StudioScope scope,
        string projectionName,
        long eventSequence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compacts one stream: <c>CompactStreams</c>, the write policy for the scope as selected - tenant
    /// included - and the typed confirmation, then Marten's typed <c>CompactStreamAsync&lt;T&gt;</c> and
    /// <c>SaveChangesAsync</c> on a session pinned to the scope's database and tenant, audited either way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Marten folds the stream's events into one <c>Compacted&lt;T&gt;</c> written in place of the last,
    /// deletes the rest, and records <c>mt_streams.compacted_version</c>, in one transaction and with no
    /// archiver (<see cref="StreamCompactionRules" /> has the whole account). <c>T</c> is the registered
    /// single-stream aggregate the stream's <c>mt_streams.type</c> names (<see cref="CompactionAggregates" />).
    /// </para>
    /// <para>
    /// The store-level <c>IEventStore.CompactStreamAsync</c> overloads are not used: they open
    /// <c>LightweightSession()</c> or <c>LightweightSession(tenantId)</c>, neither of which pins the
    /// database (hard rule 15). On a conjoined store the scope must name the stream's tenant, and only that
    /// tenant's stream of that id is touched - at every Marten from the 9.39.0 floor, because the
    /// session-level path is tenant-filtered there already.
    /// </para>
    /// </remarks>
    /// <param name="scope">The store, database and tenant.</param>
    /// <param name="streamId">The stream, as the page names it.</param>
    /// <param name="confirmation">What the visitor typed into the dialog; it has to be the stream's id.</param>
    /// <param name="expectedVersion">
    /// The version the dialog described (<see cref="CompactionPlan.Version" />). Marten is told to fold through
    /// it and no further, so an event appended after the dialog opened stays after the snapshot.
    /// </param>
    /// <param name="cancellationToken">Cancels the steps before the confirmation; the compaction runs to the end.</param>
    /// <returns>
    /// What happened, as audited: the version the stream is now compacted through, read back from
    /// <c>mt_streams.compacted_version</c>. It can be below <paramref name="expectedVersion" /> on a store
    /// that records skipped events, because Marten folds through the last event its fetch returned, and the
    /// sentence then says so.
    /// </returns>
    /// <exception cref="StudioCapabilityDeniedException">The capability is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The write policy refused this scope.</exception>
    /// <exception cref="StreamCompactionUnconfirmedException">
    /// The save returned but its result could not be read back - "saved, not confirmed". Audited as a failure
    /// with that message.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The stream is not one that can be compacted (its id is empty, it is archived, its aggregate is hidden or
    /// not registered, there is nothing to fold), the scope names no tenant on a conjoined store, the
    /// confirmation did not match, the version is past the stream's end or already compacted, Postgres refused
    /// the delete (a tagged stream), or the save did not move <c>compacted_version</c> - nothing was compacted.
    /// </exception>
    Task<string> CompactStreamAsync(
        StudioScope scope,
        string streamId,
        string confirmation,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}
