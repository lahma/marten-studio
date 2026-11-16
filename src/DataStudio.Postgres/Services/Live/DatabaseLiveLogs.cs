using DataStudio.Services;

using Microsoft.Extensions.Logging;

using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Services.Live;

/// <summary>
/// The live-update loop's log lines in each studio's words: Marten Studio's are the <see cref="StudioLog" /> methods and Postgres Studio's the
/// <see cref="PostgresStudioLog" /> ones, the very calls the loop made while it branched on the product itself, so the events, levels and templates did not move.
/// </summary>
/// <remarks>
/// The shell's loop names no throttle: the delegate it calls takes the logger and the exception only, and the database module closes over the
/// <see cref="StudioLogThrottle" /> it resolved from its own container, where the level of event 9220 is taken next to the call.
/// </remarks>
internal static class DatabaseLiveLogs
{
    /// <summary>The throttle's key for event 9220, one per kind of failure per window, whichever product logs it.</summary>
    private const string FailedHandlerKey = "LiveUpdates.FailedHandler";

    /// <summary>Marten Studio's words.</summary>
    /// <param name="throttle">The container's throttle, or <see langword="null" /> for a caller a test built without one (a failing handler is then a Warning).</param>
    public static StudioLiveLog MartenStudio(StudioLogThrottle? throttle = null) => new()
    {
        LiveUpdateFailed = static (logger, exception) => logger.LogDebug(exception, "A Marten Studio live update failed"),
        LiveUpdateHandlerFailed = (logger, exception) =>
        {
            // A bug in a page, on a loop that runs every RefreshInterval while the read keeps failing: event 9220, a Warning once per kind of failure per window.
            LogLevel level = StudioLogThrottle.LevelOrWarning(throttle, FailedHandlerKey, null, null, StudioLogThrottle.KindOf(exception));
            logger.LiveUpdateHandlerFailed(level, exception);
        },
    };

    /// <summary>Postgres Studio's words.</summary>
    /// <param name="throttle">The container's throttle, or <see langword="null" /> for a caller a test built without one (a failing handler is then a Warning).</param>
    public static StudioLiveLog PostgresStudio(StudioLogThrottle? throttle = null) => new()
    {
        LiveUpdateFailed = static (logger, exception) => logger.LogDebug(exception, "A Postgres Studio live update failed"),
        LiveUpdateHandlerFailed = (logger, exception) =>
        {
            LogLevel level = StudioLogThrottle.LevelOrWarning(throttle, FailedHandlerKey, null, null, StudioLogThrottle.KindOf(exception));
            logger.PostgresStudioLiveUpdateHandlerFailed(level, exception);
        },
    };

    /// <summary>The words of <paramref name="product" />.</summary>
    /// <param name="product">The studio that registered the shell.</param>
    /// <param name="throttle">The container's throttle, or <see langword="null" /> for a caller a test built without one.</param>
    public static StudioLiveLog For(StudioProduct product, StudioLogThrottle? throttle = null) =>
        product == StudioProduct.PostgresStudio ? PostgresStudio(throttle) : MartenStudio(throttle);
}
