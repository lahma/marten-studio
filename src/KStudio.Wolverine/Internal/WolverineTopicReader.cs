using System.Globalization;

using Confluent.Kafka;

using KStudio.Core.Clusters;

using Wolverine.Configuration;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime.Interop;
using Wolverine.Runtime.Routing;

namespace KStudio.Internal;

/// <summary>The topics and consumer groups one Kafka transport declares.</summary>
/// <param name="Topics">The topics, ordinal by name.</param>
/// <param name="ConsumerGroups">The distinct consumer groups of its listeners, ordinal.</param>
/// <param name="Notes">What was not listed and why.</param>
internal sealed record WolverineTopicRead(
    IReadOnlyList<KafkaDiscoveredTopic> Topics,
    IReadOnlyList<string> ConsumerGroups,
    IReadOnlyList<KafkaSourceNote> Notes);

/// <summary>
/// Reads one Kafka transport's topics (D43, decision 5.5). It enumerates <c>Topics</c> and never indexes it - the indexer creates an endpoint - and
/// calls no method that completes, resolves or inherits configuration (<c>GetEffectiveConsumerConfig</c> writes the parent's values into a
/// topic's own): it reads plain getters only. <c>ForbiddenWolverineCallsTests</c> holds that on the IL.
/// </summary>
internal static class WolverineTopicReader
{
    /// <summary>The most topics one transport lists; the first by name.</summary>
    internal const int MaxTopics = 500;

    /// <summary>The most message types one topic lists.</summary>
    internal const int MaxMessageTypes = 20;

    private const string RetryInfix = ".retry.";

    private const string EnvelopeMapperTypeName = "Wolverine.Kafka.Internals.KafkaEnvelopeMapper";

