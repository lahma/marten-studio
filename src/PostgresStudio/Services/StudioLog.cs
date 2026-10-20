using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

/// <summary>
/// Every event the shared studio logs, as source-generated methods with a pinned event id.
/// </summary>
/// <remarks>
/// <para>
/// <b>The split (0.5, PS-1a).</b> Event ids <c>9200-9299</c> belong to the studio, and one id space is shared by the
/// events every studio logs - here, in <c>StudioLog</c> and its partial files - and the events only MartenStudio logs,
/// in MartenStudio's <c>MartenLog</c>: the schema apply (9206), projection rebuilds and daemon control (9207-9209), the
/// document round trip (9211), the daemon, tracker, projection and event-store anomalies (9212, 9215-9218) and LINQ
/// mode (9260-9264). No id moved and no message changed; what changed is only which class declares an event, which no
/// host can see. Every event a later packet needs is already declared on one side or the other, so no packet after
/// this one adds a method here: a shared event is here, and a Marten one is in <c>MartenLog</c>.
/// </para>
/// <para>
/// Here: 9200-9205 (audit, the SQL console), 9210 (a source that will not build), 9213, 9214, 9219-9222 (listing,
/// version, tenant discovery and policy, live-update and health-check anomalies), 9230 (the database browser's
/// configuration notice), 9235-9237 (row reads), 9245-9246 (Sessions), 9270 (the health export), 9280-9281 (the
/// statement checks) and 9290 (platform detection).
/// </para>
/// <para>
/// The first twelve ids (9200-9211) are the audit events
/// and were declared together from the first packet that logs any of them, so the numbers are reserved
/// rather than assigned in the order features happened to land - an operator's log query is written
/// against the number, and renumbering one later would silently change what a saved query matches.
/// </para>
/// <para>
/// <b>9212-9229 are the operational anomalies</b>, and every one of them takes its level as a parameter.
/// They are the failures that happen on a path the studio polls - the Overview, the projections screen,
/// the navigation badges, each re-read every <c>RefreshInterval</c> in every open circuit - so each is
/// logged at Warning the first time <see cref="StudioLogThrottle" /> sees its store, database and kind of
/// failure in ten minutes, and at Debug after that. A host that wants them gone filters on the id; a host
/// that wants every occurrence turns Debug on. Expected states - no daemon here, a daemon run by an
/// external system, event tables that do not exist yet - are not anomalies and have no id: they are
/// Debug lines, because they are values the page already shows (AGENTS.md hard rule 11).
/// </para>
/// <para>
/// <b>Every call of one of these takes its level from the throttle</b> -
/// <see cref="StudioLogThrottle.WarningOrDebug" />, or <see cref="StudioLogThrottle.LevelOrWarning" /> for
/// an owner a test builds without one - and never from a literal <c>LogLevel.Warning</c>, which would put
/// the site back on every poll. <c>LogLevelsTests</c> reads every call site and enforces it.
/// </para>
/// <para>
/// The in-memory Activity ring is bounded at 500 entries in one process and is gone at the next restart.
/// These are the same events on the way to whatever the application logs to, which is the only record
/// that survives a deployment.
/// </para>
/// </remarks>
internal static partial class StudioLog
{
    [LoggerMessage(EventId = 9200, Level = LogLevel.Information, Message = "Marten Studio user {User} performed {Action} on {Target} in store {StoreKey}, database {DatabaseId}, tenant {TenantId}: {Outcome}")]
    public static partial void ActionPerformed(this ILogger logger, string user, string action, string target, string storeKey, string databaseId, string tenantId, string outcome);

    [LoggerMessage(EventId = 9201, Level = LogLevel.Information, Message = "Marten Studio user {User} attempted {Action} on {Target} in store {StoreKey}, database {DatabaseId}, tenant {TenantId} and it failed: {Reason}")]
    public static partial void ActionFailed(this ILogger logger, string user, string action, string target, string storeKey, string databaseId, string tenantId, string? reason);

    /// <remarks>
    /// Warning, and it names the option: a capability refusal is a configuration answer rather than a
    /// fault, but it is also what an operator sees when a colleague reports that a button does nothing.
    /// </remarks>
    [LoggerMessage(EventId = 9202, Level = LogLevel.Warning, Message = "Marten Studio user {User} was refused capability {Capability}: {Option} would have to be set")]
    public static partial void CapabilityDenied(this ILogger logger, string user, string capability, string option);

