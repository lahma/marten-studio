using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What the usage guards read: the statistics window's signals, and the oldest moment this database's statistics record.</summary>
/// <param name="Reading">The sweep's one read of the window's signals (<see cref="StatisticsWindow.ReadingAsync" />).</param>
/// <param name="OldestRecorded">
/// The oldest last-vacuum, last-analyze or (16+) last-scan time this database's statistics hold for the judged tables and the
/// system catalogs (<see cref="IndexLintQueries.CountersAgeSql" />) - a moment its counters had already begun by - or
/// <see langword="null" /> for none.
/// </param>
internal sealed record UsageWindowReading(StatisticsReading Reading, DateTimeOffset? OldestRecorded);

/// <summary>
/// The window a usage verdict is judged over: the statistics window (W5-CAP), narrowed - where the server cannot say when
/// this database's own counters began - to what the database's statistics themselves prove.
/// </summary>
/// <param name="Window">The statistics window.</param>
/// <param name="Since">Since when this database's counters provably count: <see cref="StatisticsWindow.Since" />, or later.</param>
/// <param name="NarrowedByRecords">
/// Whether <see cref="Since" /> is the oldest recorded moment rather than the window's own start: from PostgreSQL 15 a
/// database's <c>stats_reset</c> is null until somebody resets it, and a database newer than the server's statistics would
/// otherwise read as old as they are.
/// </param>
/// <param name="OldestRecorded">The oldest moment the database's statistics record, or <see langword="null" />.</param>
internal sealed record UsageWindow(StatisticsWindow Window, DateTimeOffset Since, bool NarrowedByRecords, DateTimeOffset? OldestRecorded)
{
    /// <summary>When it was measured, by the studio's clock.</summary>
    public DateTimeOffset MeasuredAt => Window.MeasuredAt;

    /// <summary>How long this database's counters provably count, never negative.</summary>
    public TimeSpan Length => MeasuredAt > Since ? MeasuredAt - Since : TimeSpan.Zero;

    /// <summary>Whether a usage verdict over <paramref name="span" /> may be given: long enough, and not looking reset.</summary>
    public bool Covers(TimeSpan span) => !Window.LooksReset && Length >= span;

    /// <summary>"since 2026-10-20 08:57 UTC (pg_stat_checkpointer.stats_reset)", for the evidence and the caveat.</summary>
    public string SinceText => NarrowedByRecords
        ? "since " + StatisticsWindow.When(Since) + " (the oldest moment this database's statistics record)"
        : Window.SinceText + " (" + Window.BasisText + ")";

    /// <summary>
    /// What a check's status row says while a usage verdict over <paramref name="span" /> is withheld - "Withheld: statistics
    /// 3 days old (since ..., ...)" or "Withheld: the counters look reset (...)" - or <see langword="null" /> when it is not.
    /// </summary>
    /// <param name="span">What the verdict needs: <see cref="UsageGuard.MinimumWindow" />.</param>
    public string? Withheld(TimeSpan span)
    {
        if (Covers(span))
        {
            return null;
        }

        string needs = Days(span);

        if (Window.LooksReset)
        {
            return "Withheld: the counters look reset (" + (Window.Signal ?? Window.ReasonText) + "), so usage is judged again once "
                + "they have counted for " + needs + " (issue #8 G4).";
        }

        if (NarrowedByRecords)
        {
            return "Withheld: this database's counters are proven only " + Age(Length) + " old - "
                + (OldestRecorded is { } oldest
                    ? "the oldest moment its statistics record is " + StatisticsWindow.When(oldest)
                    : "its statistics record no moment yet")
                + ". From PostgreSQL 15 a database's counters begin when it is first used, which no catalog records, so the "
                + "server's statistics (" + Window.SinceText + ", " + Window.BasisText + ") do not prove this database's own are "
                + needs + " old; usage is judged once they are (issue #8 G4).";
        }

        return "Withheld: statistics " + Age(Length) + " old (" + Window.SinceText + ", " + Window.BasisText + "), so usage is "
            + "judged once they have counted for " + needs + " (issue #8 G4).";
    }

