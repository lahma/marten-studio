using Marten.Exceptions;

using MartenStudio.Integration.Tests.Health;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// H2 criterion 4: a start the daemon refuses during a rebuild is audited and shown as refused - where JasperFx says so
/// (2.86, Marten 9.50) and, for a rebuild Marten Studio started, at every version. At the floor a rebuild somebody else
/// started is invisible to the studio and to JasperFx 2.74, and the start goes through: recorded here, not hidden.
/// </summary>
/// <remarks>
/// The replay is held inside <see cref="GatedProjection" /> on a closed gate, and each test waits until a page is really
/// waiting at it, so "during a rebuild" is a fact the test established rather than a race it hopes to win. The studio's
/// own coordinator pause comes first: per-agent controls are offered only while it is in effect.
/// </remarks>
public class StartDuringRebuildLiveTests(PostgresFixture postgres)
{
    private const string Shard = GatedProjection.ProjectionName + ":All";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool DaemonTracksRebuilds =>
        RuntimeFeature.DaemonIsRebuilding.IsAvailable(MartenRuntime.Current);

    [PostgresFact]
    public async Task A_start_during_a_rebuild_somebody_else_started_is_refused_where_the_daemon_can_say_and_recorded_where_it_cannot()
    {
        const string gate = "h2-start-external";

        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "h2_start_external", gated: true);
        await host.WaitForDaemonAsync();
        await host.ProjectionsAsync(async x => { await x.PauseDaemonAsync(host.Scope, Token); return true; });

        GatedProjection.Close(gate);
        Task rebuild = Task.CompletedTask;

        try
        {
            await host.AppendGatedAsync(1, gate);

            // Not through the studio: the operation tracker knows nothing of it.
            rebuild = host.Daemon.RebuildProjectionAsync(GatedProjection.ProjectionName, TimeSpan.FromMinutes(5), CancellationToken.None);

            await ProjectionsFixture.WaitForAsync(
                () => Task.FromResult(GatedProjection.WaitingAt(gate) > 0),
                "the replay to reach the gate",
                TimeSpan.FromSeconds(60),
                Token);

            DaemonControlResult result = await host.ProjectionsAsync(x => x.StartAgentAsync(host.Scope, Shard, Token));
            StudioActionLogEntry audited = host.Services.GetRequiredService<StudioActionLogService>().GetLatest()
                .First(x => x.Action == "StartAgent" && x.Target == Shard);

            if (DaemonTracksRebuilds)
            {
                result.Applied.Should().BeFalse("JasperFx 2.86 refuses a continuous start while the shard is rebuilt");
                result.Reason.Should().Contain("being rebuilt on this daemon");
                audited.Succeeded.Should().BeFalse("a start that did not happen is not audited as one");
                audited.Message.Should().Be(result.Reason);

                host.Services.GetRequiredService<DaemonControlState>().StartRefusals(host.Scope.StoreKey, host.Scope.DatabaseId)
                    .Should().ContainKey(Shard).WhoseValue.Reason.Should().Be("Rebuilding");
            }
            else
            {
                // The floor's JasperFx 2.74 has no rebuild registry: the start goes through and a continuous agent runs
                // beside the replay. Recorded as the old behaviour - the studio cannot see a rebuild it did not start.
                result.Applied.Should().BeTrue("JasperFx 2.74 has no guard, and nothing tells the studio a rebuild is running");
                audited.Succeeded.Should().BeTrue();
            }
        }
        finally
        {
            GatedProjection.Open(gate);

            try
            {
                await rebuild.WaitAsync(TimeSpan.FromMinutes(2), Token);
            }
            catch (MartenCommandException e) when (!DaemonTracksRebuilds && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The rest of the floor's old behaviour, measured: the continuous agent nothing refused writes the
                // shard's progression row beside the replay, and whichever inserts second loses on
                // pk_mt_event_progression - so the rebuild somebody else started can fail because of the start.
                // Which writer wins is a race; that the two collide is not.
            }
        }
    }

    /// <summary>A rebuild Marten Studio is running is known at every version, from its own operation tracker.</summary>
    [PostgresFact]
    public async Task A_start_during_a_rebuild_the_studio_started_is_refused_at_every_version()
    {
        const string gate = "h2-start-studio";

        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "h2_start_studio", gated: true);
        await host.WaitForDaemonAsync();
        await host.ProjectionsAsync(async x => { await x.PauseDaemonAsync(host.Scope, Token); return true; });

        GatedProjection.Close(gate);

        try
        {
            await host.AppendGatedAsync(1, gate);

            OperationStart start = await host.ProjectionsAsync(x => x.RebuildAsync(host.Scope, GatedProjection.ProjectionName, Token));
            start.Started.Should().BeTrue();

            await ProjectionsFixture.WaitForAsync(
                () => Task.FromResult(GatedProjection.WaitingAt(gate) > 0),
                "the replay to reach the gate",
                TimeSpan.FromSeconds(60),
                Token);

            DaemonControlResult result = await host.ProjectionsAsync(x => x.StartAgentAsync(host.Scope, Shard, Token));

            result.Applied.Should().BeFalse();
            result.Reason.Should().Contain("Marten Studio is rebuilding " + GatedProjection.ProjectionName).And.Contain(start.Id);

            // The screen draws it as a state: the projection is rebuilding, by the studio.
            ProjectionsView view = await host.ProjectionsAsync(x => x.GetProjectionsAsync(host.Scope, Token));
            IReadOnlyList<StudioOperation> running = await host.ProjectionsAsync(x => x.RunningOperationsAsync(host.Scope, Token));
            running.Should().Contain(x => x.Kind == "Rebuild" && x.Target == GatedProjection.ProjectionName);

            if (DaemonTracksRebuilds)
            {
                view.Daemon.IsRebuildingHere(GatedProjection.ProjectionName).Should().BeTrue("the daemon says so from JasperFx 2.86");
            }

            // And a second rebuild is answered with the running one, never started twice.
            OperationStart again = await host.ProjectionsAsync(x => x.RebuildAsync(host.Scope, GatedProjection.ProjectionName, Token));
            again.Started.Should().BeFalse();
            again.Refusal.Should().BeNull("the tracker answers its own rebuild with its handle, not with a refusal");
            again.Id.Should().Be(start.Id);
        }
        finally
        {
            GatedProjection.Open(gate);
        }
    }
}
