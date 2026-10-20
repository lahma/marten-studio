using System.Globalization;

using JasperFx.Events;

using Marten;
using Marten.Events;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Services.Events;

/// <summary>Why a stream is not offered for compaction.</summary>
internal enum CompactionUnavailableReason
{
    /// <summary>Nothing stands in the way.</summary>
    None,

    /// <summary>There is no such stream in this scope.</summary>
    StreamMissing,

    /// <summary>
    /// The stream is archived. Marten's stream fetch always applies <c>IsNotArchivedFilter</c>, so a
    /// compaction would read no events and return having done nothing.
    /// </summary>
    Archived,

    /// <summary>The stream was started without an aggregate type, so there is nothing to fold it into.</summary>
    Untyped,

    /// <summary>The stream's aggregate type is not one this store registers a single-stream projection for.</summary>
    AggregateNotRegistered,

    /// <summary>Two registered aggregate types answer to the stream's type name.</summary>
    AggregateAmbiguous,

    /// <summary>One event, or one snapshot and nothing after it: there is nothing to fold.</summary>
    NothingToFold,

    /// <summary>
    /// The stream's id is <see cref="Guid.Empty" />, or an empty string key - the one id Marten's stream fetch
    /// read as "no stream filter at all" below 9.51, and that Marten's compaction refuses from 9.51.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Below Marten 9.51 (read at 9.39.0, 9.50.2 and 9.50.3) <c>EventStatement.filters()</c> emits
    /// <c>d.stream_id = ?</c> only when <c>StreamId != Guid.Empty</c>, and the key predicate only when
    /// <c>StreamKey.IsNotEmpty()</c>. So <c>FetchStreamAsync(Guid.Empty)</c> returns every event of the tenant - of
    /// the store, when it is single-tenanted - and a compaction would fold them all, rewrite the newest event of the
    /// log as this stream's snapshot and delete every other event. Such a stream exists: DCB's
    /// <c>EventBoundary</c>, the bulk appender, legacy data and hand-written SQL can all leave one.
    /// </para>
    /// <para>
    /// From 9.51 (#5695) the fetch reads only that stream and <c>CompactStreamAsync</c> throws
    /// <c>ArgumentOutOfRangeException</c> for an empty key or <see cref="Guid.Empty" />. The studio's refusal stays at
    /// every version - it is the floor's guard, and at 9.51 it says first what Marten would say second.
    /// </para>
    /// </remarks>
    EmptyStreamId,

    /// <summary>
    /// The stream's aggregate type is one <c>MartenStudioOptions.IsDocumentTypeVisible</c> hides: compacting
    /// it would write that aggregate's state into an event, which the studio does not do for a type it is
    /// not allowed to show.
    /// </summary>
    AggregateHidden,
}

/// <summary>What a compaction's save turned out to have done, read back from <c>mt_streams</c>.</summary>
internal enum CompactionVerdict
{
    /// <summary>This save moved the watermark: the stream is compacted, through the version Marten folded to.</summary>
    Compacted,

    /// <summary>The watermark did not move: Marten found nothing to fold, and nothing changed.</summary>
    NothingCompacted,

    /// <summary>
    /// The save returned, but what it did cannot be told - the read-back failed, the stream is gone, or the
    /// watermark is past anything this compaction could have written.
    /// </summary>
    Unconfirmed,
}

/// <summary>
/// A compaction whose save returned but whose result could not be confirmed. Audited as a failure, because
/// the audit has two outcomes and this is not a success; its message says "saved, not confirmed", and the
/// page shows it as a warning rather than as an error, and reloads.
/// </summary>
internal sealed class StreamCompactionUnconfirmedException : InvalidOperationException
{
    public StreamCompactionUnconfirmedException(string message) : base(message)
    {
    }

    public StreamCompactionUnconfirmedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// The aggregate a stream compacts into: a registered single-stream aggregate whose alias is the stream's
/// <c>mt_streams.type</c>.
/// </summary>
/// <param name="Type">The CLR type <c>CompactStreamAsync&lt;T&gt;</c> is closed over.</param>
/// <param name="Alias">Its alias, as Marten writes it into <c>mt_streams.type</c>.</param>
internal sealed record CompactionAggregate(Type Type, string Alias)
{
    /// <summary>Its short name.</summary>
    public string Name => Type.Name;

