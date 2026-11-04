using Microsoft.Extensions.Logging;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services;

// 1.2-H-C's events, 9223 and 9224: a finding's on-demand action on the Health page. In a file of their own so that the 1.2
// packets' event blocks never meet in one file; the class's own documentation is StudioLog.cs's. Postgres Studio's spellings
// are PostgresStudioLog's (PostgresStudioLog.HealthActions.cs), chosen here by the studio's product. Every value a visitor
// could have shaped (the scope's names, the reason a refusal gives) goes through StudioLogValue inside the dispatcher.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// Warning: a finding's action a visitor pressed failed for a reason nobody chose - a connection failure, an unexpected
    /// SQLSTATE, an exception - after its gates passed. One line per press, never polled: the page shows the failure, and the
    /// audit entry records it.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9223, Level = LogLevel.Warning, Message = "Marten Studio could not run the health action {ActionId} in store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void HealthActionFailed(this ILogger logger, Exception exception, string actionId, string storeKey, string databaseId, string? sqlState);

    /// <remarks>
    /// <para>
    /// Debug: a finding's action was refused for an expected reason - a gate the visitor is short of, another action running on
    /// the database, a statement timeout, an action the finding does not offer. The page says it in words; this is for somebody
    /// who turns Debug on to ask why.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9224, Level = LogLevel.Debug, Message = "Marten Studio did not run the health action {ActionId} in store {StoreKey}, database {DatabaseId}: {Reason}")]
    public static partial void HealthActionRefused(this ILogger logger, string actionId, string storeKey, string databaseId, string reason);

    /// <summary>9223, in the studio's words.</summary>
    public static void HealthActionFailed(this ILogger logger, StudioProduct product, Exception exception, string actionId, string storeKey, string databaseId, string? sqlState)
    {
        string action = StudioLogValue.Cap(actionId);
        string store = StudioLogValue.Cap(storeKey);
        string database = StudioLogValue.Cap(databaseId);

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioHealthActionFailed(exception, action, store, database, sqlState);
        }
        else
        {
            logger.HealthActionFailed(exception, action, store, database, sqlState);
        }
    }

    /// <summary>9224, in the studio's words.</summary>
    public static void HealthActionRefused(this ILogger logger, StudioProduct product, string actionId, string storeKey, string databaseId, string reason)
    {
        string action = StudioLogValue.Cap(actionId);
        string store = StudioLogValue.Cap(storeKey);
        string database = StudioLogValue.Cap(databaseId);
        string why = StudioLogValue.CapText(reason);

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioHealthActionRefused(action, store, database, why);
        }
        else
        {
            logger.HealthActionRefused(action, store, database, why);
        }
    }
}
