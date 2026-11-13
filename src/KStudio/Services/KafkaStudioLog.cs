using Microsoft.Extensions.Logging;

using PostgresStudio.Services;

namespace KStudio.Services;

/// <summary>
/// KStudio's own log events, 9600 to 9649 (the engine's are 9650 to 9699, <c>KStudioLog</c>): the audit and refusal events of the module
/// and its shell side. Source-generated with a pinned id each, declared <c>private</c> and called only through the wrappers below, so
/// that a value a visitor could shape reaches the log only through <see cref="StudioLogValue" /> - control, format and separator
/// characters escaped, a backslash doubled, a name bounded at 256 characters and other text at 4,000 (AGENTS.md hard rule 5).
/// </summary>
/// <remarks>
/// <para>
/// 9600 and 9601 (a search ran, or did not), 9602 and 9603 (a capability or a cluster, topic refused), 9604 (a record opened) are the audit
/// events; 9605 is a record's value shown (<c>ReadPayloads</c>), 9606 an export written, 9607 and 9608 are the live tail's start and end (M5, UI-TAIL) and 9609 an
/// export or tail that did not run. 9610 is the address naming a cluster the visitor may not open (Debug), and 9611 the twin of the studios'
/// 9220. 9612 to 9649 are held for the shell side.
/// The module never logs 9200 or 9201: their templates name a store, a database and a tenant.
/// </para>
/// <para>
/// A predicate reaches 9600 and 9601 only from the engine's audit printer, which writes every literal on a redacted field as
/// <c>&lt;redacted&gt;</c>; the visitor's typed text never does. <c>TenantId</c> is written <c>(none)</c> when the cluster entry has none.
/// </para>
/// </remarks>
internal static partial class KafkaStudioLog
{
    private const string NoTenant = "(none)";

    /// <summary>9600: a search ran.</summary>
    public static void SearchRan(
        this ILogger logger,
        string user,
        string topics,
        string cluster,
        string? tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        long records,
        long bytes,
        string stopReason,
        string predicate)
    {
        string tenant = StudioLogValue.Cap(tenantId ?? NoTenant)!;
        (user, topics, cluster, stopReason, predicate) =
            (StudioLogValue.Cap(user)!, StudioLogValue.Cap(topics)!, StudioLogValue.Cap(cluster)!, StudioLogValue.Cap(stopReason)!, StudioLogValue.CapText(predicate)!);

        SearchRanCore(logger, user, topics, cluster, tenant, from, to, records, bytes, stopReason, predicate);
    }

    /// <summary>9601: a search did not run.</summary>
    public static void SearchNotRun(this ILogger logger, string user, string topics, string cluster, string? tenantId, string reason, string predicate)
    {
        string tenant = StudioLogValue.Cap(tenantId ?? NoTenant)!;
        (user, topics, cluster, reason, predicate) =
            (StudioLogValue.Cap(user)!, StudioLogValue.Cap(topics)!, StudioLogValue.Cap(cluster)!, StudioLogValue.CapText(reason.TrimEnd('.'))!, StudioLogValue.CapText(predicate)!);

        SearchNotRunCore(logger, user, topics, cluster, tenant, reason, predicate);
    }

    /// <summary>9602: a capability was refused.</summary>
    public static void CapabilityRefused(this ILogger logger, string user, string capability, string option)
    {
        (user, capability, option) = (StudioLogValue.Cap(user)!, StudioLogValue.Cap(capability)!, StudioLogValue.Cap(option)!);

        CapabilityRefusedCore(logger, user, capability, option);
    }

    /// <summary>9603: a cluster or topic was refused by a policy.</summary>
    public static void ClusterRefused(this ILogger logger, string user, string cluster, string? tenantId, string? topic, string policy)
    {
        string tenant = StudioLogValue.Cap(tenantId ?? NoTenant)!;
        string named = StudioLogValue.Cap(topic ?? "(cluster)")!;
        (user, cluster, policy) = (StudioLogValue.Cap(user)!, StudioLogValue.Cap(cluster)!, StudioLogValue.Cap(policy)!);

        ClusterRefusedCore(logger, user, cluster, tenant, named, policy);
    }

    /// <summary>9604: a record was opened.</summary>
    public static void RecordOpened(this ILogger logger, string user, string topic, int partition, long offset, string cluster, string? tenantId)
    {
        string tenant = StudioLogValue.Cap(tenantId ?? NoTenant)!;
        (user, topic, cluster) = (StudioLogValue.Cap(user)!, StudioLogValue.Cap(topic)!, StudioLogValue.Cap(cluster)!);

        RecordOpenedCore(logger, user, topic, partition, offset, cluster, tenant);
    }

