using Microsoft.Extensions.Logging;

namespace KStudio.Core.Logging;

/// <summary>
/// The scan engine's log events, 9665 to 9675 (decision 11; 9675 is the payload rules' in M3). They carry cluster keys, topics, partitions, offsets, counts, enum names and
/// exception type names, and never a predicate, a literal, a key, a header, a value, an owner or a user: the audit of a scan is KStudio's.
/// </summary>
internal static partial class KStudioLog
{
    [LoggerMessage(EventId = 9665, Level = LogLevel.Debug, Message = "Scan {ScanId} on {Cluster}: {Topics} topics, {Partitions} partitions, about {Offsets} offsets, {Isolation}")]
    public static partial void ScanStarted(ILogger logger, Guid scanId, string cluster, int topics, int partitions, long offsets, string isolation);

    [LoggerMessage(EventId = 9666, Level = LogLevel.Debug, Message = "Scan {ScanId} on {Cluster} stopped ({Reason}): {Records} records, {Bytes} bytes, {Matches} matches, {Undecidable} undecidable, {ElapsedMilliseconds} ms")]
    public static partial void ScanStopped(
        ILogger logger, Guid scanId, string cluster, string reason, long records, long bytes, long matches, long undecidable, long elapsedMilliseconds);

    [LoggerMessage(EventId = 9667, Level = LogLevel.Debug, Message = "KStudio refused a scan on {Cluster}: {Refusal}")]
    public static partial void ScanRefused(ILogger logger, string cluster, string refusal);

    [LoggerMessage(EventId = 9668, Level = LogLevel.Warning, Message = "Scan {ScanId} on {Cluster} failed: {ExceptionKind}")]
    public static partial void ScanFailed(ILogger logger, Guid scanId, string cluster, string exceptionKind);

    [LoggerMessage(EventId = 9669, Level = LogLevel.Debug, Message = "Scan {ScanId}: {Lost} offsets of {Topic} partition {Partition} were deleted while it read")]
    public static partial void OffsetsDeletedDuringScan(ILogger logger, Guid scanId, long lost, string topic, int partition);

    [LoggerMessage(EventId = 9670, Level = LogLevel.Debug, Message = "Scan {ScanId}: {Topic} partition {Partition} made no progress at offset {Position}")]
    public static partial void PartitionStalled(ILogger logger, Guid scanId, string topic, int partition, long position);

    [LoggerMessage(EventId = 9671, Level = LogLevel.Debug, Message = "Scan {ScanId} on {Cluster}: the group coordinator refused its group (no group ACL)")]
    public static partial void GroupCoordinatorRefused(ILogger logger, Guid scanId, string cluster);

    [LoggerMessage(EventId = 9672, Level = LogLevel.Debug, Message = "Decoder {Decoder} of {Topic} on {Cluster} threw {ExceptionKind}")]
    public static partial void DecoderFailed(ILogger logger, string decoder, string topic, string cluster, string exceptionKind);

    [LoggerMessage(EventId = 9673, Level = LogLevel.Debug, Message = "Scan {ScanId}'s consumer took {ElapsedMilliseconds} ms to close")]
    public static partial void ConsumerCloseSlow(ILogger logger, Guid scanId, long elapsedMilliseconds);

    [LoggerMessage(EventId = 9674, Level = LogLevel.Debug, Message = "KStudio could not sample record sizes of {Topic} on {Cluster}: {Kind}")]
    public static partial void SampleFailed(ILogger logger, string topic, string cluster, string kind);

    [LoggerMessage(EventId = 9675, Level = LogLevel.Debug, Message = "KStudio could not apply a {RuleKind} redaction rule to a value of {Topic} on {Cluster}: {Failure}, {ExceptionKind}")]
    public static partial void PayloadRuleNotApplied(ILogger logger, string ruleKind, string topic, string cluster, string failure, string exceptionKind);
}
