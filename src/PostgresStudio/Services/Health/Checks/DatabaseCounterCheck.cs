using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Sources;

using Npgsql;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What C08 reads: this database's cumulative counters, and how busy the studio itself has been.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Commits"><c>xact_commit</c>.</param>
/// <param name="Rollbacks"><c>xact_rollback</c>.</param>
/// <param name="Deadlocks"><c>deadlocks</c>.</param>
/// <param name="TempFiles"><c>temp_files</c>.</param>
/// <param name="TempBytes"><c>temp_bytes</c>.</param>
/// <param name="StatsReset"><c>stats_reset</c>, or <see langword="null" /> when they never were.</param>
/// <param name="StudioRollbacks">
/// How many rollbacks the studio's own read-only sessions have sent to this database since the process started
/// (<see cref="StudioDatabaseTime.RollbacksEver" />): every one of them is a transaction that ends in
/// <c>ROLLBACK</c>, which <c>xact_rollback</c> counts like any other.
/// </param>
internal sealed record DatabaseCounterFacts(
    DateTimeOffset ReadAt,
    long Commits,
    long Rollbacks,
    long Deadlocks,
    long TempFiles,
    long TempBytes,
    DateTimeOffset? StatsReset,
    long StudioRollbacks)
{
    /// <summary>
    /// The sweep's statistics window signals (W5-CAP), for the rates per day - or <see langword="null" /> when they could not
    /// be read, which leaves the hour's rules as they are.
    /// </summary>
    public StatisticsReading? Statistics { get; init; }

    /// <summary>Why <see cref="Statistics" /> is missing - the studio's words and a SQLSTATE - or <see langword="null" />.</summary>
    public string? StatisticsNotRead { get; init; }

    /// <summary>
    /// Who runs the server, for where temporary files go (Aurora's local storage, issue #8 A6) - or <see langword="null" /> when
    /// the sweep's server read failed, which words them as on any other server.
    /// </summary>
    public PostgresPlatform? Platform { get; init; }
}

/// <summary>C08: deadlocks, a high share of rollbacks, and sorts spilling to disk, over the advisor's history.</summary>
/// <remarks>
/// <para>
/// <b>Rates, so two samples.</b> <c>pg_stat_database</c>'s counters only ever grow until somebody resets them, so
/// the check records them in <see cref="HealthHistory" /> each sweep and judges the difference between the first
/// and the last sample of the run since the last reset - which needs two sweeps one <c>HealthInterval</c> apart,
/// and so a page open that long. Before that it says it is watching.
/// </para>
/// <para>
/// <b>The studio's own rollbacks are taken out, exactly.</b> Every read the studio makes in its read-only
/// transaction ends in <c>ROLLBACK</c>, health checks included, and an idle store watched by the studio would
/// otherwise show a rollback share made entirely by the studio. The studio counts the rollbacks its own sessions
/// sent to each database (<see cref="StudioDatabaseTime.RollbacksEver" />, keyed by host, port and database as the
/// connection string names them); that count is sampled beside the server's, and its change over the window is
/// subtracted from the server's. The server may not have counted the last few yet when it is read - a backend
/// reports its statistics a moment after the fact - which can only make the subtraction larger, never the share.
/// A share is judged only over <see cref="ConnectionThresholds.RollbackMinimumTransactions" /> transactions or
/// more.
/// </para>
/// <para>
/// <b>And per day, over the statistics window</b> (issue #8 G5, W5-CAP). The same counters are cumulative since
/// <see cref="StatisticsWindow.Since" />, so once the window has counted for a day and does not look reset, they are a rate
/// too: <see cref="TempBytesPerDayWarning" /> or more of temporary files a day is a Warning of its own - the hour's Info
/// stays below it - and a deadlock finding adds the window's deadlocks per day to its evidence. Nothing per day is said
/// while the window is under a day or looks reset.
/// </para>
/// </remarks>
[HealthCheck("C08", HealthArea.Connections, HealthCostTier.Catalog, "Deadlocks, rollbacks and temp spills")]
internal sealed class DatabaseCounterCheck : HealthCheck<DatabaseCounterFacts>
{
    /// <summary>At least this many bytes of temporary files a day over the statistics window is a Warning (G5): 1 GiB.</summary>
    internal const long TempBytesPerDayWarning = 1L << 30;

