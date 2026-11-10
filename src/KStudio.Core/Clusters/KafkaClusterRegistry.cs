using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using KStudio.Core.Wire;

namespace KStudio.Core.Clusters;

/// <summary>
/// The clusters the host registered, validated and split by role (decision 2). The host's configuration was cloned when the cluster was
/// added; nothing here writes to it.
/// </summary>
internal sealed class KafkaClusterRegistry
{
    private readonly Dictionary<string, KafkaClusterDefinition> byKey = new(StringComparer.Ordinal);

    /// <summary>Builds the registry; options the validation refuses never become clusters.</summary>
    /// <param name="options">The options.</param>
    public KafkaClusterRegistry(KafkaEngineOptions options)
    {
        IReadOnlyList<string> problems = KafkaEngineOptionsValidation.Validate(options);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException("KStudio's Kafka options are not valid: " + string.Join(" ", problems));
        }

        List<KafkaClusterDefinition> clusters = [];

        foreach (KafkaEngineOptions.ClusterRegistration registration in options.Clusters)
        {
            KafkaClusterDefinition definition = new(registration, options.Topics);
            clusters.Add(definition);
            byKey[definition.Key] = definition;
        }

        Clusters = clusters;
        Settings = KafkaEngineSnapshot.Take(options);
    }

    /// <summary>
    /// The options' values at validation. The engine reads its timings, group id prefix and limits from here only, so that a write to the
    /// options after validation changes nothing.
    /// </summary>
    public KafkaEngineSnapshot Settings { get; }

    /// <summary>Every cluster, in the order the host added them.</summary>
    public IReadOnlyList<KafkaClusterDefinition> Clusters { get; }

    /// <summary>Finds a cluster by its key.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cluster">The cluster.</param>
    public bool TryGet(string key, [NotNullWhen(true)] out KafkaClusterDefinition? cluster) => byKey.TryGetValue(key, out cluster);

    /// <summary>Finds a cluster by its key, or throws.</summary>
    /// <param name="key">The key.</param>
    public KafkaClusterDefinition Get(string key) =>
        byKey.TryGetValue(key, out KafkaClusterDefinition? cluster)
            ? cluster
            : throw new KeyNotFoundException("No Kafka cluster is registered under that key.");
}

/// <summary>One wire per cluster, created on first use from the factory and kept for the life of the container.</summary>
internal sealed class KafkaWires(KafkaClusterRegistry registry, IKafkaWireFactory factory)
{
    private readonly ConcurrentDictionary<string, Lazy<IKafkaWire>> wires = new(StringComparer.Ordinal);

    /// <summary>The wire of a cluster.</summary>
    /// <param name="clusterKey">The cluster's key.</param>
    public IKafkaWire For(string clusterKey) =>
        wires.GetOrAdd(clusterKey, key => new Lazy<IKafkaWire>(() => factory.Create(registry.Get(key)))).Value;
}

/// <summary>What the engine takes from the studio that hosts it.</summary>
internal interface IKafkaEngineSettings
{
    /// <summary>How long a catalog read is reused: the host studio's <c>RefreshInterval</c>.</summary>
    TimeSpan CatalogTimeToLive { get; }
}
