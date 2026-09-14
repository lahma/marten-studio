using JasperFx.Events;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The shape of one store's event tables, discovered rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// Plan §4.4 and U8: the <c>mt_events</c> column set is conditional. <c>bdata</c>,
/// <c>correlation_id</c>, <c>causation_id</c>, <c>headers</c>, <c>user_name</c>, <c>is_skipped</c> and
/// <c>tags</c> each appear only when the store asked for them, so a select list written from Marten's
/// configuration alone would name columns the database does not have. Every one of them is therefore read
/// from <c>information_schema</c> through <see cref="ColumnCatalog"/> and recorded here.
/// </para>
/// <para>
/// <c>bdata</c> is present in this list only so that it is never selected: a binary event payload is shown
/// as "binary payload, N bytes", which needs <c>bdata is not null</c> and <c>octet_length(bdata)</c> and
/// not one byte of the column itself.
/// </para>
/// </remarks>
internal sealed record EventTableInfo
{
    /// <summary>The events table.</summary>
    public const string EventsTable = "mt_events";

    /// <summary>The streams table.</summary>
    public const string StreamsTable = "mt_streams";

    /// <summary>The alias every generated event query uses for <c>mt_events</c>.</summary>
    public const string EventsAlias = "e";

    /// <summary>The alias every generated stream query uses for <c>mt_streams</c>.</summary>
    public const string StreamsAlias = "s";

    /// <summary>The dead letter table, which is a document table Marten registers for itself.</summary>
    public const string DeadLetterTable = "mt_doc_deadletterevent";

    /// <summary>The schema the event tables live in.</summary>
    public required string Schema { get; init; }

    /// <summary>Whether stream ids are <c>uuid</c> or <c>varchar</c>, which types every stream parameter.</summary>
    public StreamIdentity StreamIdentity { get; init; } = StreamIdentity.AsGuid;

    /// <summary>The columns <c>mt_events</c> actually has.</summary>
    public IReadOnlyCollection<string> EventColumns { get; init; } = [];

    /// <summary>The columns <c>mt_streams</c> actually has.</summary>
    public IReadOnlyCollection<string> StreamColumns { get; init; } = [];

    /// <summary>Whether <c>mt_events</c> has this column.</summary>
    public bool HasEventColumn(string column) => Contains(EventColumns, column);

    /// <summary>Whether <c>mt_streams</c> has this column.</summary>
    public bool HasStreamColumn(string column) => Contains(StreamColumns, column);

    /// <summary>Whether events can carry a binary payload. The column itself is never selected.</summary>
    public bool HasBinaryData => HasEventColumn("bdata");

    /// <summary>Whether the store records a correlation id on events.</summary>
    public bool HasCorrelationId => HasEventColumn("correlation_id");

    /// <summary>Whether the store records a causation id on events.</summary>
    public bool HasCausationId => HasEventColumn("causation_id");

    /// <summary>Whether the store records event headers.</summary>
    public bool HasHeaders => HasEventColumn("headers");

    /// <summary>Whether the store records the user name that appended an event.</summary>
    public bool HasUserName => HasEventColumn("user_name");

    /// <summary>Whether the daemon's skipped-event marker column exists.</summary>
    public bool HasIsSkipped => HasEventColumn("is_skipped");

    /// <summary>Whether the store tags events.</summary>
    public bool HasTags => HasEventColumn("tags");

    /// <summary>Whether the event store is conjoined-tenanted.</summary>
    public bool HasTenantId => HasEventColumn("tenant_id");

    /// <summary>Whether events carry the .NET type name.</summary>
    public bool HasDotNetType => HasEventColumn("mt_dotnet_type");

    /// <summary>Whether events can be archived.</summary>
    public bool HasIsArchived => HasEventColumn("is_archived");

    /// <summary>The quoted, schema-qualified <c>mt_events</c>.</summary>
    public string QualifiedEvents => SqlIdentifier.Qualify(Schema, EventsTable);

    /// <summary>The quoted, schema-qualified <c>mt_streams</c>.</summary>
    public string QualifiedStreams => SqlIdentifier.Qualify(Schema, StreamsTable);

    /// <summary>The Npgsql type a stream id parameter is bound as.</summary>
    public NpgsqlDbType StreamIdDbType =>
        StreamIdentity == StreamIdentity.AsString ? NpgsqlDbType.Varchar : NpgsqlDbType.Uuid;

    /// <summary>Builds the description from what <see cref="ColumnCatalog"/> read.</summary>
    public static EventTableInfo FromColumns(
        string schema,
        StreamIdentity streamIdentity,
        TableColumns events,
        TableColumns streams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(streams);

        return new EventTableInfo
        {
            Schema = schema,
            StreamIdentity = streamIdentity,
            EventColumns = events.Columns.Select(x => x.Name).ToArray(),
            StreamColumns = streams.Columns.Select(x => x.Name).ToArray(),
        };
    }

    /// <summary>
    /// Parses a stream id against the store's stream identity, the same way a document id is parsed against
    /// its column type — and for the same reason: a half-pasted GUID is a message, not an exception.
    /// </summary>
    public IdParseResult ParseStreamId(string? rawId)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return IdParseResult.Failed("A stream id is required.");
        }

        var trimmed = rawId.Trim();

        if (StreamIdentity == StreamIdentity.AsString)
        {
            return IdParseResult.Ok(trimmed);
        }

        return Guid.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out var guid)
            ? IdParseResult.Ok(guid)
            : IdParseResult.Failed($"'{trimmed}' is not a GUID, and this store's streams are identified by GUID.");
    }

    private static bool Contains(IReadOnlyCollection<string> columns, string column)
    {
        foreach (var candidate in columns)
        {
            if (string.Equals(candidate, column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
