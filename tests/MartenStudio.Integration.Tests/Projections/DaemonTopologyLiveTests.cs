using JasperFx.Events.Daemon;

using MartenStudio.Integration.Tests.Health;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// M7 under Solo, live: the card says this process runs every shard itself, and while Marten Studio's pause holds, a Stop
/// with no agent here and a Start of an agent already running are refused as the no-ops they are - while the Start that
/// starts something is made.
/// </summary>
public class DaemonTopologySoloLiveTests(PostgresFixture postgres) : ProjectionsTestBase(postgres)
{
    private const string Shard = "DailySales:All";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Solo_runs_every_shard_here_and_a_paused_node_refuses_the_starts_and_stops_that_would_do_nothing()
    {
        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));

        view.Daemon.Coordination.Should().Be(DaemonCoordination.LeadershipLoop);
        view.Daemon.Distribution.Should().Be(DaemonDistribution.Solo);
        view.Daemon.IsHotCold.Should().BeFalse();
        view.Daemon.LeadsHere(Shard).Should().BeTrue("Solo holds every set, with no lock");
        DaemonTopologyWording.Describe(view.Daemon).Should().StartWith("Solo: this process runs every async shard of this database itself");

        try
        {
            await Fixture.UseAsync(service => service.PauseDaemonAsync(Fixture.Scope, Token));

            await ProjectionsFixture.WaitForAsync(
                async () => (await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token))).Daemon.Agents.Count == 0,
                "the paused coordinator to leave no agents running",
                cancellationToken: Token);

            DaemonControlResult stop = await Fixture.UseAsync(service => service.StopAgentAsync(Fixture.Scope, Shard, Token));
            stop.Applied.Should().BeFalse();
            stop.Reason.Should().Be(DaemonTopologyWording.NothingToStop(Shard, hotCold: false), "the pause stopped every agent");

            DaemonControlResult start = await Fixture.UseAsync(service => service.StartAgentAsync(Fixture.Scope, Shard, Token));
            start.Applied.Should().BeTrue(start.Reason);

            DaemonControlResult again = await Fixture.UseAsync(service => service.StartAgentAsync(Fixture.Scope, Shard, Token));
            again.Applied.Should().BeFalse();
            again.Reason.Should().Be(DaemonTopologyWording.AlreadyRunning(Shard), "JasperFx's start is idempotent, and started nothing");

            Fixture.ActionLog.GetLatest().Where(static x => x.Action is "StartAgent" or "StopAgent")
                .Select(static x => x.Succeeded).Should().BeEquivalentTo([false, true, false], "two refusals and one start, each audited");
        }
        finally
        {
            await Fixture.UseAsync(service => service.ResumeDaemonAsync(Fixture.Scope, Token));
        }
    }
}

