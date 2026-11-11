using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace KStudio.Core.Logging;

/// <summary>
/// Logs an expected, repeatable state once per window per key (decision 5.4): a refusal by an ACL, an API a broker does not have, a
/// cluster that cannot be reached. A refusal is never a warning, and a page polling at five seconds must not write a line each time.
/// </summary>
internal sealed class KafkaLogOnce(TimeProvider timeProvider)
{
    /// <summary>The window: the same key is not logged again within it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private const int PruneThreshold = 1_024;

    private readonly ConcurrentDictionary<string, DateTimeOffset> lastLogged = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="key" /> was not logged within the window; when it was not, the key is recorded as logged now.</summary>
    /// <param name="key">What is being logged: for example the cluster, the call and the resource.</param>
    public bool ShouldLog(string key) => Acquire(key);

    /// <summary>
    /// <see cref="LogLevel.Warning" /> the first time <paramref name="key" /> is seen within the window and <see cref="LogLevel.Debug" />
    /// afterwards: a cluster that cannot be reached is worth one warning, not one per poll.
    /// </summary>
    /// <param name="once">The window's owner.</param>
    /// <param name="key">What is being logged.</param>
    /// <remarks>
    /// Static, and named as the studios' <c>StudioLogThrottle.WarningOrDebug</c> is, because KStudio.Core cannot reference the
    /// Diagnostics assembly (the fence) and the log-level roster test recognises a throttle by that name.
    /// </remarks>
    public static LogLevel WarningOrDebug(KafkaLogOnce once, string key) => once.Acquire(key) ? LogLevel.Warning : LogLevel.Debug;

    private bool Acquire(string key)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool first = false;

        lastLogged.AddOrUpdate(
            key,
            _ =>
            {
                first = true;
                return now;
            },
            (_, previous) =>
            {
                if (now - previous >= Window)
                {
                    first = true;
                    return now;
                }

                return previous;
            });

        if (first && lastLogged.Count > PruneThreshold)
        {
            foreach (KeyValuePair<string, DateTimeOffset> pair in lastLogged)
            {
                if (now - pair.Value >= Window)
                {
                    lastLogged.TryRemove(pair);
                }
            }
        }

        return first;
    }
}
