namespace KStudio;

/// <summary>What <see cref="KafkaWolverineExtensions.UseWolverineKafka" /> may be told about how a Wolverine application's clusters are listed (D43).</summary>
public sealed class KafkaWolverineOptions
{
    private readonly List<Action<string?, KafkaClusterBuilder>> hooks = [];
    private readonly List<Type> cloudEventsSerializers = [];
    private readonly List<string> excludedBrokers = [];

    /// <summary>
    /// The key of the default Kafka transport's cluster (default <c>wolverine</c>): lower-case letters, digits and hyphens, at most 63 characters. A named
    /// broker's cluster is <c>{ClusterKey}-{broker}</c>, and one tenant's of either is <c>{key}/{tenant}</c>.
    /// </summary>
    public string ClusterKey { get; set; } = "wolverine";

    /// <summary>Whether broker-per-tenant registrations are listed, one cluster entry per tenant (default <see langword="true" />).</summary>
    public bool IncludeTenants { get; set; } = true;

    /// <summary>The callbacks of <see cref="Cluster" />, in the order added.</summary>
    internal IReadOnlyList<Action<string?, KafkaClusterBuilder>> Hooks => hooks;

    /// <summary>The serializer types of <see cref="CloudEventsSerializer(Type)" />, in the order added.</summary>
    internal IReadOnlyList<Type> CloudEventsSerializers => cloudEventsSerializers;

    /// <summary>The broker names of <see cref="ExcludeBroker" />, in the order added.</summary>
    internal IReadOnlyList<string> ExcludedBrokers => excludedBrokers;

    /// <summary>
    /// Declares that endpoints whose serializer is a <typeparamref name="TSerializer" /> (or a type assignable to it) write structured CloudEvents, so
    /// KStudio decodes their topics' values as CloudEvents, as it does for Wolverine's own <c>CloudEventsMapper</c> (<c>ce.*</c> predicates and
    /// chips). Nothing is guessed from a value: a custom serializer that sets the content type to <c>application/json</c> is only a CloudEvents
    /// writer because the host says so here.
    /// </summary>
    /// <typeparam name="TSerializer">The serializer type, a class or an interface.</typeparam>
    /// <returns>These options.</returns>
    /// <example>
    /// <code>
    /// services.AddKafkaStudio().UseWolverineKafka(w => w.CloudEventsSerializer&lt;StructuredCloudEventsSerializer&gt;());
    /// </code>
    /// </example>
    public KafkaWolverineOptions CloudEventsSerializer<TSerializer>()
        where TSerializer : class =>
        CloudEventsSerializer(typeof(TSerializer));

    /// <summary>
    /// Declares that endpoints whose serializer is an instance of <paramref name="serializerType" /> (or of a type assignable to it) write structured
    /// CloudEvents. See <see cref="CloudEventsSerializer{TSerializer}" />.
    /// </summary>
    /// <param name="serializerType">The serializer type, a class or an interface.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serializerType" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="serializerType" /> is an open generic type.</exception>
    public KafkaWolverineOptions CloudEventsSerializer(Type serializerType)
    {
        ArgumentNullException.ThrowIfNull(serializerType);

        if (serializerType.ContainsGenericParameters)
        {
            throw new ArgumentException("The serializer type must be a closed type.", nameof(serializerType));
        }

        cloudEventsSerializers.Add(serializerType);
        return this;
    }

    /// <summary>
    /// Leaves a named broker out of discovery: no cluster, no topics and no consumer groups of it are listed, nor the tenants of a broker-per-tenant
    /// registration of it. For brokers registered with <c>AddNamedKafkaBroker</c> against the same physical cluster as another (a durable broker and its
    /// buffered twin, say): the cluster selector marks such clusters, and this leaves one out. Not discovering a broker changes no host cluster or
    /// topic declaration.
    /// </summary>
    /// <remarks>
    /// The name is the broker's own, as given to <c>AddNamedKafkaBroker</c> and as the <see cref="Cluster" /> callback receives it, compared ordinally.
    /// A broker's cluster is listed as <c>{ClusterKey}-{name}</c> (the name lower-cased, a character outside letters, digits and hyphens made a hyphen)
    /// and shown as <c>Wolverine · {name}</c>. The default transport has no name and cannot be left out.
    /// </remarks>
    /// <param name="brokerName">The broker's name.</param>
    /// <returns>These options.</returns>
    /// <exception cref="ArgumentException"><paramref name="brokerName" /> is <see langword="null" />, empty or white space.</exception>
    public KafkaWolverineOptions ExcludeBroker(string brokerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerName);

        excludedBrokers.Add(brokerName);
        return this;
    }

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
