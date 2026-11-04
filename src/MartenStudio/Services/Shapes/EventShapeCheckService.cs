using System.Diagnostics;
using System.Globalization;

using Marten;
using Marten.Events;
using Marten.Exceptions;
using Marten.Internal;
using Marten.Services;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Events;
using MartenStudio.Services.Query;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

using Weasel.Storage;

using IEvent = JasperFx.Events.IEvent;
using JasperFxEvents = JasperFx.Events;

namespace MartenStudio.Services.Shapes;

/// <summary>
/// The events' shape check (D-SHAPE): reads a bounded sample of one event type in scope with the studio's own SQL, hands every row to
/// Marten's own event resolver - the one the daemon's loader and the stepper use, so upcasters, the alias and <c>mt_dotnet_type</c>
/// fallback and binary events behave as in a rebuild - and reports what a rebuild would meet: paths, kinds, counts and sequences,
/// never a value of an event and never an exception message.
/// </summary>
/// <remarks>
/// <para>
/// <b>A plain read under the store policy</b>, as the document check and the Event types page are: <c>ResolveAsync(scope, null)</c> on
/// every call, before the cache. <b>The name is validated before the cache and before any statement:</b> bounded in length, and one the
/// store registers or its events table holds (the Event types page's own list), so an arbitrary string never becomes a cache key or
/// reaches SQL.
/// </para>
/// <para>
/// Events are read only through <c>IEventStorage</c> as <c>ISelector&lt;IEvent&gt;</c>, from a query session opened with
/// <c>SessionOptions.ForDatabase</c> (hard rule 15) that is used for <c>EventStorage()</c> and runs nothing - never a LINQ terminal,
/// <c>session.Events</c>, <c>QueryRawEvents</c> or <c>FetchStreamAsync</c> (hard rule 14). For an alias with no mapping the resolver loads the
/// stored <c>mt_dotnet_type</c> and registers it in this process, as any event read in the host would; nothing reaches the database.
/// </para>
/// </remarks>
internal sealed class EventShapeCheckService : IEventShapeCheck
{
    private const string Kind = "events";

    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioScopeResolver resolver;
    private readonly ColumnCatalog columnCatalog;
    private readonly IndexCatalog indexCatalog;
    private readonly IEventDataService eventTypes;
    private readonly ShapeCheckCache cache;
    private readonly ILogger<EventShapeCheckService> logger;
    private readonly TimeProvider timeProvider;

