using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// H3's events, 9245-9249: Database › Sessions and the ViewDatabaseActivity gate. In a file of their own so that
// the wave-1 packets' event blocks never meet in one file; the class's own documentation is StudioLog.cs's.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// Information, like 9235: an audit line, written once per circuit, store and database the first time a
    /// visitor through the <c>ViewDatabaseActivity</c> gate reads session details - never per poll, never on a
    /// refresh, and never from a static render. What they read is not in it: the process ids, roles, client
    /// addresses and statements of other sessions are exactly what the gate exists to keep, and a log line is
    /// readable by more people than the page.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9245, Level = LogLevel.Information, Message = "Marten Studio user {User} read the session details of store {StoreKey}, database {DatabaseId}: {Sessions} sessions, {Locks} locks, {Prepared} prepared transactions")]
    public static partial void SessionDetailsRead(this ILogger logger, string user, string storeKey, string databaseId, int sessions, int locks, int prepared);

    /// <remarks>
    /// <para>
    /// A read of a database's sessions, locks or prepared transactions that failed for a reason nobody chose - a
    /// connection failure, an unexpected SQLSTATE. A page open on Database › Sessions repeats the read every
    /// <c>HealthInterval</c>, so the level comes from <see cref="StudioLogThrottle" />: a Warning once per store,
    /// database and kind of failure in ten minutes, Debug in between. Expected states - a privilege the role
    /// lacks (42501), a statement out of its timeout (57014) or its lock wait (55P03) - are what the page says in
    /// words, and are logged at Debug without coming here.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9246, Message = "Marten Studio could not read the sessions of store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void SessionActivityReadFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId, string? sqlState);
}
