using Microsoft.Extensions.Logging;

namespace KStudio.Core.Logging;

/// <summary>
/// The events of the clusters a source contributes (D43), 9677 and 9678. They carry a source's name and general words, and never a tenant id, a
/// topic, a key, a configuration value or anything a visitor could shape: the sentence with the name is on the Settings page.
/// </summary>
internal static partial class KStudioLog
{
    /// <summary>
    /// 9677: a cluster, a tenant or a topic a source found was not listed. Logged at Warning the first time within ten minutes for the same
    /// source, item and reason and at Debug afterwards (<c>KafkaLogOnce.WarningOrDebug</c>).
    /// </summary>
    [LoggerMessage(EventId = 9677, Message = "KStudio did not list {What} from {Source}: {Reason}")]
    public static partial void ClusterSourceItemNotListed(ILogger logger, LogLevel level, string what, string source, string reason);

    /// <summary>9678: the link from a studio's record to a search for it could not read the clusters, so no link was drawn.</summary>
    [LoggerMessage(EventId = 9678, Level = LogLevel.Debug, Message = "KStudio's Find in Kafka link could not read the clusters: {ExceptionKind}")]
    public static partial void FindLinkClustersUnreadable(ILogger logger, string exceptionKind);
}
