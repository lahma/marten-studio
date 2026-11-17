using Microsoft.Extensions.Logging;

namespace DataStudio.Postgres.Services;

// W5-S2's events, 9440-9449: the Server page's reads. In a file of their own so that the wave-5 packets' event blocks never
// meet in one file; the class's own documentation is StudioLog.cs's.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// One section of the Server page's facts - the curated settings, the extensions, the connection's posture, the grants -
    /// failed for a reason nobody chose: a connection failure, an unexpected SQLSTATE. The page re-reads every
    /// <c>HealthInterval</c> while it is open, so the level comes from <see cref="StudioLogThrottle" />: a Warning once per
    /// store, database and kind of failure in ten minutes, Debug in between. A privilege the role lacks (42501) or a statement
    /// out of its time (57014, 55P03) is the section's own sentence on the page and Debug here. The section is named; nothing
    /// it read is.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9440, Message = "Data Studio could not read the server {Section} of source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void ServerFactsReadFailed(this ILogger logger, LogLevel level, Exception exception, string section, string storeKey, string databaseId, string? sqlState);
}
