using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Live;

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
    /// Whether a mark taken at <paramref name="markAt" /> still counts, by the statistics window now: it does unless the window
    /// begins after the mark - a <c>pg_stat_reset()</c>, a crash, a restore, a failover or another instance, all of which the
    /// window folds into its start (health-engine.md, "The statistics window").
    /// </summary>
    /// <param name="markAt">When the counters were read that the mark holds.</param>
    /// <param name="now">The statistics window now, or <see langword="null" /> when it could not be read.</param>
    /// <param name="text">
    /// When the mark does not hold, why ("the statistics began again at …"). When it holds but <paramref name="now" /> is
    /// <see langword="null" />, a caveat; otherwise <see langword="null" />.
    /// </param>
    public static bool Holds(DateTimeOffset markAt, StatisticsWindow? now, out string? text)
    {
        if (now is null)
        {
            text = "Whether the counters were reset since your mark could not be read; a counter that went down is shown as reset.";
            return true;
        }

        if (now.Since > markAt)
        {
            text = "the statistics began again at " + TableText.When(now.Since) + " (" + now.ReasonText + ")";
            return false;
        }

        text = null;
        return true;
    }

    private static (string Page, string StoreKey, string DatabaseId) KeyOf(string page, StudioScope scope) =>
        (page, scope.StoreKey, scope.DatabaseId);
}