    internal const string CommitSeries = "connections.xact-commit";

    internal const string RollbackSeries = "connections.xact-rollback";

    internal const string DeadlockSeries = "connections.deadlocks";

    internal const string TempFilesSeries = "connections.temp-files";

    internal const string TempBytesSeries = "connections.temp-bytes";

    internal const string StatsResetSeries = "connections.stats-reset";

    internal const string StudioRollbacksSeries = "connections.studio-rollbacks";

    private static readonly string[] Series =
        [CommitSeries, RollbackSeries, DeadlockSeries, TempFilesSeries, TempBytesSeries, StatsResetSeries, StudioRollbacksSeries];

    /// <inheritdoc />
    public override async Task<DatabaseCounterFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The studio meters each database under its connection string's host, port and database; an unopened
        // connection says which, with no I/O.
        string meterKey;
        using (NpgsqlConnection unopened = context.Target.CreateConnection(StudioConnectionUsage.ReadWrite))
        {
            meterKey = StudioDatabaseTime.KeyOf(unopened);
        }

        IReadOnlyList<(long Commits, long Rollbacks, long Deadlocks, long TempFiles, long TempBytes, DateTimeOffset? Reset)> rows = await context.Sql
            .QueryAsync(HealthConnectionQueries.DatabaseCounters(), Map, cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            throw new HealthCheckUnavailableException("pg_stat_database has no row for this database, so its counters cannot be read.");
        }

        (long commits, long rollbacks, long deadlocks, long tempFiles, long tempBytes, DateTimeOffset? reset) = rows[0];

        // The window's signals, shared with C11 and read once for the sweep.
        StatisticsReading? statistics = null;
        string? notRead = null;

        try
        {
            statistics = await StatisticsWindow.ReadingAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failure there costs only the rates per day, which the status row then says were not checked: the hour's rules
            // stand on this check's own read, and C11's row shows the window's failure with its reason.
            notRead = StatisticsWindow.NotRead(exception);
        }

        // The platform, shared with S01, for where a spill lands: a failure there words the temp files as anywhere else.
        PostgresPlatform? platform = null;