    [LoggerMessage(EventId = 9203, Level = LogLevel.Warning, Message = "Marten Studio user {User} was refused store {StoreKey}, database {DatabaseId}, tenant {TenantId} by policy {Policy}")]
    public static partial void ScopeAuthorizationDenied(this ILogger logger, string user, string storeKey, string databaseId, string tenantId, string policy);

    [LoggerMessage(EventId = 9204, Level = LogLevel.Information, Message = "Marten Studio user {User} ran SQL against store {StoreKey}, database {DatabaseId} in {ElapsedMilliseconds} ms returning {RowCount} rows: {Sql}")]
    public static partial void SqlExecuted(this ILogger logger, string user, string storeKey, string databaseId, long elapsedMilliseconds, int rowCount, string sql);

    [LoggerMessage(EventId = 9205, Level = LogLevel.Information, Message = "Marten Studio refused SQL from user {User} against store {StoreKey}, database {DatabaseId}: {Reason}. Statement: {Sql}")]
    public static partial void SqlRejected(this ILogger logger, string user, string storeKey, string databaseId, string reason, string sql);

    /// <remarks>
    /// Warning rather than Error: a store that will not build is the application's own configuration
    /// answering, and the studio goes on serving every other store. It is logged because the studio
    /// renders it as one disabled row in a picker, which nobody is watching. The level is the caller's:
    /// the registry caches a failure for ten seconds, and every polling page asks again after that, so
    /// the same broken store is a Warning once per <see cref="StudioLogThrottle.Window" /> and Debug in
    /// between.
    /// </remarks>
    [LoggerMessage(EventId = 9210, Message = "Marten Studio could not resolve store {StoreKey} ({ServiceType}): {Reason}")]
    public static partial void StoreUnavailable(this ILogger logger, LogLevel level, string storeKey, string serviceType, string reason);

    // --------------------------------------------------------------------------------------------------
    // 9212-9229: operational anomalies, each at the level StudioLogThrottle chose (see the remarks above)
    // --------------------------------------------------------------------------------------------------

    /// <remarks>Logged by the Overview's store cards and by the scope selector's database listing.</remarks>
    [LoggerMessage(EventId = 9213, Message = "Marten Studio could not list the databases of store {StoreKey}")]
    public static partial void StoreDatabasesUnreadable(this ILogger logger, LogLevel level, Exception exception, string storeKey);

    /// <remarks>Logged by the Overview's store cards and by the configuration screen.</remarks>
    [LoggerMessage(EventId = 9214, Message = "Marten Studio could not read the Postgres version of store {StoreKey}")]
    public static partial void PostgresVersionUnreadable(this ILogger logger, LogLevel level, Exception? exception, string storeKey);

