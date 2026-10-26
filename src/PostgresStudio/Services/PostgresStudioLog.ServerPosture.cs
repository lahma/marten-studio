using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-S2's events, 9440-9449, in Postgres Studio's words: the same ids, levels and properties as StudioLog.ServerPosture.cs.
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9440, Message = "Postgres Studio could not read the server {Section} of data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioServerFactsReadFailed(this ILogger logger, LogLevel level, Exception exception, string section, string storeKey, string databaseId, string? sqlState);
}
