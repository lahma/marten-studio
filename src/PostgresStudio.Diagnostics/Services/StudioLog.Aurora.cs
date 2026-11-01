using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-AUR's events, 9500-9509: the host's capacity source and Aurora's own reads. In a file of their own so that the wave-5
// packets' event blocks never meet in one file; the class's own documentation is StudioLog.cs's, and Postgres Studio's
// wording of each is in PostgresStudioLog.Aurora.cs, under the same id.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// The host's <c>IPostgresCapacitySource</c> threw (issue #8 A8). Its answer is asked once per database and instance per
    /// <c>HealthInterval</c>, and a page that polls asks again, so the level comes from <see cref="StudioLogThrottle" />: a
    /// Warning once per database and kind of failure in ten minutes, Debug in between. The capacity is "unknown" for that
    /// interval and nothing else fails. A source that answers null or takes longer than five seconds is a value, at Debug.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9500, Message = "Marten Studio's capacity source failed for store {StoreKey}, database {DatabaseId}; the capacity is unknown until the next interval")]
    public static partial void CapacitySourceFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    /// <remarks>
    /// <para>
    /// One of Aurora's own reads - the instance's waits, the database's commit latency, the replica status - failed on a server
    /// the platform detector calls Aurora, for a reason nobody chose. The sweep and the Server page read them every
    /// <c>HealthInterval</c>, so the level comes from <see cref="StudioLogThrottle" />; a privilege the role lacks (42501) or a
    /// statement out of its time (57014, 55P03) is the section's own sentence and Debug here. The read is named; nothing it
    /// read is.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9501, Message = "Marten Studio could not read Aurora's {Section} of store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void AuroraReadFailed(this ILogger logger, LogLevel level, Exception exception, string section, string storeKey, string databaseId, string? sqlState);
}
