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

    /// <summary>Whether archived events are included.</summary>
    public bool IncludeArchived { get; init; }

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

            sql.Append("  and (").Append(parameters.AddNullable(table.StreamIdDbType, streamId, "streamId"))
                .Append(" is null or ").Append(Column(Alias, "stream_id")).Append(" = @streamId)\n");

            sql.Append("  and (").Append(parameters.AddNullable(NpgsqlDbType.Varchar, query.EventType, "type"))
                .Append(" is null or ").Append(Column(Alias, "type")).Append(" = @type)\n");

            if (table.HasIsArchived)
            {
                sql.Append("  and (").Append(parameters.Add(NpgsqlDbType.Boolean, query.IncludeArchived, "includeArchived"))
                    .Append(" or ").Append(Column(Alias, "is_archived")).Append(" = false)\n");
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

            var streamId = ParseStream(table, query.StreamId);

            sql.Append("where (").Append(parameters.AddNullable(table.StreamIdDbType, streamId, "id"))
                .Append(" is null or ").Append(Column(StreamAlias, "id")).Append(" = @id)\n");

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
    public static NpgsqlCommand BuildRecentlyActiveStreams(EventTableInfo table, int take)
    {
        ArgumentNullException.ThrowIfNull(table);

        var command = new NpgsqlCommand();

        try
        {
            var parameters = new ParameterBuilder(command);

            command.CommandText =
                "select " + Column(Alias, "stream_id") + ", max(" + Column(Alias, "seq_id") + ") as last_seq\n" +
                "from " + table.QualifiedEvents + " as " + Alias + '\n' +
                "group by " + Column(Alias, "stream_id") + '\n' +
                "order by 2 desc\n" +
                "limit " + parameters.Add(NpgsqlDbType.Integer, Math.Clamp(take, 1, 1000), "limit");

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>How many events there are of each type, and the sequence range each covers.</summary>
    public static NpgsqlCommand BuildEventTypeCounts(EventTableInfo table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return new NpgsqlCommand(
            "select " + Column(Alias, "type") + ", count(*) as event_count, " +
            "min(" + Column(Alias, "seq_id") + ") as first_seq, max(" + Column(Alias, "seq_id") + ") as last_seq\n" +
            "from " + table.QualifiedEvents + " as " + Alias + '\n' +
            "group by " + Column(Alias, "type") + '\n' +
            "order by 2 desc, 1");
    }

    /// <summary>
    /// How many dead letters there are. A plain count of Marten's own document table, which is the one
    /// place the studio counts a document table by name rather than through a mapping.
    /// </summary>
    public static NpgsqlCommand BuildDeadLetterCount(string schema) =>
        new("select count(*) from " + SqlIdentifier.Qualify(schema, EventTableInfo.DeadLetterTable));

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

    private static object? ParseStream(EventTableInfo table, string? rawStreamId)
    {
        if (string.IsNullOrWhiteSpace(rawStreamId))
        {
            return null;
        }

        var parsed = table.ParseStreamId(rawStreamId);

        // A filter by an id that cannot exist matches nothing, which is what the caller asked for. An
        // unparseable GUID filter is therefore not an error here, it is an empty page.
        return parsed.Success ? parsed.Value : null;
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
