using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Live;

/// <summary>
/// Why a since-mark stopped counting: a sentence with one moment in it, which the page spells in its visitor's time zone -
/// the service layer returns the moment, never a formatted time (1.2-H-A-FIX).
/// </summary>
/// <param name="Before">The words before the moment.</param>
/// <param name="At">The moment.</param>
/// <param name="After">The words after it.</param>
internal sealed record SinceMarkEnd(string Before, DateTimeOffset At, string After = "")
{
    /// <summary>The sentence, with the moment spelt by <paramref name="when" />.</summary>
    /// <param name="when">How the page spells a moment.</param>
    public string Text(Func<DateTimeOffset, string> when)
    {
        ArgumentNullException.ThrowIfNull(when);
        return Before + when(At) + After;
    }
}

/// <summary>
/// The since-marks of one circuit (1.2-H-A, X6): "count from now" on Statements, Storage and Maintenance - the counters a page
/// showed when its visitor pressed Mark, kept so the page can show what changed since.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per circuit, in memory, nothing written anywhere.</b> Scoped, so one instance per circuit: no browser storage, no
/// interop call, no server-wide cache, and nothing another circuit can read. It ends with the circuit (a reload, a closed tab)
/// and survives moving between pages and between databases. At most <see cref="Capacity" /> marks are kept, the one least
/// recently set dropped first.
/// </para>
/// <para>
/// <b>Numbers only.</b> A mark holds counters, keyed by a statement's <c>queryid</c> or a name the page showed the visitor
/// (<c>schema.table</c>) - never a statement's text and never a row the page withheld. The three pages are database-wide, so a
/// mark is keyed by the page, the store's key and the database as the active scope spells them, and the tenant is ignored.
/// </para>
/// </remarks>
internal sealed class SinceMarks
{
    /// <summary>How many marks a circuit keeps.</summary>
    public const int Capacity = 16;

    private readonly Lock gate = new();
    private readonly Dictionary<(string Page, string StoreKey, string DatabaseId), (object Mark, long Tick)> marks = [];
    private long tick;

    /// <summary>The page's mark for the scope's database, or <see langword="null" /> when there is none (or it is another type).</summary>
    /// <typeparam name="T">The page's mark type.</typeparam>
    /// <param name="page">The page's name.</param>
    /// <param name="scope">The active scope; its tenant is ignored.</param>
    public T? Get<T>(string page, StudioScope scope)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(scope);

        lock (gate)
        {
            return marks.TryGetValue(KeyOf(page, scope), out var held) ? held.Mark as T : null;
        }
    }

    /// <summary>Keeps <paramref name="mark" /> for the page and the scope's database, replacing any, and dropping the least recently set mark past the bound.</summary>
    /// <typeparam name="T">The page's mark type.</typeparam>
    /// <param name="page">The page's name.</param>
    /// <param name="scope">The active scope; its tenant is ignored.</param>
    /// <param name="mark">The mark.</param>
    public void Set<T>(string page, StudioScope scope, T mark)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(mark);

        lock (gate)
        {
            marks[KeyOf(page, scope)] = (mark, ++tick);

            while (marks.Count > Capacity)
            {
                var oldest = marks.MinBy(static x => x.Value.Tick).Key;
                marks.Remove(oldest);
            }
        }
    }

    /// <summary>Forgets the page's mark for the scope's database.</summary>
    /// <param name="page">The page's name.</param>
    /// <param name="scope">The active scope; its tenant is ignored.</param>
    public void Clear(string page, StudioScope scope)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(scope);

        lock (gate)
        {
            marks.Remove(KeyOf(page, scope));
        }
    }

    /// <summary>
    /// Why a mark taken at <paramref name="markAt" /> no longer counts, by the statistics window now - or <see langword="null" />
    /// while it does. It does unless the window begins after the mark (a <c>pg_stat_reset()</c>, a crash, a restore, a failover or
    /// another instance, all of which the window folds into its start; health-engine.md, "The statistics window"), or its start
    /// has moved since the mark by more than <see cref="StatisticsWindow.SameEvent" /> in either direction - a failover or an
    /// Aurora instance switch answers with counters that began earlier, which no <c>Since &gt; markAt</c> sees.
    /// </summary>
    /// <param name="markAt">When the counters were read that the mark holds.</param>
    /// <param name="markWindowSince">The window's start when the mark was taken, or <see langword="null" /> when it was not read.</param>
    /// <param name="now">The statistics window now, or <see langword="null" /> when it could not be read.</param>
    public static SinceMarkEnd? Ended(DateTimeOffset markAt, DateTimeOffset? markWindowSince, StatisticsWindow? now)
    {
        if (now is null)
        {
            return null;
        }

        if (now.Since > markAt)
        {
            return new SinceMarkEnd("the statistics began again at ", now.Since, " (" + now.ReasonText + ")");
        }

        if (MovedSince(markWindowSince, now))
        {
            return new SinceMarkEnd("the server's statistics now begin at ", now.Since, " (" + now.ReasonText + ")");
        }

        return null;
    }

    /// <summary>Whether the window's start now is not the one the mark saw, beyond the same event's slack.</summary>
    /// <param name="markWindowSince">The window's start when the mark was taken, or <see langword="null" /> when it was not read.</param>
    /// <param name="now">The statistics window now, or <see langword="null" />.</param>
    public static bool MovedSince(DateTimeOffset? markWindowSince, StatisticsWindow? now) =>
        markWindowSince is { } then && now is not null && (now.Since - then).Duration() > StatisticsWindow.SameEvent;

    /// <summary>A caveat while the mark holds but the window could not be read now, or <see langword="null" />.</summary>
    /// <param name="now">The statistics window now, or <see langword="null" /> when it could not be read.</param>
    public static string? Caveat(StatisticsWindow? now) => now is null
        ? "Whether the counters were reset since your mark could not be read; a counter that went down is shown as reset."
        : null;

    private static (string Page, string StoreKey, string DatabaseId) KeyOf(string page, StudioScope scope) =>
        (page, scope.StoreKey, scope.DatabaseId);
}
