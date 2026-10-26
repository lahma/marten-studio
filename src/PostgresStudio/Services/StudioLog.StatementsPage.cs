using Microsoft.Extensions.Logging;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services;

// W5-Q1's events, 9450-9459: the Statements page. In a file of their own so that the wave-5 packets' event blocks never
// meet in one file; the class's own documentation is StudioLog.cs's. Postgres Studio's spellings are PostgresStudioLog's
// (PostgresStudioLog.StatementsPage.cs), chosen here by the studio's product.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// Debug: the Statements page's read was refused by its gate - <c>ViewDatabaseActivity</c> off (or <c>ReadOnly</c>), the
    /// store policy for the database as a whole, or the write policy with the capability named - which the page says in words
    /// and which a visitor causes on every open of a page they may not read. Not audited: the page asks once per load and poll,
    /// and a page whose gate is closed refuses nothing a person did.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9450, Level = LogLevel.Debug, Message = "Marten Studio did not show the statements of store {StoreKey}, database {DatabaseId}: {Reason}")]
    public static partial void StatementsPageRefused(this ILogger logger, string storeKey, string databaseId, string reason);

    /// <remarks>
    /// <para>
    /// A read of a database's statements that failed for a reason nobody chose - the statement sweep could not be taken, a
    /// connection failure. A page open on Statements repeats the read every <c>HealthInterval</c>, so the level comes from
    /// <see cref="StudioLogThrottle" />: a Warning once per store, database and kind of failure in ten minutes, Debug in
    /// between. An expected state - the extension not installed or not preloaded, nothing recorded yet - is what the page says
    /// in words, and never comes here; a statement check that failed inside the sweep is the sweep's own event 9222.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9451, Message = "Marten Studio could not read the statements of store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void StatementsReadFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId, string? sqlState);

    /// <remarks>
    /// <para>
    /// Information, like 9245: an audit line, written once per circuit, store and database the first time a visitor through
    /// the <c>ViewDatabaseActivity</c> gate reads the Statements page - never per poll, never on a refresh, and never from a
    /// static render. What they read is not in it: other programs' statements are exactly what the gate exists to keep, and a
    /// log line is readable by more people than the page.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9452, Level = LogLevel.Information, Message = "Marten Studio user {User} read the statements of store {StoreKey}, database {DatabaseId}: {Statements} statements")]
    public static partial void StatementsRead(this ILogger logger, string user, string storeKey, string databaseId, int statements);

    /// <summary>9450, in the studio's words.</summary>
    public static void StatementsPageRefused(this ILogger logger, StudioProduct product, string storeKey, string databaseId, string reason)
    {
        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioStatementsPageRefused(storeKey, databaseId, reason);
        }
        else
        {
            logger.StatementsPageRefused(storeKey, databaseId, reason);
        }
    }

    /// <summary>9451, in the studio's words.</summary>
    public static void StatementsReadFailed(this ILogger logger, StudioProduct product, LogLevel level, Exception exception, string storeKey, string databaseId, string? sqlState)
    {
        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioStatementsReadFailed(level, exception, storeKey, databaseId, sqlState);
        }
        else
        {
            logger.StatementsReadFailed(level, exception, storeKey, databaseId, sqlState);
        }
    }

    /// <summary>9452, in the studio's words.</summary>
    public static void StatementsRead(this ILogger logger, StudioProduct product, string user, string storeKey, string databaseId, int statements)
    {
        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioStatementsRead(user, storeKey, databaseId, statements);
        }
        else
        {
            logger.StatementsRead(user, storeKey, databaseId, statements);
        }
    }
}
