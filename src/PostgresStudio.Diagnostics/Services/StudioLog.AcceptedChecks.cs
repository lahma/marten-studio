using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// 1.1's event, 9490: options validation met an AcceptedChecks entry for a retired check. In a file of its own; the class's own
// documentation is StudioLog.cs's. The line names no product, so there is no Postgres Studio wording of it.
internal static partial class StudioLog
{
    /// <remarks>
    /// An <c>AcceptedChecks</c> entry names a retired check code (K10, retired in 1.1). Startup accepts it - refusing would stop a
    /// host that did nothing wrong from starting after an upgrade - and ignores it, and says so once per code, at Information,
    /// because the host's own configuration is what to tidy. Only the code and the release, which are constants of the package;
    /// never the entry's reason.
    /// </remarks>
    [LoggerMessage(EventId = 9490, Level = LogLevel.Information, Message = "AcceptedChecks lists {Code}, which was retired in {Release} and no longer runs; the entry has no effect and can be removed.")]
    public static partial void AcceptedCheckRetired(this ILogger logger, string code, string release);
}
