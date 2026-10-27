using System.Data;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;

using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

using Marten;
using Marten.Services;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

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
/// <b>Exactly one call here touches a daemon.</b> Every read goes to the database, and whether a daemon
/// runs in this process is a question the projections page answers; its absence is a value rather than an
/// error. <see cref="RewindSubscriptionAsync" /> is the exception, because rewinding a subscription is
/// something only a running daemon can be asked to do - and it asks through
/// <see cref="MartenStudio.Services.Projections.DaemonAccessor" />, which finds the coordinator the host
/// registered and never builds a second daemon (hard rule 11).
/// </para>
/// </remarks>
internal sealed partial class EventDataService : IEventDataService
{
    /// <summary>The action names the audit ring records these operations under.</summary>
    internal const string ArchiveStreamAction = "Archive stream";

    /// <summary>
    /// What a write by stream id is told when the stream is not this scope's: the same sentence for a stream that
    /// does not exist and for one that belongs to a tenant the scope does not name, so the refusal reveals neither.
    /// </summary>
    internal const string StreamNotInScopeMessage = "There is no such stream in this scope.";

    /// <summary>What an archive on a conjoined event store is told when no tenant is selected.</summary>
    internal const string ArchiveTenantRequiredMessage =
        "This event store is tenanted per event, and Marten archives a stream as exactly one tenant. Pick the stream's "
        + "tenant in the header and archive it there: with \"All tenants\" selected, Marten would archive the default "
        + "tenant's stream of this id instead.";

    /// <summary>The action name for discarding a dead letter.</summary>
    internal const string DiscardDeadLetterAction = "Discard dead letter";

    /// <summary>The action name for marking an event skipped.</summary>
    internal const string SkipEventAction = "Skip event";

    /// <summary>The action name for rewinding a subscription to a dead letter's event.</summary>
    internal const string RewindSubscriptionAction = "Rewind subscription";

    /// <summary>The most dead letters one page will read, however large a page is asked for.</summary>
    private const int MaxDeadLetterPageSize = 200;

    /// <summary>The most event types the screen will list.</summary>
    private const int MaxEventTypes = 2_000;

    /// <summary>The most streams the Overview's panel will read, however many it asks for.</summary>
    private const int MaxRecentStreams = 50;

    private readonly StudioScopeResolver resolver;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly StudioAuthorization authorization;
    private readonly DaemonAccessor daemons;
    private readonly ColumnCatalog catalog;
    private readonly IndexCatalog indexes;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<EventDataService> logger;
    private readonly DatabaseReachAuthorization reach;
    private readonly DeadLetterGroupCache groups;

