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
    long StudioRollbacks);

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
/// </remarks>
[HealthCheck("C08", HealthArea.Connections, HealthCostTier.Catalog, "Deadlocks, rollbacks and temp spills")]
internal sealed class DatabaseCounterCheck : HealthCheck<DatabaseCounterFacts>
{
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

        // Read after the statement, so its own rollback is counted on both samples alike.
        return new DatabaseCounterFacts(
            context.Now, commits, rollbacks, deadlocks, tempFiles, tempBytes, reset, StudioDatabaseTime.Shared.RollbacksEver(meterKey));
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

        List<HealthSample> run = Run(evaluation.History);

        if (run.Count < 2)
        {
            return HealthVerdict.Quiet(
                "Watching: deadlocks, rollbacks and temp files are judged as rates, which need two checks one HealthInterval apart "
                + "while a page is open. Counted " + since + ".");
        }

        HealthSample first = run[0];
        HealthSample last = run[^1];
        TimeSpan window = last.At - first.At;

        long deadlocks = Delta(first, last, DeadlockSeries);
        long commits = Delta(first, last, CommitSeries);
        long rollbacks = Delta(first, last, RollbackSeries);
        long tempFiles = Delta(first, last, TempFilesSeries);
        long tempBytes = Delta(first, last, TempBytesSeries);

        long studio = Delta(first, last, StudioRollbacksSeries);
        long ownRollbacks = Math.Max(0, rollbacks - studio);
        long transactions = commits + ownRollbacks;

        string measured = "Measured in pg_stat_database between two checks " + SessionText.Duration(window) + " apart ("
            + SessionText.Count(run.Count, "sample") + ", taken only while a page was open), counted " + since + ".";

        List<HealthFinding> findings = [];

        if (deadlocks > 0)
        {
            findings.Add(new HealthFinding
            {
                Code = "C08",
                Subject = "deadlocks",
                Severity = HealthSeverity.Warning,
                Title = SessionText.Count(deadlocks, "deadlock") + " in the last " + SessionText.Duration(window),
                WhyItMatters =
                    "Postgres resolved each deadlock by rolling one of the two transactions back, so each is a write that failed for "
                    + "the application. " + evaluation.Text.DeadlockCause,
                Evidence =
                [
                    new("Deadlocks in the window", SessionText.Number(deadlocks)),
                    new("Window", SessionText.Duration(window)),
                ],
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
                Title = SessionText.Percent(share) + " of this database's transactions rolled back in the last " + SessionText.Duration(window),
                WhyItMatters =
                    "A rolled-back transaction is work the database did and threw away. A share this high is most often failures the "
                    + "application retries - optimistic concurrency on documents, an expected version on an event stream, unique "
                    + "violations - or a framework that rolls back read-only transactions by habit.",
                Evidence =
                [
                    new("Rolled back", SessionText.Number(ownRollbacks) + " of " + SessionText.Number(transactions)),
                    new("The studio's own, subtracted", SessionText.Number(Math.Min(studio, rollbacks))),
                    new("Window", SessionText.Duration(window)),
                ],
                Caveat = measured + " Every read the studio makes is a read-only transaction that rolls back; the rollbacks this "
                    + "process's studio sent to this database over the window are subtracted. Another process's studio, if one "
                    + "watches the same database, is not.",
            });
        }

        if (tempFiles > 0)
        {
            findings.Add(new HealthFinding
            {
                Code = "C08",
                Subject = "temp-files",
                Severity = HealthSeverity.Info,
                Title = "Statements wrote " + SessionText.Count(tempFiles, "temporary file") + " to disk in the last " + SessionText.Duration(window),
                WhyItMatters =
                    "A sort or a hash that does not fit in work_mem spills to temporary files on disk, which is slower by orders of "
                    + "magnitude. " + evaluation.Text.TempSpillCause + "; an index for the sort removes the spill, and a larger work_mem "
                    + "is the DBA's lever.",
                Evidence =
                [
                    new("Temporary files", SessionText.Number(tempFiles)),
                    new("Written", Bytes(tempBytes)),
                    new("Window", SessionText.Duration(window)),
                ],
                Caveat = measured,
            });
        }

        if (findings.Count == 0)
        {
            return HealthVerdict.Quiet(
                "No deadlock, no temporary file and " + SessionText.Number(ownRollbacks) + " of " + SessionText.Number(transactions)
                + " transactions rolled back in the last " + SessionText.Duration(window) + " (the studio's own subtracted).");
        }

        return new HealthVerdict(findings);
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