    /// <remarks>Asked whenever the header describes a scope, which is every scope change in every circuit.</remarks>
    [LoggerMessage(EventId = 9219, Message = "Marten Studio could not discover the tenants of store {StoreKey}")]
    public static partial void TenantDiscoveryFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey);

    /// <remarks>
    /// A page's own failure handler threw while a refresh was already failing - a bug in the studio, and
    /// on a polling loop, which is why it is throttled rather than written once per tick.
    /// </remarks>
    [LoggerMessage(EventId = 9220, Message = "A Marten Studio page failed to handle a live update failure")]
    public static partial void LiveUpdateHandlerFailed(this ILogger logger, LogLevel level, Exception exception);

    /// <remarks>
    /// Tenant discovery answered, and the host's <c>StoreAuthorizationPolicy</c> threw while it was asked
    /// which of those tenants to list. A fault in the host's authorization handler, not in the database -
    /// which is why it is not 9219 - and asked on every scope change in every circuit, which is why it is
    /// throttled. The selector lists no tenant: a policy that could not answer has allowed nothing.
    /// </remarks>
    [LoggerMessage(EventId = 9221, Message = "Marten Studio could not ask the store policy which tenants of store {StoreKey}, database {DatabaseId} to list")]
    public static partial void TenantPolicyFailed(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    /// <remarks>
    /// A health check failed for a reason nobody chose: an unexpected SQLSTATE, a connection the database
    /// refused, a bug in a check. The Health page and the Overview re-check at <c>HealthInterval</c> while
    /// they are open, so the same failure comes round again; the throttle key is the check's code (or the
    /// connection, which is one failure however many checks it stops), the store, the database and the kind
    /// of failure. Expected states - a missing extension, a privilege the role lacks, a statement that ran out
    /// of its timeout - are rows the page draws and Debug lines, and never come here.
    /// </remarks>
    [LoggerMessage(EventId = 9222, Message = "Marten Studio's health check {Code} could not check store {StoreKey}, database {DatabaseId}")]
    public static partial void HealthCheckFailed(this ILogger logger, LogLevel level, Exception exception, string code, string storeKey, string databaseId);

    /// <remarks>
    /// <para>
    /// Warning, once per host start, from <c>DatabaseBrowserConfigurationNotice</c>. <c>"*"</c> in
    /// <c>BrowsableSchemas</c> is a legitimate development setting, and it is also the one line that makes
    /// every table the store's own Postgres role can select from browsable - including other applications'
    /// tables in a shared database. <c>SqlConsoleRole</c> is what narrows that, as it narrows the SQL
    /// console, so the combination of the first without the second is said out loud where an operator
    /// reads the startup log rather than discovered on a screen.
    /// </para>
    /// <para>
    /// Event ids <c>9230-9234</c> belong to the database browser; <c>9231-9234</c> are unassigned.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9230, Level = LogLevel.Warning, Message = "Marten Studio's database browser may show every schema (MartenStudioOptions.BrowsableSchemas contains \"*\") and MartenStudioOptions.SqlConsoleRole is not set, so it reads the catalog and rows as the store's own Postgres role: every table that role can select from is browsable by anyone granted MartenStudioOptions.Capabilities.BrowseDatabase. Set SqlConsoleRole to a role that can read only what the browser should show.")]
    public static partial void DatabaseBrowserOpenToEverySchemaWithoutRole(this ILogger logger);

    // --------------------------------------------------------------------------------------------------
    // 9235-9239: the database browser's row reads (DB-3)
    // --------------------------------------------------------------------------------------------------

    /// <remarks>
    /// The other half of the ring entry for opening a relation's rows, changing its filter or counting the
    /// rows a filter matches - written for every first contact under a filter and sort, whatever page it
    /// starts on, and for no page turn. The ring is
    /// readable by anyone with the read policy, so it names the relation and the sort and never a value;
    /// this line carries the filter as typed - values included - to wherever the application logs, which
    /// is the record an operator reconstructs "who looked for what" from. Information, never above: a
    /// visitor reading rows they were granted is not an anomaly.
    /// </remarks>
    [LoggerMessage(EventId = 9235, Level = LogLevel.Information, Message = "Marten Studio user {User} performed {Action} on {Relation} in store {StoreKey}, database {DatabaseId} with filter {Filter} and sort {Sort}")]
    public static partial void TableRowsRead(this ILogger logger, string user, string action, string relation, string storeKey, string databaseId, string filter, string sort);

    /// <remarks>
    /// The key values of a row the visitor opened, expanded a cell of, or followed the references of -
    /// values the ring never holds, for the same reason as 9235.
    /// </remarks>
    [LoggerMessage(EventId = 9236, Level = LogLevel.Information, Message = "Marten Studio user {User} performed {Action} on {Relation} in store {StoreKey}, database {DatabaseId} for the row with key {Key}")]
    public static partial void TableRowOpened(this ILogger logger, string user, string action, string relation, string storeKey, string databaseId, string key);

    /// <remarks>
    /// A row read Postgres or the connection refused. The visitor's own statement timeout, an unrefreshed
    /// materialized view, a privilege <c>SqlConsoleRole</c> lacks, row-level security and a filter value
    /// that does not fit its column are what the page already says in words, and are Debug. Anything else
    /// - a connection failure, an unexpected SQLSTATE - is an anomaly, and a visitor who pages on through it
    /// repeats it, so the level comes from <see cref="StudioLogThrottle" />.
    /// </remarks>
    [LoggerMessage(EventId = 9237, Message = "Marten Studio's {Action} on {Relation} in store {StoreKey}, database {DatabaseId} failed: {SqlState}")]
    public static partial void TableRowReadFailed(this ILogger logger, LogLevel level, Exception exception, string action, string relation, string storeKey, string databaseId, string? sqlState);
}
