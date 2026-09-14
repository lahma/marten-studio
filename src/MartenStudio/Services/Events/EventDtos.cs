using System.Globalization;

using JasperFx.Events;

namespace MartenStudio.Services.Events;

/// <summary>
/// A read that could not be completed, as a value rather than an exception.
/// </summary>
/// <remarks>
/// Plan §4.8: a <c>PostgresException</c> is rendered as its <c>SqlState</c> plus its message, and the
/// four states that mean "try again, or fix the environment" - <c>28P01</c> bad credentials,
/// <c>3D000</c> no such database, <c>08006</c> connection failure, <c>57014</c> cancelled by
/// <c>statement_timeout</c> - are offered a Retry. A region that failed says so; it never blanks the
/// page, and it is never drawn the same way as an empty result.
/// </remarks>
/// <param name="Message">What went wrong, in the words the database or the studio used.</param>
/// <param name="SqlState">The Postgres <c>SQLSTATE</c>, when Postgres was the one that refused.</param>
/// <param name="CanRetry">Whether trying again could plausibly work.</param>
internal sealed record EventDataError(string Message, string? SqlState, bool CanRetry)
{
    /// <summary>The states worth offering a Retry for.</summary>
    private static readonly string[] RetryableStates = ["28P01", "3D000", "08006", "57014"];

    /// <summary>Whether <paramref name="sqlState" /> is one the studio offers a Retry for.</summary>
    public static bool IsRetryable(string? sqlState) =>
        sqlState is not null && Array.IndexOf(RetryableStates, sqlState) >= 0;

    /// <summary>The error as one line, with the SQLSTATE in front when there is one.</summary>
    public string Display => SqlState is null ? Message : $"[{SqlState}] {Message}";
}

/// <summary>
/// The shape of one store's event store, as the pages need it before they can draw a control.
/// </summary>
/// <remarks>
/// Every one of these is discovered, not assumed. The stream identity decides whether the id box wants a
/// GUID; the optional columns decide which metadata chips exist at all; <see cref="HasIsSkipped" />
/// decides whether "Skip event" is an action this store can perform at all, because
/// <c>MarkEventsAsSkipped</c> writes a column only a store with event skipping enabled has.
/// </remarks>
internal sealed record EventStoreShape
{
    /// <summary>The shape of a store that could not be reached at all.</summary>
    public static EventStoreShape Unavailable(EventDataError error) => new() { Error = error };

    /// <summary>Whether the event store could be described.</summary>
    public bool Available => Error is null;

    /// <summary>Why it could not be described, when it could not.</summary>
    public EventDataError? Error { get; init; }

    /// <summary>Whether stream ids are GUIDs or strings.</summary>
    public StreamIdentity StreamIdentity { get; init; } = StreamIdentity.AsGuid;

    /// <summary>The schema the event tables live in.</summary>
    public string Schema { get; init; } = string.Empty;

    /// <summary>Whether the event tables exist - a store whose event storage was never created has none.</summary>
    public bool EventTablesExist { get; init; }

    /// <summary>Whether events carry a tenant.</summary>
    public bool HasTenantId { get; init; }

    /// <summary>Whether events can be archived.</summary>
    public bool HasIsArchived { get; init; }

    /// <summary>Whether the daemon's skipped-event marker column exists.</summary>
    public bool HasIsSkipped { get; init; }

    /// <summary>Whether an event can carry a binary payload.</summary>
    public bool HasBinaryData { get; init; }

    /// <summary>The optional event columns this store actually has, in select order.</summary>
    public IReadOnlyList<string> OptionalColumns { get; init; } = [];

    /// <summary>The tenant the current scope is pinned to, or <see langword="null" /> for all of them.</summary>
    public string? TenantId { get; init; }
}

/// <summary>One row of the stream list.</summary>
/// <param name="Id">The stream id as text, which is also how it travels in a link (D9).</param>
/// <param name="AggregateType">The aggregate type name the stream was started with, when it was.</param>
/// <param name="Version">How many events the stream holds.</param>
/// <param name="Created">When the stream was started.</param>
/// <param name="LastEvent">When it was last appended to.</param>
/// <param name="IsArchived">Whether the stream is archived.</param>
/// <param name="TenantId">The tenant, when the store is tenanted.</param>
internal sealed record StreamRow(
    string Id,
    string? AggregateType,
    long Version,
    DateTimeOffset? Created,
    DateTimeOffset? LastEvent,
    bool IsArchived,
    string? TenantId);

