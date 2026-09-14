using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Live;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Services.Projections;

/// <summary>
/// Reads what the store was configured to project and what it has actually projected, and asks the
/// host's own daemon to change it.
/// </summary>
/// <remarks>
/// <para>
/// Three sources, in this order. The <b>static model</b> comes from
/// <c>store.Options.Events.Projections()</c> and is the list: a projection that has never run still has a
/// row. <b>Progress</b> comes from the database - the studio's own parameterised reads of
/// <c>mt_event_progression</c> and of the event sequence, through
/// <see cref="ProjectionProgressQueries" /> - and is true whether or not a daemon is hosted here.
/// <b>Live state</b> comes from the in-process shard tracker and only refines the numbers between polls.
/// </para>
/// <para>
/// <b>The database half is never read through Marten's own calls.</b>
/// <c>IMartenDatabase.AllProjectionProgress</c> and <c>IMartenDatabase.FetchHighestEventSequenceNumber</c>
/// each open with <c>EnsureStorageExistsAsync(typeof(IEvent), token)</c>, which applies the event store's
/// Weasel migration under the database's own <c>AutoCreate</c> - so a projections page on a timer would
/// create <c>mt_events</c> on a database that never had one and apply any pending event-store change to
/// one that did (AGENTS.md hard rule 14). A database with no event tables is
/// <see cref="ProjectionsView.HasEventStore" /> <see langword="false" /> and no rows, which is a value
/// rather than an error or a migration.
/// </para>
/// <para>
/// Every method resolves its scope first (plan section 4.3), so a store, database or tenant the visitor
/// may not have is refused before Marten is touched, and every mutating method is
/// <c>Require</c> → resolve with the capability → do → audit, written on failure as well as on success.
/// </para>
/// <para>
/// The running daemon is only ever reached through <see cref="DaemonAccessor" />, never through
/// <c>store.BuildProjectionDaemonAsync()</c> (AGENTS.md hard rule 11).
/// </para>
/// </remarks>
internal sealed class ProjectionDataService : IProjectionDataService
{
    /// <summary>
    /// The progression row Marten keeps the store-global high-water mark in, which is not a shard and
    /// must not be rendered as one.
    /// </summary>
    /// <remarks>
    /// Tenant-neutral: under per-tenant high water Marten also writes <c>HighWaterMark:{tenant}</c> rows
    /// (<c>ShardName.Identity</c> hard-codes both forms), and those are bookkeeping too.
    /// </remarks>
    private const string HighWaterMarkRowName = "HighWaterMark";

    /// <summary>The prefix of a per-tenant high-water row.</summary>
    private const string TenantHighWaterPrefix = HighWaterMarkRowName + ":";

    /// <summary>
    /// What a failure rehydrated from the progression table calls its exception type, because the type
    /// names were never persisted. The same sentinel <c>Marten.Events.Daemon.Progress.ShardStateSelector</c>
    /// uses, so a row read here and a row read by Marten are indistinguishable.
    /// </summary>
    private const string UnknownExceptionType = "Unknown";

    private readonly StudioScopeResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly DaemonAccessor daemons;
    private readonly StudioSnapshotCache cache;
    private readonly StudioLiveState liveState;
    private readonly StudioOperationTracker operations;
    private readonly DaemonControlState controlState;
    private readonly ColumnCatalog catalog;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly ILogger<ProjectionDataService> logger;

    public ProjectionDataService(
        StudioScopeResolver resolver,
        StudioAuthorization authorization,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        DaemonAccessor daemons,
        StudioSnapshotCache cache,
        StudioLiveState liveState,
        StudioOperationTracker operations,
        DaemonControlState controlState,
        ColumnCatalog catalog,
        IOptions<MartenStudioOptions> options,
        ILogger<ProjectionDataService> logger)
    {
        this.resolver = resolver;
        this.authorization = authorization;
        this.capabilities = capabilities;
        this.audit = audit;
        this.daemons = daemons;
        this.cache = cache;
        this.liveState = liveState;
        this.operations = operations;
        this.controlState = controlState;
        this.catalog = catalog;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>The budget every read here runs under - <c>MartenStudioOptions.QueryTimeout</c>.</summary>
    private TimeSpan CommandTimeout => options.Value.QueryTimeout;

    /// <inheritdoc />
    public async Task<ProjectionsView> GetProjectionsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        // The database read is shared; the daemon and the tracker are in-process and are read fresh, so a
        // start or a stop shows on the next render rather than a second later.
        StoredProjections stored = await cache
            .GetAsync(CacheKey(scope, "projections"), token => ReadStoredAsync(resolved, token), cancellationToken)
            .ConfigureAwait(false);

        DaemonStatus daemon = await DescribeDaemonAsync(resolved, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ShardProgress> progress = MergeProgress(resolved, stored);

        return new ProjectionsView(
            stored.Projections, progress, daemon, stored.HighWaterMark, stored.ReadAt, stored.HasEventStore);
    }

    /// <inheritdoc />
    public async Task<ProjectionSummary> GetSummaryAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        try
        {
            ProjectionsView view = await GetProjectionsAsync(scope, cancellationToken).ConfigureAwait(false);

            ShardProgress? worst = view.Progress.Count > 0 ? view.Progress[0] : null;

            return new ProjectionSummary(
                view.Projections.Count,
                view.Projections.Count(static x => x.IsAsync),
                worst?.Lag ?? 0,
                worst is { Lag: > 0 } ? worst.ShardName : null,
                view.Progress.Count(static x => x.IsPaused),
                view.Progress.Count(static x => x.HasFailed),
                view.Daemon,
                null,
                view.HighWaterMark,
                view.Progress);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // "Cannot report" is a value the Overview draws differently from zero (plan section 4.8).
            logger.LogWarning(exception, "Marten Studio could not summarise projections for store {StoreKey}", scope?.StoreKey);

            return new ProjectionSummary(
                0, 0, 0, null, 0, 0,
                new DaemonStatus(DaemonHostingState.NotHostedInThisProcess, false, "Unknown", [], false, null, exception.Message),
                exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<RebuildScope> DescribeRebuildAsync(
        StudioScope scope,
        string projectionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        ISubscriptionSource? source = FindSource(resolved.Store, projectionName)
            ?? throw new KeyNotFoundException($"This store has no projection named '{projectionName}'.");

        // The same read the page's own numbers come from, and for the same reason: describing a rebuild
        // must not be the thing that creates an event store (hard rule 14). A database that has none has
        // nothing to replay, which is honestly zero.
        await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);

        long highWaterMark = await ReadHighWaterMarkAsync(resolved, connection, cancellationToken)
            .ConfigureAwait(false) ?? 0;

        return new RebuildScope(
            source.Name,
            highWaterMark,
            [.. ShardIdentities(source)],
            TablesFor(resolved.Store, source),
            source.Lifecycle,
            options.Value.RebuildShardTimeout);
    }

    /// <inheritdoc />
    public async Task<IDisposable> SubscribeToLiveStateAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        DaemonHosting hosting = await daemons.ForScopeAsync(resolved, cancellationToken).ConfigureAwait(false);

        // No daemon here means no tracker to observe: the page falls back to what the database says, which
        // is what it was reading anyway.
        return hosting.State == DaemonHostingState.Hosted
            ? liveState.Subscribe(resolved.Registration.Key, resolved.Database)
            : NullLease.Instance;
    }

    /// <inheritdoc />
    public Task<DaemonControlResult> StartAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardName);