    /// <summary>Its full name.</summary>
    public string FullName => Type.FullName ?? Type.Name;
}

/// <summary>
/// Resolves a stream's aggregate type the way Marten does in a freshly started process - and without
/// touching the cache Marten itself resolves through.
/// </summary>
/// <remarks>
/// <para>
/// <b>How Marten does it.</b> The untyped <c>IEventStore.CompactStreamAsync(id)</c> reads the stream state
/// and takes <c>StreamState.AggregateType</c>, which <c>EventDocumentStorage</c> fills with
/// <c>EventGraph.AggregateTypeFor(mt_streams.type)</c>: a lookup in a cache whose miss handler walks
/// <c>Projections.AllAggregateTypes()</c> comparing aliases ignoring case, and returns <see langword="null" />
/// when none matches (9.39.0).
/// </para>
/// <para>
/// <b>Why the studio does not call it.</b> That cache stores the <see langword="null" />, and its
/// <c>Fill</c> - which is how <c>AggregateAliasFor</c> teaches it a type when the host later starts a stream
/// with <c>StartStream&lt;T&gt;</c> - never overwrites an entry (JasperFx 2.74.0 <c>Cache.Fill</c>). So a
/// studio that asked about an alias Marten had not learned yet would have taught Marten that the alias
/// means nothing, for the life of the process, and Marten's own stream-state reads would go on answering
/// "no aggregate type" for it. That is the host's behaviour changed by registering the studio.
/// </para>
/// <para>
/// <b>What it does instead.</b> It asks the other direction: for every registered single-stream aggregate
/// (<see cref="AggregateStreamInvoker.Candidates" />, the time-travel picker's own list), Marten's alias via
/// <c>AggregateAliasFor(type)</c> - whose only side effect is to fill both caches with a pair Marten's
/// <c>Initialize</c> already pre-warmed for every registered aggregate - and matches the stream's type
/// against those, ignoring case as Marten does. That is the set Marten resolves in a fresh process; a type
/// Marten only knows because something in this process aggregated it live is left out, and the plan says
/// the stream's type is not one the store registers. A registered single-stream projection is also exactly
/// what makes compaction safe to rebuild through: it fast-forwards from <c>Compacted&lt;T&gt;</c>
/// (<c>JasperFxSingleStreamProjectionBase.BuildAsync</c>).
/// </para>
/// </remarks>
internal static class CompactionAggregates
{
    /// <summary>The registered single-stream aggregates and their aliases.</summary>
    /// <param name="options">The store's own options.</param>
    public static IReadOnlyList<CompactionAggregate> Registered(IReadOnlyStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Events is not EventGraph graph)
        {
            return [];
        }

        List<CompactionAggregate> aggregates = [];

        foreach (AggregateTypeCandidate candidate in AggregateStreamInvoker.Candidates(options))
        {
            if (candidate.Source != AggregateCandidateSource.SingleStreamProjection)
            {
                continue;
            }

            aggregates.Add(new CompactionAggregate(candidate.Type, graph.AggregateAliasFor(candidate.Type)));
        }

        return aggregates;
    }

    /// <summary>The registered aggregate a stream's <c>mt_streams.type</c> names, or why there is none.</summary>
    /// <param name="registered">What <see cref="Registered" /> returned.</param>
    /// <param name="streamType">The stream's <c>type</c> column.</param>
    /// <param name="isVisible">
    /// <c>MartenStudioOptions.IsDocumentTypeVisible</c>: an aggregate it hides is refused as
    /// <see cref="CompactionUnavailableReason.AggregateHidden" /> - matched first and refused after, so the
    /// refusal is that reason rather than "not registered", and says nothing about which type it is.
    /// </param>
    public static (CompactionAggregate? Aggregate, CompactionUnavailableReason Reason) Resolve(
        IReadOnlyList<CompactionAggregate> registered,
        string? streamType,
        Func<Type, bool>? isVisible = null)
    {
        ArgumentNullException.ThrowIfNull(registered);

        if (string.IsNullOrWhiteSpace(streamType))
        {
            return (null, CompactionUnavailableReason.Untyped);
        }

        CompactionAggregate? found = null;

        foreach (CompactionAggregate aggregate in registered)
        {
            if (!string.Equals(aggregate.Alias, streamType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found is not null && found.Type != aggregate.Type)
            {
                return (null, CompactionUnavailableReason.AggregateAmbiguous);
            }

            found = aggregate;
        }

        if (found is null)
        {
            return (null, CompactionUnavailableReason.AggregateNotRegistered);
        }

        return isVisible is not null && !isVisible(found.Type)
            ? (null, CompactionUnavailableReason.AggregateHidden)
            : (found, CompactionUnavailableReason.None);
    }
}

/// <summary>Whether a stream can be compacted from here, and every fact the dialog says about it.</summary>
internal sealed record CompactionPlan
{
    /// <summary>The stream's id, as the database holds it - which is what the dialog asks to be typed.</summary>
    public required string StreamId { get; init; }

    /// <summary>Why it is not offered, or <see cref="CompactionUnavailableReason.None" />.</summary>
    public CompactionUnavailableReason Reason { get; init; }

    /// <summary>The sentence behind <see cref="Reason" />.</summary>
    public string? Explanation { get; init; }

    /// <summary>What went wrong, when the stream could not be read.</summary>
    public EventDataError? Error { get; init; }

    /// <summary>The aggregate the stream compacts into.</summary>
    public string? AggregateName { get; init; }

    /// <summary>Its full name.</summary>
    public string? AggregateFullName { get; init; }

