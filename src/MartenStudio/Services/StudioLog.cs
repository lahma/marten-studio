using Microsoft.Extensions.Logging;

namespace MartenStudio.Services;

/// <summary>
/// Every event Marten Studio logs, as source-generated methods with a pinned event id.
/// </summary>
/// <remarks>
/// <para>
/// Event ids <c>9200-9299</c> belong to Marten Studio. All twelve are declared here from the first
/// packet that logs any of them, so the numbers are reserved rather than assigned in the order features
/// happened to land - an operator's log query is written against the number, and renumbering one later
/// would silently change what a saved query matches.
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
    /// renders it as one disabled row in a picker, which nobody is watching.
    /// </remarks>
    [LoggerMessage(EventId = 9210, Level = LogLevel.Warning, Message = "Marten Studio could not resolve store {StoreKey} ({ServiceType}): {Reason}")]
    public static partial void StoreUnavailable(this ILogger logger, string storeKey, string serviceType, string reason);

    /// <remarks>
    /// Warning, and it counts the properties: Marten has no untyped write path, so saving an edited
    /// document round-trips through the CLR type and anything the type does not have is gone. The dialog
    /// shows the loss before the save; this is the record that it happened anyway.
    /// </remarks>
    [LoggerMessage(EventId = 9211, Level = LogLevel.Warning, Message = "Marten Studio user {User} saved {DocumentType} {Id} and the round trip dropped {DroppedCount} properties: {Dropped}")]
    public static partial void DocumentWriteRoundTripDropped(this ILogger logger, string user, string documentType, string id, int droppedCount, string dropped);
}
