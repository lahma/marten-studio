using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-CAP's events, 9410-9419: the Storage page. In a file of their own so that the wave-5 packets' event blocks never meet in
// one file; the class's own documentation is StudioLog.cs's.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// A read of a database's storage - its size, the statistics window, the largest tables - that failed for a reason nobody
    /// chose: a connection failure, an unexpected SQLSTATE, a table source that threw. A page open on Storage repeats the read
    /// every <c>HealthInterval</c>, so the level comes from <see cref="StudioLogThrottle" />: a Warning once per store, database
    /// and kind of failure in ten minutes, Debug in between. Expected states - a privilege the role lacks (42501), a statement
    /// out of its timeout (57014) or its lock wait (55P03) - are what the page says in words, and are logged at Debug without
    /// coming here.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9410, Message = "Marten Studio could not read the storage of store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void StorageReadFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId, string? sqlState);
}
