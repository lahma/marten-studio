using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;

using Marten;
using Marten.Schema;

using MartenStudio.Services.Live;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    /// The progression row Marten keeps the high-water mark in, which is not a shard and must not be
    /// rendered as one.
    /// </summary>
    private const string HighWaterMarkRowName = "HighWaterMark";

    private readonly StudioScopeResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly DaemonAccessor daemons;
    private readonly StudioSnapshotCache cache;
    private readonly StudioLiveState liveState;
    private readonly StudioOperationTracker operations;
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
    public Task StartAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardName);

        return ControlAsync(
            scope, StudioCapability.ControlDaemon, "StartAgent", shardName,
            (daemon, token) => daemon.StartAgentAsync(shardName, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shardName);

        return ControlAsync(
            scope, StudioCapability.ControlDaemon, "StopAgent", shardName,
            // StopAgentAsync takes the exception that stopped it rather than a token: null means "asked to".
            (daemon, _) => daemon.StopAgentAsync(shardName, null),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task StartAllAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        ControlAsync(
            scope, StudioCapability.ControlDaemon, "StartAllAgents", "(all shards)",
            static (daemon, _) => daemon.StartAllAsync(),
            cancellationToken);

    /// <inheritdoc />
    public Task StopAllAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        ControlAsync(
            scope, StudioCapability.ControlDaemon, "StopAllAgents", "(all shards)",
            static (daemon, _) => daemon.StopAllAsync(),
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
            if (row.ShardName is { Length: > 0 } name && !IsHighWaterRow(name))
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

        DaemonHosting hosting = await daemons.ForScopeAsync(resolved, cancellationToken).ConfigureAwait(false);
        if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
        {
            return new DaemonStatus(DaemonHostingState.NotHostedInThisProcess, false, mode, [], false, null, hosting.Explanation);
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
            hosting.Explanation);
    }

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

    private static bool IsHighWaterRow(string shardName) =>
        string.Equals(shardName, HighWaterMarkRowName, StringComparison.OrdinalIgnoreCase);

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
