using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-V1's events, 9420-9429, in Postgres Studio's words: the same ids, levels and properties as StudioLog.Maintenance.cs,
// whose dispatchers choose between the two. The class's own documentation is PostgresStudioLog.cs's.
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9420, Level = LogLevel.Warning, Message = "Postgres Studio could not measure the bloat of a table in data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioBloatMeasureFailed(this ILogger logger, Exception exception, string storeKey, string databaseId, string? sqlState);

    [LoggerMessage(EventId = 9421, Level = LogLevel.Debug, Message = "Postgres Studio did not measure the bloat of a table in data source {StoreKey}, database {DatabaseId}: {Reason}")]
    public static partial void PostgresStudioBloatMeasureRefused(this ILogger logger, string storeKey, string databaseId, string reason);
}