/// <summary>Where the next keyset page of the stream list starts.</summary>
/// <param name="Timestamp">The last row's timestamp.</param>
/// <param name="Id">The last row's id, as text.</param>
internal sealed record StreamCursor(DateTimeOffset Timestamp, string Id)
{
    /// <summary>The cursor as one URL-safe token, so a page of the list is a link (D9).</summary>
    public string ToToken() =>
        Timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "|" + Id;

    /// <summary>Reads a token back, or <see langword="null" /> when it is not one.</summary>
    public static StreamCursor? FromToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var separator = token.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator == token.Length - 1)
        {
            return null;
        }

        return long.TryParse(
            token.AsSpan(0, separator),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var milliseconds)
            ? new StreamCursor(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), token[(separator + 1)..])
            : null;
    }
}

/// <summary>How the stream list is ordered.</summary>
internal enum StreamListOrder
{
    /// <summary>By the streams table's own timestamp, keyset-paged. The default, and the cheap one.</summary>
    Newest,

    /// <summary>By the highest event sequence per stream. Offset-paged, and an aggregate over the events.</summary>
    RecentlyActive
}

/// <summary>What the streams page is asking for.</summary>
internal sealed record StreamListRequest
{
    /// <summary>One stream by id, as typed.</summary>
    public string? StreamId { get; init; }

    /// <summary>Streams whose aggregate type name starts with this.</summary>
    public string? TypePrefix { get; init; }

    /// <summary>Whether archived streams are included.</summary>
    public bool IncludeArchived { get; init; }

    /// <summary>How the list is ordered.</summary>
    public StreamListOrder Order { get; init; } = StreamListOrder.Newest;

    /// <summary>The keyset cursor, for <see cref="StreamListOrder.Newest" />.</summary>
    public StreamCursor? Cursor { get; init; }

    /// <summary>The offset, for <see cref="StreamListOrder.RecentlyActive" />.</summary>
    public int Offset { get; init; }

