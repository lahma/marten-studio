using System.Data;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Services.Events;

// F4: DCB event tags. In a file of its own; the class's documentation is EventDataService.cs's.
internal sealed partial class EventDataService
{
    /// <inheritdoc />
    public async Task<EventTagTypeList> ListTagTypesAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            EventTagModel model = EventTagModel.From(resolved.Store.Options, options.Value.IsDocumentTypeVisible);
            if (model.Storage == EventTagStorage.None)
            {
                return EventTagTypeList.None;
            }

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            // reltuples counts every tenant's rows. On a conjoined store a visitor scoped to one tenant is
            // shown no whole-table estimate at all - the documents rail's rule, for the same reason.
            bool crossTenant = model.IsConjoined && resolved.TenantId is not null;

            CountEstimator estimator = Estimator();
            List<EventTagTypeInfo> types = [];

            if (model.Storage == EventTagStorage.HStore)
            {
                IReadOnlyList<PostgresIndex> eventIndexes = table.EventColumns.Count == 0
                    ? []
                    : await indexes.GetAsync(connection, table.Schema, EventTableInfo.EventsTable, cancellationToken).ConfigureAwait(false);

                (IndexVerdictLevel level, string reason) = EventTagIndexes.HStore(eventIndexes);

                foreach (EventTagTypeModel type in model.Types)
                {
                    // Inline tags have no table of their own to count, and counting the events that carry one
                    // would be a scan of mt_events: the column is what there is.
                    types.Add(new EventTagTypeInfo(
                        type.Name, type.Suffix, type.ValueTypeName, type.AggregateName,
                        EventTableInfo.EventsTable + "." + EventTagModel.HStoreColumn,
                        table.HasTags,
                        EventStoreCount.Unknown,
                        "Kept inline in mt_events.tags, so there is no per-type table to estimate.",
                        level,
                        reason));
                }
            }
            else
            {
                foreach (EventTagTypeModel type in model.Types)
                {
                    TableColumns columns = await catalog
                        .GetAsync(connection, table.Schema, type.Table, cancellationToken).ConfigureAwait(false);

                    if (!columns.Exists)
                    {
                        // Declared and never created: nothing to read, and nothing here will create it.
                        types.Add(new EventTagTypeInfo(
                            type.Name, type.Suffix, type.ValueTypeName, type.AggregateName, type.Table,
                            TableExists: false,
                            EventStoreCount.Unknown,
                            "The table is not in this database yet: Marten creates it with the event store's schema.",
                            IndexVerdictLevel.Red,
                            "There is no tag table to filter on."));
                        continue;
                    }

                    EventStoreCount rows = EventStoreCount.Unknown;
                    string? note = EventTagText.CrossTenantEstimateNote;

                    if (!crossTenant)
                    {
                        DocumentCount counted = await estimator
                            .EstimateOrCountAsync(connection, table.Schema, type.Table, cancellationToken).ConfigureAwait(false);

                        rows = ToEventStoreCount(counted);
                        note = counted.Reason;
                    }

                    IReadOnlyList<PostgresIndex> tagIndexes = await indexes
                        .GetAsync(connection, table.Schema, type.Table, cancellationToken).ConfigureAwait(false);

                    (IndexVerdictLevel level, string reason) = EventTagIndexes.TagTable(tagIndexes, type.Table);

                    types.Add(new EventTagTypeInfo(
                        type.Name, type.Suffix, type.ValueTypeName, type.AggregateName, type.Table,
                        TableExists: true, rows, note, level, reason));
                }
            }

            TableColumns version = await catalog
                .GetAsync(connection, table.Schema, EventTagModel.VersionTable, cancellationToken).ConfigureAwait(false);

            EventStoreCount versionRows = version.Exists && !crossTenant
                ? ToEventStoreCount(await estimator
                    .EstimateOrCountAsync(connection, table.Schema, EventTagModel.VersionTable, cancellationToken)
                    .ConfigureAwait(false))
                : EventStoreCount.Unknown;

