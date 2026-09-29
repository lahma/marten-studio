using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

namespace MartenStudio.Services.Schema;

/// <summary>
/// Turns the Tables tab's catalog rows into what the tab shows, through the database browser's own
/// classification.
/// </summary>
/// <remarks>
/// <para>
/// Pure, so the classification can be tested row by row without a database. The Tables tab predates the
/// database browser and used to decide ownership on its own: a table was a document collection when a
/// visible document type named it, and "event store" when it lived in the event store's schema - so a
/// Quartz.NET table, <c>mt_hilo</c> or a partition in that schema was badged "event store", and a hidden
/// document type's table was listed like any other. Now there is one rule, the browser's
/// (<see cref="DatabaseObjectClassifier" />, AGENTS.md D27), and this is where the tab asks it.
/// </para>
/// <para>
/// <b>A hidden document type's table is dropped</b>, not labelled: <see cref="DatabaseObjectClassifier.ClassifyRelation" />
/// answers <see langword="null" /> for it. <b>Partitions never arrive here</b> - the SQL rolls them into their
/// parent - and <b>the count of them is withheld</b> unless the caller says the gate is open, because a
/// per-tenant partition count is the number of tenants.
/// </para>
/// </remarks>
internal static class SchemaTableAssembler
{
    /// <summary>Classifies and shapes the Tables tab's rows.</summary>
    /// <param name="rows">What <see cref="SchemaStatsQueries.TableStatsSql" /> read.</param>
    /// <param name="classifier">The classification over every registered store.</param>
    /// <param name="storeKey">The scope's store, whose documents and streams the tab may link to.</param>
    /// <param name="documentTypeNames">
    /// The .NET type name of each of this store's visible document tables, keyed by
    /// <see cref="SchemaKey.For(string, string)" />.
    /// </param>
    /// <param name="partitionCountsShown">
    /// Whether this visitor is past the database browser's gate, so a partition count may be shown.
    /// </param>
    public static IReadOnlyList<TableStats> Assemble(
        IReadOnlyList<TableStatsRow> rows,
        DatabaseObjectClassifier classifier,
        string storeKey,
        IReadOnlyDictionary<string, string> documentTypeNames,
        bool partitionCountsShown)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(documentTypeNames);

        List<TableStats> tables = new(rows.Count);

        foreach (TableStatsRow row in rows)
        {
            if (classifier.ClassifyRelation(row.Schema, row.Table) is not { } ownership)
            {
                // A hidden document type's table: absent, together with everything about it.
                continue;
            }

            bool ours = ownership.StoreKey is null
                || string.Equals(ownership.StoreKey, storeKey, StringComparison.OrdinalIgnoreCase);

            string? alias = ownership.Owner == DatabaseObjectOwner.MartenDocument && ours ? ownership.Alias : null;
            string? typeName = alias is not null
                && documentTypeNames.TryGetValue(SchemaKey.For(row.Schema, row.Table), out string? name)
                    ? name
                    : null;

            tables.Add(new TableStats(
                row.Schema,
                row.Table,
                row.TotalBytes,
                row.HeapBytes,
                row.IndexBytes,
                row.EstimatedRows,
                row.LiveRows,
                row.DeadRows,
                row.SequentialScans,
                row.IndexScans,
                row.LastVacuum,
                row.LastAnalyze,
                alias,
                typeName,
                ownership,
                ours,
                row.IsPartitioned,
                row.IsPartitioned && partitionCountsShown ? row.PartitionCount : null));
        }

        return tables;
    }

    /// <summary>
    /// Why the partition counts are withheld from this visitor, naming the gate that is shut - or
    /// <see langword="null" /> when they are shown.
    /// </summary>
    /// <param name="readOnly"><see cref="MartenStudioOptions.ReadOnly" />.</param>
    /// <param name="capabilityEnabled">Whether <c>BrowseDatabase</c> is effectively on.</param>
    /// <param name="authorized">The write policy's answer, or <see langword="null" /> when it was not asked.</param>
    public static string? PartitionCountsWithheld(bool readOnly, bool capabilityEnabled, bool? authorized)
    {
        if (capabilityEnabled && authorized == true)
        {
            return null;
        }

        const string Why =
            "How many partitions a table has is shown only past the database browser's gate, because a " +
            "per-tenant partition count is the number of tenants. ";

        return Why + (readOnly
            ? DatabaseGate.ReadOnlyDenial
            : !capabilityEnabled
                ? "It needs MartenStudioOptions.Capabilities.BrowseDatabase."
                : DatabaseGate.WritePolicyDenial);
    }
}