    /// <summary>The stream's version, from <c>mt_streams.version</c>.</summary>
    public long Version { get; init; }

    /// <summary>The version an earlier compaction folded through, from <c>mt_streams.compacted_version</c>; 0 if none.</summary>
    public long CompactedVersion { get; init; }

    /// <summary>How many events the stream holds by its version, which is how many Marten folds.</summary>
    public long EventsToFold { get; init; }

    /// <summary>The stream's tenant, on a tenanted store.</summary>
    public string? StreamTenantId { get; init; }

    /// <summary>
    /// Whether the scope names no tenant on a conjoined store. A compaction runs in exactly one tenant's
    /// session, so the visitor picks the stream's tenant first.
    /// </summary>
    public bool NeedsTenant { get; init; }

    /// <summary>
    /// Whether the store keeps DCB tags in tag tables whose foreign keys refuse a tagged stream's delete - which they do
    /// below Marten 9.51, whose compaction deletes the events alone.
    /// </summary>
    public bool TagTablesMayRefuse { get; init; }

    /// <summary>
    /// Whether the store keeps DCB tags in tag tables and this Marten deletes the folded events' tag rows with them
    /// (9.51+, <c>RuntimeFeature.TaggedStreamCompaction</c>), so a tagged stream compacts instead of being refused.
    /// </summary>
    public bool TagRowsFoldWithEvents { get; init; }

    /// <summary>Whether the store records skipped events, which Marten's fetch leaves out and compaction leaves in.</summary>
    public bool SkippedEventsStay { get; init; }

    /// <summary>Whether the stream may be compacted from here.</summary>
    public bool IsOffered => Reason == CompactionUnavailableReason.None && Error is null;

    /// <summary>Whether it has been compacted before.</summary>
    public bool WasCompacted => CompactedVersion > 0;

    /// <summary>A stream that is not offered, and why.</summary>
    public static CompactionPlan NotOffered(string streamId, CompactionUnavailableReason reason) => new()
    {
        StreamId = streamId,
        Reason = reason,
        Explanation = StreamCompactionRules.Explain(reason),
    };

    /// <summary>A stream that could not be read.</summary>
    public static CompactionPlan Failed(string streamId, EventDataError error) => new()
    {
        StreamId = streamId,
        Reason = CompactionUnavailableReason.StreamMissing,
        Error = error,
        Explanation = error.Display,
    };
}

/// <summary>
/// The compaction rules, as pure functions over facts the service has already read - so what is offered,
/// what is refused and what the dialog says can be tested without a Postgres.
/// </summary>
/// <remarks>
/// <para>
/// <b>What Marten does</b>, read from <c>EventStore.StreamCompacting.cs</c> at 9.39.0 and 9.50.2, which are
/// byte for byte the same file, and at 9.51.0, which adds two things and changes nothing else: an empty key or
/// <see cref="Guid.Empty" /> is refused with <c>ArgumentOutOfRangeException</c> before anything is read (#5695), and
/// the delete of the folded events deletes their DCB tag rows first, in the same statement (#5694, see
/// <see cref="CompactionPlan.TagRowsFoldWithEvents" />). <c>FetchStreamAsync</c> reads the stream's events - never archived ones, and
/// never skipped ones on a store that records them; returns having done nothing for no events, or for a
/// stream that is already exactly one <c>Compacted&lt;T&gt;</c>; folds the events with
/// <c>Projections.AggregatorFor&lt;T&gt;()</c> (the registered projection first); calls an
/// <c>IEventsArchiver</c> only if the request carries one; then queues three statements - the last event's
/// row rewritten as <c>Compacted&lt;T&gt;</c> (same <c>seq_id</c>, same version, new id, <c>now()</c>, headers
/// and causation, correlation and user cleared), every earlier event's row deleted, and
/// <c>mt_streams.compacted_version = greatest(compacted_version, version)</c>. Each statement carries
/// <c>AppendConjoinedTenantFilter</c>, and the reads carry the session's tenant, so on a conjoined store a
/// tenant-pinned session touches that tenant's stream and no other (#5234). Nothing writes the aggregate
/// document, nothing touches the async daemon, and <c>SaveChangesAsync</c> runs it as one transaction.
/// </para>
/// <para>
/// <b>Why the studio does not call <c>IEventStore.CompactStreamAsync</c>.</b> Every store-level overload
/// opens <c>LightweightSession()</c> or, from 9.41.0, <c>LightweightSession(tenantId)</c>: neither pins the
/// database the scope selector named (hard rule 15), and the tenant-less one is the default tenant's
/// session. The studio opens its own with <c>SessionOptions.ForDatabase(tenant, database)</c> and calls the
/// typed session-level <c>CompactStreamAsync&lt;T&gt;</c> the store-level overloads themselves call - so a
/// tenanted stream compacts correctly at the 9.39.0 floor, with no tenant overload needed.
/// </para>
/// </remarks>
internal static class StreamCompactionRules
{
    /// <summary>What a mismatched typed confirmation is refused with.</summary>
    public const string ConfirmationMismatch = "The typed confirmation did not match the stream id.";

