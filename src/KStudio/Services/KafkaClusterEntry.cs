using KStudio.Core.Catalog;

namespace KStudio.Services;

/// <summary>
/// One cluster as the selector lists it and the policies are asked about it (D35): a registered cluster, or - for a broker-per-tenant
/// cluster - one tenant of it. The tenant is a property of the entry, so no address can name a tenant the entry does not carry.
/// </summary>
/// <param name="Key">The entry's key, compared ordinally: the cluster's registration name. It travels in the address as <c>?cluster=</c>.</param>
/// <param name="TenantId">The entry's own tenant id, or <see langword="null" /> for a cluster with none.</param>
/// <param name="DisplayName">The name the selector shows.</param>
/// <param name="Environment">The environment label, when the host set one.</param>
internal sealed record KafkaClusterEntry(string Key, string? TenantId, string DisplayName, string? Environment)
{
    /// <summary>What the selector and the Activity screen's Scope column show: <c>primary</c>, or <c>primary · tenant-a</c>.</summary>
    public string Label => TenantId is null ? DisplayName : DisplayName + " · " + TenantId;
}

/// <summary>Where <see cref="KafkaState" /> gets the cluster entries it lists. A seam so that a broker-per-tenant cluster is one entry per tenant.</summary>
internal interface IKafkaClusterEntrySource
{
    /// <summary>The entries, in the order the host registered them. No I/O.</summary>
    IReadOnlyList<KafkaClusterEntry> Entries { get; }
}

/// <summary>The engine's registered clusters, one entry each (a tenant-per-broker cluster arrives with the Wolverine bridge, M4).</summary>
internal sealed class CatalogClusterEntrySource(IKafkaCatalog catalog) : IKafkaClusterEntrySource
{
    /// <inheritdoc />
    public IReadOnlyList<KafkaClusterEntry> Entries { get; } =
        [.. catalog.Clusters.Select(static x => new KafkaClusterEntry(x.Key, null, x.DisplayName, x.Environment))];
}
