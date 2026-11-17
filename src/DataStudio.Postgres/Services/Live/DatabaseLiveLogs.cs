using DataStudio.Services;

using Microsoft.Extensions.Logging;

namespace DataStudio.Postgres.Services.Live;

/// <summary>
/// The live-update loop's log lines: the <see cref="StudioLog" /> methods, in the one wording every module's events have.
/// </summary>
/// <remarks>
/// The shell's loop names no throttle: the delegate it calls takes the logger and the exception only, and the database module closes over the
/// <see cref="StudioLogThrottle" /> it resolved from its own container, where the level of event 9220 is taken next to the call.
/// </remarks>
internal static class DatabaseLiveLogs
{
    /// <summary>The throttle's key for event 9220, one per kind of failure per window.</summary>
    private const string FailedHandlerKey = "LiveUpdates.FailedHandler";

    /// <summary>The live-update loop's log lines.</summary>
    /// <param name="throttle">The container's throttle, or <see langword="null" /> for a caller a test built without one (a failing handler is then a Warning).</param>
    public static StudioLiveLog For(StudioLogThrottle? throttle = null) => new()
    {
        LiveUpdateFailed = static (logger, exception) => logger.LogDebug(exception, "A Data Studio live update failed"),
        LiveUpdateHandlerFailed = (logger, exception) =>
        {
            // A bug in a page, on a loop that runs every RefreshInterval while the read keeps failing: event 9220, a Warning once per kind of failure per window.
            LogLevel level = StudioLogThrottle.LevelOrWarning(throttle, FailedHandlerKey, null, null, StudioLogThrottle.KindOf(exception));
            logger.LiveUpdateHandlerFailed(level, exception);
        },
    };
}
