using Confluent.Kafka;

using KStudio.Core.Wire.Confluent;

namespace KStudio.Core.Clusters;

/// <summary>What the engine knows about one topic the host declared, or the default for one it did not.</summary>
internal sealed class KafkaTopicDescriptor
{
    /// <summary>Creates a descriptor from what a host declared.</summary>
    /// <param name="name">The topic's name.</param>
    /// <param name="settings">What the host said, or <see langword="null" /> for a topic that declared nothing.</param>
    public KafkaTopicDescriptor(string name, KafkaTopicBuilder? settings)
    {
        Name = name;
        Settings = settings ?? new KafkaTopicBuilder();
        Declared = settings is not null;
    }

    /// <summary>The topic's name.</summary>
    public string Name { get; }

    /// <summary>Whether the host declared this topic at all.</summary>
    public bool Declared { get; }

    /// <summary>What the host said (empty for an undeclared topic). ENG-B's decoder lives here too.</summary>
    public KafkaTopicBuilder Settings { get; }

    /// <summary>The description, when declared.</summary>
    public string? Description => Settings.DescriptionText;

    /// <summary>Whether the key format was declared; a verdict about keys says "assumed" when it was not.</summary>
    public bool KeyFormatDeclared => Settings.KeyFormat is not null;

    /// <summary>The key format: the declared one, or UTF-8 text.</summary>
    public KafkaKeyFormat KeyFormat => Settings.KeyFormat ?? KafkaKeyFormat.Utf8String;
}

/// <summary>
/// One registered cluster as the engine uses it: the host's configuration cloned and split by role, never the host's instance
/// (decision 2.1).
/// </summary>
internal sealed class KafkaClusterDefinition
{
    private readonly Dictionary<string, KafkaTopicDescriptor> topics;

    /// <summary>Builds a definition from one registration and the topics declared for every cluster.</summary>
    /// <param name="registration">The <c>AddCluster</c> call.</param>
    /// <param name="globalTopics">The topics declared on the options.</param>
    public KafkaClusterDefinition(
        KafkaEngineOptions.ClusterRegistration registration,
        IReadOnlyList<KeyValuePair<string, KafkaTopicBuilder>> globalTopics)
    {
        Key = registration.Key;
        DisplayName = registration.Builder.DisplayNameText ?? registration.Key;
        Environment = registration.Builder.EnvironmentText;
        TokenRefresh = registration.Builder.TokenRefresh;
        ConsumerGroups = [.. registration.Builder.ConsumerGroups];

        ConsumerSettings = ConfluentPropertyScopes.ForConsumer(registration.Consumer);
        AdminSettings = ConfluentPropertyScopes.ForAdmin(registration.Admin);

        topics = new Dictionary<string, KafkaTopicDescriptor>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, KafkaTopicBuilder> global in globalTopics)
        {
            topics[global.Key] = new KafkaTopicDescriptor(global.Key, global.Value);
        }

        // A descriptor of the cluster replaces, never merges with, a global one of the same name.
        foreach (KeyValuePair<string, KafkaTopicBuilder> own in registration.Builder.Topics)
        {
            topics[own.Key] = new KafkaTopicDescriptor(own.Key, own.Value);
        }
    }

    /// <summary>The cluster's key.</summary>
    public string Key { get; }

    /// <summary>The name shown for the cluster.</summary>
    public string DisplayName { get; }

    /// <summary>The environment label, when one was set.</summary>
    public string? Environment { get; }

    /// <summary>The OAuth token refresh hook, when one was set.</summary>
    public Action<IClient, string>? TokenRefresh { get; }

    /// <summary>The groups the host's own consumers use.</summary>
    public IReadOnlyList<string> ConsumerGroups { get; }

    /// <summary>The scan consumer's configuration: the clone minus producer-only keys. Never mutated.</summary>
    public IReadOnlyDictionary<string, string> ConsumerSettings { get; }

    /// <summary>The admin client's configuration: the clone minus consumer-only and producer-only keys. Never mutated.</summary>
    public IReadOnlyDictionary<string, string> AdminSettings { get; }

    /// <summary>The declared topics by name.</summary>
    public IReadOnlyDictionary<string, KafkaTopicDescriptor> Topics => topics;

    /// <summary>The descriptor of a topic: the declared one, or a default that says nothing was declared.</summary>
    /// <param name="topic">The topic's name.</param>
    public KafkaTopicDescriptor Descriptor(string topic) =>
        topics.TryGetValue(topic, out KafkaTopicDescriptor? declared) ? declared : new KafkaTopicDescriptor(topic, null);
}
