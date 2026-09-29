using Microsoft.Extensions.Logging;

namespace MartenStudio.Services;

/// <summary>
/// Every event Marten Studio logs, as source-generated methods with a pinned event id.
/// </summary>
/// <remarks>
/// <para>
/// Event ids <c>9200-9299</c> belong to Marten Studio. The first twelve (9200-9211) are the audit events
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

    [LoggerMessage(EventId = 9206, Level = LogLevel.Information, Message = "Marten Studio user {User} applied schema changes to store {StoreKey}, database {DatabaseId}: {Summary}")]
    public static partial void SchemaChangeApplied(this ILogger logger, string user, string storeKey, string databaseId, string summary);

    [LoggerMessage(EventId = 9207, Level = LogLevel.Information, Message = "Marten Studio user {User} started a rebuild of projection {Projection} in store {StoreKey}, database {DatabaseId}")]
    public static partial void ProjectionRebuildStarted(this ILogger logger, string user, string projection, string storeKey, string databaseId);

    [LoggerMessage(EventId = 9208, Level = LogLevel.Information, Message = "Rebuild of projection {Projection} in store {StoreKey}, database {DatabaseId} finished after {ElapsedMilliseconds} ms: {Outcome}")]
    public static partial void ProjectionRebuildFinished(this ILogger logger, string projection, string storeKey, string databaseId, long elapsedMilliseconds, string outcome);

    [LoggerMessage(EventId = 9209, Level = LogLevel.Information, Message = "Marten Studio user {User} requested daemon control {Operation} on {Target} in store {StoreKey}, database {DatabaseId}")]
    public static partial void DaemonControlRequested(this ILogger logger, string user, string operation, string target, string storeKey, string databaseId);

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

    /// <remarks>
    /// Warning, and it counts the properties: Marten has no untyped write path, so saving an edited
    /// document round-trips through the CLR type and anything the type does not have is gone. The dialog
    /// shows the loss before the save; this is the record that it happened anyway.
    /// </remarks>
    [LoggerMessage(EventId = 9211, Level = LogLevel.Warning, Message = "Marten Studio user {User} saved {DocumentType} {Id} and the round trip dropped {DroppedCount} properties: {Dropped}")]
    public static partial void DocumentWriteRoundTripDropped(this ILogger logger, string user, string documentType, string id, int droppedCount, string dropped);

    // --------------------------------------------------------------------------------------------------
    // 9212-9229: operational anomalies, each at the level StudioLogThrottle chose (see the remarks above)
    // --------------------------------------------------------------------------------------------------

    /// <remarks>
    /// A coordinator <em>is</em> registered and answered with something other than the two expected
    /// shapes, or could not be constructed for a reason other than the expected one. Wolverine's managed
    /// distribution throws <c>NotSupportedException</c> from the per-database lookup by design, and its
    /// constructor throws <c>ArgumentOutOfRangeException</c> for a store its agent family does not know;
    /// neither comes here. Two throttle sites share this event - the lookup and the construction - so
    /// one does not silence the other.
    /// </remarks>
    [LoggerMessage(EventId = 9212, Message = "Marten Studio could not reach the async daemon of store {StoreKey}, database {DatabaseId}")]
    public static partial void DaemonUnreachable(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    /// <remarks>Logged by the Overview's store cards and by the scope selector's database listing.</remarks>
    [LoggerMessage(EventId = 9213, Message = "Marten Studio could not list the databases of store {StoreKey}")]
    public static partial void StoreDatabasesUnreadable(this ILogger logger, LogLevel level, Exception exception, string storeKey);

    /// <remarks>Logged by the Overview's store cards and by the configuration screen.</remarks>
    [LoggerMessage(EventId = 9214, Message = "Marten Studio could not read the Postgres version of store {StoreKey}")]
    public static partial void PostgresVersionUnreadable(this ILogger logger, LogLevel level, Exception? exception, string storeKey);

    /// <remarks>
    /// The page falls back to the database rows it was reading anyway, so nothing on screen is wrong -
    /// only less fresh between polls.
    /// </remarks>
    [LoggerMessage(EventId = 9215, Message = "Marten Studio could not observe the shard state tracker of store {StoreKey}, database {DatabaseId}")]
    public static partial void ShardTrackerUnobservable(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    /// <remarks>Polled by the Overview's projection tiles and by the navigation badges.</remarks>
    [LoggerMessage(EventId = 9216, Message = "Marten Studio could not summarise the projections of store {StoreKey}, database {DatabaseId}")]
    public static partial void ProjectionSummaryUnreadable(this ILogger logger, LogLevel level, Exception exception, string storeKey, string databaseId);

    /// <remarks>
    /// Every event-store read renders its failure as a value through one describer, and this is its log
    /// line. A timeout - 57014, or the <c>NpgsqlException</c> wrapping a <c>TimeoutException</c> that the
    /// same expiry produces when the backend is slow to answer the cancel - is the page's business and is
    /// Debug. A table or schema that is not there (42P01, 3F000) comes here: every read asks the column
    /// catalog, or <c>DeadLetterTableExistsAsync</c>, before it touches a table, so an event store that
    /// has not been created yet is an empty answer and never this, and one of those SQLSTATEs means the
    /// catalog and the database disagree. So does a connection failure and any other SQLSTATE.
    /// </remarks>
    [LoggerMessage(EventId = 9217, Message = "Marten Studio could not {What} in store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void EventReadFailed(this ILogger logger, LogLevel level, Exception exception, string what, string storeKey, string databaseId, string? sqlState);

    /// <remarks>
    /// <c>mt_streams."timestamp"</c> is <c>NOT NULL</c> in every schema Marten creates, so this is a table
    /// somebody migrated by hand - worth one line, and not one per page of the stream list.
    /// </remarks>
    [LoggerMessage(EventId = 9218, Message = "Marten Studio stopped paging the stream list: {Schema}.mt_streams has a row with a null timestamp, which no Marten-created schema has and which a keyset cannot page past")]
    public static partial void StreamTimestampMissing(this ILogger logger, LogLevel level, string schema);

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
}
