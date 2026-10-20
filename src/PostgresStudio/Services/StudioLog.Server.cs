using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// H10's events, 9290-9294: the server and replication checks and the managed-platform detector. In a file of their own
// so that the wave-3 packets' event blocks never meet in one file; the class's own documentation is StudioLog.cs's.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// Debug, and nothing louder: which platform the detector settled on for a database, and what said so - so that
    /// somebody asking why S01 speaks of "your provider's schedule", or why S02 said nothing about <c>fsync = off</c>,
    /// can turn Debug on and see. A platform is a value, not an anomaly; it is read once per regular sweep, which a page
    /// asks for at <c>HealthInterval</c>, so anything above Debug would be a line a minute about nothing.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9290, Level = LogLevel.Debug, Message = "Marten Studio took the Postgres server of store {StoreKey}, database {DatabaseId} to be {Platform}: {Marker}")]
    public static partial void PostgresPlatformDetected(this ILogger logger, string platform, string marker, string storeKey, string databaseId);
}
