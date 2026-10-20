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

    /// <summary>
    /// <c>IReadOnlyDaemonSettings.HighWaterProgressStallThreshold</c> - JasperFx.Events 2.91.0 (jasperfx#1027/#1028),
    /// <c>src/JasperFx.Events/Daemon/DaemonSettings.cs</c>: a default interface property answering
    /// <see langword="null" />, which <c>DaemonSettings</c> overrides with a settable one defaulting to two minutes.
    /// Absent at V2.90.1; first in Marten 9.51.0, which brings 2.91.0 (9.50.3 brings 2.90.1).
    /// </summary>
    /// <remarks>
    /// How long the high-water mark may stay below <c>max(seq_id)</c> before the health-check watchdog restarts the
    /// high-water agent, with a warning each time; a restart re-reads the mark and never advances it, so a gap held
    /// on purpose stays held. The Configuration page shows it beside "High water staleness"; nothing else reads it.
    /// </remarks>
    public static readonly RuntimeFeature HighWaterProgressStall = new(
        nameof(HighWaterProgressStall),
        RuntimePackage.JasperFxEvents,
        "2.91.0",
        "9.51.0",
        "How long the high-water mark may stay below the events committed above it before the daemon restarts the high-water agent (DaemonSettings.HighWaterProgressStallThreshold).",
        static () => HighWaterProgressStallProperty() is not null);

    /// <summary>
    /// <c>HighWaterProgressStallThreshold</c> as <c>IReadOnlyDaemonSettings</c> declares it, so reading it through the
    /// interface reaches whichever implementation the store holds; <see langword="null" /> below JasperFx 2.91.
    /// </summary>
    internal static PropertyInfo? HighWaterProgressStallProperty() =>
        typeof(JasperFx.Events.Daemon.IReadOnlyDaemonSettings).GetProperty(
            "HighWaterProgressStallThreshold", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var type } property
            && type == typeof(TimeSpan?)
                ? property
                : null;
}
