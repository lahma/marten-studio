using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// H7's events, 9280-9284: the statement checks over pg_stat_statements (T02, T22, T23). In a file of their own so that
// the wave-3 packets' event blocks never meet in one file; the class's own documentation is StudioLog.cs's.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// Debug, always: T02 could not plan one of the store's statements - the console role may not read a table it names
    /// (42501), a placeholder's type cannot be inferred (42P18), a lock or a timeout - which is that statement's
    /// evidence on the page ("Not planned: Postgres refused the plan (SQLSTATE …)") and never the check's failure. The
    /// statement's text is not in the message: it is another program's, and a log line is read by more people than the
    /// page that masks it. Its <c>queryid</c> finds it in <c>pg_stat_statements</c>.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9280, Level = LogLevel.Debug, Message = "Marten Studio's health check T02 could not plan statement {QueryId} of store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void StatementPlanFailed(this ILogger logger, Exception exception, long queryId, string storeKey, string databaseId, string? sqlState);

    /// <remarks>
    /// <para>
    /// Debug: T02 could not take its plan's prepared statement back - neither in a read-only transaction nor, once more,
    /// outside one - so the session it ran on keeps the one prepared name until Npgsql resets that session with
    /// <c>DISCARD ALL</c> or the studio's next plan there deallocates it (<c>ExpensiveStatementsCheck.ForgetAsync</c>).
    /// Not a Warning: what is left is a prepared read that runs nothing unless somebody executes it by name, and it
    /// clears itself; the line exists so that somebody who finds the name in <c>pg_prepared_statements</c> can find
    /// why.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9281, Level = LogLevel.Debug, Message = "Marten Studio's health check T02 could not take back the prepared statement {PreparedName} of its plan of statement {QueryId} (store {StoreKey}, database {DatabaseId}); that session keeps the name until it is reset or planned on again")]
    public static partial void StatementPlanNotTakenBack(this ILogger logger, Exception exception, long queryId, string storeKey, string databaseId, string preparedName);
}
