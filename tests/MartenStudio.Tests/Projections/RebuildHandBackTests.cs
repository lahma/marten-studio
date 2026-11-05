using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using MartenStudio.Services.Projections;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// 1.2-FINAL-FIX-2: after a rebuild the studio hands the shards the rebuild stopped back to the coordinator. JasperFx's rebuild leaves a
/// Stopped agent registered under each shard's name, and the coordinator starts a shard only when it has no agent, so without this the
/// shard stayed down. The hand-back is a stop of that registered agent, for a shard that was Running or held no agent before, with no
/// pause of the studio's in effect, after a rebuild that succeeded.
/// </summary>
public class RebuildHandBackTests
{
    private const string Shard = "DailySales:All";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>An agent that answers its name and a status a test can change, and nothing else.</summary>
    public class StatusAgent : System.Reflection.DispatchProxy
    {
        private ShardName name = default!;

        /// <summary>What the agent says it is doing.</summary>
        public AgentStatus Status { get; set; }

        internal static ISubscriptionAgent For(string identity, AgentStatus status)
        {
            ShardName.TryParse(identity, out ShardName? parsed).Should().BeTrue();
            ISubscriptionAgent agent = Create<ISubscriptionAgent, StatusAgent>();
            StatusAgent self = (StatusAgent) (object) agent;
            self.name = parsed!;
            self.Status = status;
            return agent;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_Name" => name,
                "get_Status" => Status,
                _ => throw new NotSupportedException($"StatusAgent does not implement {targetMethod?.Name}."),
            };
    }

    /// <summary>What JasperFx's rebuild leaves behind: the shard's agent, registered, Stopped.</summary>
    private static void LeaveStoppedAgents(FakeProjectionDaemon daemon, params string[] shards)
    {
        daemon.Agents.RemoveAll(static x => x.Name.Name == "DailySales");
        foreach (string shard in shards)
        {
            daemon.Agents.Add(StatusAgent.For(shard, AgentStatus.Stopped));
        }
    }

    private static Task<string?> RunAsync(FakeProjectionDaemon daemon, bool loop = true, bool paused = false) =>
        ProjectionDataService.RebuildAndHandBackAsync(daemon, "DailySales", TimeSpan.FromHours(1), loop, () => paused, Token);

    [Fact]
    public async Task A_shard_that_was_running_is_handed_back_and_the_result_line_says_so()
    {
        using var daemon = new FakeProjectionDaemon();
        daemon.Agents.Add(StatusAgent.For(Shard, AgentStatus.Running));
        daemon.OnRebuild = () => LeaveStoppedAgents(daemon, Shard);

        string? line = await RunAsync(daemon);

        daemon.StoppedAgents.Should().Equal(Shard);
        line.Should().Be("Completed. Handed DailySales:All back to the coordinator, which starts it again.");
    }

    [Fact]
    public async Task A_shard_that_held_no_agent_before_is_handed_back_too()
    {
        using var daemon = new FakeProjectionDaemon();
        daemon.OnRebuild = () => LeaveStoppedAgents(daemon, Shard, "DailySales:Other");

        string? line = await RunAsync(daemon);

        daemon.StoppedAgents.Should().Equal(Shard, "DailySales:Other");
        line.Should().Be("Completed. Handed DailySales:All, DailySales:Other back to the coordinator, which starts them again.");
    }

    [Fact]
    public async Task A_shard_that_was_stopped_or_paused_before_the_rebuild_is_handed_back_too()
    {
        // The studio's own Stop removes the agent, so a Stopped agent left after a rebuild is the rebuild's (or a failure's) leftover,
        // never an operator's choice; and a Paused one the coordinator would have restarted itself.
        using var daemon = new FakeProjectionDaemon();
        daemon.Agents.Add(StatusAgent.For(Shard, AgentStatus.Stopped));
        daemon.Agents.Add(StatusAgent.For("DailySales:Other", AgentStatus.Paused));
        daemon.OnRebuild = () => LeaveStoppedAgents(daemon, Shard, "DailySales:Other");

        string? line = await RunAsync(daemon);

        daemon.StoppedAgents.Should().Equal(Shard, "DailySales:Other");
        line.Should().Be("Completed. Handed DailySales:All, DailySales:Other back to the coordinator, which starts them again.");
    }

    [Fact]
    public async Task An_agent_the_coordinator_replaced_before_the_stop_is_left_alone()
    {
        using var daemon = new FakeProjectionDaemon();
        daemon.OnRebuild = () => LeaveStoppedAgents(daemon, Shard);

        // The first CurrentAgents() call after the rebuild lists the Stopped agent; by the second (the re-read before the stop) a
        // running one has taken its place.
        daemon.OnCurrentAgents = call =>
        {
            if (call == 2)
            {
                daemon.Agents.Clear();
                daemon.Agents.Add(StatusAgent.For(Shard, AgentStatus.Running));
            }
        };

        string? line = await RunAsync(daemon);

        daemon.StoppedAgents.Should().BeEmpty("the agent now registered is another instance, and running");
        line.Should().BeNull();
    }

    [Fact]
    public async Task A_long_list_of_shards_is_summarised_in_the_line()
    {
        using var daemon = new FakeProjectionDaemon();
        string[] shards = [.. Enumerable.Range(1, 42).Select(static i => "DailySales:S" + i)];
        daemon.OnRebuild = () => LeaveStoppedAgents(daemon, shards);

        string? line = await RunAsync(daemon);

        daemon.StoppedAgents.Should().HaveCount(42);
        line.Should().Be("Completed. Handed DailySales:S1, DailySales:S2, DailySales:S3, DailySales:S4, DailySales:S5 and 37 more back to the coordinator, which starts them again.");
    }

