using Microsoft.Extensions.Logging;

namespace DataStudio.Postgres.Services;

// The dispatchers: one public method per audit and refusal event a shared site writes, in front of the event's private
// ...Raw template (StudioLog.cs). A dispatcher is the one way a site writes its event, and it takes every value a visitor could
// have shaped - the user's name, the scope the address asked for, a target, a reason, a statement - through StudioLogValue:
// escaped and bounded, so a refused ?store= cannot write a log line of any length or a second, forged one. 9204's statement,
// the one that ran, is kept whole up to the circuit's receive bound instead (StudioLogValue.CapStatement). Constants (an
// action's name, a capability, an option, a policy's name) go as they are. The templates are private, so the compiler keeps a
// site from calling one uncapped; RefusalLogValuesTests holds every dispatcher to the caps. None of these is a [LoggerMessage],
// so this class still declares each id once. 9223, 9224 and 9245's siblings are in StudioLog.HealthActions.cs, and 9452's in
// StudioLog.StatementsPage.cs.
internal static partial class StudioLog
{
    /// <summary>9200.</summary>
    public static void ActionPerformed(this ILogger logger, string user, string action, string target, string storeKey, string databaseId, string tenantId, string outcome)
    {
        (user, target, storeKey, databaseId, tenantId, outcome) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(target), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.Cap(tenantId), StudioLogValue.CapText(outcome));

        logger.ActionPerformedRaw(user, action, target, storeKey, databaseId, tenantId, outcome);
    }

    /// <summary>9201.</summary>
    public static void ActionFailed(this ILogger logger, string user, string action, string target, string storeKey, string databaseId, string tenantId, string? reason)
    {
        (user, target, storeKey, databaseId, tenantId, reason) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(target), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.Cap(tenantId), StudioLogValue.CapText(reason));

        logger.ActionFailedRaw(user, action, target, storeKey, databaseId, tenantId, reason);
    }

    /// <summary>9202.</summary>
    public static void CapabilityDenied(this ILogger logger, string user, string capability, string option)
    {
        user = StudioLogValue.Cap(user);

        logger.CapabilityDeniedRaw(user, capability, option);
    }

    /// <summary>9203.</summary>
    public static void ScopeAuthorizationDenied(this ILogger logger, string user, string storeKey, string databaseId, string tenantId, string policy)
    {
        (user, storeKey, databaseId, tenantId) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.Cap(tenantId));

        logger.ScopeAuthorizationDeniedRaw(user, storeKey, databaseId, tenantId, policy);
    }

    /// <summary>
    /// 9204: the statement that ran is escaped and kept whole up to the circuit's receive bound
    /// (<see cref="StudioLogValue.CapStatement" />), never cut at its front, so no padding keeps it out of the record.
    /// </summary>
    public static void SqlExecuted(this ILogger logger, string user, string storeKey, string databaseId, long elapsedMilliseconds, int rowCount, string sql)
    {
        (user, storeKey, databaseId, sql) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.CapStatement(sql));

        logger.SqlExecutedRaw(user, storeKey, databaseId, elapsedMilliseconds, rowCount, sql);
    }

    /// <summary>9205.</summary>
    public static void SqlRejected(this ILogger logger, string user, string storeKey, string databaseId, string reason, string sql)
    {
        (user, storeKey, databaseId, reason, sql) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.CapText(reason), StudioLogValue.CapText(sql));

        logger.SqlRejectedRaw(user, storeKey, databaseId, reason, sql);
    }

    /// <summary>9235.</summary>
    public static void TableRowsRead(this ILogger logger, string user, string action, string relation, string storeKey, string databaseId, string filter, string sort)
    {
        (user, relation, storeKey, databaseId, filter, sort) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(relation), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.CapText(filter), StudioLogValue.CapText(sort));

        logger.TableRowsReadRaw(user, action, relation, storeKey, databaseId, filter, sort);
    }

    /// <summary>9236.</summary>
    public static void TableRowOpened(this ILogger logger, string user, string action, string relation, string storeKey, string databaseId, string key)
    {
        (user, relation, storeKey, databaseId, key) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(relation), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.CapText(key));

        logger.TableRowOpenedRaw(user, action, relation, storeKey, databaseId, key);
    }

    /// <summary>9245.</summary>
    public static void SessionDetailsRead(this ILogger logger, string user, string storeKey, string databaseId, int sessions, int locks, int prepared)
    {
        (user, storeKey, databaseId) = (StudioLogValue.Cap(user), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId));

        logger.SessionDetailsReadRaw(user, storeKey, databaseId, sessions, locks, prepared);
    }
}
