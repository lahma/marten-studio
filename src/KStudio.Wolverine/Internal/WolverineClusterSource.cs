using System.Text;

using Confluent.Kafka;

using KStudio.Core.Clusters;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Wolverine;
using Wolverine.Kafka.Internals;

namespace KStudio.Internal;

/// <summary><see cref="KafkaWolverineOptions" /> as <c>UseWolverineKafka</c> found them: a copy, so a later write to the host's object reaches nothing.</summary>
internal sealed class WolverineSourceSettings
{
    private WolverineSourceSettings(
        string clusterKey,
        bool includeTenants,
        IReadOnlyList<Action<string?, KafkaClusterBuilder>> hooks,
        IReadOnlyList<Type> cloudEventsSerializers,
        IReadOnlySet<string> excludedBrokers)
    {
        ClusterKey = clusterKey;
        IncludeTenants = includeTenants;
        Hooks = hooks;
        CloudEventsSerializers = cloudEventsSerializers;
        ExcludedBrokers = excludedBrokers;
    }

    /// <summary>The default transport's cluster key.</summary>
    public string ClusterKey { get; }

    /// <summary>Whether broker-per-tenant registrations are listed.</summary>
    public bool IncludeTenants { get; }

    /// <summary>The host's callbacks for each cluster's builder.</summary>
    public IReadOnlyList<Action<string?, KafkaClusterBuilder>> Hooks { get; }

    /// <summary>The serializer types the host declared to write structured CloudEvents.</summary>
    public IReadOnlyList<Type> CloudEventsSerializers { get; }

    /// <summary>The names of the brokers left out of discovery, ordinal.</summary>
    public IReadOnlySet<string> ExcludedBrokers { get; }

    /// <summary>Copies <paramref name="options" />.</summary>
    /// <param name="options">The host's options.</param>
    /// <exception cref="ArgumentException">The cluster key is not a valid one.</exception>
    public static WolverineSourceSettings Take(KafkaWolverineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!KafkaClusterKey.IsValid(options.ClusterKey))
        {
            throw new ArgumentException(
                "KafkaWolverineOptions.ClusterKey must be lower-case letters, digits and hyphens, starting with a letter or digit, at most 63 characters.",
                nameof(options));
        }

        return new WolverineSourceSettings(
            options.ClusterKey,
            options.IncludeTenants,
            [.. options.Hooks],
            [.. options.CloudEventsSerializers],
            new HashSet<string>(options.ExcludedBrokers, StringComparer.Ordinal));
    }
}

/// <summary>
/// KStudio's view of a Wolverine application's Kafka configuration (D43, decision 5): one cluster per Kafka transport, one per broker-per-tenant
/// registration, and each transport's topics. It reads the built <c>WolverineOptions</c> once, when the cluster registry is first built, and starts,
/// connects and writes nothing: it reads no message store, envelope table, handler graph or runtime agent, runs none of Wolverine's builder callbacks
/// and calls no method that completes or inherits configuration. Every failure is a note, never an exception.
/// </summary>
/// <param name="services">The application's services.</param>
/// <param name="settings">How the clusters are listed.</param>
internal sealed class WolverineClusterSource(IServiceProvider services, WolverineSourceSettings settings) : IKafkaClusterSource
{
    /// <summary>The source's name.</summary>
    public const string SourceName = "Wolverine";

    /// <summary>The name Wolverine gives its default Kafka transport (<c>new KafkaTransport()</c>).</summary>
    private const string DefaultProtocol = "kafka";

    /// <summary>The most consumer groups a cluster takes from discovery: the engine's own bound.</summary>
    private const int MaxConsumerGroups = KafkaClusterDefinition.MaxDiscoveredConsumerGroups;

    /// <inheritdoc />
    public string Name => SourceName;

