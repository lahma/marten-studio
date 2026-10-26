using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

/// <summary>
/// Postgres Studio's wording of every shared event: the same id, level and properties as MartenStudio's
/// (<see cref="StudioLog" />), in its own product's words and its own source's noun.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second set rather than one shared template.</b> A message template is part of a host's structured-log
/// contract - a saved query or an alert rule matches it - and MartenStudio's have said "Marten Studio ... store" since
/// 0.1. Rewording them for a second product would have changed what every existing MartenStudio host's queries match.
/// So <see cref="StudioLog" />'s templates stay exactly as they were, and Postgres Studio logs each shared event under the
/// same <see cref="EventId" /> with a template of its own. An operator filtering by id sees either studio's events; one
/// matching on template text sees only the studio the template names, which is the point.
/// </para>
/// <para>
/// <b>The same properties</b>, so a dashboard written against one studio's fields reads the other's: <c>{User}</c>,
/// <c>{Action}</c>, <c>{StoreKey}</c> (here the data source's name), <c>{DatabaseId}</c> and the rest. A tenant is never
/// part of a Postgres Studio scope, so <c>{TenantId}</c> is the one property its events leave out.
/// </para>
/// <para>
/// <b>A class of its own</b>, so that <see cref="StudioLog" /> still declares each shared id exactly once, and every
/// method named apart from <see cref="StudioLog" />'s, so that neither call is ambiguous. A call site picks the set by
/// <c>IStudioSettings.Text.Product</c>: a fixed-level event through <see cref="StudioLog" />'s dispatchers
/// (<c>StudioLog.PostgresStudio.cs</c>), and one whose level is a parameter directly, in both studios' forms, from the site
/// that took its level from the throttle - because <c>LogLevelsTests</c> follows that level from the call itself.
/// </para>
/// </remarks>
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9200, Level = LogLevel.Information, Message = "Postgres Studio user {User} performed {Action} on {Target} in data source {StoreKey}, database {DatabaseId}: {Outcome}")]
    public static partial void PostgresStudioActionPerformed(this ILogger logger, string user, string action, string target, string storeKey, string databaseId, string outcome);

    [LoggerMessage(EventId = 9201, Level = LogLevel.Information, Message = "Postgres Studio user {User} attempted {Action} on {Target} in data source {StoreKey}, database {DatabaseId} and it failed: {Reason}")]
    public static partial void PostgresStudioActionFailed(this ILogger logger, string user, string action, string target, string storeKey, string databaseId, string? reason);

    [LoggerMessage(EventId = 9202, Level = LogLevel.Warning, Message = "Postgres Studio user {User} was refused capability {Capability}: {Option} would have to be set")]
    public static partial void PostgresStudioCapabilityDenied(this ILogger logger, string user, string capability, string option);

    [LoggerMessage(EventId = 9203, Level = LogLevel.Warning, Message = "Postgres Studio user {User} was refused data source {StoreKey}, database {DatabaseId} by policy {Policy}")]
    public static partial void PostgresStudioScopeAuthorizationDenied(this ILogger logger, string user, string storeKey, string databaseId, string policy);

    [LoggerMessage(EventId = 9204, Level = LogLevel.Information, Message = "Postgres Studio user {User} ran SQL against data source {StoreKey}, database {DatabaseId} in {ElapsedMilliseconds} ms returning {RowCount} rows: {Sql}")]
    public static partial void PostgresStudioSqlExecuted(this ILogger logger, string user, string storeKey, string databaseId, long elapsedMilliseconds, int rowCount, string sql);

    [LoggerMessage(EventId = 9205, Level = LogLevel.Information, Message = "Postgres Studio refused SQL from user {User} against data source {StoreKey}, database {DatabaseId}: {Reason}. Statement: {Sql}")]
    public static partial void PostgresStudioSqlRejected(this ILogger logger, string user, string storeKey, string databaseId, string reason, string sql);

    [LoggerMessage(EventId = 9210, Message = "Postgres Studio could not resolve data source {StoreKey} ({ServiceType}): {Reason}")]
    public static partial void PostgresStudioStoreUnavailable(this ILogger logger, LogLevel level, string storeKey, string serviceType, string reason);

    [LoggerMessage(EventId = 9213, Message = "Postgres Studio could not list the databases of data source {StoreKey}")]
    public static partial void PostgresStudioStoreDatabasesUnreadable(this ILogger logger, LogLevel level, Exception exception, string storeKey);

    [LoggerMessage(EventId = 9214, Message = "Postgres Studio could not read the Postgres version of data source {StoreKey}")]
    public static partial void PostgresStudioPostgresVersionUnreadable(this ILogger logger, LogLevel level, Exception? exception, string storeKey);

    [LoggerMessage(EventId = 9219, Message = "Postgres Studio could not discover the tenants of data source {StoreKey}")]
    public static partial void PostgresStudioTenantDiscoveryFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey);

    [LoggerMessage(EventId = 9220, Message = "A Postgres Studio page failed to handle a live update failure")]
    public static partial void PostgresStudioLiveUpdateHandlerFailed(this ILogger logger, LogLevel level, Exception exception);

    [LoggerMessage(EventId = 9221, Message = "Postgres Studio could not ask the source policy which tenants of data source {StoreKey}, database {DatabaseId} to list")]
    public static partial void PostgresStudioTenantPolicyFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    [LoggerMessage(EventId = 9222, Message = "Postgres Studio's health check {Code} could not check data source {StoreKey}, database {DatabaseId}")]
    public static partial void PostgresStudioHealthCheckFailed(this ILogger logger, LogLevel level, Exception exception, string code, string storeKey, string databaseId);

    [LoggerMessage(EventId = 9230, Level = LogLevel.Warning, Message = "Postgres Studio's database browser may show every schema (PostgresStudioOptions.BrowsableSchemas contains \"*\") and PostgresStudioOptions.SqlConsoleRole is not set, so it reads the catalog and rows as the data source's own Postgres role: every table that role can select from is browsable by anyone granted PostgresStudioOptions.Capabilities.BrowseDatabase. Set SqlConsoleRole to a role that can read only what the browser should show.")]
    public static partial void PostgresStudioDatabaseBrowserOpenToEverySchemaWithoutRole(this ILogger logger);

    [LoggerMessage(EventId = 9235, Level = LogLevel.Information, Message = "Postgres Studio user {User} performed {Action} on {Relation} in data source {StoreKey}, database {DatabaseId} with filter {Filter} and sort {Sort}")]
    public static partial void PostgresStudioTableRowsRead(this ILogger logger, string user, string action, string relation, string storeKey, string databaseId, string filter, string sort);

    [LoggerMessage(EventId = 9236, Level = LogLevel.Information, Message = "Postgres Studio user {User} performed {Action} on {Relation} in data source {StoreKey}, database {DatabaseId} for the row with key {Key}")]
    public static partial void PostgresStudioTableRowOpened(this ILogger logger, string user, string action, string relation, string storeKey, string databaseId, string key);

    [LoggerMessage(EventId = 9237, Message = "Postgres Studio's {Action} on {Relation} in data source {StoreKey}, database {DatabaseId} failed: {SqlState}")]
    public static partial void PostgresStudioTableRowReadFailed(this ILogger logger, LogLevel level, Exception exception, string action, string relation, string storeKey, string databaseId, string? sqlState);

    [LoggerMessage(EventId = 9245, Level = LogLevel.Information, Message = "Postgres Studio user {User} read the session details of data source {StoreKey}, database {DatabaseId}: {Sessions} sessions, {Locks} locks, {Prepared} prepared transactions")]
    public static partial void PostgresStudioSessionDetailsRead(this ILogger logger, string user, string storeKey, string databaseId, int sessions, int locks, int prepared);

    [LoggerMessage(EventId = 9246, Message = "Postgres Studio could not read the sessions of data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioSessionActivityReadFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId, string? sqlState);

    [LoggerMessage(EventId = 9270, Message = "Postgres Studio's health export failed, so its health check reports Degraded")]
    public static partial void PostgresStudioHealthExportFailed(this ILogger logger, LogLevel level, Exception exception);

    [LoggerMessage(EventId = 9280, Level = LogLevel.Debug, Message = "Postgres Studio's health check T02 could not plan statement {QueryId} of data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioStatementPlanFailed(this ILogger logger, Exception exception, long queryId, string storeKey, string databaseId, string? sqlState);

    [LoggerMessage(EventId = 9281, Level = LogLevel.Debug, Message = "Postgres Studio's health check T02 could not take back the prepared statement {PreparedName} of its plan of statement {QueryId} (data source {StoreKey}, database {DatabaseId}); that session keeps the name until it is reset or planned on again")]
    public static partial void PostgresStudioStatementPlanNotTakenBack(this ILogger logger, Exception exception, long queryId, string storeKey, string databaseId, string preparedName);

    [LoggerMessage(EventId = 9290, Level = LogLevel.Debug, Message = "Postgres Studio took the Postgres server of data source {StoreKey}, database {DatabaseId} to be {Platform}: {Marker}")]
    public static partial void PostgresStudioPostgresPlatformDetected(this ILogger logger, string platform, string marker, string storeKey, string databaseId);
}
