using Confluent.Kafka;

using KStudio.Core.Clusters;

namespace KStudio;

/// <summary>
/// The engine's options (AGENTS.md D36): the clusters KStudio may read and what it declares about their topics, the limits of a
/// scan, and a few timings. KStudio's own options derive from this class.
/// </summary>
/// <remarks>
/// The host's client configuration is <b>cloned</b> when a cluster is added and never written; a later change to the host's
/// instance is not seen.
/// </remarks>
public class KafkaEngineOptions
{
    private readonly List<ClusterRegistration> clusters = [];
    private readonly List<KeyValuePair<string, KafkaTopicBuilder>> topics = [];
    private readonly List<Func<IServiceProvider, IKafkaClusterSource>> clusterSources = [];

    /// <summary>What a scan may cost (D41).</summary>
    public KafkaScanLimits Limits { get; } = new();

    /// <summary>The prefix of the group id every scan consumer gets, followed by the scan's id. Default <c>kstudio-</c>.</summary>
    public string GroupIdPrefix { get; set; } = "kstudio-";

    /// <summary>How long the engine waits for one request to a cluster (1 second to 2 minutes). Default 10 seconds.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long an admin client may sit unused before it is disposed (30 seconds to 1 hour). Default 5 minutes.</summary>
    public TimeSpan AdminIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The clusters added, in the order added.</summary>
    internal IReadOnlyList<ClusterRegistration> Clusters => clusters;

    /// <summary>The topic descriptors declared for every cluster, in the order declared.</summary>
    internal IReadOnlyList<KeyValuePair<string, KafkaTopicBuilder>> Topics => topics;

    /// <summary>
    /// The sources of further clusters (D43), run once by the registry when it is built. Internal: the one source is KStudio.Wolverine's, granted
    /// by <c>InternalsVisibleTo</c> and an exact version pin.
    /// </summary>
    internal List<Func<IServiceProvider, IKafkaClusterSource>> ClusterSources => clusterSources;

    /// <summary>Adds a cluster from the host's own client configuration (a consumer, admin or producer config all work).</summary>
    /// <param name="key">The cluster's key: lower-case letters, digits and hyphens, at most 63 characters. It travels in the address.</param>
    /// <param name="config">The host's configuration, cloned now.</param>
    /// <param name="configure">Describes the cluster and its topics.</param>
    /// <returns>These options.</returns>
    public KafkaEngineOptions AddCluster(string key, ClientConfig config, Action<KafkaClusterBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(config);

        Dictionary<string, string> clone = Clone(config);
        return Add(key, clone, clone, configure);
    }

    /// <summary>Adds a cluster from a host that keeps both a consumer and an admin configuration, as Wolverine's Kafka transport does.</summary>
    /// <param name="key">The cluster's key: lower-case letters, digits and hyphens, at most 63 characters.</param>
    /// <param name="consumerConfig">The host's consumer configuration, cloned now.</param>
    /// <param name="adminConfig">The host's admin configuration, cloned now.</param>
    /// <param name="configure">Describes the cluster and its topics.</param>
    /// <returns>These options.</returns>
    public KafkaEngineOptions AddCluster(
        string key,
        ConsumerConfig consumerConfig,
        AdminClientConfig adminConfig,
        Action<KafkaClusterBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(consumerConfig);
        ArgumentNullException.ThrowIfNull(adminConfig);

        return Add(key, Clone(consumerConfig), Clone(adminConfig), configure);
    }

    /// <summary>Declares a descriptor for a topic name on every registered cluster.</summary>
    /// <param name="name">The topic's name.</param>
    /// <param name="configure">Describes the topic.</param>
    /// <returns>These options.</returns>
    public KafkaEngineOptions Topic(string name, Action<KafkaTopicBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        KafkaTopicBuilder builder = new();
        configure(builder);
        topics.Add(new KeyValuePair<string, KafkaTopicBuilder>(name, builder));
        return this;
    }

    private static Dictionary<string, string> Clone(IEnumerable<KeyValuePair<string, string>> config) =>
        new(config, StringComparer.Ordinal);

    private KafkaEngineOptions Add(
        string key,
        Dictionary<string, string> consumer,
        Dictionary<string, string> admin,
        Action<KafkaClusterBuilder>? configure)
    {
        KafkaClusterBuilder builder = new();
        configure?.Invoke(builder);
        clusters.Add(new ClusterRegistration(key, consumer, admin, builder));
        return this;
    }

    /// <summary>One <c>AddCluster</c> call, as the engine keeps it until the registry is built.</summary>
    /// <param name="Key">The key as the host wrote it, not yet validated.</param>
    /// <param name="Consumer">The clone of the consumer-side configuration.</param>
    /// <param name="Admin">The clone of the admin-side configuration.</param>
    /// <param name="Builder">What the host said about the cluster.</param>
    internal sealed record ClusterRegistration(
        string Key,
        Dictionary<string, string> Consumer,
        Dictionary<string, string> Admin,
        KafkaClusterBuilder Builder);
}
