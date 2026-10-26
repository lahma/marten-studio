using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-AUR's events, 9500-9509, in Postgres Studio's words: the same ids, levels and properties as StudioLog.Aurora.cs.
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9500, Message = "Postgres Studio's capacity source failed for data source {StoreKey}, database {DatabaseId}; the capacity is unknown until the next interval")]
    public static partial void PostgresStudioCapacitySourceFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    [LoggerMessage(EventId = 9501, Message = "Postgres Studio could not read Aurora's {Section} of data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioAuroraReadFailed(this ILogger logger, LogLevel level, Exception exception, string section, string storeKey, string databaseId, string? sqlState);
}