    /// <summary>A span as the guard's sentences say it: "5 h", "3 days", "under a minute".</summary>
    internal static string Age(TimeSpan span) => span switch
    {
        _ when span < TimeSpan.FromMinutes(1) => "under a minute",
        _ when span < TimeSpan.FromHours(2) => ((int) span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min",
        _ when span < TimeSpan.FromDays(2) => ((int) span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h",
        _ => ((int) span.TotalDays).ToString(CultureInfo.InvariantCulture) + " days",
    };

    private static string Days(TimeSpan span) => ((int) span.TotalDays).ToString(CultureInfo.InvariantCulture) + " days";
}

/// <summary>
/// Issue #8 G4's usage guards, for T01, T05 and T06: no verdict about how much an index or a table is used is given over
/// statistics that are under <see cref="MinimumWindow" /> old, or that look reset - a restore, a pre-18 <c>pg_upgrade</c>, a
/// failover, a crash - and the check's status row says which instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>The window is CAP's</b> (<see cref="StatisticsWindow" />): <c>max(database reset, min(shared reset, archiver reset))</c>,
/// carried through the history, with <see cref="StatisticsWindow.LooksReset" /> set for the sweep that first sees a reset.
/// </para>
/// <para>
/// <b>Narrowed where the server cannot tell</b> (CAP's documented limit). From PostgreSQL 15 a database's own
/// <c>stats_reset</c> stays null until somebody resets it, so a database created after the server's statistics began - a
/// tenant database a store created yesterday - reads as old as the server's statistics. A verdict that needs seven days must
/// not fire on that. So while the database's own reset time is null, the window starts no earlier than the oldest moment the
/// database's statistics record (<see cref="IndexLintQueries.CountersAgeSql" />): every such timestamp is cleared with the
/// counters, so it is a moment they had already begun by. The cost is the safe one - a database whose every recorded moment
/// is recent stays withheld though it may be older.
/// </para>
/// </remarks>
internal static class UsageGuard
{
    /// <summary>How long the counters must have counted before a usage verdict is given (issue #8 G4).</summary>
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromDays(7);

    /// <summary>The memo key of the counters-age read: one statement per sweep, whichever check asks first.</summary>
    internal const string CountersAgeKey = "tables.counters-age";

    /// <summary>The window's signals and the counters' oldest recorded moment, each read once per sweep.</summary>
    public static async Task<UsageWindowReading> ReadAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        StatisticsReading reading = await StatisticsWindow.ReadingAsync(context).ConfigureAwait(false);
        DateTimeOffset? oldest = await context.Shared(CountersAgeKey, token => ReadOldestAsync(context, token)).ConfigureAwait(false);

        return new UsageWindowReading(reading, oldest);
    }

    /// <summary>The window a usage verdict is judged over, from the sweep's window when it computed one.</summary>
    /// <param name="facts">What the gatherer read.</param>
    /// <param name="evaluation">The evaluation, whose <see cref="HealthEvaluation.Statistics" /> is the sweep's window.</param>
    public static UsageWindow Judge(UsageWindowReading facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        StatisticsWindow window = evaluation.Statistics ?? StatisticsWindow.Compute(facts.Reading, evaluation.History);

        if (facts.Reading.DatabaseReset is not null)
        {
            return new UsageWindow(window, window.Since, false, facts.OldestRecorded);
        }

        // The database's own start is unknown: no earlier than the oldest moment its statistics record - none at all is now.
        DateTimeOffset proven = facts.OldestRecorded ?? window.MeasuredAt;

        return proven > window.Since
            ? new UsageWindow(window, proven, true, facts.OldestRecorded)
            : new UsageWindow(window, window.Since, false, facts.OldestRecorded);
    }

    /// <summary>
    /// The window for a check's facts: <see cref="Judge" /> over what its gatherer read, or the sweep's own window where the
    /// facts carry no reading (a unit test that states the window alone) - or <see langword="null" /> when neither exists,
    /// which withholds every usage verdict (<see cref="NotRead" />).
    /// </summary>
    public static UsageWindow? For(UsageWindowReading? reading, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        return reading is not null
            ? Judge(reading, evaluation)
            : evaluation.Statistics is { } window
                ? new UsageWindow(window, window.Since, false, null)
                : null;
    }

    /// <summary>What a check's status row says when no window was read at all.</summary>
    public const string NotRead = "Withheld: the statistics window was not read, so no usage verdict is given (issue #8 G4).";

    private static async Task<DateTimeOffset?> ReadOldestAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        IStudioTableSet set = await HealthTables.SetAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        IReadOnlyList<DateTimeOffset?> rows = await context.Sql
            .QueryAsync(
                IndexLintQueries.CountersAge(HealthTables.Targets(set), PostgresFeatures.Has(PostgresFeature.LastScanColumns, server.VersionNumber)),
                IndexLintQueries.ReadCountersAge,
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }
}
