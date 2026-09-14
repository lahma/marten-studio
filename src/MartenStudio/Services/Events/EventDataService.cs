using System.Data;
using System.Globalization;

using JasperFx.Events;
using JasperFx.Events.Daemon;

using Marten;
using Marten.Storage;

using MartenStudio.Internal.Sql;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Services.Events;

/// <summary>
/// The Events area's one data service: streams, one stream's timeline, the global feed, the event types,
/// aggregate time travel, and the dead letters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads are raw parameterised SQL, writes go through a Marten session</b> (D6). The studio only ever
/// knows event and aggregate types as runtime <see cref="Type" />s, so a generic query is not available
/// to it; every read is built by <see cref="EventQueryBuilder" /> against the column set
/// <see cref="ColumnCatalog" /> discovered, which is the only way a select list can name
/// <c>correlation_id</c> on the store that has it and not on the store that does not (U8). The three
/// writes - archive, discard, skip - are Marten's own operations and are never open-coded.
/// </para>
/// <para>
/// <b><c>bdata</c> is never selected.</b> An event with a binary payload is reported as "binary payload,
/// N bytes", which is <c>bdata is not null</c> and <c>octet_length(bdata)</c>; selecting the column would
/// push the payload down a SignalR circuit to be rendered as a sentence.
/// </para>
/// <para>
/// <b>A failed read is a value.</b> Every read method catches the database's refusal and returns it on
/// the page's own record as an <see cref="EventDataError" /> carrying the <c>SQLSTATE</c>, so a database
/// that is down breaks the region that needed it and leaves navigation, the scope selector and every
/// other page alone (plan §4.8).
/// </para>
/// <para>
/// <b>No daemon is touched here.</b> The Events screens read the database; whether a daemon is running in
/// this process is a question for the projections page, and its absence is a value rather than an error.
/// That is also why "Rewind subscription to this event" is not among the dead-letter actions in this
/// release - rewinding needs a live daemon.
/// </para>
/// </remarks>
internal sealed class EventDataService : IEventDataService
{
    /// <summary>The action names the audit ring records these operations under.</summary>
    internal const string ArchiveStreamAction = "Archive stream";

    /// <summary>The action name for discarding a dead letter.</summary>
    internal const string DiscardDeadLetterAction = "Discard dead letter";

    /// <summary>The action name for marking an event skipped.</summary>
    internal const string SkipEventAction = "Skip event";

    /// <summary>The most dead letters one page will read, however large a page is asked for.</summary>
    private const int MaxDeadLetterPageSize = 200;

    /// <summary>The most event types the screen will list.</summary>
    private const int MaxEventTypes = 2_000;

    private readonly StudioScopeResolver resolver;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly ColumnCatalog catalog;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly ILogger<EventDataService> logger;

    public EventDataService(
        StudioScopeResolver resolver,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        ColumnCatalog catalog,
        IOptions<MartenStudioOptions> options,
        ILogger<EventDataService> logger)
    {
        this.resolver = resolver;
        this.capabilities = capabilities;
        this.audit = audit;
        this.catalog = catalog;
        this.options = options;
        this.logger = logger;
    }

    private int CommandTimeoutSeconds => (int) Math.Ceiling(options.Value.QueryTimeout.TotalSeconds);

