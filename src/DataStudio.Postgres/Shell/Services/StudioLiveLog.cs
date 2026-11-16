using Microsoft.Extensions.Logging;

namespace DataStudio.Services;

/// <summary>
/// The lines the live-update loop logs, in the words of the studio that registered the shell: one delegate per log call, each named after the event it
/// logs and taking the logger extension's own arguments (<see cref="ILogger" />, then the event's).
/// </summary>
/// <remarks>
/// The loop is the shell's and the words are the studio's: the studio that registers the shell points each delegate at its own logger extensions
/// (the very methods the loop called before this type existed), so event ids, levels, templates and properties are the studio's and the shell names
/// no product.
/// </remarks>
internal sealed class StudioLiveLog
{
    /// <summary>A tick threw: logged at Debug, because the page shows the failure and the loop carries on.</summary>
    public required Action<ILogger, Exception> LiveUpdateFailed { get; init; }

    /// <summary>
    /// A page's handler for a failed tick threw (event 9220). The loop hands over the throttle it was given (or <see langword="null" />) and the studio
    /// takes the level from it next to the call, a Warning once per kind of failure per window, as every call of a throttled event does
    /// (<c>LogLevelsTests</c> follows the level only where the call is).
    /// </summary>
    public required Action<ILogger, DataStudio.Postgres.Services.StudioLogThrottle?, Exception> LiveUpdateHandlerFailed { get; init; }
}
