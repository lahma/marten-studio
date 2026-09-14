using JasperFx.Events.Projections;

using MartenStudio.Services;
using MartenStudio.Services.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The projections the page is given, said outright by a test.
/// </summary>
/// <remarks>
/// Hand-written rather than mocked (AGENTS.md's package budget has no mocking library, deliberately):
/// the seam is one interface, and a fake that a test reads like a sentence is worth more here than a
/// call-matching DSL.
/// </remarks>
internal sealed class FakeProjectionDataService : IProjectionDataService
{
    private readonly List<ProjectionInfo> projections = [];
    private readonly List<ShardProgress> progress = [];
    private readonly List<StudioOperation> running = [];

    /// <summary>The daemon card the page is handed.</summary>
    public DaemonStatus Daemon { get; set; } =
        new(DaemonHostingState.Hosted, true, "Solo", [], false, DateTimeOffset.UtcNow, "The async daemon is hosted in this process.");

    /// <summary>The store's high-water mark.</summary>
    public long HighWaterMark { get; set; } = 1_000;

    /// <summary>What <see cref="GetProjectionsAsync" /> throws, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>What a mutating call throws, when a test is about a refusal.</summary>
    public Exception? ActionFailure { get; set; }

    /// <summary>Every mutating call the page made, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>How many live-state leases the page took.</summary>
    public int Subscriptions { get; private set; }

    /// <summary>How many of those it gave back.</summary>
    public int Releases { get; private set; }

    /// <summary>The per-shard replay budget the rebuild dialog is handed.</summary>
    public TimeSpan ShardTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>What <see cref="RebuildAsync" /> answers: whether this call is what started it.</summary>
    public bool RebuildStarts { get; set; } = true;

    /// <summary>Adds one projection with a single shard and that shard's progress.</summary>
    public FakeProjectionDataService WithProjection(
        string name,
        ProjectionLifecycle lifecycle = ProjectionLifecycle.Async,
        long sequence = 1_000,
        string? agentStatus = "Running",
        string? pauseReason = null,
        string? failure = null,
        long? skipped = null,
        bool hasProgressRow = true,
        bool isLive = false)
    {
        string shardName = name + ":All";

        projections.Add(new ProjectionInfo(name, lifecycle, "Projection", 1, name + "Projection", [shardName]));

        if (lifecycle == ProjectionLifecycle.Async)
        {
            progress.Add(new ShardProgress(
                shardName, name, sequence, HighWaterMark, agentStatus, pauseReason, failure,
                DateTimeOffset.UnixEpoch, skipped, null, hasProgressRow, isLive));
        }

        return this;
    }

    /// <summary>
    /// Adds a progression row no registered projection claims - a projection that was removed or renamed.
    /// </summary>
    public FakeProjectionDataService WithUnregisteredShard(string shardName, long sequence = 10)
    {
        int separator = shardName.IndexOf(':', StringComparison.Ordinal);
        string projectionName = separator > 0 ? shardName[..separator] : shardName;

        progress.Add(new ShardProgress(
            shardName, projectionName, sequence, HighWaterMark, null, null, null,
            DateTimeOffset.UnixEpoch, null, null, true, false, IsRegistered: false));

        return this;
    }

    /// <summary>Says the daemon is not hosted in this process, with the explanation the card shows.</summary>
    public FakeProjectionDataService WithNoDaemonHere(string explanation = DaemonAccessor.NotRegisteredExplanation)
    {
        Daemon = new DaemonStatus(DaemonHostingState.NotHostedInThisProcess, false, "Disabled", [], false, null, explanation);
        return this;
    }

    /// <summary>Says the daemon is hosted here but stopped.</summary>
    public FakeProjectionDataService WithStoppedDaemon()
    {
        Daemon = new DaemonStatus(DaemonHostingState.Hosted, false, "Solo", [], false, null, "The async daemon is hosted in this process.");
        return this;
    }

    /// <summary>Adds a running operation for the page to watch.</summary>
    public FakeProjectionDataService WithRunningOperation(string id, string target)
    {
        running.Add(new StudioOperation(
            id, "Rebuild", target, "default", "localhost.marten", "admin",
            DateTimeOffset.UnixEpoch, null, StudioOperationState.Running, null, 1_500));

        return this;
    }