    /// <summary>The refusal for a conjoined store's stream compacted from an "all tenants" scope.</summary>
    public static string TenantRequired(string? streamTenantId) => WriteNeedsTenant("a compaction", streamTenantId);

    /// <summary>
    /// The one sentence for a read that has no order across tenants on a store whose events are numbered per tenant
    /// (<c>UseTenantPartitionedEvents</c>), when no tenant is selected: the feed's (SEC1-fix3).
    /// </summary>
    /// <param name="read">What cannot be read, as the sentence names it: "the feed".</param>
    public static string ReadNeedsTenant(string read) =>
        "This store numbers events per tenant (UseTenantPartitionedEvents): choose a tenant to read " + read + ".";

    /// <summary>
    /// Why Skip event is refused on a store whose events are numbered per tenant (<c>UseTenantPartitionedEvents</c>)
    /// by a Marten without the tenant overload of <c>MarkEventsAsSkipped</c> - below 9.51 Marten skips by sequence alone
    /// (SEC1-fix4). The service's refusal and the disabled button's title, word for word.
    /// </summary>
    public static readonly string SkipNeedsOneSequence =
        "This host's Marten marks events skipped by sequence alone, and this store numbers events per tenant "
        + "(UseTenantPartitionedEvents): skipping this event would skip every tenant's event with the same number, "
        + "so Marten Studio does not skip events on this store. Skipping one tenant's event needs "
        + RuntimeFeature.TenantEventSkipping.Requirement + ".";

    /// <summary>
    /// Why Rewind subscription is refused on such a store by a Marten without the per-tenant rewind - below 9.51 the
    /// daemon rewinds to one sequence for every tenant (SEC1-fix4). The service's refusal and the disabled button's
    /// title, word for word.
    /// </summary>
    public static readonly string RewindNeedsOneSequence =
        "This host's Marten rewinds a subscription to one sequence for every tenant, and this store numbers events per "
        + "tenant (UseTenantPartitionedEvents): the floor would name a different event in each tenant, so Marten Studio "
        + "does not rewind subscriptions on this store. Rewinding one tenant's agents needs "
        + RuntimeFeature.TenantSubscriptionRewind.Requirement + ".";

    /// <summary>
    /// Why Skip event is refused on such a store when the scope names no tenant: from Marten 9.51 the skip is one
    /// tenant's, and a sequence alone names an event in every tenant. The page pins the dead letter's own tenant, so
    /// only a caller that did not is told this.
    /// </summary>
    public const string SkipNeedsTenant =
        "This store numbers events per tenant (UseTenantPartitionedEvents), so a sequence names an event in every "
        + "tenant: choose the dead letter's tenant to skip its event.";

    /// <summary>The same for Rewind subscription, which from Marten 9.51 rewinds one tenant's agents.</summary>
    public const string RewindNeedsTenant =
        "This store numbers events per tenant (UseTenantPartitionedEvents), so a rewind's floor names a position in one "
        + "tenant's events: choose the dead letter's tenant to rewind its subscription.";

    /// <summary>
    /// Why a per-tenant rewind is refused for a tenant whose agents' shard identity does not read back as that tenant's
    /// - an id with a <c>:</c> in it. JasperFx hard-stops the tenant's agents and only then has Marten parse the identity,
    /// which fails; so it is refused before the daemon is asked. The service's refusal and the page's title.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    public static string RewindTenantNotAddressable(string tenant) =>
        "Tenant " + tenant + "'s agents are named with its id, and with a ':' in it that name does not read back as this "
        + "tenant's: the daemon would stop the tenant's agents and Marten would then refuse the rewind, leaving them "
        + "stopped. Marten Studio does not rewind subscriptions for this tenant.";

    /// <summary>
    /// What a rewind Marten or JasperFx refused as an unknown subscription is reported as - in place of their own message,
    /// which lists every subscription the store has (D5).
    /// </summary>
    /// <param name="projection">The projection the rewind was asked for.</param>
    /// <param name="tenantId">The tenant, for a per-tenant rewind; <see langword="null" /> otherwise.</param>
    public static string SubscriptionNotRecognised(string projection, string? tenantId) =>
        "Marten did not recognise the subscription '" + projection + "'"
        + (string.IsNullOrEmpty(tenantId) ? string.Empty : " for tenant " + tenantId)
        + ", so nothing was rewound. The daemon stops the agents before it asks Marten, so check the Projections screen: "
        + "they may need starting again.";

