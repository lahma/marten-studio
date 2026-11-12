namespace KStudio;

/// <summary>What <see cref="KafkaWolverineExtensions.UseWolverineKafka" /> may be told about how a Wolverine application's clusters are listed (D43).</summary>
public sealed class KafkaWolverineOptions
{
    private readonly List<Action<string?, KafkaClusterBuilder>> hooks = [];

    /// <summary>
    /// The key of the default Kafka transport's cluster (default <c>wolverine</c>): lower-case letters, digits and hyphens, at most 63 characters. A named
    /// broker's cluster is <c>{ClusterKey}-{broker}</c>, and one tenant's of either is <c>{key}/{tenant}</c>.
    /// </summary>
    public string ClusterKey { get; set; } = "wolverine";

    /// <summary>Whether broker-per-tenant registrations are listed, one cluster entry per tenant (default <see langword="true" />).</summary>
    public bool IncludeTenants { get; set; } = true;

    /// <summary>The callbacks of <see cref="Cluster" />, in the order added.</summary>
    internal IReadOnlyList<Action<string?, KafkaClusterBuilder>> Hooks => hooks;

    /// <summary>
    /// Applies <paramref name="configure" /> to each discovered cluster's builder, after KStudio gave it its default name and the consumer groups it found:
    /// a display name or environment, <c>OAuthBearerTokenRefresh</c> (Wolverine's builder delegates are never run, so a host that refreshes tokens there
    /// must say so here), <c>ConsumerGroup</c> and <c>Topic</c>. A topic declared here is the host's descriptor and so wins over what was discovered
    /// about it, field by field (D43).
    /// </summary>
    /// <param name="configure">Receives the broker's name - <see langword="null" /> for the default transport - and the cluster's builder.</param>
    /// <returns>These options.</returns>
    public KafkaWolverineOptions Cluster(Action<string?, KafkaClusterBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        hooks.Add(configure);
        return this;
    }
}