        return AgentControlAsync(
            scope, "StartAgent", shardName,
            (daemon, token) => daemon.StartAgentAsync(shardName, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<DaemonControlResult> StopAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardName);

        return AgentControlAsync(
            scope, "StopAgent", shardName,
            // StopAgentAsync takes the exception that stopped it rather than a token: null means "asked to".
            (daemon, _) => daemon.StopAgentAsync(shardName, null),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task PauseDaemonAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        CoordinatorControlAsync(
            scope, "PauseDaemon", DaemonControlMessages.PauseIsProcessWide,
            static async (coordinator, context) =>
            {
                await coordinator.PauseAsync().ConfigureAwait(false);

                context.ControlState.RecordPause(new StudioDaemonPause(
                    context.Resolved.Registration.Key,
                    context.User,
                    DateTimeOffset.UtcNow,
                    context.Resolved.Database.Id.Identity,
                    context.Resolved.TenantId));
            },
            cancellationToken);

    /// <inheritdoc />
    public Task ResumeDaemonAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        CoordinatorControlAsync(
            scope, "ResumeDaemon", DaemonControlMessages.ResumeIsProcessWide,
            static async (coordinator, context) =>
            {
                await coordinator.ResumeAsync().ConfigureAwait(false);

                // Cleared after the resume rather than before it: a ResumeAsync that throws leaves the
                // recorded pause standing, which is what the card should keep saying.
                context.ControlState.ClearPause(context.Resolved.Registration.Key);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task RestartHighWaterAgentAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        ControlAsync(
            scope, StudioCapability.ControlDaemon, "RestartHighWaterAgent", HighWaterMarkRowName,
            static (daemon, token) => daemon.RestartHighWaterAgentAsync(token),
            cancellationToken);

    /// <inheritdoc />
    public async Task<OperationStart> RebuildAsync(
        StudioScope scope,
        string projectionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionName);

        const string action = "RebuildProjection";

        try
        {
            capabilities.Require(StudioCapability.RebuildProjections);

            ResolvedScope resolved = await resolver
                .ResolveAsync(scope, nameof(StudioCapability.RebuildProjections), cancellationToken)
                .ConfigureAwait(false);

            DaemonHosting hosting = await daemons.ForScopeAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
            {
                throw new StudioDaemonNotHostedException(hosting.Explanation);
            }

            ISubscriptionSource source = FindSource(resolved.Store, projectionName)
                ?? throw new KeyNotFoundException($"This store has no projection named '{projectionName}'.");

            string user = await authorization.UserNameAsync().ConfigureAwait(false);
            string storeKey = resolved.Registration.Key;
            string databaseId = resolved.Database.Id.Identity;
            string name = source.Name;
            TimeSpan shardTimeout = options.Value.RebuildShardTimeout;

            // Detached, with a cancellation token the tracker owns. A rebuild that a closing browser tab
            // could cancel would leave a projection's tables neither the old shape nor the new one.
            OperationStart start = operations.Start(
                "Rebuild", name, storeKey, databaseId, user, StudioCapability.RebuildProjections,
                async token =>
                {
                    await RebuildWithTimeoutAsync(daemon, name, shardTimeout, token).ConfigureAwait(false);
                    cache.InvalidatePrefix(CachePrefix(scope));
                });

            audit.Record(
                action,
                name,
                true,
                start.Started
                    ? $"Started as operation {start.Id}; per-shard timeout {shardTimeout}."
                    : $"Already running as operation {start.Id}; nothing was started.",
                StudioCapability.RebuildProjections,
                scope);

            cache.InvalidatePrefix(CachePrefix(scope));

            return start;
        }
        catch (StudioCapabilityDeniedException denial)
        {
            audit.RecordCapabilityDenied(denial, action, projectionName);
            throw;
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, PolicyName(StudioCapability.RebuildProjections), action, projectionName);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            audit.Record(action, projectionName, false, exception.Message, StudioCapability.RebuildProjections, scope);
            throw;
        }
    }

    /// <inheritdoc />
    public Task AdvanceHighWaterMarkAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        AdvancedAsync(
            scope, "AdvanceHighWaterMark",
            static (advanced, tenantId, token) => tenantId is null
                ? advanced.AdvanceHighWaterMarkToLatestAsync(token)
                : advanced.AdvanceHighWaterMarkToLatestAsync(tenantId, token),
            cancellationToken);

    /// <inheritdoc />
    public Task CorrectProgressionAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        AdvancedAsync(
            scope, "CorrectProgression",
            static (advanced, tenantId, token) => tenantId is null
                ? advanced.TryCorrectProgressInDatabaseAsync(token)
                : advanced.TryCorrectProgressInDatabaseAsync(tenantId, token),
            cancellationToken);

    /// <inheritdoc />
    public StudioOperation? FindOperation(string? operationId) => operations.Find(operationId);

    /// <inheritdoc />
    public async Task<bool> CancelOperationAsync(
        StudioScope scope,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        const string action = "CancelOperation";
        const StudioCapability capability = StudioCapability.RebuildProjections;

        try
        {
            capabilities.Require(capability);

            ResolvedScope resolved = await resolver
                .ResolveAsync(scope, capability.ToString(), cancellationToken)
                .ConfigureAwait(false);

            // An operation is addressed by where it runs: a visitor authorized for one database must not
            // be able to stop a rebuild running against another.
            StudioOperation? operation = operations.Find(operationId);
            if (operation is null
                || !string.Equals(operation.StoreKey, resolved.Registration.Key, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(operation.DatabaseId, resolved.Database.Id.Identity, StringComparison.OrdinalIgnoreCase))
            {
                audit.Record(action, operationId, false, "No such operation in this scope.", capability, scope);
                return false;
            }

            bool cancelled = operations.Cancel(operationId);

            audit.Record(
                action,
                operation.Kind + " " + operation.Target,
                cancelled,
                cancelled ? $"Asked operation {operationId} to stop." : $"Operation {operationId} was no longer running.",
                capability,
                scope);

            return cancelled;
        }
        catch (StudioCapabilityDeniedException denial)
        {
            audit.RecordCapabilityDenied(denial, action, operationId);
            throw;
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, PolicyName(capability), action, operationId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StudioOperation>> RunningOperationsAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Resolved like every sibling, so the store, database and tenant a visitor may address is decided
        // in one place rather than trusted from the three strings in a URL.
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        return operations.RunningFor(resolved.Registration.Key, resolved.Database.Id.Identity);
    }

    /// <summary>
    /// The one call that starts a replay, and the only place the per-shard timeout is chosen.
    /// </summary>
    /// <remarks>
    /// <c>RebuildProjectionAsync(name, token)</c> looks like the plain overload and is not: JasperFx
    /// 2.69.3 forwards it to <c>RebuildProjectionAsync(name, 5.Minutes(), token)</c>. The teardown of the
    /// projection's tables happens <em>before</em> that budget starts applying to the replay, so on a
    /// production-sized store the five minutes expire with the tables already emptied and the operation
    /// recorded as failed. The studio therefore always names its own budget
    /// (<c>MartenStudioOptions.RebuildShardTimeout</c>, one hour by default), and the rebuild dialog
    /// states it next to the number of events.
    /// </remarks>
    internal static Task RebuildWithTimeoutAsync(
        IProjectionDaemon daemon,
        string projectionName,
        TimeSpan shardTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        return daemon.RebuildProjectionAsync(projectionName, shardTimeout, cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------------------------------------

    private async Task<StoredProjections> ReadStoredAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        IReadOnlyList<ISubscriptionSource> sources = resolved.Store.Options.Events.Projections();

        List<ProjectionInfo> projections = [];
        foreach (ISubscriptionSource source in sources)
        {
            projections.Add(new ProjectionInfo(
                source.Name,
                source.Lifecycle,
                source.Type.ToString(),
                source.Version,
                source.ImplementationType?.Name ?? "(unknown)",
                [.. ShardIdentities(source)]));
        }

        projections.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        // One read connection for both numbers, on the database the scope resolved - never
        // IMartenDatabase.AllProjectionProgress or FetchHighestEventSequenceNumber, which migrate before
        // they read (hard rule 14, and ProjectionProgressQueries says where that was verified).
        await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);

        long? highWaterMark = await ReadHighWaterMarkAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

        string? tenantId = TenantFilterFor(resolved);

        ProgressionRows rows = await ProjectionProgressQueries
            .ReadProgressionRowsAsync(
                connection,
                catalog,
                EventSchemaOf(resolved),
                tenantId,
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<ProgressionRow> kept = tenantId is null
            ? rows.Rows
            : rows.Rows.Where(x => BelongsToTenant(x.Name, tenantId));

        List<ShardState> states = [.. kept.Select(ToShardState)];

        return new StoredProjections(
            projections,
            states,
            highWaterMark ?? 0,
            highWaterMark is not null || rows.TableExists,
            DateTimeOffset.UtcNow);
    }

    /// <summary>The event store's schema, which is where all three of these objects live.</summary>
    private static string EventSchemaOf(ResolvedScope resolved) => resolved.Store.Options.Events.DatabaseSchemaName;

    /// <summary>
    /// The high-water mark for the scope's database, or <see langword="null" /> when there is no event
    /// store in it to have one.
    /// </summary>
    private Task<long?> ReadHighWaterMarkAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        CancellationToken cancellationToken) =>
        ProjectionProgressQueries.ReadHighWaterMarkAsync(
            connection,
            catalog,
            EventSchemaOf(resolved),
            resolved.Store.Options.Events.UseTenantPartitionedEvents,
            CommandTimeout,
            cancellationToken);

    /// <summary>
    /// The tenant whose progression rows this scope may see, or <see langword="null" /> for every row in
    /// the database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A progression name carries a tenant only under <c>UseTenantPartitionedEvents</c>, where
    /// <c>ShardName.Identity</c> is <c>{Name}:{ShardKey}:{tenantId}</c>. That is the one shape where a
    /// database holds one tenant's progress next to another's, and where a scope pinned to a tenant must
    /// not read the other's rows. Everywhere else - a single-tenanted store, a conjoined store without
    /// per-tenant partitioning, database-per-tenant - every row is store-global or the database *is* the
    /// tenant, and filtering on a <c>:{tenant}</c> suffix would return nothing at all and draw a healthy
    /// store as one whose projections have never run.
    /// </para>
    /// <para>
    /// This is what <c>store.Advanced.AllProjectionProgress(tenantId, ct)</c> did for the studio before
    /// hard rule 14 took it away, and it is not a filter: verified against Marten 9.35's
    /// <c>AdvancedOperations</c>, it resolves the tenant's <em>database</em> and then calls the
    /// <em>untenanted</em> <c>IMartenDatabase.AllProjectionProgress(token)</c> on it. The scope resolver
    /// has already picked that database, so the only thing left to decide is the narrowing above.
    /// </para>
    /// </remarks>
    private static string? TenantFilterFor(ResolvedScope resolved) =>
        resolved.Store.Options.Events.UseTenantPartitionedEvents && resolved.TenantId is { Length: > 0 } tenantId
            ? tenantId
            : null;

    /// <summary>
    /// Whether a progression name really belongs to <paramref name="tenantId" />, rather than merely
    /// ending in it.
    /// </summary>
    /// <remarks>
    /// Marten's own second pass, and it is not redundant with the SQL suffix filter. A trailing
    /// <c>:{tenantId}</c> is the most a string comparison can check, but <c>ShardName.Identity</c> without
    /// a tenant is <c>{Name}:{ShardKey}</c> - so a projection sliced by a shard key that happens to equal
    /// a tenant id ends in the same suffix and would be reported as that tenant's progress. Parsing
    /// settles it: <c>ShardName.TryParse</c> puts <c>Foo:acme</c>'s trailing segment in the shard-key slot
    /// and <c>Orders:All:acme</c>'s in the tenant slot. A name that does not parse at all is not
    /// attributable to a tenant, so it is excluded rather than guessed at.
    /// </remarks>
    internal static bool BelongsToTenant(string progressionName, string tenantId) =>
        ShardName.TryParse(progressionName, out ShardName? shard) && shard?.TenantId == tenantId;

    /// <summary>A read connection on the database the scope resolved, opened or not opened at all.</summary>
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

    /// <summary>
    /// One progression row as the rest of the studio - and the live tracker beside it - speaks about
    /// shards.
    /// </summary>
    /// <remarks>
    /// The same mapping <c>Marten.Events.Daemon.Progress.ShardStateSelector</c> performs, so a row read
    /// here and a row read by Marten are the same <see cref="ShardState" />. <c>Timestamp</c> is set by
    /// the constructor to "now" in both cases and means "when this was read", not when the shard moved.
    /// </remarks>
    private static ShardState ToShardState(ProgressionRow row) =>
        new(row.Name, row.Sequence)
        {
            LastHeartbeat = row.LastHeartbeat,
            AgentStatus = row.AgentStatus,
            PauseReason = row.PauseReason,
            RunningOnNode = row.RunningOnNode,
            Failure = BuildFailure(row),
        };

    /// <summary>
    /// The classified failure a progression row carries, or <see langword="null" /> when it carries none.
    /// </summary>
    /// <remarks>
    /// Marten's own reconstruction, repeated because its selector is internal: <c>failure_category</c> is
    /// the presence flag, <c>pause_reason</c> is both <c>Message</c> and <c>Detail</c> (the exception text
    /// is the only part that was ever persisted), and the exception type names are the same <c>Unknown</c>
    /// sentinel <c>ShardStateSelector</c> uses - both members are <c>required</c> on
    /// <see cref="ShardFailure" />, and inventing a plausible type name would be worse than admitting
    /// there is none. A category the enum does not know is treated as no failure rather than guessed at.
    /// </remarks>
    private static ShardFailure? BuildFailure(ProgressionRow row)
    {
        if (row.FailureCategory is not { Length: > 0 } category
            || !Enum.TryParse(category, out ShardFailureCategory parsed))
        {
            return null;
        }

        string detail = row.PauseReason ?? string.Empty;

        return new ShardFailure
        {
            Category = parsed,
            ExceptionType = UnknownExceptionType,
            RootExceptionType = UnknownExceptionType,
            Message = detail,
            Detail = detail,
            OccurredAt = row.LastHeartbeat ?? default,
            Event = row.FailureEventSequence is { } sequence
                ? new EventFailureDetails
                {
                    Sequence = sequence,
                    EventTypeName = row.FailureEventType,
                    TenantId = row.FailureEventTenantId,
                }
                : null,
        };
    }

    private List<ShardProgress> MergeProgress(ResolvedScope resolved, StoredProjections stored)
    {
        IReadOnlyDictionary<string, ShardState> live = liveState.Latest(
            resolved.Registration.Key,
            resolved.Database.Id.Identity);

        Dictionary<string, ShardState> byShard = new(StringComparer.OrdinalIgnoreCase);
        foreach (ShardState row in stored.Progress)
        {
            if (row.ShardName is { Length: > 0 } name && !IsBookkeepingRow(name))
            {
                byShard[name] = row;
            }
        }

        List<ShardProgress> progress = [];
        HashSet<string> rendered = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProjectionInfo projection in stored.Projections)
        {
            // Inline and live projections have no daemon shard: the async daemon never runs them, so a
            // progress row for one would be a number about something that does not happen.
            if (!projection.IsAsync)
            {
                continue;
            }

            foreach (string shardName in projection.ShardNames)
            {
                rendered.Add(shardName);
                progress.Add(Build(shardName, projection.Name, byShard.GetValueOrDefault(shardName), live.GetValueOrDefault(shardName), stored.HighWaterMark, isRegistered: true));
            }
        }

        // Rows the database has that the configuration does not: a projection that was removed from the
        // host's registration still has progress, and hiding it would hide the reason a table is stale.
        // They are marked rather than mixed in, so the page can render them under their own heading
        // instead of inventing a projection row for a projection this store no longer has.
        foreach (KeyValuePair<string, ShardState> orphan in byShard)
        {
            if (!rendered.Contains(orphan.Key))
            {
                progress.Add(Build(orphan.Key, ProjectionNameOf(orphan.Key), orphan.Value, live.GetValueOrDefault(orphan.Key), stored.HighWaterMark, isRegistered: false));
            }
        }

        progress.Sort(static (left, right) =>
        {
            int byLag = right.Lag.CompareTo(left.Lag);
            return byLag != 0 ? byLag : string.Compare(left.ShardName, right.ShardName, StringComparison.OrdinalIgnoreCase);
        });

        return progress;
    }

    private static ShardProgress Build(
        string shardName,
        string projectionName,
        ShardState? stored,
        ShardState? live,
        long highWaterMark,
        bool isRegistered)
    {
        // The tracker is ahead of the database by design: it publishes as the shard advances, and the
        // progression row is written in the same transaction as the projected documents. Take whichever
        // sequence is higher and say which one it was.
        bool useLive = live is not null && (stored is null || live.Sequence >= stored.Sequence);
        ShardState? state = useLive ? live : stored;

        return new ShardProgress(
            shardName,
            projectionName,
            state?.Sequence ?? 0,
            highWaterMark,
            Blank(state?.AgentStatus),
            Blank(state?.PauseReason),
            Describe(state?.Failure),
            state?.LastAdvanced,
            state?.SkippedEventsCount,
            Blank(state?.TenantId),
            stored is not null,
            useLive && live is not null,
            isRegistered);
    }

    private async Task<DaemonStatus> DescribeDaemonAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        string mode = ModeOf(resolved.Store);
        int pollingMilliseconds = LeadershipPollingMillisecondsOf(resolved.Store);
        int agentPauseMilliseconds = AgentPauseMillisecondsOf(resolved.Store);

        // What this process itself paused, which is the only pause anybody can honestly report: the
        // coordinator has PauseAsync and ResumeAsync and no public flag between them.
        StudioDaemonPause? pause = controlState.Find(resolved.Registration.Key);

        DaemonHosting hosting = await daemons.ForScopeAsync(resolved, cancellationToken).ConfigureAwait(false);

        // The dialog names the databases a pause would reach, and it must never name one this visitor may
        // not address: the list comes from IMartenStorage.AllDatabases() and is therefore the store's
        // whole set, while StoreAuthorizationPolicy is per database (D5).
        IReadOnlyList<string> databases = await VisibleDatabasesAsync(
            resolved, hosting.Databases ?? [], cancellationToken).ConfigureAwait(false);

        if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
        {
            return new DaemonStatus(
                DaemonHostingState.NotHostedInThisProcess, false, mode, [], false, null, hosting.Explanation,
                PausedByStudio: null,
                LeadershipPollingMilliseconds: pollingMilliseconds,
                CoordinatedDatabases: databases,
                AgentPauseMilliseconds: agentPauseMilliseconds);
        }

        // Several IProjectionDaemon members are default interface methods in JasperFx whose default
        // bodies throw, and an implementation is free not to override them. A daemon card is not worth a
        // page-wide failure, so each fact is read defensively and its absence is drawn as "unknown".
        bool isRunning = Read(() => daemon.IsRunning, false);
        bool hasAnyPaused = Read(daemon.HasAnyPaused, false);
        DateTimeOffset? lastPolled = Read<DateTimeOffset?>(() => daemon.HighWaterLastPolledAt, null);

        List<DaemonAgentInfo> agents = [];
        foreach (ISubscriptionAgent agent in Read<IReadOnlyList<ISubscriptionAgent>>(daemon.CurrentAgents, []))
        {
            agents.Add(new DaemonAgentInfo(
                Read(() => agent.Name?.Identity, null) ?? "(unnamed)",
                Read(() => agent.Status.ToString(), "Unknown"),
                Read(() => agent.Position, 0L),
                Read(() => agent.HighWaterMark, 0L)));
        }

        agents.Sort(static (left, right) => string.Compare(left.ShardName, right.ShardName, StringComparison.OrdinalIgnoreCase));

        return new DaemonStatus(
            DaemonHostingState.Hosted,
            isRunning,
            mode,
            agents,
            hasAnyPaused,
            lastPolled,
            hosting.Explanation,
            pause,
            pollingMilliseconds,
            databases,
            agentPauseMilliseconds);
    }

    /// <summary>
    /// The databases of this store the visitor may address, of those a coordinator control would reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same question <see cref="StudioScopeResolver" /> asks when a scope is resolved, asked once per
    /// database rather than once for the one in the URL - because a coordinator pause is not scoped to
    /// the database the selector is on. It is used twice: to filter what the confirm dialog lists, and -
    /// in <see cref="RequireEveryCoordinatedDatabaseAsync" /> - to decide whether the pause may happen at
    /// all.
    /// </para>
    /// <para>
    /// The read form of the policy (a <see langword="null" /> capability), because listing a database is
    /// a read. The write form is what the control itself checks.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> VisibleDatabasesAsync(
        ResolvedScope resolved,
        IReadOnlyList<string> databases,
        CancellationToken cancellationToken)
    {
        if (databases.Count == 0 || !authorization.IsEnabled)
        {
            return databases;
        }

        List<string> visible = [];

        foreach (string databaseId in databases)
        {
            bool allowed = await authorization
                .IsAuthorizedAsync(new StudioScope(resolved.Registration.Key, databaseId, null), null, cancellationToken)
                .ConfigureAwait(false);

            if (allowed)
            {
                visible.Add(databaseId);
            }
        }

        return visible;
    }

    /// <summary>
    /// How often this store's projection coordinator restarts the agents it finds missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read off the store rather than assumed, because the whole point of quoting it is that the person
    /// reading the hint can go and change it. The route is not obvious and was verified against
    /// Marten 9.35 and JasperFx.Events 2.69.3: <c>LeadershipPollingTime</c> is an <c>int</c> of
    /// milliseconds on <c>DaemonSettings</c>, and <em>neither</em> <c>IReadOnlyStoreOptions</c> (which is
    /// all <c>IDocumentStore.Options</c> hands out - it has no <c>Projections</c> member at all) nor
    /// <c>IReadOnlyDaemonSettings</c> exposes it. What does reach it is the object identity:
    /// <c>EventGraph</c> implements <c>IReadOnlyEventStoreOptions.Daemon</c> as
    /// <c>_store.Options.Projections</c>, and <c>ProjectionOptions : ProjectionGraph&lt;,,&gt; :
    /// DaemonSettings</c> - so the interface the studio is handed <em>is</em> a
    /// <see cref="DaemonSettings" /> at run time.
    /// </para>
    /// <para>
    /// A pattern match and not a cast, with the documented default as the fallback: this is a hint on a
    /// card, and a store whose settings object is some other implementation must not take the page down
    /// for it. <c>MartenApiSurfaceTest</c> pins the identity so the fallback stays theoretical.
    /// </para>
    /// </remarks>
    internal static int LeadershipPollingMillisecondsOf(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return store.Options.Events.Daemon is DaemonSettings { LeadershipPollingTime: > 0 } settings
                ? settings.LeadershipPollingTime
                : DaemonDefaults.LeadershipPollingMilliseconds;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DaemonDefaults.LeadershipPollingMilliseconds;
        }
    }

