using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

using Marten;
using Marten.Schema;

using MartenStudio.Services.Live;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// row. <b>Progress</b> comes from the database - <c>AllProjectionProgress</c> joined with
/// <c>FetchHighestEventSequenceNumber</c> - and is true whether or not a daemon is hosted here.
/// <b>Live state</b> comes from the in-process shard tracker and only refines the numbers between polls.
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

    private readonly StudioScopeResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly DaemonAccessor daemons;
    private readonly StudioSnapshotCache cache;
    private readonly StudioLiveState liveState;
    private readonly StudioOperationTracker operations;
    private readonly DaemonControlState controlState;
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
        this.options = options;
        this.logger = logger;
    }

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

        return new ProjectionsView(stored.Projections, progress, daemon, stored.HighWaterMark, stored.ReadAt);
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

        long highWaterMark = await resolved.Database
            .FetchHighestEventSequenceNumber(cancellationToken)
            .ConfigureAwait(false);

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
            scope, "PauseDaemon",
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
            scope, "ResumeDaemon",
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

    private static async Task<StoredProjections> ReadStoredAsync(ResolvedScope resolved, CancellationToken cancellationToken)
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

        long highWaterMark = await resolved.Database
            .FetchHighestEventSequenceNumber(cancellationToken)
            .ConfigureAwait(false);

        // AdvancedOperations.AllProjectionProgress(tenantId, ct) is not a filter: it resolves the tenant's
        // database and returns every progression row that database has (verified against Marten 9.35's
        // AdvancedOperations). It is used here because a tenant names a database under database-per-tenant
        // tenancy, and the rows it returns are exactly the rows the resolved database holds - which is
        // what IMartenDatabase.AllProjectionProgress answers when no tenant is selected.
        IReadOnlyList<ShardState> rows = resolved.TenantId is { Length: > 0 } tenantId
            ? await resolved.Store.Advanced.AllProjectionProgress(tenantId, cancellationToken).ConfigureAwait(false)
            : await resolved.Database.AllProjectionProgress(cancellationToken).ConfigureAwait(false);

        return new StoredProjections(projections, rows, highWaterMark, DateTimeOffset.UtcNow);
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

        // What this process itself paused, which is the only pause anybody can honestly report: the
        // coordinator has PauseAsync and ResumeAsync and no public flag between them.
        StudioDaemonPause? pause = controlState.Find(resolved.Registration.Key);

        DaemonHosting hosting = await daemons.ForScopeAsync(resolved, cancellationToken).ConfigureAwait(false);
        if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
        {
            return new DaemonStatus(
                DaemonHostingState.NotHostedInThisProcess, false, mode, [], false, null, hosting.Explanation,
                PausedByStudio: null,
                LeadershipPollingMilliseconds: pollingMilliseconds,
                CoordinatedDatabases: hosting.Databases);
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
            hosting.Databases);
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
                string reason = DaemonControlMessages.AgentControlNeedsPause(LeadershipPollingTimeOf(resolved.Store));

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
    /// The target of the audit entry is the store key, because that is the granularity the operation
    /// actually has: <c>PauseAsync()</c> stops the coordinator's leadership runner and then stops every
    /// daemon it has resolved, across every database of that store. Recording one database's identity as
    /// the target would make the entry narrower than the thing that happened.
    /// </remarks>
    private async Task CoordinatorControlAsync(
        StudioScope scope,
        string action,
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

            MartenCoordinator coordinator = daemons.CoordinatorForScope(resolved)
                ?? throw new StudioDaemonNotHostedException(DaemonAccessor.NotRegisteredExplanation);

            string user = await authorization.UserNameAsync().ConfigureAwait(false);
            logger.DaemonControlRequested(user, action, target, resolved.Registration.Key, resolved.Database.Id.Identity);

            await operation(coordinator, new DaemonControlContext(resolved, user, controlState)).ConfigureAwait(false);

            audit.Record(action, target, true, DaemonControlMessages.PauseIsProcessWide, capability, scope);
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
    private sealed record StoredProjections(
        IReadOnlyList<ProjectionInfo> Projections,
        IReadOnlyList<ShardState> Progress,
        long HighWaterMark,
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
