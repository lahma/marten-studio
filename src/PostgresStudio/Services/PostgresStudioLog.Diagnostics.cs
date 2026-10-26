using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// W5-API's events, 9250-9254, in Postgres Studio's words: the same ids, levels and properties as StudioLog.Diagnostics.cs's.
internal static partial class PostgresStudioLog
{
    [LoggerMessage(EventId = 9250, Level = LogLevel.Debug, Message = "Postgres Studio's diagnostics API read data source {StoreKey}, database {DatabaseId}: {Outcome}, {Findings} findings, {Withheld} withheld, sections {Sections}")]
    public static partial void PostgresStudioDiagnosticsSnapshotRead(this ILogger logger, string storeKey, string databaseId, string outcome, int findings, int withheld, string sections);

    [LoggerMessage(EventId = 9251, Message = "Postgres Studio's diagnostics API could not read the {Section} of data source {StoreKey}, database {DatabaseId}: {SqlState}")]
    public static partial void PostgresStudioDiagnosticsSectionFailed(this ILogger logger, LogLevel level, Exception exception, string section, string storeKey, string databaseId, string? sqlState);
}