    /// <inheritdoc cref="LeadershipPollingMillisecondsOf" />
    internal static TimeSpan LeadershipPollingTimeOf(IDocumentStore store) =>
        TimeSpan.FromMilliseconds(LeadershipPollingMillisecondsOf(store));

    /// <summary>
    /// How long the coordinator's leadership loop sleeps between passes while any shard is paused.
    /// </summary>
    /// <remarks>
    /// Read off the store by the same route and with the same fallback as
    /// <see cref="LeadershipPollingMillisecondsOf" />, and quoted for the same reason: it is the number
    /// that actually applies to somebody looking at a paused shard, and it is five times smaller than the
    /// leadership interval by default. <c>ProjectionCoordinatorBase</c>'s loop ends each pass with
    /// <c>Task.Delay(agentPauseTime)</c> rather than the leadership interval whenever any resolved daemon
    /// answers <c>HasAnyPaused()</c> - verified against JasperFx.Events 2.69.3.
    /// </remarks>
    internal static int AgentPauseMillisecondsOf(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return store.Options.Events.Daemon is DaemonSettings { AgentPauseTime.TotalMilliseconds: > 0 } settings
                ? (int) Math.Ceiling(settings.AgentPauseTime.TotalMilliseconds)
                : DaemonDefaults.AgentPauseMilliseconds;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DaemonDefaults.AgentPauseMilliseconds;
        }
    }

    /// <inheritdoc cref="AgentPauseMillisecondsOf" />
    internal static TimeSpan AgentPauseTimeOf(IDocumentStore store) =>
        TimeSpan.FromMilliseconds(AgentPauseMillisecondsOf(store));

    // ------------------------------------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One daemon control: require the capability, resolve with it, ask the daemon, write the audit entry
    /// either way.
    /// </summary>
    private async Task ControlAsync(
        StudioScope scope,
        StudioCapability capability,
        string action,
        string target,
        Func<IProjectionDaemon, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            capabilities.Require(capability);

            ResolvedScope resolved = await resolver
                .ResolveAsync(scope, capability.ToString(), cancellationToken)
                .ConfigureAwait(false);

            DaemonHosting hosting = await daemons.ForScopeAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
            {
                throw new StudioDaemonNotHostedException(hosting.Explanation);
            }

            string user = await authorization.UserNameAsync().ConfigureAwait(false);
            logger.DaemonControlRequested(user, action, target, resolved.Registration.Key, resolved.Database.Id.Identity);

            await operation(daemon, cancellationToken).ConfigureAwait(false);

            audit.Record(action, target, true, null, capability, scope);
            cache.InvalidatePrefix(CachePrefix(scope));
        }
        catch (StudioCapabilityDeniedException denial)
        {
            audit.RecordCapabilityDenied(denial, action, target);
            throw;
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, PolicyName(capability), action, target);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            audit.Record(action, target, false, exception.Message, capability, scope);
            throw;
        }
    }

    /// <summary>
    /// One per-agent control: everything <see cref="ControlAsync" /> does, plus the check that makes the
    /// control mean what it says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator's leadership loop starts every shard of every set it holds the lock for that is
    /// missing from <c>daemon.CurrentAgents()</c>, every <c>LeadershipPollingTime</c> - and
    /// <c>JasperFxAsyncDaemon.StopAgentAsync</c> removes the agent from that map. So stopping one agent
    /// while the coordinator runs is undone within a poll, and starting one is a no-op the coordinator
    /// was about to do anyway. Both are therefore refused rather than performed: a control that reverts
    /// itself a second later is worse than no control, because the person watching believes it worked.
    /// </para>
    /// <para>
    /// A refusal is <b>not</b> a capability denial and is not recorded as one - the capability is on, the
    /// policy passed, and the operation is available the moment the daemon is paused. It is audited as an
    /// action that did not succeed, carrying the reason, so the ring shows the attempt and shows that
    /// nothing was done.
    /// </para>
    /// </remarks>
    private async Task<DaemonControlResult> AgentControlAsync(
        StudioScope scope,
        string action,
        string target,
        Func<IProjectionDaemon, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        const StudioCapability capability = StudioCapability.ControlDaemon;

        try
        {
            capabilities.Require(capability);

            ResolvedScope resolved = await resolver
                .ResolveAsync(scope, capability.ToString(), cancellationToken)
                .ConfigureAwait(false);

            DaemonHosting hosting = await daemons.ForScopeAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
            {
                throw new StudioDaemonNotHostedException(hosting.Explanation);
            }

            if (controlState.Find(resolved.Registration.Key) is null)
            {
                string reason = DaemonControlMessages.AgentControlNeedsPause(
                    LeadershipPollingTimeOf(resolved.Store),
                    AgentPauseTimeOf(resolved.Store));

                audit.Record(action, target, false, reason, capability, scope);

                return DaemonControlResult.Refused(reason);
            }

            string user = await authorization.UserNameAsync().ConfigureAwait(false);
            logger.DaemonControlRequested(user, action, target, resolved.Registration.Key, resolved.Database.Id.Identity);

            await operation(daemon, cancellationToken).ConfigureAwait(false);

            audit.Record(action, target, true, null, capability, scope);
            cache.InvalidatePrefix(CachePrefix(scope));

            return DaemonControlResult.Done;
        }
        catch (StudioCapabilityDeniedException denial)
        {
            audit.RecordCapabilityDenied(denial, action, target);
            throw;
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, PolicyName(capability), action, target);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            audit.Record(action, target, false, exception.Message, capability, scope);
            throw;
        }
    }

    /// <summary>
    /// One coordinator control - a pause or a resume - gated, audited and recorded exactly like the
    /// daemon controls, but aimed at the store rather than at one database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target of the audit entry is the store key, because that is the granularity the operation
    /// actually has: <c>PauseAsync()</c> stops the coordinator's leadership runner and then stops every
    /// daemon it has resolved, across every database of that store. Recording one database's identity as
    /// the target would make the entry narrower than the thing that happened.
    /// </para>
    /// <para>
    /// <b>Which is exactly why one resolved scope is not enough to authorize it.</b>
    /// <see cref="StudioScopeResolver" /> authorizes the store, database and tenant in the URL; the
    /// operation reaches <em>every</em> database of the store. A host whose
    /// <see cref="MartenStudioOptions.StoreAuthorizationPolicy" /> scopes by database - which the scope
    /// selector honours, so it is a shape hosts are expected to have - would otherwise let a visitor
    /// allowed only database A stop the projections of database B, and the confirm dialog would name B
    /// while doing it. So the write policy is asked for every coordinated database before the coordinator
    /// is touched, and the first refusal is a scope denial: the same exception, the same 9203 audit
    /// entry, and nothing paused.
    /// </para>
    /// </remarks>
    private async Task CoordinatorControlAsync(
        StudioScope scope,
        string action,
        string outcome,
        Func<MartenCoordinator, DaemonControlContext, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        const StudioCapability capability = StudioCapability.ControlDaemon;
        string target = scope.StoreKey is { Length: > 0 } key ? key : "(default store)";

        try
        {
            capabilities.Require(capability);

            ResolvedScope resolved = await resolver
                .ResolveAsync(scope, capability.ToString(), cancellationToken)
                .ConfigureAwait(false);

            await RequireEveryCoordinatedDatabaseAsync(resolved, capability, cancellationToken).ConfigureAwait(false);

            MartenCoordinator coordinator = daemons.CoordinatorForScope(resolved)
                ?? throw new StudioDaemonNotHostedException(DaemonAccessor.NotRegisteredExplanation);

            string user = await authorization.UserNameAsync().ConfigureAwait(false);
            logger.DaemonControlRequested(user, action, target, resolved.Registration.Key, resolved.Database.Id.Identity);

            await operation(coordinator, new DaemonControlContext(resolved, user, controlState)).ConfigureAwait(false);

            audit.Record(action, target, true, outcome, capability, scope);
            cache.InvalidatePrefix(CachePrefix(scope));
        }
        catch (StudioCapabilityDeniedException denial)
        {
            audit.RecordCapabilityDenied(denial, action, target);
            throw;
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, PolicyName(capability), action, target);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            audit.Record(action, target, false, exception.Message, capability, scope);
            throw;
        }
    }

    /// <summary>
    /// Refuses a coordinator control unless the visitor may address <em>every</em> database it would
    /// reach.
    /// </summary>
    /// <remarks>
    /// The databases come from <c>IMartenStorage.AllDatabases()</c>, which is what the coordinator's own
    /// distributor enumerates and what <see cref="DaemonAccessor" /> reports on the card - never
    /// <c>AllSchemaNames()</c> or <c>AllObjects()</c>, which apply migrations (hard rule 14). A store
    /// that cannot enumerate its databases is refused rather than allowed: the whole point of the check
    /// is that the operation is wider than the scope, so not knowing how wide is not a reason to proceed.
    /// </remarks>
    /// <exception cref="StudioNotAuthorizedException">
    /// The store policy refuses one of them, which the caller records as a scope denial.
    /// </exception>
    private async Task RequireEveryCoordinatedDatabaseAsync(
        ResolvedScope resolved,
        StudioCapability capability,
        CancellationToken cancellationToken)
    {
        if (!authorization.IsEnabled)
        {
            return;
        }

        IReadOnlyList<IMartenDatabase> databases;

        try
        {
            databases = await resolved.Store.Storage.AllDatabases().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Marten Studio could not enumerate the databases of store {StoreKey} before a coordinator control",
                resolved.Registration.Key);

            throw new StudioNotAuthorizedException(resolved.Scope);
        }

        foreach (IMartenDatabase database in databases)
        {
            string databaseId = database.Id.Identity;

            bool allowed = await authorization
                .IsAuthorizedAsync(
                    new StudioScope(resolved.Registration.Key, databaseId, null),
                    capability.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!allowed)
            {
                // The refused scope, not the one that was asked for: the audit entry then says which
                // database the visitor was not allowed to reach, which is the fact worth keeping.
                throw new StudioNotAuthorizedException(new StudioScope(resolved.Registration.Key, databaseId, null));
            }
        }
    }

    /// <summary>
    /// One progression correction. These do not need a daemon - they are writes against the progression
    /// table - which is why they are gated on <c>CorrectProgression</c> and offered even when the daemon
    /// runs somewhere else.
    /// </summary>
    private async Task AdvancedAsync(
        StudioScope scope,
        string action,
        Func<AdvancedOperations, string?, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        const StudioCapability capability = StudioCapability.CorrectProgression;
        string target = scope.DatabaseId is { Length: > 0 } id ? id : "(default database)";

        try
        {
            capabilities.Require(capability);

            ResolvedScope resolved = await resolver
                .ResolveAsync(scope, capability.ToString(), cancellationToken)
                .ConfigureAwait(false);

            string user = await authorization.UserNameAsync().ConfigureAwait(false);
            logger.DaemonControlRequested(user, action, target, resolved.Registration.Key, resolved.Database.Id.Identity);

            await operation(resolved.Store.Advanced, resolved.TenantId, cancellationToken).ConfigureAwait(false);

            audit.Record(action, target, true, null, capability, scope);
            cache.InvalidatePrefix(CachePrefix(scope));
        }
        catch (StudioCapabilityDeniedException denial)
        {
            audit.RecordCapabilityDenied(denial, action, target);
            throw;
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, PolicyName(capability), action, target);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            audit.Record(action, target, false, exception.Message, capability, scope);
            throw;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private string PolicyName(StudioCapability capability) =>
        authorization.PolicyFor(capability.ToString()) ?? "(none configured)";

    private static string CachePrefix(StudioScope scope) =>
        scope.StoreKey + "|" + scope.DatabaseId + "|" + (scope.TenantId ?? string.Empty) + "|";

    private static string CacheKey(StudioScope scope, string query) => CachePrefix(scope) + query;

    /// <summary>
    /// Whether a row of <c>mt_event_progression</c> is Marten's own bookkeeping rather than a shard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Marten writes three kinds of bookkeeping row beside the real shards: <c>HighWaterMark</c> (and
    /// <c>HighWaterMark:{tenant}</c> under per-tenant high water - <c>ShardName.Identity</c> hard-codes
    /// both forms), <c>HighWaterAllocationFence</c> and <c>HighWaterStuckGap</c>
    /// (<c>Marten.Events.Daemon.HighWater.HighWaterStatisticsDetector</c>'s two <c>ProgressionName</c>
    /// constants). None of them is a projection, and only the first was being excluded - so the fence and
    /// the gap were rendered under "Unregistered shards" and, worse, counted towards the worst lag. The
    /// gap row trails the mark by the detection gap <em>by design</em>, which made a permanently
    /// 10 000-events-behind projection that does not exist the top row of an operations screen sorted by
    /// lag.
    /// </para>
    /// <para>
    /// The general rule rather than a list of three names: every real shard identity contains a colon
    /// (<c>Name:ShardKey</c>, <c>Name:ShardKey:Tenant</c>, <c>Name:V2:ShardKey</c>), because
    /// <c>ShardName</c> composes it that way and refuses to parse anything else - the high-water names
    /// are the only ones it documents as "not carrying a shard key". So a progression name without a
    /// colon cannot be a shard, whatever Marten decides to bookkeep next; the per-tenant high-water
    /// prefix is the one colon-carrying exception and is named explicitly.
    /// </para>
    /// </remarks>
    internal static bool IsBookkeepingRow(string progressionName) =>
        string.IsNullOrWhiteSpace(progressionName)
        || !progressionName.Contains(':', StringComparison.Ordinal)
        || progressionName.StartsWith(TenantHighWaterPrefix, StringComparison.OrdinalIgnoreCase);

    private static string ProjectionNameOf(string shardName)
    {
        int separator = shardName.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 ? shardName[..separator] : shardName;
    }

    private static IEnumerable<string> ShardIdentities(ISubscriptionSource source)
    {
        ShardName[] shards;
        try
        {
            shards = source.ShardNames();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A composite projection can refuse to enumerate its stages; the bundle is then one row named
            // after the projection, which is how it is driven anyway.
            return [source.Name + ":All"];
        }

        return shards.Length == 0 ? [source.Name + ":All"] : shards.Select(static x => x.Identity);
    }

    private static ISubscriptionSource? FindSource(IDocumentStore store, string projectionName)
    {
        foreach (ISubscriptionSource source in store.Options.Events.Projections())
        {
            if (string.Equals(source.Name, projectionName, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>
    /// The tables a rebuild would rewrite, by matching the projection's published types against the
    /// document types this store knows.
    /// </summary>
    private static List<string> TablesFor(IDocumentStore store, ISubscriptionSource source)
    {
        HashSet<Type> published = [];
        if (source is IProjectionSource<IDocumentOperations, IQuerySession> projection)
        {
            try
            {
                foreach (Type type in projection.PublishedTypes())
                {
                    published.Add(type);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A projection that will not describe itself still rebuilds; the dialog just cannot list
                // its tables, and says so by listing none.
            }
        }

        List<string> tables = [];
        foreach (IDocumentType documentType in store.Options.AllKnownDocumentTypes())
        {
            if (published.Contains(documentType.DocumentType))
            {
                tables.Add(documentType.TableName.QualifiedName);
            }
        }

        tables.Sort(StringComparer.OrdinalIgnoreCase);
        return tables;
    }

    private static string ModeOf(IDocumentStore store)
    {
        try
        {
            return store.Options.Events.Daemon.AsyncMode.ToString();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "Unknown";
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>A shard failure as one sentence, which is all a table cell can hold.</summary>
    private static string? Describe(ShardFailure? failure)
    {
        if (failure is null)
        {
            return null;
        }

        string message = Blank(failure.Message) ?? Blank(failure.ExceptionType) ?? "failed";
        return Blank(failure.ExceptionType) is { } type ? $"{type}: {message}" : message;
    }

    /// <summary>
    /// Reads one fact off the daemon, treating a throwing default interface implementation as "unknown".
    /// </summary>
    private static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// What a coordinator control needs besides the coordinator, so its body can stay a static lambda.
    /// </summary>
    /// <param name="Resolved">The scope, already authorized.</param>
    /// <param name="User">Who asked, for the pause record.</param>
    /// <param name="ControlState">Where the pause this process issued is remembered.</param>
    private readonly record struct DaemonControlContext(
        ResolvedScope Resolved,
        string User,
        DaemonControlState ControlState);

    /// <summary>What the database said, before the tracker and the daemon are consulted.</summary>
    /// <param name="Projections">The static model, from the store's own options.</param>
    /// <param name="Progress">The progression rows this database holds.</param>
    /// <param name="HighWaterMark">The event sequence's high-water mark, or zero when there is none.</param>
    /// <param name="HasEventStore">
    /// Whether this database has an event store at all. <see langword="false" /> is not "nothing has
    /// happened yet": it is "there is nothing here to have happened in", and the studio will not create
    /// one to find out (hard rule 14).
    /// </param>
    /// <param name="ReadAt">When this was read.</param>
    private sealed record StoredProjections(
        IReadOnlyList<ProjectionInfo> Projections,
        IReadOnlyList<ShardState> Progress,
        long HighWaterMark,
        bool HasEventStore,
        DateTimeOffset ReadAt);

    /// <summary>The lease handed out when there is no tracker to watch.</summary>
    private sealed class NullLease : IDisposable
    {
        public static NullLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
