using System.Reflection;

using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The daemon light-up helpers, right at the floor and at the latest Marten: each says what the resolved JasperFx can
/// say and nothing it cannot - and the start refusals the studio remembers for D05.
/// </summary>
public class DaemonLightUpTests
{
    /// <summary>
    /// <c>IsRebuilding</c> is a default interface method answering false for a daemon that does not track rebuilds - the
    /// fake is one - and below JasperFx 2.86 it does not exist. Either way the answer is "not known to be", never a yes.
    /// </summary>
    [Fact]
    public void A_daemon_that_does_not_track_rebuilds_is_never_said_to_be_rebuilding()
    {
        DaemonLightUp.IsRebuilding(new FakeProjectionDaemon(), new ShardName("DailySales")).Should().BeFalse();
        DaemonLightUp.CanTellRebuilding.Should().Be(RuntimeFeature.DaemonIsRebuilding.Probe() == true);
    }

    /// <summary>
    /// CL1 item 5: <c>IsRebuilding</c> is named by its string in two files - the probe that says whether it exists
    /// (<c>RuntimeFeature</c>) and the one helper that calls it (<see cref="DaemonLightUp" />). D08 kept a private
    /// <c>GetMethod("IsRebuilding")</c> of its own, which a JasperFx that moved the member would have left answering
    /// "absent" while the probe and the helper failed loudly.
    /// </summary>
    [Fact]
    public void IsRebuilding_is_reflected_in_the_probe_and_the_helper_and_nowhere_else()
    {
        string[] files = Conventions.SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        List<string> naming =
        [
            .. files
                .Where(static file => File.ReadAllText(file).Contains("\"IsRebuilding\"", StringComparison.Ordinal))
                .Select(Conventions.SourceScanner.Relative)
                .Order(StringComparer.Ordinal),
        ];

        naming.Should().Equal(
            "src/MartenStudio/Services/Configuration/RuntimeFeature.cs",
            "src/MartenStudio/Services/Projections/DaemonLightUp.cs");
    }

    /// <summary>
    /// A refusal's classification where JasperFx classifies (2.76+): Faulted and not transient for an outright fault;
    /// at the floor, the message alone. The exception is made through its (exception) constructor, which exists at both
    /// ends - internal at 2.74, public from 2.76.
    /// </summary>
    [Fact]
    public void A_start_refusal_carries_its_reason_exactly_where_jasperfx_classifies()
    {
        ShardStartException exception = (ShardStartException) Activator.CreateInstance(
            typeof(ShardStartException),
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            args: ["DailySales:All", new InvalidOperationException("boom")],
            culture: null)!;

        ShardStartRefusal refusal = DaemonLightUp.Describe(exception);

        refusal.Message.Should().Contain("DailySales:All");

        if (RuntimeFeature.ShardStartReason.Probe() == true)
        {
            refusal.Reason.Should().Be("Faulted");
            refusal.IsTransient.Should().BeFalse();
            DaemonLightUp.Explain(refusal).Should().Contain("(Faulted, which a retry will not resolve on its own)");
        }
        else
        {
            refusal.Reason.Should().BeNull("JasperFx below 2.76 does not classify a refusal");
            refusal.IsTransient.Should().BeNull();
            DaemonLightUp.Explain(refusal).Should().StartWith("The daemon did not start it: ");
        }
    }

    /// <summary>
    /// The 2.89 classifier is first in Marten 9.50.3 (JasperFx 2.90.1): absent at the floor, where the studio says nothing,
    /// and present at the latest tested, 9.51.0, where it classifies.
    /// </summary>
    [Fact]
    public void Without_the_transient_classifier_nothing_is_called_transient_or_permanent()
    {
        bool? answer = DaemonLightUp.IsTransient(new InvalidOperationException());

        if (RuntimeFeature.TransientFailureClassifier.Probe() == true)
        {
            answer.Should().NotBeNull("JasperFx 2.89 classifies every exception");
        }
        else
        {
            answer.Should().BeNull("JasperFx below 2.89 has no classifier, and the studio does not guess");
        }
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