    [Fact]
    public async Task A_rebuild_that_returned_because_it_was_cancelled_is_cancelled_and_hands_nothing_back()
    {
        using var daemon = new FakeProjectionDaemon();
        using var cancellation = new CancellationTokenSource();
        daemon.Agents.Add(StatusAgent.For(Shard, AgentStatus.Running));
        daemon.OnRebuild = () =>
        {
            LeaveStoppedAgents(daemon, Shard);
            cancellation.Cancel();
        };

        Func<Task> run = () => ProjectionDataService.RebuildAndHandBackAsync(
            daemon, "DailySales", TimeSpan.FromHours(1), true, () => false, cancellation.Token);

        (await run.Should().ThrowAsync<StudioOperationCancelledException>())
            .WithMessage("Cancelled. Nothing was handed back*");
        daemon.StoppedAgents.Should().BeEmpty();
    }

    [Fact]
    public async Task While_the_studios_pause_is_in_effect_the_stale_agent_is_cleared_and_the_line_says_nothing_was_started()
    {
        using var daemon = new FakeProjectionDaemon();
        daemon.Agents.Add(StatusAgent.For(Shard, AgentStatus.Running));
        daemon.OnRebuild = () => LeaveStoppedAgents(daemon, Shard);

        string? line = await RunAsync(daemon, paused: true);

        // The coordinator is paused, so removing the agent starts nothing; without it resuming would leave the shard down.
        daemon.StoppedAgents.Should().Equal(Shard);
        line.Should().Be(
            "Completed. Marten Studio's pause is in effect, so nothing was started; cleared the Stopped agent the rebuild left for DailySales:All, which resuming the daemon starts.");
    }

    [Fact]
    public async Task While_the_studios_pause_is_in_effect_a_shard_that_was_stopped_before_is_cleared_too()
    {
        using var daemon = new FakeProjectionDaemon();
        daemon.Agents.Add(StatusAgent.For(Shard, AgentStatus.Stopped));
        daemon.OnRebuild = () => LeaveStoppedAgents(daemon, Shard);

        string? line = await RunAsync(daemon, paused: true);

        daemon.StoppedAgents.Should().Equal(Shard);
        line.Should().Contain("nothing was started").And.Contain("DailySales:All");
    }

    [Fact]
    public async Task Nothing_is_handed_back_where_no_coordinator_loop_would_start_it()
    {
        using var daemon = new FakeProjectionDaemon();
        daemon.Agents.Add(StatusAgent.For(Shard, AgentStatus.Running));
        daemon.OnRebuild = () => LeaveStoppedAgents(daemon, Shard);

        string? line = await RunAsync(daemon, loop: false);

        daemon.StoppedAgents.Should().BeEmpty();
        line.Should().Contain("no coordinator loop").And.Contain("Start buttons");
    }

    [Fact]
    public async Task A_failed_rebuild_hands_nothing_back_and_says_what_state_it_leaves()
    {
        using var daemon = new FakeProjectionDaemon();
        daemon.Agents.Add(StatusAgent.For(Shard, AgentStatus.Running));
        daemon.OnRebuild = () => LeaveStoppedAgents(daemon, Shard);
        daemon.RebuildFailure = new InvalidOperationException("the replay timed out");

        Func<Task> run = () => RunAsync(daemon);

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("the replay timed out. Nothing was handed back to the coordinator: the shards of DailySales are as the rebuild left them.");
        daemon.StoppedAgents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cancelled_rebuild_hands_nothing_back_and_is_recorded_as_cancelled_with_that_sentence()
    {
        using var daemon = new FakeProjectionDaemon();
        daemon.Agents.Add(StatusAgent.For(Shard, AgentStatus.Running));
        daemon.RebuildFailure = new OperationCanceledException();

        Func<Task> run = () => RunAsync(daemon);

        (await run.Should().ThrowAsync<StudioOperationCancelledException>())
            .WithMessage("Cancelled. Nothing was handed back to the coordinator: the shards of DailySales are as the rebuild left them.");
        daemon.StoppedAgents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_projection_with_nothing_stopped_says_nothing_extra()
    {
        using var daemon = new FakeProjectionDaemon();

        (await RunAsync(daemon)).Should().BeNull("the operation then reads 'Completed.'");
    }

    [Fact]
    public async Task The_tracker_records_the_result_line_as_the_operations_message_and_a_cancellation_with_its_own()
    {
        using var tracker = new StudioOperationTracker(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StudioOperationTracker>.Instance,
            new StudioActionLogService(),
            TimeProvider.System,
            null);

        OperationStart done = tracker.StartReporting(
            "Rebuild", "A", "default", "db", "ada", StudioCapability.RebuildProjections, _ => Task.FromResult<string?>("Completed. Handed A:All back."));
        OperationStart cancelled = tracker.StartReporting(
            "Rebuild", "B", "default", "db", "ada", StudioCapability.RebuildProjections,
            _ => Task.FromException<string?>(new StudioOperationCancelledException("Cancelled. Nothing was handed back.")));

        await WaitAsync(() => tracker.Find(done.Id)?.IsRunning == false && tracker.Find(cancelled.Id)?.IsRunning == false);

        tracker.Find(done.Id)!.Message.Should().Be("Completed. Handed A:All back.");
        tracker.Find(done.Id)!.State.Should().Be(StudioOperationState.Succeeded);
        tracker.Find(cancelled.Id)!.Message.Should().Be("Cancelled. Nothing was handed back.");
        tracker.Find(cancelled.Id)!.State.Should().Be(StudioOperationState.Cancelled);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Yield();
            timeout.Token.ThrowIfCancellationRequested();
        }
    }
}