    /// <inheritdoc />
    public async Task<EventStoreShape> DescribeAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            return new EventStoreShape
            {
                StreamIdentity = table.StreamIdentity,
                Schema = table.Schema,
                EventTablesExist = table.EventColumns.Count > 0 && table.StreamColumns.Count > 0,
                HasTenantId = table.HasTenantId,
                HasIsArchived = table.HasIsArchived,
                HasIsSkipped = table.HasIsSkipped,
                HasBinaryData = table.HasBinaryData,
                OptionalColumns = MetadataColumns(table),
                TenantId = scope.TenantId,
            };
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventStoreShape.Unavailable(Describe(exception, "describe the event store"));
        }
    }

    /// <inheritdoc />
    public async Task<StreamPage> ListStreamsAsync(
        StudioScope scope,
        StreamListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table.StreamColumns.Count == 0)
            {
                return StreamPage.Empty;
            }

            int pageSize = Math.Clamp(request.PageSize, 1, options.Value.MaxPageSize);

            var query = new StreamListQuery
            {
                StreamId = request.StreamId,
                TypePrefix = string.IsNullOrWhiteSpace(request.TypePrefix) ? null : request.TypePrefix,
                TenantId = scope.TenantId,
                IncludeArchived = request.IncludeArchived,
                Cursor = request.Order == StreamListOrder.Newest
                    ? ToBuilderCursor(request.Cursor)
                    : null,
                Offset = request.Order == StreamListOrder.RecentlyActive ? request.Offset : 0,

                // One more than asked for, so "is there another page" is answered without a count.
                PageSize = pageSize + 1,
            };

            await using NpgsqlCommand command = request.Order == StreamListOrder.RecentlyActive
                ? EventQueryBuilder.BuildStreamsByRecentActivity(table, query)
                : EventQueryBuilder.BuildStreams(table, query);

            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            List<StreamRow> rows = [];
            await using (NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false))
            {
                ColumnMap columns = ColumnMap.From(reader);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(ReadStreamRow(reader, columns));
                }
            }

            bool hasMore = rows.Count > pageSize;
            if (hasMore)
            {
                rows.RemoveRange(pageSize, rows.Count - pageSize);
            }

            StreamCursor? next = null;
            if (hasMore && request.Order == StreamListOrder.Newest && rows.Count > 0)
            {
                StreamRow last = rows[^1];
                if (last.LastEvent is { } timestamp)
                {
                    next = new StreamCursor(timestamp, last.Id);
                }
            }

            return new StreamPage(rows, next, hasMore);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return StreamPage.Failed(Describe(exception, "list the streams"));
        }
    }

    /// <inheritdoc />
    public async Task<StreamState> GetStreamAsync(
        StudioScope scope,
        string streamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(streamId))
        {
            return StreamState.Missing(streamId ?? string.Empty);
        }

        StreamPage page = await ListStreamsAsync(
            scope,
            new StreamListRequest { StreamId = streamId, IncludeArchived = true, PageSize = 1 },
            cancellationToken).ConfigureAwait(false);

        if (page.Error is { } error)
        {
            return StreamState.Failed(streamId, error);
        }

        if (page.Rows.Count == 0)
        {
            return StreamState.Missing(streamId);
        }

        StreamRow row = page.Rows[0];
        return new StreamState(
            row.Id,
            Exists: true,
            row.AggregateType,
            row.Version,
            row.Created,
            row.LastEvent,
            row.IsArchived,
            row.TenantId);
    }

    /// <inheritdoc />
    public async Task<EventPage> GetStreamEventsAsync(
        StudioScope scope,
        string streamId,
        long afterVersion,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table.EventColumns.Count == 0)
            {
                return EventPage.Empty;
            }

            int pageSize = Math.Clamp(take, 1, options.Value.MaxPageSize);

            if (!EventQueryBuilder.TryBuildStreamEvents(
                    table, streamId ?? string.Empty, afterVersion, pageSize + 1, scope.TenantId,
                    out NpgsqlCommand? command, out string? error))
            {
                return EventPage.Failed(new EventDataError(error ?? "That is not a usable stream id.", null, false));
            }

            await using (command)
            {
                command!.Connection = connection;
                command.CommandTimeout = CommandTimeoutSeconds;

                List<EventRow> rows = await ReadEventsAsync(command, table, cancellationToken).ConfigureAwait(false);

                bool hasMore = rows.Count > pageSize;
                if (hasMore)
                {
                    rows.RemoveRange(pageSize, rows.Count - pageSize);
                }

                return new EventPage(rows, rows.Count > 0 ? rows[^1].Sequence : null, hasMore);
            }
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventPage.Failed(Describe(exception, "read the stream's events"));
        }
    }

    /// <inheritdoc />
    public async Task<EventPage> GetFeedAsync(
        StudioScope scope,
        EventFeedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table.EventColumns.Count == 0)
            {
                return EventPage.Empty;
            }

            int pageSize = Math.Clamp(request.PageSize, 1, options.Value.MaxPageSize);

            await using NpgsqlCommand command = EventQueryBuilder.BuildFeed(table, new EventFeedQuery
            {
                AfterSequence = request.AfterSequence,
                TenantId = scope.TenantId,
                StreamId = request.StreamId,
                EventTypes = request.EventTypes,
                From = request.From,
                To = request.To,
                IncludeArchived = request.IncludeArchived,
                IncludeSkipped = request.IncludeSkipped,
                PageSize = pageSize + 1,
            });

            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            List<EventRow> rows = await ReadEventsAsync(command, table, cancellationToken).ConfigureAwait(false);

            // Follow mode asks for everything after a sequence it already has; the builder pages
            // backwards, so the delta is the head of the newest page, trimmed here rather than by a
            // second statement with a different plan.
            if (request.SinceSequence is { } since)
            {
                rows.RemoveAll(row => row.Sequence <= since);
                return new EventPage(rows, rows.Count > 0 ? rows[^1].Sequence : null, HasMore: false);
            }

            bool hasMore = rows.Count > pageSize;
            if (hasMore)
            {
                rows.RemoveRange(pageSize, rows.Count - pageSize);
            }

            return new EventPage(rows, rows.Count > 0 ? rows[^1].Sequence : null, hasMore);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventPage.Failed(Describe(exception, "read the event feed"));
        }
    }

    /// <inheritdoc />
    public async Task<long?> GetHighestSequenceAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            // Marten's own read, not the builder's fallback: it is the supported route and it knows
            // about the high-water mark rather than only about the table.
            return await resolved.Database.FetchHighestEventSequenceNumber(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            logger.LogDebug(exception, "Marten Studio could not read the highest event sequence");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<EventTypeList> ListEventTypesAsync(
        StudioScope scope,
        bool withCounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            Dictionary<string, EventTypeInfo> registered = new(StringComparer.Ordinal);
            foreach (IEventType eventType in resolved.Store.Options.Events.AllKnownEventTypes())
            {
                registered[eventType.EventTypeName] = new EventTypeInfo(
                    eventType.EventTypeName,
                    eventType.DotNetTypeName,
                    IsRegistered: true);
            }

            if (!withCounts)
            {
                return new EventTypeList(Order(registered.Values), CountsLoaded: false);
            }

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table.EventColumns.Count == 0)
            {
                return new EventTypeList(Order(registered.Values), CountsLoaded: true);
            }

            await using NpgsqlCommand command = EventQueryBuilder.BuildEventTypeCounts(table);
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            Dictionary<string, EventTypeInfo> merged = new(registered, StringComparer.Ordinal);

            await using (NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && merged.Count < MaxEventTypes)
                {
                    string name = reader.GetString(0);
                    long count = reader.GetInt64(1);
                    long first = reader.GetInt64(2);
                    long last = reader.GetInt64(3);

                    EventTypeInfo? known = merged.GetValueOrDefault(name);
                    merged[name] = new EventTypeInfo(
                        name,
                        known?.DotNetTypeName,
                        known is not null,
                        count,
                        first,
                        last);
                }
            }

            // A registered type the group-by never mentioned has no events at all, which is a fact worth
            // flagging rather than an unknown.
            foreach (string name in registered.Keys)
            {
                if (merged[name].Count is null)
                {
                    merged[name] = merged[name] with { Count = 0 };
                }
            }

            return new EventTypeList(Order(merged.Values), CountsLoaded: true);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventTypeList.Failed(Describe(exception, "list the event types"));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AggregateTypeCandidate>> ListAggregateCandidatesAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            return AggregateStreamInvoker.Candidates(resolved.Store.Options);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            logger.LogDebug(exception, "Marten Studio could not list the aggregate types of a store");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<AggregateSnapshot> AggregateAtVersionAsync(
        StudioScope scope,
        string streamId,
        string aggregateTypeFullName,
        long version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string typeName = aggregateTypeFullName ?? string.Empty;

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            AggregateTypeCandidate? candidate = null;
            foreach (AggregateTypeCandidate known in AggregateStreamInvoker.Candidates(resolved.Store.Options))
            {
                if (string.Equals(known.FullName, typeName, StringComparison.Ordinal))
                {
                    candidate = known;
                    break;
                }
            }

            if (candidate is null)
            {
                // Never Type.GetType on a string a browser supplied: the picker offers the store's own
                // types and nothing else is replayable.
                return AggregateSnapshot.Failed(
                    typeName,
                    version,
                    new EventDataError($"'{typeName}' is not one of this store's aggregate types.", null, false));
            }

            IdParseResult parsed = ParseStreamIdFor(resolved, streamId);
            if (!parsed.Success)
            {
                return AggregateSnapshot.Failed(
                    candidate.Name, version, new EventDataError(parsed.Error ?? "That stream id is not usable.", null, false));
            }

            await using IQuerySession session = OpenQuerySession(resolved);

            object? aggregate = await AggregateStreamInvoker
                .AggregateAtVersionAsync(session, candidate.Type, parsed.Value!, version, cancellationToken)
                .ConfigureAwait(false);

            if (aggregate is null)
            {
                return AggregateSnapshot.NotFound(candidate.Name, version);
            }

            // The store's own serializer, never a JsonSerializer of ours (AGENTS.md hard rule 10): an
            // aggregate rendered through different settings than Marten writes with is a lie about the
            // document that projection would store.
            string json = resolved.Store.Options.Serializer().ToJson(aggregate);

            return new AggregateSnapshot(candidate.Name, version, json, Found: true);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return AggregateSnapshot.Failed(typeName, version, Describe(exception, "replay the stream"));
        }
    }

    /// <inheritdoc />
    public async Task<DeadLetterPage> ListDeadLettersAsync(
        StudioScope scope,
        DeadLetterQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            int pageSize = Math.Clamp(query.PageSize, 1, MaxDeadLetterPageSize);

            await using IQuerySession session = OpenQuerySession(resolved);

            IQueryable<DeadLetterEvent> queryable = session.Query<DeadLetterEvent>();

            if (!string.IsNullOrWhiteSpace(query.ProjectionName))
            {
                string projection = query.ProjectionName;
                queryable = queryable.Where(x => x.ProjectionName == projection);
            }

            if (!string.IsNullOrWhiteSpace(query.ShardName))
            {
                string shard = query.ShardName;
                queryable = queryable.Where(x => x.ShardName == shard);
            }

            if (!string.IsNullOrWhiteSpace(query.ExceptionType))
            {
                string exceptionType = query.ExceptionType;
                queryable = queryable.Where(x => x.ExceptionType.Contains(exceptionType));
            }

            // Async terminals only - Marten 9 has no synchronous ones, they throw (AGENTS.md hard rule 10).
            IReadOnlyList<DeadLetterEvent> found = await queryable
                .OrderByDescending(x => x.Timestamp)
                .Skip(Math.Max(0, query.Offset))
                .Take(pageSize + 1)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            List<DeadLetterRow> rows = [];
            foreach (DeadLetterEvent letter in found)
            {
                rows.Add(new DeadLetterRow(
                    letter.Id,
                    letter.ProjectionName ?? string.Empty,
                    letter.ShardName ?? string.Empty,
                    letter.Timestamp,
                    letter.ExceptionType ?? string.Empty,
                    letter.ExceptionMessage ?? string.Empty,
                    letter.EventSequence,
                    letter.TenantId ?? string.Empty));
            }

            bool hasMore = rows.Count > pageSize;
            if (hasMore)
            {
                rows.RemoveRange(pageSize, rows.Count - pageSize);
            }

            return new DeadLetterPage(rows, hasMore);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return DeadLetterPage.Failed(Describe(exception, "list the dead letters"));
        }
    }

    /// <inheritdoc />
    public async Task<long?> CountDeadLettersAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);

            // Marten registers DeadLetterEvent into the *event* store's schema, single-tenanted
            // (StoreOptions.ApplyConfiguration: Schema.For<DeadLetterEvent>().DatabaseSchemaName(
            // Events.DatabaseSchemaName).SingleTenanted()), which is why the count is scoped by the event
            // schema rather than by the document schema.
            string schema = resolved.Store.Options.Events.DatabaseSchemaName;

            TableColumns table = await catalog
                .GetAsync(connection, schema, EventTableInfo.DeadLetterTable, cancellationToken)
                .ConfigureAwait(false);

            if (!table.Exists)
            {
                // No table means the daemon never recorded one, which is a real zero rather than a
                // failure - and a store with no async projections never creates it at all.
                return 0;
            }

            await using NpgsqlCommand command = EventQueryBuilder.BuildDeadLetterCount(schema);
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return scalar is long count ? count : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            logger.LogDebug(exception, "Marten Studio could not count the dead letters");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<EventRow?> GetEventBySequenceAsync(
        StudioScope scope,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table.EventColumns.Count == 0)
            {
                return null;
            }

            await using NpgsqlCommand command = EventQueryBuilder.BuildEventBySequence(table, sequence);
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            List<EventRow> rows = await ReadEventsAsync(command, table, cancellationToken).ConfigureAwait(false);
            return rows.Count > 0 ? rows[0] : null;
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio could not read event {Sequence}", sequence);
            }

            return null;
        }
    }

    /// <inheritdoc />
    public async Task ArchiveStreamAsync(
        StudioScope scope,
        string streamId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string target = streamId ?? string.Empty;

        await MutateAsync(
            scope,
            StudioCapability.ArchiveStreams,
            ArchiveStreamAction,
            target,
            async (resolved, token) =>
            {
                IdParseResult parsed = ParseStreamIdFor(resolved, target);
                if (!parsed.Success)
                {
                    throw new InvalidOperationException(parsed.Error ?? "That stream id is not usable.");
                }

                await using IDocumentSession session = OpenWriteSession(resolved);

                // Queued on the session, applied by SaveChangesAsync - which is why it takes no token
                // and returns void (verified against Marten 9.35's IEventStoreOperations).
                if (parsed.Value is Guid guid)
                {
                    session.Events.ArchiveStream(guid);
                }
                else
                {
                    session.Events.ArchiveStream((string) parsed.Value!);
                }

                await session.SaveChangesAsync(token).ConfigureAwait(false);

                return "archived";
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DiscardDeadLetterAsync(
        StudioScope scope,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await MutateAsync(
            scope,
            StudioCapability.ManageDeadLetters,
            DiscardDeadLetterAction,
            id.ToString(),
            async (resolved, token) =>
            {
                await using IDocumentSession session = OpenWriteSession(resolved);

                DeadLetterEvent? letter = await session.Query<DeadLetterEvent>()
                    .Where(x => x.Id == id)
                    .FirstOrDefaultAsync(token)
                    .ConfigureAwait(false);

                if (letter is null)
                {
                    return "already gone";
                }

                // The non-generic write API: the studio holds documents as object and re-implementing
                // Marten's write path is how a UI corrupts a store (D6).
                session.DeleteObjects([letter]);
                await session.SaveChangesAsync(token).ConfigureAwait(false);

                return "discarded";
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SkipEventAsync(
        StudioScope scope,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await MutateAsync(
            scope,
            StudioCapability.ManageDeadLetters,
            SkipEventAction,
            sequence.ToString(CultureInfo.InvariantCulture),
            async (resolved, token) =>
            {
                await using (NpgsqlConnection connection = await OpenAsync(resolved, token).ConfigureAwait(false))
                {
                    EventTableInfo table = await DescribeTablesAsync(resolved, connection, token).ConfigureAwait(false);

                    if (!table.HasIsSkipped)
                    {
                        throw new InvalidOperationException(
                            "This store does not record skipped events: mt_events has no is_skipped column, " +
                            "which means the host did not enable event skipping in projections or subscriptions. " +
                            "Marking an event as skipped would have nothing to write to.");
                    }
                }

                await resolved.Database.MarkEventsAsSkipped([sequence], token).ConfigureAwait(false);

                return "marked as skipped";
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The shape of every mutating call: capability, then scope with the write policy, then the
    /// operation, then the audit entry - written on failure as well as on success (hard rule 5).
    /// </summary>
    private async Task MutateAsync(
        StudioScope scope,
        StudioCapability capability,
        string action,
        string target,
        Func<ResolvedScope, CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            capabilities.Require(capability);
        }
        catch (StudioCapabilityDeniedException denied)
        {
            audit.RecordCapabilityDenied(denied, action, target);
            throw;
        }

        ResolvedScope resolved;
        try
        {
            resolved = await resolver.ResolveAsync(scope, capability.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, options.Value.WriteAuthorizationPolicy ?? "(none)", action, target);
            throw;
        }

        try
        {
            string outcome = await operation(resolved, cancellationToken).ConfigureAwait(false);
            audit.Record(action, target, succeeded: true, outcome, capability, scope);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            audit.Record(action, target, succeeded: false, exception.Message, capability, scope);
            throw;
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The event tables' real column set, from <c>information_schema</c> and cached.</summary>
    private async Task<EventTableInfo> DescribeTablesAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        string schema = resolved.Store.Options.Events.DatabaseSchemaName;

        TableColumns events = await catalog
            .GetAsync(connection, schema, EventTableInfo.EventsTable, cancellationToken).ConfigureAwait(false);
        TableColumns streams = await catalog
            .GetAsync(connection, schema, EventTableInfo.StreamsTable, cancellationToken).ConfigureAwait(false);

        return EventTableInfo.FromColumns(schema, resolved.Store.Options.Events.StreamIdentity, events, streams);
    }

    /// <summary>A read session on the scope's tenant, or on the store's default when none is pinned.</summary>
    private static IQuerySession OpenQuerySession(ResolvedScope resolved) =>
        resolved.TenantId is { } tenantId
            ? resolved.Store.QuerySession(tenantId)
            : resolved.Store.QuerySession();

    /// <summary>A write session on the scope's tenant.</summary>
    private static IDocumentSession OpenWriteSession(ResolvedScope resolved) =>
        resolved.TenantId is { } tenantId
            ? resolved.Store.LightweightSession(tenantId)
            : resolved.Store.LightweightSession();

    private static IdParseResult ParseStreamIdFor(ResolvedScope resolved, string streamId) =>
        new EventTableInfo
        {
            Schema = resolved.Store.Options.Events.DatabaseSchemaName,
            StreamIdentity = resolved.Store.Options.Events.StreamIdentity,
        }.ParseStreamId(streamId);

    private static List<EventTypeInfo> Order(IEnumerable<EventTypeInfo> types) =>
        [.. types
            .OrderByDescending(static x => x.IsRegistered)
            .ThenBy(static x => x.Name, StringComparer.Ordinal)];

    /// <summary>The optional columns whose values become the metadata chips on an event card.</summary>
    private static List<string> MetadataColumns(EventTableInfo table)
    {
        List<string> present = [];

        foreach (string column in
                 (string[]) ["tenant_id", "mt_dotnet_type", "correlation_id", "causation_id", "headers", "user_name", "tags"])
        {
            if (table.HasEventColumn(column))
            {
                present.Add(column);
            }
        }

        return present;
    }

    private static async Task<List<EventRow>> ReadEventsAsync(
        NpgsqlCommand command,
        EventTableInfo table,
        CancellationToken cancellationToken)
    {
        List<string> metadataColumns = MetadataColumns(table);

        List<EventRow> rows = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);

        ColumnMap columns = ColumnMap.From(reader);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Dictionary<string, string?> metadata = new(StringComparer.Ordinal);
            foreach (string column in metadataColumns)
            {
                metadata[column] = columns.Text(reader, column);
            }

            rows.Add(new EventRow(
                columns.Int64(reader, "seq_id") ?? 0,
                columns.Guid(reader, "id") ?? Guid.Empty,
                columns.Text(reader, "stream_id") ?? string.Empty,
                columns.Int64(reader, "version") ?? 0,
                columns.Text(reader, "type") ?? string.Empty,
                columns.Timestamp(reader, "timestamp") ?? default,
                columns.Text(reader, "tenant_id"),
                columns.Text(reader, "mt_dotnet_type"),
                columns.Boolean(reader, "is_archived") ?? false,
                columns.Boolean(reader, "is_skipped") ?? false,
                columns.Boolean(reader, "has_binary") ?? false,
                columns.Int64(reader, "binary_length"),
                columns.Text(reader, "data"),
                metadata));
        }

        return rows;
    }

    private static StreamRow ReadStreamRow(NpgsqlDataReader reader, ColumnMap columns) =>
        new(
            columns.Text(reader, "id") ?? string.Empty,
            columns.Text(reader, "type"),
            columns.Int64(reader, "version") ?? 0,
            columns.Timestamp(reader, "created"),
            columns.Timestamp(reader, "timestamp"),
            columns.Boolean(reader, "is_archived") ?? false,
            columns.Text(reader, "tenant_id"));

    private static StreamKeysetCursor? ToBuilderCursor(StreamCursor? cursor) =>
        cursor is null ? null : new StreamKeysetCursor(cursor.Timestamp, cursor.Id);

    /// <summary>
    /// Whether an exception is one a read turns into a value rather than letting it kill the circuit.
    /// </summary>
    /// <remarks>
    /// Cancellation is not: a cancelled read is the page being disposed, and swallowing it would draw a
    /// failure where the visitor simply navigated away.
    /// </remarks>
    private static bool IsReadFailure(Exception exception) => exception is not OperationCanceledException;

    /// <summary>Renders a failure the way plan §4.8 asks: SQLSTATE, message, and whether to offer Retry.</summary>
    private EventDataError Describe(Exception exception, string what)
    {
        if (exception is PostgresException postgres)
        {
            logger.LogWarning(exception, "Marten Studio could not {What}: {SqlState}", what, postgres.SqlState);
            return new EventDataError(postgres.MessageText, postgres.SqlState, EventDataError.IsRetryable(postgres.SqlState));
        }

        if (exception is StudioNotAuthorizedException)
        {
            // Deliberately says nothing about what is behind the refusal (D5).
            return new EventDataError("Not authorized for this store, database or tenant.", null, false);
        }

        if (exception is StudioStoreUnavailableException unavailable)
        {
            return new EventDataError(unavailable.Reason, null, true);
        }

        if (exception is NpgsqlException)
        {
            logger.LogWarning(exception, "Marten Studio could not {What}", what);
            return new EventDataError(exception.Message, null, true);
        }

        logger.LogWarning(exception, "Marten Studio could not {What}", what);
        return new EventDataError(exception.Message, null, false);
    }

    /// <summary>
    /// The reader's columns by name, so a select list that changes with the store's column set can still
    /// be read positionally-free. Built once per reader, not per row.
    /// </summary>
    private sealed class ColumnMap
    {
        private readonly Dictionary<string, int> ordinals;

        private ColumnMap(Dictionary<string, int> ordinals) => this.ordinals = ordinals;

        public static ColumnMap From(NpgsqlDataReader reader)
        {
            Dictionary<string, int> ordinals = new(reader.FieldCount, StringComparer.Ordinal);

            for (int index = 0; index < reader.FieldCount; index++)
            {
                ordinals[reader.GetName(index)] = index;
            }

            return new ColumnMap(ordinals);
        }

        public string? Text(NpgsqlDataReader reader, string column)
        {
            if (!ordinals.TryGetValue(column, out int index) || reader.IsDBNull(index))
            {
                return null;
            }

            object value = reader.GetValue(index);
            return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public long? Int64(NpgsqlDataReader reader, string column) =>
            ordinals.TryGetValue(column, out int index) && !reader.IsDBNull(index) ? reader.GetInt64(index) : null;

        public bool? Boolean(NpgsqlDataReader reader, string column) =>
            ordinals.TryGetValue(column, out int index) && !reader.IsDBNull(index) ? reader.GetBoolean(index) : null;

        public Guid? Guid(NpgsqlDataReader reader, string column) =>
            ordinals.TryGetValue(column, out int index) && !reader.IsDBNull(index) ? reader.GetGuid(index) : null;

        public DateTimeOffset? Timestamp(NpgsqlDataReader reader, string column) =>
            ordinals.TryGetValue(column, out int index) && !reader.IsDBNull(index)
                ? reader.GetFieldValue<DateTimeOffset>(index)
                : null;
    }
}
