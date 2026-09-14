using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.SampleDomain.Events;
using MartenStudio.Services.Projections;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// The projections screen against a real store, a real daemon and real progress.
/// </summary>
/// <remarks>
/// Everything goes through <see cref="IProjectionDataService" /> rather than around it, so the scope
/// resolution, the capability gating and the audit entries are exercised by the same calls that read the
/// numbers.
/// </remarks>
public class ProjectionsLiveTests(PostgresFixture postgres) : ProjectionsTestBase(postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The projection the daemon has real work to do for.</summary>
    private const string DailySalesName = "DailySales";

    /// <summary>The projection that throws on one event on purpose.</summary>
    private const string ShipmentTrackerName = "ShipmentTracker";

    [PostgresFact]
    public async Task The_table_lists_exactly_what_the_store_registered_with_the_lifecycle_it_registered()
    {
        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));

        view.Projections.Select(x => x.Name).Should().BeEquivalentTo(["OrderSummary", DailySalesName, ShipmentTrackerName]);

        view.Projections.Single(x => x.Name == "OrderSummary").Lifecycle.Should().Be(ProjectionLifecycle.Inline);
        view.Projections.Single(x => x.Name == DailySalesName).Lifecycle.Should().Be(ProjectionLifecycle.Async);
        view.Projections.Single(x => x.Name == ShipmentTrackerName).Lifecycle.Should().Be(ProjectionLifecycle.Async);

        // Every async projection has at least one shard, and a shard identity is {Projection}:{Key}.
        view.Projections.Where(x => x.IsAsync).Should().AllSatisfy(projection =>
            projection.ShardNames.Should().AllSatisfy(shard => shard.Should().StartWith(projection.Name + ":")));
    }

    [PostgresFact]
    public async Task The_daemon_is_hosted_in_this_process_and_the_card_says_so()
    {
        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));

        view.Daemon.Hosting.Should().Be(DaemonHostingState.Hosted);
        view.Daemon.IsRunning.Should().BeTrue();
        view.Daemon.Mode.Should().Be(nameof(DaemonMode.Solo));
        view.NoDaemonAnywhere.Should().BeFalse();
    }

    /// <summary>
    /// Progress is read from the database and measured against <c>FetchHighestEventSequenceNumber</c>, so
    /// it is true whether or not a daemon is hosted here.
    /// </summary>
    [PostgresFact]
    public async Task Progress_catches_up_to_the_high_water_mark_and_the_rows_carry_the_daemons_own_state()
    {
        await WaitUntilCaughtUpAsync(DailySalesName);

        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));

        view.HighWaterMark.Should().BeGreaterThan(0);

        ShardProgress daily = view.Progress.Single(x => x.ProjectionName == DailySalesName);
        daily.HasProgressRow.Should().BeTrue();
        daily.Sequence.Should().BeGreaterThan(0);
        daily.Lag.Should().Be(0);

        // AgentStatus, PauseReason and Failure are deliberately not asserted here: mt_event_progression
        // stores a name and a sequence and nothing else, so those three are only ever populated by the
        // in-process tracker - which is what the live-update test proves.

        // Worst first: the sort is what makes this an operations screen rather than a listing.
        view.Progress.Select(x => x.Lag).Should().BeInDescendingOrder();
    }

    [PostgresFact]
    public async Task Stopping_and_starting_one_agent_reaches_the_real_daemon()
    {
        string shard = await ShardOfAsync(DailySalesName);

        await Fixture.UseAsync(service => service.StopAgentAsync(Fixture.Scope, shard, Token));

        await ProjectionsFixture.WaitForAsync(
            async () => !await HasAgentAsync(shard),
            $"the daemon to drop the agent for {shard}",
            cancellationToken: Token);

        await Fixture.UseAsync(service => service.StartAgentAsync(Fixture.Scope, shard, Token));

        await ProjectionsFixture.WaitForAsync(
            () => HasAgentAsync(shard),
            $"the daemon to run the agent for {shard} again",
            cancellationToken: Token);

        Fixture.ActionLog.GetLatest().Select(x => x.Action).Should().Contain(["StopAgent", "StartAgent"]);
    }

    [PostgresFact]
    public async Task Stopping_and_starting_every_agent_reaches_the_real_daemon()
    {
        await Fixture.UseAsync(service => service.StopAllAsync(Fixture.Scope, Token));

        await ProjectionsFixture.WaitForAsync(
            async () => (await CurrentAgentsAsync()).Count == 0,
            "the daemon to stop every agent",
            cancellationToken: Token);

        await Fixture.UseAsync(service => service.StartAllAsync(Fixture.Scope, Token));

        await ProjectionsFixture.WaitForAsync(
            async () => (await CurrentAgentsAsync()).Count > 0,
            "the daemon to start its agents again",
            cancellationToken: Token);
    }

    [PostgresFact]
    public async Task Restarting_the_high_water_agent_is_accepted_and_audited()
    {
        await Fixture.UseAsync(service => service.RestartHighWaterAgentAsync(Fixture.Scope, Token));

        Fixture.ActionLog.GetLatest().Should().Contain(x => x.Action == "RestartHighWaterAgent" && x.Succeeded);
    }

    /// <summary>
    /// A rebuild runs detached in <see cref="StudioOperationTracker" /> - it is addressed by a handle and
    /// finishes whether or not the page that started it is still open.
    /// </summary>
    [PostgresFact]
    public async Task Rebuilding_a_projection_finishes_and_the_progress_catches_up()
    {
        await WaitUntilCaughtUpAsync(DailySalesName);

        OperationHandle handle = await Fixture.UseAsync(service => service.RebuildAsync(Fixture.Scope, DailySalesName, Token));

        handle.Target.Should().Be(DailySalesName);

        await ProjectionsFixture.WaitForAsync(
            () => Task.FromResult(Fixture.Operations.Find(handle.Id)?.IsRunning == false),
            $"the rebuild of {DailySalesName} to finish",
            TimeSpan.FromMinutes(2),
            Token);

        StudioOperation operation = Fixture.Operations.Find(handle.Id)!;
        operation.State.Should().Be(StudioOperationState.Succeeded, operation.Message);

        await WaitUntilCaughtUpAsync(DailySalesName);

        // The rebuild wrote the documents again rather than emptying them.
        await using IQuerySession session = Fixture.Store.QuerySession();
        (await session.Query<DailySales>().CountAsync(Token)).Should().BeGreaterThan(0);

        Fixture.ActionLog.GetLatest().Select(x => x.Action)
            .Should().Contain(["RebuildProjection", "RebuildStarted", "RebuildFinished"]);
    }

    [PostgresFact]
    public async Task The_high_water_mark_can_be_advanced_and_the_progression_corrected()
    {
        await Fixture.UseAsync(service => service.AdvanceHighWaterMarkAsync(Fixture.Scope, Token));
        await Fixture.UseAsync(service => service.CorrectProgressionAsync(Fixture.Scope, Token));

        Fixture.ActionLog.GetLatest().Should().Contain(x => x.Action == "AdvanceHighWaterMark" && x.Succeeded);
        Fixture.ActionLog.GetLatest().Should().Contain(x => x.Action == "CorrectProgression" && x.Succeeded);

        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));
        view.HighWaterMark.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The live-update proof: with the daemon hosted here, the studio's observer on
    /// <c>IMartenDatabase.Tracker</c> receives a <c>ShardState</c> after an event is appended, without
    /// anything having polled for it.
    /// </summary>
    [PostgresFact]
    public async Task The_tracker_pushes_a_shard_state_after_an_event_is_appended()
    {
        using IDisposable lease = await Fixture.UseAsync(service => service.SubscribeToLiveStateAsync(Fixture.Scope, Token));

        Fixture.LiveState.WatchCount.Should().Be(1, "a daemon hosted here means there is a tracker to observe");

        Guid streamId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using (IDocumentSession session = Fixture.Store.LightweightSession())
        {
            session.Events.StartStream(streamId, new OrderPlaced(streamId, "Live Customer", now));
            await session.SaveChangesAsync(Token);
        }

        await ProjectionsFixture.WaitForAsync(
            () => Task.FromResult(Fixture.LiveState.LastObservedAt(Fixture.Scope.StoreKey, Fixture.Scope.DatabaseId) is not null),
            "the shard state tracker to push a state",
            cancellationToken: Token);

        IReadOnlyDictionary<string, ShardState> latest =
            Fixture.LiveState.Latest(Fixture.Scope.StoreKey, Fixture.Scope.DatabaseId);

        latest.Should().NotBeEmpty();
        latest.Values.Should().AllSatisfy(state => state.ShardName.Should().NotBeNullOrWhiteSpace());

        // And the page reads it: at least one row says its numbers came from the tracker rather than from
        // the progression table.
        await ProjectionsFixture.WaitForAsync(
            async () =>
            {
                ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));
                return view.Progress.Any(x => x.IsLive);
            },
            "a progress row fed by the tracker",
            cancellationToken: Token);

        lease.Dispose();

        Fixture.LiveState.WatchCount.Should().Be(0, "the last page leaving detaches the observer");
    }

    /// <summary>
    /// The demo's poisoned stream produces a real dead letter: the projection threw, the daemon skipped
    /// the event and recorded it. Read here with a direct query - the dead-letter screen itself belongs to
    /// another packet.
    /// </summary>
    [PostgresFact]
    public async Task The_poisoned_stream_produces_a_dead_letter_the_daemon_wrote()
    {
        await ProjectionsFixture.WaitForAsync(
            async () =>
            {
                await using IQuerySession session = Fixture.Store.QuerySession();
                return await session.Query<DeadLetterEvent>().AnyAsync(Token);
            },
            "the daemon to record the poisoned event as a dead letter",
            TimeSpan.FromMinutes(2),
            Token);

        await using IQuerySession letters = Fixture.Store.QuerySession();
        IReadOnlyList<DeadLetterEvent> dead = await letters.Query<DeadLetterEvent>().ToListAsync(Token);

        dead.Should().NotBeEmpty();
        dead.Should().Contain(x => x.ProjectionName == ShipmentTrackerName);
        dead.Should().AllSatisfy(letter =>
        {
            letter.EventSequence.Should().BeGreaterThan(0);
            letter.ExceptionMessage.Should().NotBeNullOrWhiteSpace();
            letter.ExceptionType.Should().NotBeNullOrWhiteSpace();
        });
    }

    /// <summary>
    /// The Overview's tiles read the same facts through a smaller shape, and "could not report" is a value
    /// rather than an exception.
    /// </summary>
    [PostgresFact]
    public async Task The_summary_carries_what_the_overview_tiles_need()
    {
        await WaitUntilCaughtUpAsync(DailySalesName);

        ProjectionSummary summary = await Fixture.UseAsync(service => service.GetSummaryAsync(Fixture.Scope, Token));

        summary.Error.Should().BeNull();
        summary.ProjectionCount.Should().Be(3);
        summary.AsyncProjectionCount.Should().Be(2);
        summary.Daemon.Hosting.Should().Be(DaemonHostingState.Hosted);
    }

    [PostgresFact]
    public async Task A_rebuild_names_the_tables_it_would_rewrite_and_the_events_it_would_replay()
    {
        RebuildScope scope = await Fixture.UseAsync(service => service.DescribeRebuildAsync(Fixture.Scope, DailySalesName, Token));

        scope.ProjectionName.Should().Be(DailySalesName);
        scope.EventsToReplay.Should().BeGreaterThan(0);
        scope.ShardNames.Should().NotBeEmpty();
        scope.TablesAffected.Should().Contain(x => x.Contains("mt_doc_dailysales", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private async Task<string> ShardOfAsync(string projectionName)
    {
        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));
        return view.Projections.Single(x => x.Name == projectionName).ShardNames[0];
    }

    private Task WaitUntilCaughtUpAsync(string projectionName) =>
        ProjectionsFixture.WaitForAsync(
            async () =>
            {
                ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));
                ShardProgress? shard = view.Progress.FirstOrDefault(x => x.ProjectionName == projectionName);
                return shard is { HasProgressRow: true, Lag: 0, Sequence: > 0 };
            },
            $"{projectionName} to catch up with the high water mark",
            TimeSpan.FromMinutes(2),
            Token);

    private async Task<IReadOnlyList<string>> CurrentAgentsAsync()
    {
        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));
        return [.. view.Daemon.Agents.Select(x => x.ShardName)];
    }

    private async Task<bool> HasAgentAsync(string shardName) =>
        (await CurrentAgentsAsync()).Any(x => string.Equals(x, shardName, StringComparison.OrdinalIgnoreCase));
}
