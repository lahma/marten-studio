using System.Reflection;

using Marten.Events;

namespace MartenStudio.Services.Configuration;

/// <summary>The event-store features the event health checks light up on.</summary>
internal sealed partial class RuntimeFeature
{
    /// <summary>
    /// <c>opts.Events.DcbTagVersionFillFactor</c>, the heap fillfactor of <c>mt_dcb_tag_version</c> - Marten 9.47.0,
    /// <c>src/Marten/Events/EventGraph.cs</c> and <c>IEventStoreOptions.cs</c> (#5622; absent at V9.46.0). Weasel
    /// 9.42's <c>WithFillFactor</c> is what lets the schema delta manage it.
    /// </summary>
    public static readonly RuntimeFeature DcbTagVersionFillFactor = new(
        nameof(DcbTagVersionFillFactor),
        RuntimePackage.Marten,
        "9.47.0",
        "9.47.0",
        "The heap fillfactor of the DCB tag-version table, declared with Events.DcbTagVersionFillFactor and managed by the schema delta.",
        static () => typeof(EventGraph).GetProperty(
            "DcbTagVersionFillFactor", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var type } && type == typeof(int?));

    /// <summary>
    /// Compaction deletes the folded events' DCB tag rows with them - Marten 9.51.0 (#5694):
    /// <c>DeleteEventsOperation.ConfigureCommand</c> in <c>src/Marten/Events/EventStore.StreamCompacting.cs</c> puts one
    /// <c>delete from mt_event_tag_&lt;suffix&gt;</c> CTE per tag table, each carrying the conjoined tenant filter, ahead
    /// of the event delete. Below it the event delete alone ran, and a tagged event's tag row refused it with
    /// <c>23503</c> (V9.39.0, V9.50.2 and V9.50.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The probe names a private method of the same pull request, on purpose</b> - the
    /// <see cref="HighWaterMarkLivenessStamp" /> pattern. The behaviour has no public member: the delete statement is
    /// built inside an internal operation, and the only named member #5694 adds is
    /// <c>TenantDataCleaner.deleteTagRowsAsync</c> (<c>src/Marten/Internal/TenantDataCleaner.cs</c>), which ships with
    /// it. A rename reads as the behaviour going away, which <c>MartenRuntimeProbeTests</c> catches at the latest
    /// Marten; and absence is the conservative direction - the compaction dialog keeps saying a tagged stream may be
    /// refused, and the service still turns the refusal into the studio's words.
    /// </para>
    /// <para>
    /// The same release declares <c>idx_mt_event_tag_&lt;suffix&gt;_seq_id</c> on every tag table
    /// (<c>EventTagTable</c>), so a store whose tag tables were built below 9.51 shows one plain <c>CREATE INDEX</c>
    /// per tag table as drift - which <c>MigrationRisk.Notes</c> names, because that build holds a SHARE lock on the
    /// tag table and every tagged append waits for it.
    /// </para>
    /// </remarks>
    public static readonly RuntimeFeature TaggedStreamCompaction = new(
        nameof(TaggedStreamCompaction),
        RuntimePackage.Marten,
        "9.51.0",
        "9.51.0",
        "Compacting a stream deletes the folded events' DCB tag rows with them, so a tagged stream compacts instead of being refused by its tag table's foreign key.",
        static () => typeof(Marten.DocumentStore).Assembly
            .GetType("Marten.Internal.TenantDataCleaner", throwOnError: false)?
            .GetMethod("deleteTagRowsAsync", BindingFlags.NonPublic | BindingFlags.Instance) is not null);
}
