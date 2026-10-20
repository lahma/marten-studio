using Microsoft.Extensions.Logging;
using PostgresStudio.Services;

namespace MartenStudio.Services;

/// <summary>
/// The events only MartenStudio logs, in the studio's one id space (<c>9200-9299</c>, see <see cref="StudioLog" />).
/// </summary>
/// <remarks>
/// <para>
/// Moved out of <c>StudioLog.cs</c> by 0.5's split with their ids, levels and message templates unchanged: the schema
/// apply (9206), projection rebuilds and daemon control (9207-9209), the document round trip (9211), and the
/// operational anomalies of the daemon, the shard tracker, the projection summary and the event store (9212,
/// 9215-9218). The LINQ events (9260-9264) are in <c>MartenLog.Query.cs</c>.
/// </para>
/// <para>
/// Every rule of <see cref="StudioLog" /> applies here too: an anomaly takes its level as a parameter, and every call
/// takes it from <see cref="StudioLogThrottle" /> (<c>LogLevelsTests</c> reads both files).
/// </para>
/// </remarks>
internal static partial class MartenLog
{
    [LoggerMessage(EventId = 9206, Level = LogLevel.Information, Message = "Marten Studio user {User} applied schema changes to store {StoreKey}, database {DatabaseId}: {Summary}")]
    public static partial void SchemaChangeApplied(this ILogger logger, string user, string storeKey, string databaseId, string summary);

    [LoggerMessage(EventId = 9207, Level = LogLevel.Information, Message = "Marten Studio user {User} started a rebuild of projection {Projection} in store {StoreKey}, database {DatabaseId}")]
    public static partial void ProjectionRebuildStarted(this ILogger logger, string user, string projection, string storeKey, string databaseId);

    [LoggerMessage(EventId = 9208, Level = LogLevel.Information, Message = "Rebuild of projection {Projection} in store {StoreKey}, database {DatabaseId} finished after {ElapsedMilliseconds} ms: {Outcome}")]
    public static partial void ProjectionRebuildFinished(this ILogger logger, string projection, string storeKey, string databaseId, long elapsedMilliseconds, string outcome);

    [LoggerMessage(EventId = 9209, Level = LogLevel.Information, Message = "Marten Studio user {User} requested daemon control {Operation} on {Target} in store {StoreKey}, database {DatabaseId}")]
    public static partial void DaemonControlRequested(this ILogger logger, string user, string operation, string target, string storeKey, string databaseId);

    /// <remarks>
    /// Warning, and it counts the properties: Marten has no untyped write path, so saving an edited
    /// document round-trips through the CLR type and anything the type does not have is gone. The dialog
    /// shows the loss before the save; this is the record that it happened anyway.
    /// </remarks>
    [LoggerMessage(EventId = 9211, Level = LogLevel.Warning, Message = "Marten Studio user {User} saved {DocumentType} {Id} and the round trip dropped {DroppedCount} properties: {Dropped}")]
    public static partial void DocumentWriteRoundTripDropped(this ILogger logger, string user, string documentType, string id, int droppedCount, string dropped);

    // --------------------------------------------------------------------------------------------------
    // 9212-9229: operational anomalies, each at the level StudioLogThrottle chose (StudioLog's remarks)
    // --------------------------------------------------------------------------------------------------

    /// <remarks>
    /// A coordinator <em>is</em> registered and answered with something other than the two expected
    /// shapes, or could not be constructed for a reason other than the expected one. Wolverine's managed
    /// distribution throws <c>NotSupportedException</c> from the per-database lookup by design, and its
    /// constructor throws <c>ArgumentOutOfRangeException</c> for a store its agent family does not know;
    /// neither comes here. Two throttle sites share this event - the lookup and the construction - so
    /// one does not silence the other.
    /// </remarks>
    [LoggerMessage(EventId = 9212, Message = "Marten Studio could not reach the async daemon of store {StoreKey}, database {DatabaseId}")]
    public static partial void DaemonUnreachable(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    /// <remarks>
    /// The page falls back to the database rows it was reading anyway, so nothing on screen is wrong -
    /// only less fresh between polls.
    /// </remarks>
    [LoggerMessage(EventId = 9215, Message = "Marten Studio could not observe the shard state tracker of store {StoreKey}, database {DatabaseId}")]
    public static partial void ShardTrackerUnobservable(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    /// <remarks>Polled by the Overview's projection tiles and by the navigation badges.</remarks>
    [LoggerMessage(EventId = 9216, Message = "Marten Studio could not summarise the projections of store {StoreKey}, database {DatabaseId}")]
    public static partial void ProjectionSummaryUnreadable(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    /// <remarks>
    /// Every event-store read renders its failure as a value through one describer, and this is its log
    /// line. A timeout - 57014, or the <c>NpgsqlException</c> wrapping a <c>TimeoutException</c> that the
    /// same expiry produces when the backend is slow to answer the cancel - is the page's business and is
    /// Debug. A table or schema that is not there (42P01, 3F000) comes here: every read asks the column
    /// catalog, or <c>DescribeDeadLettersAsync</c>, before it touches a table, so an event store that
    /// has not been created yet is an empty answer and never this, and one of those SQLSTATEs means the
    /// catalog and the database disagree. So does a connection failure and any other SQLSTATE.
    /// </remarks>
    [LoggerMessage(EventId = 9217, Message = "Marten Studio could not {What} in store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void EventReadFailed(this ILogger logger, LogLevel level, Exception exception, string what, string storeKey, string databaseId, string? sqlState);

    /// <remarks>
    /// <c>mt_streams."timestamp"</c> is <c>NOT NULL</c> in every schema Marten creates, so this is a table
    /// somebody migrated by hand - worth one line, and not one per page of the stream list.
    /// </remarks>
    [LoggerMessage(EventId = 9218, Message = "Marten Studio stopped paging the stream list: {Schema}.mt_streams has a row with a null timestamp, which no Marten-created schema has and which a keyset cannot page past")]
    public static partial void StreamTimestampMissing(this ILogger logger, LogLevel level, string schema);
}