    /// <summary>9605: a record's value was shown.</summary>
    public static void RecordValueRead(this ILogger logger, string user, string topic, int partition, long offset, string cluster, string? tenantId)
    {
        string tenant = StudioLogValue.Cap(tenantId ?? NoTenant)!;
        (user, topic, cluster) = (StudioLogValue.Cap(user)!, StudioLogValue.Cap(topic)!, StudioLogValue.Cap(cluster)!);

        RecordValueReadCore(logger, user, topic, partition, offset, cluster, tenant);
    }

    /// <summary>9606: records were exported to a file.</summary>
    public static void RecordsExported(
        this ILogger logger,
        string user,
        long records,
        string topics,
        string cluster,
        string? tenantId,
        string format,
        long bytes,
        bool values,
        string predicate)
    {
        string tenant = StudioLogValue.Cap(tenantId ?? NoTenant)!;
        (user, topics, cluster, format, predicate) =
            (StudioLogValue.Cap(user)!, StudioLogValue.Cap(topics)!, StudioLogValue.Cap(cluster)!, StudioLogValue.Cap(format)!, StudioLogValue.CapText(predicate)!);

        RecordsExportedCore(logger, user, records, topics, cluster, tenant, format, bytes, values ? "; values included" : string.Empty, predicate);
    }

    /// <summary>9607: a live tail started (its first segment). The tail's one audit entry is written when it ends (9608).</summary>
    public static void TailStarted(this ILogger logger, string user, string topics, string cluster, string? tenantId, string predicate)
    {
        string tenant = StudioLogValue.Cap(tenantId ?? NoTenant)!;
        (user, topics, cluster, predicate) = (StudioLogValue.Cap(user)!, StudioLogValue.Cap(topics)!, StudioLogValue.Cap(cluster)!, StudioLogValue.CapText(predicate)!);

        TailStartedCore(logger, user, topics, cluster, tenant, predicate);
    }

    /// <summary>9608: a live tail ended, once per tail whatever the number of segments it read in.</summary>
    public static void TailEnded(
        this ILogger logger,
        string user,
        string topics,
        string cluster,
        string? tenantId,
        string elapsed,
        int segments,
        long records,
        long bytes,
        long matches,
        string stopReason,
        string predicate)
    {
        string tenant = StudioLogValue.Cap(tenantId ?? NoTenant)!;
        (user, topics, cluster, elapsed, stopReason, predicate) =
            (StudioLogValue.Cap(user)!, StudioLogValue.Cap(topics)!, StudioLogValue.Cap(cluster)!, StudioLogValue.Cap(elapsed)!, StudioLogValue.Cap(stopReason)!, StudioLogValue.CapText(predicate)!);

        TailEndedCore(logger, user, topics, cluster, tenant, elapsed, segments, records, bytes, matches, stopReason, predicate);
    }

    /// <summary>9609: an export or a live tail did not run.</summary>
    public static void ActionNotRun(this ILogger logger, string user, string action, string topics, string cluster, string? tenantId, string reason, string predicate)
    {
        string tenant = StudioLogValue.Cap(tenantId ?? NoTenant)!;
        (user, action, topics, cluster, reason, predicate) =
            (StudioLogValue.Cap(user)!, StudioLogValue.Cap(action)!, StudioLogValue.Cap(topics)!, StudioLogValue.Cap(cluster)!, StudioLogValue.CapText(reason.TrimEnd('.'))!, StudioLogValue.CapText(predicate)!);

        ActionNotRunCore(logger, user, action, topics, cluster, tenant, reason, predicate);
    }

    /// <summary>9610: the address named a cluster that is unknown or refused, and another was opened.</summary>
    public static void ClusterAddressIgnored(this ILogger logger, string cluster, string opened)
    {
        (cluster, opened) = (StudioLogValue.Cap(cluster)!, StudioLogValue.Cap(opened)!);

        ClusterAddressIgnoredCore(logger, cluster, opened);
    }

    /// <summary>9611: a page failed to handle a live update failure; the twin of 9220.</summary>
    public static void LiveUpdateHandlerFailed(this ILogger logger, LogLevel level, Exception exception) =>
        LiveUpdateHandlerFailedCore(logger, level, exception);

