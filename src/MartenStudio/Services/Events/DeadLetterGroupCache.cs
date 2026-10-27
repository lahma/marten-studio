using Microsoft.Extensions.Options;

using PostgresStudio.Services.Live;

namespace MartenStudio.Services.Events;

/// <summary>
/// The grouped dead-letter view's single-flight snapshot cache (D10): one read per store, database and tenant per
/// refresh interval, however many circuits are looking.
/// </summary>
/// <remarks>
/// <para>
/// A singleton of its own rather than the shared <see cref="StudioSnapshotCache" />, because its time to live is the
/// studio's <c>RefreshInterval</c> rather than that cache's one second: the read takes every letter in scope up to its
/// bound and fingerprints each, and a person pressing Refresh, or five circuits opening the view, must not each read them
/// all again (M3). The view says when it was read.
/// </para>
/// <para>
/// The key is the scope as it resolved - the store's registered key, the database's own identity and the tenant - and
/// nothing about the visitor, which is the cache's own rule: the scope is resolved, and every policy it carries applied,
/// before the cache is reached, and the per-visitor part (which tenants a visitor may see named) is applied after it.
/// A dead-letter write invalidates every tenant's entry for its database, so the view after a skip is read again.
/// </para>
/// </remarks>
internal sealed class DeadLetterGroupCache
{
    private readonly StudioSnapshotCache cache;

    private readonly int lettersGrouped = DeadLetterGroupLimits.MaxLettersGrouped;

    public DeadLetterGroupCache(IOptions<MartenStudioOptions> options)
        : this(TimeProvider.System, options.Value.RefreshInterval)
    {
    }

    internal DeadLetterGroupCache(TimeProvider timeProvider, TimeSpan timeToLive) =>
        cache = new StudioSnapshotCache(timeProvider, timeToLive > StudioSnapshotCache.DefaultTimeToLive ? timeToLive : StudioSnapshotCache.DefaultTimeToLive);

    /// <summary>
    /// The most letters one read groups, newest first - and the window the list opened on a group finds that group's letters
    /// in, so the list shows the letters the group counts and never fingerprints more (SEC-FIX).
    /// <see cref="DeadLetterGroupLimits.MaxLettersGrouped" /> in a host; a test sets fewer, and never more.
    /// </summary>
    internal int LettersGrouped
    {
        get => lettersGrouped;
        init => lettersGrouped = Math.Clamp(value, 1, DeadLetterGroupLimits.MaxLettersGrouped);
    }

    /// <summary>The groups of <paramref name="resolved" />'s scope, read by <paramref name="read" /> at most once per interval.</summary>
    public Task<DeadLetterGroupList> GetAsync(
        ResolvedScope resolved,
        Func<CancellationToken, Task<DeadLetterGroupList>> read,
        CancellationToken cancellationToken) =>
        cache.GetAsync(Key(resolved), read, cancellationToken);

    /// <summary>Forgets every tenant's groups of <paramref name="resolved" />'s database.</summary>
    public void InvalidateDatabase(ResolvedScope resolved) => cache.InvalidatePrefix(Prefix(resolved));

    private static string Prefix(ResolvedScope resolved) =>
        resolved.Registration.Key + "\u001f" + resolved.Database.Id.Identity + "\u001f";

    private static string Key(ResolvedScope resolved) => Prefix(resolved) + (resolved.TenantId ?? string.Empty) + "\u001fgroups";
}
