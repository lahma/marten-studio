using Microsoft.Extensions.Logging;

namespace DataStudio.Services;

/// <summary>
/// The shell's own words for the live-update loop (<see cref="StudioLiveLog" />), registered when a host adds the shell, so that a host with no
/// database module has one: a database module replaces it with its own, in its own words and with the container's throttle.
/// </summary>
/// <remarks>
/// Event 9220 is a Warning the first time per kind of failure per ten minutes and Debug in between, the loop runs every <c>RefreshInterval</c> while
/// a read keeps failing. The throttle is private to this class: a dictionary of last-warned times under a lock, read through a
/// <see cref="TimeProvider" />, bounded so that a stream of distinct failures cannot grow it.
/// </remarks>
internal static partial class ShellLiveLog
{
    /// <summary>How long a kind of failure stays at Debug after it was logged as a Warning.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>How many kinds of failure the throttle remembers at once.</summary>
    private const int MaxKinds = 256;

    /// <summary>The shell's live-update log, throttled by <paramref name="clock" />.</summary>
    /// <param name="clock">The clock the throttle's window is measured on.</param>
    public static StudioLiveLog Create(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        Dictionary<string, DateTimeOffset> lastWarned = new(StringComparer.Ordinal);
        Lock gate = new();

        return new StudioLiveLog
        {
            LiveUpdateFailed = static (logger, exception) => logger.LogDebug(exception, "A Data Studio live update failed"),
            LiveUpdateHandlerFailed = (logger, exception) =>
            {
                // A bug in a page, on a loop that runs every RefreshInterval while the read keeps failing: a Warning once per kind of failure per window.
                LogLevel level = LevelOf(exception.GetType().FullName ?? exception.GetType().Name);
                logger.LiveUpdateHandlerFailed(level, exception);
            },
        };

        LogLevel LevelOf(string kind)
        {
            DateTimeOffset now = clock.GetUtcNow();

            lock (gate)
            {
                if (lastWarned.TryGetValue(kind, out DateTimeOffset warnedAt) && now - warnedAt < Window)
                {
                    return LogLevel.Debug;
                }

                if (lastWarned.Count >= MaxKinds && !lastWarned.ContainsKey(kind))
                {
                    foreach (string expired in lastWarned.Where(x => now - x.Value >= Window).Select(static x => x.Key).ToArray())
                    {
                        lastWarned.Remove(expired);
                    }

                    if (lastWarned.Count >= MaxKinds)
                    {
                        lastWarned.Remove(lastWarned.MinBy(static x => x.Value).Key);
                    }
                }

                lastWarned[kind] = now;
                return LogLevel.Warning;
            }
        }
    }

    [LoggerMessage(EventId = 9220, Message = "A Data Studio page failed to handle a live update failure")]
    private static partial void LiveUpdateHandlerFailed(this ILogger logger, LogLevel level, Exception exception);
}
