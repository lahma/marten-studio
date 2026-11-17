using System.Collections.Concurrent;

using Confluent.Kafka;

using DataStudio.Kafka.Core.Clusters;
using DataStudio.Kafka.Core.Logging;

using Microsoft.Extensions.Logging;

namespace DataStudio.Kafka.Core.Wire.Confluent;

/// <summary>
/// What the engine's own log and error handlers do (decisions 2.4 and 3.4): librdkafka's lines go to 9656 at Debug and never to the
/// host's stderr, a client error is classified and logged, and a recent authentication failure is remembered so that a call that then
/// times out is reported as what it was.
/// </summary>
internal sealed class ConfluentClientEvents(KafkaEngineLoggers loggers, KafkaLogOnce once, TimeProvider timeProvider)
{
    private static readonly TimeSpan AuthenticationMemory = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Code)> authentication = new(StringComparer.Ordinal);

    /// <summary>Handles a librdkafka log line.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="client">The kind of client, <c>admin</c> or <c>scan</c>.</param>
    /// <param name="message">The line.</param>
    public void OnLog(string cluster, string client, LogMessage message)
    {
        string facility = KafkaLogValue.Cap(message.Facility);
        string text = KafkaLogValue.CapText(message.Message);
        KafkaEngineLog.ClientLog(loggers.Clients, cluster, client, facility, text);
    }

    /// <summary>Handles an error the client reports outside any call.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="client">The kind of client.</param>
    /// <param name="error">The error.</param>
    public void OnError(string cluster, string client, Error error)
    {
        string code = error.Code.ToString();

        if (error.IsFatal)
        {
            KafkaEngineLog.ClientFatalError(loggers.Clients, client, cluster, code);
            return;
        }

        if (ConfluentErrors.Map(error, WireCall.Metadata) is WireError.AuthenticationFailed)
        {
            authentication[cluster] = (timeProvider.GetUtcNow(), code);
            LogLevel level = KafkaLogOnce.WarningOrDebug(once, "auth|" + cluster);
            KafkaEngineLog.AuthenticationFailed(loggers.Clients, level, cluster, code);
            return;
        }

        KafkaEngineLog.ClientError(loggers.Clients, client, cluster, code);
    }

    /// <summary>Records that a token refresh handler of the host threw; only the exception's type name is logged.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="client">The kind of client.</param>
    /// <param name="exception">What the handler threw.</param>
    public void OnTokenRefreshFailed(string cluster, string client, Exception exception)
    {
        string kind = exception.GetType().Name;
        KafkaEngineLog.ClientError(loggers.Clients, client, cluster, kind);
    }

    /// <summary>The authentication failure seen in the last 30 seconds, if any.</summary>
    /// <param name="cluster">The cluster's key.</param>
    public WireError? RecentAuthenticationFailure(string cluster) =>
        authentication.TryGetValue(cluster, out (DateTimeOffset At, string Code) seen)
        && timeProvider.GetUtcNow() - seen.At < AuthenticationMemory
            ? new WireError.AuthenticationFailed(seen.Code)
            : null;

    /// <summary>Tells the log which host settings are replaced for a client of this cluster.</summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="client">The kind of client.</param>
    public Action<string> Overridden(KafkaClusterDefinition cluster, string client)
    {
        ILogger logger = loggers.Clients;
        string key = cluster.Key;
        return setting =>
        {
            string name = KafkaLogValue.Cap(setting);
            KafkaEngineLog.HostSettingOverridden(logger, name, key, client);
        };
    }
}
