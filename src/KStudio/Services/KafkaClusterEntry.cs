using KStudio.Core.Catalog;
using KStudio.Core.Clusters;

namespace KStudio.Services;

/// <summary>
/// One cluster as the selector lists it and the policies are asked about it (D35): a registered cluster, or - for a broker-per-tenant
/// cluster - one tenant of it. The tenant is a property of the entry, so no address can name a tenant the entry does not carry.
/// </summary>
/// <param name="Key">
/// The entry's key, compared ordinally: the cluster's registration name, or <c>{name}/{tenant}</c> for one tenant of a broker-per-tenant cluster
/// (D43). It travels in the address as <c>?cluster=</c>, escaped, and is the engine's key. A policy never sees it: see <see cref="Cluster" />.
/// </param>
/// <param name="TenantId">The entry's own tenant id, or <see langword="null" /> for a cluster with none.</param>
/// <param name="DisplayName">The name the selector shows.</param>
/// <param name="Environment">The environment label, when the host set one.</param>
/// <param name="Profile">The profile the host set on the cluster, or <see langword="null" /> for none (AGENTS.md D47: never inferred).</param>
internal sealed record KafkaClusterEntry(string Key, string? TenantId, string DisplayName, string? Environment, KafkaProfile? Profile = null)
{
    /// <summary>
    /// The cluster's own key without a tenant: what <see cref="KafkaTopicResource.Cluster" /> and the audit scope carry, so that a policy never
    /// parses a key (D35, D43). For an entry with no tenant it is the entry's key.
    /// </summary>
    public string Cluster { get; init; } = KafkaClusterKey.BaseOf(Key);

    /// <summary>
    /// The keys of the other clusters, of another base cluster, whose bootstrap servers are the same set as this one's (#72), from configuration alone.
    /// Keys, not names: a name is drawn only for a cluster the visitor may see (<see cref="SameServersNames" />), so a refused cluster is never described.
    /// </summary>
    public IReadOnlyList<string> SameServersAs { get; init; } = [];

    /// <summary>
    /// The labels of the clusters in <see cref="SameServersAs" /> among <paramref name="visible" />, in the order the visitor's selector lists them.
    /// A cluster the visitor may not see is left out.
    /// </summary>
    /// <param name="visible">The entries this visitor may see.</param>
    public IReadOnlyList<string> SameServersNames(IEnumerable<KafkaClusterEntry> visible) =>
        SameServersAs.Count == 0 ? [] : [.. visible.Where(x => SameServersAs.Contains(x.Key, StringComparer.Ordinal)).Select(static x => x.Label)];

    /// <summary>Value equality, as a record's, with the same-servers list compared by its elements: the entries are rebuilt on every read.</summary>
    /// <param name="other">The other entry.</param>
    public bool Equals(KafkaClusterEntry? other) =>
        other is not null
        && Key == other.Key
        && TenantId == other.TenantId
        && DisplayName == other.DisplayName
        && Environment == other.Environment
        && Profile == other.Profile
        && Cluster == other.Cluster
        && SameServersAs.SequenceEqual(other.SameServersAs, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Key, TenantId, DisplayName, Environment, Profile, Cluster);

    /// <summary>What the selector and the Activity screen's Scope column show: <c>primary</c>, or <c>primary · tenant-a</c>.</summary>
    public string Label => TenantId is null ? DisplayName : DisplayName + " · " + TenantId;
}

/// <summary>Where <see cref="KafkaState" /> gets the cluster entries it lists. A seam so that a broker-per-tenant cluster is one entry per tenant.</summary>
internal interface IKafkaClusterEntrySource
{
    /// <summary>The entries, in the order the host registered them. No I/O.</summary>
    IReadOnlyList<KafkaClusterEntry> Entries { get; }
}

/// <summary>The engine's registered clusters, one entry each; a broker-per-tenant cluster a source contributed is one entry per tenant (D43).</summary>
internal sealed class CatalogClusterEntrySource(IKafkaCatalog catalog) : IKafkaClusterEntrySource
{
    /// <inheritdoc />
    public IReadOnlyList<KafkaClusterEntry> Entries =>
        [.. catalog.Clusters.Select(static x => new KafkaClusterEntry(x.Key, x.TenantId, x.DisplayName, x.Environment, x.Profile) { Cluster = x.BaseKey, SameServersAs = x.SameServersAs ?? [] })];
}
