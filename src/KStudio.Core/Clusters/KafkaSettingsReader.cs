using System.Globalization;

using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;

namespace KStudio.Core.Clusters;

/// <summary>Which of the engine's clients a setting of the host's reaches.</summary>
internal enum KafkaSettingRole
{
    /// <summary>The scan consumer and the admin client (the configuration was one <c>ClientConfig</c>, or both configurations hold the same value).</summary>
    Both,

    /// <summary>Only the scan consumer: a consumer-only key, or a value that differs from the admin configuration's.</summary>
    ScansOnly,

    /// <summary>Only the admin client, which reads metadata: a value that differs from the consumer configuration's, or a key only it holds.</summary>
    MetadataOnly,
}

/// <summary>One of the host's settings as the page shows it. <see cref="Shown" /> is never the raw value of a secret.</summary>
/// <param name="Key">The configuration key.</param>
/// <param name="Exposure">How the key is classified.</param>
/// <param name="Shown">What the page may display.</param>
/// <param name="Role">Which client uses it.</param>
internal sealed record KafkaSettingRow(string Key, KafkaSettingExposure Exposure, string Shown, KafkaSettingRole Role);

/// <summary>A value the engine's clients set over the host's, or a key they drop (D37).</summary>
/// <param name="Key">The configuration key.</param>
/// <param name="KStudioValue">What the engine's clients use; <c>removed</c> for a key they drop.</param>
/// <param name="Why">The reason, in D37's words.</param>
internal sealed record KafkaSettingOverride(string Key, string KStudioValue, string Why);

/// <summary>A cluster's settings for the page.</summary>
/// <param name="Host">What the host set, ordinal by key.</param>
/// <param name="Overrides">What the engine's clients set over it, by key.</param>
/// <param name="TokenRefreshDeclared">Whether an OAuth token refresh was declared; the delegate itself never leaves the definition.</param>
internal sealed record KafkaClusterSettingsView(
    IReadOnlyList<KafkaSettingRow> Host,
    IReadOnlyList<KafkaSettingOverride> Overrides,
    bool TokenRefreshDeclared);

