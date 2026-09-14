using System.Globalization;
using System.Text;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>What the global event feed is asking for.</summary>
internal sealed record EventFeedQuery
{
    /// <summary>The default page of the feed.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>The largest page the builder will emit.</summary>
    public const int MaxPageSize = 1000;

    /// <summary>The keyset cursor: only events before this sequence. Null for the newest page.</summary>
    public long? AfterSequence { get; init; }

    /// <summary>Only this tenant's events, when the store is conjoined-tenanted.</summary>
    public string? TenantId { get; init; }

    /// <summary>Only this stream's events, as typed by the user.</summary>
    public string? StreamId { get; init; }

    /// <summary>Only this event type name.</summary>
    public string? EventType { get; init; }

    /// <summary>
    /// Only these event type names, for the feed's multi-select. Null or empty means every type.
    /// </summary>
    /// <remarks>
    /// A <c>text[]</c> parameter compared with <c>= any(@types)</c>, not a generated <c>in (…)</c> list:
    /// one statement and one plan however many types are ticked, and no identifier or value is ever
    /// interpolated. <see cref="EventType" /> stays for the single-type links and the two are ANDed, which
    /// is what "the feed opened from an event type, then narrowed" means.
    /// </remarks>
    public IReadOnlyList<string>? EventTypes { get; init; }

    /// <summary>Only events at or after this instant.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only events before this instant.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Whether archived events are included.</summary>
    public bool IncludeArchived { get; init; }

    /// <summary>
    /// Whether events the daemon was told to skip are included. Only meaningful on a store that enabled
    /// event skipping, which is the only store whose <c>mt_events</c> has an <c>is_skipped</c> column.
    /// </summary>
    public bool IncludeSkipped { get; init; } = true;

    /// <summary>How many events to return.</summary>
    public int PageSize { get; init; } = DefaultPageSize;
}

/// <summary>Where a keyset page of the stream list starts.</summary>
/// <param name="Timestamp">The last row's timestamp.</param>
/// <param name="Id">The last row's id, as text.</param>
internal sealed record StreamKeysetCursor(DateTimeOffset Timestamp, string Id);

/// <summary>What the streams list is asking for.</summary>
internal sealed record StreamListQuery
{
    /// <summary>The default page of the stream list.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The largest page the builder will emit.</summary>
    public const int MaxPageSize = 500;

    /// <summary>One stream by id, as typed by the user.</summary>
    public string? StreamId { get; init; }

    /// <summary>Streams whose aggregate type name starts with this.</summary>
    public string? TypePrefix { get; init; }

    /// <summary>Only this tenant's streams.</summary>
    public string? TenantId { get; init; }

    /// <summary>Whether archived streams are included.</summary>
    public bool IncludeArchived { get; init; }

    /// <summary>The keyset cursor. When set, <see cref="Offset"/> is ignored.</summary>
    public StreamKeysetCursor? Cursor { get; init; }

    /// <summary>The offset, for the offset toggle.</summary>
    public int Offset { get; init; }

    /// <summary>How many streams to return.</summary>
    public int PageSize { get; init; } = DefaultPageSize;
}

/// <summary>
/// Builds the event-store reads: the global feed, one stream's events, the stream list, the "recently
/// active" panel, the per-type counts, the dead-letter count and the high-water fallback.
/// </summary>
/// <remarks>
/// <para>
/// The same rules as <see cref="DocumentQueryBuilder"/>: identifiers quoted, values parameters, never
/// <c>select *</c>. What is different is that the column set is not known at compile time — see
/// <see cref="EventTableInfo"/> — so every optional column is emitted only when the table has it.
/// </para>
/// <para>
/// <b><c>bdata</c> is never selected.</b> An event with a binary payload shows as "binary payload, N
/// bytes", which is <c>(bdata is not null)</c> and <c>octet_length(bdata)</c>; selecting the column itself
/// would push megabytes of protobuf down a SignalR circuit to render as nothing.
/// </para>
/// <para>
/// The feed pages on <c>seq_id</c> descending, which is a total order by construction, so unlike the
/// document list it needs no tiebreaker. <c>(@afterSeq is null or seq_id &lt; @afterSeq)</c> keeps one
/// statement — and therefore one plan — for the first page and every page after it.
/// </para>
/// </remarks>
internal static class EventQueryBuilder
{
    private const string Alias = EventTableInfo.EventsAlias;
    private const string StreamAlias = EventTableInfo.StreamsAlias;
    private const string AsAlias = " as " + Alias;
    private const string AsStreamAlias = " as " + StreamAlias;

