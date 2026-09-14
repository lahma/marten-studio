using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

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

    // ------------------------------------------------------------------------------------------------
    // Daemon control (P5-fix-2)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The stop that holds, and the proof that it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This replaces <c>Stopping_and_starting_every_agent_reaches_the_real_daemon</c>, which asked the
    /// <em>daemon</em> to stop everything and then waited for zero agents. That wait could only ever
    /// pass by sampling the gap: <c>ProjectionCoordinatorBase</c>'s leadership loop starts every shard
    /// missing from <c>CurrentAgents()</c> on its next pass, so the agents were back within a
    /// <c>LeadershipPollingTime</c>. It passed locally and timed out on both CI runs.
    /// </para>
    /// <para>
    /// The holding assertion below is what makes this test non-vacuous, and it is exactly what the old
    /// code could not have satisfied at any budget.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Pausing_leaves_no_agents_for_at_least_two_leadership_polls_and_resuming_brings_them_back()
    {
        TimeSpan poll = Fixture.ConfiguredLeadershipPollingTime;

        try
        {
            await Fixture.UseAsync(service => service.PauseDaemonAsync(Fixture.Scope, Token));

            Fixture.ControlState.Find(Fixture.Scope.StoreKey).Should().NotBeNull("the studio records its own pauses");

            await ProjectionsFixture.WaitForAsync(
                async () => (await CurrentAgentsAsync()).Count == 0,
                "the paused coordinator to leave no agents running",
                StopBudget,
                Token);

            // The half the old test could not have: it stays that way. Two leadership polls plus a
            // second, so a coordinator that was going to restart them has had two chances to.
            await ProjectionsFixture.AssertHoldsForAsync(
                async () => (await CurrentAgentsAsync()).Count == 0,
                "a paused coordinator leaving no agents running",
                (2 * poll) + TimeSpan.FromSeconds(1),
                Token);

            ProjectionsView paused = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));

            paused.Daemon.IsPausedByStudio.Should().BeTrue();
            paused.Daemon.PausedByStudio!.StoreKey.Should().Be(Fixture.Scope.StoreKey);
            paused.Daemon.CanControlAgents.Should().BeTrue("per-agent control is the point of pausing");
            paused.Daemon.LeadershipPollingTime.Should().Be(poll, "the card quotes the store's own setting");

            Fixture.ActionLog.GetLatest().Should().Contain(x => x.Action == "PauseDaemon" && x.Succeeded);
        }
        finally
        {
            // Even when an assertion above failed: every other test in this class needs a running daemon,
            // and xunit v3 orders by test-case id rather than by source order.
            await Fixture.UseAsync(service => service.ResumeDaemonAsync(Fixture.Scope, Token));
        }

        await ProjectionsFixture.WaitForAsync(
            async () => (await CurrentAgentsAsync()).Count > 0,
            "the resumed coordinator to bring its agents back",
            cancellationToken: Token);

        Fixture.ControlState.Find(Fixture.Scope.StoreKey).Should().BeNull("resuming forgets the pause");

        ProjectionsView resumed = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));

        resumed.Daemon.IsPausedByStudio.Should().BeFalse();
        resumed.Daemon.CanControlAgents.Should().BeFalse();
        resumed.Daemon.AgentControlRefusal.Should().Contain("LeadershipPollingTime");

        Fixture.ActionLog.GetLatest().Should().Contain(x => x.Action == "ResumeDaemon" && x.Succeeded);
    }

    /// <summary>
    /// While the coordinator is paused, one agent can be started and it stays started.
    /// </summary>
    [PostgresFact]
    public async Task While_paused_one_agent_can_be_started_and_nothing_stops_it_again()
    {
        string shard = await ShardOfAsync(DailySalesName);
        TimeSpan poll = Fixture.ConfiguredLeadershipPollingTime;

        try
        {
            await Fixture.UseAsync(service => service.PauseDaemonAsync(Fixture.Scope, Token));

            await ProjectionsFixture.WaitForAsync(
                async () => (await CurrentAgentsAsync()).Count == 0,
                "the paused coordinator to leave no agents running",
                StopBudget,
                Token);

            DaemonControlResult started = await Fixture.UseAsync(
                service => service.StartAgentAsync(Fixture.Scope, shard, Token));

            started.Applied.Should().BeTrue();
            started.Reason.Should().BeNull();

            await ProjectionsFixture.WaitForAsync(
                () => HasAgentAsync(shard),
                $"the daemon to run the agent for {shard}",
                cancellationToken: Token);

            await ProjectionsFixture.AssertHoldsForAsync(
                () => HasAgentAsync(shard),
                $"the agent for {shard} staying started while the coordinator is paused",
                (2 * poll) + TimeSpan.FromSeconds(1),
                Token);

            DaemonControlResult stopped = await Fixture.UseAsync(
                service => service.StopAgentAsync(Fixture.Scope, shard, Token));

            stopped.Applied.Should().BeTrue();

            await ProjectionsFixture.WaitForAsync(
                async () => !await HasAgentAsync(shard),
                $"the daemon to drop the agent for {shard}",
                StopBudget,
                Token);

            await ProjectionsFixture.AssertHoldsForAsync(
                async () => !await HasAgentAsync(shard),
                $"the agent for {shard} staying stopped while the coordinator is paused",
                (2 * poll) + TimeSpan.FromSeconds(1),
                Token);

            Fixture.ActionLog.GetLatest().Should().Contain(x => x.Action == "StartAgent" && x.Succeeded);
            Fixture.ActionLog.GetLatest().Should().Contain(x => x.Action == "StopAgent" && x.Succeeded);
        }
        finally
        {
            await Fixture.UseAsync(service => service.ResumeDaemonAsync(Fixture.Scope, Token));
        }

        await ProjectionsFixture.WaitForAsync(
            async () => (await CurrentAgentsAsync()).Count > 0,
            "the resumed coordinator to bring its agents back",
            cancellationToken: Token);
    }

    /// <summary>
    /// With the coordinator running, a per-agent control is refused rather than performed - and the
    /// refusal is a result the page renders as a hint, not an exception and not a capability denial.
    /// </summary>
    [PostgresFact]
    public async Task While_the_coordinator_runs_per_agent_control_is_refused_and_changes_nothing()
    {
        string shard = await ShardOfAsync(DailySalesName);
        TimeSpan poll = Fixture.ConfiguredLeadershipPollingTime;

        await ProjectionsFixture.WaitForAsync(
            () => HasAgentAsync(shard),
            $"the coordinator to be running the agent for {shard}",
            cancellationToken: Token);

        IReadOnlyList<string> before = await CurrentAgentsAsync();

        DaemonControlResult stop = await Fixture.UseAsync(
            service => service.StopAgentAsync(Fixture.Scope, shard, Token));
        DaemonControlResult start = await Fixture.UseAsync(
            service => service.StartAgentAsync(Fixture.Scope, shard, Token));

        foreach (DaemonControlResult result in new[] { stop, start })
        {
            result.Applied.Should().BeFalse();
            result.Reason.Should().Contain("LeadershipPollingTime");
            result.Reason.Should().Contain("pause the daemon first");
        }

        // The reason quotes this store's own interval, not the 5 s default.
        stop.Reason.Should().Contain($"every {poll.TotalSeconds:0.#} s");

        // And nothing moved: the agent set is what it was, two leadership polls later.
        await ProjectionsFixture.AssertHoldsForAsync(
            async () => (await CurrentAgentsAsync()).SequenceEqual(before, StringComparer.OrdinalIgnoreCase),
            "the daemon's agent set being unchanged by a refused control",
            (2 * poll) + TimeSpan.FromSeconds(1),
            Token);

        // Audited as an attempt that did nothing, and never as a success.
        IReadOnlyList<StudioActionLogEntry> log = Fixture.ActionLog.GetLatest();

        log.Should().Contain(x =>
            x.Action == "StopAgent"
            && !x.Succeeded
            && x.Capability == nameof(StudioCapability.ControlDaemon)
            && x.Message!.Contains("LeadershipPollingTime", StringComparison.Ordinal));

        log.Where(x => x.Action is "StartAgent" or "StopAgent")
            .Should().NotContain(x => x.Succeeded, "nothing was applied, so nothing may be recorded as done");
    }

    /// <summary>
    /// Marten's own bookkeeping rows are not projections, whatever they do to their sequence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three bookkeeping rows, written straight into <c>mt_event_progression</c> because Marten writes
    /// them only under conditions a test cannot force. What each proves differs: Marten 9.35's
    /// <c>ProjectionProgressStatement</c> already excludes <c>HighWaterAllocationFence</c> and
    /// <c>HighWaterStuckGap</c> at the SQL level, so for those two the studio's predicate is a belt to
    /// Marten's braces - but <c>HighWaterMark:{tenant}</c> is excluded by nothing, and it is the one that
    /// would otherwise arrive as an unregistered shard with a lag of its own.
    /// </para>
    /// <para>
    /// A fourth row, <c>RetiredProjection:All</c>, is the control and is what makes the rest
    /// non-vacuous: it is written the same way, at the same sequence, and it <em>does</em> arrive - as an
    /// unregistered shard, and as the worst lag on the screen. Without it "no bookkeeping row appeared"
    /// would also pass on a read path that had stopped seeing progression rows at all.
    /// </para>
    /// <para>
    /// Everything is written at sequence 1 because the failure this is about is a sort: the table is
    /// ordered by lag descending, so a bookkeeping row that trails by design takes the top of an
    /// operations screen and the Overview's worst-lag tile with it.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Marten_bookkeeping_rows_are_never_projections_and_never_the_worst_lag()
    {
        const string control = "RetiredProjection:All";

        string[] bookkeeping = ["HighWaterAllocationFence", "HighWaterStuckGap", "HighWaterMark:acme"];
        string[] rows = [.. bookkeeping, control];

        try
        {
            await WriteProgressionRowsAsync(rows, sequence: 1);

            // They really are in the table - otherwise everything below would pass by there being
            // nothing to find.
            (await ProgressionNamesAsync()).Should().Contain(rows);

            // The control arrives. This is the read path working, stated before anything is asserted
            // about what does not arrive.
            await ProjectionsFixture.WaitForAsync(
                async () =>
                {
                    ProjectionsView view = await Fixture.UseAsync(
                        service => service.GetProjectionsAsync(Fixture.Scope, Token));

                    return view.UnregisteredShards.Any(x => x.ShardName == control);
                },
                $"the control row {control} to arrive as an unregistered shard",
                cancellationToken: Token);

            // And no bookkeeping row ever does - held across the snapshot cache's whole time-to-live
            // several times over, so these are fresh queries and not the entry that was already in hand.
            await ProjectionsFixture.AssertHoldsForAsync(
                async () =>
                {
                    ProjectionsView view = await Fixture.UseAsync(
                        service => service.GetProjectionsAsync(Fixture.Scope, Token));

                    return bookkeeping.All(row =>
                        view.Progress.All(x => x.ShardName != row)
                        && view.UnregisteredShards.All(x => x.ShardName != row)
                        && view.Projections.All(x => x.Name != row));
                },
                "Marten's bookkeeping rows staying out of the projections table",
                TimeSpan.FromSeconds(5),
                Token);

            ProjectionSummary summary = await Fixture.UseAsync(service => service.GetSummaryAsync(Fixture.Scope, Token));

            bookkeeping.Should().NotContain(
                x => x == summary.WorstShardName,
                "a bookkeeping row is never what the Overview's worst-lag tile is about");
        }
        finally
        {
            await DeleteProgressionRowsAsync(rows);
        }
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

        OperationStart handle = await Fixture.UseAsync(service => service.RebuildAsync(Fixture.Scope, DailySalesName, Token));

        handle.Started.Should().BeTrue();
        handle.Handle.Target.Should().Be(DailySalesName);

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

        // B1: the audit says which per-shard budget was handed to Marten, so a rebuild that ran out of
        // time can be told apart from one that failed - and the number is in the record either way.
        Fixture.ActionLog.GetLatest()
            .Should().Contain(x => x.Action == "RebuildProjection" && x.Message!.Contains("per-shard timeout", StringComparison.Ordinal));
    }

    /// <summary>
    /// B3: two rebuilds of one projection would replay the same events into the same tables at once. The
    /// second request is answered with the first, and only one operation exists.
    /// </summary>
    [PostgresFact]
    public async Task Two_rebuilds_of_one_projection_in_quick_succession_yield_one_operation()
    {
        await WaitUntilCaughtUpAsync(ShipmentTrackerName);

        using IServiceScope scope = Fixture.CreateScope();
        IProjectionDataService service = ProjectionsFixture.ServiceIn(scope);

        OperationStart first = await service.RebuildAsync(Fixture.Scope, ShipmentTrackerName, Token);

        // A second circuit, resolved separately, asking for the same thing before the first has finished.
        OperationStart second = await Fixture.UseAsync(other => other.RebuildAsync(Fixture.Scope, ShipmentTrackerName, Token));

        second.Id.Should().Be(first.Id);
        second.Started.Should().BeFalse("one rebuild of a projection runs at a time");

        Fixture.Operations.All()
            .Count(x => x.Kind == "Rebuild" && x.Target == ShipmentTrackerName)
            .Should().Be(1);

        await ProjectionsFixture.WaitForAsync(
            () => Task.FromResult(Fixture.Operations.Find(first.Id)?.IsRunning == false),
            $"the rebuild of {ShipmentTrackerName} to finish",
            TimeSpan.FromMinutes(2),
            Token);

        Fixture.ActionLog.GetLatest()
            .Should().Contain(x => x.Action == "RebuildProjection" && x.Message!.Contains("Already running", StringComparison.Ordinal));
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

    /// <summary>
    /// How long a pause is given to leave no agents running.
    /// </summary>
    /// <remarks>
    /// <c>DaemonSettings.StopAndDrainTimeout</c> is five seconds and applies <em>per agent</em>: a pause
    /// drains each one in turn, and this fixture's store has more than one async projection. Sixty
    /// seconds is generous rather than tight on purpose - the thing being measured is whether the agents
    /// come back, and the wait is on the condition either way.
    /// </remarks>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Writes bookkeeping rows straight into <c>mt_event_progression</c>.
    /// </summary>
    /// <remarks>
    /// Raw SQL, in a test, deliberately: Marten writes <c>HighWaterAllocationFence</c> and
    /// <c>HighWaterStuckGap</c> only under high-water conditions a test cannot force, and
    /// <c>HighWaterMark:{tenant}</c> only under per-tenant high water. The studio's own rule - no SQL
    /// outside <c>Internal/Sql</c> - is about the shipped library; this is the fixture stating a database
    /// state, which is the only way to state this one.
    /// </remarks>
    private async Task WriteProgressionRowsAsync(IEnumerable<string> names, long sequence)
    {
        await using NpgsqlConnection connection = await OpenAsync();

        foreach (string name in names)
        {
            await using NpgsqlCommand command = connection.CreateCommand();

            command.CommandText =
                $"insert into {Fixture.EventSchema}.mt_event_progression (name, last_seq_id) values (@name, @seq) " +
                "on conflict (name) do update set last_seq_id = excluded.last_seq_id";

            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("seq", sequence);

            await command.ExecuteNonQueryAsync(Token);
        }
    }

    private async Task DeleteProgressionRowsAsync(IEnumerable<string> names)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();

        command.CommandText = $"delete from {Fixture.EventSchema}.mt_event_progression where name = ANY(@names)";
        command.Parameters.AddWithValue("names", names.ToArray());

        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<IReadOnlyList<string>> ProgressionNamesAsync()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();

        command.CommandText = $"select name from {Fixture.EventSchema}.mt_event_progression";

        List<string> names = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// A connection of the test's own, opened from the same database the studio resolved.
    /// </summary>
    private async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection connection = (NpgsqlConnection) Fixture.Database.CreateConnection();
        await connection.OpenAsync(Token);
        return connection;
    }
}
