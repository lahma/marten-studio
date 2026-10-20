using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// The events only PostgresStudio logs: what its source provider leaves out of a listing. Ids 9300-9309.
/// </summary>
/// <remarks>
/// Everything else PostgresStudio's sources log is a shared event with its shared id: a source that will not build is
/// 9210, a source whose databases cannot be listed is 9213, each throttled as every studio throttles it.
/// </remarks>
internal static partial class PostgresLog
{
    /// <summary>
    /// The container holds an unkeyed <c>NpgsqlDataSource</c> that is not a singleton, so it is not listed. Said once,
    /// at the first listing, at Information: it is the host's configuration, not a fault.
    /// </summary>
    [LoggerMessage(EventId = 9300, Level = LogLevel.Information, Message = "Postgres Studio does not list the application's unkeyed NpgsqlDataSource: it is registered as {Lifetime}, and only a singleton is a data source the studio browses, because resolving one per circuit would build a pool per visitor")]
    public static partial void DataSourceLifetimeSkipped(this ILogger logger, string lifetime);

    /// <summary>
    /// A host's database source listed names the studio left out - invalid, a duplicate, another source's, or past the
    /// cap - counted and never named. Throttled: the list is read again every minute in every circuit that looks.
    /// </summary>
    [LoggerMessage(EventId = 9301, Message = "Postgres Studio left {Count} of the databases source {SourceKey} listed out: {Reason}")]
    public static partial void DatabaseNamesSkipped(this ILogger logger, LogLevel level, int count, string sourceKey, string reason);
}
