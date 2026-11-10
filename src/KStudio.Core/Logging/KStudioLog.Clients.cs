using Microsoft.Extensions.Logging;

namespace KStudio.Core.Logging;

/// <summary>
/// The engine's log events, 9650 to 9699 (decision 11). Engine events carry cluster keys, topic names, partitions, offsets, counts,
/// error code names, exception type names and librdkafka's own text, each string through <see cref="KafkaLogValue" />. They never carry
/// a predicate, a literal, a key, a header, a value, an owner or a user.
/// </summary>
internal static partial class KStudioLog
{
    [LoggerMessage(EventId = 9650, Level = LogLevel.Debug, Message = "KStudio created the admin client of cluster {Cluster}")]
    public static partial void AdminClientCreated(ILogger logger, string cluster);

    [LoggerMessage(EventId = 9651, Level = LogLevel.Debug, Message = "KStudio disposed the admin client of cluster {Cluster} after {IdleSeconds} s idle")]
    public static partial void AdminClientDisposed(ILogger logger, string cluster, int idleSeconds);

    /// <summary>9655: logged at Warning the first time within ten minutes and at Debug afterwards (<c>KafkaLogOnce.WarningThenDebug</c>).</summary>
    [LoggerMessage(EventId = 9655, Message = "Kafka cluster {Cluster} refused KStudio's authentication: {Code}")]
    public static partial void AuthenticationFailed(ILogger logger, LogLevel level, string cluster, string code);

    [LoggerMessage(EventId = 9656, Level = LogLevel.Debug, Message = "librdkafka ({Cluster}, {Client}) {Facility}: {Text}")]
    public static partial void ClientLog(ILogger logger, string cluster, string client, string facility, string text);

    [LoggerMessage(EventId = 9657, Level = LogLevel.Debug, Message = "Kafka client {Client} of cluster {Cluster} reported {Code}")]
    public static partial void ClientError(ILogger logger, string client, string cluster, string code);

    [LoggerMessage(EventId = 9658, Level = LogLevel.Warning, Message = "Kafka client {Client} of cluster {Cluster} failed fatally: {Code}")]
    public static partial void ClientFatalError(ILogger logger, string client, string cluster, string code);

    [LoggerMessage(EventId = 9661, Level = LogLevel.Debug, Message = "KStudio overrides {Setting} of cluster {Cluster} for its {Client} client")]
    public static partial void HostSettingOverridden(ILogger logger, string setting, string cluster, string client);

    [LoggerMessage(EventId = 9664, Level = LogLevel.Debug, Message = "KStudio could not dispose the admin client of cluster {Cluster}")]
    public static partial void AdminClientDisposeFailed(ILogger logger, Exception exception, string cluster);

    [LoggerMessage(EventId = 9662, Level = LogLevel.Debug, Message = "KStudio is stopping {Count} scans")]
    public static partial void ShutdownStarted(ILogger logger, int count);

    [LoggerMessage(EventId = 9663, Level = LogLevel.Warning, Message = "{Count} KStudio scans did not stop within 10 s of shutdown")]
    public static partial void ShutdownTimedOut(ILogger logger, int count);
}
