using Confluent.Kafka;

namespace KStudio;

/// <summary>What a host says about one cluster beyond its client configuration (AGENTS.md D36).</summary>
public sealed class KafkaClusterBuilder
{
    private readonly List<string> consumerGroups = [];
    private readonly List<KeyValuePair<string, KafkaTopicBuilder>> topics = [];

    internal KafkaClusterBuilder()
    {
    }

    /// <summary>The display name, when one was set.</summary>
    internal string? DisplayNameText { get; private set; }

    /// <summary>The environment label, when one was set.</summary>
    internal string? EnvironmentText { get; private set; }

    /// <summary>The OAuth token refresh hook, when one was set.</summary>
    internal Action<IClient, string>? TokenRefresh { get; private set; }

    /// <summary>The consumer groups the host's own consumers use, in the order declared.</summary>
    internal IReadOnlyList<string> ConsumerGroups => consumerGroups;

    /// <summary>The topic descriptors declared for this cluster, in the order declared.</summary>
    internal IReadOnlyList<KeyValuePair<string, KafkaTopicBuilder>> Topics => topics;

    /// <summary>The name shown for the cluster (1 to 100 characters). Defaults to the cluster's key.</summary>
    /// <param name="displayName">The name.</param>
    /// <returns>This builder.</returns>
    public KafkaClusterBuilder DisplayName(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        DisplayNameText = displayName;
        return this;
    }

    /// <summary>A free-text environment label such as <c>production</c> (1 to 40 characters), shown as a chip.</summary>
    /// <param name="environment">The label.</param>
    /// <returns>This builder.</returns>
    public KafkaClusterBuilder Environment(string environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        EnvironmentText = environment;
        return this;
    }

    /// <summary>
    /// The hook that gives the engine's own clients an OAuth bearer token when librdkafka asks for one. It is applied to the
    /// engine's admin client and scan consumers and to nothing else; the host's own builder delegates are never run (D37). A
    /// handler that throws is caught and reported to librdkafka as a failed refresh.
    /// </summary>
    /// <param name="handler">The refresh handler; sets the token with <c>OAuthBearerSetToken</c> on the client it is given.</param>
    /// <returns>This builder.</returns>
    public KafkaClusterBuilder OAuthBearerTokenRefresh(Action<IClient, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        TokenRefresh = handler;
        return this;
    }

    /// <summary>Declares a descriptor for a topic of this cluster; it replaces, never merges with, a global one of the same name.</summary>
    /// <param name="name">The topic's name.</param>
    /// <param name="configure">Describes the topic.</param>
    /// <returns>This builder.</returns>
    public KafkaClusterBuilder Topic(string name, Action<KafkaTopicBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        KafkaTopicBuilder builder = new();
        configure(builder);
        topics.Add(new KeyValuePair<string, KafkaTopicBuilder>(name, builder));
        return this;
    }

    /// <summary>
    /// A consumer group the host's own consumers use, whose state and lag the engine may read (up to 50 per cluster). The engine
    /// never lists all of a cluster's groups.
    /// </summary>
    /// <param name="groupId">The group's id.</param>
    /// <returns>This builder.</returns>
    public KafkaClusterBuilder ConsumerGroup(string groupId)
    {
        ArgumentNullException.ThrowIfNull(groupId);
        consumerGroups.Add(groupId);
        return this;
    }
}