    /// <inheritdoc />
    public KafkaClusterContribution Contribute()
    {
        List<KafkaContributedCluster> clusters = [];
        List<KafkaSourceNote> notes = [];
        HashSet<string> usedKeys = new(StringComparer.Ordinal);

        // Tenants are read only once the application has started: a broker-per-tenant registration has no configuration until Wolverine's runtime
        // has connected the parent, and that is part of starting. With no host lifetime registered there is nothing to wait for.
        IHostApplicationLifetime? lifetime = services.GetService<IHostApplicationLifetime>();
        bool started = lifetime is null || lifetime.ApplicationStarted.IsCancellationRequested;
        bool incomplete = settings.IncludeTenants && !started;

        try
        {
            // The built options, as Wolverine registered them. Nothing is resolved that could start or connect anything.
            WolverineOptions? wolverine = services.GetService<WolverineOptions>();

            if (wolverine is null)
            {
                notes.Add(new KafkaSourceNote("clusters", "Wolverine is not registered in this application", "Wolverine is not registered in this application, so no cluster was listed."));
                return new KafkaClusterContribution(clusters, notes);
            }

            foreach (KafkaTransport transport in wolverine.Transports.OfType<KafkaTransport>().ToArray())
            {
                AddTransport(clusters, notes, usedKeys, wolverine, transport, listTenants: settings.IncludeTenants && started);
            }

            if (clusters.Count == 0 && notes.Count == 0)
            {
                notes.Add(new KafkaSourceNote("clusters", "Wolverine has no Kafka transport", "Wolverine has no Kafka transport in this application, so no cluster was listed."));
            }
        }
        catch (Exception e)
        {
            // Never thrown from a source, cancellation included: the host's own clusters must still list.
            notes.Add(new KafkaSourceNote("clusters", "reading Wolverine's configuration failed (" + e.GetType().Name + ")", "Reading Wolverine's configuration failed (" + e.GetType().Name + "), so some clusters were not listed."));
        }

        return incomplete
            ? new KafkaClusterContribution(clusters, notes, Incomplete: true, IncompleteNote: IncompleteNote, CompleteWhen: lifetime!.ApplicationStarted)
            : new KafkaClusterContribution(clusters, notes);
    }

    /// <summary>The Settings page's sentence while the tenants are not read yet.</summary>
    internal const string IncompleteNote = "Tenant clusters are listed once the application has started.";

    private void AddTransport(
        List<KafkaContributedCluster> clusters,
        List<KafkaSourceNote> notes,
        HashSet<string> usedKeys,
        WolverineOptions wolverine,
        KafkaTransport transport,
        bool listTenants)
    {
        string? broker = string.Equals(transport.Protocol, DefaultProtocol, StringComparison.Ordinal) ? null : transport.Protocol;

        // A broker the host left out is not discovered at all (#72): no cluster, topics, groups or tenants, and no note.
        if (broker is not null && settings.ExcludedBrokers.Contains(broker))
        {
            return;
        }

        string? key = KeyFor(broker);
        string label = broker is null ? "the default Kafka transport" : "the Kafka transport '" + broker + "'";

        if (key is null)
        {
            notes.Add(new KafkaSourceNote("a cluster", "its broker name makes no cluster key", "The Kafka transport '" + broker + "' was not listed: its name makes no cluster key."));
            return;
        }

        // Two named brokers whose names make one slug: the second is not listed, and neither are its tenants, which would otherwise stand under the first's cluster.
        if (!usedKeys.Add(key))
        {
            notes.Add(new KafkaSourceNote("a cluster", "its key is already used", "The Kafka transport '" + broker + "' was not listed: its name makes the cluster key '" + key + "', which another transport has."));
            return;
        }

        if (!TryConfigs(transport, out ConsumerConfig? consumer, out AdminClientConfig? admin))
        {
            notes.Add(new KafkaSourceNote("a cluster", "it has no bootstrap servers", "Wolverine's " + label + " has no bootstrap servers, so it was not listed."));
            return;
        }

        WolverineTopicRead read = WolverineTopicReader.Read(transport, wolverine.ServiceName, settings.CloudEventsSerializers);
        notes.AddRange(read.Notes);

        if (read.ConsumerGroups.Count > MaxConsumerGroups)
        {
            notes.Add(new KafkaSourceNote(
                "consumer groups",
                "there are more than " + MaxConsumerGroups + " of them",
                "Only the first " + MaxConsumerGroups + " consumer groups of " + label + ", by name, are shown with lag."));
        }

        clusters.Add(Build(key, tenantId: null, broker, consumer, admin, read, notes));

        if (!listTenants)
        {
            return;
        }

        WolverineTenantProbeResult tenants = WolverineTenantProbe.Probe(transport);
        notes.AddRange(tenants.Notes);

        foreach (WolverineProbedTenant tenant in tenants.Tenants)
        {
            if (!TryConfigs(tenant.Transport, out ConsumerConfig? tenantConsumer, out AdminClientConfig? tenantAdmin))
            {
                continue;
            }

            // A tenant shares its parent's topology: the topics and groups found on the parent are the tenant's too (KafkaTenant.Compile).
            clusters.Add(Build(key, tenant.TenantId, broker, tenantConsumer, tenantAdmin, read, notes));
        }
    }

