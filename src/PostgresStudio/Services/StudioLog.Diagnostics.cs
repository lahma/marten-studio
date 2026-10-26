using Microsoft.Extensions.Logging;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services;

// W5-API's events, 9250-9254: the public diagnostics API (D31). In a file of their own so that no other packet's event block
// meets this one; the class's own documentation is StudioLog.cs's, and Postgres Studio's wording of each is in
// PostgresStudioLog.Diagnostics.cs, under the same id. 9252-9254 are unused and held for this API.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// One call of <c>IPostgresDiagnostics.GetSnapshotAsync</c>, at Debug and nowhere higher (D31: no log line above Debug
    /// carries an API result). Codes and counts only - the outcome, how many findings were included and withheld, and which
    /// sections were asked for - with the source and database the host named, never a finding's subject, a tenant, a
    /// statement or anything Postgres said. An API call is not audited: the Activity ring records what visitors did through
    /// the studio, and the host's code is not a visitor.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9250, Level = LogLevel.Debug, Message = "Marten Studio's diagnostics API read store {StoreKey}, database {DatabaseId}: {Outcome}, {Findings} findings, {Withheld} withheld, sections {Sections}")]
    public static partial void DiagnosticsSnapshotRead(this ILogger logger, string storeKey, string databaseId, string outcome, int findings, int withheld, string sections);

    /// <summary>9250 in the studio's own words.</summary>
    public static void DiagnosticsSnapshotRead(
        this ILogger logger,
        StudioProduct product,
        string storeKey,
        string databaseId,
        string outcome,
        int findings,
        int withheld,
        string sections)
    {
        if (product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioDiagnosticsSnapshotRead(storeKey, databaseId, outcome, findings, withheld, sections);
        }
        else
        {
            logger.DiagnosticsSnapshotRead(storeKey, databaseId, outcome, findings, withheld, sections);
        }
    }

    /// <remarks>
    /// <para>
    /// A section the diagnostics API was asked for - storage, statements, settings, connections - could not be read, for a
    /// reason nobody chose: a connection failure, an unexpected SQLSTATE, a reader that threw. The section says so in the
    /// studio's words and the SQLSTATE, and the snapshot is answered all the same; the exception is here. A host polls, so the
    /// level comes from <see cref="StudioLogThrottle" />: a Warning once per source, database and kind of failure in ten
    /// minutes, Debug in between. An expected state - a privilege the role lacks (42501), a statement out of its timeout
    /// (57014) or its lock wait (55P03) - is the section's own outcome, at Debug.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9251, Message = "Marten Studio's diagnostics API could not read the {Section} of store {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void DiagnosticsSectionFailed(this ILogger logger, LogLevel level, Exception exception, string section, string storeKey, string databaseId, string? sqlState);
}
