using System.Text;
using System.Text.RegularExpressions;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>What the aggregate replay reads from <c>mt_events</c>.</summary>
internal sealed record ReplayEventsQuery
{
    /// <summary>
    /// The columns Marten's own event selector reads, in the order it reads them: <c>IEventStorage.SelectFields()</c>,
    /// passed through as Marten built it, because <c>ResolveAsync</c> reads the row by ordinal.
    /// </summary>
    public required IReadOnlyList<string> SelectFields { get; init; }

    /// <summary>The stream, already parsed against the store's stream identity: a <see cref="Guid" /> or a string.</summary>
    public required object StreamId { get; init; }

    /// <summary>The last version to read; zero for the whole stream.</summary>
    public long Version { get; init; }

    /// <summary>
    /// The tenant the session would have filtered on, on a conjoined event store; <see langword="null" /> on any other.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>Whether events the daemon was told to skip are left out, as <c>EnableEventSkippingInProjectionsOrSubscriptions</c> says.</summary>
    public bool IgnoreSkipped { get; init; }
}

/// <summary>
/// The two stream reads that used to be Marten's event-store calls: a stream's events for the aggregate replay, and
/// whether a stream exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>session.Events</c>.</b> <c>QueryEventStore.FetchStreamAsync</c> - which <c>AggregateStreamAsync</c>
/// goes through - opens with <c>EnsureStorageExistsAsync(typeof(IEvent))</c>, and <c>FetchStreamStateAsync</c> with
/// <c>EnsureStorageExistsAsync(typeof(StreamAction))</c> (read at <c>V9.39.0</c> and <c>V9.50.2</c>, identical). Under the
/// database's own <c>AutoCreate</c> that is Weasel's <c>ensureStorageExistsAsync</c>: the event store's dependent
/// features (the dead-letter table among them), <c>AllObjects()</c> for post-processing - <c>mt_hilo</c> on a store with
/// a HiLo id, hard rule 14 - and the event store's own migration, the first time the process asks. A replay or a
/// document's "open stream" link that does that is a studio writing DDL because somebody opened a page.
/// </para>
/// <para>
/// <b>The replay keeps Marten's reading of a row.</b> Only the <c>from</c>, <c>where</c> and <c>order by</c> are
/// the studio's: the select list is <c>IEventStorage.SelectFields()</c>, and the caller hands each row to that same
/// storage's <c>ResolveAsync</c>, which is what applies upcasters, binary payloads and every metadata column the
/// store configured. So <c>bdata</c> is selected here, unlike every other event read the studio makes - the selector
/// needs it at its ordinal to deserialize a binary event, and the row is folded into the aggregate on the server and
/// never sent to the browser.
/// </para>
/// </remarks>
internal static partial class StreamReadQueries
{
    private const string Alias = EventTableInfo.EventsAlias;
    private const string StreamAlias = EventTableInfo.StreamsAlias;