    /// <summary>
    /// Why a per-tenant rewind is refused when the daemon in this process does not run that tenant's agent of the
    /// projection: JasperFx's per-tenant rewind starts the tenant's agents here whether or not this process ran them, so
    /// under HotCold or a distributed host it would run a second agent beside the one on the node that has it.
    /// </summary>
    /// <param name="projection">The projection.</param>
    /// <param name="tenant">The tenant.</param>
    public static string RewindNeedsTenantAgentHere(string projection, string tenant) =>
        "The async daemon in this process does not run '" + projection + "' for tenant " + tenant + ". A per-tenant "
        + "rewind starts that tenant's agents in the daemon it is asked of, so asking this one would run a second agent "
        + "beside the one wherever it runs now. Rewind it from the process that runs it.";

    /// <summary>
    /// The one sentence Stream detail draws beside a write that a conjoined event store runs as exactly one tenant, when
    /// no tenant is selected: Compact's and Archive's (SEC1-fix F5).
    /// </summary>
    /// <param name="write">The write, as the sentence names it: "a compaction", "an archive".</param>
    /// <param name="streamTenantId">The stream's own tenant, when the read knows it.</param>
    public static string WriteNeedsTenant(string write, string? streamTenantId) =>
        "This event store is tenanted per event, and " + write + " runs as exactly one tenant: pick "
        + (string.IsNullOrEmpty(streamTenantId) ? "the stream's tenant" : "tenant " + streamTenantId)
        + " in the header first.";

    /// <summary>
    /// The refusal a tagged stream gets back from Postgres, said in the studio's words.
    /// </summary>
    /// <remarks>
    /// Marten's <c>EventTagTable</c> declares <c>seq_id</c> a foreign key to <c>mt_events(seq_id)</c> with
    /// Weasel's default <c>NO ACTION</c> on delete (still so at 9.51.0), and below Marten 9.51 compaction's delete
    /// does not remove the tag rows first - so the delete of a tagged event fails with <c>23503</c> and the whole
    /// <c>SaveChangesAsync</c> rolls back. From 9.51 the delete removes them first and this is not reached; it stays
    /// for the Marten that still refuses.
    /// </remarks>
    /// <param name="table">The tag table Postgres named, when it named one.</param>
    public static string TaggedStreamRefused(string? table) =>
        "Postgres refused to delete the folded events: they carry DCB tags, and "
        + (string.IsNullOrEmpty(table) ? "a tag table" : table)
        + " references each tagged event by a foreign key with no cascade. Nothing was changed - Marten's "
        + "compaction is one transaction.";

    /// <summary>How many events a stream holds, by its version and the version an earlier compaction folded through.</summary>
    /// <remarks>
    /// A stream never compacted holds <c>version</c> events. One compacted through <c>c</c> holds the
    /// <c>Compacted&lt;T&gt;</c> event at version <c>c</c> and every event after it: <c>version - c + 1</c>.
    /// Counted by version, not by row: skipped events are included, and a stream somebody deleted rows from
    /// by hand is miscounted. It decides whether there is anything to fold, and the dialog says it is "by the
    /// stream's version".
    /// </remarks>
    public static long EventsInStream(long version, long compactedVersion) =>
        compactedVersion <= 0
            ? Math.Max(version, 0)
            : Math.Max(version - compactedVersion, 0) + 1;

    /// <summary>Whether what the visitor typed is the stream id, as the database holds it.</summary>
    public static bool ConfirmationMatches(string? typed, string streamId) =>
        typed is not null && string.Equals(typed.Trim(), streamId, StringComparison.Ordinal);

    /// <summary>
    /// Whether a stream id, as text, is the one Marten's stream fetch reads as "no stream filter":
    /// <see cref="Guid.Empty" /> on a GUID-identified store, an empty key on a string-identified one.
    /// </summary>
    /// <remarks>
    /// On a string-identified store a key that merely <em>looks</em> like the empty GUID is an ordinary key:
    /// <c>StreamKey.IsNotEmpty()</c> is true for it and the predicate is emitted.
    /// </remarks>
    public static bool IsWholeLogId(StreamIdentity identity, string? streamId) =>
        identity == StreamIdentity.AsString
            ? string.IsNullOrEmpty(streamId)
            : Guid.TryParse(streamId, CultureInfo.InvariantCulture, out Guid guid) && guid == Guid.Empty;

    /// <summary>The same question of an id already parsed to a <see cref="Guid" /> or a string.</summary>
    public static bool IsWholeLogId(object? parsedStreamId) => parsedStreamId switch
    {
        Guid guid => guid == Guid.Empty,
        string key => key.Length == 0,
        _ => true,
    };

