using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-R2's events, 9430-9439: the Replication page's reads. In a file of their own so that the wave-5 packets' event blocks
// never meet in one file; the class's own documentation is StudioLog.cs's, and Postgres Studio's wording of each is in
// PostgresStudioLog.Replication.cs, under the same id.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// One section of the Replication page's facts - the standbys, the archiver, a statistics view - failed for a reason nobody
    /// chose: a connection failure, an unexpected SQLSTATE. The page re-reads every <c>HealthInterval</c> while it is open, so
    /// the level comes from <see cref="StudioLogThrottle" />: a Warning once per store, database and kind of failure in ten
    /// minutes, Debug in between. A privilege the role lacks (42501) or a statement out of its time (57014, 55P03) is the
    /// section's own sentence on the page and Debug here. The section is named; nothing it read is.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9430, Message = "Marten Studio could not read the replication {Section} of store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void ReplicationReadFailed(this ILogger logger, LogLevel level, Exception exception, string section, string storeKey, string databaseId, string? sqlState);

    /// <remarks>
    /// <para>
    /// A tablespace size the visitor asked for failed for a reason other than a privilege (42501, which the page says in words
    /// and logs at Debug) or a timeout: one line per click, never polled. The tablespace's name is not in it.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9431, Level = LogLevel.Warning, Message = "Marten Studio could not measure a tablespace of store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void TablespaceSizeFailed(this ILogger logger, Exception exception, string storeKey, string databaseId, string? sqlState);
}