    /// <summary>How many streams to return.</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>One page of the stream list.</summary>
/// <param name="Rows">The streams, in the order asked for.</param>
/// <param name="NextCursor">Where the next keyset page starts, or <see langword="null" /> at the end.</param>
/// <param name="HasMore">Whether there is another page.</param>
/// <param name="Error">What went wrong, when the page could not be read.</param>
internal sealed record StreamPage(
    IReadOnlyList<StreamRow> Rows,
    StreamCursor? NextCursor,
    bool HasMore,
    EventDataError? Error = null)
{
    /// <summary>An empty page that failed.</summary>
    public static StreamPage Failed(EventDataError error) => new([], null, false, error);

    /// <summary>An empty page that succeeded.</summary>
    public static StreamPage Empty { get; } = new([], null, false);
}

/// <summary>
/// The header facts about one stream.
/// </summary>
/// <remarks>
/// Read from <c>mt_streams</c> through the builder rather than from
/// <c>session.Events.FetchStreamStateAsync</c>: the row carries the tenant as well, which the typed API
/// does not return, and one statement answers for both stream identities.
/// </remarks>
/// <param name="Id">The stream id as text.</param>
/// <param name="Exists">Whether there is such a stream at all.</param>
/// <param name="AggregateType">The aggregate type name the stream was started with.</param>
/// <param name="Version">How many events it holds.</param>
/// <param name="Created">When it was started.</param>
/// <param name="LastEvent">When it was last appended to.</param>
/// <param name="IsArchived">Whether it is archived.</param>
/// <param name="TenantId">Its tenant, when the store is tenanted.</param>
/// <param name="Error">What went wrong, when it could not be read.</param>
internal sealed record StreamState(
    string Id,
    bool Exists,
    string? AggregateType,
    long Version,
    DateTimeOffset? Created,
    DateTimeOffset? LastEvent,
    bool IsArchived,
    string? TenantId,
    EventDataError? Error = null)
{
    /// <summary>A stream that could not be read.</summary>
    public static StreamState Failed(string id, EventDataError error) =>
        new(id, false, null, 0, null, null, false, null, error);

    /// <summary>A stream that is not there.</summary>
    public static StreamState Missing(string id) => new(id, false, null, 0, null, null, false, null);
}

/// <summary>One event, as the timeline and the feed draw it.</summary>
/// <param name="Sequence">The global <c>seq_id</c>, which is what the feed keysets on.</param>
/// <param name="Id">The event's own id.</param>
/// <param name="StreamId">The stream it belongs to, as text.</param>
/// <param name="Version">Its version within that stream.</param>
/// <param name="EventType">The event type name Marten stored.</param>
/// <param name="Timestamp">When it was appended.</param>
/// <param name="TenantId">Its tenant, when the store is tenanted.</param>
/// <param name="DotNetType">The .NET type name, when the store records one.</param>
/// <param name="IsArchived">Whether it belongs to an archived stream.</param>
/// <param name="IsSkipped">Whether the daemon was told to skip it.</param>
/// <param name="HasBinary">Whether it carries a binary payload. The payload itself is never read.</param>
/// <param name="BinaryLength">How many bytes that payload is.</param>
/// <param name="Json">The event body, or <see langword="null" /> when there is only a binary one.</param>
/// <param name="Metadata">Every optional column this store has, by column name.</param>
internal sealed record EventRow(
    long Sequence,
    Guid Id,
    string StreamId,
    long Version,
    string EventType,
    DateTimeOffset Timestamp,
    string? TenantId,
    string? DotNetType,
    bool IsArchived,
    bool IsSkipped,
    bool HasBinary,
    long? BinaryLength,
    string? Json,
    IReadOnlyDictionary<string, string?> Metadata);

/// <summary>One page of events.</summary>
/// <param name="Rows">The events: newest first in the feed, oldest first in a timeline.</param>
/// <param name="NextSequence">The keyset cursor for the next page, or <see langword="null" /> at the end.</param>
/// <param name="HasMore">Whether there is another page.</param>
/// <param name="Error">What went wrong, when the page could not be read.</param>
internal sealed record EventPage(
    IReadOnlyList<EventRow> Rows,
    long? NextSequence,
    bool HasMore,
    EventDataError? Error = null)
{
    /// <summary>An empty page that failed.</summary>
    public static EventPage Failed(EventDataError error) => new([], null, false, error);

    /// <summary>An empty page that succeeded.</summary>
    public static EventPage Empty { get; } = new([], null, false);
}

/// <summary>What the global feed is asking for.</summary>
internal sealed record EventFeedRequest
{
    /// <summary>Only events before this sequence. Null for the newest page.</summary>
    public long? AfterSequence { get; init; }

    /// <summary>Only events after this sequence, for follow mode's delta read.</summary>
    public long? SinceSequence { get; init; }

    /// <summary>Only these event type names. Empty means every type.</summary>
    public IReadOnlyList<string> EventTypes { get; init; } = [];

    /// <summary>Only this stream, as typed.</summary>
    public string? StreamId { get; init; }

    /// <summary>Only events at or after this instant.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only events before this instant.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Whether archived events are included.</summary>
    public bool IncludeArchived { get; init; }

    /// <summary>Whether events the daemon was told to skip are included.</summary>
    public bool IncludeSkipped { get; init; } = true;