    /// <inheritdoc />
    public Task<ProjectionsView> GetProjectionsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return Task.FromException<ProjectionsView>(Failure);
        }

        List<ShardProgress> sorted = [.. progress.OrderByDescending(x => x.Lag).ThenBy(x => x.ShardName, StringComparer.OrdinalIgnoreCase)];

        return Task.FromResult(new ProjectionsView(projections, sorted, Daemon, HighWaterMark, DateTimeOffset.UnixEpoch));
    }

    /// <inheritdoc />
    public async Task<ProjectionSummary> GetSummaryAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ProjectionsView view = await GetProjectionsAsync(scope, cancellationToken);
        ShardProgress? worst = view.Progress.Count > 0 ? view.Progress[0] : null;

        return new ProjectionSummary(
            view.Projections.Count,
            view.Projections.Count(x => x.IsAsync),
            worst?.Lag ?? 0,
            worst is { Lag: > 0 } ? worst.ShardName : null,
            view.Progress.Count(x => x.IsPaused),
            view.Progress.Count(x => x.HasFailed),
            view.Daemon,
            null);
    }

    /// <inheritdoc />
    public Task<RebuildScope> DescribeRebuildAsync(StudioScope scope, string projectionName, CancellationToken cancellationToken = default)
    {
        Calls.Add("Describe:" + projectionName);

        ProjectionInfo projection = projections.First(x => x.Name == projectionName);

        return Task.FromResult(new RebuildScope(
            projectionName,
            HighWaterMark,
            projection.ShardNames,
            ["studio_sample.mt_doc_" + projectionName.ToLowerInvariant()],
            projection.Lifecycle,
            ShardTimeout));
    }

    /// <inheritdoc />
    public Task<IDisposable> SubscribeToLiveStateAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Subscriptions++;
        return Task.FromResult<IDisposable>(new Lease(this));
    }

    /// <inheritdoc />
    public Task StartAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default) =>
        Record("StartAgent:" + shardName);

    /// <inheritdoc />
    public Task StopAgentAsync(StudioScope scope, string shardName, CancellationToken cancellationToken = default) =>
        Record("StopAgent:" + shardName);

    /// <inheritdoc />
    public Task StartAllAsync(StudioScope scope, CancellationToken cancellationToken = default) => Record("StartAll");

    /// <inheritdoc />
    public Task StopAllAsync(StudioScope scope, CancellationToken cancellationToken = default) => Record("StopAll");

    /// <inheritdoc />
    public Task RestartHighWaterAgentAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Record("RestartHighWater");

    /// <inheritdoc />
    public async Task<OperationStart> RebuildAsync(StudioScope scope, string projectionName, CancellationToken cancellationToken = default)
    {
        await Record("Rebuild:" + projectionName);
        return new OperationStart(new OperationHandle("op1", "Rebuild", projectionName), RebuildStarts);
    }

    /// <inheritdoc />
    public Task AdvanceHighWaterMarkAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Record("AdvanceHighWaterMark");

    /// <inheritdoc />
    public Task CorrectProgressionAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Record("CorrectProgression");

    /// <inheritdoc />
    public StudioOperation? FindOperation(string? operationId) =>
        running.FirstOrDefault(x => x.Id == operationId);

    /// <inheritdoc />
    public async Task<bool> CancelOperationAsync(StudioScope scope, string operationId, CancellationToken cancellationToken = default)
    {
        await Record("CancelOperation:" + operationId);
        return true;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StudioOperation>> RunningOperationsAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StudioOperation>>(running);

    private Task Record(string call)
    {
        Calls.Add(call);
        return ActionFailure is null ? Task.CompletedTask : Task.FromException(ActionFailure);
    }

    private sealed class Lease : IDisposable
    {
        private readonly FakeProjectionDataService owner;

        public Lease(FakeProjectionDataService owner) => this.owner = owner;

        public void Dispose() => owner.Releases++;
    }
}