    [LoggerMessage(
        EventId = 9600,
        Level = LogLevel.Information,
        Message = "KStudio user {User} searched topics {Topics} of cluster {Cluster}, tenant {TenantId}, from {From} to {To}: {Records} records and {Bytes} bytes read, stopped by {StopReason}. Predicate: {Predicate}")]
    private static partial void SearchRanCore(
        ILogger logger,
        string user,
        string topics,
        string cluster,
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        long records,
        long bytes,
        string stopReason,
        string predicate);

    [LoggerMessage(
        EventId = 9601,
        Level = LogLevel.Information,
        Message = "KStudio user {User}'s search of topics {Topics} of cluster {Cluster}, tenant {TenantId} did not run: {Reason}. Predicate: {Predicate}")]
    private static partial void SearchNotRunCore(ILogger logger, string user, string topics, string cluster, string tenantId, string reason, string predicate);

    [LoggerMessage(
        EventId = 9602,
        Level = LogLevel.Warning,
        Message = "KStudio user {User} was refused capability {Capability}: {Option} would have to be set")]
    private static partial void CapabilityRefusedCore(ILogger logger, string user, string capability, string option);

    [LoggerMessage(
        EventId = 9603,
        Level = LogLevel.Warning,
        Message = "KStudio user {User} was refused cluster {Cluster}, tenant {TenantId}, topic {Topic} by policy {Policy}")]
    private static partial void ClusterRefusedCore(ILogger logger, string user, string cluster, string tenantId, string topic, string policy);

    [LoggerMessage(
        EventId = 9604,
        Level = LogLevel.Information,
        Message = "KStudio user {User} opened record {Topic}/{Partition}/{Offset} of cluster {Cluster}, tenant {TenantId}")]
    private static partial void RecordOpenedCore(ILogger logger, string user, string topic, int partition, long offset, string cluster, string tenantId);

    [LoggerMessage(
        EventId = 9605,
        Level = LogLevel.Information,
        Message = "KStudio user {User} read the value of record {Topic}/{Partition}/{Offset} of cluster {Cluster}, tenant {TenantId}")]
    private static partial void RecordValueReadCore(ILogger logger, string user, string topic, int partition, long offset, string cluster, string tenantId);

    [LoggerMessage(
        EventId = 9606,
        Level = LogLevel.Information,
        Message = "KStudio user {User} exported {Records} records of topics {Topics} of cluster {Cluster}, tenant {TenantId} as {Format}: {Bytes} bytes{Values}. Predicate: {Predicate}")]
    private static partial void RecordsExportedCore(
        ILogger logger,
        string user,
        long records,
        string topics,
        string cluster,
        string tenantId,
        string format,
        long bytes,
        string values,
        string predicate);

    [LoggerMessage(
        EventId = 9607,
        Level = LogLevel.Information,
        Message = "KStudio user {User} started a live tail of topics {Topics} of cluster {Cluster}, tenant {TenantId}. Predicate: {Predicate}")]
    private static partial void TailStartedCore(ILogger logger, string user, string topics, string cluster, string tenantId, string predicate);

    [LoggerMessage(
        EventId = 9608,
        Level = LogLevel.Information,
        Message = "KStudio user {User} tailed topics {Topics} of cluster {Cluster}, tenant {TenantId} for {Elapsed} in {Segments} segments: {Records} records and {Bytes} bytes read, {Matches} matches, ended by {StopReason}. Predicate: {Predicate}")]
    private static partial void TailEndedCore(
        ILogger logger,
        string user,
        string topics,
        string cluster,
        string tenantId,
        string elapsed,
        int segments,
        long records,
        long bytes,
        long matches,
        string stopReason,
        string predicate);

    [LoggerMessage(
        EventId = 9609,
        Level = LogLevel.Information,
        Message = "KStudio user {User}'s {Action} of topics {Topics} of cluster {Cluster}, tenant {TenantId} did not run: {Reason}. Predicate: {Predicate}")]
    private static partial void ActionNotRunCore(ILogger logger, string user, string action, string topics, string cluster, string tenantId, string reason, string predicate);

    [LoggerMessage(
        EventId = 9610,
        Level = LogLevel.Debug,
        Message = "KStudio ignored the cluster {Cluster} the address named, and opened {Opened}")]
    private static partial void ClusterAddressIgnoredCore(ILogger logger, string cluster, string opened);

    [LoggerMessage(EventId = 9611, Message = "A KStudio page failed to handle a live update failure")]
    private static partial void LiveUpdateHandlerFailedCore(ILogger logger, LogLevel level, Exception exception);
}
