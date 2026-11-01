using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-CAP's events, 9410-9419, in Postgres Studio's words: the same id, level and properties as StudioLog.Storage.cs's. In a
// file of their own for the same reason; the class's own documentation is PostgresStudioLog.cs's.
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9410, Message = "Postgres Studio could not read the storage of data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioStorageReadFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId, string? sqlState);
}
