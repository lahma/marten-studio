using System.Globalization;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Query;

/// <summary>
/// Turns a Postgres <c>SQLSTATE</c> into the sentence the page shows above Postgres' own message.
/// </summary>
/// <remarks>
/// Two of these carry real information a person cannot get from the raw message. <c>25006</c> is what the
/// read-only transaction produces, and saying so out loud is the difference between "the studio is
/// broken" and "the studio is doing exactly what D13 says it does". <c>57014</c> is the statement timeout,
/// and naming the number of seconds turns it into something the host can change
/// (<see cref="MartenStudioOptions.QueryTimeout" />) rather than a mystery.
/// </remarks>
internal static class QuerySqlErrors
{
    /// <summary>Postgres' <c>read_only_sql_transaction</c>.</summary>
    public const string ReadOnlyTransaction = "25006";

    /// <summary>Postgres' <c>query_canceled</c>, which is what <c>statement_timeout</c> raises.</summary>
    public const string QueryCanceled = "57014";

    /// <summary>Postgres' <c>lock_not_available</c>, which is what <c>lock_timeout</c> raises.</summary>
    public const string LockNotAvailable = "55P03";

    /// <summary>Postgres' <c>insufficient_privilege</c>, which is what <c>SET LOCAL ROLE</c> narrows to.</summary>
    public const string InsufficientPrivilege = "42501";

    /// <summary>The headline for <paramref name="error" />, or <see langword="null" /> when the raw message says it all.</summary>
    public static string? Describe(SqlError? error, TimeSpan statementTimeout)
    {
        if (error is null)
        {
            return null;
        }

        return error.SqlState switch
        {
            ReadOnlyTransaction =>
                "Refused by the read-only transaction. Everything the SQL console runs is inside " +
                "BEGIN; SET TRANSACTION READ ONLY, so a statement that writes changes nothing.",
            QueryCanceled =>
                "Statement timed out after " +
                statementTimeout.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) +
                " s. MartenStudioOptions.QueryTimeout is what sets that.",
            LockNotAvailable =>
                "Gave up waiting for a lock. The console never queues behind a migration on purpose.",
            InsufficientPrivilege =>
                "The role this statement ran as may not read that. " +
                "MartenStudioOptions.SqlConsoleRole is what narrows it.",
            _ => null,
        };
    }

    /// <summary>
    /// The one-line summary that goes into the audit ring and the toast: the SQLSTATE and the message.
    /// </summary>
    public static string Summarize(SqlError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.SqlState + ": " + error.MessageText;
    }
}
