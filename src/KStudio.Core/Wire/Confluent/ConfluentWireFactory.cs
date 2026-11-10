using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;

namespace KStudio.Core.Wire.Confluent;

/// <summary>The production factory: one <see cref="ConfluentWire" /> per cluster, over the shared admin pool.</summary>
internal sealed class ConfluentWireFactory(
    KafkaAdminPool<IAdminClient> pool,
    KafkaEngineOptions options,
    ConfluentClientEvents events,
    KStudioLoggers loggers) : IKafkaWireFactory
{
    /// <inheritdoc />
    public IKafkaWire Create(KafkaClusterDefinition cluster) => new ConfluentWire(cluster, pool, options, events, loggers);

    /// <summary>
    /// Builds the admin client of a cluster: the host's configuration with the engine's overrides, the engine's own log and error
    /// handlers, and the host's OAuth hook when it declared one. Nothing else of the host's is applied (D37).
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="events">The engine's handlers.</param>
    public static IAdminClient BuildAdminClient(KafkaClusterDefinition cluster, ConfluentClientEvents events)
    {
        AdminClientConfig config = ConfluentClientSettings.BuildAdminConfig(cluster, events.Overridden(cluster, "admin"));
        string key = cluster.Key;

        AdminClientBuilder builder = new AdminClientBuilder(config)
            .SetLogHandler((_, message) => events.OnLog(key, "admin", message))
            .SetErrorHandler((_, error) => events.OnError(key, "admin", error));

        if (cluster.TokenRefresh is { } refresh)
        {
            builder.SetOAuthBearerTokenRefreshHandler((client, text) => RefreshToken(refresh, client, text, key, "admin", events));
        }

        return builder.Build();
    }

    /// <summary>Runs the host's token hook; a hook that throws is reported to librdkafka as a failed refresh, so that it retries.</summary>
    /// <param name="refresh">The host's hook.</param>
    /// <param name="client">The engine's client.</param>
    /// <param name="config">The text librdkafka passes.</param>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="kind">The kind of client.</param>
    /// <param name="events">Where the failure is logged.</param>
    internal static void RefreshToken(
        Action<IClient, string> refresh,
        IClient client,
        string config,
        string cluster,
        string kind,
        ConfluentClientEvents events)
    {
        try
        {
            refresh(client, config);
        }
        catch (Exception exception)
        {
            events.OnTokenRefreshFailed(cluster, kind, exception);
            client.OAuthBearerSetTokenFailure("token refresh failed");
        }
    }
}