    /// <summary>The service a host's container builds.</summary>
    public EventShapeCheckService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        IndexCatalog indexCatalog,
        IEventDataService eventTypes,
        ShapeCheckCache cache,
        ILogger<EventShapeCheckService> logger)
        : this(options, resolver, columnCatalog, indexCatalog, eventTypes, cache, logger, TimeProvider.System)
    {
    }

    internal EventShapeCheckService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        IndexCatalog indexCatalog,
        IEventDataService eventTypes,
        ShapeCheckCache cache,
        ILogger<EventShapeCheckService> logger,
        TimeProvider timeProvider)
    {
        this.options = options;
        this.resolver = resolver;
        this.columnCatalog = columnCatalog;
        this.indexCatalog = indexCatalog;
        this.eventTypes = eventTypes;
        this.cache = cache;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    /// <summary>The wall budget of one check; a test shortens it to prove the partial result.</summary>
    internal TimeSpan Budget { get; init; } = ShapeCheckLimits.Budget;

    /// <inheritdoc />
    public async Task<ShapeCheckResult> CheckAsync(StudioScope scope, string eventTypeName, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(eventTypeName);

        // The store policy, on every call, before the cache: a cached result is only ever handed to someone who has just passed it.
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        string shown = eventTypeName.Length <= ShapeCheckLimits.MaxSubjectLength
            ? eventTypeName
            : eventTypeName[..ShapeCheckLimits.MaxSubjectLength];

        if (!await IsKnownAsync(scope, resolved, eventTypeName, cancellationToken).ConfigureAwait(false))
        {
            return ShapeCheckResult.NotChecked(shown, ShapeCheckText.NoSuchEventType(shown), timeProvider.GetUtcNow());
        }

        return await cache
            .GetAsync(resolved, Kind, eventTypeName, force, token => RunAsync(resolved, eventTypeName, token), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="name" /> is an event type the page lists: bounded, and registered by the store or present in its events
    /// table. The second half is a listing the Event types page makes itself, kept five minutes per scope, and read only for a name the
    /// store does not register.
    /// </summary>
    private async Task<bool> IsKnownAsync(StudioScope scope, ResolvedScope resolved, string name, CancellationToken cancellationToken)
    {
        if (name.Length is 0 or > ShapeCheckLimits.MaxSubjectLength)
        {
            return false;
        }

        foreach (JasperFxEvents.IEventType registered in resolved.Store.Options.Events.AllKnownEventTypes())
        {
            if (string.Equals(registered.EventTypeName, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        try
        {
            IReadOnlySet<string> observed = await cache
                .EventTypeNamesAsync(
                    resolved,
                    async token =>
                    {
                        EventTypeList list = await eventTypes.ListEventTypesAsync(scope, withCounts: true, token).ConfigureAwait(false);

                        return list.Error is not null
                            ? throw new InvalidOperationException("The event types could not be listed.")
                            : new HashSet<string>(list.Types.Select(static x => x.Name), StringComparer.Ordinal);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return observed.Contains(name);
        }
        catch (InvalidOperationException)
        {
            // The listing failed: the name is not one the page can show either.
            return false;
        }
    }

    private async Task<ShapeCheckResult> RunAsync(ResolvedScope resolved, string eventType, CancellationToken shared)
    {
        long started = Stopwatch.GetTimestamp();
        RunState state = new();

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(shared);
        budget.CancelAfter(Budget);

        TimeSpan statementTimeout = options.Value.QueryTimeout < ShapeCheckLimits.StatementTimeout
            ? options.Value.QueryTimeout
            : ShapeCheckLimits.StatementTimeout;

        if (statementTimeout <= TimeSpan.Zero)
        {
            statementTimeout = TimeSpan.FromMilliseconds(1);
        }

        ShapeCheckResult result;

        try
        {
            result = await ReadAsync(resolved, eventType, state, statementTimeout, started, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = Partial(eventType, state, ShapeCheckText.BudgetRanOut(Budget), started);
        }
        catch (Exception exception) when (PostgresFailure.IsTimeout(exception))
        {
            result = Partial(eventType, state, ShapeCheckText.StatementTimedOut(statementTimeout), started);
        }
        catch (Exception exception) when (PostgresFailure.IsStudioBusy(exception))
        {
            result = Failed(eventType, state, "the studio's share of the database's connections is in use. Try again in a moment.", started);
        }
        catch (Exception exception) when (PostgresFailure.IsConnectionFailure(exception))
        {
            result = Failed(eventType, state, PostgresFailure.CouldNotConnect(exception), started);
        }
        catch (Exception exception)
        {
            // Never the exception's message or the exception: a Postgres error can quote a value.
            string reason = exception is PostgresException postgres ? "SQLSTATE " + postgres.SqlState : exception.GetType().Name;

            logger.LogWarning("Marten Studio could not check the shapes of events '{Subject}': {Reason}", StudioLogValue.Cap(eventType), reason);
            result = Failed(eventType, state, "PostgreSQL or the reader refused a step (" + reason + ").", started);
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.EventShapesChecked(
                StudioLogValue.Cap(eventType),
                result.Examined,
                result.Types.Sum(static x => x.Failures.Count + x.Members.Count + x.MoreFailures + x.MoreMembers),
                result.Coverage.ToString());
        }

        return result;
    }

    private async Task<ShapeCheckResult> ReadAsync(
        ResolvedScope resolved,
        string eventType,
        RunState state,
        TimeSpan statementTimeout,
        long started,
        CancellationToken token)
    {
        int commandTimeout = (int) Math.Ceiling(statementTimeout.TotalSeconds) + 5;
        IReadOnlyStoreOptions storeOptions = resolved.Store.Options;
        string schema = storeOptions.Events.DatabaseSchemaName;

        await using NpgsqlConnection connection = MartenStudioDatabase.CreateConnection(resolved.Database, ConnectionUsage.Read);
        await connection.OpenStudioAsync(token).ConfigureAwait(false);

        TableColumns events = await columnCatalog.GetAsync(connection, schema, EventTableInfo.EventsTable, token).ConfigureAwait(false);
        TableColumns streams = await columnCatalog.GetAsync(connection, schema, EventTableInfo.StreamsTable, token).ConfigureAwait(false);

        if (!events.Exists)
        {
            return ShapeCheckResult.NotChecked(eventType, ShapeCheckText.TableMissing, timeProvider.GetUtcNow());
        }

        EventTableInfo table = EventTableInfo.FromColumns(schema, storeOptions.Events.StreamIdentity, events, streams);
        bool skipping = storeOptions.Events.EnableEventSkippingInProjectionsOrSubscriptions;
        bool perTenantSequences = storeOptions.Events.UseTenantPartitionedEvents;

        // Marten's own resolver: a query session pinned to this database (and tenant), used for EventStorage() only.
        SessionOptions sessionOptions = resolved.TenantId is { } tenant
            ? SessionOptions.ForDatabase(tenant, resolved.Database)
            : SessionOptions.ForDatabase(resolved.Database);
        sessionOptions.Timeout = commandTimeout;

        await using IQuerySession session = resolved.Store.QuerySession(sessionOptions);
        IEventStorage storage = ((IMartenSession) session).EventStorage();
        IReadOnlyList<string> selectFields = storage.SelectFields();

        IReadOnlyList<string> missing = StreamReadQueries.MissingColumns(
            table,
            new ReplayEventsQuery { SelectFields = selectFields, StreamId = Guid.Empty, IgnoreSkipped = skipping, TenantId = resolved.TenantId });

        if (missing.Count > 0)
        {
            return ShapeCheckResult.NotChecked(eventType, ShapeCheckText.EventColumnsMissing(schema, missing), timeProvider.GetUtcNow());
        }

        bool typeIndex = await HasTypeIndexAsync(connection, schema, token).ConfigureAwait(false);

        if (table.HasIsArchived)
        {
            state.LeftOut.Add(ShapeCheckText.ArchivedLeftOut);
        }

        if (skipping && table.HasIsSkipped)
        {
            state.LeftOut.Add(ShapeCheckText.SkippedLeftOut);
        }

        (bool rebuildUnknown, bool daemonUnknown, bool rebuildSerialization, bool daemonSerialization) = storeOptions is StoreOptions concrete
            ? (concrete.Projections.RebuildErrors.SkipUnknownEvents,
                concrete.Projections.Errors.SkipUnknownEvents,
                concrete.Projections.RebuildErrors.SkipSerializationErrors,
                concrete.Projections.Errors.SkipSerializationErrors)
            : (false, true, false, true);

        ISerializer serializer = storeOptions.Serializer();
        ReadOnlySqlSession readOnly = new(new ReadOnlySqlOptions { StatementTimeout = statementTimeout });
        EventShapeQueries.EventScope scope = new(resolved.TenantId, skipping);

        await readOnly.InTransactionAsync(
                connection,
                async (transaction, cancel) =>
                {
                    await ReadSampleAsync(
                            connection, transaction, table, eventType, scope, storage, selectFields, serializer, typeIndex, perTenantSequences, state, commandTimeout, cancel)
                        .ConfigureAwait(false);

                    return 0;
                },
                token)
            .ConfigureAwait(false);

        return Finish(eventType, state, started, rebuildUnknown, daemonUnknown, rebuildSerialization, daemonSerialization);
    }

    private ShapeCheckResult Finish(
        string eventType,
        RunState state,
        long started,
        bool rebuildUnknown,
        bool daemonUnknown,
        bool rebuildSerialization,
        bool daemonSerialization)
    {
        IReadOnlyList<ShapeTypeResult> types = state.Aggregator.Build();
        List<string> rebuild = [];
        List<string> remedies = [];

        long? firstUnknown = FirstSequence(types, ShapeFailureKind.UnknownEventType);
        long? firstUnreadable = FirstSequence(types, ShapeFailureKind.Unreadable);

        if (HasKind(types, ShapeFailureKind.UnknownEventType))
        {
            rebuild.Add(ShapeCheckText.RebuildUnknown(firstUnknown, rebuildUnknown, daemonUnknown));
        }

        if (HasKind(types, ShapeFailureKind.Unreadable))
        {
            rebuild.Add(ShapeCheckText.RebuildSerialization(eventType, firstUnreadable, rebuildSerialization, daemonSerialization));
            remedies.Add(ShapeCheckText.UpcasterRemedy);
        }

        if (state.Remedy is not null)
        {
            remedies.Add(state.Remedy);
        }

        return new ShapeCheckResult(
            eventType,
            state.Complete ? ShapeCoverage.Complete : ShapeCoverage.Sampled,
            timeProvider.GetUtcNow(),
            Stopwatch.GetElapsedTime(started),
            state.Examined,
            state.TooLarge,
            state.Lowest,
            state.Highest,
            state.Spread,
            state.Reason,
            remedies.Count == 0 ? null : string.Join(" ", remedies),
            state.LeftOut,
            types,
            rebuild.Count == 0 ? null : string.Join(" ", rebuild),
            null,
            state.Statements);
    }

    private static bool HasKind(IReadOnlyList<ShapeTypeResult> types, ShapeFailureKind kind) =>
        types.Any(x => x.Failures.Any(f => f.Kind == kind) || (x.MoreFailures > 0 && kind == ShapeFailureKind.Unreadable));

    private static long? FirstSequence(IReadOnlyList<ShapeTypeResult> types, ShapeFailureKind kind)
    {
        long? first = null;

        foreach (ShapeFailureGroup failure in types.SelectMany(static x => x.Failures).Where(x => x.Kind == kind))
        {
            if (failure.FirstSequence is { } sequence && (first is null || sequence < first))
            {
                first = sequence;
            }
        }

        return first;
    }

    private async Task<bool> HasTypeIndexAsync(NpgsqlConnection connection, string schema, CancellationToken token)
    {
        IReadOnlyList<PostgresIndex> indexes = await indexCatalog.GetAsync(connection, schema, EventTableInfo.EventsTable, token).ConfigureAwait(false);

        return indexes.Any(static x => string.Equals(IndexAdvisor.LeadingColumn(x.Definition), "type", StringComparison.Ordinal));
    }

    private static async Task ReadSampleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EventTableInfo table,
        string eventType,
        EventShapeQueries.EventScope scope,
        IEventStorage storage,
        IReadOnlyList<string> selectFields,
        ISerializer serializer,
        bool typeIndex,
        bool perTenantSequences,
        RunState state,
        int commandTimeout,
        CancellationToken token)
    {
        // Phase 0: is the event type small enough to read whole?
        int size;

        using (NpgsqlCommand probe = EventShapeQueries.BuildSizeProbe(table, eventType, scope, ShapeCheckLimits.SampleSize + 1, commandTimeout))
        {
            Attach(probe, connection, transaction, state);
            size = Convert.ToInt32(await probe.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        state.Complete = size <= ShapeCheckLimits.SampleSize;

        // Phase 1: keys and stored sizes. Spread probes need an index leading with type, and a sequence one number line to interpolate on.
        bool spread = typeIndex && !(perTenantSequences && scope.TenantId is null && table.HasTenantId);
        int edge = spread ? ShapeCheckLimits.Edge : ShapeCheckLimits.EdgeWithoutTypeIndex;
        List<KeyRow> keys = [];

        using (NpgsqlCommand command = state.Complete
                   ? EventShapeQueries.BuildCompleteKeys(table, eventType, scope, ShapeCheckLimits.SampleSize, commandTimeout)
                   : EventShapeQueries.BuildEdgeKeys(table, eventType, scope, edge, commandTimeout))
        {
            Attach(command, connection, transaction, state);
            await ReadKeysAsync(command, keys, token).ConfigureAwait(false);
        }

        if (!state.Complete)
        {
            if (!typeIndex)
            {
                state.Reason = ShapeCheckText.NoTypeIndex;
                state.Remedy = ShapeCheckText.TypeIndexRemedy;
            }
            else if (!spread)
            {
                state.Reason = ShapeCheckText.EventsAllTenantsEdgesOnly;
            }
            else
            {
                long[] probes = Probes(keys);

                if (probes.Length > 0)
                {
                    using NpgsqlCommand command = EventShapeQueries.BuildSpreadKeys(table, eventType, scope, probes, commandTimeout);
                    Attach(command, connection, transaction, state);
                    await ReadKeysAsync(command, keys, token).ConfigureAwait(false);
                }
            }
        }

        // Oldest, then the spread, then newest; a key found twice keeps the first of those it was found by.
        Dictionary<(string?, long), int> parts = [];
        List<KeyRow> chosen = [];

        foreach (KeyRow key in keys.OrderBy(static x => PartOrder(x.Part)))
        {
            if (!parts.TryAdd((key.Tenant, key.Sequence), key.Part))
            {
                continue;
            }

            if (key.StoredBytes > ShapeCheckLimits.MaxItemBytes)
            {
                state.TooLarge++;
                continue;
            }

            chosen.Add(key);
        }

        if (chosen.Count == 0)
        {
            return;
        }

        // Phase 2: the chosen rows by key, handed to Marten's own resolver.
        using NpgsqlCommand rows = EventShapeQueries.BuildRows(
            table,
            eventType,
            scope,
            selectFields,
            perTenantSequences,
            [.. chosen.Select(static x => x.Sequence)],
            [.. chosen.Select(static x => x.Tenant)],
            commandTimeout);

        Attach(rows, connection, transaction, state);

        await using NpgsqlDataReader reader = await rows.ExecuteReaderAsync(token).ConfigureAwait(false);

        int sequenceOrdinal = reader.GetOrdinal("seq_id");
        int dotNetOrdinal = reader.GetOrdinal("mt_dotnet_type");
        int bdataOrdinal = reader.GetOrdinal("bdata");
        int shapeOrdinal = reader.GetOrdinal("shape_json");
        int streamOrdinal = reader.GetOrdinal("stream_id");
        int versionOrdinal = reader.GetOrdinal("version");
        int tenantOrdinal = table.HasTenantId ? reader.GetOrdinal("tenant_id") : -1;

        ISelector<IEvent> selector = storage;

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();

            long sequence = reader.GetInt64(sequenceOrdinal);
            string? tenant = tenantOrdinal >= 0 && !await reader.IsDBNullAsync(tenantOrdinal, token).ConfigureAwait(false)
                ? reader.GetString(tenantOrdinal)
                : null;

            ShapeItemKey key = new(
                Convert.ToString(reader.GetValue(streamOrdinal), CultureInfo.InvariantCulture) ?? string.Empty,
                tenant,
                sequence,
                reader.GetInt64(versionOrdinal));

            string? storedDotNet = await reader.IsDBNullAsync(dotNetOrdinal, token).ConfigureAwait(false) ? null : reader.GetString(dotNetOrdinal);
            bool binary = !await reader.IsDBNullAsync(bdataOrdinal, token).ConfigureAwait(false);
            string? json = await reader.IsDBNullAsync(shapeOrdinal, token).ConfigureAwait(false) ? null : reader.GetString(shapeOrdinal);

            Judge(state, eventType, key, storedDotNet, binary, json, serializer, await ResolveAsync(selector, reader, json, eventType, storedDotNet, token).ConfigureAwait(false));

            switch (parts.GetValueOrDefault((tenant, sequence)))
            {
                case EventShapeQueries.PartLowest:
                    state.Lowest++;
                    break;
                case EventShapeQueries.PartHighest:
                    state.Highest++;
                    break;
                case EventShapeQueries.PartSpread:
                    state.Spread++;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>One row through Marten's resolver: the event it made, or why it could not.</summary>
    private static async Task<(IEvent? Event, ShapeFailure? Failure)> ResolveAsync(
        ISelector<IEvent> selector,
        NpgsqlDataReader reader,
        string? json,
        string eventType,
        string? storedDotNet,
        CancellationToken token)
    {
        try
        {
            return (await selector.ResolveAsync(reader, token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnknownEventTypeException)
        {
            // The alias and the stored .NET type name are both type names, already on the Event types page and in E05 - never a message.
            string alias = Bound(eventType);
            string dotNet = storedDotNet is null ? "(none)" : Bound(storedDotNet);

            return (null, ShapeFailureDescriber.Of(ShapeFailureKind.UnknownEventType, nameof(UnknownEventTypeException), alias + " / " + dotNet));
        }
        catch (EventDeserializationFailureException exception)
        {
            return (null, ShapeFailureDescriber.Describe(ShapeFailureKind.Unreadable, exception.InnerException ?? exception, json));
        }
        catch (Exception exception)
        {
            // An upcaster that threw, a type with no usable constructor, a binary serializer missing: the type of it, not its words.
            return (null, ShapeFailureDescriber.Describe(ShapeFailureKind.Unreadable, exception, json));
        }
    }

    private static void Judge(
        RunState state,
        string eventType,
        ShapeItemKey key,
        string? storedDotNet,
        bool binary,
        string? json,
        ISerializer serializer,
        (IEvent? Event, ShapeFailure? Failure) resolved)
    {
        state.Examined++;

        if (resolved.Failure is { } failure)
        {
            state.Aggregator.Add(eventType, null, key, failure, []);
            return;
        }

        object? data = resolved.Event?.Data;

        if (data is null)
        {
            state.Aggregator.Add(eventType, null, key, ShapeFailureDescriber.Of(ShapeFailureKind.ReadAsNothing, string.Empty), []);
            return;
        }

        if (binary)
        {
            // Read through the binary serializer; there is no JSON of it to compare.
            state.Aggregator.Add(eventType, null, key, null, []);
            state.Aggregator.NoteBinary(eventType, null);
            return;
        }

        if (storedDotNet is not null && !IsSameType(storedDotNet, data.GetType()))
        {
            // Written as one type and read as another: an upcaster, or an alias kept across a rename. The stored JSON was written for
            // the other type, so a member diff would call every transformation a loss.
            state.Aggregator.Add(eventType, null, key, null, []);
            state.Aggregator.NoteReadAsOtherType(eventType, null);
            return;
        }

        if (json is null)
        {
            state.Aggregator.Add(eventType, null, key, ShapeFailureDescriber.Of(ShapeFailureKind.NotWritable, string.Empty), []);
            return;
        }

        string roundTripped;

        try
        {
            roundTripped = serializer.ToJson(data);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.Aggregator.Add(eventType, null, key, ShapeFailureDescriber.Describe(ShapeFailureKind.NotWritable, exception, json), []);
            return;
        }

        try
        {
            state.Aggregator.Add(eventType, null, key, null, ShapeDiff.Compare(json, roundTripped));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.Aggregator.Add(eventType, null, key, ShapeFailureDescriber.Of(ShapeFailureKind.NotWritable, exception.GetType().Name), []);
        }
    }

    /// <summary>
    /// Whether the stored <c>mt_dotnet_type</c> names <paramref name="type" />: Marten writes <c>"{FullName}, {assembly}"</c>, so it is the
    /// same type when it equals that, or when only the assembly's name has changed. A split at the first comma would be wrong: a closed
    /// generic's name holds commas.
    /// </summary>
    internal static bool IsSameType(string stored, Type type)
    {
        string? fullName = type.FullName;

        if (fullName is null)
        {
            return false;
        }

        if (string.Equals(stored, fullName + ", " + type.Assembly.GetName().Name, StringComparison.Ordinal))
        {
            return true;
        }

        int cut = stored.LastIndexOf(", ", StringComparison.Ordinal);

        return cut > 0 && string.Equals(stored[..cut], fullName, StringComparison.Ordinal);
    }

    /// <summary>The probes between the newest of the oldest edge and the oldest of the newest: evenly spaced sequences.</summary>
    internal static long[] Probes(IReadOnlyList<KeyRow> edges)
    {
        long low = long.MinValue;
        long high = long.MaxValue;

        foreach (KeyRow key in edges)
        {
            if (key.Part == EventShapeQueries.PartLowest)
            {
                low = Math.Max(low, key.Sequence);
            }
            else if (key.Part == EventShapeQueries.PartHighest)
            {
                high = Math.Min(high, key.Sequence);
            }
        }

        if (low == long.MinValue || high == long.MaxValue || high - low <= 1)
        {
            return [];
        }

        long[] probes = new long[ShapeCheckLimits.Spread];

        for (int i = 0; i < probes.Length; i++)
        {
            probes[i] = low + (long) ((high - low) * ((double) (i + 1) / (probes.Length + 1)));
        }

        return probes;
    }

    private static async Task ReadKeysAsync(NpgsqlCommand command, List<KeyRow> keys, CancellationToken token)
    {
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            keys.Add(new KeyRow(
                reader.GetInt64(0),
                await reader.IsDBNullAsync(1, token).ConfigureAwait(false) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3)));
        }
    }

    private static void Attach(NpgsqlCommand command, NpgsqlConnection connection, NpgsqlTransaction transaction, RunState state)
    {
        command.Connection = connection;
        command.Transaction = transaction;
        state.Statements.Add(new ShapeStatement(command.CommandText, EventShapeQueries.ParameterNames(command)));
    }

    private static int PartOrder(int part) => part switch
    {
        EventShapeQueries.PartLowest => 0,
        EventShapeQueries.PartSpread => 1,
        EventShapeQueries.PartHighest => 2,
        _ => 0,
    };

    private static string Bound(string name) => name.Length <= ShapeCheckLimits.MaxSubjectLength ? name : name[..ShapeCheckLimits.MaxSubjectLength];

    private ShapeCheckResult Partial(string subject, RunState state, string reason, long started) =>
        new(
            subject,
            ShapeCoverage.Partial,
            timeProvider.GetUtcNow(),
            Stopwatch.GetElapsedTime(started),
            state.Examined,
            state.TooLarge,
            state.Lowest,
            state.Highest,
            state.Spread,
            reason,
            state.Remedy,
            state.LeftOut,
            state.Aggregator.Build(),
            null,
            null,
            state.Statements);

    private ShapeCheckResult Failed(string subject, RunState state, string reason, long started) =>
        ShapeCheckResult.Failed(
            subject, ShapeCheckText.Failed(reason), timeProvider.GetUtcNow(), Stopwatch.GetElapsedTime(started), state.Statements);

    /// <summary>One key phase 1 read.</summary>
    internal sealed record KeyRow(long Sequence, string? Tenant, long StoredBytes, int Part);

    /// <summary>What a run has read so far, so that a stopped run can still say what it found.</summary>
    private sealed class RunState
    {
        public ShapeAggregator Aggregator { get; } = new();

        public List<ShapeStatement> Statements { get; } = [];

        public List<string> LeftOut { get; } = [];

        public bool Complete { get; set; }

        public int Examined { get; set; }

        public int TooLarge { get; set; }

        public int Lowest { get; set; }

        public int Highest { get; set; }

        public int Spread { get; set; }

        public string? Reason { get; set; }

        public string? Remedy { get; set; }
    }
}