            return new EventTagTypeList(model.Storage, types, version.Exists, versionRows, IsConjoined: model.IsConjoined);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventTagTypeList.Failed(Describe(scope, exception, "list the tag types"));
        }
    }

    /// <inheritdoc />
    public async Task<EventTagRead> GetEventTagsAsync(
        StudioScope scope,
        IReadOnlyList<long> sequences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sequences);

        if (sequences.Count == 0)
        {
            return EventTagRead.Empty;
        }

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            // Hstore tags arrive with the event row, so there is nothing to read separately.
            EventTagModel model = EventTagModel.From(resolved.Store.Options);
            if (model.Storage != EventTagStorage.TagTables)
            {
                return EventTagRead.Empty;
            }

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<EventTagTableRef> tables = await TagTablesAsync(connection, table, model, cancellationToken).ConfigureAwait(false);

            return tables.Count == 0
                ? EventTagRead.Empty
                : new EventTagRead(await ReadTagRowsAsync(connection, table, tables, sequences, resolved.TenantId, cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventTagRead.Failed(Describe(scope, exception, "read the events' tags"));
        }
    }

    /// <summary>
    /// The feed's tag filter, resolved against the store's tag types into a predicate the builder can emit,
    /// or the reason it cannot be.
    /// </summary>
    /// <remarks>
    /// A tag type this store does not register, and a value its type cannot hold, are answers - an empty
    /// page would read as "no event carries that tag", which is a different fact (JasperFx's own
    /// <c>RequireByTagName</c> refuses an unknown name for the same reason).
    /// </remarks>
    private async Task<(EventTagPredicate? Predicate, string? Error)> ResolveTagFilterAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        EventTableInfo table,
        EventTagFilter filter,
        CancellationToken cancellationToken)
    {
        EventTagModel model = EventTagModel.From(resolved.Store.Options);

        if (model.Storage == EventTagStorage.None)
        {
            return (null, "This store registers no DCB tag types, so there are no tags to filter on.");
        }

        EventTagTypeModel? type = model.Find(filter.TypeName);
        if (type is null)
        {
            return (null, $"This store registers no tag type called '{filter.TypeName}'. Registered: "
                + string.Join(", ", model.Types.Select(static x => x.Name + " (\"" + x.Suffix + "\")")) + ".");
        }

        if (!EventTagValues.TryParse(type.SimpleType, filter.Value, out object? value, out string? error))
        {
            return (null, error);
        }

        if (model.Storage == EventTagStorage.HStore)
        {
            return table.HasTags
                ? (new EventTagPredicate { HStoreKey = type.Suffix, Value = EventTagValues.ToHStoreText(value!) }, null)
                : (null, "mt_events has no tags column in this database yet, so no event carries a tag.");
        }

        TableColumns columns = await catalog.GetAsync(connection, table.Schema, type.Table, cancellationToken).ConfigureAwait(false);
        if (!columns.Exists)
        {
            return (null, $"{type.Table} is not in this database yet, so no event carries a {type.Name} tag.");
        }

        return (new EventTagPredicate
        {
            Table = type.Table,
            Value = value!,
            ValueType = EventTagPredicate.DbTypeFor(type.SimpleType),
            TableHasTenant = columns.Has("tenant_id"),
        }, null);
    }

    /// <summary>
    /// Puts a page's tags on its rows - when that is cheap - and says what happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hstore tags are already on the rows (<c>ReadEventsAsync</c> reads the column). Tag-table tags need a
    /// read of every tag table, and a tag table's key leads with <c>value</c>, so finding a page's tags is a
    /// scan of each one. That is paid with the page only while every tag table is one the studio would
    /// count exactly - <c>CountEstimator.MayCountExactlyAsync</c> against <c>ExactCountThreshold</c>, the
    /// rule the documents rail and the Overview's tiles already live by - and otherwise left for the
    /// visitor to ask for, once, with <see cref="GetEventTagsAsync" />.
    /// </para>
    /// <para>
    /// A failure here is a value too: the page is shown without its tags and says why, rather than losing
    /// the events because their decoration could not be read.
    /// </para>
    /// </remarks>
    private async Task<(List<EventRow> Rows, EventTagLoad Load)> AttachTagsAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        EventTableInfo table,
        List<EventRow> rows,
        CancellationToken cancellationToken)
    {
        EventTagModel model = EventTagModel.From(resolved.Store.Options);

        if (model.Storage == EventTagStorage.None)
        {
            return (rows, EventTagLoad.NotApplicable);
        }

        if (model.Storage == EventTagStorage.HStore || rows.Count == 0)
        {
            return (rows, EventTagLoad.Loaded);
        }

        try
        {
            IReadOnlyList<EventTagTableRef> tables = await TagTablesAsync(connection, table, model, cancellationToken).ConfigureAwait(false);
            if (tables.Count == 0)
            {
                return (rows, EventTagLoad.Loaded);
            }

            CountEstimator estimator = Estimator();

            foreach (EventTagTableRef tagTable in tables)
            {
                DocumentCount estimate = await estimator
                    .EstimateAsync(connection, table.Schema, tagTable.Table, cancellationToken).ConfigureAwait(false);

                if (!await estimator.MayCountExactlyAsync(connection, table.Schema, tagTable.Table, estimate, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return (rows, EventTagLoad.Deferred(EventTagText.Deferred(tagTable.Table, estimator.ExactCountThreshold)));
                }
            }

            List<EventTagRow> tags = await ReadTagRowsAsync(
                connection, table, tables, [.. rows.Select(static x => x.Sequence)], resolved.TenantId, cancellationToken)
                .ConfigureAwait(false);

            return (EventTagView.Merge(rows, tags), EventTagLoad.Loaded);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            EventDataError error = Describe(resolved.Scope, exception, "read the events' tags");
            return (rows, EventTagLoad.Failed(error.Display));
        }
    }

    /// <summary>The tag tables that exist in this database, with whether each carries a tenant.</summary>
    private async Task<IReadOnlyList<EventTagTableRef>> TagTablesAsync(
        NpgsqlConnection connection,
        EventTableInfo table,
        EventTagModel model,
        CancellationToken cancellationToken)
    {
        List<EventTagTableRef> tables = [];

        foreach (EventTagTypeModel type in model.Types)
        {
            TableColumns columns = await catalog.GetAsync(connection, table.Schema, type.Table, cancellationToken).ConfigureAwait(false);

            if (columns.Exists && columns.Has("seq_id") && columns.Has("value"))
            {
                tables.Add(new EventTagTableRef(type.Suffix, type.Table, columns.Has("tenant_id")));
            }
        }

        return tables;
    }

    /// <summary>One statement across every tag table, narrowed to the scope's tenant when it has one.</summary>
    private async Task<List<EventTagRow>> ReadTagRowsAsync(
        NpgsqlConnection connection,
        EventTableInfo table,
        IReadOnlyList<EventTagTableRef> tables,
        IReadOnlyCollection<long> sequences,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = EventTagQueries.BuildTagsForEvents(table.Schema, tables, sequences, tenantId);
        command.Connection = connection;
        command.CommandTimeout = CommandTimeoutSeconds;

        List<EventTagRow> rows = [];

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new EventTagRow(
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                new EventTag(reader.GetString(0), reader.IsDBNull(3) ? string.Empty : reader.GetString(3))));
        }

        return rows;
    }

    /// <summary>The estimator every tag read uses, bounded by the studio's own options.</summary>
    private CountEstimator Estimator() => new()
    {
        CommandTimeoutSeconds = CommandTimeoutSeconds,
        ExactCountThreshold = Math.Max(options.Value.ExactCountThreshold, 0),
    };

    /// <summary>A <see cref="DocumentCount" /> as an Events-area count: unknown, an estimate, or exact.</summary>
    private static EventStoreCount ToEventStoreCount(DocumentCount count) => count switch
    {
        { IsUnavailable: true } => EventStoreCount.Unknown,
        { IsEstimate: true } => EventStoreCount.Estimate(count.Value),
        _ => EventStoreCount.Exact(count.Value),
    };
}
