using KStudio.Core.Wire.Confluent;

namespace KStudio.Core.Clusters;

/// <summary>What the settings page may show of one configuration key (D33's idea, for librdkafka's keys).</summary>
internal enum KafkaSettingExposure
{
    /// <summary>The value as the host set it (a URL's user info masked).</summary>
    Shown,

    /// <summary>Only whether it is set.</summary>
    Secret,

    /// <summary>A path, shown as given; the file is never opened.</summary>
    Path,

    /// <summary>A key the catalogue does not know: its value is not shown.</summary>
    Withheld,

    /// <summary>A key the engine's clients never use.</summary>
    Removed,
}

/// <summary>
/// Classifies every configuration key (decision 3): a catalogue of the keys Confluent.Kafka's <c>ClientConfig</c>, <c>ConsumerConfig</c> and
/// <c>AdminClientConfig</c> declare, in which each key is shown, a secret or a path. A key that is not in it is withheld, so a key a later
/// Confluent adds is never shown until a person has classified it (<c>KafkaSettingsReaderTests</c> fails at the latest tested version
/// until then). The name rule at the end fails closed over every key, known or not.
/// </summary>
internal static class KafkaSettingsCatalog
{
    /// <summary>Keys whose value is a credential or key material. A PEM is not always secret, but it is large and never useful on screen.</summary>
    public static IReadOnlySet<string> Secrets { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "sasl.password",
        "sasl.oauthbearer.client.secret",
        "sasl.oauthbearer.config",
        "sasl.oauthbearer.extensions",
        "ssl.key.password",
        "ssl.keystore.password",
        "ssl.key.pem",
        "ssl.certificate.pem",
        "ssl.ca.pem",
        "https.ca.pem",
        "sasl.oauthbearer.assertion.private.key.passphrase",
        "sasl.oauthbearer.assertion.private.key.pem",
    };

    /// <summary>Keys whose value is a file path, which is shown and never opened.</summary>
    public static IReadOnlySet<string> Paths { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ssl.key.location",
        "ssl.keystore.location",
        "ssl.ca.location",
        "ssl.certificate.location",
        "ssl.crl.location",
        "ssl.engine.location",
        "https.ca.location",
        "sasl.kerberos.keytab",
        "sasl.oauthbearer.assertion.file",
        "sasl.oauthbearer.assertion.private.key.file",
        "sasl.oauthbearer.assertion.jwt.template.file",
    };

    /// <summary>
    /// Every other key <c>ClientConfig</c> and <c>ConsumerConfig</c> declare, at the floor and at the latest tested Confluent.Kafka. The producer-only
    /// keys are not here: the engine's clones drop them before anything is read.
    /// </summary>
    public static IReadOnlySet<string> Shown { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // ClientConfig
        "acks",
        "allow.auto.create.topics",
        "api.version.fallback.ms",
        "api.version.request",
        "api.version.request.timeout.ms",
        "bootstrap.servers",
        "broker.address.family",
        "broker.address.ttl",
        "broker.version.fallback",
        "client.dns.lookup",
        "client.id",
        "client.rack",
        "connections.max.idle.ms",
        "debug",
        "enable.metrics.push",
        "enable.random.seed",
        "enable.sasl.oauthbearer.unsecure.jwt",
        "enable.ssl.certificate.verification",
        "internal.termination.signal",
        "log.connection.close",
        "log.queue",
        "log.thread.name",
        "max.in.flight",
        "message.copy.max.bytes",
        "message.max.bytes",
        "metadata.max.age.ms",
        "metadata.recovery.rebootstrap.trigger.ms",
        "metadata.recovery.strategy",
        "plugin.library.paths",
        "receive.message.max.bytes",
        "reconnect.backoff.max.ms",
        "reconnect.backoff.ms",
        "retry.backoff.max.ms",
        "retry.backoff.ms",
        "sasl.kerberos.kinit.cmd",
        "sasl.kerberos.min.time.before.relogin",
        "sasl.kerberos.principal",
        "sasl.kerberos.service.name",
        "sasl.mechanism",
        "sasl.oauthbearer.assertion.algorithm",
        "sasl.oauthbearer.assertion.claim.aud",
        "sasl.oauthbearer.assertion.claim.exp.seconds",
        "sasl.oauthbearer.assertion.claim.iss",
        "sasl.oauthbearer.assertion.claim.jti.include",
        "sasl.oauthbearer.assertion.claim.nbf.seconds",
        "sasl.oauthbearer.assertion.claim.sub",
        "sasl.oauthbearer.client.id",
        "sasl.oauthbearer.grant.type",
        "sasl.oauthbearer.metadata.authentication.type",
        "sasl.oauthbearer.method",
        "sasl.oauthbearer.scope",
        "sasl.oauthbearer.sub.claim.name",
        "sasl.oauthbearer.token.endpoint.url",
        "sasl.username",
        "security.protocol",
        "socket.connection.setup.timeout.ms",
        "socket.keepalive.enable",
        "socket.max.fails",
        "socket.nagle.disable",
        "socket.receive.buffer.bytes",
        "socket.send.buffer.bytes",
        "socket.timeout.ms",
        "ssl.ca.certificate.stores",
        "ssl.cipher.suites",
        "ssl.curves.list",
        "ssl.endpoint.identification.algorithm",
        "ssl.engine.id",
        "ssl.providers",
        "ssl.sigalgs.list",
        "statistics.interval.ms",
        "topic.blacklist",
        "topic.metadata.propagation.max.ms",
        "topic.metadata.refresh.fast.interval.ms",
        "topic.metadata.refresh.interval.ms",
        "topic.metadata.refresh.sparse",

        // ConsumerConfig
        "auto.commit.interval.ms",
        "auto.offset.reset",
        "check.crcs",
        "coordinator.query.interval.ms",
        "dotnet.consumer.consume.result.fields",
        "enable.auto.commit",
        "enable.auto.offset.store",
        "enable.partition.eof",
        "fetch.error.backoff.ms",
        "fetch.max.bytes",
        "fetch.min.bytes",
        "fetch.queue.backoff.ms",
        "fetch.wait.max.ms",
        "group.id",
        "group.instance.id",
        "group.protocol",
        "group.protocol.type",
        "group.remote.assignor",
        "heartbeat.interval.ms",
        "isolation.level",
        "max.partition.fetch.bytes",
        "max.poll.interval.ms",
        "max.poll.records",
        "partition.assignment.strategy",
        "queued.max.messages.kbytes",
        "queued.min.messages",
        "session.timeout.ms",
        "share.acknowledgement.mode",
    };

    /// <summary>
    /// Parts of a name that make a key a secret whatever the lists say (<c>PastedConnectionString.IsSecret</c>'s rule: any new keyword named
    /// for a password), unless a list made it a path.
    /// </summary>
    private static readonly string[] SecretNameParts = ["password", "secret", "passphrase", ".pem", "keytab", "token", "credential", "private.key"];

    /// <summary>How a key is shown.</summary>
    /// <param name="key">The configuration key.</param>
    public static KafkaSettingExposure Classify(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (ConfluentClientSettings.IsRemoved(key))
        {
            return KafkaSettingExposure.Removed;
        }

        if (Paths.Contains(key))
        {
            return KafkaSettingExposure.Path;
        }

        // The name rule comes after the lists and before the known keys: it fails closed over every key.
        if (Secrets.Contains(key) || SecretNameParts.Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase)))
        {
            return KafkaSettingExposure.Secret;
        }

        return Shown.Contains(key) ? KafkaSettingExposure.Shown : KafkaSettingExposure.Withheld;
    }
}