    private KafkaContributedCluster Build(
        string key,
        string? tenantId,
        string? broker,
        ConsumerConfig consumer,
        AdminClientConfig admin,
        WolverineTopicRead read,
        List<KafkaSourceNote> notes)
    {
        KafkaClusterBuilder builder = new();
        builder.DisplayName(Truncate(broker is null ? "Wolverine" : "Wolverine · " + broker, 100));

        // The groups the listeners use are not declared here: each travels with the topic whose listener names it (KafkaDiscoveredTopic.ConsumerGroup),
        // so a topic's page reads its own group and not every group of the application (#73). A group the host declares in a callback is the host's.
        foreach (Action<string?, KafkaClusterBuilder> hook in settings.Hooks)
        {
            try
            {
                hook(broker, builder);
            }
            catch (Exception e)
            {
                notes.Add(new KafkaSourceNote(
                    "a cluster callback",
                    "it threw (" + e.GetType().Name + ")",
                    "A Cluster callback threw (" + e.GetType().Name + "), so the rest of what it set for this cluster was not applied."));
            }
        }

        return new KafkaContributedCluster(key, tenantId, consumer, admin, builder, read.Topics);
    }

    /// <summary>
    /// Copies of the transport's consumer and admin configuration - the originals are never handed over, so nothing downstream can write to Wolverine's.
    /// A transport that set bootstrap servers on only one of the two is read with the other's.
    /// </summary>
    private static bool TryConfigs(KafkaTransport transport, out ConsumerConfig consumer, out AdminClientConfig admin)
    {
        consumer = new ConsumerConfig(new Dictionary<string, string>(transport.ConsumerConfig, StringComparer.Ordinal));
        admin = new AdminClientConfig(new Dictionary<string, string>(transport.AdminClientConfig, StringComparer.Ordinal));

        bool hasConsumer = !string.IsNullOrWhiteSpace(consumer.BootstrapServers);
        bool hasAdmin = !string.IsNullOrWhiteSpace(admin.BootstrapServers);

        if (!hasConsumer && !hasAdmin)
        {
            return false;
        }

        if (!hasConsumer)
        {
            consumer.BootstrapServers = admin.BootstrapServers;
        }
        else if (!hasAdmin)
        {
            admin.BootstrapServers = consumer.BootstrapServers;
        }

        return true;
    }

    /// <summary>The cluster key of a transport: the configured key, or <c>{key}-{slug}</c> for a named broker; <see langword="null" /> when the name leaves no slug.</summary>
    private string? KeyFor(string? broker)
    {
        if (broker is null)
        {
            return settings.ClusterKey;
        }

        string slug = Slug(broker);

        return slug.Length == 0 ? null : settings.ClusterKey + "-" + slug;
    }

    /// <summary>The broker's name lower-cased, each character outside <c>[a-z0-9-]</c> replaced by <c>-</c>, runs collapsed and the ends trimmed.</summary>
    internal static string Slug(string name)
    {
        StringBuilder slug = new(name.Length);

        foreach (char character in name.ToLowerInvariant())
        {
            char next = character is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? character : '-';

            if (next != '-' || (slug.Length > 0 && slug[^1] != '-'))
            {
                slug.Append(next);
            }
        }

        return slug.ToString().Trim('-');
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}
