using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

namespace MartenStudio.Services.Schema;

/// <summary>What the Tables tab shows, and how many tables it withheld while a store could not be read.</summary>
/// <param name="Tables">The rows, in the order the catalog read gave them.</param>
/// <param name="Withheld">
/// How many tables were left out because a store that could not be read might own them
/// (<see cref="SchemaClassification.WithholdsRelation" />) - never a hidden type's table, which is simply absent.
/// </param>
internal sealed record AssembledTables(IReadOnlyList<TableStats> Tables, int Withheld);

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
/// per-tenant partition count is the number of tenants. <b>So are the row figures of Marten's tenancy
/// registry</b> (<see cref="TenancyRegistryTables" />), for the same reason: one row per tenant.
/// </para>
/// </remarks>
internal static class SchemaTableAssembler
{
    /// <summary>
    /// Marten's tenancy bookkeeping tables, each of which holds one row per tenant - so a row estimate, a
    /// live-row count or even an index-scan counter on one of them is a count of tenants.
    /// </summary>
    /// <remarks>
    /// <c>mt_tenant_partitions</c> is Marten-managed tenant partitioning's list (one row per tenant partition
    /// value), <c>mt_tenant_databases</c> is <c>MasterTableTenancy</c>'s (one row per tenant database, with its
    /// connection string), and <c>mt_tenant_migration_log</c> records each tenant a conjoined-to-partitioned
    /// migration completed. Matched by name in any schema: all three are <c>mt_</c>-prefixed, so the
    /// classifier calls them Marten's infrastructure wherever they live.
    /// </remarks>
    internal static readonly string[] TenancyRegistryTables = ["mt_tenant_partitions", "mt_tenant_databases", "mt_tenant_migration_log"];

    /// <summary>Classifies and shapes the Tables tab's rows.</summary>
    /// <param name="rows">What <see cref="SchemaStatsQueries.TableStatsSql" /> read.</param>
    /// <param name="classifier">The classification over every registered store.</param>
    /// <param name="storeKey">The scope's store, whose documents and streams the tab may link to.</param>
    /// <param name="documentTypeNames">
    /// The .NET type name of each of this store's visible document tables, keyed by
    /// <see cref="SchemaKey.For(string, string)" />.
    /// </param>
    /// <param name="partitionCountsShown">
    /// Whether this visitor is past the database browser's gate, so a partition count - and the tenancy
    /// registry's row figures - may be shown.
    /// </param>
    public static IReadOnlyList<TableStats> Assemble(
        IReadOnlyList<TableStatsRow> rows,
        DatabaseObjectClassifier classifier,
        string storeKey,
        IReadOnlyDictionary<string, string> documentTypeNames,
        bool partitionCountsShown) =>
        Assemble(rows, SchemaClassification.Complete(classifier), storeKey, documentTypeNames, partitionCountsShown).Tables;

    /// <summary>
    /// Classifies and shapes the Tables tab's rows, withholding what a store that could not be read might own.
    /// </summary>
    /// <param name="rows">What <see cref="SchemaStatsQueries.TableStatsSql" /> read.</param>
    /// <param name="classification">The classification, complete or degraded.</param>
    /// <param name="storeKey">The scope's store, whose documents and streams the tab may link to.</param>
    /// <param name="documentTypeNames">
    /// The .NET type name of each of this store's visible document tables, keyed by
    /// <see cref="SchemaKey.For(string, string)" />.
    /// </param>
    /// <param name="partitionCountsShown">
    /// Whether this visitor is past the database browser's gate, so a partition count - and the tenancy
    /// registry's row figures - may be shown.
    /// </param>
    public static AssembledTables Assemble(
        IReadOnlyList<TableStatsRow> rows,
        SchemaClassification classification,
        string storeKey,
        IReadOnlyDictionary<string, string> documentTypeNames,
        bool partitionCountsShown)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(documentTypeNames);

        List<TableStats> tables = new(rows.Count);
        int withheld = 0;

        foreach (TableStatsRow row in rows)
        {
            if (classification.Classifier.ClassifyRelation(row.Schema, row.Table) is not { } ownership)
            {
                // A hidden document type's table: absent, together with everything about it.
                continue;
            }

            if (classification.WithholdsRelation(row.Table, ownership))
            {
                // A store could not be read, and nothing readable vouches for this one.
                withheld++;
                continue;
            }

            bool ours = ownership.StoreKey is null
                || string.Equals(ownership.StoreKey, storeKey, StringComparison.OrdinalIgnoreCase);

            string? alias = ownership.Owner == DatabaseObjectOwner.MartenDocument && ours ? ownership.Alias : null;
            string? typeName = alias is not null
                && documentTypeNames.TryGetValue(SchemaKey.For(row.Schema, row.Table), out string? name)
                    ? name
                    : null;

            bool figuresWithheld = !partitionCountsShown && IsTenancyRegistry(row.Table);

            tables.Add(new TableStats(
                row.Schema,
                row.Table,
                row.TotalBytes,
                row.HeapBytes,
                row.IndexBytes,
                figuresWithheld ? -1 : row.EstimatedRows,
                figuresWithheld ? null : row.LiveRows,
                figuresWithheld ? null : row.DeadRows,
                figuresWithheld ? null : row.SequentialScans,
                figuresWithheld ? null : row.IndexScans,
                row.LastVacuum,
                row.LastAnalyze,
                alias,
                typeName,
                ownership,
                ours,
                row.IsPartitioned,
                row.IsPartitioned && partitionCountsShown ? row.PartitionCount : null,
                figuresWithheld));
        }

        return new AssembledTables(tables, withheld);
    }

    /// <summary>Whether <paramref name="table" /> is one of Marten's tenancy registry tables.</summary>
    public static bool IsTenancyRegistry(string table)
    {
        ArgumentNullException.ThrowIfNull(table);

        foreach (string registry in TenancyRegistryTables)
        {
            if (string.Equals(table, registry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Why the partition counts and the tenancy registry's figures are withheld from this visitor, naming the
    /// gate that is shut - or <see langword="null" /> when they are shown.
    /// </summary>
    /// <param name="readOnly"><see cref="MartenStudioOptions.ReadOnly" />.</param>
    /// <param name="capabilityEnabled">Whether <c>BrowseDatabase</c> is effectively on.</param>
    /// <param name="authorized">The policies' answer, or <see langword="null" /> when they were not asked.</param>
    /// <param name="refusal">
    /// When <paramref name="authorized" /> is <see langword="false" />, which policy said no - the store policy
    /// for the database as a whole, or the write policy with <c>BrowseDatabase</c> named - as
    /// <see cref="DatabaseAccess.EvaluatePoliciesAsync" /> reports it.
    /// </param>
    public static string? PartitionCountsWithheld(
        bool readOnly,
        bool capabilityEnabled,
        bool? authorized,
        DatabaseRefusal refusal = DatabaseRefusal.WritePolicy)
    {
        if (capabilityEnabled && authorized == true)
        {
            return null;
        }

        const string Why =
            "How many partitions a table has, and how many rows Marten's tenancy tables hold, are shown only past " +
            "the database browser's gate, because a per-tenant partition count and a tenancy table's row count " +
            "are both the number of tenants. ";

        return Why + (readOnly
            ? DatabaseGate.ReadOnlyDenial
            : !capabilityEnabled
                ? "It needs MartenStudioOptions.Capabilities.BrowseDatabase."
                : refusal == DatabaseRefusal.StorePolicy
                    ? DatabaseGate.StorePolicyDenial
                    : DatabaseGate.WritePolicyDenial);
    }
}