    /// <summary>The plan for one stream, from facts already read.</summary>
    /// <param name="stream">The stream as the scope sees it.</param>
    /// <param name="identity">How the store identifies streams, which decides what an empty id is.</param>
    /// <param name="aggregate">The aggregate its type resolved to, when it did.</param>
    /// <param name="resolution">Why it did not, when it did not.</param>
    /// <param name="isConjoined">Whether the event store is conjoined-tenanted.</param>
    /// <param name="scopeTenantId">The scope's tenant.</param>
    /// <param name="tags">How the store keeps DCB tags.</param>
    /// <param name="skipping">Whether the store records skipped events.</param>
    /// <param name="martenDeletesTagRows">
    /// Whether this Marten's compaction deletes the folded events' tag rows with them
    /// (<c>RuntimeFeature.TaggedStreamCompaction</c>, 9.51+); below it a tag table refuses a tagged stream's delete.
    /// </param>
    public static CompactionPlan Plan(
        StreamState stream,
        StreamIdentity identity,
        CompactionAggregate? aggregate,
        CompactionUnavailableReason resolution,
        bool isConjoined,
        string? scopeTenantId,
        EventTagStorage tags,
        bool skipping,
        bool martenDeletesTagRows = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.Error is { } error)
        {
            return CompactionPlan.Failed(stream.Id, error);
        }

        if (!stream.Exists)
        {
            return CompactionPlan.NotOffered(stream.Id, CompactionUnavailableReason.StreamMissing);
        }

        // Before anything else about the stream: an id Marten reads as "no filter" turns compaction into a
        // rewrite of the whole log, whatever else is true of the stream.
        if (IsWholeLogId(identity, stream.Id))
        {
            return CompactionPlan.NotOffered(stream.Id, CompactionUnavailableReason.EmptyStreamId);
        }

        if (stream.IsArchived)
        {
            return CompactionPlan.NotOffered(stream.Id, CompactionUnavailableReason.Archived);
        }

        if (aggregate is null)
        {
            CompactionUnavailableReason reason = resolution == CompactionUnavailableReason.None
                ? CompactionUnavailableReason.AggregateNotRegistered
                : resolution;

            return CompactionPlan.NotOffered(stream.Id, reason);
        }

        long events = EventsInStream(stream.Version, stream.CompactedVersion);

        if (events < 2)
        {
            return CompactionPlan.NotOffered(stream.Id, CompactionUnavailableReason.NothingToFold);
        }

        return new CompactionPlan
        {
            StreamId = stream.Id,
            AggregateName = aggregate.Name,
            AggregateFullName = aggregate.FullName,
            Version = stream.Version,
            CompactedVersion = Math.Max(stream.CompactedVersion, 0),
            EventsToFold = events,

            // Only a conjoined store's tenant narrows anything Marten does. A store with single-tenanted events
            // still records the appending session's tenant in mt_streams.tenant_id (EventGraph.Actions and the
            // quick-append function write session.TenantId on every store; read at V9.39.0 and V9.50.2), but
            // Marten's compaction fetches the stream by id alone there, and deletes and replaces the sequences
            // that fetch returned with no tenant predicate (ConjoinedEventFilter adds one only on a conjoined
            // store) - so naming the tenant would be the dialog claiming a narrowing Marten does not make. What keeps a compaction on such a
            // store to the scope's own stream is the service's read of it, narrowed to the scope's tenant
            // (EventDataService.CompactStreamAsync, step 1), and a stream of another tenant is never planned.
            StreamTenantId = isConjoined ? stream.TenantId : null,
            NeedsTenant = isConjoined && scopeTenantId is null,
            TagTablesMayRefuse = tags == EventTagStorage.TagTables && !martenDeletesTagRows,
            TagRowsFoldWithEvents = tags == EventTagStorage.TagTables && martenDeletesTagRows,
            SkippedEventsStay = skipping,
        };
    }

    /// <summary>
    /// Why a compaction from a plan at <paramref name="plannedVersion" /> cannot run against the stream as it
    /// stands now, or <see langword="null" /> when it can.
    /// </summary>
    /// <remarks>
    /// The service hands Marten <c>request.Version = plannedVersion</c>, so events appended after the dialog
    /// opened are left alone. That needs the version to be one this stream reached and one past what an
    /// earlier compaction already folded.
    /// </remarks>
    public static string? CutoffProblem(long plannedVersion, StreamState current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (plannedVersion < 1 || plannedVersion > current.Version)
        {
            return "The dialog described version " + plannedVersion.ToString(CultureInfo.InvariantCulture)
                + ", which this stream is not at (it is at version " + current.Version.ToString(CultureInfo.InvariantCulture)
                + "). Reload the page.";
        }

        if (current.CompactedVersion >= plannedVersion)
        {
            return "The stream has already been compacted through version " + current.CompactedVersion.ToString(CultureInfo.InvariantCulture)
                + ", so there is nothing left to fold up to version " + plannedVersion.ToString(CultureInfo.InvariantCulture) + ".";
        }

        return EventsInStream(plannedVersion, current.CompactedVersion) < 2 ? Explain(CompactionUnavailableReason.NothingToFold) : null;
    }

