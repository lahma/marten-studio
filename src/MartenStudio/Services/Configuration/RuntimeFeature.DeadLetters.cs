using System.Reflection;

using Marten;
using Marten.Events.Daemon;
using Marten.Storage;

namespace MartenStudio.Services.Configuration;

/// <summary>
/// The dead-letter page's two light-ups: Skip event and Rewind subscription for one tenant, on a store that numbers
/// its events per tenant (<c>UseTenantPartitionedEvents</c>), where the studio refuses both below Marten 9.51.
/// </summary>
internal sealed partial class RuntimeFeature
{
    /// <summary>
    /// <c>IMartenDatabase.MarkEventsAsSkipped(string tenantId, long[] sequences, CancellationToken)</c>, implemented by
    /// Marten - Marten 9.51.0 (#5698): <c>src/Marten/Storage/IMartenDatabase.cs</c> declares it as a default interface
    /// method that throws <c>NotSupportedException</c>, and <c>MartenDatabase.EventStorage.cs</c> implements it as
    /// <c>update mt_events set is_skipped = TRUE where seq_id = ANY(:sequences) and tenant_id = :tenant_id</c> - the
    /// tenant predicate added whenever the event store is conjoined, which <c>UseTenantPartitionedEvents</c> requires.
    /// Absent at V9.50.2 and V9.50.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The probe asks for Marten's implementation, not the declaration</b> - the
    /// <see cref="TenantStreamCompaction" /> shape - because the declaration alone is a method that throws.
    /// </para>
    /// <para>
    /// At the same release the tenant-less overload starts refusing under <c>UseTenantPartitionedEvents</c> with an
    /// <c>InvalidOperationException</c> (below it, it skipped every tenant's event of the number). The studio refuses
    /// before it reaches either, so nothing it does changes with that.
    /// </para>
    /// </remarks>
    public static readonly RuntimeFeature TenantEventSkipping = new(
        nameof(TenantEventSkipping),
        RuntimePackage.Marten,
        "9.51.0",
        "9.51.0",
        "Skip event marks one tenant's event of a number skipped, where each tenant numbers its events (UseTenantPartitionedEvents).",
        static () => ImplementedBy(typeof(MartenDatabase), typeof(IMartenDatabase), TenantSkipMethod()));

    /// <summary>
    /// The per-tenant rewind, whole: JasperFx.Events 2.91.0's <c>JasperFxAsyncDaemon.RewindSubscriptionAsync(string,
    /// string? tenantId, CancellationToken, long?, DateTimeOffset?)</c> (jasperfx#1025; until then only
    /// <c>IProjectionDaemon</c>'s default, which throws for a tenant, declared since at least V2.74.0), <em>and</em>
    /// Marten 9.51.0's <c>DocumentStore.findTenantShard</c> (#5700), which is what lets
    /// <c>RewindSubscriptionProgressAsync</c> take the tenant agent's identity the daemon hands it - below it Marten
    /// throws "Unknown subscription name" for <c>{Name}:{ShardKey}:{tenant}</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves, because each ships in its own package.</b> A host on Marten 9.50 that pinned JasperFx 2.91
    /// would have the daemon's half and not Marten's, and its rewind would fail half-way. The second half is a private
    /// member - the <see cref="HighWaterMarkLivenessStamp" /> pattern: a rename reads as the feature going away, which
    /// <c>MartenRuntimeProbeTests</c> catches at the latest Marten, and absence is the safe direction (the studio then
    /// refuses as it does at the floor).
    /// </para>
    /// <para>
    /// The first half is looked for on Marten's own <see cref="ProjectionDaemon" />, a class: at JasperFx 2.87.1 the
    /// overload is only the interface's default, which a class's <c>GetMethod</c> does not return. The studio checks
    /// the daemon it is actually handed as well (<c>EventDataService.ImplementsTenantRewind</c>), since a coordinator
    /// may hand it some other <c>IProjectionDaemon</c>.
    /// </para>
    /// </remarks>
    public static readonly RuntimeFeature TenantSubscriptionRewind = new(
        nameof(TenantSubscriptionRewind),
        RuntimePackage.Marten,
        "9.51.0",
        "9.51.0",
        "Rewind subscription rewinds one tenant's agents of a projection, where each tenant numbers its events (UseTenantPartitionedEvents).",
        static () => typeof(ProjectionDaemon).GetMethod(
                "RewindSubscriptionAsync",
                BindingFlags.Public | BindingFlags.Instance,
                [typeof(string), typeof(string), typeof(CancellationToken), typeof(long?), typeof(DateTimeOffset?)]) is not null
            && typeof(DocumentStore).GetMethod("findTenantShard", BindingFlags.NonPublic | BindingFlags.Instance) is not null);

    /// <summary>The declaration of the tenant overload of <c>MarkEventsAsSkipped</c>, or <see langword="null" />.</summary>
    internal static MethodInfo? TenantSkipMethod() =>
        typeof(IMartenDatabase).GetMethod(
            "MarkEventsAsSkipped",
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(string), typeof(long[]), typeof(CancellationToken)]);
}
