using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// 1.0-C43's events, 9460-9464, in Postgres Studio's words: the same ids, levels and properties as StudioLog.ConnectionHistory.cs's.
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9460, Message = "Postgres Studio's connection history could not finish a round; the next interval tries again")]
    public static partial void PostgresStudioConnectionHistoryRoundFailed(this ILogger logger, LogLevel level, Exception exception);

    [LoggerMessage(EventId = 9461, Message = "Postgres Studio's connection history could not sample server {Server}; the next interval opens its connection again")]
    public static partial void PostgresStudioConnectionHistorySampleFailed(this ILogger logger, LogLevel level, Exception exception, string server);

    [LoggerMessage(EventId = 9462, Level = LogLevel.Debug, Message = "Postgres Studio's connection history could not list the databases of data source {StoreKey}")]
    public static partial void PostgresStudioConnectionHistorySourceUnreadable(this ILogger logger, Exception exception, string storeKey);

    [LoggerMessage(EventId = 9463, Level = LogLevel.Information, Message = "Postgres Studio's connection history stored {Count} samples of server {Server} it had held since {Since}")]
    public static partial void PostgresStudioConnectionHistoryBacklogStored(this ILogger logger, int count, string server, DateTimeOffset since);

    [LoggerMessage(EventId = 9464, Message = "Postgres Studio's connection history dropped {Count} of the oldest samples of server {Server} it was holding; the history's storage has refused them since {Since}")]
    public static partial void PostgresStudioConnectionHistoryBacklogDropped(this ILogger logger, LogLevel level, int count, string server, DateTimeOffset since);
}
