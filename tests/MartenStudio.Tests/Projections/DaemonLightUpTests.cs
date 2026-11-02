using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using MartenStudio.Services.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The daemon helpers: each says what JasperFx says, and the start refusals the studio remembers for D05.
/// </summary>
public class DaemonLightUpTests
{
    /// <summary>
    /// <c>IsRebuilding</c> is a default interface method answering false for a daemon that does not track rebuilds - the
    /// fake is one. The answer is "not known to be", never a yes.
    /// </summary>
    [Fact]
    public void A_daemon_that_does_not_track_rebuilds_is_never_said_to_be_rebuilding()
    {
        DaemonLightUp.IsRebuilding(new FakeProjectionDaemon(), new ShardName("DailySales")).Should().BeFalse();
    }

    /// <summary>
    /// A refusal's classification: Faulted and not transient for an outright fault. The exception is made through its
    /// (exception) constructor.
    /// </summary>
    [Fact]
    public void A_start_refusal_carries_its_reason()
    {
        var exception = new ShardStartException("DailySales:All", new InvalidOperationException("boom"));

        ShardStartRefusal refusal = DaemonLightUp.Describe(exception);

        refusal.Message.Should().Contain("DailySales:All");
        refusal.Reason.Should().Be("Faulted");
        refusal.IsTransient.Should().BeFalse();
        DaemonLightUp.Explain(refusal).Should().Contain("(Faulted, which a retry will not resolve on its own)");
        DaemonLightUp.Explain(new ShardStartRefusal(null, null, "refused")).Should().StartWith("The daemon did not start it: ");
    }

    /// <summary>The 2.89 classifier decides every exception, and the studio asks it.</summary>
    [Fact]
    public void An_exception_is_called_transient_or_permanent_by_JasperFx()
    {
        DaemonLightUp.IsTransient(new InvalidOperationException()).Should().BeFalse();
        DaemonLightUp.IsTransient(new TimeoutException()).Should().BeTrue();
    }

    [Fact]
    public void A_start_refusal_is_remembered_per_store_database_and_shard_and_forgotten_once_it_starts()
    {
        DaemonControlState state = new();
        DateTimeOffset at = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

        state.RecordStartRefusal(new StudioStartRefusal("default", "db1", "DailySales:All", "Rebuilding", true, "refused", at));
        state.RecordStartRefusal(new StudioStartRefusal("default", "db2", "DailySales:All", "AgentPaused", false, "refused", at));

        state.StartRefusals("default", "db1").Should().ContainKey("DailySales:All").WhoseValue.Reason.Should().Be("Rebuilding");
        state.StartRefusals("default", "db2").Should().ContainSingle();
        state.StartRefusals("other", "db1").Should().BeEmpty();

        state.ClearStartRefusal("default", "db1", "DailySales:All");
        state.StartRefusals("default", "db1").Should().BeEmpty();
    }

    [Fact]
    public void The_remembered_refusals_are_bounded_newest_kept()
    {
        DaemonControlState state = new();
        DateTimeOffset at = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < DaemonControlState.MaxStartRefusals + 10; i++)
        {
            state.RecordStartRefusal(new StudioStartRefusal("default", "db", "P" + i + ":All", null, null, "refused", at.AddSeconds(i)));
        }

        IReadOnlyDictionary<string, StudioStartRefusal> kept = state.StartRefusals("default", "db");
        kept.Should().HaveCount(DaemonControlState.MaxStartRefusals);
        kept.Should().NotContainKey("P0:All").And.ContainKey("P" + (DaemonControlState.MaxStartRefusals + 9) + ":All");
    }
}
