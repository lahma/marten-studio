using Microsoft.Extensions.Logging;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services;

// The dispatchers: one per fixed-level shared event a shared site logs, choosing the studio's own template - this class's
// for MartenStudio, exactly as it always was, and PostgresStudioLog's for Postgres Studio (whose remarks say why there are
// two). An event whose level is a parameter has no dispatcher: its site calls both forms itself, because LogLevelsTests
// follows the level from the call. None of these is a [LoggerMessage], so this class still declares each id once.
//
// Wave 6: the audit and refusal dispatchers (9200-9205, 9235, 9236, 9245, and 9452's in StudioLog.StatementsPage.cs) are
// the one way a site writes those events, and each takes every value a visitor could have shaped - the user's name, the
// scope the address asked for, a target, a reason, a statement - through StudioLogValue: escaped and bounded, so a refused
// ?store= cannot write a log line of any length or a second, forged one. 9204's statement, the one that ran, is kept whole
// up to the circuit's receive bound instead (StudioLogValue.CapStatement). Constants (an action's name, a capability, an
// option, a policy's name) go as they are. RefusalLogValuesTests holds every site to the dispatchers and every dispatcher
// to the caps.
internal static partial class StudioLog
{
    /// <summary>9200 in the studio's own words.</summary>
    public static void ActionPerformed(this ILogger logger, StudioProduct product, string user, string action, string target, string storeKey, string databaseId, string tenantId, string outcome)
    {
        (user, target, storeKey, databaseId, tenantId, outcome) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(target), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.Cap(tenantId), StudioLogValue.CapText(outcome));

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioActionPerformed(user, action, target, storeKey, databaseId, outcome);
        }
        else
        {
            logger.ActionPerformed(user, action, target, storeKey, databaseId, tenantId, outcome);
        }
    }

    /// <summary>9201 in the studio's own words.</summary>
    public static void ActionFailed(this ILogger logger, StudioProduct product, string user, string action, string target, string storeKey, string databaseId, string tenantId, string? reason)
    {
        (user, target, storeKey, databaseId, tenantId, reason) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(target), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.Cap(tenantId), StudioLogValue.CapText(reason));

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioActionFailed(user, action, target, storeKey, databaseId, reason);
        }
        else
        {
            logger.ActionFailed(user, action, target, storeKey, databaseId, tenantId, reason);
        }
    }

    /// <summary>9202 in the studio's own words.</summary>
    public static void CapabilityDenied(this ILogger logger, StudioProduct product, string user, string capability, string option)
    {
        user = StudioLogValue.Cap(user);

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioCapabilityDenied(user, capability, option);
        }
        else
        {
            logger.CapabilityDenied(user, capability, option);
        }
    }

    /// <summary>9203 in the studio's own words.</summary>
    public static void ScopeAuthorizationDenied(this ILogger logger, StudioProduct product, string user, string storeKey, string databaseId, string tenantId, string policy)
    {
        (user, storeKey, databaseId, tenantId) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.Cap(tenantId));

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioScopeAuthorizationDenied(user, storeKey, databaseId, policy);
        }
        else
        {
            logger.ScopeAuthorizationDenied(user, storeKey, databaseId, tenantId, policy);
        }
    }

    /// <summary>
    /// 9204 in the studio's own words: the statement that ran is escaped and kept whole up to the circuit's receive bound
    /// (<see cref="StudioLogValue.CapStatement" />), never cut at its front, so no padding keeps it out of the record.
    /// </summary>
    public static void SqlExecuted(this ILogger logger, StudioProduct product, string user, string storeKey, string databaseId, long elapsedMilliseconds, int rowCount, string sql)
    {
        (user, storeKey, databaseId, sql) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.CapStatement(sql));

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioSqlExecuted(user, storeKey, databaseId, elapsedMilliseconds, rowCount, sql);
        }
        else
        {
            logger.SqlExecuted(user, storeKey, databaseId, elapsedMilliseconds, rowCount, sql);
        }
    }

    /// <summary>9205 in the studio's own words.</summary>
    public static void SqlRejected(this ILogger logger, StudioProduct product, string user, string storeKey, string databaseId, string reason, string sql)
    {
        (user, storeKey, databaseId, reason, sql) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.CapText(reason), StudioLogValue.CapText(sql));

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioSqlRejected(user, storeKey, databaseId, reason, sql);
        }
        else
        {
            logger.SqlRejected(user, storeKey, databaseId, reason, sql);
        }
    }

    /// <summary>9230 in the studio's own words.</summary>
    public static void DatabaseBrowserOpenToEverySchemaWithoutRole(this ILogger logger, StudioProduct product)
    {
        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioDatabaseBrowserOpenToEverySchemaWithoutRole();
        }
        else
        {
            logger.DatabaseBrowserOpenToEverySchemaWithoutRole();
        }
    }

    /// <summary>9235 in the studio's own words.</summary>
    public static void TableRowsRead(this ILogger logger, StudioProduct product, string user, string action, string relation, string storeKey, string databaseId, string filter, string sort)
    {
        (user, relation, storeKey, databaseId, filter, sort) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(relation), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.CapText(filter), StudioLogValue.CapText(sort));

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioTableRowsRead(user, action, relation, storeKey, databaseId, filter, sort);
        }
        else
        {
            logger.TableRowsRead(user, action, relation, storeKey, databaseId, filter, sort);
        }
    }

    /// <summary>9236 in the studio's own words.</summary>
    public static void TableRowOpened(this ILogger logger, StudioProduct product, string user, string action, string relation, string storeKey, string databaseId, string key)
    {
        (user, relation, storeKey, databaseId, key) =
            (StudioLogValue.Cap(user), StudioLogValue.Cap(relation), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId), StudioLogValue.CapText(key));

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioTableRowOpened(user, action, relation, storeKey, databaseId, key);
        }
        else
        {
            logger.TableRowOpened(user, action, relation, storeKey, databaseId, key);
        }
    }

    /// <summary>9245 in the studio's own words.</summary>
    public static void SessionDetailsRead(this ILogger logger, StudioProduct product, string user, string storeKey, string databaseId, int sessions, int locks, int prepared)
    {
        (user, storeKey, databaseId) = (StudioLogValue.Cap(user), StudioLogValue.Cap(storeKey), StudioLogValue.Cap(databaseId));

        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioSessionDetailsRead(user, storeKey, databaseId, sessions, locks, prepared);
        }
        else
        {
            logger.SessionDetailsRead(user, storeKey, databaseId, sessions, locks, prepared);
        }
    }

    /// <summary>9280 in the studio's own words.</summary>
    public static void StatementPlanFailed(this ILogger logger, StudioProduct product, Exception exception, long queryId, string storeKey, string databaseId, string? sqlState)
    {
        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioStatementPlanFailed(exception, queryId, storeKey, databaseId, sqlState);
        }
        else
        {
            logger.StatementPlanFailed(exception, queryId, storeKey, databaseId, sqlState);
        }
    }

    /// <summary>9281 in the studio's own words.</summary>
    public static void StatementPlanNotTakenBack(this ILogger logger, StudioProduct product, Exception exception, long queryId, string storeKey, string databaseId, string preparedName)
    {
        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioStatementPlanNotTakenBack(exception, queryId, storeKey, databaseId, preparedName);
        }
        else
        {
            logger.StatementPlanNotTakenBack(exception, queryId, storeKey, databaseId, preparedName);
        }
    }

    /// <summary>9290 in the studio's own words.</summary>
    public static void PostgresPlatformDetected(this ILogger logger, StudioProduct product, string platform, string marker, string storeKey, string databaseId)
    {
        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioPostgresPlatformDetected(platform, marker, storeKey, databaseId);
        }
        else
        {
            logger.PostgresPlatformDetected(platform, marker, storeKey, databaseId);
        }
    }
}
