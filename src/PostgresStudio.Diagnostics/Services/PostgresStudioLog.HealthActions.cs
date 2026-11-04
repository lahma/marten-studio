using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// 1.2-H-C's events, 9223 and 9224, in Postgres Studio's words: the same ids, levels and properties as
// StudioLog.HealthActions.cs, whose dispatchers choose between the two. The class's own documentation is PostgresStudioLog.cs's.
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9223, Level = LogLevel.Warning, Message = "Postgres Studio could not run the health action {ActionId} in data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioHealthActionFailed(this ILogger logger, Exception exception, string actionId, string storeKey, string databaseId, string? sqlState);

    [LoggerMessage(EventId = 9224, Level = LogLevel.Debug, Message = "Postgres Studio did not run the health action {ActionId} in data source {StoreKey}, database {DatabaseId}: {Reason}")]
    public static partial void PostgresStudioHealthActionRefused(this ILogger logger, string actionId, string storeKey, string databaseId, string reason);
}
