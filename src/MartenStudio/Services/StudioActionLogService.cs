namespace MartenStudio.Services;

/// <summary>
/// One thing a visitor changed, as the Activity page shows it.
/// </summary>
/// <param name="Timestamp">When it happened, in UTC.</param>
/// <param name="User">Who the circuit belonged to, or <c>anonymous</c>.</param>
/// <param name="StoreKey">The store the action was aimed at.</param>
/// <param name="DatabaseId">Marten's database identity, never a connection string.</param>
/// <param name="TenantId">The tenant in scope, or <see langword="null" /> for a view spanning all of them.</param>
/// <param name="Action">What was done, named the way the page names it (for example <c>DeleteDocument</c>).</param>
/// <param name="Target">What it was done to - an alias and an id, a stream, a projection.</param>
/// <param name="Succeeded">Whether it worked. Failures are recorded too, which is the point.</param>
/// <param name="Message">The outcome, or the reason it failed.</param>
/// <param name="Capability">The capability it required, or <see langword="null" /> for something that needed none.</param>
internal sealed record StudioActionLogEntry(
    DateTimeOffset Timestamp,
    string User,
    string StoreKey,
    string DatabaseId,
    string? TenantId,
    string Action,
    string Target,
    bool Succeeded,
    string? Message,
    string? Capability)
{
    /// <summary>
    /// Where this entry sits in the ring, counting from one. Assigned by
    /// <see cref="StudioActionLogService.Record" />; zero on an entry that was never recorded.
    /// </summary>
    /// <remarks>
    /// A record has value equality, so two identical actions - the same user deleting the same document
    /// twice, or a retried failure - are the same object as far as any dictionary or Blazor <c>@key</c>
    /// is concerned. The Activity page keys its rows on this instead, because a duplicate key makes the
    /// renderer reuse the wrong row. A monotonic counter rather than a timestamp: two entries can share
    /// a timestamp, and the ring is already ordered by insertion.
    /// </remarks>
    public long Sequence { get; init; }
}

/// <summary>
/// The process's ring of recent actions, newest first.
/// </summary>
/// <remarks>
/// Singleton and bounded: this is a convenience the Activity page reads, not the record of what
/// happened - that is <see cref="StudioLog" />, on the way to the application's own logging. Five
/// hundred entries is enough to answer "what did I just do" across a few circuits and small enough that
/// an idle process never notices it.
/// </remarks>
internal sealed class StudioActionLogService
{
    /// <summary>How many entries the ring keeps before the oldest falls out.</summary>
    public const int MaxEntries = 500;

    private readonly List<StudioActionLogEntry> entries = [];
    private readonly Lock gate = new();
    private long sequence;

    /// <summary>
    /// Keeps one action, dropping the oldest once the bound is reached, and stamps it with the next
    /// <see cref="StudioActionLogEntry.Sequence" />.
    /// </summary>
    /// <remarks>
    /// The sequence is assigned here rather than by the caller so that it is the ring's own count and
    /// nothing else can forge an ordering. Under the same lock as the insert, so the order of the
    /// sequence numbers is the order of the list.
    /// </remarks>
    public void Record(StudioActionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (gate)
        {
            entries.Insert(0, entry with { Sequence = ++sequence });
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }
        }
    }

    /// <summary>The most recent <paramref name="maxCount" /> entries, newest first.</summary>
    public IReadOnlyList<StudioActionLogEntry> GetLatest(int maxCount = MaxEntries)
    {
        int safeMaxCount = Math.Clamp(maxCount, 1, MaxEntries);
        lock (gate)
        {
            return entries.Take(safeMaxCount).ToList();
        }
    }
}
