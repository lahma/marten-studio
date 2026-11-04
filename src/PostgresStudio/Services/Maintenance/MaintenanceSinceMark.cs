using System.Globalization;

using PostgresStudio.Components.Pages.Maintenance;
using PostgresStudio.Services.Live;

namespace PostgresStudio.Services.Maintenance;

/// <summary>
/// What the Maintenance page's Mark remembers (1.2-H-A, X6): how many vacuums and analyzes each table the page showed has had,
/// and on PostgreSQL 18 how long they took - numbers only.
/// </summary>
/// <param name="At">When the sweep read the counters (the view's <c>MeasuredAt</c>), not when the visitor pressed Mark.</param>
/// <param name="WindowSince">The statistics window's start then, when it was read: a start that moved means the server's counters began again.</param>
/// <param name="Tables">The tables the page showed, by <see cref="MaintenanceTableView.Key" />; a withheld table is never in it.</param>
internal sealed record MaintenanceMark(DateTimeOffset At, DateTimeOffset? WindowSince, IReadOnlyDictionary<string, MaintenanceMarkRow> Tables);

/// <summary>One table at a mark.</summary>
/// <param name="Vacuums"><c>vacuum_count</c> plus <c>autovacuum_count</c>.</param>
/// <param name="Analyzes"><c>analyze_count</c> plus <c>autoanalyze_count</c>.</param>
/// <param name="VacuumMs"><c>total_vacuum_time</c> plus <c>total_autovacuum_time</c> (PostgreSQL 18).</param>
/// <param name="AnalyzeMs"><c>total_analyze_time</c> plus <c>total_autoanalyze_time</c> (PostgreSQL 18).</param>
internal sealed record MaintenanceMarkRow(long? Vacuums, long? Analyzes, double? VacuumMs, double? AnalyzeMs);

/// <summary>The Maintenance page counted from a mark: <see cref="Rows" /> by <see cref="MaintenanceTableView.Key" />.</summary>
/// <param name="Ended">Why the mark no longer holds, or <see langword="null" /> while it does; then nothing else is set.</param>
/// <param name="Note">A caveat while it holds, or <see langword="null" />.</param>
/// <param name="Rows">What each table has had since, in words.</param>
internal sealed record MaintenanceSince(SinceMarkEnd? Ended, string? Note, IReadOnlyDictionary<string, string> Rows);

/// <summary>The Maintenance page counted from a mark. Pure.</summary>
/// <remarks>
/// The gauges that reset themselves - dead rows, changes since the last analyze, inserts since the last vacuum - are not marked:
/// the page's bars already show them against their triggers. Only the counters that only grow are.
/// </remarks>
internal static class MaintenanceSinceMark
{
    /// <summary>The page's name in <see cref="SinceMarks" />.</summary>
    public const string Page = "maintenance";

    /// <summary>The mark of <paramref name="view" />, or <see langword="null" /> when it is not a read of tables.</summary>
    /// <param name="view">The maintenance as the page shows it now.</param>
    public static MaintenanceMark? Take(MaintenanceView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Refusal != MaintenanceRefusal.None || !view.TablesRead || view.MeasuredAt is not { } at)
        {
            return null;
        }

        return new MaintenanceMark(
            at,
            view.Statistics?.Since,
            view.Tables.ToDictionary(
                static x => x.Key,
                static x => new MaintenanceMarkRow(x.Vacuums, x.Analyzes, x.TotalVacuumMilliseconds, x.TotalAnalyzeMilliseconds),
                StringComparer.Ordinal));
    }

    /// <summary><paramref name="view" /> counted from <paramref name="mark" />.</summary>
    /// <param name="view">The maintenance as the page shows it now.</param>
    /// <param name="mark">The mark.</param>
    /// <param name="withTimes">Whether the server counts the time vacuums and analyzes took (PostgreSQL 18).</param>
    public static MaintenanceSince Apply(MaintenanceView view, MaintenanceMark mark, bool withTimes)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(mark);

        if (SinceMarks.Ended(mark.At, mark.WindowSince, view.Statistics) is { } ended)
        {
            return new MaintenanceSince(ended, null, new Dictionary<string, string>());
        }

        Dictionary<string, string> rows = new(StringComparer.Ordinal);

        foreach (MaintenanceTableView table in view.Tables)
        {
            rows[table.Key] = mark.Tables.TryGetValue(table.Key, out MaintenanceMarkRow? held)
                ? Counted(table, held, withTimes)
                : "not listed at your mark";
        }

        return new MaintenanceSince(null, SinceMarks.Caveat(view.Statistics), rows);
    }

    private static string Counted(MaintenanceTableView table, MaintenanceMarkRow held, bool withTimes)
    {
        long? vacuums = Change(table.Vacuums, held.Vacuums);
        long? analyzes = Change(table.Analyzes, held.Analyzes);

        if (vacuums is < 0 || analyzes is < 0)
        {
            return "reset since your mark";
        }

        if (vacuums is null && analyzes is null)
        {
            return "–";
        }

        string text = vacuums == 0 && analyzes == 0
            ? "none yet"
            : Plural(vacuums ?? 0, "vacuum") + ", " + Plural(analyzes ?? 0, "analyze");

        if (withTimes && vacuums is > 0 && table.TotalVacuumMilliseconds is { } vacuumNow && held.VacuumMs is { } vacuumThen && vacuumNow > vacuumThen)
        {
            text += " · " + MaintenancePresentation.Milliseconds(vacuumNow - vacuumThen) + " vacuuming";
        }

        return text;
    }

    private static long? Change(long? now, long? then) => now is { } a && then is { } b ? a - b : null;

    private static string Plural(long count, string noun) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");
}