    /// <summary>The columns the feed and the stream timeline always select, in order.</summary>
    private static readonly string[] CoreEventColumns =
        ["seq_id", "id", "stream_id", "version", "type", "timestamp"];

    /// <summary>The optional event columns, selected when the table has them.</summary>
    private static readonly string[] OptionalEventColumns =
        ["tenant_id", "mt_dotnet_type", "is_archived", "is_skipped", "correlation_id", "causation_id", "headers", "user_name", "tags"];

    /// <summary>The columns the stream list selects, when they exist.</summary>
    private static readonly string[] StreamColumns =
        ["id", "type", "version", "timestamp", "created", "is_archived", "tenant_id"];

    /// <summary>The global event feed, newest first.</summary>
    public static NpgsqlCommand BuildFeed(EventTableInfo table, EventFeedQuery query)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);

        var command = new NpgsqlCommand();

        try
        {
            var parameters = new ParameterBuilder(command);
            var sql = new StringBuilder();

            AppendEventSelect(sql, table);
            sql.Append("from ").Append(table.QualifiedEvents).Append(AsAlias).Append('\n');

            sql.Append("where (").Append(parameters.AddNullable(NpgsqlDbType.Bigint, query.AfterSequence, "afterSeq"))
                .Append(" is null or ").Append(Column(Alias, "seq_id")).Append(" < @afterSeq)\n");

            if (table.HasTenantId)
            {
                sql.Append("  and (").Append(parameters.AddNullable(NpgsqlDbType.Varchar, query.TenantId, "tenant"))
                    .Append(" is null or ").Append(Column(Alias, "tenant_id")).Append(" = @tenant)\n");
            }

            var streamId = ParseStream(table, query.StreamId);

            if (streamId.IsImpossible)
            {
                // The user asked for a stream id this store could never hold. "Not asked" and "asked for
                // something that cannot exist" are different questions with different answers, and mapping
                // both onto a null parameter answered the first one - so a typo in the stream box showed
                // the whole feed instead of an empty page.
                sql.Append("  and false\n");
            }
            else
            {
                sql.Append("  and (").Append(parameters.AddNullable(table.StreamIdDbType, streamId.Value, "streamId"))
                    .Append(" is null or ").Append(Column(Alias, "stream_id")).Append(" = @streamId)\n");
            }

            sql.Append("  and (").Append(parameters.AddNullable(NpgsqlDbType.Varchar, query.EventType, "type"))
                .Append(" is null or ").Append(Column(Alias, "type")).Append(" = @type)\n");

            sql.Append("  and (").Append(parameters.AddNullable(
                    NpgsqlDbType.Array | NpgsqlDbType.Text, NormalizeTypes(query.EventTypes), "types"))
                .Append(" is null or ").Append(Column(Alias, "type")).Append(" = any(@types))\n");

            sql.Append("  and (").Append(parameters.AddNullable(NpgsqlDbType.TimestampTz, query.From, "from"))
                .Append(" is null or ").Append(Column(Alias, "timestamp")).Append(" >= @from)\n");

            sql.Append("  and (").Append(parameters.AddNullable(NpgsqlDbType.TimestampTz, query.To, "to"))
                .Append(" is null or ").Append(Column(Alias, "timestamp")).Append(" < @to)\n");

            if (table.HasIsArchived)
            {
                sql.Append("  and (").Append(parameters.Add(NpgsqlDbType.Boolean, query.IncludeArchived, "includeArchived"))
                    .Append(" or ").Append(Column(Alias, "is_archived")).Append(" = false)\n");
            }

            if (table.HasIsSkipped)
            {
                sql.Append("  and (").Append(parameters.Add(NpgsqlDbType.Boolean, query.IncludeSkipped, "includeSkipped"))
                    .Append(" or ").Append(Column(Alias, "is_skipped")).Append(" = false)\n");
            }

            sql.Append("order by ").Append(Column(Alias, "seq_id")).Append(" desc\n");
            sql.Append("limit ").Append(parameters.Add(
                NpgsqlDbType.Integer, Math.Clamp(query.PageSize, 1, EventFeedQuery.MaxPageSize), "limit"));

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>One stream's events, in version order, from a version the caller already has.</summary>
    public static bool TryBuildStreamEvents(
        EventTableInfo table,
        string rawStreamId,
        long afterVersion,
        int take,
        string? tenantId,
        out NpgsqlCommand? command,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(table);

        var parsed = table.ParseStreamId(rawStreamId);

        if (!parsed.Success)
        {
            command = null;
            error = parsed.Error;
            return false;
        }

        var built = new NpgsqlCommand();

        try
        {
            var parameters = new ParameterBuilder(built);
            var sql = new StringBuilder();

            AppendEventSelect(sql, table);
            sql.Append("from ").Append(table.QualifiedEvents).Append(AsAlias).Append('\n');
            sql.Append("where ").Append(Column(Alias, "stream_id")).Append(" = ")
                .Append(parameters.Add(table.StreamIdDbType, parsed.Value!, "id")).Append('\n');
            sql.Append("  and ").Append(Column(Alias, "version")).Append(" > ")
                .Append(parameters.Add(NpgsqlDbType.Bigint, afterVersion, "afterVersion")).Append('\n');

            if (table.HasTenantId && tenantId is not null)
            {
                sql.Append("  and ").Append(Column(Alias, "tenant_id")).Append(" = ")
                    .Append(parameters.Add(NpgsqlDbType.Varchar, tenantId, "tenant")).Append('\n');
            }

            sql.Append("order by ").Append(Column(Alias, "version")).Append('\n');
            sql.Append("limit ").Append(parameters.Add(
                NpgsqlDbType.Integer, Math.Clamp(take, 1, EventFeedQuery.MaxPageSize), "take"));

            built.CommandText = sql.ToString();
            command = built;
            error = null;
            return true;
        }
        catch
        {
            built.Dispose();
            throw;
        }
    }

    /// <summary>The stream list, newest activity first.</summary>
    public static NpgsqlCommand BuildStreams(EventTableInfo table, StreamListQuery query)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);

        if (query.Offset is < 0 or > DocumentQueryBuilder.MaxOffset)
        {
            throw new ArgumentException(
                $"An offset must be between 0 and {DocumentQueryBuilder.MaxOffset.ToString(CultureInfo.InvariantCulture)}.",
                nameof(query));
        }

        var command = new NpgsqlCommand();

        try
        {
            var parameters = new ParameterBuilder(command);
            var sql = new StringBuilder();
            var first = true;

            sql.Append("select ");

            foreach (var column in StreamColumns)
            {
                if (!table.HasStreamColumn(column))
                {
                    continue;
                }

                sql.Append(first ? string.Empty : ",\n       ").Append(Column(StreamAlias, column));
                first = false;
            }

            sql.Append('\n');
            sql.Append("from ").Append(table.QualifiedStreams).Append(AsStreamAlias).Append('\n');

            AppendStreamFilters(sql, parameters, table, query);

            if (query.Cursor is { } cursor)
            {
                var parsedCursorId = table.ParseStreamId(cursor.Id);

                if (!parsedCursorId.Success)
                {
                    throw new ArgumentException($"The page cursor's stream id is not usable: {parsedCursorId.Error}",
                        nameof(query));
                }

                var timestamp = parameters.Add(NpgsqlDbType.TimestampTz, cursor.Timestamp, "k");
                var id = parameters.Add(table.StreamIdDbType, parsedCursorId.Value!, "i");

                // (timestamp, id) is a total order here because mt_streams."timestamp" is NOT NULL in
                // Marten's own schema - a keyset on a nullable column silently drops every row whose key is
                // null, since `null < @k` is null and not true. The `nulls last` on the ORDER BY below is
                // the belt to this braces, for a table somebody migrated by hand.
                sql.Append("  and (").Append(Column(StreamAlias, "timestamp")).Append(" < ").Append(timestamp)
                    .Append(" or (").Append(Column(StreamAlias, "timestamp")).Append(" = ").Append(timestamp)
                    .Append(" and ").Append(Column(StreamAlias, "id")).Append(" > ").Append(id).Append("))\n");
            }

            sql.Append("order by ").Append(Column(StreamAlias, "timestamp")).Append(" desc nulls last, ")
                .Append(Column(StreamAlias, "id")).Append('\n');
            sql.Append("limit ").Append(parameters.Add(
                NpgsqlDbType.Integer, Math.Clamp(query.PageSize, 1, StreamListQuery.MaxPageSize), "limit"));

            if (query.Cursor is null)
            {
                sql.Append(" offset ").Append(parameters.Add(NpgsqlDbType.Integer, query.Offset, "offset"));
            }

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>The streams with the most recent activity, for the overview panel.</summary>
    /// <param name="table">The discovered event tables.</param>
    /// <param name="take">How many streams to return.</param>
    /// <param name="tenantId">The scope's tenant, when the event store is conjoined-tenanted.</param>
    /// <param name="commandTimeout">A bound on how long the panel may cost, when the caller sets one.</param>
    /// <remarks>
    /// <para>
    /// <b>This reads <c>mt_streams</c>, and deliberately does not aggregate <c>mt_events</c>.</b> The
    /// obvious query — <c>group by stream_id order by max(seq_id) desc limit n</c> — is an aggregate over
    /// every event the store has ever appended, on a dashboard panel that refreshes on a timer, which is
    /// D8's argument against <c>count(*)</c> made a second time and ignored. <c>mt_streams</c> holds one
    /// row per stream already, so the same panel is a top-n with a <c>limit</c> instead of a full scan and
    /// a hash aggregate.
    /// </para>
    /// <para>
    /// What that <c>timestamp</c> means depends on the store's append mode, verified against Marten 9.35
    /// (2026-09-14). Under the default <c>EventAppendMode.Rich</c> the version
    /// bump is <c>update … mt_streams set version = $1 where id = $2 and version = $3 returning version</c>
    /// and never touches <c>timestamp</c>, so the column keeps its <c>default now()</c> from stream
    /// creation; under <c>Quick</c> and <c>QuickWithServerTimestamps</c> the append goes through
    /// <c>mt_quick_append_events</c>, whose body does <c>set version = event_version, timestamp =
    /// now()</c>. So this is "most recently started" on a Rich store and "most recently appended to" on a
    /// Quick one. The panel this feeds wants the cheap answer either way;
    /// <see cref="BuildStreamsByRecentActivity" /> is the expensive one that is exact on both.
    /// </para>
    /// </remarks>
    public static NpgsqlCommand BuildRecentlyActiveStreams(
        EventTableInfo table,
        int take,
        string? tenantId = null,
        TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        var command = new NpgsqlCommand();

        try
        {
            var parameters = new ParameterBuilder(command);
            var sql = new StringBuilder();
            var first = true;

            sql.Append("select ");

            foreach (var column in StreamColumns)
            {
                if (!table.HasStreamColumn(column))
                {
                    continue;
                }

                sql.Append(first ? string.Empty : ",\n       ").Append(Column(StreamAlias, column));
                first = false;
            }

            sql.Append('\n');
            sql.Append("from ").Append(table.QualifiedStreams).Append(AsStreamAlias).Append('\n');

            if (table.HasStreamColumn("tenant_id"))
            {
                sql.Append("where (").Append(parameters.AddNullable(NpgsqlDbType.Varchar, tenantId, "tenant"))
                    .Append(" is null or ").Append(Column(StreamAlias, "tenant_id")).Append(" = @tenant)\n");
            }

            // mt_streams."timestamp" is NOT NULL in every Marten schema, so there is nothing for a
            // `nulls last` to rescue here and the id is only a tiebreaker for streams appended in the same
            // transaction, which share a timestamp exactly.
            sql.Append("order by ").Append(Column(StreamAlias, "timestamp")).Append(" desc, ")
                .Append(Column(StreamAlias, "id")).Append('\n');
            sql.Append("limit ").Append(parameters.Add(NpgsqlDbType.Integer, Math.Clamp(take, 1, 1000), "limit"));

            command.CommandText = sql.ToString();
            ApplyTimeout(command, commandTimeout);

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The stream list ordered by the sequence of the last event appended to each stream, rather than by
    /// the streams table's own timestamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two orderings answer two different questions. <see cref="BuildStreams" /> keysets on
    /// <c>(timestamp desc, id)</c>, which is a total order over an indexed column and is what paging
    /// through every stream must use. "Recently active" is the <c>max(seq_id) group by stream_id</c>
    /// aggregate joined back to <c>mt_streams</c>: it is the truth about which stream was appended to
    /// last, and it costs an aggregate over <c>mt_events</c>.
    /// </para>
    /// <para>
    /// The aggregate earns its cost because the cheap column does not answer the question. Under Marten's
    /// default <c>EventAppendMode.Rich</c> the per-append statement is
    /// <c>update … mt_streams set version = $1 where id = $2 and version = $3 returning version</c> —
    /// <c>timestamp</c> is not in the <c>set</c> list, so it stays at the <c>default now()</c> the row was
    /// created with (verified against Marten 9.35, 2026-09-14; only the <c>Quick</c> modes' PL/pgSQL
    /// <c>mt_quick_append_events</c> does <c>timestamp = now()</c> on append). On a Rich store, which is
    /// most of them, ordering by <c>mt_streams.timestamp</c> would list the newest streams and call it
    /// activity. <see cref="BuildRecentlyActiveStreams" /> takes that cheap approximation deliberately,
    /// because it feeds a panel on a refresh timer; this one is the opt-in on a page the visitor asked
    /// for.
    /// </para>
    /// <para>
    /// Because the aggregate has no usable keyset, this one pages by offset, under the same
    /// <see cref="DocumentQueryBuilder.MaxOffset" /> cap as every other offset walk in the studio. That is
    /// the deliberate trade: the expensive ordering is also the bounded one.
    /// </para>
    /// </remarks>
    public static NpgsqlCommand BuildStreamsByRecentActivity(EventTableInfo table, StreamListQuery query)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);

        if (query.Offset is < 0 or > DocumentQueryBuilder.MaxOffset)
        {
            throw new ArgumentException(
                $"An offset must be between 0 and {DocumentQueryBuilder.MaxOffset.ToString(CultureInfo.InvariantCulture)}.",
                nameof(query));
        }

        var command = new NpgsqlCommand();

        try
        {
            var parameters = new ParameterBuilder(command);
            var sql = new StringBuilder();
            var first = true;

            sql.Append("select ");

            foreach (var column in StreamColumns)
            {
                if (!table.HasStreamColumn(column))
                {
                    continue;
                }

                sql.Append(first ? string.Empty : ",\n       ").Append(Column(StreamAlias, column));
                first = false;
            }

            sql.Append('\n');
            sql.Append("from ").Append(table.QualifiedStreams).Append(AsStreamAlias).Append('\n');
            sql.Append("join (select ").Append(Column(Alias, "stream_id")).Append(" as stream_id, max(")
                .Append(Column(Alias, "seq_id")).Append(") as last_seq\n")
                .Append("      from ").Append(table.QualifiedEvents).Append(AsAlias).Append('\n')
                .Append("      group by ").Append(Column(Alias, "stream_id")).Append(") as a\n")
                .Append("  on a.stream_id = ").Append(Column(StreamAlias, "id")).Append('\n');

            AppendStreamFilters(sql, parameters, table, query);

            sql.Append("order by a.last_seq desc\n");
            sql.Append("limit ").Append(parameters.Add(
                NpgsqlDbType.Integer, Math.Clamp(query.PageSize, 1, StreamListQuery.MaxPageSize), "limit"));
            sql.Append(" offset ").Append(parameters.Add(NpgsqlDbType.Integer, query.Offset, "offset"));

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>One event by its sequence, for the dead-letter expansion.</summary>
    public static NpgsqlCommand BuildEventBySequence(EventTableInfo table, long sequence)
    {
        ArgumentNullException.ThrowIfNull(table);

        var command = new NpgsqlCommand();

        try
        {
            var parameters = new ParameterBuilder(command);
            var sql = new StringBuilder();

            AppendEventSelect(sql, table);
            sql.Append("from ").Append(table.QualifiedEvents).Append(AsAlias).Append('\n');
            sql.Append("where ").Append(Column(Alias, "seq_id")).Append(" = ")
                .Append(parameters.Add(NpgsqlDbType.Bigint, sequence, "seq"));

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>How many events there are of each type, and the sequence range each covers.</summary>
    /// <param name="table">The discovered event tables.</param>
    /// <param name="tenantId">The scope's tenant, when the event store is conjoined-tenanted.</param>
    /// <param name="commandTimeout">A bound on how long the aggregate may cost, when the caller sets one.</param>
    /// <remarks>
    /// This one genuinely is an aggregate over <c>mt_events</c> — there is no other place the per-type
    /// counts live — which is exactly why it takes a timeout. A caller that puts it on a refreshing panel
    /// without one has written a scheduled sequential scan.
    /// </remarks>
    public static NpgsqlCommand BuildEventTypeCounts(
        EventTableInfo table,
        string? tenantId = null,
        TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        var command = new NpgsqlCommand();

        try
        {
            var parameters = new ParameterBuilder(command);
            var sql = new StringBuilder();

            sql.Append("select ").Append(Column(Alias, "type")).Append(", count(*) as event_count, ")
                .Append("min(").Append(Column(Alias, "seq_id")).Append(") as first_seq, ")
                .Append("max(").Append(Column(Alias, "seq_id")).Append(") as last_seq\n");
            sql.Append("from ").Append(table.QualifiedEvents).Append(AsAlias).Append('\n');

            if (table.HasTenantId)
            {
                sql.Append("where (").Append(parameters.AddNullable(NpgsqlDbType.Varchar, tenantId, "tenant"))
                    .Append(" is null or ").Append(Column(Alias, "tenant_id")).Append(" = @tenant)\n");
            }

            sql.Append("group by ").Append(Column(Alias, "type")).Append('\n');
            sql.Append("order by 2 desc, 1");

            command.CommandText = sql.ToString();
            ApplyTimeout(command, commandTimeout);

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// How many dead letters there are. A plain count of Marten's own document table, which is the one
    /// place the studio counts a document table by name rather than through a mapping.
    /// </summary>
    /// <param name="schema">The event store's schema.</param>
    /// <param name="tenantId">
    /// The scope's tenant. Unlike the other builders there is no <see cref="EventTableInfo"/> here to ask
    /// whether <c>tenant_id</c> exists, so the predicate is emitted only when a tenant is actually given —
    /// passing one is the caller's assertion that the event store is conjoined-tenanted.
    /// </param>
    /// <param name="commandTimeout">A bound on how long the count may cost, when the caller sets one.</param>
    public static NpgsqlCommand BuildDeadLetterCount(
        string schema,
        string? tenantId = null,
        TimeSpan? commandTimeout = null)
    {
        var command = new NpgsqlCommand();

        try
        {
            var sql = "select count(*) from " + SqlIdentifier.Qualify(schema, EventTableInfo.DeadLetterTable);

            if (tenantId is not null)
            {
                sql += " where " + SqlIdentifier.Quote("tenant_id") + " = @tenant";
                command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = tenantId });
            }

            command.CommandText = sql;
            ApplyTimeout(command, commandTimeout);

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The highest event sequence. A <em>fallback</em>: the services normally ask Marten with
    /// <c>IMartenDatabase.FetchHighestEventSequenceNumber</c>, which is the supported route.
    /// </summary>
    public static NpgsqlCommand BuildHighestSequence(EventTableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return new NpgsqlCommand(
            "select max(" + Column(Alias, "seq_id") + ") from " + table.QualifiedEvents + " as " + Alias);
    }

    /// <summary>
    /// The <c>where</c> block both stream listings share: id, aggregate-type prefix, tenant and the
    /// archived flag, each a parameter that may be null so that one statement serves every filter
    /// combination.
    /// </summary>
    /// <remarks>
    /// The id is the one filter that is not simply nullable: an id this store's stream identity cannot
    /// hold is a filter that matches nothing, and it has to be emitted as a <c>false</c> predicate rather
    /// than as a null parameter, which would read as "no filter at all". Same rule as the feed.
    /// </remarks>
    private static void AppendStreamFilters(
        StringBuilder sql,
        ParameterBuilder parameters,
        EventTableInfo table,
        StreamListQuery query)
    {
        var streamId = ParseStream(table, query.StreamId);

        if (streamId.IsImpossible)
        {
            sql.Append("where false\n");
        }
        else
        {
            sql.Append("where (").Append(parameters.AddNullable(table.StreamIdDbType, streamId.Value, "id"))
                .Append(" is null or ").Append(Column(StreamAlias, "id")).Append(" = @id)\n");
        }

        sql.Append("  and (").Append(parameters.AddNullable(
                NpgsqlDbType.Varchar,
                query.TypePrefix is null ? null : EscapeLikePrefix(query.TypePrefix),
                "typePrefix"))
            .Append(" is null or ").Append(Column(StreamAlias, "type")).Append(" like @typePrefix)\n");

        if (table.HasStreamColumn("tenant_id"))
        {
            sql.Append("  and (").Append(parameters.AddNullable(NpgsqlDbType.Varchar, query.TenantId, "tenant"))
                .Append(" is null or ").Append(Column(StreamAlias, "tenant_id")).Append(" = @tenant)\n");
        }

        if (table.HasStreamColumn("is_archived"))
        {
            sql.Append("  and (").Append(parameters.Add(NpgsqlDbType.Boolean, query.IncludeArchived, "includeArchived"))
                .Append(" or ").Append(Column(StreamAlias, "is_archived")).Append(" = false)\n");
        }
    }

    /// <summary>
    /// The type filter as an array parameter, or <see langword="null" /> when nothing was ticked. Blank
    /// entries are dropped: an empty string is not an event type and would silently match nothing.
    /// </summary>
    private static string[]? NormalizeTypes(IReadOnlyList<string>? types)
    {
        if (types is null || types.Count == 0)
        {
            return null;
        }

        List<string> kept = [];
        foreach (var type in types)
        {
            if (!string.IsNullOrWhiteSpace(type))
            {
                kept.Add(type.Trim());
            }
        }

        return kept.Count == 0 ? null : [.. kept];
    }

    private static void AppendEventSelect(StringBuilder sql, EventTableInfo table)
    {
        sql.Append("select ");

        var first = true;

        foreach (var column in CoreEventColumns)
        {
            sql.Append(first ? string.Empty : ",\n       ").Append(Column(Alias, column));
            first = false;
        }

        foreach (var column in OptionalEventColumns)
        {
            if (table.HasEventColumn(column))
            {
                sql.Append(",\n       ").Append(Column(Alias, column));
            }
        }

        sql.Append(",\n       ").Append(Column(Alias, "data")).Append("::text as data");

        if (table.HasBinaryData)
        {
            // Never the column, only whether there is one and how big it is.
            sql.Append(",\n       (").Append(Column(Alias, "bdata")).Append(" is not null) as has_binary");
            sql.Append(",\n       octet_length(").Append(Column(Alias, "bdata")).Append(") as binary_length");
        }

        sql.Append('\n');
    }

    /// <summary>
    /// A stream-id filter in three states, because two of them are not the same answer.
    /// </summary>
    /// <param name="Value">The parsed id, when there is one.</param>
    /// <param name="IsImpossible">
    /// Whether the caller asked for an id this store's stream identity cannot hold — a non-GUID on a
    /// GUID-identified store. Such a filter matches nothing, and the builder emits a <c>false</c> predicate
    /// rather than a null parameter, which would have read as "no filter at all".
    /// </param>
    private readonly record struct StreamIdFilter(object? Value, bool IsImpossible)
    {
        public static StreamIdFilter NotAsked { get; } = new(null, false);

        public static StreamIdFilter Impossible { get; } = new(null, true);
    }

    private static StreamIdFilter ParseStream(EventTableInfo table, string? rawStreamId)
    {
        if (string.IsNullOrWhiteSpace(rawStreamId))
        {
            return StreamIdFilter.NotAsked;
        }

        var parsed = table.ParseStreamId(rawStreamId);

        // A filter by an id that cannot exist matches nothing, which is what the caller asked for. An
        // unparseable GUID filter is therefore not an error here, it is an empty page - but it has to be
        // built as an empty page, and not as the absence of a filter.
        return parsed.Success ? new StreamIdFilter(parsed.Value, false) : StreamIdFilter.Impossible;
    }

    /// <summary>Escapes LIKE metacharacters and anchors the pattern at the start.</summary>
    internal static string EscapeLikePrefix(string prefix)
    {
        var escaped = new StringBuilder(prefix.Length + 1);

        foreach (var c in prefix)
        {
            if (c is '\\' or '%' or '_')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.Append('%').ToString();
    }

    private static string Column(string alias, string name) => alias + "." + SqlIdentifier.Quote(name);

    /// <summary>
    /// Puts the caller's budget on the command, rounded up to whole seconds because that is the only unit
    /// <c>NpgsqlCommand.CommandTimeout</c> has — and never to zero, which is Npgsql for "wait forever" and
    /// is the opposite of what anyone passing a timeout meant.
    /// </summary>
    private static void ApplyTimeout(NpgsqlCommand command, TimeSpan? commandTimeout)
    {
        if (commandTimeout is { } timeout)
        {
            command.CommandTimeout = Math.Max((int)Math.Ceiling(timeout.TotalSeconds), 1);
        }
    }

    private sealed class ParameterBuilder(NpgsqlCommand command)
    {
        public string Add(NpgsqlDbType type, object value, string name)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });

            return "@" + name;
        }

        /// <summary>
        /// Adds a parameter that may be null. The type is always stated, because <c>@p is null</c> against
        /// an untyped parameter is a question Postgres cannot answer.
        /// </summary>
        public string AddNullable(NpgsqlDbType type, object? value, string name)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

            return "@" + name;
        }
    }
}