    /// <summary>How many events to return.</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>One row of the event-types screen.</summary>
/// <param name="Name">The event type name as it appears in <c>mt_events.type</c>.</param>
/// <param name="DotNetTypeName">The .NET type name Marten records, when the type is registered.</param>
/// <param name="IsRegistered">Whether the store knows this type, or it was only seen in the table.</param>
/// <param name="Count">How many events there are, once counts have been loaded.</param>
/// <param name="FirstSequence">The lowest sequence of this type.</param>
/// <param name="LastSequence">The highest sequence of this type.</param>
internal sealed record EventTypeInfo(
    string Name,
    string? DotNetTypeName,
    bool IsRegistered,
    long? Count = null,
    long? FirstSequence = null,
    long? LastSequence = null)
{
    /// <summary>
    /// Whether this is a registered type with no events at all. Only meaningful once counts are loaded:
    /// before that every count is unknown, which is a different thing from zero.
    /// </summary>
    public bool IsRegisteredButEmpty => IsRegistered && Count == 0;

    /// <summary>Whether this type is in the table but not in the store's configuration.</summary>
    public bool IsUnregistered => !IsRegistered;
}

/// <summary>Every event type the screen knows about, and whether the counts were loaded.</summary>
/// <param name="Types">The types, registered ones first.</param>
/// <param name="CountsLoaded">Whether the group-by has been run.</param>
/// <param name="Error">What went wrong, when the list could not be read.</param>
internal sealed record EventTypeList(
    IReadOnlyList<EventTypeInfo> Types,
    bool CountsLoaded,
    EventDataError? Error = null)
{
    /// <summary>An empty list that failed.</summary>
    public static EventTypeList Failed(EventDataError error) => new([], false, error);
}

/// <summary>One dead letter.</summary>
/// <param name="Id">The dead letter document's own id, which is what Discard deletes.</param>
/// <param name="ProjectionName">The projection whose shard failed.</param>
/// <param name="ShardName">The shard.</param>
/// <param name="Timestamp">When it failed.</param>
/// <param name="ExceptionType">The exception's type name.</param>
/// <param name="ExceptionMessage">The exception's message.</param>
/// <param name="EventSequence">The sequence of the event that could not be applied.</param>
/// <param name="TenantId">The tenant the failing event belonged to.</param>
internal sealed record DeadLetterRow(
    Guid Id,
    string ProjectionName,
    string ShardName,
    DateTimeOffset Timestamp,
    string ExceptionType,
    string ExceptionMessage,
    long EventSequence,
    string TenantId);

/// <summary>One page of dead letters.</summary>
/// <param name="Rows">The dead letters, newest failure first.</param>
/// <param name="HasMore">Whether there is another page.</param>
/// <param name="Error">What went wrong, when the page could not be read.</param>
internal sealed record DeadLetterPage(
    IReadOnlyList<DeadLetterRow> Rows,
    bool HasMore,
    EventDataError? Error = null)
{
    /// <summary>An empty page that failed.</summary>
    public static DeadLetterPage Failed(EventDataError error) => new([], false, error);

    /// <summary>An empty page that succeeded.</summary>
    public static DeadLetterPage Empty { get; } = new([], false);
}

/// <summary>What the dead-letter list is asking for.</summary>
internal sealed record DeadLetterQuery
{
    /// <summary>Only this projection's dead letters.</summary>
    public string? ProjectionName { get; init; }

    /// <summary>Only this shard's.</summary>
    public string? ShardName { get; init; }

    /// <summary>Only those whose exception type contains this.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>How many to skip.</summary>
    public int Offset { get; init; }

    /// <summary>How many to return.</summary>
    public int PageSize { get; init; } = 50;
}

/// <summary>
/// An aggregate type the stream detail page can replay a stream into.
/// </summary>
/// <param name="Type">The CLR type the generic <c>AggregateStreamAsync</c> is closed over.</param>
/// <param name="Name">Its short name, for the picker.</param>
/// <param name="FullName">Its full name, for the tooltip.</param>
/// <param name="Source">
/// Where it came from: a registered single-stream projection, or the manual picker over every document
/// type the store knows.
/// </param>
internal sealed record AggregateTypeCandidate(Type Type, string Name, string FullName, AggregateCandidateSource Source);

/// <summary>Where an aggregate-type candidate came from.</summary>
internal enum AggregateCandidateSource
{
    /// <summary>A registered single-stream projection whose identity matches this store's streams.</summary>
    SingleStreamProjection,

    /// <summary>Any document type the store knows, offered as the fallback picker.</summary>
    DocumentType
}

/// <summary>The result of replaying a stream to a version.</summary>
/// <param name="TypeName">The aggregate type that was replayed into.</param>
/// <param name="Version">The version it was replayed to.</param>
/// <param name="Json">The aggregate, serialized with the store's own serializer.</param>
/// <param name="Found">Whether the replay produced an aggregate at all.</param>
/// <param name="Error">What went wrong, when the replay failed.</param>
internal sealed record AggregateSnapshot(
    string TypeName,
    long Version,
    string? Json,
    bool Found,
    EventDataError? Error = null)
{
    /// <summary>A replay that failed.</summary>
    public static AggregateSnapshot Failed(string typeName, long version, EventDataError error) =>
        new(typeName, version, null, false, error);

    /// <summary>A replay that produced nothing: no events of that shape, or a deleted aggregate.</summary>
    public static AggregateSnapshot NotFound(string typeName, long version) =>
        new(typeName, version, null, false);
}
