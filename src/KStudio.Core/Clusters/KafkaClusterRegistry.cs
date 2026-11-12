using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using KStudio.Core.Logging;
using KStudio.Core.Wire;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KStudio.Core.Clusters;

/// <summary>
/// The clusters the host registered, validated and split by role (decision 2), and the ones its sources contributed (D43). The host's
/// configuration was cloned when the cluster was added; nothing here writes to it.
/// </summary>
internal sealed class KafkaClusterRegistry
{
    private readonly Dictionary<string, KafkaClusterDefinition> byKey = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds the registry from options that have no cluster source; options the validation refuses never become clusters. A source needs the
    /// container, so options that name one are refused here.
    /// </summary>
    /// <param name="options">The options.</param>
    public KafkaClusterRegistry(KafkaEngineOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// Builds the registry: the host's own clusters first, then every source once, in the order they were added. A cluster a source contributed
    /// that breaks a rule, or whose key a host cluster holds, is skipped and noted, never thrown.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="services">The container the sources are given, or <see langword="null" /> for none.</param>
    public KafkaClusterRegistry(KafkaEngineOptions options, IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (services is null && options.ClusterSources.Count > 0)
        {
            throw new InvalidOperationException("KStudio's Kafka options name a cluster source, which needs the application's services to run.");
        }

        // The snapshot first, and the snapshot is what is validated: nothing written to the options after this line reaches the engine.
        KafkaEngineSnapshot settings = KafkaEngineSnapshot.Take(options);
        IReadOnlyList<string> problems = KafkaEngineOptionsValidation.Validate(options, settings);

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

        List<KafkaRegistryNote> notes = [];

        if (services is not null)
        {
            foreach (Func<IServiceProvider, IKafkaClusterSource> factory in options.ClusterSources)
            {
                AddFromSource(factory, services, options, clusters, notes);
            }
        }

        Clusters = clusters;
        Notes = notes;
        Settings = settings;
    }

    /// <summary>
    /// The options' values at validation. The engine reads its timings, group id prefix and limits from here only, so that a write to the
    /// options after validation changes nothing.
    /// </summary>
    public KafkaEngineSnapshot Settings { get; }

    /// <summary>Every cluster, in the order the host added them, then the clusters of each source in the order the source found them.</summary>
    public IReadOnlyList<KafkaClusterDefinition> Clusters { get; }

    /// <summary>What the sources could not list, for the Settings page; the log has the same without the names.</summary>
    public IReadOnlyList<KafkaRegistryNote> Notes { get; }

    /// <summary>Finds a cluster by its entry key.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="cluster">The cluster.</param>
    public bool TryGet(string key, [NotNullWhen(true)] out KafkaClusterDefinition? cluster) => byKey.TryGetValue(key, out cluster);

    /// <summary>Finds a cluster by its entry key, or throws.</summary>
    /// <param name="key">The entry key.</param>
    public KafkaClusterDefinition Get(string key) =>
        byKey.TryGetValue(key, out KafkaClusterDefinition? cluster)
            ? cluster
            : throw new KeyNotFoundException("No Kafka cluster is registered under that key.");

    private void AddFromSource(
        Func<IServiceProvider, IKafkaClusterSource> factory,
        IServiceProvider services,
        KafkaEngineOptions options,
        List<KafkaClusterDefinition> clusters,
        List<KafkaRegistryNote> notes)
    {
        ILogger? logger = services.GetService<KStudioLoggers>()?.Catalog;
        KafkaLogOnce? once = services.GetService<KafkaLogOnce>();
        string sourceName = "A cluster source";
        KafkaClusterContribution contribution;

        try
        {
            IKafkaClusterSource source = factory(services);
            sourceName = source.Name;
            contribution = source.Contribute();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A source must not throw; if it does, the host's own clusters are unaffected and the page says so.
            Note(logger, once, notes, sourceName, new KafkaSourceNote("clusters", "the source failed (" + e.GetType().Name + ")", "The source failed and listed nothing (" + e.GetType().Name + ")."));
            return;
        }

        foreach (KafkaSourceNote note in contribution.Notes)
        {
            Note(logger, once, notes, sourceName, note);
        }

        foreach (KafkaContributedCluster contributed in contribution.Clusters)
        {
            IReadOnlyList<string> problems = KafkaEngineOptionsValidation.ValidateContributed(contributed);

            if (problems.Count > 0)
            {
                Note(logger, once, notes, sourceName, new KafkaSourceNote("a cluster", "it breaks a rule for clusters", string.Join(" ", problems)));
                continue;
            }

            string entryKey = KafkaClusterKey.Compose(contributed.Key, contributed.TenantId);

            if (byKey.ContainsKey(entryKey))
            {
                Note(logger, once, notes, sourceName, new KafkaSourceNote("a cluster", "its key is already used", "A cluster keyed '" + entryKey + "' is already registered, so the one found was not listed."));
                continue;
            }

            KafkaContributedCluster cleaned = contributed with { Topics = ValidTopics(contributed.Topics) };
            KafkaEngineOptions.ClusterRegistration registration = new(
                entryKey,
                new Dictionary<string, string>(contributed.ConsumerConfig, StringComparer.Ordinal),
                new Dictionary<string, string>(contributed.AdminConfig, StringComparer.Ordinal),
                contributed.Builder);

            KafkaClusterDefinition definition = new(registration, options.Topics, cleaned, sourceName);
            clusters.Add(definition);
            byKey[definition.Key] = definition;
        }
    }

    /// <summary>The topics a source found that can be addressed: a valid name, once, and a description inside its bound.</summary>
    private static List<KafkaDiscoveredTopic> ValidTopics(IReadOnlyList<KafkaDiscoveredTopic> topics)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<KafkaDiscoveredTopic> valid = [];

        foreach (KafkaDiscoveredTopic topic in topics)
        {
            if (!KafkaTopicName.IsValid(topic.Name) || !seen.Add(topic.Name))
            {
                continue;
            }

            string? description = topic.Description;
            valid.Add(description is { Length: > KafkaEngineOptionsValidation.MaxDiscoveredDescriptionLength }
                ? topic with { Description = description[..KafkaEngineOptionsValidation.MaxDiscoveredDescriptionLength] }
                : topic);
        }

        return valid;
    }

    private static void Note(ILogger? logger, KafkaLogOnce? once, List<KafkaRegistryNote> notes, string source, KafkaSourceNote note)
    {
        notes.Add(new KafkaRegistryNote(source, note.Text));

        if (logger is null)
        {
            return;
        }

        // The item is the general words, never a name: the sentence with the name is on the Settings page (event 9677).
        LogLevel level = once is null ? LogLevel.Warning : KafkaLogOnce.WarningOrDebug(once, source + "|" + note.What + "|" + note.Reason);
        KStudioLog.ClusterSourceItemNotListed(logger, level, note.What, source, note.Reason);
    }
}

/// <summary>One thing a source did not list, as the Settings page shows it.</summary>
/// <param name="Source">The source's name.</param>
/// <param name="Text">The sentence.</param>
internal sealed record KafkaRegistryNote(string Source, string Text);

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
