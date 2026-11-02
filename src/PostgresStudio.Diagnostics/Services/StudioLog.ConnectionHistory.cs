using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// 1.0-C43's events, 9460-9462: the opt-in connection history's sampler (issue #43). In a file of their own; the class's own
// documentation is StudioLog.cs's, and Postgres Studio's wording of each is in PostgresStudioLog.ConnectionHistory.cs, under the
// same id.
internal static partial class StudioLog
{
    /// <remarks>
    /// A round of the connection history failed as a whole - the host's <c>ShouldSample</c> or the store threw, say. The sampler
    /// tries again every <c>Interval</c>, so the level comes from <see cref="StudioLogThrottle" />: a Warning once per kind of
    /// failure in ten minutes, Debug in between.
    /// </remarks>
    [LoggerMessage(EventId = 9460, Message = "Marten Studio's connection history could not finish a round; the next interval tries again")]
    public static partial void ConnectionHistoryRoundFailed(this ILogger logger, LogLevel level, Exception exception);

    /// <remarks>
    /// One server could not be sampled, or its sample could not be kept. Its connection is closed and opened again at the next
    /// round, every <c>Interval</c>, so the level comes from <see cref="StudioLogThrottle" />, per server and kind of failure. The
    /// server is the host and port the application's own connection string names - never a database, a user or a password.
    /// </remarks>
    [LoggerMessage(EventId = 9461, Message = "Marten Studio's connection history could not sample server {Server}; the next interval opens its connection again")]
    public static partial void ConnectionHistorySampleFailed(this ILogger logger, LogLevel level, Exception exception, string server);

    /// <remarks>
    /// A store's databases could not be listed for the sampler. The store's own pages say so under 9213; this is Debug.
    /// </remarks>
    [LoggerMessage(EventId = 9462, Level = LogLevel.Debug, Message = "Marten Studio's connection history could not list the databases of store {StoreKey}")]
    public static partial void ConnectionHistorySourceUnreadable(this ILogger logger, Exception exception, string storeKey);
}
