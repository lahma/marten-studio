using Confluent.Kafka;

using KStudio.Core.Correlation;
using KStudio.Core.Wire.Confluent;

namespace KStudio.Core.Clusters;

/// <summary>What the engine knows about one topic the host declared, or the default for one it did not.</summary>
internal sealed class KafkaTopicDescriptor
{
    private IReadOnlyList<CorrelationField>? correlationFields;

    /// <summary>Creates a descriptor from what a host declared.</summary>
    /// <param name="name">The topic's name.</param>
    /// <param name="settings">What the host said, or <see langword="null" /> for a topic that declared nothing.</param>
    public KafkaTopicDescriptor(string name, KafkaTopicBuilder? settings)
        : this(name, settings, null)
    {
    }

    /// <summary>
    /// Creates a descriptor from what a host declared and what a source found out (decision 5.6). The host's declaration is the descriptor and
    /// the source's facts fill only what the host left open: a source never replaces a host's key format, decoder, partitioner or description,
    /// and never adds a redaction rule.
    /// </summary>
    /// <param name="name">The topic's name.</param>
    /// <param name="settings">What the host said, or <see langword="null" /> for a topic it declared nothing about.</param>
    /// <param name="discovered">What a source found, or <see langword="null" />.</param>
    public KafkaTopicDescriptor(string name, KafkaTopicBuilder? settings, KafkaDiscoveredTopic? discovered)
    {
        Name = name;
        Discovered = discovered;

        KafkaTopicBuilder composed = settings?.Snapshot() ?? new KafkaTopicBuilder();

        if (discovered is not null)
        {
            if (composed.DescriptionText is null && discovered.Description is { Length: > 0 } description)
            {
                composed.Description(description);
            }

            // Wolverine writes the envelope's group id as the record key, UTF-8 (F8), so the key format is declared, not assumed.
            if (composed.KeyFormat is null)
            {
                composed.Key(KafkaKeyFormat.Utf8String);
            }

            // Wolverine's structured CloudEvents carry content-type application/json, which detection does not take for CloudEvents (F6).
            if (composed.DecoderSource is null && discovered.CloudEvents)
            {
                composed.Value(KafkaDecoders.CloudEvents);
            }

            // A claim, verified by sampling before any pruning (D39).
            if (composed.PartitionerValue is null && discovered.Partitioner is { } partitioner)
            {
                composed.Partitioner(partitioner);
            }
        }

        Settings = composed;
        Declared = settings is not null || discovered is not null;
    }

    /// <summary>The topic's name.</summary>
    public string Name { get; }

    /// <summary>Whether the host declared this topic at all, or a source found it (decision 5.6).</summary>
    public bool Declared { get; }

    /// <summary>What a source found out about this topic, or <see langword="null" /> for a topic only the host declared or nobody did.</summary>
    public KafkaDiscoveredTopic? Discovered { get; }

    /// <summary>What the host said (empty for an undeclared topic), with a source's facts filled in where the host left them open. ENG-B's decoder lives here too.</summary>
    public KafkaTopicBuilder Settings { get; }

    /// <summary>The description, when declared.</summary>
    public string? Description => Settings.DescriptionText;

    /// <summary>Whether the key format was declared; a verdict about keys says "assumed" when it was not.</summary>
    public bool KeyFormatDeclared => Settings.KeyFormat is not null;

    /// <summary>The key format: the declared one, or UTF-8 text.</summary>
    public KafkaKeyFormat KeyFormat => Settings.KeyFormat ?? KafkaKeyFormat.Utf8String;

    /// <summary>The partitioner the host declared, when it declared one: a claim the engine verifies before it prunes.</summary>
    public KafkaPartitioner? Partitioner => Settings.PartitionerValue;

    /// <summary>The topic's correlation fields (D42): the defaults, with the host's own, resolved once.</summary>
    public IReadOnlyList<CorrelationField> CorrelationFields => correlationFields ??= CorrelationDefaults.Resolve(Settings.Correlations);
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
        : this(registration, globalTopics, null, null)
    {
    }

