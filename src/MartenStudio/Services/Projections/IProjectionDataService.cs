namespace MartenStudio.Services.Projections;

/// <summary>
/// What a mutating projection call throws when the daemon it needs is not running in this process.
/// </summary>
/// <remarks>
/// Distinct from a capability refusal and from an authorization refusal, because it is neither: the
/// visitor may well be allowed to do this, and the option may well be on. There is simply nothing here
/// to ask. The page disables those controls for the same reason, but the service refuses regardless of
/// what was rendered (AGENTS.md hard rule 5).
/// </remarks>
internal sealed class StudioDaemonNotHostedException : InvalidOperationException
{
    public StudioDaemonNotHostedException(string explanation) : base(explanation)
    {
    }
}

/// <summary>
/// What the projections screen reads and what it can ask the daemon to do.
/// </summary>
/// <remarks>
/// An interface so a component test can render the page against stated facts rather than a Postgres and
/// a running daemon - which between them are most of what this screen is about, and neither of which
/// belongs in a test about a progress bar.
/// </remarks>
internal interface IProjectionDataService
{
    /// <summary>
    /// The static model, the progress and the daemon card for one scope.
    /// </summary>
    /// <remarks>
    /// Goes through the single-flight snapshot cache, so N circuits polling the same scope cost one
    /// query per interval (plan D10).
    /// </remarks>
    Task<ProjectionsView> GetProjectionsAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same facts reduced to what the Overview's tiles show, with "could not read" as a value rather
    /// than an exception.
    /// </summary>
    Task<ProjectionSummary> GetSummaryAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>What a rebuild of <paramref name="projectionName" /> would touch, for the confirm dialog.</summary>
    Task<RebuildScope> DescribeRebuildAsync(StudioScope scope, string projectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts watching the in-process daemon's shard-state tracker for this scope, if one is hosted here.
    /// </summary>
    /// <returns>
    /// A lease to dispose when the page goes away. Always returns something, so a caller never has to ask
    /// whether the daemon is hosted before it subscribes.
    /// </returns>
    Task<IDisposable> SubscribeToLiveStateAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>Starts one shard. Requires <c>ControlDaemon</c>.</summary>
    Task StartAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default);

    /// <summary>Stops one shard. Requires <c>ControlDaemon</c>.</summary>
    Task StopAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default);

    /// <summary>Starts every shard. Requires <c>ControlDaemon</c>.</summary>
    Task StartAllAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>Stops every shard. Requires <c>ControlDaemon</c>.</summary>
    Task StopAllAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>Restarts the high-water agent. Requires <c>ControlDaemon</c>.</summary>
    Task RestartHighWaterAgentAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds one projection, detached. Requires <c>RebuildProjections</c>.
    /// </summary>
    /// <returns>The handle to watch it by. The rebuild outlives the page that started it.</returns>
    Task<OperationHandle> RebuildAsync(StudioScope scope, string projectionName, CancellationToken cancellationToken = default);

    /// <summary>Moves the high-water mark to the latest event. Requires <c>CorrectProgression</c>.</summary>
    Task AdvanceHighWaterMarkAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>Pulls progression back to the highest real sequence. Requires <c>CorrectProgression</c>.</summary>
    Task CorrectProgressionAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>One long-running operation by its handle, or <see langword="null" />.</summary>
    StudioOperation? FindOperation(string? operationId);

    /// <summary>Everything still running against this scope, newest first.</summary>
    IReadOnlyList<StudioOperation> RunningOperations(StudioScope scope);
}