    /// <summary>Reads <paramref name="transport" />'s topics.</summary>
    /// <param name="transport">A Kafka transport of the application's Wolverine configuration.</param>
    /// <param name="serviceName">The application's Wolverine service name, the last resort for a listener's consumer group.</param>
    public static WolverineTopicRead Read(KafkaTransport transport, string? serviceName)
    {
        ArgumentNullException.ThrowIfNull(transport);

        Dictionary<string, Accumulator> byName = new(StringComparer.Ordinal);
        List<KafkaSourceNote> notes = [];
        List<KafkaTopic> endpoints = [];

        // Enumerated, never indexed (F5): the Cache's indexer creates a topic.
        foreach (KafkaTopic topic in transport.Topics)
        {
            endpoints.Add(topic);
        }

        foreach (Endpoint endpoint in transport.Endpoints())
        {
            if (endpoint is KafkaTopicGroup group)
            {
                endpoints.Add(group);
            }
        }

        foreach (KafkaTopic endpoint in endpoints)
        {
            if (endpoint is KafkaTopicGroup group)
            {
                foreach (string name in group.TopicNames)
                {
                    Add(byName, notes, transport, serviceName, group, name);
                }
            }
            else if (!string.Equals(endpoint.TopicName, KafkaTopic.WolverineTopicsName, StringComparison.Ordinal))
            {
                Add(byName, notes, transport, serviceName, endpoint, endpoint.TopicName);
            }
        }

        // Tenant clusters inherit the parent's dead-letter name (KafkaTenant.Compile); native dead letters are on when any endpoint has them on.
        if (endpoints.Any(static x => x.NativeDeadLetterQueueEnabled) && !string.IsNullOrWhiteSpace(transport.DeadLetterQueueTopicName))
        {
            Accumulator dead = Get(byName, transport.DeadLetterQueueTopicName);
            dead.DeadLetter = true;
        }

        List<KafkaDiscoveredTopic> topics = [];
        string[] sorted = [.. byName.Keys.Order(StringComparer.Ordinal)];

        foreach (string name in sorted)
        {
            if (!KafkaTopicName.IsValid(name))
            {
                notes.Add(new KafkaSourceNote("a topic", "its name is one KStudio cannot address", "The topic '" + name + "' was not listed: its name is one KStudio cannot address."));
                continue;
            }

            if (topics.Count >= MaxTopics)
            {
                notes.Add(new KafkaSourceNote(
                    "topics",
                    "there are more than " + MaxTopics.ToString(CultureInfo.InvariantCulture) + " of them",
                    "Only the first " + MaxTopics.ToString(CultureInfo.InvariantCulture) + " topics of a Wolverine transport, by name, are listed."));
                break;
            }

            topics.Add(byName[name].ToTopic(byName));
        }

        string[] groups =
        [
            .. byName.Values
                .Where(static x => x.Listener && !x.DeadLetter && !string.IsNullOrWhiteSpace(x.ConsumerGroup))
                .Select(static x => x.ConsumerGroup!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        return new WolverineTopicRead(topics, groups, notes);
    }

    private static void Add(
        Dictionary<string, Accumulator> byName,
        List<KafkaSourceNote> notes,
        KafkaTransport transport,
        string? serviceName,
        KafkaTopic endpoint,
        string name)
    {
        if (IsOnAnotherCluster(transport, endpoint))
        {
            notes.Add(new KafkaSourceNote("a topic", "it is on another cluster", "The topic '" + name + "' is on another cluster, so it is not listed here."));
            return;
        }

        bool listener = endpoint.IsListener;
        bool sender = endpoint.Subscriptions.Count > 0;

        if (!listener && !sender)
        {
            // An endpoint nothing listens to and nothing is sent to (one made by a lookup, say) is not a topic Wolverine uses.
            return;
        }

        Accumulator accumulator = Get(byName, name);
        accumulator.Listener |= listener;
        accumulator.Sender |= sender;

        if (listener && accumulator.ConsumerGroup is null)
        {
            accumulator.ConsumerGroup = FirstSet(endpoint.ConsumerConfig?.GroupId, transport.ConsumerConfig.GroupId, serviceName);
        }

        if (listener && endpoint.MessageType?.FullName is { } listened)
        {
            accumulator.AddMessageType(listened);
        }

        foreach (Subscription subscription in endpoint.Subscriptions)
        {
            accumulator.AddMessageType(Describe(subscription));
        }

        if (accumulator.Serializer is null && endpoint.DefaultSerializer is { } serializer)
        {
            accumulator.Serializer = serializer.GetType().Name;
        }

        accumulator.CloudEvents |= endpoint.DefaultSerializer is CloudEventsMapper;
        accumulator.CustomMapper |= endpoint.EnvelopeMapper is { } mapper
            && !string.Equals(mapper.GetType().FullName, EnvelopeMapperTypeName, StringComparison.Ordinal);

        accumulator.Partitioner ??= PartitionerOf(endpoint.ProducerConfig ?? transport.ProducerConfig);
    }

    private static Accumulator Get(Dictionary<string, Accumulator> byName, string name)
    {
        if (!byName.TryGetValue(name, out Accumulator? found))
        {
            found = new Accumulator(name);
            byName[name] = found;
        }

        return found;
    }

    private static string Describe(Subscription subscription) =>
        subscription.Scope switch
        {
            RoutingScope.Type or RoutingScope.TypeName => subscription.Match,
            RoutingScope.Namespace => "namespace " + subscription.Match,
            RoutingScope.Assembly => "assembly " + subscription.Match,
            RoutingScope.Implements => "implements " + subscription.Match,
            RoutingScope.All => "all messages",
            _ => subscription.Scope.ToString() + " " + subscription.Match,
        };

    /// <summary>
    /// A topic with its own bootstrap servers that differ from the transport's is on another cluster: listing it here would read the wrong broker.
    /// Read from the topic's own configuration only - never through <c>GetEffectiveConsumerConfig</c>, which writes.
    /// </summary>
    private static bool IsOnAnotherCluster(KafkaTransport transport, KafkaTopic endpoint) =>
        Differs(endpoint.ConsumerConfig?.BootstrapServers, transport.ConsumerConfig.BootstrapServers)
        || Differs(endpoint.ProducerConfig?.BootstrapServers, transport.ProducerConfig.BootstrapServers);

    private static bool Differs(string? own, string? parent) =>
        !string.IsNullOrWhiteSpace(own) && !string.Equals(Normalize(own), Normalize(parent), StringComparison.Ordinal);

    private static string Normalize(string? servers) =>
        string.IsNullOrWhiteSpace(servers) ? string.Empty : string.Concat(servers.Where(static c => !char.IsWhiteSpace(c)));

    private static string? FirstSet(params string?[] values) => values.FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x));