    /// <summary>Builds a definition from a registration, the topics declared for every cluster and, for a cluster a source contributed, what it found.</summary>
    /// <param name="registration">The registration; its key is the entry key.</param>
    /// <param name="globalTopics">The topics declared on the options.</param>
    /// <param name="contributed">The cluster a source contributed, or <see langword="null" /> for a host cluster.</param>
    /// <param name="source">The source's name, or <see langword="null" />.</param>
    public KafkaClusterDefinition(
        KafkaEngineOptions.ClusterRegistration registration,
        IReadOnlyList<KeyValuePair<string, KafkaTopicBuilder>> globalTopics,
        KafkaContributedCluster? contributed,
        string? source)
    {
        Key = registration.Key;
        BaseKey = contributed?.Key ?? registration.Key;
        TenantId = contributed?.TenantId;
        CanBeLeftOut = contributed?.CanBeLeftOut == true;
        Source = source;
        DisplayName = registration.Builder.DisplayNameText ?? BaseKey;
        Environment = registration.Builder.EnvironmentText;
        Profile = registration.Builder.ProfileValue;
        TokenRefresh = registration.Builder.TokenRefresh;
        DeclaredConsumerGroups = [.. registration.Builder.ConsumerGroups];

        ConsumerSettings = ConfluentPropertyScopes.ForConsumer(registration.Consumer);
        AdminSettings = ConfluentPropertyScopes.ForAdmin(registration.Admin);
        ServersKey = ServersKeyOf(ConsumerSettings, AdminSettings);

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

        // A topic a source found: the host's descriptor of that name (the cluster's own, else a global one) stays the descriptor, and the
        // source's facts are composed beside it (decision 5.6). A source never competes with a host's declaration.
        if (contributed is not null)
        {
            foreach (KafkaDiscoveredTopic discovered in contributed.Topics)
            {
                KafkaTopicBuilder? host = HostBuilder(discovered.Name, globalTopics, registration.Builder.Topics);
                topics[discovered.Name] = new KafkaTopicDescriptor(discovered.Name, host, discovered);
            }

            // The groups a source's listeners use are paired with their topics, not declared for the cluster (#73): each is read for the topic that
            // names it, and the cluster-wide list holds them only for the Settings page and the host's code.
            DiscoveredConsumerGroups =
            [
                .. contributed.Topics
                    .Select(static x => x.ConsumerGroup)
                    .Where(static x => !string.IsNullOrWhiteSpace(x))
                    .Select(static x => x!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Take(MaxDiscoveredConsumerGroups),
            ];
        }

        ConsumerGroups = [.. DeclaredConsumerGroups.Concat(DiscoveredConsumerGroups).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The most consumer groups a source's listeners may add to a cluster, the first by name: what a host may declare for one.</summary>
    public const int MaxDiscoveredConsumerGroups = 50;

    /// <summary>
    /// The cluster's entry key, the engine's key for it: the cluster's own key, or <c>{base}/{tenant}</c> for one tenant of a broker-per-tenant
    /// cluster (decision 6). It is what <c>?cluster=</c> carries.
    /// </summary>
    public string Key { get; }

    /// <summary>The cluster's key without a tenant: what a policy sees as <c>KafkaTopicResource.Cluster</c>.</summary>
    public string BaseKey { get; }

    /// <summary>The tenant this entry is a cluster of, or <see langword="null" />.</summary>
    public string? TenantId { get; }

    /// <summary>Whether the source that contributed the cluster lets the host leave it out of discovery.</summary>
    public bool CanBeLeftOut { get; }

    /// <summary>The name of the source that contributed the cluster, or <see langword="null" /> for a host cluster.</summary>
    public string? Source { get; }

    /// <summary>The name shown for the cluster.</summary>
    public string DisplayName { get; }

    /// <summary>The environment label, when one was set.</summary>
    public string? Environment { get; }

    /// <summary>The profile the host set with <c>KafkaClusterBuilder.Profile</c>, or <see langword="null" /> for none (never inferred: D47).</summary>
    public KafkaProfile? Profile { get; }

    /// <summary>The OAuth token refresh hook, when one was set.</summary>
    public Action<IClient, string>? TokenRefresh { get; }

    /// <summary>The groups the host declared for the whole cluster with <c>KafkaClusterBuilder.ConsumerGroup</c>: read on every topic's page.</summary>
    public IReadOnlyList<string> DeclaredConsumerGroups { get; }

    /// <summary>The groups a source found its listeners using (at most <see cref="MaxDiscoveredConsumerGroups" />): each is read only for the topics that name it.</summary>
    public IReadOnlyList<string> DiscoveredConsumerGroups { get; private set; } = [];

    /// <summary>Every group of the cluster, the declared ones first.</summary>
    public IReadOnlyList<string> ConsumerGroups { get; }

    /// <summary>
    /// The cluster's bootstrap servers as a set: trimmed, lower-cased, empty entries dropped, ordinal-sorted and joined, or <see langword="null" /> when the
    /// configuration names none. A key to compare clusters by, built from configuration alone - never shown, and nothing connects for it.
    /// </summary>
    public string? ServersKey { get; }

    /// <summary>
    /// The groups to read on a topic's page: the groups the host declared for the cluster, and the group a source paired with the topic's listener
    /// (when it is among the cluster's first <see cref="MaxDiscoveredConsumerGroups" />). One read per group and topic, never one per group of the cluster.
    /// </summary>
    /// <param name="topic">The topic's name.</param>
    /// <returns>The groups, each with whether it is cluster-wide (declared by the host, with nothing saying it reads this topic).</returns>
    public IReadOnlyList<(string Group, bool ClusterWide)> GroupsOf(string topic)
    {
        string? paired = Descriptor(topic).Discovered?.ConsumerGroup;

        if (paired is not null && !DiscoveredConsumerGroups.Contains(paired, StringComparer.Ordinal))
        {
            paired = null;
        }

        List<(string Group, bool ClusterWide)> groups = [];

        foreach (string group in DeclaredConsumerGroups.Distinct(StringComparer.Ordinal))
        {
            groups.Add((group, paired is null || !string.Equals(group, paired, StringComparison.Ordinal)));
        }

        if (paired is not null && !DeclaredConsumerGroups.Contains(paired, StringComparer.Ordinal))
        {
            groups.Add((paired, false));
        }

        return groups;
    }

    private static string? ServersKeyOf(IReadOnlyDictionary<string, string> consumer, IReadOnlyDictionary<string, string> admin)
    {
        string? servers = consumer.TryGetValue("bootstrap.servers", out string? own) && !string.IsNullOrWhiteSpace(own)
            ? own
            : admin.TryGetValue("bootstrap.servers", out string? other) ? other : null;

        if (string.IsNullOrWhiteSpace(servers))
        {
            return null;
        }

        string[] parts =
        [
            .. servers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(static x => string.Concat(x.Where(static c => !char.IsWhiteSpace(c))).ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        return parts.Length == 0 ? null : string.Join(',', parts);
    }

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

    private static KafkaTopicBuilder? HostBuilder(
        string name,
        IReadOnlyList<KeyValuePair<string, KafkaTopicBuilder>> globalTopics,
        IReadOnlyList<KeyValuePair<string, KafkaTopicBuilder>> ownTopics)
    {
        KafkaTopicBuilder? found = null;

        foreach (KeyValuePair<string, KafkaTopicBuilder> global in globalTopics)
        {
            if (string.Equals(global.Key, name, StringComparison.Ordinal))
            {
                found = global.Value;
            }
        }

        foreach (KeyValuePair<string, KafkaTopicBuilder> own in ownTopics)
        {
            if (string.Equals(own.Key, name, StringComparison.Ordinal))
            {
                found = own.Value;
            }
        }

        return found;
    }
}
