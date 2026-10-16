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

    /// <summary>
    /// The D01 Explain's shared read, per store and database, for a minute - kept here because this is the process's
    /// one singleton of daemon facts, and a read every circuit shares is one.
    /// </summary>
    public HighWaterExplainCache Explains { get; } = new();

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

    /// <summary>The most start refusals remembered per process, newest kept.</summary>
    internal const int MaxStartRefusals = 200;

    private readonly ConcurrentDictionary<string, StudioStartRefusal> startRefusals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Remembers that a start the studio asked for was refused, and why - which D05 shows beside the shard's state.
    /// </summary>
    /// <remarks>
    /// Keyed by store, database and shard; the newest refusal of a shard replaces the last. Bounded at
    /// <see cref="MaxStartRefusals" /> by dropping the oldest, so a page clicked all day cannot grow it.
    /// </remarks>
    public void RecordStartRefusal(StudioStartRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        startRefusals[RefusalKey(refusal.StoreKey, refusal.DatabaseId, refusal.ShardName)] = refusal;

        while (startRefusals.Count > MaxStartRefusals
               && startRefusals.OrderBy(static x => x.Value.RefusedAt).FirstOrDefault() is { Key: { } oldest })
        {
            startRefusals.TryRemove(oldest, out _);
        }
    }

    /// <summary>Forgets a shard's refusal: it has since started.</summary>
    public void ClearStartRefusal(string storeKey, string databaseId, string shardName) =>
        startRefusals.TryRemove(RefusalKey(storeKey, databaseId, shardName), out _);

    /// <summary>The refusals recorded for one store and database, by shard.</summary>
    public IReadOnlyDictionary<string, StudioStartRefusal> StartRefusals(string storeKey, string databaseId) =>
        startRefusals.Values
            .Where(x => string.Equals(x.StoreKey, storeKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.DatabaseId, databaseId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static x => x.ShardName, StringComparer.OrdinalIgnoreCase);

    private static string RefusalKey(string storeKey, string databaseId, string shardName) =>
        storeKey + "|" + databaseId + "|" + shardName;
}

/// <summary>One start the studio asked the daemon for, and the daemon did not do.</summary>
/// <param name="StoreKey">The store.</param>
/// <param name="DatabaseId">The database.</param>
/// <param name="ShardName">The shard it was asked to start.</param>
/// <param name="Reason">The classified reason (<c>ShardStartException.Reason</c>), or the studio's own (<c>Rebuilding</c>).</param>
/// <param name="IsTransient">Whether a retry is expected to resolve it, when JasperFx says.</param>
/// <param name="Message">What the visitor was told.</param>
/// <param name="RefusedAt">When.</param>
internal sealed record StudioStartRefusal(
    string StoreKey,
    string DatabaseId,
    string ShardName,
    string? Reason,
    bool? IsTransient,
    string Message,
    DateTimeOffset RefusedAt);
