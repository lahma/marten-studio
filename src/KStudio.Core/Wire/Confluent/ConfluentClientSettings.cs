using System.Globalization;
using System.Text;

using Confluent.Kafka;

using KStudio.Core.Clusters;

namespace KStudio.Core.Wire.Confluent;

/// <summary>
/// The overrides every client the engine builds gets (AGENTS.md D37): the host's configuration, cloned, with these values written
/// last. Nothing is written to the host's instance, nothing is committed or stored, no group is joined, no topic is created.
/// </summary>
/// <remarks>
/// The builders return the configuration before any client exists, so <c>ConfluentOverridesTests</c> asserts the table key by key.
/// Each host key that is replaced or dropped is reported by name through <c>overridden</c>, never by value.
/// </remarks>
internal static class ConfluentClientSettings
{
    private const int MaxClientIdLength = 255;
    private const int ReceiveMessageMaxBytesDefault = 100_000_000;
    private const int ReceiveMessageOverhead = 512;
    private const int LibrdkafkaPartitionFetchBytes = 1024 * 1024;

    /// <summary>
    /// The names a key can also be written as (<c>CONFIGURATION.md</c> at v2.14.0, "Alias for"): the host's spelling of an overridden key
    /// is dropped with it, so that two names for one setting never both reach the client.
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["max.partition.fetch.bytes"] = ["fetch.message.max.bytes"],
        ["enable.auto.commit"] = ["auto.commit.enable"],
    };

    /// <summary>
    /// Keys that load a host's code into librdkafka. A host's interceptor (Confluent's monitoring interceptor, for one) would make the
    /// studio's clients produce to the host's monitoring topic (D37), so these are never copied.
    /// </summary>
    private static readonly string[] PluginKeys = ["plugin.library.paths"];

    private static readonly string[] PluginPrefixes = ["confluent.monitoring.interceptor."];

    /// <summary>The keys a scan consumer's configuration drops outright: no static membership, no host debug contexts, and every consume-result field is needed.</summary>
    public static IReadOnlyList<string> ConsumerRemoved { get; } = ["group.instance.id", "debug", "dotnet.consumer.consume.result.fields"];

    /// <summary>The keys the admin client's configuration drops outright.</summary>
    public static IReadOnlyList<string> AdminRemoved { get; } = ["debug"];

    /// <summary>
    /// Whether the engine never gives <paramref name="key" /> to a client of its own, whatever the host set: a plugin, a key a client of
    /// either role drops, or the second name of a setting the engine sets (D37). The settings page says "removed" for these.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    public static bool IsRemoved(string key) =>
        IsPlugin(key)
        || ConsumerRemoved.Contains(key, StringComparer.Ordinal)
        || AdminRemoved.Contains(key, StringComparer.Ordinal)
        || Aliases.Values.Any(names => names.Contains(key, StringComparer.Ordinal));

    /// <summary>
    /// The configuration of the offsets reader, the per-cluster consumer that only gives the group-offsets read a handle with a consumer
    /// group (librdkafka #5040): the scan consumer's table with a small queue and fetch size, since it never fetches, and a group id of its own.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="readerId">The reader; its id ends the group id.</param>
    /// <param name="groupIdPrefix">The group id's prefix.</param>
    /// <param name="overridden">Told the name of each host key that was replaced or dropped.</param>
    public static ConsumerConfig BuildOffsetsReaderConfig(
        KafkaClusterDefinition cluster,
        Guid readerId,
        string groupIdPrefix,
        Action<string>? overridden = null) =>
        BuildConsumerConfig(
            cluster,
            readerId,
            WireIsolation.ReadCommitted,
            new KafkaScanLimits { QueueKilobytes = 1024, FetchBytes = LibrdkafkaPartitionFetchBytes },
            groupIdPrefix,
            overridden);

    /// <summary>The scan consumer's configuration (decision 2.4).</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="scanId">The scan; its id ends the group id.</param>
    /// <param name="isolation">The isolation level.</param>
    /// <param name="limits">Where the queue and fetch sizes come from.</param>
    /// <param name="groupIdPrefix">The group id's prefix.</param>
    /// <param name="overridden">Told the name of each host key that was replaced or dropped.</param>
    public static ConsumerConfig BuildConsumerConfig(
        KafkaClusterDefinition cluster,
        Guid scanId,
        WireIsolation isolation,
        KafkaScanLimits limits,
        string groupIdPrefix,
        Action<string>? overridden = null) =>
        new(Compose(cluster.ConsumerSettings, ConsumerEngineSettings(cluster, scanId, isolation, limits, groupIdPrefix), ConsumerRemoved, overridden));

    /// <summary>The values the engine writes last into a scan consumer's configuration, in the order it writes them.</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="scanId">The scan; its id ends the group id.</param>
    /// <param name="isolation">The isolation level.</param>
    /// <param name="limits">Where the queue and fetch sizes come from.</param>
    /// <param name="groupIdPrefix">The group id's prefix.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> ConsumerEngineSettings(
        KafkaClusterDefinition cluster,
        Guid scanId,
        WireIsolation isolation,
        KafkaScanLimits limits,
        string groupIdPrefix)
    {
        IReadOnlyDictionary<string, string> host = cluster.ConsumerSettings;

        // The ceiling on one fetch: librdkafka needs receive.message.max.bytes >= fetch.max.bytes + 512.
        int receiveMax = IntOf(host, "receive.message.max.bytes", ReceiveMessageMaxBytesDefault);
        int fetchMax = Math.Max(1, Math.Min(limits.FetchBytes, receiveMax - ReceiveMessageOverhead));

        List<KeyValuePair<string, string>> engine =
        [
            new("allow.auto.create.topics", "false"),
            new("enable.auto.commit", "false"),
            new("enable.auto.offset.store", "false"),
            new("group.id", groupIdPrefix + scanId.ToString("N", CultureInfo.InvariantCulture)),
            new("group.protocol", "classic"),
            new("client.id", ClientId(host)),
            new("isolation.level", isolation == WireIsolation.ReadCommitted ? "read_committed" : "read_uncommitted"),
            new("enable.partition.eof", "true"),
            new("auto.offset.reset", "error"),
            new("statistics.interval.ms", "0"),
            new("enable.metrics.push", "false"),
            new("queued.max.messages.kbytes", limits.QueueKilobytes.ToString(CultureInfo.InvariantCulture)),
            new("fetch.max.bytes", fetchMax.ToString(CultureInfo.InvariantCulture)),
            new("max.partition.fetch.bytes", Math.Min(LibrdkafkaPartitionFetchBytes, fetchMax).ToString(CultureInfo.InvariantCulture)),
        ];

        // librdkafka refuses a consumer whose fetch.max.bytes is below message.max.bytes (SPIKE S3d), so a host that raised the latter
        // for its producers is clamped here.
        if (IntOf(host, "message.max.bytes", 1_000_000) > fetchMax)
        {
            engine.Add(new("message.max.bytes", fetchMax.ToString(CultureInfo.InvariantCulture)));
        }

        return engine;
    }

    /// <summary>The admin client's configuration, one per cluster (decision 2.4).</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="overridden">Told the name of each host key that was replaced or dropped.</param>
    public static AdminClientConfig BuildAdminConfig(KafkaClusterDefinition cluster, Action<string>? overridden = null) =>
        new(Compose(cluster.AdminSettings, AdminEngineSettings(cluster), AdminRemoved, overridden));

    /// <summary>The values the engine writes last into the admin client's configuration.</summary>
    /// <param name="cluster">The cluster.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> AdminEngineSettings(KafkaClusterDefinition cluster) =>
    [
        // A default admin client creates a topic on a single-topic metadata request when the principal may (SPIKE S1.6).
        new("allow.auto.create.topics", "false"),
        new("client.id", ClientId(cluster.AdminSettings)),
        new("statistics.interval.ms", "0"),
        new("enable.metrics.push", "false"),
    ];

    /// <summary>
    /// <c>{host client.id, or "rdkafka"}.kstudio</c>, each character outside <c>[A-Za-z0-9._-]</c> replaced by <c>-</c>, at most 255
    /// characters, so that broker metrics and client-id quotas tell the studio apart.
    /// </summary>
    /// <param name="settings">The clone, which may hold the host's <c>client.id</c>.</param>
    public static string ClientId(IReadOnlyDictionary<string, string> settings)
    {
        string host = settings.TryGetValue("client.id", out string? value) && !string.IsNullOrWhiteSpace(value) ? value : "rdkafka";
        StringBuilder id = new(host.Length + 8);

        foreach (char c in host + ".kstudio")
        {
            id.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        return id.Length > MaxClientIdLength ? id.ToString(id.Length - MaxClientIdLength, MaxClientIdLength) : id.ToString();
    }

    /// <summary>
    /// The host's keys minus the engine's keys, their aliases, the keys to remove and the host's plugins, then the engine's keys. A new
    /// dictionary is built rather than a key replaced in a clone, so that no host spelling of a setting can come after the engine's value
    /// (a dictionary reuses a freed slot, and librdkafka applies its properties in order).
    /// </summary>
    private static Dictionary<string, string> Compose(
        IReadOnlyDictionary<string, string> host,
        IReadOnlyList<KeyValuePair<string, string>> engine,
        IReadOnlyCollection<string> remove,
        Action<string>? overridden)
    {
        Dictionary<string, string> engineValues = new(StringComparer.Ordinal);
        HashSet<string> aliases = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> pair in engine)
        {
            engineValues[pair.Key] = pair.Value;

            if (Aliases.TryGetValue(pair.Key, out string[]? names))
            {
                aliases.UnionWith(names);
            }
        }

        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> pair in host)
        {
            if (engineValues.TryGetValue(pair.Key, out string? value))
            {
                // Told only when the host asked for something else: the same value is not an override.
                if (!string.Equals(value, pair.Value, StringComparison.Ordinal))
                {
                    overridden?.Invoke(pair.Key);
                }
            }
            else if (aliases.Contains(pair.Key) || IsPlugin(pair.Key) || remove.Contains(pair.Key))
            {
                overridden?.Invoke(pair.Key);
            }
            else
            {
                result[pair.Key] = pair.Value;
            }
        }

        foreach (KeyValuePair<string, string> pair in engine)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static bool IsPlugin(string key) =>
        PluginKeys.Contains(key, StringComparer.Ordinal) || PluginPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    private static int IntOf(IReadOnlyDictionary<string, string> settings, string key, int fallback) =>
        settings.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
}
