using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-R2's events, 9430-9439, in Postgres Studio's words: the same ids, levels and properties as StudioLog.Replication.cs.
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9430, Message = "Postgres Studio could not read the replication {Section} of data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioReplicationReadFailed(this ILogger logger, LogLevel level, Exception exception, string section, string storeKey, string databaseId, string? sqlState);

    [LoggerMessage(EventId = 9431, Level = LogLevel.Warning, Message = "Postgres Studio could not measure a tablespace of data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioTablespaceSizeFailed(this ILogger logger, Exception exception, string storeKey, string databaseId, string? sqlState);
}
