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
/// <remarks>
/// <para>
/// <b>A source runs once, or twice.</b> The registry runs every source when it is built. A source that says its answer is incomplete (its
/// contribution is <see cref="KafkaClusterContribution.Incomplete" />) is run <em>once more</em>, on the first access of the registry after the
/// token it gave has fired (for Wolverine, <c>ApplicationStarted</c>): the tenants of a broker-per-tenant registration have no configuration
/// until Wolverine's runtime has connected them, and a registry first read before then must not list them half-configured. The second answer
/// can only <em>add</em> tenant entries, atomically, as a new snapshot: an entry already listed is never changed or removed, and a source is
/// never run a third time.
/// </para>
/// </remarks>
internal sealed class KafkaClusterRegistry
{
    private readonly Lock gate = new();
    private readonly List<KafkaClusterDefinition> clusters = [];
    private readonly Dictionary<string, KafkaClusterDefinition> byKey = new(StringComparer.Ordinal);
    private readonly List<KafkaRegistryNote> notes = [];
    private readonly List<PendingSource> pending = [];
    private readonly KafkaEngineOptions options;
    private readonly ILogger? logger;
    private readonly KafkaLogOnce? once;
    private volatile RegistryState state;
    private volatile bool hasPending;

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

        this.options = options;
        logger = services?.GetService<KStudioLoggers>()?.Catalog;
        once = services?.GetService<KafkaLogOnce>();

        foreach (KafkaEngineOptions.ClusterRegistration registration in options.Clusters)
        {
            KafkaClusterDefinition definition = new(registration, options.Topics);
            clusters.Add(definition);
            byKey[definition.Key] = definition;
        }

        if (services is not null)
        {
            foreach (Func<IServiceProvider, IKafkaClusterSource> factory in options.ClusterSources)
            {
                AddFromSource(factory, services);
            }
        }

