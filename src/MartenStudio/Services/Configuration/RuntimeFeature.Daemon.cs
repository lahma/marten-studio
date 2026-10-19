using System.Reflection;

using Marten;

namespace MartenStudio.Services.Configuration;

/// <summary>The daemon and high-water checks' light-up features (H2).</summary>
internal sealed partial class RuntimeFeature
{
    /// <summary>
    /// The high-water agent restamps the <c>HighWaterMark</c> progression row's <c>last_updated</c> on every poll
    /// that leaves the mark where it was - so the column is the agent's liveness, not the mark's last move -
    /// Marten 9.43.0 (#5541, jasperfx#924): <c>HighWaterDetector.refreshHighWaterFreshnessAsync</c> in
    /// <c>src/Marten/Events/Daemon/HighWater/HighWaterDetector.cs</c> (absent at V9.42.0, present at V9.43.0 and
    /// V9.50.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The probe names a private method, on purpose.</b> This is a behaviour rather than an API, and the method is
    /// the behaviour: it is called from <c>persistDetectedMarkAsync</c> whenever the statistics have not changed, and
    /// it is the only writer of that column on that path. A probe of a private member is fragile - a rename reads as
    /// the behaviour going away - which is exactly what <c>MartenRuntimeProbeTests</c> catches at the latest Marten:
    /// "present iff Marten &gt;= 9.43.0". Going quiet in the conservative direction (treating the column as progress)
    /// is the failure mode, never an alarm.
    /// </para>
    /// <para>
    /// What D03 does with it: from 9.43 a <c>HighWaterMark</c> row written within the window is evidence that the
    /// daemon is alive, and one that is not is evidence that it is not; below 9.43 only a recent write counts, and an
    /// old one says nothing, because the column freezes on a store that is caught up and idle.
    /// </para>
    /// </remarks>
    public static readonly RuntimeFeature HighWaterMarkLivenessStamp = new(
        nameof(HighWaterMarkLivenessStamp),
        RuntimePackage.Marten,
        "9.43.0",
        "9.43.0",
        "The HighWaterMark progression row's last_updated is restamped on every high-water poll, so it says whether the high-water agent is alive rather than when the mark last moved.",
        static () => typeof(DocumentStore).Assembly
            .GetType("Marten.Events.Daemon.HighWater.HighWaterDetector", throwOnError: false)?
            .GetMethod("refreshHighWaterFreshnessAsync", BindingFlags.NonPublic | BindingFlags.Instance) is not null);
}
