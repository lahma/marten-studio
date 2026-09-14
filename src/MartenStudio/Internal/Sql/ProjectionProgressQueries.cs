using System.Globalization;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// One row of <c>mt_event_progression</c>, exactly as the table holds it.
/// </summary>
/// <remarks>
/// A row rather than a <c>JasperFx.Events.Projections.ShardState</c>, because this layer builds and reads
/// SQL and nothing else: the mapping onto Marten's own type - including the rule that rebuilds a
/// <c>ShardFailure</c> out of four nullable columns - belongs to the service that renders it, next to the
/// rest of the studio's opinions about what a shard row means.
/// </remarks>
/// <param name="Name">The progression name, which is a <c>ShardName.Identity</c> for a real shard.</param>
/// <param name="Sequence"><c>last_seq_id</c> - how far this shard has processed.</param>
/// <param name="LastHeartbeat"><c>heartbeat</c>, or <see langword="null" /> without extended tracking.</param>
/// <param name="AgentStatus"><c>agent_status</c>, or <see langword="null" />.</param>
/// <param name="PauseReason"><c>pause_reason</c>, or <see langword="null" />.</param>
/// <param name="RunningOnNode"><c>running_on_node</c>, or <see langword="null" />.</param>
/// <param name="FailureCategory"><c>failure_category</c> - the presence flag for the other three.</param>
/// <param name="FailureEventSequence"><c>failure_event_sequence</c>, when one event can be blamed.</param>
/// <param name="FailureEventType"><c>failure_event_type</c>.</param>
/// <param name="FailureEventTenantId"><c>failure_event_tenant_id</c>.</param>
internal sealed record ProgressionRow(
    string Name,
    long Sequence,
    DateTimeOffset? LastHeartbeat = null,
    string? AgentStatus = null,
    string? PauseReason = null,
    int? RunningOnNode = null,
    string? FailureCategory = null,
    long? FailureEventSequence = null,
    string? FailureEventType = null,
    string? FailureEventTenantId = null);

/// <summary>
/// The event store's two progress reads - the projection progression rows and the high-water mark - as
/// the studio's own parameterised SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are not Marten calls.</b> <c>IMartenDatabase.AllProjectionProgress</c> (both overloads)
/// and <c>IMartenDatabase.FetchHighestEventSequenceNumber</c> each open with
/// <c>await EnsureStorageExistsAsync(typeof(IEvent), token)</c> - verified in Marten 9.35,
/// <c>src/Marten/Storage/MartenDatabase.EventStorage.cs</c> - which applies the event store's Weasel
/// migration under the database's own <c>AutoCreate</c> before a row is read. The projections screen, the
/// feed's follow tick, the Overview and the navigation badge all ask for those numbers on a timer, so
/// calling them is a studio that creates <c>mt_events</c>, <c>mt_streams</c>, <c>mt_events_sequence</c>
/// and <c>mt_event_progression</c> because somebody opened a tab, and applies any pending event-store
/// change on a database that already had them. AGENTS.md hard rule 14 forbids exactly that, so the reads
/// are re-expressed here against <c>IMartenDatabase.CreateConnection(ConnectionUsage.Read)</c> and the
/// absence of the tables becomes a value.
/// </para>
/// <para>
/// <b>They still answer the same numbers.</b> The progression statement is Marten's own
/// (<c>Marten.Events.Daemon.Progress.ProjectionProgressStatement</c>): the same table, the same ordering
/// of the same columns, the same <c>name &lt;&gt; all(...)</c> exclusion of the two high-water bookkeeping
/// rows, and the same trailing-substring tenant filter. The high-water read is Marten's own two-branch
/// statement, <c>select last_value from …mt_events_sequence</c> and - only on a store with
/// <c>UseTenantPartitionedEvents</c>, where the store-global sequence is never advanced -
/// <c>select coalesce(max(seq_id), 0) from …mt_events</c>.
/// </para>
/// <para>
/// <b>The select list is built from the physical columns</b> (<see cref="ColumnCatalog" />), not from the
/// two <c>StoreOptions</c> flags Marten branches on. A store whose <c>EnableExtendedProgressionTracking</c>
/// was switched on without the migration having been applied has the flag and not the columns, and
/// Marten's own read raises <c>42703</c> on it; the point of this packet is that a read path must not be
/// the thing that fixes that by migrating. What the table has is what is selected, and there is no
/// <c>select *</c> (hard rule 10).
/// </para>
/// </remarks>
internal static class ProjectionProgressQueries
{
    /// <summary>The table the async daemon records each shard's position in.</summary>
    public const string ProgressionTable = "mt_event_progression";