    /// <summary>
    /// What the save actually did, read back from <c>mt_streams</c> - which is what the audit records.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "It did not throw" is not "it compacted", and "the watermark reached the dialog's version" is not
    /// either. The test is whether this save <em>moved</em> the watermark: <paramref name="compactedBefore" />
    /// is the service's own read, just before the save.
    /// </para>
    /// <para>
    /// Marten returns having done nothing when its fetch finds no events - an archive that landed between the
    /// studio's checks and Marten's read is exactly that - or finds only a <c>Compacted&lt;T&gt;</c>, and
    /// <c>SaveChangesAsync</c> then commits an empty unit of work. And when it does compact, it does not
    /// compact through the version it was given: <c>EventStore.StreamCompacting.cs</c> (line 112 at 9.39.0 and
    /// 9.50.2) sets <c>request.Version</c> to the version of the last event its fetch <em>returned</em>, and
    /// the rewrite, the delete and the watermark all use that. On a store with
    /// <c>EnableEventSkippingInProjectionsOrSubscriptions</c> the fetch adds <c>is_skipped = FALSE</c>, so a
    /// dialog's version whose event was skipped - the dead-letter Skip marks one, so does the daemon skipping
    /// an apply error - compacts through an earlier version. That is a compaction, and is reported as one,
    /// through the version Marten actually folded to.
    /// </para>
    /// <para>
    /// A watermark past the dialog's version is another compaction's - Marten's own is never past the version
    /// it was handed - so this one cannot be confirmed. The same before/after comparison is what keeps
    /// another operator's earlier compaction from being recorded as this one; one that runs between the
    /// service's read and this save is the window that remains.
    /// </para>
    /// </remarks>
    /// <param name="plannedVersion">The version the dialog described, which Marten was told to stop at.</param>
    /// <param name="compactedBefore">The stream's <c>compacted_version</c>, read just before the save.</param>
    /// <param name="after">The stream, read again after the save.</param>
    /// <param name="aggregateName">The aggregate, for the sentence.</param>
    /// <param name="tenantId">The tenant it was narrowed to, on a conjoined store.</param>
    /// <param name="skipping">Whether the store records skipped events, which Marten's fetch leaves out.</param>
    /// <returns>What happened, and the outcome in a sentence.</returns>
    public static (CompactionVerdict Verdict, string Outcome) Verify(
        long plannedVersion,
        long compactedBefore,
        StreamState after,
        string aggregateName,
        string? tenantId,
        bool skipping)
    {
        ArgumentNullException.ThrowIfNull(after);

        long before = Math.Max(compactedBefore, 0);
        string planned = Text(plannedVersion);
        string tenant = string.IsNullOrEmpty(tenantId) ? string.Empty : " in tenant " + tenantId;

        if (after.Error is { } error)
        {
            return (CompactionVerdict.Unconfirmed, Unconfirmed("the stream could not be read back - " + error.Display));
        }

        if (!after.Exists)
        {
            return (CompactionVerdict.Unconfirmed, Unconfirmed("the stream is no longer in this scope"));
        }

        long through = after.CompactedVersion;

        if (through <= before)
        {
            if (after.IsArchived)
            {
                return (CompactionVerdict.NothingCompacted,
                    "Nothing was compacted: the stream was archived before Marten read it, and Marten's stream fetch never returns archived events.");
            }

            return (CompactionVerdict.NothingCompacted,
                "Nothing was compacted: mt_streams.compacted_version is still " + Text(through) + ". "
                + (skipping
                    ? "Marten's stream fetch leaves out skipped events on this store, and up to version " + planned
                        + " it found nothing to fold - every event after the last snapshot is a skipped one."
                    : "Marten's stream fetch found nothing to fold up to version " + planned + "."));
        }

        if (through > plannedVersion)
        {
            return (CompactionVerdict.Unconfirmed, Unconfirmed(
                "mt_streams.compacted_version is now " + Text(through) + ", past version " + planned
                + " that this compaction was bounded to, so another compaction of this stream ran at the same time"));
        }

        string done = "compacted through version " + Text(through) + " into one Compacted<" + aggregateName + "> event" + tenant;

        if (through < plannedVersion)
        {
            string left = through + 1 == plannedVersion
                ? "version " + planned
                : "versions " + Text(through + 1) + " to " + planned;

            done += ", not through version " + planned + ": "
                + (skipping
                    ? left + (through + 1 == plannedVersion ? " is a skipped event" : " are skipped events")
                        + ", which Marten's stream fetch leaves out, so "
                    : "Marten's stream fetch did not return " + left + ", so ")
                + (through + 1 == plannedVersion ? "it stays" : "they stay") + " in the stream after the snapshot";
        }

        return (CompactionVerdict.Compacted, done + (after.IsArchived ? "; the stream has been archived since" : string.Empty));
    }