        Settings = settings;
        state = Publish();
        hasPending = pending.Count > 0;
    }

    /// <summary>
    /// The options' values at validation. The engine reads its timings, group id prefix and limits from here only, so that a write to the
    /// options after validation changes nothing.
    /// </summary>
    public KafkaEngineSnapshot Settings { get; }

    /// <summary>Every cluster, in the order the host added them, then the clusters of each source in the order the source found them.</summary>
    public IReadOnlyList<KafkaClusterDefinition> Clusters => Current.Clusters;

    /// <summary>What the sources could not list, for the Settings page; the log has the same without the names.</summary>
    public IReadOnlyList<KafkaRegistryNote> Notes => Current.Notes;

    private RegistryState Current
    {
        get
        {
            if (hasPending)
            {
                CompletePending();
            }

            return state;
        }
    }

    /// <summary>Finds a cluster by its entry key.</summary>
    /// <param name="key">The entry key.</param>
    /// <param name="cluster">The cluster.</param>
    public bool TryGet(string key, [NotNullWhen(true)] out KafkaClusterDefinition? cluster) => Current.ByKey.TryGetValue(key, out cluster);

    /// <summary>Finds a cluster by its entry key, or throws.</summary>
    /// <param name="key">The entry key.</param>
    public KafkaClusterDefinition Get(string key) =>
        Current.ByKey.TryGetValue(key, out KafkaClusterDefinition? cluster)
            ? cluster
            : throw new KeyNotFoundException("No Kafka cluster is registered under that key.");

    private RegistryState Publish() =>
        new([.. clusters], new Dictionary<string, KafkaClusterDefinition>(byKey, StringComparer.Ordinal), [.. notes, .. pending.Select(static x => new KafkaRegistryNote(x.Name, x.Note, "some clusters", "the application had not started"))]);

    private void AddFromSource(Func<IServiceProvider, IKafkaClusterSource> factory, IServiceProvider services)
    {
        string sourceName = "A cluster source";
        IKafkaClusterSource source;
        KafkaClusterContribution contribution;

        // Any exception, cancellation included: a source runs inside the registry's construction, and what it throws must cost the host's own
        // clusters nothing.
        try
        {
            source = factory(services);
            sourceName = source.Name;
            contribution = source.Contribute();
        }
        catch (Exception e)
        {
            AddNote(sourceName, new KafkaSourceNote("clusters", "the source failed (" + e.GetType().Name + ")", "The source failed and listed nothing (" + e.GetType().Name + ")."));
            return;
        }

        foreach (KafkaSourceNote note in contribution.Notes)
        {
            AddNote(sourceName, note);
        }

        AddClusters(contribution, sourceName, tenantsOnly: false);

        if (contribution.Incomplete)
        {
            pending.Add(new PendingSource(source, sourceName, contribution.IncompleteNote ?? "Some clusters are listed once the application has started.", contribution.CompleteWhen));
        }
    }

    /// <summary>Runs every pending source whose token has fired, once more, and publishes the result as one new snapshot.</summary>
    private void CompletePending()
    {
        lock (gate)
        {
            bool changed = false;

            foreach (PendingSource source in pending.ToArray())
            {
                if (!source.When.IsCancellationRequested)
                {
                    continue;
                }

                // Removed first: a source is never run a third time, whatever the second run does.
                pending.Remove(source);
                changed = true;
                Complete(source);
            }

            if (changed)
            {
                state = Publish();
                hasPending = pending.Count > 0;
            }
        }
    }

    private void Complete(PendingSource source)
    {
        KafkaClusterContribution contribution;

        try
        {
            contribution = source.Source.Contribute();
        }
        catch (Exception e)
        {
            AddNote(source.Name, new KafkaSourceNote("tenant clusters", "the source failed (" + e.GetType().Name + ")", "The source failed when it was asked again, so its tenant clusters were not listed (" + e.GetType().Name + ")."));
            return;
        }

        // What the first answer already said is not said twice; what is new (a tenant Wolverine had not connected) is.
        HashSet<string> known = [.. notes.Select(static x => x.Text)];

        foreach (KafkaSourceNote note in contribution.Notes.Where(x => !known.Contains(x.Text)))
        {
            AddNote(source.Name, note);
        }

        AddClusters(contribution, source.Name, tenantsOnly: true);
    }

    private void AddClusters(KafkaClusterContribution contribution, string sourceName, bool tenantsOnly)
    {
        // A tenant is listed only under a parent this source listed: a parent that broke a rule or whose key was taken leaves its tenants out,
        // because a tenant entry shows under, and is judged by, its cluster.
        HashSet<string> listedParents = new(StringComparer.Ordinal);

        foreach (KafkaClusterDefinition listed in clusters.Where(x => x.TenantId is null && string.Equals(x.Source, sourceName, StringComparison.Ordinal)))
        {
            listedParents.Add(listed.BaseKey);
        }

        foreach (KafkaContributedCluster contributed in contribution.Clusters)
        {
            if (tenantsOnly && contributed.TenantId is null)
            {
                continue;
            }

            IReadOnlyList<string> problems = KafkaEngineOptionsValidation.ValidateContributed(contributed);

            if (problems.Count > 0)
            {
                AddNote(sourceName, new KafkaSourceNote("a cluster", "it breaks a rule for clusters", string.Join(" ", problems)));
                continue;
            }

            if (contributed.TenantId is not null && !listedParents.Contains(contributed.Key))
            {
                AddNote(
                    sourceName,
                    new KafkaSourceNote("a tenant cluster", "its cluster was not listed", "Tenant clusters of '" + contributed.Key + "' were not listed because that cluster was not."));
                continue;
            }

            string entryKey = KafkaClusterKey.Compose(contributed.Key, contributed.TenantId);

            if (byKey.ContainsKey(entryKey))
            {
                AddNote(sourceName, new KafkaSourceNote("a cluster", "its key is already used", "A cluster keyed '" + entryKey + "' is already registered, so the one found was not listed."));
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

            if (contributed.TenantId is null)
            {
                listedParents.Add(contributed.Key);
            }
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

    private void AddNote(string source, KafkaSourceNote note)
    {
        notes.Add(new KafkaRegistryNote(source, note.Text, note.What, note.Reason));

        if (logger is null || once is null)
        {
            return;
        }

        // The item is the general words, never a name (event 9677). The Settings page shows the same general words (KafkaRegistryNote.Generic), not Text.
        LogLevel level = KafkaLogOnce.WarningOrDebug(once, source + "|" + note.What + "|" + note.Reason);
        KStudioLog.ClusterSourceItemNotListed(logger, level, note.What, source, note.Reason);
    }

    private sealed record PendingSource(IKafkaClusterSource Source, string Name, string Note, CancellationToken When);

    private sealed record RegistryState(
        IReadOnlyList<KafkaClusterDefinition> Clusters,
        IReadOnlyDictionary<string, KafkaClusterDefinition> ByKey,
        IReadOnlyList<KafkaRegistryNote> Notes);
}

/// <summary>One thing a source did not list.</summary>
/// <param name="Source">The source's name.</param>
/// <param name="Text">The sentence the source wrote. It may name a tenant, a topic or a broker, so it is never shown to a visitor (D35): it is for the engine's own tests.</param>
/// <param name="What">What was not listed, in general terms (no name).</param>
/// <param name="Reason">Why, in general terms (no name).</param>
internal sealed record KafkaRegistryNote(string Source, string Text, string What, string Reason)
{
    /// <summary>
    /// The sentence the Settings page shows, built from the general words only: <c>Not listed: a tenant cluster, because Wolverine had not connected it.</c>
    /// A visitor a tenant's or a topic's policy refuses must not learn that it exists (D35).
    /// </summary>
    public string Generic => "Not listed: " + What + ", because " + Reason + ".";
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
