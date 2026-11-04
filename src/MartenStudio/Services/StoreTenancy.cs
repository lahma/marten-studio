using JasperFx.Descriptors;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;
using Marten.Storage;

using Weasel.Core.MultiTenancy;
using Weasel.Postgresql.Migrations;

namespace MartenStudio.Services;

/// <summary>How a store lays its tenants out (1.2: the Stores page).</summary>
internal enum StoreTenancyKind
{
    /// <summary>One database, and no document type or event store is conjoined.</summary>
    Single,

    /// <summary>One database; conjoined tables shared by every tenant.</summary>
    Conjoined,

    /// <summary>One database; conjoined tables partitioned by <c>tenant_id</c>.</summary>
    ConjoinedPartitioned,

    /// <summary>A database per tenant, found in a master table.</summary>
    MasterTable,

    /// <summary>Tenants pooled across databases (<c>ShardedTenancy</c>).</summary>
    Sharded,

    /// <summary>A database per tenant, on one server.</summary>
    OneServer,

    /// <summary>A database per tenant, configured.</summary>
    Static,

    /// <summary>A tenancy of the application's own, or one that could not be read.</summary>
    Custom,
}

/// <summary>
/// A store's tenancy in words. A pure classification of the configured <see cref="IReadOnlyStoreOptions" />: it opens no connection and
/// enumerates no database (hard rule 14, D27), and it touches the tenancy object only through its type and its in-memory
/// <c>Cardinality</c>.
/// </summary>
/// <param name="Kind">Which shape.</param>
/// <param name="Words">The sentence the Stores page prints.</param>
internal sealed record StoreTenancy(StoreTenancyKind Kind, string Words)
{
    /// <summary>The column Marten partitions a tenant-partitioned table on.</summary>
    private const string TenantIdColumn = "tenant_id";

    /// <summary>What a store that did not build says: nothing was classified.</summary>
    public static StoreTenancy Unknown { get; } = new(StoreTenancyKind.Custom, "Not known: the store did not build");

    /// <summary>
    /// How <paramref name="options" /> lays its tenants out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DefaultTenancy</c>, <c>SingleServerMultiTenancy</c> and <c>TenantPartitionedSingleDatabaseTenancy</c> are
    /// <see langword="internal" /> at Marten 9.57.0, so the classes are matched on the public types they derive from and on
    /// cardinality: a master table and a sharded tenancy are public; <c>SingleServerMultiTenancy</c> is the only
    /// <see cref="SingleServerDatabaseCollection{T}" /> Marten has; a one-database tenancy is whatever else answers
    /// <see cref="DatabaseCardinality.Single" />. A tenancy whose properties throw is <see cref="StoreTenancyKind.Custom" />
    /// (fail closed).
    /// </para>
    /// <para>
    /// Only visible document types count towards "conjoined" and "partitioned": a hidden type is absent (D27), and the
    /// sentence must not tell a visitor a hidden type exists.
    /// </para>
    /// </remarks>
    /// <param name="options">The store's configuration.</param>
    /// <param name="isVisible">The host's <c>IsDocumentTypeVisible</c>, or <see langword="null" /> for every type.</param>
    public static StoreTenancy Classify(IReadOnlyStoreOptions options, Func<Type, bool>? isVisible)
    {
        ArgumentNullException.ThrowIfNull(options);

        ITenancy? tenancy = null;

        try
        {
            tenancy = options.Tenancy;

            switch (tenancy)
            {
                case MasterTableTenancy:
                    return new(StoreTenancyKind.MasterTable, "A database per tenant, from a master table");

                case ShardedTenancy or ITenantDatabasePool:
                    return new(StoreTenancyKind.Sharded, "Tenants pooled across databases (sharded)");

                case SingleServerDatabaseCollection<MartenDatabase>:
                    return new(StoreTenancyKind.OneServer, "A database per tenant, on one server");

                case StaticMultiTenancy:
                    return new(StoreTenancyKind.Static, "A database per tenant, configured");
            }

            if (tenancy.Cardinality != DatabaseCardinality.Single)
            {
                return Custom(tenancy);
            }

            (int conjoined, bool partitioned) = Conjoined(options, isVisible);

            if (conjoined == 0 && options.Events.TenancyStyle != TenancyStyle.Conjoined)
            {
                return new(StoreTenancyKind.Single, "One database, no tenants");
            }

            partitioned |= options.Events.UseTenantPartitionedEvents;

            return partitioned
                ? new(StoreTenancyKind.ConjoinedPartitioned, "Tenants in one database, partitioned by tenant")
                : new(StoreTenancyKind.Conjoined, "Tenants in one database, in shared tables");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Custom(tenancy);
        }
    }

    /// <summary>
    /// How many visible document types are conjoined, and whether any of them is partitioned on <c>tenant_id</c>.
    /// </summary>
    /// <param name="options">The store's configuration.</param>
    /// <param name="isVisible">The host's visibility test, or <see langword="null" />.</param>
    internal static (int Count, bool Partitioned) Conjoined(IReadOnlyStoreOptions options, Func<Type, bool>? isVisible)
    {
        ArgumentNullException.ThrowIfNull(options);

        int count = 0;
        bool partitioned = false;

        foreach (IDocumentType documentType in options.AllKnownDocumentTypes())
        {
            if ((isVisible is not null && !isVisible(documentType.DocumentType)) || documentType.TenancyStyle != TenancyStyle.Conjoined)
            {
                continue;
            }

            count++;

            // Marten-managed partitions and MultiTenantedWithPartitioning both set a ListPartitioning on tenant_id.
            partitioned |= documentType is DocumentMapping { Partitioning.Columns: { } columns }
                && columns.Contains(TenantIdColumn, StringComparer.OrdinalIgnoreCase);
        }

        return (count, partitioned);
    }

    /// <summary>Whether <paramref name="documentType" /> is a conjoined collection partitioned on <c>tenant_id</c> (Marten-managed or declared).</summary>
    /// <param name="documentType">The collection.</param>
    internal static bool IsTenantPartitioned(IDocumentType documentType)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        return documentType.TenancyStyle == TenancyStyle.Conjoined
            && documentType is DocumentMapping { Partitioning.Columns: { } columns }
            && columns.Contains(TenantIdColumn, StringComparer.OrdinalIgnoreCase);
    }

    private static StoreTenancy Custom(ITenancy? tenancy)
    {
        string typeName = tenancy?.GetType().Name ?? "unknown";

        try
        {
            return new(StoreTenancyKind.Custom, $"A tenancy of the application's own ({typeName}), {tenancy!.Cardinality}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(StoreTenancyKind.Custom, $"A tenancy of the application's own ({typeName})");
        }
    }
}
