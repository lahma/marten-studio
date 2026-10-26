using System.Reflection;

using Marten;
using Marten.Events;

using Weasel.Core.Migrations;
using Weasel.Postgresql.Tables;

namespace MartenStudio.Services.Configuration;

/// <summary>The event-store features the event health checks light up on.</summary>
internal sealed partial class RuntimeFeature
{
    /// <summary>The table suffix the <see cref="TaggedStreamCompaction" /> probe registers its tag type under.</summary>
    internal const string TagProbeSuffix = "studio_probe";

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
    /// <c>23503</c> (V9.39.0, V9.50.2 and V9.50.3). The compaction is the same code at <c>v9.51.0</c> and <c>V9.54.1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The probe reads what the same pull request declares</b>, because the behaviour has no member of its own: the
    /// delete is built inside an internal operation. #5694 also declares <c>idx_mt_event_tag_&lt;suffix&gt;_seq_id</c> on
    /// every tag table (<c>EventTagTable</c>; absent at V9.50.3, present at v9.51.0 and V9.54.1), the index that makes
    /// those CTEs an index scan. So the probe builds a throwaway <see cref="StoreOptions" /> - no connection, nothing
    /// applied - registers one tag type on it, and asks the event store's feature schema whether that tag table declares
    /// the index: the same <c>IFeatureSchema.Objects</c> walk <c>SchemaDeclarationReader</c> makes.
    /// </para>
    /// <para>
    /// <b>It used to name a private method</b>, <c>TenantDataCleaner.deleteTagRowsAsync</c>, which shipped in the same
    /// release; marten#5720 (9.52.0) moved that work to <c>PerTenantPartitionedCleanup.DeleteSideTableRowsAsync</c>, and
    /// the probe answered "absent" at every Marten from 9.52 while compaction went on deleting the rows. A declared
    /// index is sturdier than a private name: renaming it is a schema change Marten would have to migrate on every
    /// host's tag tables. A change that does remove it reads as the behaviour going away, which
    /// <c>MartenRuntimeProbeTests</c> catches at the latest Marten; and absence is the conservative direction - the
    /// compaction dialog keeps saying a tagged stream may be refused, and the service still turns the refusal into the
    /// studio's words.
    /// </para>
    /// <para>
    /// The index is also drift on a store whose tag tables were built below 9.51: one plain <c>CREATE INDEX</c> per tag
    /// table - which <c>MigrationRisk.Notes</c> names, because that build holds a SHARE lock on the tag table and every
    /// tagged append waits for it.
    /// </para>
    /// </remarks>
    public static readonly RuntimeFeature TaggedStreamCompaction = new(
        nameof(TaggedStreamCompaction),
        RuntimePackage.Marten,
        "9.51.0",
        "9.51.0",
        "Compacting a stream deletes the folded events' DCB tag rows with them, so a tagged stream compacts instead of being refused by its tag table's foreign key.",
        static () => DeclaresTagSequenceIndex());

    /// <summary>
    /// Whether this Marten declares <c>idx_mt_event_tag_&lt;suffix&gt;_seq_id</c> on a tag table: a throwaway store's
    /// options with one tag type registered, read through the event store's feature schema without a connection.
    /// </summary>
    internal static bool DeclaresTagSequenceIndex()
    {
        var options = new StoreOptions();
        options.Events.RegisterTagType<TagProbe>(TagProbeSuffix);

        if (options.Events is not IFeatureSchema feature)
        {
            return false;
        }

        string table = "mt_event_tag_" + TagProbeSuffix;
        string index = "idx_mt_event_tag_" + TagProbeSuffix + "_seq_id";

        return feature.Objects.OfType<Table>().Any(x =>
            string.Equals(x.Identifier.Name, table, StringComparison.Ordinal)
            && x.Indexes.Any(i => string.Equals(i.Name, index, StringComparison.Ordinal)));
    }

    /// <summary>The strong-typed id the probe registers: one wrapped string, the shape a DCB tag type has.</summary>
    /// <param name="Value">Never read.</param>
    internal sealed record TagProbe(string Value);
}