/// <summary>
/// Reads a cluster's configuration for display (decision 3, spec B.6.2: "the raw configuration never leaves the reader"). The cloned
/// dictionaries are read here and nowhere outside <c>KStudio.Core</c>; every value goes through <see cref="KafkaSettingsCatalog" />, so a
/// secret is only "set" or "not set", a path is never opened and a key nobody classified is withheld.
/// </summary>
/// <param name="settings">The engine options as validated.</param>
internal sealed class KafkaSettingsReader(KafkaEngineSnapshot settings)
{
    private const string UserInfoMask = "•••";

    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["allow.auto.create.topics"] = "a metadata request must never create a topic",
        ["enable.auto.commit"] = "nothing is committed or stored",
        ["enable.auto.offset.store"] = "nothing is committed or stored",
        ["group.id"] = "Confluent's consumer requires one; it is never subscribed and never committed",
        ["group.protocol"] = "one tested path",
        ["client.id"] = "broker metrics and client-id quotas tell the studio apart",
        ["isolation.level"] = "explicit, never inherited",
        ["enable.partition.eof"] = "the engine must know a partition ran out",
        ["auto.offset.reset"] = "an offset deleted during a scan is reported, never skipped",
        ["statistics.interval.ms"] = "no statistics",
        ["enable.metrics.push"] = "the studio's clients never appear in the cluster's client metrics",
        ["queued.max.messages.kbytes"] = "the clients' defaults prefetch 64 MiB per consumer",
        ["fetch.max.bytes"] = "the clients' defaults prefetch 64 MiB per consumer",
        ["max.partition.fetch.bytes"] = "the clients' defaults prefetch 64 MiB per consumer",
        ["message.max.bytes"] = "librdkafka refuses a consumer whose fetch.max.bytes is below message.max.bytes",
    };

    /// <summary>The settings of a cluster for the page.</summary>
    /// <param name="definition">The cluster.</param>
    public KafkaClusterSettingsView Read(KafkaClusterDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        IReadOnlyDictionary<string, string> consumer = definition.ConsumerSettings;
        IReadOnlyDictionary<string, string> admin = definition.AdminSettings;
        List<KafkaSettingRow> rows = [];

        foreach (string key in consumer.Keys.Union(admin.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            bool inConsumer = consumer.TryGetValue(key, out string? scanValue);
            bool inAdmin = admin.TryGetValue(key, out string? metadataValue);

            if (inConsumer && inAdmin)
            {
                if (string.Equals(scanValue, metadataValue, StringComparison.Ordinal))
                {
                    rows.Add(Row(key, scanValue!, KafkaSettingRole.Both));
                }
                else
                {
                    // Two configurations that disagree: each client's value is shown, as the client it is.
                    rows.Add(Row(key, scanValue!, KafkaSettingRole.ScansOnly));
                    rows.Add(Row(key, metadataValue!, KafkaSettingRole.MetadataOnly));
                }
            }
            else
            {
                rows.Add(Row(key, (inConsumer ? scanValue : metadataValue)!, inConsumer ? KafkaSettingRole.ScansOnly : KafkaSettingRole.MetadataOnly));
            }
        }

        return new KafkaClusterSettingsView(rows, Overrides(definition), definition.TokenRefresh is not null);
    }

    private static KafkaSettingRow Row(string key, string value, KafkaSettingRole role)
    {
        KafkaSettingExposure exposure = KafkaSettingsCatalog.Classify(key);

        string shown = exposure switch
        {
            KafkaSettingExposure.Shown => MaskUserInfo(value),
            KafkaSettingExposure.Secret => string.IsNullOrEmpty(value) ? "not set" : "set",
            KafkaSettingExposure.Path => value,
            KafkaSettingExposure.Removed => "removed: KStudio's clients never use it",
            _ => "value withheld: KStudio does not know this setting",
        };

        return new KafkaSettingRow(key, exposure, shown, role);
    }

    /// <summary>A value that is a URL with user info shows <c>•••</c> in its place: <c>https://•••@idp.example</c>.</summary>
    private static string MaskUserInfo(string value)
    {
        int scheme = value.IndexOf("://", StringComparison.Ordinal);

        if (scheme < 0)
        {
            return value;
        }

        // Everything up to the last '@' is masked, however the password is spelled: a '/', '?' or '#' in an unescaped password must not end
        // the user info early and leave its tail on the page. Over-masking a host is the safe side.
        int authorityStart = scheme + 3;
        int at = value.LastIndexOf('@');

        return at < authorityStart ? value : string.Concat(value.AsSpan(0, authorityStart), UserInfoMask, value.AsSpan(at));
    }

    /// <summary>D37's table, read from the configurations the engine builds (neither client is created).</summary>
    private List<KafkaSettingOverride> Overrides(KafkaClusterDefinition definition)
    {
        KafkaScanLimits limits = settings.Limits;
        string prefix = settings.GroupIdPrefix;
        Guid scanId = Guid.Empty;

        IReadOnlyList<KeyValuePair<string, string>> consumer = ConfluentClientSettings.ConsumerEngineSettings(definition, scanId, WireIsolation.ReadCommitted, limits, prefix);
        IReadOnlyList<KeyValuePair<string, string>> admin = ConfluentClientSettings.AdminEngineSettings(definition);
        Dictionary<string, string> adminValues = admin.ToDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal);
        List<KafkaSettingOverride> overrides = [];

        foreach ((string key, string value) in consumer)
        {
            string shown = key switch
            {
                "group.id" => prefix + "{scan id}",
                "isolation.level" => "read_committed, or read_uncommitted when a scan includes uncommitted records",
                _ => value,
            };

            if (adminValues.Remove(key, out string? adminValue) && !string.Equals(adminValue, value, StringComparison.Ordinal))
            {
                shown = string.Create(CultureInfo.InvariantCulture, $"{shown} (scans), {adminValue} (metadata)");
            }

            overrides.Add(new KafkaSettingOverride(key, shown, Reasons.GetValueOrDefault(key, "set by the engine for its own clients")));
        }

        // What only the admin client sets (nothing today, but the table is read, not assumed).
        foreach ((string key, string value) in adminValues)
        {
            overrides.Add(new KafkaSettingOverride(key, value, Reasons.GetValueOrDefault(key, "set by the engine for its own clients")));
        }

        overrides.Add(new KafkaSettingOverride("group.instance.id", "removed", "no static membership"));
        overrides.Add(new KafkaSettingOverride("debug", "removed", "the host's debug contexts are not the studio's"));
        overrides.Add(new KafkaSettingOverride("dotnet.consumer.consume.result.fields", "removed", "every consume-result field is needed"));
        const string interceptor = "a host's interceptor would make the studio's clients produce to the host's monitoring topic";
        const string twoNames = "one setting under two names never reaches the client twice";

        overrides.Add(new KafkaSettingOverride("plugin.library.paths", "removed", interceptor));
        overrides.Add(new KafkaSettingOverride("confluent.monitoring.interceptor.*", "removed", interceptor));
        overrides.Add(new KafkaSettingOverride("fetch.message.max.bytes", "removed", twoNames));
        overrides.Add(new KafkaSettingOverride("auto.commit.enable", "removed", twoNames));

        return overrides;
    }
}