    /// <summary>
    /// The sequence <c>seq_id</c> is drawn from, and the thing Marten's high-water read reads.
    /// </summary>
    /// <remarks>
    /// A sequence, not a table - so it is absent from <c>information_schema.tables</c> and
    /// <see cref="ColumnCatalog.HasSequenceAsync" /> is what settles whether it exists.
    /// </remarks>
    public const string EventsSequence = "mt_events_sequence";

    /// <summary>The two columns every progression row has.</summary>
    private const string NameColumn = "name";

    /// <summary>The position column.</summary>
    private const string SequenceColumn = "last_seq_id";

    /// <summary>
    /// The extended-tracking columns, in the order Marten's own selector walks them.
    /// </summary>
    /// <remarks>
    /// <c>mode</c>, <c>rebuild_threshold</c> and <c>assigned_node</c> - the three
    /// <c>UseOptimizedProjectionRebuilds</c> columns - are deliberately absent: nothing the studio draws
    /// reads them, and a select list that names a column nobody renders is a row width paid for on every
    /// poll.
    /// </remarks>
    private static readonly string[] OptionalColumns =
    [
        "heartbeat",
        "agent_status",
        "pause_reason",
        "running_on_node",
        "failure_category",
        "failure_event_sequence",
        "failure_event_type",
        "failure_event_tenant_id",
    ];

    /// <summary>
    /// The two rows in <c>mt_event_progression</c> that are high-water bookkeeping and not shards.
    /// </summary>
    /// <remarks>
    /// <c>HighWaterAllocationFence.ProgressionName</c> and <c>HighWaterStuckGap.ProgressionName</c>
    /// (Marten 9.35, <c>Events/Daemon/HighWater/HighWaterStatisticsDetector.cs</c>), which Marten's own
    /// statement excludes in SQL. Both classes are <c>internal</c>, so the strings are repeated here
    /// rather than referenced. They are not the studio's defence against a bookkeeping row being rendered
    /// as a projection - <c>ProjectionDataService.IsBookkeepingRow</c> is, and it is the general rule
    /// rather than a list of names - they are here so that this statement returns the same rows Marten's
    /// does.
    /// </remarks>
    private static readonly string[] BookkeepingRowNames = ["HighWaterAllocationFence", "HighWaterStuckGap"];

