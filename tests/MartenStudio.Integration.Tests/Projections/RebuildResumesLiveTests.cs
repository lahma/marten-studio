using JasperFx;

using MartenStudio.Integration.Tests.Health;
using MartenStudio.Services.Projections;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// 1.2-FINAL-FIX-2: after Marten Studio rebuilds a projection on a daemon this process hosts (Solo), the projection's shard runs again.
/// On the sample, <c>DailySales:All</c> read Stopped for about twelve minutes ("not judged: its agent here is Stopped (D05)") and the
/// page said "It runs 1 agent now" while Agents read 2.
/// </summary>
/// <remarks>
/// JasperFx's rebuild stops the projection's running agent, replays through a transient agent, and registers that agent in the daemon's
/// own table (<c>JasperFxAsyncDaemon.rebuildAgent</c>, <c>_agents.AddOrUpdate</c>) - then drains and disposes it, leaving a Stopped
/// agent under the shard's name. The coordinator's loop starts a shard only when it has no agent (or when the agent is Paused for
/// longer than <c>HealthCheckPollingTime</c>), so a Stopped one is left alone. The rebuild returns without restarting the continuous
/// agents by contract (jasperfx#535): the driving caller restarts them, and the studio is that caller
/// (<c>ProjectionDataService.RebuildAndHandBackAsync</c>).
/// </remarks>
public class RebuildResumesLiveTests(PostgresFixture postgres)
{
    private const string Shard = GatedProjection.ProjectionName + ":All";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<StudioOperation> FinishedAsync(DaemonHealthHost host, OperationStart start)
    {
        await ProjectionsFixture.WaitForAsync(
            async () => !(await host.ProjectionsAsync(x => Task.FromResult(x.FindOperation(start.Id))))!.IsRunning,
            "the rebuild to finish",
            TimeSpan.FromSeconds(120),
            Token);

        return (await host.ProjectionsAsync(x => Task.FromResult(x.FindOperation(start.Id))))!;
    }

    private async Task RunningAfterAppendAsync(DaemonHealthHost host, int count)
    {
        await host.AppendGatedAsync(count);
        long highest = await host.HighestSequenceAsync(postgres);
        await host.WaitForShardAsync(Shard, highest, postgres);
        await ProjectionsFixture.WaitForAsync(
            () => Task.FromResult(host.Daemon.StatusFor(Shard) == AgentStatus.Running),
            Shard + " to run",
            TimeSpan.FromSeconds(60),
            Token);
    }

    [PostgresFact]
    public async Task A_projection_rebuilt_through_the_studio_runs_again_on_a_hosted_Solo_daemon()
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "ff_rebuild_resumes", gated: true);
        await host.WaitForDaemonAsync();
        await RunningAfterAppendAsync(host, 3);

        OperationStart start = await host.ProjectionsAsync(x => x.RebuildAsync(host.Scope, GatedProjection.ProjectionName, Token));
        start.Started.Should().BeTrue();

        StudioOperation finished = await FinishedAsync(host, start);
        finished.State.Should().Be(StudioOperationState.Succeeded);
        finished.Message.Should().Be("Completed. Handed " + Shard + " back to the coordinator, which starts it again.");

        // Bounded: the coordinator's loop polls every second here, so a minute is a generous allowance for "again", not for "eventually".
        await ProjectionsFixture.WaitForAsync(
            () => Task.FromResult(host.Daemon.StatusFor(Shard) == AgentStatus.Running),
            Shard + " to run again after its rebuild",
            TimeSpan.FromSeconds(60),
            Token);

        // And it does what a running shard does: events appended now are projected.
        await host.AppendGatedAsync(2);
        long after = await host.HighestSequenceAsync(postgres);
        await host.WaitForShardAsync(Shard, after, postgres);

        // The page's view says the same.
        ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(host.Scope, Token));
        view.Progress.Should().Contain(x => x.ShardName == Shard && x.AgentStatus == "Running");
    }

    /// <summary>
    /// While the studio's own pause is in effect nothing is started, whatever the rebuild did: the pause is the operator's, and the shard
    /// stays down until the daemon is resumed. The stale Stopped agent is cleared all the same, or resuming would leave the shard down.
    /// </summary>
    [PostgresFact]
    public async Task A_rebuild_during_the_studios_pause_starts_nothing_and_resuming_then_starts_the_shard()
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "ff_rebuild_paused", gated: true);
        await host.WaitForDaemonAsync();
        await RunningAfterAppendAsync(host, 3);
        await host.ProjectionsAsync(async x => { await x.PauseDaemonAsync(host.Scope, Token); return true; });

        OperationStart start = await host.ProjectionsAsync(x => x.RebuildAsync(host.Scope, GatedProjection.ProjectionName, Token));
        StudioOperation finished = await FinishedAsync(host, start);

        finished.State.Should().Be(StudioOperationState.Succeeded);
        finished.Message.Should().Contain("pause is in effect").And.Contain("nothing was started");
        host.Daemon.StatusFor(Shard).Should().NotBe(AgentStatus.Running, "the studio's pause stands");

        // The line says "resume the daemon and it starts the shards": it does.
        await host.ProjectionsAsync(async x => { await x.ResumeDaemonAsync(host.Scope, Token); return true; });
        await ProjectionsFixture.WaitForAsync(
            () => Task.FromResult(host.Daemon.StatusFor(Shard) == AgentStatus.Running),
            Shard + " to run after the daemon is resumed",
            TimeSpan.FromSeconds(60),
            Token);
    }

    /// <summary>A cancelled rebuild hands nothing back, and its result line says what state that leaves.</summary>
    [PostgresFact]
    public async Task A_cancelled_rebuild_hands_nothing_back_and_says_so()
    {
        const string gate = "ff-cancelled-rebuild";

        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "ff_rebuild_cancelled", gated: true);
        await host.WaitForDaemonAsync();
        await RunningAfterAppendAsync(host, 1);

        GatedProjection.Close(gate);
        try
        {
            await host.AppendGatedAsync(1, gate);

            OperationStart start = await host.ProjectionsAsync(x => x.RebuildAsync(host.Scope, GatedProjection.ProjectionName, Token));
            await ProjectionsFixture.WaitForAsync(
                () => Task.FromResult(GatedProjection.WaitingAt(gate) > 0),
                "the replay to reach the gate",
                TimeSpan.FromSeconds(60),
                Token);

            (await host.ProjectionsAsync(x => x.CancelOperationAsync(host.Scope, start.Id, Token))).Should().BeTrue();
            GatedProjection.Open(gate);

            StudioOperation finished = await FinishedAsync(host, start);

            finished.State.Should().Be(StudioOperationState.Cancelled);
            finished.Message.Should().Contain("Nothing was handed back to the coordinator");
        }
        finally
        {
            GatedProjection.Open(gate);
        }
    }
}