    public EventDataService(
        StudioScopeResolver resolver,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        StudioAuthorization authorization,
        DaemonAccessor daemons,
        ColumnCatalog catalog,
        IndexCatalog indexes,
        IOptions<MartenStudioOptions> options,
        StudioLogThrottle throttle,
        ILogger<EventDataService> logger,
        DeadLetterGroupCache groups)
    {
        this.resolver = resolver;
        this.capabilities = capabilities;
        this.audit = audit;
        this.authorization = authorization;
        this.daemons = daemons;
        this.catalog = catalog;
        this.indexes = indexes;
        this.options = options;
        this.throttle = throttle;
        this.logger = logger;
        this.groups = groups;
        reach = new DatabaseReachAuthorization(authorization, logger);
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
                EventsConjoined = resolved.Store.Options.Events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined,
                PerTenantSequences = resolved.Store.Options.Events.UseTenantPartitionedEvents,
                TenantSkipping = resolved.Store.Options.Events.UseTenantPartitionedEvents && TenantSkipAvailable,
                TenantRewind = resolved.Store.Options.Events.UseTenantPartitionedEvents && TenantRewindAvailable,
                HasIsArchived = table.HasIsArchived,
                HasIsSkipped = table.HasIsSkipped,
                HasBinaryData = table.HasBinaryData,
                OptionalColumns = MetadataColumns(table),
                TenantId = scope.TenantId,
                TagStorage = EventTagModel.From(resolved.Store.Options).Storage,
                NaturalKeys = [.. NaturalKeyCatalog.From(resolved.Store.Options, options.Value.IsDocumentTypeVisible).Select(static x => x.Label)],
            };
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventStoreShape.Unavailable(Describe(scope, exception, "describe the event store"));
        }
    }

    /// <inheritdoc />
    public Task<StreamPage> ListStreamsAsync(
        StudioScope scope,
        StreamListRequest request,
        CancellationToken cancellationToken = default) =>
        ReadStreamsAsync(scope, request, ConnectionUsage.Read, cancellationToken);

    /// <summary>The stream list, on a connection of the given usage.</summary>
    /// <param name="scope">The store, database and tenant.</param>
    /// <param name="request">What to list.</param>
    /// <param name="usage">
    /// <see cref="ConnectionUsage.Read" /> for every page, which follows the host's
    /// <c>MultiHostSettings.ReadSessionPreference</c> and so may be a standby;
    /// <see cref="ConnectionUsage.ReadWrite" /> only where a write depends on the answer
    /// (<see cref="ReadStreamOnPrimaryAsync" />).
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async Task<StreamPage> ReadStreamsAsync(
        StudioScope scope,
        StreamListRequest request,
        ConnectionUsage usage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            await using NpgsqlConnection connection = await OpenAsync(resolved, usage, cancellationToken).ConfigureAwait(false);
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
                else
                {
                    // mt_streams."timestamp" is NOT NULL in every schema Marten creates (asserted live by
                    // EventsLiveTests.The_streams_table_timestamp_is_not_nullable_so_the_keyset_can_page_it),
                    // so this branch is unreachable on a store Marten built.
                    // On a table somebody migrated by hand it is reachable, and the honest answer is to
                    // end the walk: a page that says "there is more" while handing back no cursor is a
                    // "Next" link that goes nowhere, which is worse than stopping one page early.
                    hasMore = false;

                    string schema = table.Schema;
                    LogLevel level = throttle.WarningOrDebug("EventDataService.StreamTimestamp", scope.StoreKey, scope.DatabaseId, kind: null);
                    logger.StreamTimestampMissing(level, schema);
                }
            }

            return new StreamPage(rows, next, hasMore);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return StreamPage.Failed(Describe(scope, exception, "list the streams"));
        }
    }

    /// <inheritdoc />
    public Task<StreamState> GetStreamAsync(
        StudioScope scope,
        string streamId,
        CancellationToken cancellationToken = default) =>
        ReadStreamAsync(scope, streamId, ConnectionUsage.Read, cancellationToken);

    /// <summary>
    /// One stream's row, read on the primary - for a write that decides on what it reads.
    /// </summary>
    /// <remarks>
    /// <see cref="GetStreamAsync" /> opens <see cref="ConnectionUsage.Read" />, which Marten maps to
    /// <c>MultiHostSettings.ReadSessionPreference</c>: on a multi-host data source set to
    /// <c>PreferStandby</c> that is a standby, and an asynchronous standby answers with the row as it was. That
    /// is fine for a page and wrong for a compaction, whose checks before the save and whose read-back after it
    /// must see what the primary holds - a stale <c>compacted_version</c> after the save reads as "nothing was
    /// compacted" while the events are already gone. <see cref="ConnectionUsage.ReadWrite" /> maps to
    /// <c>WriteSessionPreference</c>, the host the write itself runs on, which is also what Marten's own write
    /// session reads with (<c>AutoClosingLifetime</c> picks <c>ReadWrite</c> for any session that is not
    /// read-only).
    /// </remarks>
    private Task<StreamState> ReadStreamOnPrimaryAsync(
        StudioScope scope,
        string streamId,
        CancellationToken cancellationToken) =>
        ReadStreamAsync(scope, streamId, ConnectionUsage.ReadWrite, cancellationToken);

    private async Task<StreamState> ReadStreamAsync(
        StudioScope scope,
        string streamId,
        ConnectionUsage usage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(streamId))
        {
            return StreamState.Missing(streamId ?? string.Empty);
        }

        StreamPage page = await ReadStreamsAsync(
            scope,
            new StreamListRequest { StreamId = streamId, IncludeArchived = true, PageSize = 1 },
            usage,
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
            row.TenantId)
        {
            CompactedVersion = row.CompactedVersion,
        };
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

            // Per-tenant sequences and no tenant: one id's streams of two tenants would interleave by their own counters
            // in one timeline, so such a stream asks for a tenant; an id only one tenant has reads as before (SEC1-fix3).
            if (scope.TenantId is null
                && resolved.Store.Options.Events.UseTenantPartitionedEvents
                && table.HasStreamColumn("tenant_id")
                && table.ParseStreamId(streamId ?? string.Empty) is { Success: true } id)
            {
                await using NpgsqlCommand count = StreamReadQueries.BuildStreamTenantCount(table, id.Value!);
                count.Connection = connection;
                count.CommandTimeout = CommandTimeoutSeconds;

                if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 1)
                {
                    return EventPage.Failed(new EventDataError(
                        StreamCompactionRules.ReadNeedsTenant("this stream: more than one tenant has a stream with this id"), null, false)
                    {
                        NeedsTenant = true,
                    });
                }
            }

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

                (List<EventRow> tagged, EventTagLoad tags) = await AttachTagsAsync(resolved, connection, table, rows, cancellationToken)
                    .ConfigureAwait(false);

                return new EventPage(tagged, tagged.Count > 0 ? tagged[^1].Sequence : null, hasMore) { TagLoad = tags };
            }
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventPage.Failed(Describe(scope, exception, "read the stream's events"));
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

            // Per-tenant sequences and no tenant: the feed pages and follows on seq_id, which then orders each tenant's
            // events and nothing across them - two tenants' pages interleave by their own counters, a page's cursor
            // skips the other tenants' events with the same number, and follow mode never sees a tenant whose counter is
            // behind (SEC1-fix3). Nothing correct to page through, so nothing is read, and the page says why.
            if (resolved.TenantId is null && resolved.Store.Options.Events.UseTenantPartitionedEvents)
            {
                return EventPage.Failed(new EventDataError(StreamCompactionRules.ReadNeedsTenant("the feed"), null, false) { NeedsTenant = true });
            }

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table.EventColumns.Count == 0)
            {
                return EventPage.Empty;
            }

            int pageSize = Math.Clamp(request.PageSize, 1, options.Value.MaxPageSize);

            // A tag type this store does not have, or a value its type cannot hold, is an answer rather than
            // an empty page: "no event carries that tag" is a different fact.
            EventTagPredicate? tag = null;
            if (request.Tag is { } filter)
            {
                (tag, string? tagError) = await ResolveTagFilterAsync(resolved, connection, table, filter, cancellationToken)
                    .ConfigureAwait(false);

                if (tagError is not null)
                {
                    return EventPage.Failed(new EventDataError(tagError, null, false));
                }
            }

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
                Tag = tag,
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

                (List<EventRow> delta, EventTagLoad deltaTags) = await AttachTagsAsync(resolved, connection, table, rows, cancellationToken)
                    .ConfigureAwait(false);

                return new EventPage(delta, delta.Count > 0 ? delta[^1].Sequence : null, HasMore: false) { TagLoad = deltaTags };
            }

            bool hasMore = rows.Count > pageSize;
            if (hasMore)
            {
                rows.RemoveRange(pageSize, rows.Count - pageSize);
            }

            (List<EventRow> tagged, EventTagLoad tags) = await AttachTagsAsync(resolved, connection, table, rows, cancellationToken)
                .ConfigureAwait(false);

            return new EventPage(tagged, tagged.Count > 0 ? tagged[^1].Sequence : null, hasMore) { TagLoad = tags };
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventPage.Failed(Describe(scope, exception, "read the event feed"));
        }
    }

    /// <inheritdoc />
    public async Task<long?> GetHighestSequenceAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            // Marten's own statement, run on the studio's own connection - not
            // IMartenDatabase.FetchHighestEventSequenceNumber, which opens with
            // EnsureStorageExistsAsync(typeof(IEvent), token) and would therefore let the feed's follow
            // tick create an event store, on a timer, on a database that has none (hard rule 14, and see
            // ProjectionProgressQueries for where that was verified in Marten 9.35).
            //
            // Be clear about what the number is: the sequence's last_value from <schema>.mt_events_sequence
            // (0 until it has handed one out - a fresh sequence reports 1 with is_called false),
            // or on a store with UseTenantPartitionedEvents `select coalesce(max(seq_id), 0) from
            // mt_events`. So it is neither the daemon's high-water mark nor a count of committed events -
            // a sequence's last_value runs ahead of what is visible, because numbers are handed out
            // before the transaction that took them commits and are burned outright when it rolls back.
            // It is also database-wide and never tenant-scoped.
            //
            // That is exactly the right shape for what the feed uses it for - a cheap "has anything
            // happened" tripwire that is allowed to be optimistic, since the delta read that follows is
            // the tenant-scoped, page-bounded one (EventQueryBuilder.BuildFeed). It would be the wrong
            // number to render as "this tenant has N events". A database with no event store answers
            // null, which is the same "nothing to follow" the caller already handles.
            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);

            return await ProjectionProgressQueries
                .ReadHighWaterMarkAsync(
                    connection,
                    catalog,
                    resolved.Store.Options.Events.DatabaseSchemaName,
                    resolved.Store.Options.Events.UseTenantPartitionedEvents,
                    options.Value.QueryTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            logger.LogDebug(exception, "Marten Studio could not read the highest event sequence");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<EventStoreCounts> GetEventStoreCountsAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table.EventColumns.Count == 0 || table.StreamColumns.Count == 0)
            {
                return EventStoreCounts.NoEventStorage;
            }

            // Estimates first, always (D8): these numbers feed the Overview's tiles and the nav badge's
            // gate, both of which are read on every interval, and an exact count of mt_events is exactly
            // the sequential scan a dashboard must never run on a timer.
            //
            // The one case that is paid for is the one where there is no estimate at all. reltuples is -1
            // until the first ANALYZE, which is the state of every freshly seeded store - the D18
            // zero-config sample included - so estimating alone made the tiles read "unknown" over an
            // event store that plainly has events in it, until autovacuum next ran. EstimateOrCountAsync
            // upgrades that case and only that case, behind both of the bounds the documents rail uses:
            // relpages first, then the offset/limit row probe against ExactCountThreshold. A store big
            // enough for the count to matter is a store that fails one of them.
            var estimator = new CountEstimator
            {
                CommandTimeoutSeconds = CommandTimeoutSeconds,
                ExactCountThreshold = Math.Max(options.Value.ExactCountThreshold, 0),
            };

            DocumentCount streams = await estimator
                .EstimateOrCountAsync(connection, table.Schema, EventTableInfo.StreamsTable, cancellationToken)
                .ConfigureAwait(false);
            DocumentCount events = await estimator
                .EstimateOrCountAsync(connection, table.Schema, EventTableInfo.EventsTable, cancellationToken)
                .ConfigureAwait(false);

            return new EventStoreCounts(Convert(streams), Convert(events), TablesExist: true, Error: null);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventStoreCounts.Failed(Describe(scope, exception, "count the event store"));
        }

        // The last arm is the never-analysed store that was small enough to count: an exact number, drawn
        // without the tilde. IsUnavailable covers Unknown - which is a kind of it - and both refusals,
        // whose value is Unknown too.
        static EventStoreCount Convert(DocumentCount count) => count switch
        {
            { IsUnavailable: true } => EventStoreCount.Unknown,
            { IsEstimate: true } => EventStoreCount.Estimate(count.Value),
            _ => EventStoreCount.Exact(count.Value),
        };
    }

    /// <inheritdoc />
    public async Task<RecentStreams> GetRecentStreamsAsync(
        StudioScope scope,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            // Read off the store's own configuration, not guessed: it is the fact that decides whether
            // mt_streams.timestamp means "created" or "last appended to", and therefore what the panel
            // above these rows may honestly be called.
            EventAppendMode appendMode = resolved.Store.Options.Events.AppendMode;

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table.StreamColumns.Count == 0)
            {
                return new RecentStreams([], appendMode, null);
            }

            await using NpgsqlCommand command = EventQueryBuilder.BuildRecentlyActiveStreams(
                table,
                Math.Clamp(take, 1, MaxRecentStreams),
                resolved.TenantId,
                options.Value.QueryTimeout);

            command.Connection = connection;

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

            return new RecentStreams(rows, appendMode, null);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return RecentStreams.Failed(Describe(scope, exception, "read the newest streams"));
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

            // The tenant is part of the question, not decoration: on a conjoined event store the counts
            // of "how many OrderPlaced are there" differ per tenant, and a screen scoped to one tenant
            // that showed everybody's totals would be reporting another tenant's business volume.
            await using NpgsqlCommand command = EventQueryBuilder.BuildEventTypeCounts(table, resolved.TenantId);
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            Dictionary<string, EventTypeInfo> merged = new(registered, StringComparer.Ordinal);
            bool truncated = false;

            await using (NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (merged.Count >= MaxEventTypes)
                    {
                        // The row that did not fit is the proof there were more, and the screen has to
                        // say so: a silently capped list is indistinguishable from a complete one.
                        truncated = true;
                        break;
                    }

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
            // flagging rather than an unknown - unless the group-by was cut short, in which case "no
            // events" would be a guess.
            if (!truncated)
            {
                foreach (string name in registered.Keys)
                {
                    if (merged[name].Count is null)
                    {
                        merged[name] = merged[name] with { Count = 0 };
                    }
                }
            }

            // Under per-tenant sequences with no tenant selected, min(seq_id) and max(seq_id) are taken across counters that
            // each tenant runs on its own, and name no event: the page labels them rather than show them (SEC1-fix4).
            return new EventTypeList(Order(merged.Values), CountsLoaded: true, IsTruncated: truncated)
            {
                SequencesPerTenant = resolved.TenantId is null && resolved.Store.Options.Events.UseTenantPartitionedEvents,
            };
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventTypeList.Failed(Describe(scope, exception, "list the event types"));
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

            return AggregateStreamInvoker.Candidates(resolved.Store.Options, options.Value.IsDocumentTypeVisible);
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
            // The same filtered list the picker offered: a hidden type's name typed into a request is refused
            // as "not one of this store's aggregate types", the answer for a type the store does not have.
            foreach (AggregateTypeCandidate known in AggregateStreamInvoker.Candidates(
                         resolved.Store.Options, options.Value.IsDocumentTypeVisible))
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

            // Below Marten 9.51 EventStatement emits its stream predicate only for a non-empty id (#5695 fixed it), so
            // a replay through Marten's stream fetch would fold every event of the tenant or the store into one
            // aggregate. The studio's replay no longer goes through it - ReadReplayEventsAsync reads with
            // StreamReadQueries, whose predicate is unconditional - so this guard no longer prevents a whole-log read at
            // either end; it is kept so that a stream Marten cannot address is refused alike by every page that would
            // hand it to Marten, as compaction refuses it (StreamCompactionRules.IsWholeLogId), and the message says
            // why in Marten's terms rather than the studio's SQL's.
            if (StreamCompactionRules.IsWholeLogId(parsed.Value))
            {
                return AggregateSnapshot.Failed(
                    candidate.Name,
                    version,
                    new EventDataError(
                        "This stream's id is the empty GUID, which Marten's stream reads below 9.51 take as no stream filter at all, "
                        + "so a replay through them would fold every event in the log. The studio does not replay it.",
                        null,
                        false));
            }

            await using IQuerySession session = OpenQuerySession(resolved);

            // The events with the studio's own SQL and Marten's own selector, never session.Events: see
            // ReadReplayEventsAsync. The session runs no statement of its own; the aggregator is handed it.
            ReplayEvents replay = await ReadReplayEventsAsync(resolved, session, parsed.Value!, version, cancellationToken)
                .ConfigureAwait(false);

            if (replay.Refusal is { } refusal)
            {
                return AggregateSnapshot.Failed(candidate.Name, version, refusal);
            }

            // Not a stream of this scope: the answer a stream that does not exist gets, word for word, so a guessed
            // id of another tenant's stream learns nothing - and nothing of it was read.
            if (replay.NotInScope)
            {
                return AggregateSnapshot.NotFound(candidate.Name, version, AggregateMissingReason.StreamMissing);
            }

            object? aggregate = await AggregateStreamInvoker
                .AggregateAtVersionAsync(session, candidate.Type, parsed.Value!, replay.Events, version, cancellationToken)
                .ConfigureAwait(false);

            if (aggregate is null)
            {
                // A conjoined event store replayed with no tenant in scope reads no events at all: the
                // session's tenant is Marten's *DEFAULT* and EventStatement filters on it. That is not
                // "the aggregate produced nothing", and it is the one case worth catching before paying
                // for the stream read below.
                if (resolved.TenantId is null
                    && resolved.Store.Options.Events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
                {
                    return AggregateSnapshot.NotFound(
                        candidate.Name, version, AggregateMissingReason.TenantRequired);
                }

                return await DescribeMissingAggregateAsync(
                    scope, candidate.Name, streamId ?? string.Empty, version, cancellationToken).ConfigureAwait(false);
            }

            // The store's own serializer, never a JsonSerializer of ours (AGENTS.md hard rule 10): an
            // aggregate rendered through different settings than Marten writes with is a lie about the
            // document that projection would store.
            string json = resolved.Store.Options.Serializer().ToJson(aggregate);

            return new AggregateSnapshot(candidate.Name, version, json, Found: true);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return AggregateSnapshot.Failed(typeName, version, Describe(scope, exception, "replay the stream"));
        }
    }

    /// <summary>
    /// Which of the four "the replay produced nothing" answers this one is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only reached on the null path, so the extra <c>mt_streams</c> read is paid by the replays that
    /// produced nothing and never by the ones that worked. It answers the two questions a visitor actually
    /// has when the panel comes back empty: <em>is this stream archived</em> — Marten's stream fetch
    /// always applies <c>IsNotArchivedFilter</c>, so an archived stream replays into nothing however the
    /// aggregate is written — and <em>did I ask for a version this stream never reached</em>, which Marten
    /// answers with the same <see langword="null" /> as a deleted aggregate.
    /// </para>
    /// <para>
    /// It goes back through <see cref="GetStreamAsync" />, so the read is the scope's own: on a conjoined
    /// store a stream that belongs to another tenant reads as missing rather than as archived, which is
    /// the right answer to give.
    /// </para>
    /// </remarks>
    private async Task<AggregateSnapshot> DescribeMissingAggregateAsync(
        StudioScope scope,
        string typeName,
        string streamId,
        long version,
        CancellationToken cancellationToken)
    {
        StreamState state = await GetStreamAsync(scope, streamId, cancellationToken).ConfigureAwait(false);

        if (state.Error is not null || !state.Exists)
        {
            return AggregateSnapshot.NotFound(typeName, version, AggregateMissingReason.StreamMissing);
        }

        if (state.IsArchived)
        {
            return AggregateSnapshot.NotFound(
                typeName, version, AggregateMissingReason.StreamArchived, state.Version);
        }

        if (version > 0 && version > state.Version)
        {
            return AggregateSnapshot.NotFound(
                typeName, version, AggregateMissingReason.VersionPastEnd, state.Version);
        }

        return AggregateSnapshot.NotFound(typeName, version, AggregateMissingReason.NoAggregate, state.Version);
    }

    /// <summary>
    /// One stream's events for the replay, read the way <c>QueryEventStore.FetchStreamAsync</c> reads them - minus the
    /// <c>EnsureStorageExistsAsync(typeof(IEvent))</c> it opens with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> That ensure is Weasel's <c>ensureStorageExistsAsync</c> under the database's own <c>AutoCreate</c>
    /// (read at Marten <c>V9.39.0</c> and <c>V9.50.2</c>, Weasel <c>9.33.0</c>): the event store's dependent features,
    /// <c>AllObjects()</c> for post-processing - <c>mt_hilo</c> on a store with a HiLo id - and the event store's own
    /// migration, which under <c>CreateOrUpdate</c> is not additive. The first replay in a process that had not touched
    /// the event store ran all of it because somebody pressed a version step (AGENTS.md hard rule 14).
    /// </para>
    /// <para>
    /// <b>What is still Marten's.</b> The select list and the reading of each row: <c>IMartenSession.EventStorage()</c>
    /// is the very storage <c>FetchStreamAsync</c> resolves rows through - public, and it runs nothing - so upcasters,
    /// binary payloads and every configured metadata column are read exactly as Marten reads them.
    /// </para>
    /// <para>
    /// <b>The stream is proved to be this scope's before one event of it is read</b> (<see cref="StreamInScopeAsync" />),
    /// and the events are narrowed to the scope's tenant exactly as the timeline narrows them
    /// (<c>EventQueryBuilder.TryBuildStreamEvents</c>) - <em>whatever the tenancy style</em>. Marten writes the session's
    /// tenant into <c>mt_streams.tenant_id</c> and <c>mt_events.tenant_id</c> on every append, on a single-tenanted event
    /// store too (<c>EventGraph.Actions</c>, <c>StreamsTable</c>, the quick-append function; read at <c>V9.39.0</c> and
    /// <c>V9.50.2</c>), so a store whose documents are conjoined and whose events are not still records whose stream each
    /// one is; and Marten's own <c>EventStatement</c> narrows only a conjoined store. Replaying by id alone there handed
    /// an <c>acme</c> visitor the aggregate of any <c>globex</c> stream whose id they could guess - read by the studio,
    /// for a scope that may not see it. On a conjoined store the tenant is still the session's own, as
    /// <c>EventStatement</c> binds it; with no tenant in scope that is Marten's <c>*DEFAULT*</c>, which is why such a
    /// replay still reads nothing and the caller still says a tenant is needed. On any other store with a tenant column
    /// it is the scope's, and with no tenant in scope nothing narrows, as nothing narrows the timeline. From any tenant's
    /// scope but the stream's owner's the stream is out of scope and nothing is folded at all; from the owner's, a stream
    /// some of whose events another tenant's session appended folds only the owner's events - which is what the timeline
    /// beside the panel shows, rather than what Marten folds, the one difference from Marten's aggregate this read
    /// allows, and the panel says so (SEC1-fix F2).
    /// </para>
    /// <para>
    /// An event store that is not there is no events, read as a stream that does not exist - never created to find
    /// out. A column the configuration reads and the table lacks is a pending migration Marten would have run here;
    /// the replay says so instead, and names the screen that applies it.
    /// </para>
    /// </remarks>
    private async Task<ReplayEvents> ReadReplayEventsAsync(
        ResolvedScope resolved,
        IQuerySession session,
        object streamId,
        long version,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
        EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

        if (table.EventColumns.Count == 0)
        {
            return ReplayEvents.None;
        }

        if (!await StreamInScopeAsync(resolved, connection, table, streamId, cancellationToken).ConfigureAwait(false))
        {
            return ReplayEvents.OutOfScope;
        }

        Marten.Events.IEventStorage storage = ((Marten.Internal.IMartenSession) session).EventStorage();

        var query = new ReplayEventsQuery
        {
            SelectFields = storage.SelectFields(),
            StreamId = streamId,
            Version = version,
            TenantId = resolved.Store.Options.Events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
                ? session.TenantId
                : table.HasTenantId ? resolved.TenantId : null,
            IgnoreSkipped = resolved.Store.Options.Events.EnableEventSkippingInProjectionsOrSubscriptions,
        };

        IReadOnlyList<string> missing = StreamReadQueries.MissingColumns(table, query);
        if (missing.Count > 0)
        {
            return new ReplayEvents([], new EventDataError(
                $"{table.Schema}.{EventTableInfo.EventsTable} has no {(missing.Count == 1 ? "column" : "columns")} "
                + $"{string.Join(", ", missing)}, which this store's configuration reads. Marten would add "
                + $"{(missing.Count == 1 ? "it" : "them")} by migrating the event store on its first event read; the studio "
                + "does not migrate a table to read it. Apply the pending change from the Schema screen, then replay again.",
                null,
                false));
        }

        await using NpgsqlCommand command = StreamReadQueries.BuildReplayEvents(table, query);
        command.Connection = connection;
        command.CommandTimeout = CommandTimeoutSeconds;

        Weasel.Storage.ISelector<IEvent> selector = storage;
        List<IEvent> events = [];

        await using (NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                events.Add(await selector.ResolveAsync(reader, cancellationToken).ConfigureAwait(false));
            }
        }

        return new ReplayEvents(events, null);
    }

    /// <summary>
    /// For a write by stream id from a tenant's scope on a store whose events are not conjoined: the database as a whole is
    /// asked as well (<see cref="DatabaseReachAuthorization.RequireWholeDatabaseAsync" />, D27 "reach, not scope") whenever
    /// an event of the stream was appended under another tenant - because Marten archives and compacts such a stream by id,
    /// every event of it, and the tenant in scope narrows nothing it does (SEC1-fix F1).
    /// </summary>
    /// <remarks>
    /// Run after the stream has been proved to be the scope's, on the primary as that read is. A refusal is a
    /// <see cref="StudioNotAuthorizedException" /> against <c>(store, database, null)</c>, which <c>MutateAsync</c> audits
    /// as the scope denial it is. A conjoined event store, a scope with no tenant and a table with no <c>tenant_id</c> ask
    /// nothing more: there the write is either narrowed by Marten or already authorized for every tenant.
    /// <para>
    /// <b>A residual window, for Archive.</b> Archive is by stream id with no version bound, and on a store whose events
    /// are not conjoined Marten's <c>mt_archive_stream</c> has no tenant predicate either. So an event another tenant's
    /// session appends between this check and the archive's <c>SaveChangesAsync</c> is archived with the rest. Only
    /// <c>EventAppendMode.Rich</c> can append one: the quick-append function refuses a session whose tenant does not
    /// match the stream's row. The way to close it is to take the stream's <c>mt_streams</c> row <c>for update</c> on the
    /// write session's own connection, run this check under that lock, and archive in the same transaction - not done
    /// here. Compaction has no such window past its cutoff: Marten folds only through the version the dialog described.
    /// </para>
    /// </remarks>
    private async Task RequireReachOverOtherTenantsEventsAsync(
        ResolvedScope resolved,
        object streamId,
        StudioCapability capability,
        CancellationToken cancellationToken)
    {
        if (resolved.TenantId is not { } tenant
            || resolved.Store.Options.Events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            return;
        }

        bool othersEvents;

        await using (NpgsqlConnection connection = await OpenAsync(resolved, ConnectionUsage.ReadWrite, cancellationToken).ConfigureAwait(false))
        {
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (!table.HasTenantId)
            {
                return;
            }

            await using NpgsqlCommand command = StreamReadQueries.BuildStreamHasOtherTenantsEvents(table, streamId, tenant);
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            othersEvents = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }

        if (othersEvents)
        {
            await reach.RequireWholeDatabaseAsync(resolved, resolved.Database, capability, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether <paramref name="streamId" /> is a stream of this scope, archived or not: one row of <c>mt_streams</c>,
    /// narrowed to the scope's tenant whenever the table carries <c>tenant_id</c>, whatever the tenancy style
    /// (<see cref="StreamReadQueries.BuildStreamExists" />, the predicate the stream list and the document's stream
    /// link use). The proof a read by id makes before it reads anything of the stream.
    /// </summary>
    /// <remarks>
    /// No <c>mt_streams</c> to prove it against, beside an <c>mt_events</c> that is there, is a table somebody dropped
    /// or never created - and it fails closed, as a stream that is not this scope's.
    /// </remarks>
    private async Task<bool> StreamInScopeAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        EventTableInfo table,
        object streamId,
        CancellationToken cancellationToken)
    {
        if (table.StreamColumns.Count == 0)
        {
            return false;
        }

        await using NpgsqlCommand command = StreamReadQueries.BuildStreamExists(table, streamId, resolved.TenantId);
        command.Connection = connection;
        command.CommandTimeout = CommandTimeoutSeconds;

        object? found = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return found is not null and not DBNull;
    }

    /// <summary>What the replay read: the events, or why it could not read them.</summary>
    /// <param name="Events">The stream's events in sequence order.</param>
    /// <param name="Refusal">Why nothing was read, when the table cannot be read the way Marten would read it.</param>
    /// <param name="NotInScope">The stream is not one of this scope's, so nothing of it was read.</param>
    private sealed record ReplayEvents(IReadOnlyList<IEvent> Events, EventDataError? Refusal, bool NotInScope = false)
    {
        /// <summary>No event store in this database, so no events.</summary>
        public static ReplayEvents None { get; } = new([], null);

        /// <summary>No stream of that id in this scope - missing, or another tenant's.</summary>
        public static ReplayEvents OutOfScope { get; } = new([], null, NotInScope: true);
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

            // A host that hides DeadLetterEvent hides this list, before anything is read (D27: a hidden document type is
            // absent). The page says DeadLetterVisibility.HiddenMessage, which names no alias and no table.
            if (!DeadLettersVisible(scope))
            {
                return DeadLetterPage.Hidden;
            }

            int pageSize = Math.Clamp(query.PageSize, 1, MaxDeadLetterPageSize);

            // The studio's own SQL, never session.Query<DeadLetterEvent>(): every Marten LINQ terminal opens with
            // EnsureStorageExistsAsync for the document type (read at Marten V9.39.0 and V9.50.2), which under the
            // database's own AutoCreate creates the table, runs AllObjects() - mt_hilo on a store with a HiLo id - and
            // applies the table's migration, the first time the process asks (hard rule 14). The catalog probe this
            // used to make first kept it from creating the table; it could not keep it from the rest. See
            // DeadLetterQueries for what of the LINQ query is kept, and how.
            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);

            DocumentTableInfo? table = await DescribeDeadLettersAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (table is null)
            {
                // No table means the daemon never recorded one, which is an empty page rather than a failure - and a
                // store with no async projections never creates it at all. The studio never does.
                return DeadLetterPage.Empty;
            }

            // Marten registers DeadLetterEvent SingleTenanted() unconditionally, so the table has no tenant_id column
            // to filter on. What it does have is the document's own TenantId *property*, which the daemon fills in with
            // the tenant the failing event belonged to - so the tenant axis is a predicate over the body, and a scope
            // pinned to one tenant sees only its own failures (hard rule 15).
            var page = new DeadLetterPageQuery
            {
                TenantId = resolved.TenantId,
                ProjectionName = string.IsNullOrWhiteSpace(query.ProjectionName) ? null : query.ProjectionName,
                ShardName = string.IsNullOrWhiteSpace(query.ShardName) ? null : query.ShardName,
                ExceptionType = string.IsNullOrWhiteSpace(query.ExceptionType) ? null : query.ExceptionType,
                EventSequences = query.EventSequence is { } sequence ? [sequence] : null,
                Offset = Math.Max(0, query.Offset),

                // One more than asked for, so "is there another page" is answered without a count.
                Limit = pageSize + 1,

                // A group opened from the grouped view: its letters, found by the grouped view's own predicates (M3), among
                // the newest letters the grouped view groups (SEC-FIX) - so a ?group= written by hand fingerprints no more.
                Group = query.Group,
                Events = query.Group is null
                    ? null
                    : await DescribeEventSourceAsync(resolved, connection, cancellationToken).ConfigureAwait(false),
                GroupWindow = groups.LettersGrouped,
            };

            DeadLetterJsonKeys keys = DeadLetterJsonKeys.For(
                MartenStudio.Services.Documents.DocumentDataService.NamingPolicyFor(resolved.Store));

            await using NpgsqlCommand command = DeadLetterQueries.BuildPage(table, keys, page);
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            // The store's own serializer, never a JsonSerializer of ours (hard rule 10): the body is read back with the
            // settings Marten wrote it with. The id is the row's own column, which is what Discard deletes by.
            ISerializer serializer = resolved.Store.Options.Serializer();
            List<DeadLetterRow> rows = [];

            await using (NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    Guid id = reader.GetGuid(0);
                    DeadLetterEvent letter = await serializer
                        .FromJsonAsync<DeadLetterEvent>(reader, 1, cancellationToken)
                        .ConfigureAwait(false);

                    rows.Add(new DeadLetterRow(
                        id,
                        letter.ProjectionName ?? string.Empty,
                        letter.ShardName ?? string.Empty,
                        letter.Timestamp,
                        letter.ExceptionType ?? string.Empty,
                        letter.ExceptionMessage ?? string.Empty,
                        letter.EventSequence,
                        letter.TenantId ?? string.Empty));
                }
            }

            bool hasMore = rows.Count > pageSize;
            if (hasMore)
            {
                rows.RemoveRange(pageSize, rows.Count - pageSize);
            }

            // Whether the window cut - a count that stops one past it, with no order and no fingerprint - so the page can say
            // its letters are the newest ones', as the grouped view says of its counts.
            bool lettersCut = page.Group is not null
                && await CountLettersUpToAsync(table, keys, resolved.TenantId, page.GroupWindow, connection, cancellationToken).ConfigureAwait(false)
                    > page.GroupWindow;

            return new DeadLetterPage(rows, hasMore) { LettersCut = lettersCut };
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return DeadLetterPage.Failed(Describe(scope, exception, "list the dead letters"));
        }
    }

    /// <summary>
    /// The dead-letter table as this database actually has it, asked of the catalog - or <see langword="null" /> when
    /// it is not there.
    /// </summary>
    /// <remarks>
    /// One rule for both dead-letter reads. The table is created by Marten's own registration of
    /// <c>DeadLetterEvent</c> and only when something applies it, so a database with an event store need not have
    /// it — and a read that assumes otherwise is a read that hands Marten the chance to create it (AGENTS.md hard
    /// rule 14). Its name comes from the mapping when Marten has one (<see cref="DeadLetterQueries.TableFor" />), and
    /// the lookup goes through <see cref="ColumnCatalog"/>, which is shared and expiring, so the second caller inside
    /// the window pays nothing.
    /// </remarks>
    /// <param name="resolved">The scope, already resolved — which is where the store policy was applied.</param>
    /// <param name="connection">An open connection to that scope's database.</param>
    /// <param name="cancellationToken">The usual.</param>
    private async Task<DocumentTableInfo?> DescribeDeadLettersAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        DocumentTableInfo table = DeadLetterQueries.TableFor(resolved.Store.Options);

        TableColumns physical = await catalog
            .GetAsync(connection, table.Schema, table.Table, cancellationToken)
            .ConfigureAwait(false);

        return physical.Exists ? table.WithPhysicalColumns(physical) : null;
    }

    /// <summary>
    /// Whether the host lets the studio show <c>DeadLetterEvent</c> (<see cref="MartenStudioOptions.IsDocumentTypeVisible" />),
    /// failing closed: a delegate that throws hides it, and says so in the log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One gate for the dead-letter screen's four paths</b> - the list, the badge's count, Discard and Rewind - so a host
    /// that hides the type, as D27 says a hidden type is hidden, hides it here as well: the list is
    /// <see cref="DeadLetterPage.Hidden" />, the count is no number, and the two writes are refused with
    /// <see cref="DeadLetterVisibility.HiddenMessage" />. The navigation entry, the Overview tile and the advisor's D06 ask
    /// the same question (<see cref="DeadLetterVisibility" />).
    /// </para>
    /// <para>
    /// A throwing delegate is the host's fault and is reported once a window per store and database as event 9217, Debug in
    /// between, because the badge asks on every poll. Nothing here asks the database anything.
    /// </para>
    /// </remarks>
    private bool DeadLettersVisible(StudioScope scope)
    {
        if (DeadLetterVisibility.IsVisible(options.Value.IsDocumentTypeVisible, out Exception? failure))
        {
            return true;
        }

        if (failure is not null)
        {
            LogReadFailure(scope, failure, DeadLetterVisibilityRead, sqlState: null);
        }

        return false;
    }

    /// <summary>What the log says could not be done when the visibility delegate throws for <c>DeadLetterEvent</c>.</summary>
    internal const string DeadLetterVisibilityRead =
        "ask MartenStudioOptions.IsDocumentTypeVisible about DeadLetterEvent, so the dead letters are treated as hidden,";

    /// <summary>
    /// Refuses a dead-letter write when the host hides <c>DeadLetterEvent</c>, with the page's own sentence
    /// (<see cref="DeadLetterVisibility.HiddenMessage" />) - never the type's alias, which a host that re-aliased the type
    /// it hides would not want printed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The type is hidden; <c>MutateAsync</c> audits it as a refused action.</exception>
    private void RequireDeadLettersVisible(ResolvedScope resolved)
    {
        if (!DeadLettersVisible(resolved.Scope))
        {
            throw new InvalidOperationException(DeadLetterVisibility.HiddenMessage);
        }
    }

    /// <inheritdoc />
    public async Task<long?> CountDeadLettersAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            // A hidden DeadLetterEvent has no number on the badge, as a hidden collection has none on the rail.
            if (!DeadLettersVisible(scope))
            {
                return null;
            }

            // Marten registers DeadLetterEvent into the *event* store's schema, single-tenanted
            // (StoreOptions.ApplyConfiguration: Schema.For<DeadLetterEvent>().DatabaseSchemaName(
            // Events.DatabaseSchemaName).SingleTenanted()), which is why the count is scoped by the event
            // schema rather than by the document schema - and why it cannot be scoped by tenant:
            // mt_doc_deadletterevent has no tenant_id column on any store, and the only tenant a row
            // carries is a property inside its JSON body.
            //
            // So a tenant-pinned visitor gets no number at all. The list below them narrows on that
            // property and shows their own failures; a whole-database count over the badge would be
            // other tenants' failures under their own page's heading - the same two-questions-under-one-
            // number disclosure the documents rail closed with CrossTenantEstimateNote, and a count they
            // could not act on. Counting their own would mean data->>'TenantId' with no index behind it,
            // on a badge that polls: not a trade a nav indicator gets to make (D8).
            if (resolved.TenantId is not null)
            {
                return null;
            }

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);

            if (await DescribeDeadLettersAsync(resolved, connection, cancellationToken).ConfigureAwait(false) is not { } table)
            {
                // No table means the daemon never recorded one, which is a real zero rather than a
                // failure - and a store with no async projections never creates it at all.
                return 0;
            }

            // The table the list reads, settled the same way and filtered by the same soft-delete predicate, so the badge
            // counts exactly the rows the page it links to shows.
            await using NpgsqlCommand command = EventQueryBuilder.BuildDeadLetterCount(table);
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

            // A dead letter names its event by the *global* sequence, and the dead-letter table is
            // single-tenanted - so the sequence handed to this method may belong to a tenant the scope is
            // not about. The tenant predicate is what turns that into "no such event here".
            await using NpgsqlCommand command =
                EventQueryBuilder.BuildEventBySequence(table, sequence, resolved.TenantId);
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

                // One tenant or none, as compaction's NeedsTenant: a conjoined event store's session always runs as
                // exactly one tenant, and with "All tenants" selected that is Marten's *DEFAULT* - so mt_archive_stream,
                // which takes the session's tenant, archived *DEFAULT*'s stream of this id, or nothing, and the studio
                // reported "archived" either way. Refused before anything is read, and audited as failed.
                if (resolved.TenantId is null
                    && resolved.Store.Options.Events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined)
                {
                    throw new InvalidOperationException(ArchiveTenantRequiredMessage);
                }

                // The stream, proved to be this scope's before it is archived - read on the primary, as compaction's
                // check is, because the write depends on the answer. Marten's mt_archive_stream narrows by tenant only
                // on a conjoined event store (ArchiveStreamFunction, read at V9.39.0 and V9.50.2), while every append
                // records its session's tenant in mt_streams on any store; so on a store with single-tenanted events
                // and tenanted sessions, archiving by id alone reached any tenant's stream whose id was typed - since
                // 0.3. This read is narrowed to the scope's tenant whenever mt_streams carries the column, and a stream
                // it does not find is refused with the sentence a stream that does not exist gets, so the refusal says
                // nothing about whether it exists elsewhere. Refused before the session, and audited as failed (hard
                // rule 5): nothing was written.
                StreamState stream = await ReadStreamOnPrimaryAsync(resolved.Scope, target, token).ConfigureAwait(false);

                if (stream.Error is { } error)
                {
                    throw new InvalidOperationException(error.Display);
                }

                if (!stream.Exists)
                {
                    throw new InvalidOperationException(StreamNotInScopeMessage);
                }

                await RequireReachOverOtherTenantsEventsAsync(resolved, parsed.Value!, StudioCapability.ArchiveStreams, token)
                    .ConfigureAwait(false);

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
                // Before the session: a hidden type is refused, audited as refused, and nothing of it is read.
                RequireDeadLettersVisible(resolved);

                await using IDocumentSession session = OpenWriteSession(resolved);

                IQueryable<DeadLetterEvent> queryable = session.Query<DeadLetterEvent>().Where(x => x.Id == id);

                // Same reason as the list: DeadLetterEvent is SingleTenanted(), so the session's tenant
                // filters nothing and a guessed id would otherwise delete another tenant's record. The
                // predicate is over the document's own TenantId property.
                if (resolved.TenantId is { } tenant)
                {
                    queryable = queryable.Where(x => x.TenantId == tenant);
                }

                DeadLetterEvent? letter = await queryable
                    .FirstOrDefaultAsync(token)
                    .ConfigureAwait(false);

                if (letter is null)
                {
                    // Nothing in scope under that id, which is two different facts, and they are answered
                    // differently on purpose. Somebody else discarding the same row first is routine and is
                    // not a refusal: "already gone". Asking for a row that belongs to another tenant is, and is
                    // refused and audited as a scope denial (9203). So the two can be told apart by whoever
                    // asks - a visitor learns that a dead letter of that id exists in another tenant, though
                    // never whose or what it holds: the refusal names no tenant and nothing of the row (D5).
                    // The unscoped re-read costs one query on the miss path only.
                    if (resolved.TenantId is not null
                        && await session.Query<DeadLetterEvent>()
                            .AnyAsync(x => x.Id == id, token)
                            .ConfigureAwait(false))
                    {
                        throw new StudioNotAuthorizedException(resolved.Scope);
                    }

                    return "already gone";
                }

                // The non-generic write API: the studio holds documents as object and re-implementing
                // Marten's write path is how a UI corrupts a store (D6).
                session.DeleteObjects([letter]);
                await session.SaveChangesAsync(token).ConfigureAwait(false);
                groups.InvalidateDatabase(resolved);

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
                // First, before anything is read: below Marten 9.51 MarkEventsAsSkipped is `update mt_events set
                // is_skipped = TRUE where seq_id = ANY(:sequences)` with no tenant predicate (MartenDatabase.EventStorage.cs,
                // read at V9.39.0 and V9.50.2), and under UseTenantPartitionedEvents every tenant numbers seq_id on its own -
                // so skipping acme's #42 skipped every tenant's #42, whatever the proof below found (SEC1-fix4). Refused, and
                // audited as failed; the page disables the button with the same sentence. From 9.51 (#5698) Marten has a
                // tenant overload that adds `and tenant_id = :tenant_id` on a conjoined event store - which
                // UseTenantPartitionedEvents requires - so there the skip is one tenant's, and needs one (the page pins
                // the dead letter's own). The reach is that tenant's events and nothing else, so the scope the write policy
                // was asked about, (store, database, tenant), is the whole of it (SEC1-fix4's rule, kept).
                RequireSkippableNumbering(resolved);

                long[] sequences = [sequence];

                await using (NpgsqlConnection connection = await OpenAsync(resolved, token).ConfigureAwait(false))
                {
                    EventTableInfo table = await DescribeTablesAsync(resolved, connection, token).ConfigureAwait(false);

                    RequireSkipColumn(table);

                    await ProveSequencesInScopeAsync(resolved, connection, table, sequences, token).ConfigureAwait(false);
                }

                await CommitSkipAsync(resolved, sequences).ConfigureAwait(false);

                return resolved.Store.Options.Events.UseTenantPartitionedEvents
                    ? "marked as skipped in tenant " + resolved.TenantId
                    : "marked as skipped";
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a skip on a store with no <c>is_skipped</c> column is refused with.</summary>
    internal const string NoSkipColumnMessage =
        "This store does not record skipped events: mt_events has no is_skipped column, " +
        "which means the host did not enable event skipping in projections or subscriptions. " +
        "Marking an event as skipped would have nothing to write to.";

    /// <summary>
    /// The numbering check both skips make first, before anything is read: under <c>UseTenantPartitionedEvents</c> a skip
    /// needs Marten's tenant overload (9.51) and a tenant to hand it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The store numbers events per tenant and this Marten, or this scope, cannot skip one tenant's.</exception>
    private static void RequireSkippableNumbering(ResolvedScope resolved)
    {
        if (!resolved.Store.Options.Events.UseTenantPartitionedEvents)
        {
            return;
        }

        if (!TenantSkipAvailable)
        {
            throw new InvalidOperationException(StreamCompactionRules.SkipNeedsOneSequence);
        }

        if (resolved.TenantId is null)
        {
            throw new InvalidOperationException(StreamCompactionRules.SkipNeedsTenant);
        }
    }

    /// <summary>The column check both skips make: no <c>is_skipped</c>, nothing to write to.</summary>
    private static void RequireSkipColumn(EventTableInfo table)
    {
        if (!table.HasIsSkipped)
        {
            throw new InvalidOperationException(NoSkipColumnMessage);
        }
    }

    /// <summary>
    /// The tenant proof both skips make for every sequence before it is handed over (hard rule 15): each has to be an event
    /// this scope can see, or the whole skip is refused as a scope denial that says nothing about whose event it is (D5).
    /// </summary>
    /// <remarks>
    /// <c>IMartenDatabase.MarkEventsAsSkipped</c> writes <c>update mt_events set is_skipped = true where seq_id =
    /// any(...)</c> with no tenant predicate below 9.51 - and from 9.51 its tenant overload narrows only a conjoined store -
    /// so on a store whose events carry <c>tenant_id</c> a sequence, which a dead letter hands over as a number, has to be
    /// proved to be this scope's before the write. The statement is <see cref="EventQueryBuilder.BuildSequencesInScope" />,
    /// the dead-letter expansion's own predicate over every sequence at once. With no tenant in the scope there is nothing
    /// to prove: the write policy was asked about every tenant.
    /// </remarks>
    private async Task ProveSequencesInScopeAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        EventTableInfo table,
        long[] sequences,
        CancellationToken token)
    {
        if (!table.HasTenantId || resolved.TenantId is null || sequences.Length == 0)
        {
            return;
        }

        await using NpgsqlCommand proof = EventQueryBuilder.BuildSequencesInScope(table, sequences, resolved.TenantId);
        proof.Connection = connection;
        proof.CommandTimeout = CommandTimeoutSeconds;

        HashSet<long> found = [];
        await using (NpgsqlDataReader reader = await proof
            .ExecuteReaderAsync(CommandBehavior.SingleResult, token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                found.Add(reader.GetInt64(0));
            }
        }

        if (!sequences.All(found.Contains))
        {
            throw new StudioNotAuthorizedException(resolved.Scope);
        }
    }

    /// <summary>
    /// The one Marten call both skips end in, past every check, on a token of the operation's own (<see cref="CommitAsync" />):
    /// <c>MarkEventsAsSkipped(sequences)</c>, or under <c>UseTenantPartitionedEvents</c> its tenant overload for the scope's
    /// tenant (<see cref="MarkTenantEventsAsSkippedAsync" />). A page that goes away now has not decided to abandon it, and
    /// the audit entry has to say how it ended.
    /// </summary>
    private async Task CommitSkipAsync(ResolvedScope resolved, long[] sequences)
    {
        try
        {
            if (resolved.Store.Options.Events.UseTenantPartitionedEvents)
            {
                await CommitAsync(committing => MarkTenantEventsAsSkippedAsync(resolved.Database, resolved.TenantId!, sequences, committing))
                    .ConfigureAwait(false);
                return;
            }

            await CommitAsync(committing => resolved.Database.MarkEventsAsSkipped(sequences, committing)).ConfigureAwait(false);
        }
        finally
        {
            // A group's skipped count changed - or may have, if the call failed part-way.
            groups.InvalidateDatabase(resolved);
        }
    }

    /// <summary>
    /// Whether this Marten skips one tenant's event (<see cref="RuntimeFeature.TenantEventSkipping" />, 9.51+) - asked of
    /// this process's own runtime, so the probe answers for the assemblies actually loaded.
    /// </summary>
    private static bool TenantSkipAvailable => RuntimeFeature.TenantEventSkipping.IsAvailable(MartenRuntime.Current);

    /// <summary>
    /// Whether this process's JasperFx and Marten rewind one tenant's agents (<see cref="RuntimeFeature.TenantSubscriptionRewind" />,
    /// 9.51+).
    /// </summary>
    private static bool TenantRewindAvailable => RuntimeFeature.TenantSubscriptionRewind.IsAvailable(MartenRuntime.Current);

    /// <summary>
    /// <c>IMartenDatabase.MarkEventsAsSkipped(tenantId, sequences, token)</c>, Marten 9.51's tenant overload, reached by
    /// reflection because the floor the studio compiles against has no such member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one call of it, so the argument order is chosen once, reached only from <see cref="CommitSkipAsync" />. Invoked
    /// through the interface's declaration, which dispatches to <c>MartenDatabase</c>'s implementation - the probe has
    /// already said there is one rather than the interface's throwing default - and a missing member is a refusal in the
    /// floor's words rather than a call that fails half-way. Like the tenant-less overload it opens with
    /// <c>EnsureStorageExistsAsync(typeof(IEvent))</c>, and is a capability-gated, audited write for the same reason;
    /// <c>StorageEnsuringCallTests</c> cannot see a reflected call, and lists that among the shapes it does not see.
    /// </para>
    /// </remarks>
    internal static Task MarkTenantEventsAsSkippedAsync(IMartenDatabase database, string tenantId, long[] sequences, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(sequences);

        if (RuntimeFeature.TenantSkipMethod() is not { } method)
        {
            throw new InvalidOperationException(StreamCompactionRules.SkipNeedsOneSequence);
        }

        try
        {
            return method.Invoke(database, [tenantId, sequences, token]) as Task
                ?? throw new InvalidOperationException("Marten's MarkEventsAsSkipped(tenantId, sequences) returned no task.");
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task RewindSubscriptionAsync(
        StudioScope scope,
        string projectionName,
        long eventSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(eventSequence);

        await MutateAsync(
            scope,
            StudioCapability.ManageDeadLetters,
            RewindSubscriptionAction,
            projectionName + " to #" + eventSequence.ToString(CultureInfo.InvariantCulture),
            async (resolved, token) =>
            {
                // Before anything else: Marten deletes the projection's dead letters on the way, so a rewind is a write to
                // DeadLetterEvent, and a hidden type is refused - audited as refused - before any policy is asked of it.
                RequireDeadLettersVisible(resolved);

                // Then, before the whole-database question: under UseTenantPartitionedEvents a store-global rewind has no
                // floor that means one thing. Below JasperFx 2.91 the daemon's RewindSubscriptionAsync (JasperFxAsyncDaemon,
                // read at JasperFx V2.74.0 and V2.87.1) stops every agent of the subscription - each tenant's, since the
                // agents are distributed per tenant there (IEventStore.DistributesAgentsPerTenant) - has Marten write the
                // floor into the store-global shards' progression rows only (RewindSubscriptionProgressAsync over
                // Options.Projections.AllShards(), and deletes the projection's dead letters at or above the floor in every
                // tenant, read at V9.39.0 and V9.50.2), then starts store-global agents, whose event loader has no tenant
                // filter, from that floor: one tenant's event number applied to every tenant's events. The per-tenant
                // overload IProjectionDaemon declares (jasperfx#407) throws for a tenant by default, and no daemon below
                // 2.91 overrides it. From JasperFx 2.91 (jasperfx#1025) the store-global form rewinds each tenant whose agent
                // this daemon runs and refuses a floor above zero, while the per-tenant overload rewinds one tenant - which
                // Marten 9.51 (#5700) is what makes work, by taking the tenant agent's identity. So the store-global form is
                // refused here at every version (SEC1-fix4), and the per-tenant one is the only rewind offered on such a
                // store (RewindTenantAsync).
                if (resolved.Store.Options.Events.UseTenantPartitionedEvents)
                {
                    return await RewindTenantAsync(resolved, projectionName, eventSequence, token).ConfigureAwait(false);
                }

                // Then, what the rewind reaches, which is not the tenant in the scope. Marten 9.31's
                // RewindSubscriptionProgressAsync opens its session with AllowAnyTenant = true, rewrites
                // every shard's progression row of the projection and deletes the projection's dead
                // letters at or above the floor with no tenant predicate; the daemon then restarts every
                // agent of the subscription from there (DatabaseReach). So a visitor scoped to one tenant of
                // a shared database is asked about the database as a whole - (store, database, null) -
                // unless the database is exclusively that tenant's. The daemon is the one the accessor
                // proves is this database's, so this database is the one reached. Before this, a policy
                // allowing only (db, acme) let an acme visitor delete globex's dead letters and re-apply
                // globex's poison event.
                await reach.RequireWholeDatabaseAsync(resolved, resolved.Database, StudioCapability.ManageDeadLetters, token)
                    .ConfigureAwait(false);

                // The name is checked here rather than left to Marten, and the message is the whole
                // reason. Marten's IEventStore.RewindSubscriptionProgressAsync (DocumentStore.EventStore.cs,
                // 9.35) throws ArgumentOutOfRangeException with "Unknown subscription name 'x'. Available
                // options are A, B, C" - every shard the store has, joined into the message. That message
                // reaches the audit entry and the page, so a name typed at a studio would have listed the
                // store's whole projection set to whoever typed it. The refusal below says only what the
                // caller already supplied.
                //
                // The set asked is exactly the one the page's own Rewind button is drawn from -
                // Options.Events.Projections() narrowed to the async lifecycle - so the service refuses
                // precisely what the UI declines to offer (hard rule 5), rather than inventing a second,
                // looser rule behind the button.
                if (!HasAsyncProjection(resolved.Store.Options.Events.Projections(), projectionName))
                {
                    throw UnknownAsyncProjection(projectionName);
                }

                // Hard rule 15: the sequence arrives from a dead-letter row, which Marten registers
                // SingleTenanted, and the rewind reaches an API that applies no tenant predicate of its
                // own. So a visitor scoped to one tenant has to be shown to be able to see the event
                // before it is used, exactly as Skip event does - and the refusal says nothing about
                // whose event it is (D5). This no longer narrows what the rewind does - the check above
                // is what authorizes that - but a rewind started from a tenant's dead-letter page is
                // still started from an event that tenant can see.
                await using (NpgsqlConnection connection = await OpenAsync(resolved, token).ConfigureAwait(false))
                {
                    EventTableInfo table = await DescribeTablesAsync(resolved, connection, token).ConfigureAwait(false);

                    if (table.HasTenantId && resolved.TenantId is not null)
                    {
                        await using NpgsqlCommand lookup =
                            EventQueryBuilder.BuildEventBySequence(table, eventSequence, resolved.TenantId);

                        lookup.Connection = connection;
                        lookup.CommandTimeout = CommandTimeoutSeconds;

                        List<EventRow> found = await ReadEventsAsync(lookup, table, token).ConfigureAwait(false);

                        if (found.Count == 0)
                        {
                            throw new StudioNotAuthorizedException(resolved.Scope);
                        }
                    }
                }

                // Only ever the coordinator the host registered (hard rule 11). "Not hosted here" is a
                // value the page renders, and this refuses regardless of what was rendered (hard rule 5).
                DaemonHosting hosting = await daemons.ForScopeAsync(resolved, token).ConfigureAwait(false);
                if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
                {
                    throw new StudioDaemonNotHostedException(hosting.Explanation);
                }

                // M7: the daemon's RewindSubscriptionAsync restarts every agent of the subscription in the daemon it is asked
                // of, whether or not that daemon ran them (JasperFxAsyncDaemon at V2.74.0 and V2.95.1). Under HotCold a node
                // that does not hold a shard's leadership lock would start it beside the node that does - the rule the tenant
                // rewind below already holds - so it is refused here, and from the node that leads it is made.
                if (!hosting.LeadsEveryShardOf(resolved.Store, projectionName))
                {
                    throw new InvalidOperationException(DaemonTopologyWording.RewindLedElsewhere(projectionName));
                }

                string user = await authorization.UserNameAsync().ConfigureAwait(false);
                logger.DaemonControlRequested(
                    user,
                    RewindSubscriptionAction,
                    projectionName,
                    resolved.Registration.Key,
                    resolved.Database.Id.Identity);

                long floor = RewindFloor(eventSequence);

                // Past every check, on a token of this operation's own (CommitAsync): the daemon stops the
                // subscription's agents, has Marten rewrite their progression and only then restarts them, so a page
                // that went away in between would leave them stopped - and unaudited.
                await CommitAsync(committing => RewindToFloorAsync(
                        daemon, projectionName, floor, logger, resolved.Registration.Key, resolved.Database.Id.Identity, committing))
                    .ConfigureAwait(false);

                // Marten deleted the projection's dead letters at or above the floor.
                groups.InvalidateDatabase(resolved);

                return floor == 0
                    ? "progression row removed, so the subscription replays from the first event"
                    : $"progression set to #{floor.ToString(CultureInfo.InvariantCulture)}, so event "
                        + $"#{eventSequence.ToString(CultureInfo.InvariantCulture)} and everything after it is re-applied";
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewind subscription on a store that numbers its events per tenant (<c>UseTenantPartitionedEvents</c>): one
    /// tenant's agents of the projection, or a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only from Marten 9.51 with JasperFx 2.91</b> (<see cref="RuntimeFeature.TenantSubscriptionRewind" />). There the
    /// daemon's per-tenant <c>RewindSubscriptionAsync(name, tenantId, token, floor)</c> (jasperfx#1025) stops that
    /// tenant's agents of the subscription and no others, has Marten rewrite each of that tenant's shard rows through the
    /// tenant agent's own identity, <c>{Name}:{ShardKey}:{tenant}</c> - which Marten 9.51 resolves (#5700,
    /// <c>findTenantShard</c>) where 9.50 threw "Unknown subscription name" - and restarts them against that tenant's own
    /// high-water mark. Marten deletes only the dead letters with that projection, that shard key, that tenant and a
    /// sequence at or above the floor (<c>DocumentStore.EventStore.cs</c> at v9.51.0). Below either, the floor's refusal
    /// (SEC1-fix4) stands.
    /// </para>
    /// <para>
    /// <b>Reach is the tenant.</b> The progression rows, the dead letters and the agents it touches are all that
    /// tenant's, so the scope the write policy was asked about - (store, database, tenant) - is the whole of it, and the
    /// whole-database question the store-global rewind needs (<see cref="DatabaseReachAuthorization" />, D27's "reach,
    /// not scope") is not asked. That is the one exception to that list, and it holds only for this overload.
    /// </para>
    /// <para>
    /// <b>Only for a tenant whose agent this process runs.</b> The per-tenant overload starts the tenant's agents in the
    /// daemon it is asked of whether or not that daemon ran them - so under HotCold on the cold node, or a host that
    /// distributes agents across nodes (Wolverine), it would run a second agent beside the one on the node that has it.
    /// So the daemon here has to hold an agent of that projection for that tenant (<c>CurrentAgents()</c>), and the call
    /// is made with that agent's own projection name and tenant id: JasperFx stops a tenant's agents by an ordinal
    /// comparison of both, and a name in another case would leave the running agent going beside the new one. That
    /// agent's identity must also read back, through <c>ShardName.TryParse</c>, as the same projection, shard and tenant
    /// (<see cref="TenantRewind.RoundTrips" />): Marten 9.51 resolves the tenant shard by parsing the identity the daemon
    /// hands it, and a tenant id with a <c>:</c> in it does not parse - by then the daemon has hard-stopped the tenant's
    /// agents, and Marten answers "Unknown subscription name", so such a tenant is refused before the daemon is asked.
    /// </para>
    /// <para>
    /// <b>The window that remains.</b> The agent check is a snapshot of <c>CurrentAgents()</c>, and nothing awaits
    /// between it and the call (the user is looked up before it). But the window is the whole daemon call, not that gap:
    /// JasperFx stops the tenant's agents, has Marten rewrite their progression rows, polls the tenant's high-water mark
    /// and only then restarts them. Under HotCold the node can lose leadership at any point in that: its coordinator
    /// stops every agent it runs, and the rewind, which takes no leadership lock, still restarts the tenant's agents here
    /// at its end - beside the agents the new leader starts. The studio cannot close that window, only keep it to the
    /// call itself; it is a known limit (<c>docs/security.md</c>). The extra agents last until this node's next
    /// leadership poll, which stops every agent a node without the lock is running (<c>ProjectionCoordinatorBase</c>,
    /// read at JasperFx <c>v2.91.0</c>) - at most one <c>LeadershipPollingTime</c> after they started.
    /// </para>
    /// </remarks>
    private async Task<string> RewindTenantAsync(
        ResolvedScope resolved,
        string projectionName,
        long eventSequence,
        CancellationToken token)
    {
        if (!TenantRewindAvailable)
        {
            throw new InvalidOperationException(StreamCompactionRules.RewindNeedsOneSequence);
        }

        if (resolved.TenantId is not { } tenantId)
        {
            throw new InvalidOperationException(StreamCompactionRules.RewindNeedsTenant);
        }

        // A tenant id whose agent identity does not read back as that tenant's - one with a ':' in it - cannot be
        // rewound: JasperFx hard-stops the tenant's agents first and Marten then fails to parse the identity it is
        // handed ("Unknown subscription name"), leaving them stopped. Refused before anything is asked of the daemon.
        if (!TenantRewind.IsAddressable(tenantId))
        {
            throw new InvalidOperationException(StreamCompactionRules.RewindTenantNotAddressable(tenantId));
        }

        if (!HasAsyncProjection(resolved.Store.Options.Events.Projections(), projectionName))
        {
            throw UnknownAsyncProjection(projectionName);
        }

        // The event the rewind starts from is this tenant's - the dead letter's sequence is a number in each tenant's
        // own sequence, and a visitor scoped to acme is shown to be able to see acme's #N before it is used (hard rule
        // 15). The refusal says nothing about whose event it is (D5).
        await using (NpgsqlConnection connection = await OpenAsync(resolved, token).ConfigureAwait(false))
        {
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, token).ConfigureAwait(false);

            if (table.HasTenantId)
            {
                await using NpgsqlCommand lookup = EventQueryBuilder.BuildEventBySequence(table, eventSequence, tenantId);

                lookup.Connection = connection;
                lookup.CommandTimeout = CommandTimeoutSeconds;

                List<EventRow> found = await ReadEventsAsync(lookup, table, token).ConfigureAwait(false);

                if (found.Count == 0)
                {
                    throw new StudioNotAuthorizedException(resolved.Scope);
                }
            }
        }

        DaemonHosting hosting = await daemons.ForScopeAsync(resolved, token).ConfigureAwait(false);
        if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
        {
            throw new StudioDaemonNotHostedException(hosting.Explanation);
        }

        if (!ImplementsTenantRewind(daemon))
        {
            throw new InvalidOperationException(
                "The async daemon this process hosts does not rewind one tenant's agents (IProjectionDaemon.RewindSubscriptionAsync "
                + "with a tenant), so Marten Studio does not rewind subscriptions on this store.");
        }

        // The user first, so that nothing awaits between the agent check and the call: the check is a snapshot of the
        // daemon, and the window in which it can go stale (the remarks' HotCold case) is as short as this code makes it.
        string user = await authorization.UserNameAsync().ConfigureAwait(false);

        if (TenantAgentOf(daemon, projectionName, tenantId) is not { } agent)
        {
            throw new InvalidOperationException(StreamCompactionRules.RewindNeedsTenantAgentHere(projectionName, tenantId));
        }

        logger.DaemonControlRequested(
            user,
            RewindSubscriptionAction,
            agent.Name + " (tenant " + agent.TenantId + ")",
            resolved.Registration.Key,
            resolved.Database.Id.Identity);

        long floor = RewindFloor(eventSequence);

        // Past every check, on a token of this operation's own (CommitAsync): JasperFx 2.91's rewindSubscriptionForTenantAsync
        // stops the tenant's agents, awaits Marten's rewrite of their progression rows and only then restarts them, so the
        // page's token cancelled in between would leave that tenant's projection stopped.
        await CommitAsync(committing => RewindTenantToFloorAsync(
                daemon, agent.Name, agent.TenantId!, floor, logger, resolved.Registration.Key, resolved.Database.Id.Identity, committing))
            .ConfigureAwait(false);

        // Marten deleted that tenant's dead letters of the shard at or above the floor.
        groups.InvalidateDatabase(resolved);

        string tenant = " in tenant " + agent.TenantId;

        return floor == 0
            ? "progression rows removed" + tenant + ", so the subscription replays that tenant's events from the first"
            : $"progression set to #{floor.ToString(CultureInfo.InvariantCulture)}{tenant}, so that tenant's event "
                + $"#{eventSequence.ToString(CultureInfo.InvariantCulture)} and everything after it is re-applied";
    }

    /// <summary>
    /// The shard name of the agent <paramref name="daemon" /> runs for <paramref name="projectionName" /> in
    /// <paramref name="tenantId" />, or <see langword="null" /> when it runs none - the projection name compared as
    /// Marten compares it (ignoring case), the tenant ordinally - and only one whose identity reads back as itself
    /// (<see cref="TenantRewind.RoundTrips" />), because that identity is what Marten parses to find the tenant's shard.
    /// </summary>
    internal static ShardName? TenantAgentOf(IProjectionDaemon daemon, string projectionName, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(daemon);

        foreach (ISubscriptionAgent agent in daemon.CurrentAgents())
        {
            if (agent.Name is { TenantId: { } tenant } name
                && string.Equals(tenant, tenantId, StringComparison.Ordinal)
                && string.Equals(name.Name, projectionName, StringComparison.OrdinalIgnoreCase)
                && TenantRewind.RoundTrips(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="daemon" /> itself implements the per-tenant <c>RewindSubscriptionAsync</c>, rather than
    /// inheriting <c>IProjectionDaemon</c>'s default, which throws for a tenant - asked of the daemon actually handed over,
    /// since a coordinator may hand over something other than Marten's own.
    /// </summary>
    internal static bool ImplementsTenantRewind(IProjectionDaemon daemon)
    {
        ArgumentNullException.ThrowIfNull(daemon);

        return ImplementsTenantRewind(daemon.GetType());
    }

    /// <summary>The same question of a daemon type: whether it maps the per-tenant rewind to a method of a class.</summary>
    internal static bool ImplementsTenantRewind(Type daemonType)
    {
        ArgumentNullException.ThrowIfNull(daemonType);

        if (!typeof(IProjectionDaemon).IsAssignableFrom(daemonType))
        {
            return false;
        }

        MethodInfo? declared = typeof(IProjectionDaemon).GetMethod(
            nameof(IProjectionDaemon.RewindSubscriptionAsync),
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(string), typeof(string), typeof(CancellationToken), typeof(long?), typeof(DateTimeOffset?)]);

        if (declared is null)
        {
            return false;
        }

        InterfaceMapping map = daemonType.GetInterfaceMap(typeof(IProjectionDaemon));
        int index = Array.IndexOf(map.InterfaceMethods, declared);

        return index >= 0 && map.TargetMethods[index].DeclaringType is { IsInterface: false };
    }

    /// <summary>
    /// The one call that rewinds one tenant's agents, and the only place its argument order is chosen.
    /// </summary>
    /// <remarks>
    /// <c>RewindSubscriptionAsync(string subscriptionName, string? tenantId, CancellationToken token, long? sequenceFloor,
    /// DateTimeOffset? timestamp)</c> - the token <em>third</em>, between the tenant and the floor (declared on
    /// <c>IProjectionDaemon</c> since at least JasperFx 2.74.0, implemented from 2.91.0). A floor of zero is Marten's
    /// "delete the progression row". A refusal as an unknown subscription is logged whole (event 9320) and rethrown
    /// without Marten's words (<see cref="ListsSubscriptions" />).
    /// </remarks>
    internal static async Task RewindTenantToFloorAsync(
        IProjectionDaemon daemon,
        string projectionName,
        string tenantId,
        long sequenceFloor,
        ILogger logger,
        string storeKey,
        string databaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await daemon.RewindSubscriptionAsync(projectionName, tenantId, cancellationToken, sequenceFloor).ConfigureAwait(false);
        }
        catch (Exception exception) when (ListsSubscriptions(exception))
        {
            logger.SubscriptionRewindRefused(exception, projectionName, tenantId, storeKey, databaseId);
            throw new InvalidOperationException(StreamCompactionRules.SubscriptionNotRecognised(projectionName, tenantId));
        }
    }

    /// <summary>
    /// Whether <paramref name="exception" />, or one it wraps, is Marten's or JasperFx's "Unknown subscription name 'x'.
    /// Available options are A, B, C" - every shard the store has, joined into the message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That message would reach the audit ring and the page through the failure's own text, and tell whoever asked the
    /// store's whole projection set (D5). The studio refuses an unknown name before either is asked, so this is the belt to
    /// that: the two rewinds replace it with a sentence naming only what the caller supplied, and keep none of it - not
    /// even as an inner exception. Marten's words go to the host's own log instead (event 9320), with the exception.
    /// </para>
    /// <para>
    /// Every wrapped exception is walked: an <see cref="AggregateException" />'s <see cref="AggregateException.InnerExceptions" />
    /// each, not only its first, since its own message carries its inner exceptions' messages but not theirs in turn.
    /// </para>
    /// </remarks>
    internal static bool ListsSubscriptions(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        if (exception.Message.Contains("Available options", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Unknown subscription name", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return exception is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(ListsSubscriptions)
            : ListsSubscriptions(exception.InnerException);
    }

    /// <summary>
    /// The floor a rewind is given so that <paramref name="eventSequence" /> is itself re-applied.
    /// </summary>
    /// <remarks>
    /// One below, and the off-by-one is the whole point. Marten's
    /// <c>RewindSubscriptionProgressAsync</c> writes the floor into <c>mt_event_progression.last_seq_id</c>,
    /// which records the last sequence the shard has <em>already</em> processed, and the restarted agent
    /// then asks for events strictly after it. Handing it the dead letter's own sequence would rewind the
    /// shard to just past the event the visitor is trying to replay - the one thing the button must not
    /// do. A floor of zero is Marten's "delete the progression row", which replays the projection from the
    /// first event; that is the right answer when the poison event is #1.
    /// </remarks>
    /// <param name="eventSequence">The sequence to re-apply.</param>
    internal static long RewindFloor(long eventSequence) => Math.Max(0, eventSequence - 1);

    /// <summary>
    /// Whether <paramref name="projections" /> holds an <em>async</em> projection called
    /// <paramref name="projectionName" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is <c>IReadOnlyEventStoreOptions.Projections()</c>, which is the static model - the same
    /// one the projections screen, the dead-letter page's Rewind button and
    /// <c>ProjectionDataService.DescribeRebuildAsync</c> are all built from - and it costs nothing,
    /// because it is the configuration the host wrote rather than anything in a database. The lifecycle
    /// narrowing matters as much as the name: an inline or live projection has no shard for the daemon to
    /// restart, so rewinding one is not a thing that can be done however it is spelt.
    /// </para>
    /// <para>
    /// The comparison is ordinal-ignore-case, which is Marten's own (<c>EqualsIgnoreCase</c> against the
    /// shard name). The one thing this is narrower about than Marten is a raw
    /// <c>Subscribe(ISubscription)</c> registration: those live in the projection graph's separate
    /// subscription list and <c>Projections()</c> does not return them (verified against JasperFx.Events'
    /// <c>ProjectionGraph</c> for 9.35). The studio cannot list such a subscription on any screen either,
    /// so its Rewind button is never offered for one - this refuses exactly what the UI refuses.
    /// </para>
    /// <para>
    /// A list rather than the store, so that the decision is a pure function of the configuration and can
    /// be tested without a Postgres or a built <see cref="IDocumentStore" /> behind it.
    /// </para>
    /// </remarks>
    /// <param name="projections">The store's registered projections and subscriptions.</param>
    /// <param name="projectionName">The name a dead letter carried, or a caller typed.</param>
    internal static bool HasAsyncProjection(IReadOnlyList<ISubscriptionSource> projections, string projectionName)
    {
        ArgumentNullException.ThrowIfNull(projections);

        foreach (ISubscriptionSource source in projections)
        {
            if (source.Lifecycle == ProjectionLifecycle.Async
                && string.Equals(source.Name, projectionName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The refusal a rewind gets for a name this store has no async projection for.
    /// </summary>
    /// <remarks>
    /// The message repeats only the name it was given, and that is the entire point of it existing.
    /// Marten's own check - <c>IEventStore.RewindSubscriptionProgressAsync</c> in 9.35 - throws
    /// <see cref="ArgumentOutOfRangeException" /> with <c>"Unknown subscription name 'x'. Available
    /// options are A, B, C"</c>, every shard the store has joined into the sentence. That message would
    /// travel into the audit ring and onto the page, so a wrong name typed at the studio would have
    /// listed the store's whole projection set to whoever typed it.
    /// </remarks>
    /// <param name="projectionName">The name that was asked for.</param>
    internal static KeyNotFoundException UnknownAsyncProjection(string projectionName) =>
        new($"This store has no async projection named '{projectionName}'.");

    /// <summary>
    /// The one call that rewinds a subscription, and the only place the argument order is chosen.
    /// </summary>
    /// <remarks>
    /// <c>RewindSubscriptionAsync(string subscriptionName, CancellationToken token, long? sequenceFloor,
    /// DateTimeOffset? timestamp)</c> - the token is the <em>second</em> parameter, not the last, and
    /// both of the interesting arguments are optional and nullable (verified against JasperFx.Events
    /// 2.69.3). A call written in the usual order would compile and silently mean
    /// <c>sequenceFloor: 0</c>, which is a full replay of the projection rather than a rewind to one
    /// event. There is also a five-argument overload that takes a tenant id; below JasperFx 2.91 its default
    /// implementation throws for a non-null tenant, and outside <c>UseTenantPartitionedEvents</c> projection
    /// progression is per shard rather than per tenant, so on such a store the studio uses this store-global one,
    /// authorizes it for the database as a whole (<see cref="DatabaseReachAuthorization.RequireWholeDatabaseAsync" />),
    /// and the dialog says so. Under per-tenant partitioning the tenant overload is the only rewind
    /// (<see cref="RewindTenantToFloorAsync" />).
    /// </remarks>
    internal static async Task RewindToFloorAsync(
        IProjectionDaemon daemon,
        string projectionName,
        long sequenceFloor,
        ILogger logger,
        string storeKey,
        string databaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await daemon.RewindSubscriptionAsync(projectionName, cancellationToken, sequenceFloor).ConfigureAwait(false);
        }
        catch (Exception exception) when (ListsSubscriptions(exception))
        {
            logger.SubscriptionRewindRefused(exception, projectionName, AllTenants, storeKey, databaseId);
            throw new InvalidOperationException(StreamCompactionRules.SubscriptionNotRecognised(projectionName, tenantId: null));
        }
    }

    /// <summary>What event 9320 names as the tenant of a store-global rewind.</summary>
    private const string AllTenants = "(all tenants)";

    /// <summary>
    /// Runs an operation's commit - its Marten or daemon call, once every check has passed - on a token of the operation's
    /// own rather than the page's, as compaction does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The page's token is cancelled when the page is disposed - a closed tab, a navigation, a dropped circuit - and that
    /// is not a decision to abandon something a person confirmed in a dialog. Cancelled part-way, a rewind is the worst
    /// case: JasperFx stops the subscription's agents, awaits Marten's rewrite of their progression and only then restarts
    /// them (<c>rewindSubscriptionForTenantAsync</c> and the store-global <c>RewindSubscriptionAsync</c>, read at JasperFx
    /// <c>v2.91.0</c>), so a cancellation in between leaves the projection stopped. Either way <see cref="MutateAsync" />
    /// writes no audit entry for a cancellation, so nothing would say how it ended. The token here is never cancelled; every
    /// statement is still bounded by its own command timeout.
    /// </para>
    /// <para>
    /// A cancellation that surfaces from inside all the same - a daemon that is itself shutting down - was not the
    /// visitor's, and the operation may be partly done: it is rethrown as a failure, which <see cref="MutateAsync" /> audits
    /// like any other.
    /// </para>
    /// </remarks>
    private async Task CommitAsync(Func<CancellationToken, Task> commit)
    {
        if (CommitStarting is { } starting)
        {
            await starting().ConfigureAwait(false);
        }

        using var committing = new CancellationTokenSource();

        try
        {
            await commit(committing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancelled)
        {
            throw new InvalidOperationException(
                "It was cancelled from inside Marten or the async daemon after it had started, so it may be partly done: "
                + cancelled.Message,
                cancelled);
        }
    }

    /// <summary>
    /// A test's hook, run once every check of a skip or a rewind has passed and just before its commit - the point from
    /// which the page's token no longer counts (<see cref="CommitAsync" />). Always <see langword="null" /> outside a test.
    /// </summary>
    internal Func<Task>? CommitStarting { get; set; }

    /// <summary>
    /// The shape of every mutating call: capability, then scope with the write policy, then the
    /// operation, then the audit entry - written on failure as well as on success (hard rule 5).
    /// </summary>
    /// <returns>The operation's outcome, as it was audited.</returns>
    private async Task<string> MutateAsync(
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
        catch (StudioNotAuthorizedException denied)
        {
            audit.RecordScopeDenied(denied.Scope, options.Value.WriteAuthorizationPolicy ?? "(none)", action, target);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A scope the policies passed that names no store, database or tenant this process has
            // (KeyNotFoundException), a store that will not build (StudioStoreUnavailableException), or an enumeration
            // that failed on the way: the write was refused before it began, and an audit that skipped it would not
            // say what somebody tried (hard rule 5). Still thrown - it fails closed, and the page says why.
            audit.Record(action, target, succeeded: false, exception.Message, capability, scope);
            throw;
        }

        try
        {
            string outcome = await operation(resolved, cancellationToken).ConfigureAwait(false);
            audit.Record(action, target, succeeded: true, outcome, capability, scope);
            return outcome;
        }
        catch (StudioNotAuthorizedException denied)
        {
            // A refusal the operation itself raised - the target turned out not to be in this scope after
            // the resolution had already passed, which is how "skip event 4711" behaves on a conjoined
            // store when 4711 is another tenant's event, or the operation reaches more than the scope and
            // that wider question was refused, which is how a rewind from a tenant scope behaves on a
            // shared database. It is a scope denial and is audited as one (9203) rather than as an
            // ordinary failed action, so the log says what it was - and against the scope the refusal
            // names rather than the one that was asked for: for the rewind that is (store, database,
            // null), the database as a whole, which is the one fact the requested scope does not carry.
            audit.RecordScopeDenied(denied.Scope, options.Value.WriteAuthorizationPolicy ?? "(none)", action, target);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            audit.Record(action, target, succeeded: false, exception.Message, capability, scope);
            throw;
        }
    }

    private static Task<NpgsqlConnection> OpenAsync(ResolvedScope resolved, CancellationToken cancellationToken) =>
        OpenAsync(resolved, ConnectionUsage.Read, cancellationToken);

    private static async Task<NpgsqlConnection> OpenAsync(
        ResolvedScope resolved,
        ConnectionUsage usage,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = MartenStudioDatabase.CreateConnection(resolved.Database, usage);

        try
        {
            // Through PostgresFailure, so that a pool that ran dry or a connect that timed out - which
            // Npgsql spells exactly like a statement timeout - is never logged as one (LogReadFailure).
            await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The session options every Marten session in this service is opened with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The database is pinned, not only the tenant.</b> <c>Store.QuerySession(tenantId)</c> resolves the
    /// tenant through the store's own tenancy and lands on whichever database that says — which on a
    /// multi-database store is not the database the scope selector picked and the audit entry records. The
    /// static factories <c>Marten.Services.SessionOptions.ForDatabase(database)</c> and
    /// <c>ForDatabase(tenantId, database)</c> are what make "this database, this tenant" expressible
    /// (Appendix B), and <c>DocumentWriteService</c> has opened its sessions that way since W3.
    /// Every session here now does the same, so an archive, a discard or a replay goes to the database the
    /// visitor was looking at.
    /// </para>
    /// <para>
    /// <c>Timeout</c> is the studio's own <c>QueryTimeout</c>: Marten applies it to every command it runs
    /// on the session, which is the only way a replay of a ten-thousand-event stream is bounded at all —
    /// the studio never sees those commands to set a <c>CommandTimeout</c> on them.
    /// </para>
    /// </remarks>
    private SessionOptions SessionFor(ResolvedScope resolved)
    {
        SessionOptions sessionOptions = resolved.TenantId is { } tenantId
            ? SessionOptions.ForDatabase(tenantId, resolved.Database)
            : SessionOptions.ForDatabase(resolved.Database);

        sessionOptions.Timeout = CommandTimeoutSeconds;

        return sessionOptions;
    }

    /// <summary>A read session on the scope's own database, and its tenant when one is pinned.</summary>
    private IQuerySession OpenQuerySession(ResolvedScope resolved) =>
        resolved.Store.QuerySession(SessionFor(resolved));

    /// <summary>A write session on the scope's own database, and its tenant when one is pinned.</summary>
    private IDocumentSession OpenWriteSession(ResolvedScope resolved) =>
        resolved.Store.LightweightSession(SessionFor(resolved));

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
                 (string[]) ["tenant_id", "mt_dotnet_type", "correlation_id", "causation_id", "headers", "user_name"])
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
                metadata)
            {
                Tags = columns.HStoreTags(reader),
            });
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
            columns.Text(reader, "tenant_id"))
        {
            CompactedVersion = columns.Int64(reader, "compacted_version") ?? 0,
        };

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
    private EventDataError Describe(StudioScope scope, Exception exception, string what)
    {
        if (exception is PostgresException postgres)
        {
            LogReadFailure(scope, exception, what, postgres.SqlState);
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
            LogReadFailure(scope, exception, what, sqlState: null);
            return new EventDataError(exception.Message, null, true);
        }

        LogReadFailure(scope, exception, what, sqlState: null);
        return new EventDataError(exception.Message, null, false);
    }

    /// <summary>
    /// The log line for one failed event-store read, at the level the failure deserves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every read here renders its failure as a value, and several of them are polled - the Overview's
    /// counts, feed and newest streams, the feed page, the navigation badges - so the level is decided
    /// rather than assumed.
    /// </para>
    /// <para>
    /// <b>Debug:</b> a statement that ran past <c>QueryTimeout</c> or was cancelled - the budget the studio
    /// set doing its job, on a read the page is already showing as "timed out, retry" - in either of the
    /// spellings it arrives in (<see cref="PostgresFailure.IsTimeout" />: <c>57014</c>, or Npgsql's own
    /// client-side timeout when the backend was too busy to answer the cancel); and a store or database
    /// the URL named that does not exist (<see cref="KeyNotFoundException" />), which is a stale link
    /// rather than a fault.
    /// </para>
    /// <para>
    /// <b>Warning, throttled:</b> everything else - a connection that failed, a permission the role lacks,
    /// an SQLSTATE nobody expected. Event 9217, once per read, store, database and kind of failure
    /// (<see cref="StudioLogThrottle.KindOf" />, so a 42501 does not silence a 53300) per
    /// <see cref="StudioLogThrottle.Window" />, and Debug in between.
    /// </para>
    /// <para>
    /// <b>A missing table or schema (42P01, 3F000) is on this side too.</b> It is not how an event store
    /// that has not been created yet shows up: every read here asks the column catalog first
    /// (<c>DescribeTablesAsync</c>), and the dead-letter reads <c>DescribeDeadLettersAsync</c>, and
    /// answers "empty" when the tables are not there - so the expected state never reaches Postgres at
    /// all. One of those SQLSTATEs therefore means the catalog said a table exists and the statement found
    /// none: a table dropped from under a running studio, a search path that changed, a schema a role
    /// cannot see. That is worth one line, and being Debug hid it.
    /// </para>
    /// </remarks>
    private void LogReadFailure(StudioScope scope, Exception exception, string what, string? sqlState)
    {
        string storeKey = scope.StoreKey;
        string databaseId = scope.DatabaseId;

        LogLevel level = IsExpectedReadFailure(exception)
            ? LogLevel.Debug
            : throttle.WarningOrDebug("EventDataService." + what, storeKey, databaseId, StudioLogThrottle.KindOf(exception));

        // The scope as the address spelled it, escaped and bounded for the log (wave 6); the throttle keys on it as it is.
        if (logger.IsEnabled(level))
        {
            logger.EventReadFailed(level, exception, what, StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), sqlState);
        }
    }

    /// <summary>
    /// Whether a failed event-store read is one of the expected answers <see cref="LogReadFailure" />
    /// writes at Debug: a statement timeout in either spelling, or a stale link.
    /// </summary>
    /// <remarks>
    /// Separate so the classification can be tested without a database. A connection that could not be
    /// opened is never expected here, however Npgsql spells it:
    /// <see cref="OpenAsync(ResolvedScope, ConnectionUsage, CancellationToken)" /> opens through
    /// <see cref="PostgresFailure.OpenAsync" />, which is what keeps a pool that ran dry off this list.
    /// </remarks>
    /// <param name="exception">What the read threw.</param>
    internal static bool IsExpectedReadFailure(Exception exception) =>
        PostgresFailure.IsTimeout(exception) || exception is KeyNotFoundException;

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

        /// <summary>
        /// A store's inline DCB tags: <c>mt_events.tags</c> in <c>DcbStorageMode.HStore</c>, keyed by the tag
        /// type's suffix, which Npgsql reads as a dictionary. Empty on a store without the column, and for any
        /// <c>tags</c> column that is not an hstore.
        /// </summary>
        public List<EventTag> HStoreTags(NpgsqlDataReader reader)
        {
            if (!ordinals.TryGetValue(EventTagModel.HStoreColumn, out int index)
                || reader.IsDBNull(index)
                || reader.GetValue(index) is not IDictionary<string, string?> tags)
            {
                return [];
            }

            List<EventTag> found = new(tags.Count);
            foreach (KeyValuePair<string, string?> tag in tags.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                found.Add(new EventTag(tag.Key, tag.Value ?? string.Empty));
            }

            return found;
        }
    }
}
