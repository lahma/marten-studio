using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Services;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;
using MartensCoordinator = Marten.Events.Daemon.Coordination.ProjectionCoordinator;
using MartensExplicitCoordinator = Marten.Events.Daemon.Coordination.ExplicitProjectionCoordinator;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// M7: which coordinator runs the daemon here, which shards this node leads, and what a Start, a Stop or a Rewind does
/// under each - over Marten's real coordinators, constructed over stores that are never connected to and never started.
/// </summary>
/// <remarks>
/// The coordinators, the three distributors and <c>IProjectionSet</c> are byte-identical at JasperFx <c>V2.74.0</c> and
/// <c>V2.95.1</c> (Marten <c>V9.39.0</c> and <c>v9.54.2</c>), and these tests run at both ends: what they pin is the shape
/// the leadership probe relies on.
/// </remarks>
public class DaemonTopologyTests
{
    private const string Unreachable = "Host=marten-studio-topology.invalid;Database=topology;Username=none;Password=none;Timeout=2";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DocumentStore Store(DaemonMode mode, bool twoDatabases = false) => DocumentStore.For(options =>
    {
        if (twoDatabases)
        {
            options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(Unreachable, "first").ForTenants("first");
                tenancy.AddMultipleTenantDatabase(Unreachable.Replace("Database=topology", "Database=second", StringComparison.Ordinal), "second").ForTenants("second");
            });
        }
        else
        {
            options.Connection(Unreachable);
        }