        try
        {
            platform = (await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false)).Platform;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            platform = null;
        }

        // Read after the statement, so its own rollback is counted on both samples alike.
        return new DatabaseCounterFacts(
            context.Now, commits, rollbacks, deadlocks, tempFiles, tempBytes, reset, StudioDatabaseTime.Shared.RollbacksEver(meterKey))
        {
            Statistics = statistics,
            StatisticsNotRead = notRead,
            Platform = platform,
        };
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(DatabaseCounterFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        yield return new HealthSamplePart(CommitSeries, facts.Commits);
        yield return new HealthSamplePart(RollbackSeries, facts.Rollbacks);
        yield return new HealthSamplePart(DeadlockSeries, facts.Deadlocks);
        yield return new HealthSamplePart(TempFilesSeries, facts.TempFiles);
        yield return new HealthSamplePart(TempBytesSeries, facts.TempBytes);
        yield return new HealthSamplePart(StatsResetSeries, facts.StatsReset?.ToUnixTimeSeconds() ?? 0);
        yield return new HealthSamplePart(StudioRollbacksSeries, facts.StudioRollbacks);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DatabaseCounterFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        string since = facts.StatsReset is { } reset
            ? "since the statistics were last reset, at " + reset.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
            : "since the server's statistics began";

        // Per day, over the statistics window (G5): only once it has counted for a day and does not look reset.
        StatisticsWindow? window = evaluation.Statistics
            ?? (facts.Statistics is { } reading ? StatisticsWindow.Compute(reading, evaluation.History) : null);
        PerDay? perDay = window is { CoversADay: true } covered ? new PerDay(covered, facts) : null;

        List<HealthSample> run = Run(evaluation.History);
        List<HealthFinding> findings = [];

        if (perDay is { TempBytes: >= TempBytesPerDayWarning } spilling)
        {
            findings.Add(TempWarning(spilling, run, evaluation, since, facts.Platform));
        }

        if (run.Count < 2)
        {
            return findings.Count > 0
                ? new HealthVerdict(findings)
                : HealthVerdict.Quiet(
                    "Watching: deadlocks, rollbacks and temp files are judged as rates, which need two checks one HealthInterval apart "
                    + "while a page is open. Counted " + since + "." + PerDayNote(window, perDay, facts.StatisticsNotRead));
        }

        HealthSample first = run[0];
        HealthSample last = run[^1];
        TimeSpan span = last.At - first.At;

        long deadlocks = Delta(first, last, DeadlockSeries);
        long commits = Delta(first, last, CommitSeries);
        long rollbacks = Delta(first, last, RollbackSeries);
        long tempFiles = Delta(first, last, TempFilesSeries);
        long tempBytes = Delta(first, last, TempBytesSeries);

        long studio = Delta(first, last, StudioRollbacksSeries);
        long ownRollbacks = Math.Max(0, rollbacks - studio);
        long transactions = commits + ownRollbacks;

        string measured = "Measured in pg_stat_database between two checks " + SessionText.Duration(span) + " apart ("
            + SessionText.Count(run.Count, "sample") + ", taken only while a page was open), counted " + since + ".";

        if (deadlocks > 0)
        {
            List<HealthEvidence> evidence =
            [
                new("Deadlocks in the window", SessionText.Number(deadlocks)),
                new("Window", SessionText.Duration(span)),
            ];

            if (perDay is { } day)
            {
                evidence.Add(new("Deadlocks per day " + day.Window.SinceText, Rate(day.Deadlocks)));
            }

            findings.Add(new HealthFinding
            {
                Code = "C08",
                Subject = "deadlocks",
                Severity = HealthSeverity.Warning,
                Title = SessionText.Count(deadlocks, "deadlock") + " in the last " + SessionText.Duration(span),
                WhyItMatters =
                    "Postgres resolved each deadlock by rolling one of the two transactions back, so each is a write that failed for "
                    + "the application. " + evaluation.Text.DeadlockCause,
                Evidence = evidence,
                Caveat = measured + " Postgres logs each deadlock with both statements, in the server's log.",
            });
        }

        if (transactions >= ConnectionThresholds.RollbackMinimumTransactions
            && (double) ownRollbacks / transactions > ConnectionThresholds.RollbackShareWarning)
        {
            double share = (double) ownRollbacks / transactions;

            findings.Add(new HealthFinding
            {
                Code = "C08",
                Subject = "rollbacks",
                Severity = HealthSeverity.Warning,
                Title = SessionText.Percent(share) + " of this database's transactions rolled back in the last " + SessionText.Duration(span),
                WhyItMatters =
                    "A rolled-back transaction is work the database did and threw away. A share this high is most often failures the "
                    + "application retries - optimistic concurrency on documents, an expected version on an event stream, unique "
                    + "violations - or a framework that rolls back read-only transactions by habit.",
                Evidence =
                [
                    new("Rolled back", SessionText.Number(ownRollbacks) + " of " + SessionText.Number(transactions)),
                    new("The studio's own, subtracted", SessionText.Number(Math.Min(studio, rollbacks))),
                    new("Window", SessionText.Duration(span)),
                ],
                Caveat = measured + " Every read the studio makes is a read-only transaction that rolls back; the rollbacks this "
                    + "process's studio sent to this database over the window are subtracted. Another process's studio, if one "
                    + "watches the same database, is not.",
            });
        }

        // The hour's Info, unless the day's Warning already speaks for the temp files: one finding per subject.
        if (tempFiles > 0 && !findings.Any(static x => x.Subject == "temp-files"))
        {
            List<HealthEvidence> evidence =
            [
                new("Temporary files", SessionText.Number(tempFiles)),
                new("Written", Bytes(tempBytes)),
                new("Window", SessionText.Duration(span)),
            ];

            if (perDay is { } day)
            {
                evidence.Add(new("Written a day " + day.Window.SinceText, Bytes((long) day.TempBytes)));
            }

            findings.Add(new HealthFinding
            {
                Code = "C08",
                Subject = "temp-files",
                Severity = HealthSeverity.Info,
                Title = "Statements wrote " + SessionText.Count(tempFiles, "temporary file") + " to disk in the last " + SessionText.Duration(span),
                WhyItMatters =
                    "A sort or a hash that does not fit in work_mem spills to temporary files on disk, which is slower by orders of "
                    + "magnitude. " + evaluation.Text.TempSpillCause + "; an index for the sort removes the spill, and a larger work_mem "
                    + "is the DBA's lever." + AuroraTemp(facts.Platform),
                Evidence = evidence,
                Caveat = measured,
            });
        }

        if (findings.Count == 0)
        {
            return HealthVerdict.Quiet(
                "No deadlock, no temporary file and " + SessionText.Number(ownRollbacks) + " of " + SessionText.Number(transactions)
                + " transactions rolled back in the last " + SessionText.Duration(span) + " (the studio's own subtracted)."
                + PerDayNote(window, perDay, facts.StatisticsNotRead));
        }

        // Deadlocks, rollbacks, temp files: the order the page has always drawn them in.
        return new HealthVerdict(
            [.. findings.OrderBy(static x => Array.IndexOf(SubjectOrder, x.Subject))],
            facts.StatisticsNotRead is { } notRead ? NotChecked(notRead) : null);
    }

    /// <summary>The order C08's findings are listed in, whichever rule found them.</summary>
    private static readonly string?[] SubjectOrder = ["deadlocks", "rollbacks", "temp-files"];

    /// <summary>
    /// The per-day Warning (G5): at least <see cref="TempBytesPerDayWarning" /> of temporary files a day over the statistics
    /// window - on its own, so a database that spills this much is told so whether or not it spilled in the last hour, with the
    /// hour's figure beside it when there are two samples to take it from.
    /// </summary>
    private static HealthFinding TempWarning(PerDay day, List<HealthSample> run, HealthEvaluation evaluation, string since, PostgresPlatform? platform)
    {
        bool aurora = platform?.Kind == PostgresPlatformKind.AmazonAurora;
        List<HealthEvidence> evidence =
        [
            new("Written a day", Bytes((long) day.TempBytes)),
            new("Temporary files a day", Rate(day.TempFiles)),
            new("Window", SessionText.Duration(day.Window.Length) + ", " + day.Window.SinceText),
            new("Counting since", day.Window.BasisText),
        ];

        if (run.Count >= 2)
        {
            TimeSpan span = run[^1].At - run[0].At;
            evidence.Add(new("Written in the last " + SessionText.Duration(span), Bytes(Delta(run[0], run[^1], TempBytesSeries))));
        }

        return new HealthFinding
        {
            Code = "C08",
            Subject = "temp-files",
            Severity = HealthSeverity.Warning,
            Title = "Statements write " + Bytes((long) day.TempBytes) + " of temporary files a day " + day.Window.SinceText,
            WhyItMatters =
                "A sort or a hash that does not fit in work_mem spills to temporary files on disk, which is slower by orders of "
                + "magnitude - and at a gigabyte a day or more it is a steady load on "
                + (aurora ? "the instance's local storage, which the temporary files share with its logs. " : "the disk the database itself writes to. ")
                + evaluation.Text.TempSpillCause + "; an index for the sort removes the spill, and a larger work_mem is the DBA's lever."
                + AuroraTemp(platform),
            Evidence = evidence,
            Caveat = "Measured in pg_stat_database's temp_bytes over the statistics window, which began " + day.Window.ReasonText
                + " - counted " + since + ".",
        };
    }

    /// <summary>
    /// Where temporary files go on Aurora (issue #8 A6), as the last sentence of a temp finding's "why" - nothing on any other
    /// platform. "In Aurora PostgreSQL, these files share local storage with other log files. You can monitor your Aurora
    /// PostgreSQL DB cluster's local storage space by watching the Amazon CloudWatch metric for <c>FreeLocalStorage</c>"
    /// (<i>Managing temporary files with PostgreSQL</i>, Aurora User Guide), and "The local storage size depends on the instance
    /// class that you use" (<i>Amazon Aurora storage</i>, Aurora User Guide) - both read 2026-10-26.
    /// </summary>
    internal static string AuroraTemp(PostgresPlatform? platform) =>
        platform?.Kind == PostgresPlatformKind.AmazonAurora
            ? " On Aurora, temporary files use the instance's local storage, not the cluster volume - shared with its logs and "
              + "sized by its instance class (Managing temporary files with PostgreSQL; Amazon Aurora storage, Aurora User Guide) - so "
              + "CloudWatch's FreeLocalStorage is where a spill that fills it shows, and temp_file_limit in the DB cluster parameter "
              + "group is what stops one statement from doing so."
            : string.Empty;

    /// <summary>What the window adds to a note: the rates per day, or how long it has to go.</summary>
    private static string PerDayNote(StatisticsWindow? window, PerDay? perDay, string? notRead)
    {
        if (window is null)
        {
            return notRead is null ? string.Empty : " " + NotChecked(notRead);
        }

        if (perDay is not { } day)
        {
            return " " + window.Watching("the rate per day of deadlocks and temporary files", StatisticsWindow.Day);
        }

        return " Per day " + day.Window.SinceText + ": " + Rate(day.Deadlocks) + " deadlocks and " + Bytes((long) day.TempBytes)
            + " of temporary files.";
    }

    /// <summary>What the status row says of the rates per day when the window could not be read.</summary>
    private static string NotChecked(string notRead) => "The rates per day were not checked: " + notRead + ".";

    /// <summary>A rate per day: two decimals below ten, grouped and whole above.</summary>
    private static string Rate(double perDay) => perDay.ToString(perDay < 10 ? "0.##" : "N0", CultureInfo.InvariantCulture);

    /// <summary>The window's rates per day, read only once it covers a day.</summary>
    /// <param name="Window">The window, covering a day.</param>
    /// <param name="Deadlocks">Deadlocks a day.</param>
    /// <param name="TempFiles">Temporary files a day.</param>
    /// <param name="TempBytes">Bytes of temporary files a day.</param>
    private sealed record PerDay(StatisticsWindow Window, double Deadlocks, double TempFiles, double TempBytes)
    {
        /// <summary>The rates of <paramref name="facts" /> over <paramref name="window" />.</summary>
        public PerDay(StatisticsWindow window, DatabaseCounterFacts facts)
            : this(window, facts.Deadlocks / window.Days, facts.TempFiles / window.Days, facts.TempBytes / window.Days)
        {
        }
    }

    /// <summary>
    /// The trailing run of samples that carry every series and count from one reset: a changed
    /// <c>stats_reset</c>, or a counter that went down, starts a new run.
    /// </summary>
    internal static List<HealthSample> Run(HealthHistoryView history)
    {
        ArgumentNullException.ThrowIfNull(history);

        List<HealthSample> complete = [.. history.Samples.Where(static x => Series.All(x.Values.ContainsKey))];
        List<HealthSample> run = [];

        for (int i = complete.Count - 1; i >= 0; i--)
        {
            HealthSample sample = complete[i];

            if (run.Count > 0)
            {
                HealthSample later = run[0];

                bool sameReset = sample.Values[StatsResetSeries].Equals(later.Values[StatsResetSeries]);
                bool monotone = Series.Where(static x => x is not StatsResetSeries)
                    .All(x => sample.Values[x] <= later.Values[x]);

                if (!sameReset || !monotone)
                {
                    break;
                }
            }

            run.Insert(0, sample);
        }

        return run;
    }

    private static long Delta(HealthSample first, HealthSample last, string series) =>
        (long) Math.Max(0d, last.Values[series] - first.Values[series]);

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double) (1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB",
        >= 1L << 20 => (bytes / (double) (1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB",
        >= 1L << 10 => (bytes / (double) (1L << 10)).ToString("0.0", CultureInfo.InvariantCulture) + " KiB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };

    private static (long Commits, long Rollbacks, long Deadlocks, long TempFiles, long TempBytes, DateTimeOffset? Reset) Map(NpgsqlDataReader reader) => (
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt64(2),
        reader.GetInt64(3),
        reader.GetInt64(4),
        SessionReads.Value<DateTimeOffset>(reader, 5));
}