    /// <summary>
    /// The outcome of a save that may have committed, but whose result could not be read back: neither a
    /// success nor a refusal, and never said as either.
    /// </summary>
    /// <param name="why">What stopped the confirmation, as a clause.</param>
    public static string Unconfirmed(string why) =>
        "Compaction was saved, not confirmed: " + (why ?? string.Empty).TrimEnd().TrimEnd('.')
        + ". Reload the page to see what the stream holds now.";

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Why a stream is not offered, in a sentence.</summary>
    public static string Explain(CompactionUnavailableReason reason) => reason switch
    {
        CompactionUnavailableReason.StreamMissing => EventDataService.StreamNotInScopeMessage,
        CompactionUnavailableReason.Archived =>
            "This stream is archived, and Marten's stream fetch never returns archived events, so compacting it would do nothing.",
        CompactionUnavailableReason.Untyped =>
            "This stream was started without an aggregate type, so there is nothing to fold it into.",
        CompactionUnavailableReason.AggregateNotRegistered =>
            "This stream's aggregate type is not one this store registers a single-stream projection for, so Marten has nothing it can fold the stream into here.",
        CompactionUnavailableReason.AggregateAmbiguous =>
            "More than one registered aggregate type answers to this stream's type name, so the studio will not guess which one to fold it into.",
        CompactionUnavailableReason.NothingToFold =>
            "This stream holds a single event - or a snapshot and nothing after it - so there is nothing to fold.",
        CompactionUnavailableReason.EmptyStreamId =>
            "This stream's id is the empty GUID (or an empty key). Below Marten 9.51 the stream fetch reads that as no stream "
            + "filter at all, so compacting it would fold and delete every event in the log; from 9.51 Marten refuses to compact "
            + "it. Either way it is not offered.",
        CompactionUnavailableReason.AggregateHidden =>
            "This stream's aggregate type is one this studio is configured not to show, so it will not write that aggregate's state into an event.",
        _ => "This stream can be compacted.",
    };

    /// <summary>
    /// What compacting this stream does, one sentence per fact, for the confirmation dialog.
    /// </summary>
    /// <remarks>Every sentence is something read from Marten's source - see the class remarks.</remarks>
    public static IReadOnlyList<string> WhatHappens(CompactionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        string name = plan.AggregateName ?? "the aggregate";
        string compacted = "Compacted<" + name + ">";
        string version = plan.Version.ToString(CultureInfo.InvariantCulture);
        string earlier = Math.Max(plan.EventsToFold - 1, 0).ToString("N0", CultureInfo.InvariantCulture);

        List<string> lines =
        [
            $"Marten reads every event of this stream up to version {version}, folds them into {name} with the store's own aggregation, "
                + $"and writes the result as one {compacted} event in place of the last one - same sequence number, same version {version}.",

            plan.WasCompacted
                ? $"The {earlier} events before it, by the stream's version, are deleted from mt_events - including the {compacted} "
                    + $"an earlier compaction left at version {plan.CompactedVersion.ToString(CultureInfo.InvariantCulture)}."
                : $"The {earlier} events before it, by the stream's version, are deleted from mt_events.",

            "No archiver runs: Marten copies the folded events anywhere only when the caller hands it an IEventsArchiver, and the "
                + "studio has none to hand it. They are gone, not moved - take a backup or an export first if they may be needed.",

            $"The stream's version stays {version}, and mt_streams.compacted_version becomes {version}. The stored {name} document, "
                + "if a projection keeps one, is not rewritten.",

            "Async projections: a shard that has already processed these events is unaffected. One that has not reached them yet never "
                + $"will - they are deleted first, and the last one is a {compacted} by then. A rebuild afterwards replays only the "
                + $"snapshot: {name}'s own single-stream projection restarts from it, and every other projection that reads this stream's "
                + "events loses them.",

            $"Replaying this stream to any version below {version} - on this page, or with AggregateStreamAsync - finds nothing afterwards.",

            $"Only what this dialog describes is folded: Marten is told to stop at version {version}, so an event appended after the "
                + "dialog opened stays as it is, after the snapshot.",
        ];

        if (!string.IsNullOrEmpty(plan.StreamTenantId))
        {
            lines.Add($"Only tenant {plan.StreamTenantId}'s stream with this id is touched: every statement Marten runs is narrowed to that tenant.");
        }

        if (plan.TagTablesMayRefuse)
        {
            lines.Add("This store keeps DCB tags in tag tables, and each tag row references its event by a foreign key with no cascade: "
                + "if any event before the last carries a tag, Postgres refuses the delete and nothing changes. (From Marten 9.51 the "
                + "folded events' tag rows are deleted with them.)");
        }

        if (plan.TagRowsFoldWithEvents)
        {
            lines.Add("This store keeps DCB tags in tag tables: the folded events' tag rows are deleted with them, in the same statement, "
                + $"so a tag query no longer finds those events. The {compacted} event keeps the last event's tags.");
        }

        if (plan.SkippedEventsStay)
        {
            lines.Add("Events the daemon was told to skip are not read by Marten's stream fetch, so they are neither folded nor deleted - "
                + $"they stay in the stream. If the event at version {version} is one of them, the snapshot is written in place of the last "
                + "event before it that is not, and the stream is compacted through that version instead; the result says which.");
        }

        lines.Add("It is one transaction: all of it happens, or none of it does.");

        return lines;
    }
}