    /// <summary>
    /// The columns of <c>mt_events</c> the replay reads that the table does not have - empty when it can run.
    /// </summary>
    /// <remarks>
    /// A column Marten's configuration selects and the table lacks is a pending migration: Marten would have added it
    /// on its first event read, and the studio does not. The caller says so instead of sending a statement that can
    /// only fail with <c>42703</c>.
    /// </remarks>
    public static IReadOnlyList<string> MissingColumns(EventTableInfo table, ReplayEventsQuery query)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);

        List<string> missing = [];

        foreach (string column in ReadColumns(query))
        {
            if (!table.HasEventColumn(column) && !missing.Contains(column, StringComparer.Ordinal))
            {
                missing.Add(column);
            }
        }

        return missing;
    }

    /// <summary>One stream's events for the replay, in sequence order - Marten's <c>EventStatement</c>, read at both tags.</summary>
    /// <param name="table">The event tables as the catalog describes them.</param>
    /// <param name="query">What to read.</param>
    /// <remarks>
    /// <para>
    /// The filters are <c>EventStatement</c>'s, for the properties <c>FetchStreamAsync</c> sets: <c>is_archived = false</c>
    /// always (<c>IsNotArchivedFilter</c>), <c>is_skipped = false</c> when skipping is enabled, <c>version &lt;=</c> for a
    /// version other than zero, <c>tenant_id =</c> the session's tenant on a conjoined event store, the stream - and
    /// <c>order by seq_id</c>. One difference, on purpose: <c>EventStatement</c> leaves the stream predicate out for an
    /// empty GUID or an empty key, which reads the whole log; this one always emits it.
    /// </para>
    /// <para>
    /// No row limit: the aggregate needs every event up to the version. The caller's command timeout is the bound,
    /// as the session's <c>Timeout</c> was.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">A select field is not a plain column name the builder can quote.</exception>
    public static NpgsqlCommand BuildReplayEvents(EventTableInfo table, ReplayEventsQuery query)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);

        if (query.SelectFields.Count == 0)
        {
            throw new InvalidOperationException("Marten's event selector named no columns to read.");
        }

        var command = new NpgsqlCommand();

        try
        {
            var sql = new StringBuilder("select ");

            for (int index = 0; index < query.SelectFields.Count; index++)
            {
                string field = query.SelectFields[index];

                // Marten's EventDocumentStorage builds the list from EventsTable column names. Anything else - an
                // expression, a qualified name - is a select list the studio cannot reproduce by quoting, and replaying
                // through a guess would hand Marten's selector the wrong ordinals.
                if (!PlainColumn().IsMatch(field))
                {
                    throw new InvalidOperationException(
                        $"Marten's event selector reads '{field}', which is not a plain column name, so the studio cannot read the stream the way Marten would.");
                }

                sql.Append(index == 0 ? string.Empty : ", ").Append(Column(Alias, field));
            }

            sql.Append('\n');
            sql.Append("from ").Append(table.QualifiedEvents).Append(" as " + Alias + "\n");
            sql.Append("where ").Append(Column(Alias, "stream_id")).Append(" = ")
                .Append(Add(command, "stream", table.StreamIdDbType, query.StreamId)).Append('\n');
            sql.Append("  and ").Append(Column(Alias, "is_archived")).Append(" = false\n");

            if (query.IgnoreSkipped)
            {
                sql.Append("  and ").Append(Column(Alias, "is_skipped")).Append(" = false\n");
            }

            if (query.Version != 0)
            {
                sql.Append("  and ").Append(Column(Alias, "version")).Append(" <= ")
                    .Append(Add(command, "version", NpgsqlDbType.Bigint, query.Version)).Append('\n');
            }

            if (query.TenantId is { } tenant)
            {
                sql.Append("  and ").Append(Column(Alias, "tenant_id")).Append(" = ")
                    .Append(Add(command, "tenant", NpgsqlDbType.Varchar, tenant)).Append('\n');
            }

            sql.Append("order by ").Append(Column(Alias, "seq_id"));

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>Whether one stream exists in the scope - archived or not - as the stream page would find it.</summary>
    /// <param name="table">The event tables as the catalog describes them; <c>mt_streams</c> must exist.</param>
    /// <param name="streamId">The stream, already parsed against the store's stream identity.</param>
    /// <param name="tenantId">The scope's tenant, or <see langword="null" /> for every tenant.</param>
    /// <remarks>
    /// The same tenant predicate as the stream list the link leads to (<see cref="EventQueryBuilder.BuildStreams" />),
    /// so a link is offered exactly when the page it opens will find the stream. Returns one row or none.
    /// </remarks>
    public static NpgsqlCommand BuildStreamExists(EventTableInfo table, object streamId, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(streamId);

        var command = new NpgsqlCommand();

        try
        {
            var sql = new StringBuilder("select 1\n");
            sql.Append("from ").Append(table.QualifiedStreams).Append(" as " + StreamAlias + "\n");
            sql.Append("where ").Append(Column(StreamAlias, "id")).Append(" = ")
                .Append(Add(command, "id", table.StreamIdDbType, streamId)).Append('\n');

            if (table.HasStreamColumn("tenant_id"))
            {
                command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = (object?) tenantId ?? DBNull.Value });
                sql.Append("  and (@tenant is null or ").Append(Column(StreamAlias, "tenant_id")).Append(" = @tenant)\n");
            }

            sql.Append("limit 1");

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>Every <c>mt_events</c> column the replay's statement names: the select list, then the filters it will emit.</summary>
    private static IEnumerable<string> ReadColumns(ReplayEventsQuery query)
    {
        foreach (string field in query.SelectFields)
        {
            yield return field;
        }

        yield return "stream_id";
        yield return "is_archived";
        yield return "seq_id";

        if (query.IgnoreSkipped)
        {
            yield return "is_skipped";
        }

        if (query.Version != 0)
        {
            yield return "version";
        }

        if (query.TenantId is not null)
        {
            yield return "tenant_id";
        }
    }

    private static string Column(string alias, string name) => alias + "." + SqlIdentifier.Quote(name);

    private static string Add(NpgsqlCommand command, string name, NpgsqlDbType type, object value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        return "@" + name;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlainColumn();
}
