using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Live;

namespace PostgresStudio.Services.Statements;

/// <summary>
/// What the Statements page's Mark remembers (1.2-H-A, X6): the all-statements total and each kept statement's counters since they
/// began, as the page showed them - numbers only, never a statement's text.
/// </summary>
/// <param name="At">When the counters were read (the view's <c>MeasuredAt</c>), not when the visitor pressed Mark.</param>
/// <param name="AllExecMs">Every statement's execution time since the counters began.</param>
/// <param name="StatsReset"><c>pg_stat_statements_info.stats_reset</c> then, when the extension says.</param>
/// <param name="Rows">The kept statements, by <c>queryid</c>.</param>
internal sealed record StatementsMark(
    DateTimeOffset At,
    double AllExecMs,
    DateTimeOffset? StatsReset,
    IReadOnlyDictionary<long, StatementMarkRow> Rows);

/// <summary>One statement's counters at a mark.</summary>
/// <param name="SinceBegan">Its counters since they began.</param>
/// <param name="CountersSince">When they began, as the extension knows it.</param>
internal sealed record StatementMarkRow(StatementCounts SinceBegan, DateTimeOffset? CountersSince);

/// <summary>
/// The Statements page counted from a mark: each statement's change since it, and its share of every statement's. Pure.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mark replaces the advisor's window while it is set</b> - one baseline at a time, and the window line says which. A
/// statement's change is its counters now less the mark's. A counter that went down means the statement's entry was evicted
/// and began again, so the statement shows no change ("reset since your mark"); on PostgreSQL 17+ an entry whose own
/// <c>stats_since</c> is at or after the mark counts everything since then, which is the change since the mark exactly. A
/// statement the mark did not hold, before 17, has no known counters at the mark and ranks last.
/// </para>
/// <para>
/// <b>When a mark stops holding</b> (E3): when <c>pg_stat_statements</c> was reset after it (<c>stats_reset</c>), or the server's
/// counters began again after it by a crash, a restore, a failover or another instance (<see cref="StatementFacts.RestartsTheCounters" />,
/// the statistics window). A <c>pg_stat_reset()</c> alone does not reset <c>pg_stat_statements</c>. A mark that no longer holds is not
/// applied.
/// </para>
/// </remarks>
internal static class StatementsSinceMark
{
    /// <summary>The page's name in <see cref="SinceMarks" />.</summary>
    public const string Page = "statements";

    /// <summary>
    /// The mark of <paramref name="view" />, or <see langword="null" /> when it is not a read the counters can be remembered from.
    /// Numbers only.
    /// </summary>
    /// <param name="view">The statements as the page shows them now, unmarked.</param>
    public static StatementsMark? Take(StatementsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Refusal != DatabaseRefusal.None || view.State != StatementsState.Read || view.MeasuredAt is not { } at)
        {
            return null;
        }

        return new StatementsMark(
            at,
            view.AllExecMsSinceBegan,
            view.StatsReset,
            view.Rows.ToDictionary(static x => x.QueryId, static x => new StatementMarkRow(x.SinceBegan, x.CountersSince)));
    }

    /// <summary>
    /// <paramref name="now" /> counted from <paramref name="mark" />, or <paramref name="now" /> unchanged and the reason when the
    /// mark no longer holds.
    /// </summary>
    /// <param name="now">The statements as the page shows them now, unmarked.</param>
    /// <param name="mark">The mark.</param>
    public static (StatementsView View, string? Invalid) Apply(StatementsView now, StatementsMark mark)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(mark);

        // Nothing was read to count: the page says why, and the mark waits for a read.
        if (now.State != StatementsState.Read)
        {
            return (now, null);
        }

        if (Invalid(now, mark) is { } why)
        {
            return (now, why);
        }

        double all = now.AllExecMsSinceBegan - mark.AllExecMs;
        List<StatementRowView> rows = new(now.Rows.Count);

        foreach (StatementRowView row in now.Rows)
        {
            rows.Add(Counted(row, mark, all));
        }

        TimeSpan span = now.MeasuredAt is { } read && read > mark.At ? read - mark.At : TimeSpan.Zero;

        return (
            now with
            {
                Watched = true,
                WindowText = "Since your mark at " + TableText.When(mark.At) + " (" + TableText.Span(span)
                    + "): each statement's change since then, and its share of every statement's.",
                Basis = now.MeasuredAt == mark.At
                    ? "Refresh reads pg_stat_statements again when the last read is more than five minutes old; until then the counters are the mark's own."
                    : null,
                TotalExecMs = Math.Max(all, 0),
                Rows = rows,
            },
            null);
    }

    /// <summary>Why the mark no longer counts, or <see langword="null" /> while it does.</summary>
    internal static string? Invalid(StatementsView now, StatementsMark mark)
    {
        if (now.StatsReset is { } reset && reset > mark.At)
        {
            return "pg_stat_statements was reset at " + TableText.When(reset);
        }

        if (now.Statistics is { } statistics
            && StatementFacts.RestartsTheCounters(statistics.Reason)
            && statistics.Since > mark.At)
        {
            return "the statistics began again at " + TableText.When(statistics.Since) + " (" + statistics.ReasonText + ")";
        }

        return null;
    }

    private static StatementRowView Counted(StatementRowView row, StatementsMark mark, double allDelta)
    {
        StatementCounts? counts = null;
        DateTimeOffset? from = null;
        string? note = null;

        if (row.CountersSince is { } began && began >= mark.At)
        {
            // PostgreSQL 17+: the entry began after the mark, so everything it counts is since then.
            counts = row.SinceBegan;
            from = began;
        }
        else if (mark.Rows.TryGetValue(row.QueryId, out StatementMarkRow? held))
        {
            counts = Difference(row.SinceBegan, held.SinceBegan);
            note = counts is null ? "Reset since your mark: its counters are lower than they were, so it was evicted and began again." : null;
        }
        else
        {
            note = "Not among the statements kept at your mark, so its change since is not known.";
        }

        return row with
        {
            Window = counts,
            Share = counts is not null && allDelta > 0 ? Math.Clamp(counts.ExecMs / allDelta, 0, 1) : null,
            WatchedFrom = from,
            MarkNote = note,
        };
    }

    /// <summary>The counters now less the counters then, or <see langword="null" /> when any went down (the entry began again).</summary>
    internal static StatementCounts? Difference(StatementCounts now, StatementCounts then)
    {
        if (now.Calls < then.Calls
            || now.ExecMs < then.ExecMs
            || now.Rows < then.Rows
            || now.SharedBlocksHit < then.SharedBlocksHit
            || now.SharedBlocksRead < then.SharedBlocksRead
            || now.TempBlocksWritten < then.TempBlocksWritten
            || now.IoReadMs < then.IoReadMs
            || (now.StorageBlocksRead is { } storageNow && then.StorageBlocksRead is { } storageThen && storageNow < storageThen))
        {
            return null;
        }

        return new StatementCounts(
            now.Calls - then.Calls,
            now.ExecMs - then.ExecMs,
            now.Rows - then.Rows,
            now.SharedBlocksHit - then.SharedBlocksHit,
            now.SharedBlocksRead - then.SharedBlocksRead,
            now.TempBlocksWritten - then.TempBlocksWritten,
            now.IoReadMs - then.IoReadMs,
            now.StorageBlocksRead is { } a && then.StorageBlocksRead is { } b ? a - b : null);
    }
}
