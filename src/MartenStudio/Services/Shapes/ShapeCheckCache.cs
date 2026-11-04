using System.Collections.Concurrent;

using PostgresStudio.Services.Live;

namespace MartenStudio.Services.Shapes;

/// <summary>
/// The shape check's single-flight cache (D-SHAPE S9): one check per resolved scope and subject per five minutes, however many
/// circuits and presses ask.
/// </summary>
/// <remarks>
/// <para>
/// A singleton over <see cref="StudioSnapshotCache" />. <b>Single-flight per key:</b> a second press while one check runs waits for
/// that run's result rather than starting another read, and a caller's cancellation ends only its own wait - the check carries
/// on to its own budget (<see cref="ShapeCheckLimits.Budget" />) for everyone else.
/// </para>
/// <para>
/// <b>Shared between visitors of the same resolved scope</b>, which is <c>DeadLetterGroupCache</c>'s rule: the scope is resolved,
/// and every policy it carries applied, before the cache is reached, and nothing in a result depends on the visitor. The result
/// holds ids, never hrefs; each panel builds its links from its own visitor's scope. This is not D29's sweep: nothing runs
/// without a visitor.
/// </para>
/// <para>
/// "Check again" (<c>force</c>) runs again only once the cached result is <see cref="ShapeCheckLimits.RecheckAfter" /> old, and
/// never while a check for the key is running: pressing it twice is one read.
/// </para>
/// </remarks>
internal sealed class ShapeCheckCache
{
    private const int PruneThreshold = 256;

    private readonly StudioSnapshotCache cache;
    private readonly TimeProvider clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> finished = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> running = new(StringComparer.Ordinal);

    /// <summary>The cache a host's container holds: the system clock.</summary>
    public ShapeCheckCache()
        : this(TimeProvider.System)
    {
    }

    internal ShapeCheckCache(TimeProvider timeProvider)
    {
        clock = timeProvider;
        cache = new StudioSnapshotCache(timeProvider, ShapeCheckLimits.TimeToLive);
    }

    /// <summary>How many keys hold a result or a run, for a test.</summary>
    internal int Count => cache.Count;

    /// <summary>
    /// The key of one check: the resolved scope - the store's registered key, the database's own identity and the tenant - the
    /// kind of check and its subject, separated by U+001F.
    /// </summary>
    /// <remarks>
    /// <b>The key has no visitor in it, on purpose.</b> That is sound because <c>MartenStudioOptions.IsDocumentTypeVisible</c> is a
    /// <c>Func&lt;Type, bool&gt;</c> (<c>MartenStudioOptions.cs:261</c>): visibility does not depend on who is asking, so two
    /// visitors with the same resolved scope see the same rows. <b>If it ever takes the visitor, this sharing must go</b> and the
    /// visitor's identity, or what the predicate said, must join the key.
    /// </remarks>
    /// <param name="resolved">The scope as it resolved.</param>
    /// <param name="kind"><c>documents</c> or <c>events</c>.</param>
    /// <param name="subject">The alias lower-cased invariant, or the event type name as stored.</param>
    public static string KeyOf(ResolvedScope resolved, string kind, string subject)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(subject);

        return resolved.Registration.Key
            + "\u001f" + resolved.Database.Id.Identity
            + "\u001f" + (resolved.TenantId ?? string.Empty)
            + "\u001f" + kind
            + "\u001f" + subject;
    }

    /// <summary>The result for <paramref name="subject" /> in <paramref name="resolved" />'s scope, read by <paramref name="read" /> at most once per five minutes.</summary>
    /// <param name="resolved">The scope as it resolved: every policy applied already.</param>
    /// <param name="kind"><c>documents</c> or <c>events</c>.</param>
    /// <param name="subject">The alias or event type name.</param>
    /// <param name="force">Run again, if the cached result is at least <see cref="ShapeCheckLimits.RecheckAfter" /> old and nothing is running.</param>
    /// <param name="read">The check. Runs under no caller's token; its own budget ends it.</param>
    /// <param name="cancellationToken">Cancels this caller's wait.</param>
    public Task<ShapeCheckResult> GetAsync(
        ResolvedScope resolved,
        string kind,
        string subject,
        bool force,
        Func<CancellationToken, Task<ShapeCheckResult>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        string key = KeyOf(resolved, kind, subject);

        if (force
            && !running.ContainsKey(key)
            && finished.TryGetValue(key, out DateTimeOffset at)
            && clock.GetUtcNow() - at >= ShapeCheckLimits.RecheckAfter)
        {
            cache.Invalidate(key);
        }

        return cache.GetAsync(
            key,
            async token =>
            {
                running[key] = 0;

                try
                {
                    ShapeCheckResult result = await read(token).ConfigureAwait(false);

                    if (result.Error is not null)
                    {
                        // A failure (busy, could not connect, an SQLSTATE) is what the callers waiting on this run are given, and
                        // nobody else: the entry goes, so the next press runs again and the re-check refusal never applies to it.
                        cache.Invalidate(key);
                        finished.TryRemove(key, out _);
                    }
                    else
                    {
                        Remember(key, result.CheckedAt);
                    }

                    return result;
                }
                finally
                {
                    running.TryRemove(key, out _);
                }
            },
            cancellationToken);
    }

    private void Remember(string key, DateTimeOffset at)
    {
        finished[key] = at;

        if (finished.Count <= PruneThreshold)
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();

        foreach (KeyValuePair<string, DateTimeOffset> pair in finished)
        {
            if (now - pair.Value >= ShapeCheckLimits.TimeToLive)
            {
                finished.TryRemove(pair);
            }
        }
    }
}
