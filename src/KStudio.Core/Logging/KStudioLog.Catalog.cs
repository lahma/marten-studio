using Microsoft.Extensions.Logging;

namespace KStudio.Core.Logging;

internal static partial class KStudioLog
{
    [LoggerMessage(EventId = 9652, Level = LogLevel.Debug, Message = "Kafka cluster {Cluster} refused {Call}: needs {Operation} on {ResourceType} {Resource}")]
    public static partial void AccessRefused(ILogger logger, string cluster, string call, string operation, string resourceType, string resource);

    [LoggerMessage(EventId = 9653, Level = LogLevel.Debug, Message = "Kafka cluster {Cluster} does not support {Api}")]
    public static partial void ApiUnsupported(ILogger logger, string cluster, string api);

    /// <summary>9654: logged at Warning the first time within ten minutes and at Debug afterwards (<c>KafkaLogOnce.WarningThenDebug</c>).</summary>
    [LoggerMessage(EventId = 9654, Message = "KStudio could not reach Kafka cluster {Cluster} for {Call}: {Code}")]
    public static partial void ClusterUnavailable(ILogger logger, LogLevel level, string cluster, string call, string code);

    [LoggerMessage(EventId = 9659, Level = LogLevel.Debug, Message = "KStudio could not read {Call} of cluster {Cluster}")]
    public static partial void CatalogReadFailed(ILogger logger, Exception exception, string call, string cluster);

    [LoggerMessage(EventId = 9660, Level = LogLevel.Debug, Message = "Partition {Partition} of {Topic} on {Cluster} starts at offset {LogStart}: earlier records are gone")]
    public static partial void RetentionGap(ILogger logger, int partition, string topic, string cluster, long logStart);
}