    /// <summary>
    /// The progression rows of one database, with whichever of the optional columns the table has.
    /// </summary>
    /// <param name="schema">The event store's schema - <c>StoreOptions.Events.DatabaseSchemaName</c>.</param>
    /// <param name="physicalColumns">
    /// What <c>information_schema</c> says <c>mt_event_progression</c> has. An empty set is a caller bug:
    /// probe the table first and answer "no event store here" instead of building a statement against a
    /// table that does not exist.
    /// </param>
    /// <param name="tenantId">
    /// When set, only rows whose name ends in <c>:{tenantId}</c>. Marten's own filter, and the caller
    /// decides when it means anything - see <c>ProjectionDataService</c>.
    /// </param>
    /// <param name="commandTimeout">A bound on the read, from <c>MartenStudioOptions.QueryTimeout</c>.</param>
    public static NpgsqlCommand BuildProgressionRows(
        string schema,
        IReadOnlyCollection<string> physicalColumns,
        string? tenantId = null,
        TimeSpan? commandTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(physicalColumns);

        var command = new NpgsqlCommand();

        try
        {
            var sql = new System.Text.StringBuilder();

            sql.Append("select ").Append(SqlIdentifier.Quote(NameColumn))
                .Append(", ").Append(SqlIdentifier.Quote(SequenceColumn));

            foreach (var column in SelectedOptionalColumns(physicalColumns))
            {
                sql.Append(", ").Append(SqlIdentifier.Quote(column));
            }

            sql.Append("\nfrom ").Append(SqlIdentifier.Qualify(schema, ProgressionTable)).Append('\n');

            // The tenant suffix as a value and not a pattern. Marten changed this from `name like '%:' ||
            // tenant` for the same reason it is written this way here: `_` is both a legal tenant-id
            // character and a LIKE wildcard, so tenant `acme_corp` also matched `acmeXcorp`'s rows.
            sql.Append("where (@tenantSuffix is null or right(")
                .Append(SqlIdentifier.Quote(NameColumn))
                .Append(", char_length(@tenantSuffix)) = @tenantSuffix)\n");

            sql.Append("  and ").Append(SqlIdentifier.Quote(NameColumn)).Append(" <> all(@bookkeeping)\n");
            sql.Append("order by ").Append(SqlIdentifier.Quote(NameColumn));

            command.CommandText = sql.ToString();

            command.Parameters.Add(new NpgsqlParameter("tenantSuffix", NpgsqlDbType.Text)
            {
                Value = tenantId is { Length: > 0 } tenant ? ":" + tenant : DBNull.Value,
            });

            command.Parameters.Add(new NpgsqlParameter("bookkeeping", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = BookkeepingRowNames,
            });

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
    /// The highest assigned event sequence number, which every projection lag is measured against.
    /// </summary>
    /// <param name="schema">The event store's schema.</param>
    /// <param name="tenantPartitionedEvents">
    /// <c>StoreOptions.Events.UseTenantPartitionedEvents</c>. Marten reads <c>max(seq_id)</c> in that mode
    /// because each tenant's events draw their sequence from a partition sequence of their own and the
    /// store-global <c>mt_events_sequence</c> is never advanced - its <c>last_value</c> reads as 1.
    /// </param>
    /// <param name="commandTimeout">A bound on the read.</param>
    /// <remarks>
    /// <c>last_value</c> is not a count and not the daemon's high-water mark: a sequence hands numbers out
    /// before the transaction that took them commits, and burns them outright when it rolls back. That is
    /// the number Marten answers and therefore the number the studio answers, and the places that render
    /// it say what it is.
    /// </remarks>
    public static NpgsqlCommand BuildHighWaterMark(
        string schema,
        bool tenantPartitionedEvents,
        TimeSpan? commandTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        var command = new NpgsqlCommand();

        try
        {
            command.CommandText = tenantPartitionedEvents
                ? "select coalesce(max(" + SqlIdentifier.Quote("seq_id") + "), 0) from "
                  + SqlIdentifier.Qualify(schema, EventTableInfo.EventsTable)
                : "select last_value from " + SqlIdentifier.Qualify(schema, EventsSequence);

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
    /// The progression rows of one database, or an empty list when the table does not exist.
    /// </summary>
    /// <param name="connection">An open read connection to the database in scope.</param>
    /// <param name="catalog">The shared, expiring <c>information_schema</c> catalog.</param>
    /// <param name="schema">The event store's schema.</param>
    /// <param name="tenantId">The tenant suffix filter, or <see langword="null" /> for every row.</param>
    /// <param name="commandTimeout">A bound on the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The rows, and whether <c>mt_event_progression</c> exists at all - which are different answers: a
    /// store whose daemon has never run has the table and no rows.
    /// </returns>
    public static async Task<ProgressionRows> ReadProgressionRowsAsync(
        NpgsqlConnection connection,
        ColumnCatalog catalog,
        string schema,
        string? tenantId,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(catalog);

        TableColumns table = await catalog
            .GetAsync(connection, schema, ProgressionTable, cancellationToken)
            .ConfigureAwait(false);

        if (!table.Exists)
        {
            return ProgressionRows.NoTable;
        }

        string[] columns = [.. table.Columns.Select(x => x.Name)];

        await using NpgsqlCommand command = BuildProgressionRows(schema, columns, tenantId, commandTimeout);
        command.Connection = connection;

        // Which optional columns were selected decides what the ordinals after the first two mean, so the
        // reader walks the same list the builder did rather than asking the reader for names.
        string[] selected = [.. SelectedOptionalColumns(columns)];

        List<ProgressionRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadRow(reader, selected));
        }

        return new ProgressionRows(rows, TableExists: true);
    }

    /// <summary>
    /// The high-water mark, or <see langword="null" /> when this database has no event store to read it
    /// from.
    /// </summary>
    /// <param name="connection">An open read connection to the database in scope.</param>
    /// <param name="catalog">The shared, expiring <c>information_schema</c> catalog.</param>
    /// <param name="schema">The event store's schema.</param>
    /// <param name="tenantPartitionedEvents"><c>StoreOptions.Events.UseTenantPartitionedEvents</c>.</param>
    /// <param name="commandTimeout">A bound on the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// The probe is of the object the statement actually reads, which is the sequence in the ordinary case
    /// and the table in the partitioned one. A store that has <c>mt_events</c> and no
    /// <c>mt_events_sequence</c> - or the reverse - is not a shape Marten creates, but it is a shape a
    /// half-applied migration leaves behind, and answering <see langword="null" /> for it is what keeps
    /// the page a page instead of a stack trace.
    /// </remarks>
    public static async Task<long?> ReadHighWaterMarkAsync(
        NpgsqlConnection connection,
        ColumnCatalog catalog,
        string schema,
        bool tenantPartitionedEvents,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(catalog);

        if (tenantPartitionedEvents)
        {
            TableColumns events = await catalog
                .GetAsync(connection, schema, EventTableInfo.EventsTable, cancellationToken)
                .ConfigureAwait(false);

            if (!events.Exists)
            {
                return null;
            }
        }
        else if (!await catalog.HasSequenceAsync(connection, schema, EventsSequence, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using NpgsqlCommand command = BuildHighWaterMark(schema, tenantPartitionedEvents, commandTimeout);
        command.Connection = connection;

        object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return scalar switch
        {
            null or DBNull => null,
            long value => value,
            _ => Convert.ToInt64(scalar, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The optional columns this table has, in select order.</summary>
    internal static IEnumerable<string> SelectedOptionalColumns(IReadOnlyCollection<string> physicalColumns) =>
        OptionalColumns.Where(x => physicalColumns.Contains(x, StringComparer.Ordinal));

    private static ProgressionRow ReadRow(NpgsqlDataReader reader, string[] selected)
    {
        var row = new ProgressionRow(reader.GetString(0), reader.GetInt64(1));

        for (var i = 0; i < selected.Length; i++)
        {
            var ordinal = i + 2;

            if (reader.IsDBNull(ordinal))
            {
                continue;
            }

            row = selected[i] switch
            {
                "heartbeat" => row with { LastHeartbeat = reader.GetFieldValue<DateTimeOffset>(ordinal) },
                "agent_status" => row with { AgentStatus = reader.GetString(ordinal) },
                "pause_reason" => row with { PauseReason = reader.GetString(ordinal) },
                "running_on_node" => row with { RunningOnNode = reader.GetInt32(ordinal) },
                "failure_category" => row with { FailureCategory = reader.GetString(ordinal) },
                "failure_event_sequence" => row with { FailureEventSequence = reader.GetInt64(ordinal) },
                "failure_event_type" => row with { FailureEventType = reader.GetString(ordinal) },
                "failure_event_tenant_id" => row with { FailureEventTenantId = reader.GetString(ordinal) },
                _ => row,
            };
        }

        return row;
    }

    /// <summary>
    /// Puts the caller's budget on the command, rounded up to whole seconds and never to zero, which is
    /// Npgsql for "wait forever".
    /// </summary>
    private static void ApplyTimeout(NpgsqlCommand command, TimeSpan? commandTimeout)
    {
        if (commandTimeout is { } timeout)
        {
            command.CommandTimeout = Math.Max((int) Math.Ceiling(timeout.TotalSeconds), 1);
        }
    }
}

/// <summary>
/// What one read of <c>mt_event_progression</c> found.
/// </summary>
/// <remarks>
/// "No table" and "no rows" are drawn differently: the first means this database has no event store at
/// all, the second means it has one and the daemon has never written to it.
/// </remarks>
/// <param name="Rows">The rows, in name order.</param>
/// <param name="TableExists">Whether <c>mt_event_progression</c> exists in this database.</param>
internal sealed record ProgressionRows(IReadOnlyList<ProgressionRow> Rows, bool TableExists)
{
    /// <summary>There is no progression table in this database.</summary>
    public static ProgressionRows NoTable { get; } = new([], false);
}
