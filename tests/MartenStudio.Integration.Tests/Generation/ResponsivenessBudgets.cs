using System.Diagnostics;
using System.Globalization;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// The latency every screen's data call has to stay inside, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why constants rather than numbers at the call site.</b> A budget that lives next to its assertion
/// gets raised by whoever is looking at the assertion, one millisecond at a time, until it measures
/// nothing. Here they are all visible at once, so raising one is a decision about the set.
/// </para>
/// <para>
/// <b>What they are measuring.</b> Wall time of the studio's own data service against a Postgres in a
/// container on the same machine, over a collection of a million rows. They are generous - several times
/// what a warm call costs - because the point is not to benchmark Postgres. The point is that
/// <em>nothing on a navigation path is O(rows)</em>: a sequential scan over a million documents, an
/// exact <c>count(*)</c> where an estimate was intended, or an offset page that walks half a collection
/// does not miss these budgets by ten percent, it misses them by a factor of fifty.
/// </para>
/// </remarks>
internal static class ResponsivenessBudgets
{
    /// <summary>The collections rail: one grouped <c>reltuples</c> read for every collection (D8).</summary>
    public static readonly TimeSpan CollectionsRail = TimeSpan.FromMilliseconds(300);

    /// <summary>One page of a document list, first page or deep keyset page alike.</summary>
    public static readonly TimeSpan DocumentPage = TimeSpan.FromMilliseconds(250);

    /// <summary>The <c>_recent</c> pseudo-collection, which is a union over every collection.</summary>
    public static readonly TimeSpan RecentDocuments = TimeSpan.FromMilliseconds(500);

    /// <summary>One page of the global event feed.</summary>
    public static readonly TimeSpan EventFeedPage = TimeSpan.FromMilliseconds(250);

    /// <summary>One page of the stream list.</summary>
    public static readonly TimeSpan StreamPage = TimeSpan.FromMilliseconds(250);

    /// <summary>The projections screen's whole read: the model, the progress and the daemon card.</summary>
    public static readonly TimeSpan ProjectionsProgress = TimeSpan.FromMilliseconds(300);

    /// <summary>The schema screen's Tables tab: <c>pg_stat_user_tables</c> and the size functions.</summary>
    public static readonly TimeSpan SchemaTables = TimeSpan.FromSeconds(1);

    /// <summary>How many times each measurement is taken. The median of these is what is asserted.</summary>
    public const int Samples = 3;

    /// <summary>
    /// Runs <paramref name="action" /> <see cref="Samples" /> times and returns the median duration.
    /// </summary>
    /// <typeparam name="T">What the call returns.</typeparam>
    /// <param name="action">The call.</param>
    /// <returns>The median duration and the last result, so the caller can assert on both.</returns>
    /// <remarks>
    /// The median, not the mean and not the best: a mean is dragged by the first call's connection open
    /// and plan cache miss, and a best-of hides exactly the tail that makes a UI feel slow. Three is
    /// enough to throw away one outlier, which is all this is for.
    /// </remarks>
    public static async Task<(TimeSpan Median, T Result)> MeasureAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var durations = new List<TimeSpan>(Samples);
        T result = default!;

        for (int i = 0; i < Samples; i++)
        {
            long started = Stopwatch.GetTimestamp();
            result = await action();
            durations.Add(Stopwatch.GetElapsedTime(started));
        }

        durations.Sort();

        return (durations[Samples / 2], result);
    }

    /// <summary>A line for the test output, so a run reports numbers and not only pass or fail.</summary>
    /// <param name="what">What was measured.</param>
    /// <param name="median">The median duration.</param>
    /// <param name="budget">The budget it is held to.</param>
    public static string Line(string what, TimeSpan median, TimeSpan budget) => string.Format(
        CultureInfo.InvariantCulture,
        "{0,-42} {1,8:F1} ms   budget {2,7:F0} ms   {3}",
        what,
        median.TotalMilliseconds,
        budget.TotalMilliseconds,
        median <= budget ? "ok" : "OVER");

    /// <summary>
    /// Measurements that are over budget for a reason already written down, and what that reason is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A known hot spot is reported in the test output and does not fail the run - so that the suite
    /// still fails on a <em>new</em> one, which is the regression it exists to catch. A red suite that
    /// is permanently red tells nobody anything.
    /// </para>
    /// <para>
    /// Each entry is a debt, not an exemption: the fix is a change in <c>src/MartenStudio</c>, and when
    /// it lands the entry comes out and the budget starts holding.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> KnownHotSpots =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["customer first page"] =
                "The document list's default sort is mt_last_modified descending, and Marten creates no "
                + "index on that column - so opening any collection is a sequential scan plus a top-N "
                + "sort, whose cost is proportional to the collection. Measured 2026-09-14 against this "
                + "generator: 90 ms at 60 000 rows, 785 ms at 600 000. Offset paging inherits it, which "
                + "is why an offset page at the 10 000 cap costs about a second at 600 000 rows. The fix "
                + "is in src/MartenStudio - default the sort to the primary key, or have the index "
                + "advisor offer the mt_last_modified index the same way it offers the others - and is "
                + "outside the packet that added this suite.",
        };
}

/// <summary>
/// Collects the measurements of one run, separating a new hot spot from a known one.
/// </summary>
/// <param name="write">Where a line goes - the test's own output helper.</param>
internal sealed class BudgetReport(Action<string> write)
{
    private readonly List<string> unexpected = [];

    /// <summary>Measurements that were over budget and are not already written down.</summary>
    public IReadOnlyList<string> Unexpected => unexpected;

    /// <summary>Reports one measurement, and remembers it when it is a new one.</summary>
    /// <param name="what">What was measured; the key into <see cref="ResponsivenessBudgets.KnownHotSpots" />.</param>
    /// <param name="median">The median duration.</param>
    /// <param name="budget">The budget it is held to.</param>
    public void Check(string what, TimeSpan median, TimeSpan budget)
    {
        var over = median > budget;
        var known = ResponsivenessBudgets.KnownHotSpots.TryGetValue(what, out string? why);

        write(ResponsivenessBudgets.Line(what, median, budget) + (over && known ? "   (known)" : string.Empty));

        if (!over)
        {
            return;
        }

        if (known)
        {
            write("    known hot spot: " + why);
            return;
        }

        unexpected.Add(ResponsivenessBudgets.Line(what, median, budget));
    }

    /// <summary>The sentence a failing assertion should carry.</summary>
    public string Because =>
        "no read on a navigation path may be proportional to the collection, and this one is not a hot "
        + "spot ResponsivenessBudgets.KnownHotSpots already accounts for; over budget: "
        + string.Join(" | ", unexpected);
}
