using System.Collections.Concurrent;

namespace MartenStudio.Services.Projections;

/// <summary>
/// One pause the studio itself asked the projection coordinator for.
/// </summary>
/// <param name="StoreKey">The store whose coordinator is paused. A coordinator is per store, not per database.</param>
/// <param name="User">Who asked, as the audit entry names them.</param>
/// <param name="PausedAt">When they asked, in UTC.</param>
/// <param name="DatabaseId">The database whose page the pause was issued from - a scope, not a limit.</param>
/// <param name="TenantId">The tenant that scope named, when it named one.</param>
internal sealed record StudioDaemonPause(
    string StoreKey,
    string User,
    DateTimeOffset PausedAt,
    string DatabaseId,
    string? TenantId);

/// <summary>
/// The pauses Marten Studio issued, remembered because the coordinator will not remember them for us.
/// </summary>
/// <remarks>
/// <para>
/// <c>IProjectionCoordinator</c> has <c>PauseAsync()</c> and <c>ResumeAsync()</c> and no public "am I
/// paused" of any kind (verified by decompiling JasperFx.Events 2.69.3's
/// <c>ProjectionCoordinatorBase</c>: the paused state is the private <c>_cancellation</c>/<c>_runner</c>
/// pair). So the studio records what <em>it</em> did and says only that: "Paused by Marten Studio
/// (user, time)". It deliberately does not infer a pause from "this daemon has had no agents for a
/// while" - a daemon with no agents is also what a store with no async projections looks like, what a
/// HotCold node that lost the election looks like, and what a daemon that is still starting looks like.
/// Saying what we know beats guessing what we do not.
/// </para>
/// <para>
/// Keyed by store key, because that is the granularity the operation has: a store's coordinator holds
/// one leadership runner and fans <c>StopAllAsync</c> out over every daemon it has resolved, so a pause
/// issued from one database's page stops the agents of every database that store hosts in this process.
/// The database and tenant of the issuing scope are kept for the record, not as a key.
/// </para>
/// <para>
/// A singleton, and correct as one: the pause is a fact about this process, and the page that reads it
/// back is a different circuit from the one that issued it as often as not. Nothing here survives a
/// restart - and nothing should, because a restarted process has a freshly started coordinator whose
/// agents are running again.
/// </para>
/// </remarks>
internal sealed class DaemonControlState
{
    private readonly ConcurrentDictionary<string, StudioDaemonPause> pauses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pause recorded for <paramref name="storeKey" />, or <see langword="null" />.</summary>
    public StudioDaemonPause? Find(string? storeKey) =>
        storeKey is { Length: > 0 } key && pauses.TryGetValue(key, out StudioDaemonPause? pause) ? pause : null;

    /// <summary>
    /// Remembers that this process paused <paramref name="pause" />'s store.
    /// </summary>
    /// <remarks>
    /// Last writer wins on purpose: two people pausing the same store is one paused store, and the
    /// second pause is the one whose name should be on it.
    /// </remarks>
    public void RecordPause(StudioDaemonPause pause)
    {
        ArgumentNullException.ThrowIfNull(pause);
        pauses[pause.StoreKey] = pause;
    }

    /// <summary>Forgets the pause for <paramref name="storeKey" />, if there was one.</summary>
    /// <returns><see langword="true" /> when something was actually forgotten.</returns>
    public bool ClearPause(string? storeKey) =>
        storeKey is { Length: > 0 } key && pauses.TryRemove(key, out _);

    /// <summary>How many stores this process has paused. For a test, and for nothing else.</summary>
    internal int Count => pauses.Count;
}
