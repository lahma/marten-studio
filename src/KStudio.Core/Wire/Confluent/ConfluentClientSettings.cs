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
/// Each host key that is replaced is reported by name through <c>overridden</c>, never by value.
/// </remarks>
internal static class ConfluentClientSettings
{
    private const int MaxClientIdLength = 255;
    private const int ReceiveMessageMaxBytesDefault = 100_000_000;
    private const int ReceiveMessageOverhead = 512;
    private const int LibrdkafkaPartitionFetchBytes = 1024 * 1024;

    /// <summary>The scan consumer's configuration (decision 2.4).</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="scanId">The scan; its id ends the group id.</param>
    /// <param name="isolation">The isolation level.</param>
    /// <param name="limits">Where the queue and fetch sizes come from.</param>
    /// <param name="groupIdPrefix">The group id's prefix.</param>
    /// <param name="overridden">Told the name of each host key that was replaced.</param>
    public static ConsumerConfig BuildConsumerConfig(
        KafkaClusterDefinition cluster,
        Guid scanId,
        WireIsolation isolation,
        KafkaScanLimits limits,
        string groupIdPrefix,
        Action<string>? overridden = null)
    {
        Dictionary<string, string> settings = new(cluster.ConsumerSettings, StringComparer.Ordinal);

        void Set(string key, string value)
        {
            if (settings.TryGetValue(key, out string? existing) && !string.Equals(existing, value, StringComparison.Ordinal))
            {
                overridden?.Invoke(key);
            }

            settings[key] = value;
        }

        void Remove(string key)
        {
            if (settings.Remove(key))
            {
                overridden?.Invoke(key);
            }
        }

        // The ceiling on one fetch: librdkafka needs receive.message.max.bytes >= fetch.max.bytes + 512.
        int receiveMax = IntOf(settings, "receive.message.max.bytes", ReceiveMessageMaxBytesDefault);
        int fetchMax = Math.Max(1, Math.Min(limits.FetchBytes, receiveMax - ReceiveMessageOverhead));

        Set("allow.auto.create.topics", "false");
        Set("enable.auto.commit", "false");
        Set("enable.auto.offset.store", "false");
        Set("group.id", groupIdPrefix + scanId.ToString("N", CultureInfo.InvariantCulture));
        Remove("group.instance.id");
        Set("group.protocol", "classic");
        Set("client.id", ClientId(settings));
        Set("isolation.level", isolation == WireIsolation.ReadCommitted ? "read_committed" : "read_uncommitted");
        Set("enable.partition.eof", "true");
        Set("auto.offset.reset", "error");
        Set("statistics.interval.ms", "0");
        Set("enable.metrics.push", "false");
        Set("queued.max.messages.kbytes", limits.QueueKilobytes.ToString(CultureInfo.InvariantCulture));
        Set("fetch.max.bytes", fetchMax.ToString(CultureInfo.InvariantCulture));
        Set("max.partition.fetch.bytes", Math.Min(LibrdkafkaPartitionFetchBytes, fetchMax).ToString(CultureInfo.InvariantCulture));

        // librdkafka refuses a consumer whose fetch.max.bytes is below message.max.bytes (SPIKE S3d), so a host that raised the latter
        // for its producers is clamped here.
        if (IntOf(settings, "message.max.bytes", 1_000_000) > fetchMax)
        {
            Set("message.max.bytes", fetchMax.ToString(CultureInfo.InvariantCulture));
        }

        Remove("debug");
        Remove("dotnet.consumer.consume.result.fields");

        return new ConsumerConfig(settings);
    }

    /// <summary>The admin client's configuration, one per cluster (decision 2.4).</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="overridden">Told the name of each host key that was replaced.</param>
    public static AdminClientConfig BuildAdminConfig(KafkaClusterDefinition cluster, Action<string>? overridden = null)
    {
        Dictionary<string, string> settings = new(cluster.AdminSettings, StringComparer.Ordinal);

        void Set(string key, string value)
        {
            if (settings.TryGetValue(key, out string? existing) && !string.Equals(existing, value, StringComparison.Ordinal))
            {
                overridden?.Invoke(key);
            }

            settings[key] = value;
        }

        // A default admin client creates a topic on a single-topic metadata request when the principal may (SPIKE S1.6).
        Set("allow.auto.create.topics", "false");
        Set("client.id", ClientId(settings));
        Set("statistics.interval.ms", "0");
        Set("enable.metrics.push", "false");

        if (settings.Remove("debug"))
        {
            overridden?.Invoke("debug");
        }

        return new AdminClientConfig(settings);
    }

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

    private static int IntOf(Dictionary<string, string> settings, string key, int fallback) =>
        settings.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
}
