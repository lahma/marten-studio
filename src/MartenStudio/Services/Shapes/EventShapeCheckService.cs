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
using MartenStudio.Services.Health.Checks;
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
/// store registers or a counted listing of this scope - the caller's (if it was read for this scope within five minutes), the shared one
/// in hand, or for no tenant the planner's statistics - says its events table holds, so an arbitrary string never becomes a cache key or
/// reaches SQL. A name nothing at hand can confirm is "could not confirm", not "no such event type"; and the check runs no
/// <c>group by type</c> of its own.
/// </para>
/// <para>
/// Events are read only through <c>IEventStorage</c> as <c>ISelector&lt;IEvent&gt;</c>, from a query session opened with
/// <c>SessionOptions.ForDatabase</c> (hard rule 15) that is used for <c>EventStorage()</c> and runs nothing - never a LINQ terminal,
/// <c>session.Events</c>, <c>QueryRawEvents</c> or <c>FetchStreamAsync</c> (hard rule 14). Only a mapped alias reaches the resolver: an alias with no
/// mapping would have it load the stored <c>mt_dotnet_type</c> and register that type on the host's store, so its rows are classified without
/// the resolver (<see cref="Prepare" />) and the resolver registers nothing here.
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

    // What E05's statistics say the log holds, kept half a minute per database by this service (one per circuit - the service is scoped,
    // so this is not shared between visitors): one catalog read, however many names a click asks about. On the host's clock.
    private readonly PostgresStudio.Services.Live.StudioSnapshotCache statisticsAliases;

    /// <summary>How long a counted listing the caller holds is trusted to confirm a name: the cache's five minutes.</summary>
    private static readonly TimeSpan ListingTrust = ShapeCheckLimits.TimeToLive;

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
        statisticsAliases = new PostgresStudio.Services.Live.StudioSnapshotCache(timeProvider, TimeSpan.FromSeconds(30));
    }

    /// <summary>The wall budget of one check; a test shortens it to prove the partial result.</summary>
    internal TimeSpan Budget { get; init; } = ShapeCheckLimits.Budget;

    /// <summary>The server-side statement timeout, in place of min(<c>QueryTimeout</c>, 5 s); a test sets it to force a timeout.</summary>
    internal TimeSpan? StatementTimeoutOverride { get; init; }

    /// <inheritdoc />
    public Task<ShapeCheckResult> CheckAsync(StudioScope scope, string eventTypeName, bool force, CancellationToken cancellationToken) =>
        CheckAsync(scope, eventTypeName, force, listing: null, cancellationToken);

    /// <inheritdoc />
    public async Task<ShapeCheckResult> CheckAsync(StudioScope scope, string eventTypeName, bool force, EventTypeList? listing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(eventTypeName);

        // The store policy, on every call, before the cache: a cached result is only ever handed to someone who has just passed it.
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        string shown = eventTypeName.Length <= ShapeCheckLimits.MaxSubjectLength
            ? eventTypeName
            : eventTypeName[..ShapeCheckLimits.MaxSubjectLength];

        switch (await ConfirmAsync(scope, resolved, eventTypeName, listing, cancellationToken).ConfigureAwait(false))
        {
            case Confirmation.NoSuchType:
                return ShapeCheckResult.NotChecked(shown, ShapeCheckText.NoSuchEventType(shown), timeProvider.GetUtcNow());
            case Confirmation.Unconfirmed:
                return ShapeCheckResult.NotChecked(shown, ShapeCheckText.CouldNotConfirm, timeProvider.GetUtcNow());
        }

        return await cache
            .GetAsync(resolved, Kind, eventTypeName, force, token => RunAsync(resolved, eventTypeName, token), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasTypeIndexAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            string schema = resolved.Store.Options.Events.DatabaseSchemaName;

            await using NpgsqlConnection connection = MartenStudioDatabase.CreateConnection(resolved.Database, ConnectionUsage.Read);
            await connection.OpenStudioAsync(cancellationToken).ConfigureAwait(false);

            TableColumns events = await columnCatalog.GetAsync(connection, schema, EventTableInfo.EventsTable, cancellationToken).ConfigureAwait(false);

            return !events.Exists || await HasTypeIndexAsync(connection, schema, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return true;
        }
    }

    private enum Confirmation
    {
        /// <summary>The store registers it, or a listing that is this scope's and fresh holds it.</summary>
        Known,

        /// <summary>A listing that can say - this scope's, fresh - does not hold it, or the name is unusable.</summary>
        NoSuchType,

        /// <summary>Nothing that can say was at hand: not "no such type", only "not confirmed".</summary>
        Unconfirmed,
    }

    /// <summary>
    /// Whether <paramref name="name" /> is an event type the page lists: bounded, and registered by the store or present in its events
    /// table. For a name the store does not register, in this order (1.2-UX-FIX-FU3): the counted <paramref name="listing" /> the caller
    /// holds, used only if it was read for this very scope and within five minutes; the shared listing in hand (half a minute); for no
    /// tenant, the planner's statistics; and otherwise <see cref="Confirmation.Unconfirmed" />. Never a <c>group by type</c> of our own.
    /// A name is "no such type" only where a listing that can say does not hold it - never for want of an answer.
    /// </summary>
    private async Task<Confirmation> ConfirmAsync(StudioScope scope, ResolvedScope resolved, string name, EventTypeList? listing, CancellationToken cancellationToken)
    {
        if (name.Length is 0 or > ShapeCheckLimits.MaxSubjectLength)
        {
            return Confirmation.NoSuchType;
        }

        foreach (JasperFxEvents.IEventType registered in resolved.Store.Options.Events.AllKnownEventTypes())
        {
            if (string.Equals(registered.EventTypeName, name, StringComparison.Ordinal))
            {
                return Confirmation.Known;
            }
        }

        // (i) The listing the caller took the name from, if it is this scope's and not old.
        if (listing is { Error: null, CountsLoaded: true, ListedFor: { } listedFor, ListedAt: { } listedAt }
            && string.Equals(listedFor, EventTypeListingCache.KeyOf(resolved), StringComparison.Ordinal)
            && timeProvider.GetUtcNow() - listedAt <= ListingTrust)
        {
            return Holds(listing, name);
        }

        // (ii) The shared listing, when somebody has read one within the last half minute.
        EventTypeList? shared = await eventTypes.SharedCountedListingAsync(scope, cancellationToken).ConfigureAwait(false);

        if (shared is { Error: null, CountsLoaded: true })
        {
            return Holds(shared, name);
        }

        // (iii) The planner's statistics: a name they hold is confirmed; one they do not is not "no such type" (they are a sample).
        if (resolved.TenantId is null)
        {
            IReadOnlyList<string>? fromStatistics = await StatisticsAliasesAsync(resolved, cancellationToken).ConfigureAwait(false);

            if (fromStatistics is not null && fromStatistics.Contains(name, StringComparer.Ordinal))
            {
                return Confirmation.Known;
            }
        }

        // (iv)
        return Confirmation.Unconfirmed;
    }

    private static Confirmation Holds(EventTypeList listing, string name) =>
        listing.Types.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal)) ? Confirmation.Known : Confirmation.NoSuchType;

    private Task<IReadOnlyList<string>?> StatisticsAliasesAsync(ResolvedScope resolved, CancellationToken cancellationToken) =>
        statisticsAliases.GetAsync(
            resolved.Registration.Key + "\u001f" + resolved.Database.Id.Identity,
            token => ReadStatisticsAliasesAsync(resolved, token),
            cancellationToken);

    /// <inheritdoc />
    public async Task<UnregisteredAliases> ListUnregisteredAsync(StudioScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        EventTypeList? shared = await eventTypes.SharedCountedListingAsync(scope, cancellationToken).ConfigureAwait(false);

        if (shared is { Error: null })
        {
            return new UnregisteredAliases(
                [.. shared.Types.Where(static x => !x.IsRegistered).Select(static x => x.Name).Order(StringComparer.Ordinal)],
                UnregisteredAliasSource.Counts,
                shared.ListedAt);
        }

        // The statistics span the whole table, and Marten stamps mt_events.tenant_id whatever the tenancy style, so with any tenant
        // selected they may name another tenant's event types, which this scope may not see (D27, "Reach, not scope"): never read for a
        // tenant, whatever the style. Load counts is tenant-scoped.
        if (resolved.TenantId is not null)
        {
            return new UnregisteredAliases([], UnregisteredAliasSource.NotForATenant);
        }

        IReadOnlyList<string>? names = await StatisticsAliasesAsync(resolved, cancellationToken).ConfigureAwait(false);

        return names is null
            ? new UnregisteredAliases([], UnregisteredAliasSource.NotRead)
            : new UnregisteredAliases(names, UnregisteredAliasSource.Statistics);
    }

    /// <summary>E05's statistics read for the aliases <c>mt_events</c> holds that Marten has no mapping for, or null when it cannot be read.</summary>
    private async Task<IReadOnlyList<string>?> ReadStatisticsAliasesAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyEventStoreOptions events = resolved.Store.Options.Events;
            HashSet<string> registered = new(events.AllKnownEventTypes().Select(static x => x.EventTypeName), StringComparer.Ordinal);

            // "Unregistered" as the page's counted listing means it: no registered mapping. An upcaster's old alias is not registered, so it
            // is listed and checked ("read as another type"); E05's wider "known" (upcasters included) would leave it out.
            Func<string, bool> isKnown = registered.Contains;

            await using NpgsqlConnection connection = MartenStudioDatabase.CreateConnection(resolved.Database, ConnectionUsage.Read);
            await connection.OpenStudioAsync(cancellationToken).ConfigureAwait(false);

            await using NpgsqlCommand command = EventAliasStatistics.Command(events.DatabaseSchemaName, options.Value.QueryTimeout);
            command.Connection = connection;

            List<EventStatisticsRow> rows = [];

            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(HealthEventQueries.ReadStatistics(reader));
                }
            }

            return EventAliasStatistics.Unregistered(rows, isKnown);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not read is a value: the caller says the unregistered types were not checked.
            logger.LogDebug(exception, "Marten Studio could not read the statistics of mt_events for the shape check's list of event types");
            return null;
        }
    }

    /// <summary>
    /// What is decided once per run, from <c>StoreOptions</c> and without a statement: what a rebuild would do, whether the alias is
    /// mapped, and whether a tenant is named. <b>An unmapped alias's rows never reach Marten's resolver</b>: for an alias with no mapping the
    /// resolver loads <c>mt_dotnet_type</c> with <c>Type.GetType</c> and registers the type on the host's store, which silences E05 until a
    /// restart. So the stored name is classified with <see cref="EventHealth.ResolveLoaded" />, which loads nothing.
    /// </summary>
    private static RunState Prepare(ResolvedScope resolved, string eventType)
    {
        IReadOnlyStoreOptions storeOptions = resolved.Store.Options;
        Func<string, bool>? known = EventHealth.KnownAliases(storeOptions.Events);

        bool mapped = known is not null
            ? known(eventType)
            : storeOptions.Events.AllKnownEventTypes().Any(x => string.Equals(x.EventTypeName, eventType, StringComparison.Ordinal));

        RunState state = new()
        {
            Mapped = mapped,
            Loaded = mapped ? null : EventHealth.LoadedAssemblies(),
            NameTenants = resolved.TenantId is null && storeOptions.Events.UseTenantPartitionedEvents,
            RealTenants = storeOptions.Events.UseTenantPartitionedEvents
                || storeOptions.Events.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined,
        };

        if (storeOptions is StoreOptions concrete)
        {
            state.Policy = new RebuildPolicy(
                concrete.Projections.RebuildErrors.SkipUnknownEvents,
                concrete.Projections.Errors.SkipUnknownEvents,
                concrete.Projections.RebuildErrors.SkipSerializationErrors,
                concrete.Projections.Errors.SkipSerializationErrors);
        }

        return state;
    }

    private async Task<ShapeCheckResult> RunAsync(ResolvedScope resolved, string eventType, CancellationToken shared)
    {
        long started = Stopwatch.GetTimestamp();
        RunState state = Prepare(resolved, eventType);

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(shared);
        budget.CancelAfter(Budget);

        TimeSpan statementTimeout = StatementTimeoutOverride
            ?? (options.Value.QueryTimeout < ShapeCheckLimits.StatementTimeout
                ? options.Value.QueryTimeout
                : ShapeCheckLimits.StatementTimeout);

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

        // Known before a statement runs, so a run that is stopped on the budget or a statement timeout still says it.
        if (!typeIndex)
        {
            state.Reason = ShapeCheckText.NoTypeIndex;
            state.Remedy = ShapeCheckText.TypeIndexRemedy;
        }

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

        return Compose(eventType, state, timeProvider.GetUtcNow(), Stopwatch.GetElapsedTime(started));
    }

    private static bool HasKind(IReadOnlyList<ShapeTypeResult> types, ShapeFailureKind kind) =>
        types.Any(x => x.Failures.Any(f => f.Kind == kind) || (x.MoreFailures > 0 && kind == ShapeFailureKind.Unreadable));

    /// <summary>The lowest failing sequence of a kind and, when each tenant numbers its own, whose it is.</summary>
    private static (long? Sequence, string? Tenant) FirstFailure(IReadOnlyList<ShapeTypeResult> types, ShapeFailureKind kind, bool nameTenant)
    {
        long? first = null;
        string? tenant = null;

        foreach (ShapeFailureGroup failure in types.SelectMany(static x => x.Failures).Where(x => x.Kind == kind))
        {
            if (failure.FirstSequence is { } sequence
                && (first is null || sequence < first || (sequence == first && string.CompareOrdinal(failure.Example.TenantId, tenant) < 0)))
            {
                first = sequence;
                tenant = failure.Example.TenantId;
            }
        }

        return (first, nameTenant ? tenant : null);
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

        if (state.Complete)
        {
            // Everything in scope is read, so the index is not what limits it.
            state.Reason = null;
            state.Remedy = null;
        }

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
            ShapeCheckLimits.MaxItemBytes,
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
        int returned = 0;
        Dictionary<string, ShapeFailure> unmapped = new(StringComparer.Ordinal);

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            returned++;

            long sequence = reader.GetInt64(sequenceOrdinal);
            string? tenant = tenantOrdinal >= 0 && !await reader.IsDBNullAsync(tenantOrdinal, token).ConfigureAwait(false)
                ? reader.GetString(tenantOrdinal)
                : null;

            // A single-tenanted store's tenant_id is the default tenant's marker, which names nothing a link or a sentence can use.
            ShapeItemKey key = new(
                Convert.ToString(reader.GetValue(streamOrdinal), CultureInfo.InvariantCulture) ?? string.Empty,
                state.RealTenants ? tenant : null,
                sequence,
                reader.GetInt64(versionOrdinal));

            string? storedDotNet = await reader.IsDBNullAsync(dotNetOrdinal, token).ConfigureAwait(false) ? null : reader.GetString(dotNetOrdinal);
            bool binary = !await reader.IsDBNullAsync(bdataOrdinal, token).ConfigureAwait(false);
            string? json = await reader.IsDBNullAsync(shapeOrdinal, token).ConfigureAwait(false) ? null : reader.GetString(shapeOrdinal);

            if (state.Mapped)
            {
                Judge(state, eventType, key, storedDotNet, binary, json, serializer, await ResolveAsync(selector, reader, json, eventType, storedDotNet, token).ConfigureAwait(false));
            }
            else
            {
                JudgeUnmapped(state, eventType, key, storedDotNet, unmapped);
            }

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

        // Rows the text-size guard removed in the statement: counted, never read.
        state.TooLarge += Math.Max(0, chosen.Count - returned);
    }

    /// <summary>
    /// A row whose alias no mapping answers to, without Marten's resolver: always an unknown event type, and the stored .NET type name
    /// says whether it is one this process has loaded (Marten would read it as that type and register it on first read) or not.
    /// </summary>
    private static void JudgeUnmapped(RunState state, string eventType, ShapeItemKey key, string? storedDotNet, Dictionary<string, ShapeFailure> classified)
    {
        state.Examined++;

        string name = storedDotNet is null ? "(none)" : Bound(storedDotNet);

        if (!classified.TryGetValue(name, out ShapeFailure? failure))
        {
            Type? loaded = storedDotNet is { Length: > 0 } && !string.IsNullOrWhiteSpace(storedDotNet) && state.Loaded is { } assemblies
                ? EventHealth.ResolveLoaded(storedDotNet, assemblies)
                : null;

            failure = ShapeFailureDescriber.Of(
                loaded is null ? ShapeFailureKind.UnknownEventType : ShapeFailureKind.UnregisteredEventType,
                nameof(UnknownEventTypeException),
                Bound(eventType) + " / " + name);
            classified[name] = failure;
        }

        state.Aggregator.Add(eventType, null, key, failure, []);
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
        Compose(subject, state, timeProvider.GetUtcNow(), Stopwatch.GetElapsedTime(started), ShapeCoverage.Partial, reason);

    /// <summary>
    /// The result of a run, finished or stopped: one place builds the rebuild sentence and the remedy, so a run that stopped on the budget
    /// or a statement timeout keeps what it had found (the sequence a rebuild stops at) and what it knows about the index.
    /// </summary>
    /// <param name="eventType">The event type.</param>
    /// <param name="state">What the run read.</param>
    /// <param name="now">When it ended.</param>
    /// <param name="elapsed">How long it took.</param>
    /// <param name="stopped"><see cref="ShapeCoverage.Partial" /> for a run that was stopped; <see langword="null" /> for one that finished.</param>
    /// <param name="stopReason">Why it stopped; the index reason, if there is one, follows it.</param>
    internal static ShapeCheckResult Compose(
        string eventType,
        RunState state,
        DateTimeOffset now,
        TimeSpan elapsed,
        ShapeCoverage? stopped = null,
        string? stopReason = null)
    {
        IReadOnlyList<ShapeTypeResult> types = state.Aggregator.Build();
        List<string> rebuild = [];
        List<string> remedies = [];

        if (HasKind(types, ShapeFailureKind.UnknownEventType))
        {
            (long? sequence, string? tenant) = FirstFailure(types, ShapeFailureKind.UnknownEventType, state.NameTenants);
            rebuild.Add(ShapeCheckText.RebuildUnknown(sequence, state.Policy.RebuildSkipsUnknown, state.Policy.DaemonSkipsUnknown, tenant));
        }

        if (HasKind(types, ShapeFailureKind.Unreadable))
        {
            (long? sequence, string? tenant) = FirstFailure(types, ShapeFailureKind.Unreadable, state.NameTenants);
            rebuild.Add(ShapeCheckText.RebuildSerialization(
                eventType, sequence, state.Policy.RebuildSkipsSerialization, state.Policy.DaemonSkipsSerialization, tenant));
            remedies.Add(ShapeCheckText.UpcasterRemedy);
        }

        if (state.Remedy is not null)
        {
            remedies.Add(state.Remedy);
        }

        string? reason = stopped is null ? state.Reason : string.Join(" ", new[] { stopReason, state.Reason }.Where(static x => x is { Length: > 0 }));

        return new ShapeCheckResult(
            eventType,
            stopped ?? (state.Complete ? ShapeCoverage.Complete : ShapeCoverage.Sampled),
            now,
            elapsed,
            state.Examined,
            state.TooLarge,
            state.Lowest,
            state.Highest,
            state.Spread,
            reason,
            remedies.Count == 0 ? null : string.Join(" ", remedies),
            state.LeftOut,
            types,
            rebuild.Count == 0 ? null : string.Join(" ", rebuild),
            null,
            state.Statements);
    }

    private ShapeCheckResult Failed(string subject, RunState state, string reason, long started) =>
        ShapeCheckResult.Failed(
            subject, ShapeCheckText.Failed(reason), timeProvider.GetUtcNow(), Stopwatch.GetElapsedTime(started), state.Statements);

    /// <summary>One key phase 1 read.</summary>
    internal sealed record KeyRow(long Sequence, string? Tenant, long StoredBytes, int Part);

    /// <summary>What a rebuild and the running daemon do about unreadable and unknown events, from <c>StoreOptions</c>.</summary>
    internal sealed record RebuildPolicy(
        bool RebuildSkipsUnknown = false,
        bool DaemonSkipsUnknown = true,
        bool RebuildSkipsSerialization = false,
        bool DaemonSkipsSerialization = true);

    /// <summary>What a run has read so far, so that a stopped run can still say what it found.</summary>
    internal sealed class RunState
    {
        /// <summary>What <c>StoreOptions</c> says a rebuild and the daemon do.</summary>
        public RebuildPolicy Policy { get; set; } = new();

        /// <summary>Whether the alias is mapped: only then do its rows reach Marten's resolver, which registers a type it has to look up.</summary>
        public bool Mapped { get; set; } = true;

        /// <summary>The assemblies this process has loaded, for classifying an unmapped alias's stored type name without loading anything.</summary>
        public IReadOnlyDictionary<string, System.Reflection.Assembly>? Loaded { get; set; }

        /// <summary>Whether a failure's tenant is named: each tenant numbers its own events and every tenant is in scope.</summary>
        public bool NameTenants { get; set; }

        /// <summary>Whether the events table's <c>tenant_id</c> means a tenant: conjoined or tenant-partitioned events.</summary>
        public bool RealTenants { get; set; }

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
