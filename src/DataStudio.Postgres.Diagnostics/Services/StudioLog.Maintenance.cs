using Microsoft.Extensions.Logging;

namespace DataStudio.Postgres.Services;

// W5-V1's events, 9420-9429: the Maintenance page and its bloat measure. In a file of their own so that the wave-5
// packets' event blocks never meet in one file; the class's own documentation is StudioLog.cs's.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// Warning: a bloat measure a visitor pressed failed for a reason nobody chose - a connection failure, an unexpected
    /// SQLSTATE - after its gates and its preconditions passed. One line per press, never polled: the page shows the failure,
    /// and the audit entry records it.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9420, Level = LogLevel.Warning, Message = "Data Studio could not measure the bloat of a table in source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void BloatMeasureFailed(this ILogger logger, Exception exception, string storeKey, string databaseId, string? sqlState);

    /// <remarks>
    /// <para>
    /// Debug: a bloat measure was refused for an expected reason - <c>pgstattuple</c> not installed, the role without
    /// <c>pg_stat_scan_tables</c>, a table that is not one to measure, another measure running on the database, its
    /// <c>statement_timeout</c>. The page says it in words; this is for somebody who turns Debug on to ask why.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9421, Level = LogLevel.Debug, Message = "Data Studio did not measure the bloat of a table in source {StoreKey}, database {DatabaseId}: {Reason}")]
    public static partial void BloatMeasureRefused(this ILogger logger, string storeKey, string databaseId, string reason);
}
