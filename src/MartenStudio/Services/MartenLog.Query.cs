using Microsoft.Extensions.Logging;

namespace MartenStudio.Services;

/// <summary>
/// F7's events: LINQ mode on the Query page, ids 9260-9264.
/// </summary>
/// <remarks>
/// <para>
/// Information, all but the advice's 9264, and none on a polled path: a LINQ query runs because somebody pressed Run. They are
/// the application log's half of the audit - the ring is gone at the next restart, and these are not. They carry the
/// alias, the predicate and the ordering cut exactly as the ring's target is (<c>QueryService.Target</c>: one line,
/// two hundred characters each), unlike 9204 and 9205, which carry a console statement whole; the literals a visitor
/// typed are in them, as a statement's are. A refusal at the capability stage carries none of the text: it reads
/// <c>(text not recorded)</c> in their place. A run is logged on the first page of its signature, as the ring records
/// it; a page turn is not a new run.
/// </para>
/// </remarks>
internal static partial class MartenLog
{
    [LoggerMessage(EventId = 9260, Level = LogLevel.Information, Message = "Marten Studio user {User} ran a LINQ query on {Alias} in store {StoreKey}, database {DatabaseId}, tenant {TenantId} in {ElapsedMilliseconds} ms returning {RowCount} rows: where {Where} order by {OrderBy}")]
    public static partial void LinqQueryExecuted(this ILogger logger, string user, string alias, string storeKey, string databaseId, string tenantId, long elapsedMilliseconds, int rowCount, string where, string orderBy);

    /// <remarks>
    /// A refusal at the screen, the parser, the guard or Marten's translation - every one of them, because the
    /// shapes the guard refuses are exactly the ones an incident review wants to find.
    /// </remarks>
    [LoggerMessage(EventId = 9261, Level = LogLevel.Information, Message = "Marten Studio refused a LINQ query from user {User} on {Alias} in store {StoreKey}, database {DatabaseId} at the {Stage} stage: {Reason}. Where {Where} order by {OrderBy}")]
    public static partial void LinqQueryRefused(this ILogger logger, string user, string alias, string storeKey, string databaseId, string stage, string reason, string where, string orderBy);

    /// <remarks>Postgres' SQLSTATE only: its words are in the result the page shows, masked.</remarks>
    [LoggerMessage(EventId = 9262, Level = LogLevel.Information, Message = "Marten Studio user {User}'s LINQ query on {Alias} in store {StoreKey}, database {DatabaseId} failed in Postgres: {SqlState}")]
    public static partial void LinqQueryFailed(this ILogger logger, string user, string alias, string storeKey, string databaseId, string sqlState);

    /// <remarks>
    /// An exception the pipeline did not expect from Marten's translation or a reflection adapter - a defect, or a
    /// Marten that moved under the studio. The page shows its type name only; the stack is here.
    /// </remarks>
    [LoggerMessage(EventId = 9263, Level = LogLevel.Information, Message = "Marten Studio could not build a LINQ query on {Alias} in store {StoreKey}")]
    public static partial void LinqQueryUnexpected(this ILogger logger, Exception exception, string alias, string storeKey);

    /// <remarks>
    /// Debug: the index advice is a courtesy on a page whose query already ran, and a catalog read that failed
    /// costs the advice and nothing else - the rows are shown, and the panel simply has no advice to draw.
    /// </remarks>
    [LoggerMessage(EventId = 9264, Level = LogLevel.Debug, Message = "Marten Studio could not read the indexes of {Table} to advise on a LINQ query")]
    public static partial void LinqAdviceUnavailable(this ILogger logger, Exception exception, string table);
}
