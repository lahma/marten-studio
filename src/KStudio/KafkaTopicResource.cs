namespace KStudio;

/// <summary>
/// What KStudio's authorization policies are asked about (AGENTS.md D35). Deliberately unrelated to <c>MartenStoreResource</c> and
/// <c>PostgresStudioResource</c>: a handler written for one must never be handed another (D30).
/// </summary>
/// <param name="Cluster">The cluster entry's key.</param>
/// <param name="TenantId">The entry's own tenant id, never one from an address or a record; <see langword="null" /> for a cluster with none.</param>
/// <param name="Topic">The topic, or <see langword="null" /> for a cluster-level question: the cluster list, the cluster's description, the selector.</param>
/// <param name="Capability">The capability's name for a use of one (<c>SearchRecords</c>), or <see langword="null" /> for a read that needs none.</param>
public sealed record KafkaTopicResource(string Cluster, string? TenantId, string? Topic, string? Capability);