/// <summary>
/// M7 under HotCold, external management and no daemon, live: which agents this node runs, and what Start, Stop and Rewind
/// do here - from the leadership the coordinator's own distributor holds, at the floor and at the latest-tested Marten.
/// </summary>
/// <param name="postgres">The assembly's container.</param>
public class DaemonTopologyLiveTests(PostgresFixture postgres)
{
    private const string Shard = "DailySales:All";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two HotCold nodes over one database: the leader holds every shard's lock and says so, the follower holds none and
    /// says that - and with Marten Studio's pause in effect on the follower, a Start there is refused as an agent beside the
    /// leader's, a Stop as one that stops nothing, and a rewind as one that would restart the agents beside the leader's.
    /// </summary>
    [PostgresFact]
    public async Task Under_HotCold_the_leader_leads_and_the_follower_refuses_to_run_an_agent_beside_it()
    {
        const string schema = "m7_topology_hotcold";

        // A lock id of this test's own, so the locks waited on are this leader's and nobody else's 4444.
        const int lockId = 7_740_470;

        await using DaemonHealthHost leader = await DaemonHealthHost.StartAsync(
            postgres, schema, DaemonMode.HotCold, store: static options => options.Projections.DaemonLockId = lockId);
        await leader.WaitForDaemonAsync();

        ProjectionsView? led = null;
        await ProjectionsFixture.WaitForAsync(
            async () => (led = await leader.ProjectionsAsync(x => x.GetProjectionsAsync(leader.Scope, Token))).Daemon.Leadership is { Count: 2 } locks
                && locks.Values.All(static x => x),
            "the leader to hold both shards' locks",
            TimeSpan.FromSeconds(60),
            Token);

        led!.Daemon.Coordination.Should().Be(DaemonCoordination.LeadershipLoop);
        led.Daemon.Distribution.Should().Be(DaemonDistribution.PerShard, "HotCold over one database takes a lock per shard");
        led.Daemon.LeadsHere(Shard).Should().BeTrue();
        DaemonTopologyWording.Describe(led.Daemon).Should().Contain("This node holds the lock for 2 of this database's 2 shards.");

        // Not waited on for IsRunning: a node that wins no lock starts no agent, and its high-water agent with them.
        await using DaemonHealthHost follower = await DaemonHealthHost.StartAsync(
            postgres, schema, DaemonMode.HotCold, fresh: false, store: static options => options.Projections.DaemonLockId = lockId);

        ProjectionsView cold = await follower.ProjectionsAsync(x => x.GetProjectionsAsync(follower.Scope, Token));

        cold.Daemon.Leadership.Values.Should().HaveCount(2).And.OnlyContain(static x => !x, "the leader holds every lock");
        cold.Daemon.LeadsHere(Shard).Should().BeFalse();
        DaemonTopologyWording.Describe(cold.Daemon).Should().Contain("This node holds the lock for none of this database's 2 shards");

        long before = await leader.ShardPositionAsync(Shard, postgres);

        // The per-agent controls are offered, under a loop, only while Marten Studio's pause holds - so the follower is paused.
        await follower.ProjectionsAsync(async x =>
        {
            await x.PauseDaemonAsync(follower.Scope, Token);
            return true;
        });

        DaemonControlResult start = await follower.ProjectionsAsync(x => x.StartAgentAsync(follower.Scope, Shard, Token));
        start.Applied.Should().BeFalse();
        start.Reason.Should().Be(DaemonTopologyWording.LedElsewhere(Shard), "the leader runs it, and a pause keeps the follower's locks as they were");

        DaemonControlResult stop = await follower.ProjectionsAsync(x => x.StopAgentAsync(follower.Scope, Shard, Token));
        stop.Applied.Should().BeFalse();
        stop.Reason.Should().Be(DaemonTopologyWording.NothingToStop(Shard, hotCold: true));

        await using (AsyncServiceScope circuit = follower.Services.CreateAsyncScope())
        {
            IEventDataService events = circuit.ServiceProvider.GetRequiredService<IEventDataService>();

            Func<Task> rewinding = () => events.RewindSubscriptionAsync(follower.Scope, "DailySales", 1, Token);
            (await rewinding.Should().ThrowAsync<InvalidOperationException>()).Which.Message
                .Should().Be(DaemonTopologyWording.RewindLedElsewhere("DailySales"));
        }

        (await leader.ShardPositionAsync(Shard, postgres)).Should().BeGreaterThanOrEqualTo(before, "the follower rewound nothing");
        follower.Services.GetRequiredService<StudioActionLogService>().GetLatest()
            .Where(static x => x.Action is "StartAgent" or "StopAgent" or EventDataService.RewindSubscriptionAction)
            .Should().HaveCount(3).And.OnlyContain(static x => !x.Succeeded);
    }

    /// <summary>An externally managed store's card says which system decides, and that the studio does not ask it.</summary>
    [PostgresFact]
    public async Task An_externally_managed_store_says_another_system_decides_which_node_runs_each_agent()
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(
            postgres, "m7_topology_external", mode: null,
            store: static options => options.Projections.AsyncMode = DaemonMode.ExternallyManaged);

        ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(host.Scope, Token));

        view.Daemon.IsExternallyManaged.Should().BeTrue();
        view.Daemon.Coordination.Should().Be(DaemonCoordination.None, "the studio asks an external system's coordinator nothing");
        DaemonTopologyWording.Describe(view.Daemon).Should().StartWith("An external system - Wolverine's managed distribution, typically");

        DaemonControlResult start = await host.ProjectionsAsync(x => x.StartAgentAsync(host.Scope, Shard, Token));
        start.Applied.Should().BeFalse();
        start.Reason.Should().Be(DaemonAccessor.ExternallyManagedExplanation);
    }

    /// <summary>A process that hosts no daemon says so in the card's own words, and adds no topology of its own.</summary>
    [PostgresFact]
    public async Task A_process_with_no_daemon_adds_no_topology_to_the_cards_own_explanation()
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "m7_topology_none", mode: null);

        ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(host.Scope, Token));

        view.Daemon.Hosting.Should().Be(DaemonHostingState.NotHostedInThisProcess);
        view.Daemon.Coordination.Should().Be(DaemonCoordination.None);
        DaemonTopologyWording.Describe(view.Daemon).Should().BeNull();
    }
}