    /// <summary>
    /// The partitioner a transport's producers use, as a claim: <c>consistent_random</c> (librdkafka's default for a keyed record) is CRC32, the two
    /// murmur2 and the two fnv1a variants are theirs, and anything else is no claim. The raw setting is read from the property bag, so a value the
    /// enum does not know cannot throw.
    /// </summary>
    internal static KafkaPartitioner? PartitionerOf(ClientConfig producer)
    {
        string? setting = producer.FirstOrDefault(static x => string.Equals(x.Key, "partitioner", StringComparison.Ordinal)).Value;

        return setting switch
        {
            null or "" or "consistent_random" => KafkaPartitioner.Crc32,
            "murmur2" or "murmur2_random" => KafkaPartitioner.Murmur2,
            "fnv1a" or "fnv1a_random" => KafkaPartitioner.Fnv1a,
            _ => null,
        };
    }

    private sealed class Accumulator(string name)
    {
        private readonly List<string> messageTypes = [];

        public bool Listener { get; set; }

        public bool Sender { get; set; }

        public bool DeadLetter { get; set; }

        public string? ConsumerGroup { get; set; }

        public string? Serializer { get; set; }

        public bool CloudEvents { get; set; }

        public bool CustomMapper { get; set; }

        public KafkaPartitioner? Partitioner { get; set; }

        public void AddMessageType(string text)
        {
            if (messageTypes.Count < MaxMessageTypes && !messageTypes.Contains(text, StringComparer.Ordinal))
            {
                messageTypes.Add(text);
            }
        }

        public KafkaDiscoveredTopic ToTopic(Dictionary<string, Accumulator> all)
        {
            KafkaTopicRole role = DeadLetter
                ? KafkaTopicRole.DeadLetter
                : Listener && Sender ? KafkaTopicRole.ListenerAndSender : Listener ? KafkaTopicRole.Listener : KafkaTopicRole.Sender;

            string description = DeadLetter
                ? "Wolverine dead letters"
                : RetryTierOf(all) is { } source
                    ? "Wolverine retry tier of " + source
                    : role switch
                    {
                        KafkaTopicRole.ListenerAndSender => "Wolverine listener and sender",
                        KafkaTopicRole.Listener => "Wolverine listener",
                        _ => "Wolverine sender",
                    };

            if (CustomMapper)
            {
                description += " · custom envelope mapper: header names may differ";
            }

            return new KafkaDiscoveredTopic(
                name,
                role,
                [.. messageTypes],
                Serializer,
                Listener && !DeadLetter ? ConsumerGroup : null,
                CloudEvents,
                Partitioner,
                WolverineClusterSource.SourceName,
                description);
        }

        /// <summary>The source topic of a retry tier (<c>{source}.retry.{delay}</c>) when that source is itself a topic found here.</summary>
        private string? RetryTierOf(Dictionary<string, Accumulator> all)
        {
            int infix = name.LastIndexOf(RetryInfix, StringComparison.Ordinal);

            if (infix <= 0)
            {
                return null;
            }

            string source = name[..infix];

            return all.ContainsKey(source) ? source : null;
        }
    }
}