        options.Projections.AsyncMode = mode;
        options.Projections.Add(new SoloProjection(), ProjectionLifecycle.Async);
    });

    [Fact]
    public async Task Each_kind_of_coordinator_is_told_apart_by_the_loop_it_runs()
    {
        await using DocumentStore store = Store(DaemonMode.Solo);

        await using var loop = new MartensCoordinator(store, NullLogger<MartensCoordinator>.Instance);
        await using var subclass = new HostSubclassedCoordinator(store);
        var explicitOne = new MartensExplicitCoordinator(store, NullLogger<MartensExplicitCoordinator>.Instance);

        DaemonAccessor.CoordinationOf(loop).Should().Be(DaemonCoordination.LeadershipLoop);
        DaemonAccessor.CoordinationOf(subclass).Should().Be(DaemonCoordination.LeadershipLoop, "a host's subclass runs Marten's loop");
        DaemonAccessor.CoordinationOf(explicitOne).Should().Be(DaemonCoordination.Explicit);
        DaemonAccessor.CoordinationOf(new WolverineShapedCoordinator()).Should().Be(DaemonCoordination.Other);
    }

    /// <summary>
    /// The distributor's answer, as the leadership loop asks it: Solo holds every shard with no lock; HotCold holds a shard
    /// only with its lock - one per shard over one database, one per database over several - and a coordinator that has
    /// never started holds none. A coordinator with no loop has no leadership to ask.
    /// </summary>
    [Fact]
    public async Task The_leadership_probe_asks_the_distributor_the_loop_asks()
    {
        ShardName shard = ShardName.Compose("Solo");
        ShardName tenantShard = ShardName.Compose("Solo", tenantId: "acme");

        await using DocumentStore solo = Store(DaemonMode.Solo);
        await using (var coordinator = new MartensCoordinator(solo, NullLogger<MartensCoordinator>.Instance))
        {
            DaemonLeadership leadership = DaemonLeadership.For(coordinator, solo, solo.Storage.Database)!;
            leadership.Distribution.Should().Be(DaemonDistribution.Solo);
            leadership.IsHotCold.Should().BeFalse();
            leadership.Leads(shard).Should().BeTrue();
        }

        await using DocumentStore hotCold = Store(DaemonMode.HotCold);
        await using (var coordinator = new MartensCoordinator(hotCold, NullLogger<MartensCoordinator>.Instance))
        {
            DaemonLeadership leadership = DaemonLeadership.For(coordinator, hotCold, hotCold.Storage.Database)!;
            leadership.Distribution.Should().Be(DaemonDistribution.PerShard);
            leadership.IsHotCold.Should().BeTrue();
            leadership.Leads(shard).Should().BeFalse("a coordinator that never started has taken no lock");
            leadership.Leads(tenantShard).Should().BeFalse("a tenant's shard runs under its store-global shard's lock");
        }

        await using DocumentStore several = Store(DaemonMode.HotCold, twoDatabases: true);
        IMartenDatabase first = (await several.Storage.AllDatabases())[0];
        await using (var coordinator = new MartensCoordinator(several, NullLogger<MartensCoordinator>.Instance))
        {
            DaemonLeadership leadership = DaemonLeadership.For(coordinator, several, first)!;
            leadership.Distribution.Should().Be(DaemonDistribution.PerDatabase);
            leadership.Leads(shard).Should().BeFalse();
        }

        var explicitOne = new MartensExplicitCoordinator(solo, NullLogger<MartensExplicitCoordinator>.Instance);
        DaemonLeadership.For(explicitOne, solo, solo.Storage.Database).Should().BeNull("there is no leadership loop to lead");

        await using DocumentStore disabled = Store(DaemonMode.Disabled);
        await using (var coordinator = new MartensCoordinator(disabled, NullLogger<MartensCoordinator>.Instance))
        {
            DaemonLeadership.For(coordinator, disabled, disabled.Storage.Database).Should().BeNull("a disabled daemon has no distributor");
        }
    }

    [Fact]
    public void The_card_says_what_this_node_runs_under_each_coordination()
    {
        DaemonStatus hosted = new(DaemonHostingState.Hosted, true, "Solo", [new DaemonAgentInfo("Solo:All", "Running", 5, 5)], false, null, "hosted");

        DaemonTopologyWording.Describe(hosted with { Coordination = DaemonCoordination.LeadershipLoop, Distribution = DaemonDistribution.Solo })
            .Should().Be("Solo: this process runs every async shard of this database itself and takes no leadership lock, and while "
                + "its coordinator's loop runs it starts any agent that is not running within 5 s. It runs 1 agent now.");

        DaemonTopologyWording.Describe(hosted with
            {
                Mode = "HotCold",
                Coordination = DaemonCoordination.LeadershipLoop,
                Distribution = DaemonDistribution.PerShard,
                Leadership = new Dictionary<string, bool> { ["Solo:All"] = false, ["Other:All"] = false },
                Agents = [],
            })
            .Should().Be("HotCold: each shard runs on the one node that holds its leadership lock, and a coordinator starts agents only "
                + "for the locks it holds. This node holds the lock for none of this database's 2 shards: another node runs them, or "
                + "none has started yet. It runs 0 agents now.");

        DaemonTopologyWording.Describe(hosted with { Coordination = DaemonCoordination.Explicit })
            .Should().StartWith("MartenDaemonModeIsSolo(): a coordinator with no leadership loop.");

        DaemonTopologyWording.Describe(hosted with { Coordination = DaemonCoordination.Other })
            .Should().StartWith("A projection coordinator that is not Marten's own runs this daemon");

        DaemonTopologyWording.Describe(new DaemonStatus(DaemonHostingState.ExternallyManaged, false, "ExternallyManaged", [], false, null, "external"))
            .Should().StartWith("An external system - Wolverine's managed distribution, typically - decides which node runs each agent");

        DaemonTopologyWording.Describe(new DaemonStatus(DaemonHostingState.NotHostedInThisProcess, false, "Solo", [], false, null, "elsewhere"))
            .Should().BeNull("the card's own explanation already says no daemon is hosted here");
    }

    /// <summary>
    /// Start and Stop say when they would do nothing here: an agent already running, no agent to stop - and under HotCold a
    /// shard another node leads, a tenant's shard included through its store-global lock.
    /// </summary>
    [Fact]
    public void Start_and_Stop_say_when_they_would_do_nothing_here_or_are_refused()
    {
        DaemonStatus status = new(DaemonHostingState.Hosted, false, "HotCold", [new DaemonAgentInfo("Mine:All", "Running", 1, 1)], false, null, "hosted")
        {
            Coordination = DaemonCoordination.LeadershipLoop,
            Distribution = DaemonDistribution.PerShard,
            Leadership = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["Mine:All"] = true, ["Theirs:All"] = false },
        };

        DaemonTopologyWording.StartRefusal(status, "Mine:All").Should().Be(DaemonTopologyWording.AlreadyRunning("Mine:All"));
        DaemonTopologyWording.StartRefusal(status, "Theirs:All").Should().Be(DaemonTopologyWording.LedElsewhere("Theirs:All"));
        DaemonTopologyWording.StartRefusal(status, "Theirs:All:acme").Should().Be(DaemonTopologyWording.LedElsewhere("Theirs:All:acme"));
        DaemonTopologyWording.StopRefusal(status, "Mine:All").Should().BeNull();
        DaemonTopologyWording.StopRefusal(status, "Theirs:All")
            .Should().Be("No agent for Theirs:All runs on this node, so Stop would do nothing: nothing was stopped. Under HotCold it runs "
                + "on whichever node holds its leadership lock.");

        status.LeadsHere("Unknown:All").Should().BeNull("a shard the probe could not answer for is not called another node's");
        (status with { Coordination = DaemonCoordination.Explicit }).LeadsHere("Theirs:All").Should().BeNull();
        (status with { Distribution = DaemonDistribution.Solo }).LeadsHere("Theirs:All").Should().BeTrue();
    }

    /// <summary>With no leadership loop there is nothing to pause first: per-agent controls are offered and refuse nothing for it.</summary>
    [Fact]
    public void With_no_leadership_loop_per_agent_controls_need_no_pause()
    {
        DaemonStatus running = new(DaemonHostingState.Hosted, true, "Solo", [], false, null, "hosted");

        running.CanControlAgents.Should().BeFalse("the loop would undo them");
        running.AgentControlRefusal.Should().NotBeNull();

        DaemonStatus explicitOne = running with { Coordination = DaemonCoordination.Explicit };
        explicitOne.CanControlAgents.Should().BeTrue();
        explicitOne.AgentControlRefusal.Should().BeNull();
    }

    /// <summary>
    /// The service refuses the same, whatever was rendered (hard rule 5): under <c>MartenDaemonModeIsSolo()</c> a Stop needs
    /// no pause - the coordinator has no loop - and a Stop of a shard no agent runs here is refused as the no-op it is,
    /// audited as not done.
    /// </summary>
    [Fact]
    public async Task Under_the_explicit_coordinator_a_Stop_needs_no_pause_and_a_Stop_of_nothing_is_refused_as_a_value()
    {
        var users = new TestAuthenticationStateProvider();
        users.SignIn("tester");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMarten(static options =>
        {
            options.Connection(Unreachable);
            options.Projections.Add(new SoloProjection(), ProjectionLifecycle.Async);
        });
        services.MartenDaemonModeIsSolo();
        services.AddMartenStudio(static options => options.Capabilities = MartenStudioCapabilities.All());
        services.AddScoped<AuthenticationStateProvider>(_ => users);

        await using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<MartenCoordinator>().Should().BeOfType<MartensExplicitCoordinator>("the premise");

        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();
        StudioScope scope = new(MartenStoreRegistry.DefaultStoreKey, store.Storage.Database.Id.Identity, null);

        using IServiceScope circuit = provider.CreateScope();
        IProjectionDataService projections = circuit.ServiceProvider.GetRequiredService<IProjectionDataService>();

        DaemonControlResult result = await projections.StopAgentAsync(scope, "Solo:All", Token);

        result.Applied.Should().BeFalse();
        result.Reason.Should().Be(DaemonTopologyWording.NothingToStop("Solo:All", hotCold: false),
            "no pause was asked for - there is no loop to undo it - and no agent runs here to stop");

        provider.GetRequiredService<StudioActionLogService>().GetLatest()
            .Should().ContainSingle(static x => x.Action == "StopAgent" && !x.Succeeded);
    }

    /// <summary>A host's own subclass of Marten's coordinator.</summary>
    private sealed class HostSubclassedCoordinator(IDocumentStore store)
        : MartensCoordinator(store, NullLogger<MartensCoordinator>.Instance);
}
