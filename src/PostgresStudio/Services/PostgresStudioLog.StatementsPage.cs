using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-Q1's events, 9450-9459, in Postgres Studio's words: the same ids, levels and properties as StudioLog.StatementsPage.cs's.
// In a file of their own for the same reason; the class's own documentation is PostgresStudioLog.cs's.
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9450, Level = LogLevel.Debug, Message = "Postgres Studio did not show the statements of data source {StoreKey}, database {DatabaseId}: {Reason}")]
    public static partial void PostgresStudioStatementsPageRefused(this ILogger logger, string storeKey, string databaseId, string reason);

    [LoggerMessage(EventId = 9451, Message = "Postgres Studio could not read the statements of data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioStatementsReadFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId, string? sqlState);

    [LoggerMessage(EventId = 9452, Level = LogLevel.Information, Message = "Postgres Studio user {User} read the statements of data source {StoreKey}, database {DatabaseId}: {Statements} statements")]
    public static partial void PostgresStudioStatementsRead(this ILogger logger, string user, string storeKey, string databaseId, int statements);
}
