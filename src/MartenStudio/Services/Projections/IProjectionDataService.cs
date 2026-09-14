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

    /// <summary>
    /// Starts one shard, if the studio's own pause is in effect. Requires <c>ControlDaemon</c>.
    /// </summary>
    /// <returns>
    /// Whether the daemon was asked, and the reason when it was not. While the coordinator is running it
    /// is never asked: its leadership loop starts every shard missing from <c>CurrentAgents()</c> within
    /// <c>LeadershipPollingTime</c>, so the control would be undone before anybody saw it work.
    /// </returns>
    Task<DaemonControlResult> StartAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops one shard, if the studio's own pause is in effect. Requires <c>ControlDaemon</c>.
    /// </summary>
    /// <returns>Whether the daemon was asked, and the reason when it was not.</returns>
    Task<DaemonControlResult> StopAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses this store's projection coordinator. Requires <c>ControlDaemon</c>.
    /// </summary>
    /// <remarks>
    /// Process-wide, and deliberately so: the coordinator stops its leadership runner and then stops
    /// every agent of every database it has resolved. This is the only stop that holds - asking a daemon
    /// to stop while its coordinator runs is undone at the coordinator's next poll.
    /// </remarks>
    Task PauseDaemonAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes this store's projection coordinator. Requires <c>ControlDaemon</c>.
    /// </summary>
    /// <remarks>
    /// Restarts the leadership runner, which brings the agents back on its first iteration, and forgets
    /// the pause the studio had recorded.
    /// </remarks>
    Task ResumeDaemonAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>Restarts the high-water agent. Requires <c>ControlDaemon</c>.</summary>
    Task RestartHighWaterAgentAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds one projection, detached. Requires <c>RebuildProjections</c>.
    /// </summary>
    /// <returns>
    /// The handle to watch it by, and whether this call is what started it. The rebuild outlives the page
    /// that started it, and a projection already being rebuilt - on this circuit or any other - is
    /// answered with the running rebuild rather than started a second time.
    /// </returns>
    Task<OperationStart> RebuildAsync(StudioScope scope, string projectionName, CancellationToken cancellationToken = default);

    /// <summary>Moves the high-water mark to the latest event. Requires <c>CorrectProgression</c>.</summary>
    Task AdvanceHighWaterMarkAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>Pulls progression back to the highest real sequence. Requires <c>CorrectProgression</c>.</summary>
    Task CorrectProgressionAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>One long-running operation by its handle, or <see langword="null" />.</summary>
    StudioOperation? FindOperation(string? operationId);

    /// <summary>
    /// Asks a running operation to stop. Requires <c>RebuildProjections</c>, and is audited.
    /// </summary>
    /// <returns><see langword="true" /> when something was asked to stop.</returns>
    Task<bool> CancelOperationAsync(StudioScope scope, string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything still running against this scope, newest first.
    /// </summary>
    /// <remarks>
    /// Resolves the scope like every sibling on this interface: the list is small and in-process, but the
    /// store, database and tenant a visitor may address is decided in one place and never by the caller.
    /// </remarks>
    Task<IReadOnlyList<StudioOperation>> RunningOperationsAsync(StudioScope scope, CancellationToken cancellationToken = default);
}
