using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// That the statistics counters began again recently, and why - the input of T08's <c>since-restart</c> variant, which
/// <c>StatisticsWindow</c> (W5-CAP) supplies.
/// </summary>
/// <param name="Since">When the counters began again.</param>
/// <param name="Reason">Why, in words: "a crash", "a failover to another server", "pg_upgrade", "pg_stat_reset()".</param>
internal sealed record StatisticsRestart(DateTimeOffset Since, string Reason);

/// <summary>What T08 reads: every judged table's estimate, its modifications since the last analyze, and when that was.</summary>
/// <param name="ReadAt">When they were read.</param>
/// <param name="Tables">The judged tables that exist.</param>
/// <param name="Restart">
/// When and why the counters began again, as a test states it - or <see langword="null" />, and then the evaluator takes it
/// from the statistics window (<see cref="StaleStatisticsCheck.RestartOf" />).
/// </param>
internal sealed record StatisticsFacts(DateTimeOffset ReadAt, IReadOnlyList<TableFacts> Tables, StatisticsRestart? Restart = null)
{
    /// <summary>
    /// The sweep's one read of the statistics window's signals (<see cref="StatisticsWindow.ReadingAsync" />, W5-CAP), from which
    /// the evaluator computes the window when the sweep did not hand it one - or <see langword="null" /> when it could not be
    /// read, which costs only <c>since-restart</c>.
    /// </summary>
    public StatisticsReading? Statistics { get; init; }
}

/// <summary>Why a table's statistics are wrong.</summary>
internal enum StatisticsProblem
{
    /// <summary>Nothing wrong.</summary>
    None,

    /// <summary>
    /// Never analysed, though it has rows or is read: <c>reltuples = -1</c> on the table - or on some of its partitions -
    /// with no analyze of either kind.
    /// </summary>
    NeverAnalysed,

    /// <summary>
    /// A partitioned table whose parent has never been analysed - which autovacuum never does - though its partitions
    /// have been. Info.
    /// </summary>
    ParentNeverAnalysed,

    /// <summary>Analysed, but more modified since than twice its analyze trigger, and not in the last hour.</summary>
    Stale,
}

/// <summary>
/// T08: a table that has never been analysed, or whose statistics are stale against its own analyze trigger - and the
/// counters having begun again under every table at once.
/// </summary>
/// <remarks>
/// <para>
/// The planner chooses every plan from <c>pg_class.reltuples</c> and <c>pg_statistic</c>, and the studio's own <c>~</c>
/// counts are <c>reltuples</c> (D8): both are only as good as the last <c>ANALYZE</c>.
/// </para>
/// <list type="bullet">
/// <item><b><c>stale</c></b>: <c>n_mod_since_analyze</c> at least <see cref="MinRowsPast" /> past twice the table's own analyze
/// trigger (<see cref="Autovacuum.Triggers" />, issue #8 G2), and no analyze in <see cref="StaleAfter" />.</item>
/// <item><b><c>never-analysed</c></b>: <c>reltuples = -1</c> and no analyze of either kind, with at least
/// <see cref="MinRows" /> live rows - or that many read since the counters began (<c>seq_tup_read + idx_tup_fetch</c>), which
/// is the shape every table has after <c>pg_upgrade</c> from 14-17, where <c>n_live_tup</c> is 0 and a read-mostly table is
/// never modified enough for autovacuum to analyse it. A partitioned parent nobody has analysed is Info alone, because
/// autovacuum never analyses a partitioned table (B0 §8c).</item>
/// <item><b><c>since-restart</c></b>: one database-level Info while the statistics window says the counters began again
/// (a crash, a failover, <c>pg_upgrade</c>, a reset) less than <see cref="RestartWindow" /> ago, and the tables of at least
/// <see cref="RestartMinRows" /> estimated rows not analysed since hold at least half the judged rows: what explains a quiet
/// T07. The window is CAP's <see cref="StatisticsWindow" />, read once per sweep (<see cref="RestartOf" />).</item>
/// </list>
/// <para>
/// <b>Held, not glimpsed</b>, like T07: right after a bulk load every table looks like this until autovacuum's next pass,
/// so the condition must hold across <see cref="PersistFor" /> unless autovacuum is off for the table or the server - or
/// it is the parent's own statistics, which autovacuum never gathers.
/// </para>
/// </remarks>
[HealthCheck("T08", HealthArea.Tables, HealthCostTier.Catalog, "Never analysed, or stale statistics",
    Variants = [StaleVariant, NeverAnalysedVariant, SinceRestartVariant])]
internal sealed class StaleStatisticsCheck : HealthCheck<StatisticsFacts>
{
    /// <summary>The variant of statistics stale against the table's own trigger.</summary>
    public const string StaleVariant = "stale";

    /// <summary>The variant of a table with no statistics at all.</summary>
    public const string NeverAnalysedVariant = "never-analysed";

    /// <summary>The database-level variant of counters that began again under every table.</summary>
    public const string SinceRestartVariant = "since-restart";

    /// <summary>Live rows - or rows read - below which a never-analysed table is not worth a finding.</summary>
    public const long MinRows = 1_000;

    /// <summary>How far past twice its analyze trigger <c>n_mod_since_analyze</c> must be.</summary>
    public const long MinRowsPast = 10_000;

    /// <summary>The estimated rows from which a table counts towards <c>since-restart</c>.</summary>
    public const long RestartMinRows = 10_000;

    /// <summary>How many of the unanalysed tables <c>since-restart</c> names.</summary>
    public const int RestartNamed = 5;

    /// <summary>
    /// How long after the counters began again <c>since-restart</c> is said: a day, the window the checks that judge rates per
    /// day wait for (<see cref="StatisticsWindow.CoversADay" />) - the quiet it explains.
    /// </summary>
    /// <remarks>
    /// Not <see cref="StatisticsWindow.LooksReset" />, which is true for the one sweep that first sees a reset: the finding is
    /// shown for as long as the window is that young, across sweeps, and clears earlier once the tables are analysed.
    /// </remarks>
    public static readonly TimeSpan RestartWindow = StatisticsWindow.Day;

    /// <summary>How long ago the last analyze must have been for statistics to count as stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>How long the condition must hold while autovacuum is on.</summary>
    public static readonly TimeSpan PersistFor = TimeSpan.FromMinutes(10);

    /// <summary>The series T08 writes in every sweep it runs in.</summary>
    internal const string SweptSeries = "tables.statistics.swept";

    /// <summary>The series of a table whose statistics are wrong, written as 1.</summary>
    internal const string WrongSeries = "tables.statistics-wrong:";

    /// <inheritdoc />
    public override async Task<StatisticsFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context).ConfigureAwait(false);

        // since-restart's input: the statistics window's signals, the sweep's one read shared with C08 and C11. The sweep
        // computes the window from them after its gatherers and hands it to Evaluate (HealthEvaluation.Statistics).
        StatisticsReading? statistics;

        try
        {
            statistics = await StatisticsWindow.ReadingAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failure there costs only since-restart: the tables' own verdicts stand on the table read, and the window's
            // failure is C11's row to show, with its reason.
            statistics = null;
        }

        return new StatisticsFacts(context.Now, tables) { Statistics = statistics };
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(StatisticsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        yield return new HealthSamplePart(SweptSeries, 1);

        foreach (TableFacts table in facts.Tables)
        {
            if (ProblemOf(table, facts.ReadAt) is StatisticsProblem.NeverAnalysed or StatisticsProblem.Stale)
            {
                yield return new HealthSamplePart(WrongSeries + table.Key, 1);
            }
        }
    }

    /// <summary>
    /// Whether the table - or, for a partitioned table, one of its partitions - has no statistics at all, with rows in it
    /// or rows read from it.
    /// </summary>
    internal static bool NeverAnalysed(TableFacts table)
    {
        ArgumentNullException.ThrowIfNull(table);

        HealthTableRow row = table.Row;

        if (row.IsPartitioned)
        {
            return row.UnanalyzedLeaves > 0 && (row.UnanalyzedLiveRows >= MinRows || row.UnanalyzedRowsRead >= MinRows);
        }

        long read = (row.SequentialRowsRead ?? 0) + (row.IndexRowsFetched ?? 0);
        return row.EstimatedRows < 0 && table.LastAnalyzeOfAnyKind is null && (row.LiveRows >= MinRows || read >= MinRows);
    }

    /// <summary>What is wrong with a table's statistics at <paramref name="at" />, before persistence is considered.</summary>
    internal static StatisticsProblem ProblemOf(TableFacts table, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (NeverAnalysed(table))
        {
            return StatisticsProblem.NeverAnalysed;
        }

        if (table.Row.Analyze is { } analyze
            && analyze.Counter - (2 * analyze.Trigger) >= MinRowsPast
            && (analyze.Last is not { } last || at - last > StaleAfter))
        {
            return StatisticsProblem.Stale;
        }

        return table.Row is { IsPartitioned: true, ParentEstimatedRows: < 0 } && table.Rows >= MinRows
            ? StatisticsProblem.ParentNeverAnalysed
            : StatisticsProblem.None;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(StatisticsFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [];
        int watching = 0;

        foreach (TableFacts table in facts.Tables)
        {
            StatisticsProblem problem = ProblemOf(table, facts.ReadAt);
            if (problem == StatisticsProblem.None)
            {
                continue;
            }

            DateTimeOffset? since = TableHistory.HeldSince(evaluation.History, SweptSeries, WrongSeries + table.Key);
            TimeSpan held = since is { } start ? facts.ReadAt - start : TimeSpan.Zero;

            // Autovacuum never analyses a partitioned parent, so there is nothing to wait for there.
            if (problem != StatisticsProblem.ParentNeverAnalysed && !table.AutovacuumOff && held < PersistFor)
            {
                watching++;
                continue;
            }

            findings.Add(Finding(table, problem, held, evaluation));
        }

        // The statistics window: the sweep's, or - outside a sweep that computed one - from this check's own reading and the
        // history, as CAP's contract has an evaluator do.
        StatisticsWindow? window = evaluation.Statistics
            ?? (facts.Statistics is { } reading ? StatisticsWindow.Compute(reading, evaluation.History) : null);
        StatisticsFacts judged = facts.Restart is null && RestartOf(window) is { } began ? facts with { Restart = began } : facts;

        if (SinceRestart(judged) is { } restart)
        {
            findings.Add(restart);
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(watching == 0
            ? null
            : "Watching " + watching.ToString(CultureInfo.InvariantCulture) + " table" + (watching == 1 ? string.Empty : "s")
              + " whose statistics are behind: a finding needs that to hold for " + TableText.Span(PersistFor)
              + ", because autovacuum analyses a freshly loaded table within a minute or two.");
    }

    /// <summary>
    /// When and why the counters began again, from the statistics window - or <see langword="null" /> while it says they have
    /// counted through the server's last restart (<see cref="StatisticsResetReason.None" />), once they have counted for
    /// <see cref="RestartWindow" /> without looking reset, or when there is no window.
    /// </summary>
    /// <param name="window">The sweep's window (<see cref="HealthEvaluation.Statistics" />), or one computed from the reading.</param>
    internal static StatisticsRestart? RestartOf(StatisticsWindow? window) =>
        window is { } began && began.Reason != StatisticsResetReason.None && !began.Covers(RestartWindow)
            ? new StatisticsRestart(began.Since, RestartReason(began.Reason))
            : null;

    /// <summary>A reason, for the finding's parenthesis: "Autovacuum's counters began again at … (a crash …)".</summary>
    internal static string RestartReason(StatisticsResetReason reason) => reason switch
    {
        StatisticsResetReason.ServerStarted => "the server's start, which kept no statistics or was its first",
        StatisticsResetReason.ManualReset => "a statistics reset",
        StatisticsResetReason.Crash => "a restart without the statistics: a crash, an immediate shutdown or a restore",
        StatisticsResetReason.Restore => "a point-in-time recovery or a restore from a base backup",
        StatisticsResetReason.Failover => "a failover to another server",
        StatisticsResetReason.FreshCluster => "a new cluster: pg_upgrade, initdb or a restore from a dump",
        StatisticsResetReason.OtherInstance => "another instance answering",
        _ => "a reset nothing read explains",
    };

    /// <summary>
    /// The <c>since-restart</c> finding, or <see langword="null" />: the counters began again, and the large tables nothing has
    /// analysed since hold at least half of the judged tables' estimated rows.
    /// </summary>
    internal static HealthFinding? SinceRestart(StatisticsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Restart is not { } restart)
        {
            return null;
        }

        double total = facts.Tables.Sum(static x => (double) Math.Max(x.Row.EstimatedRows, 0));

        List<TableFacts> unanalysed =
        [
            .. facts.Tables
                .Where(x => x.Row.EstimatedRows >= RestartMinRows && (x.LastAnalyzeOfAnyKind is not { } last || last < restart.Since))
                .OrderByDescending(static x => x.Row.EstimatedRows),
        ];

        double held = unanalysed.Sum(static x => (double) x.Row.EstimatedRows);

        if (total <= 0 || held < total / 2)
        {
            return null;
        }

        // Named in a finding shown at the check's gate with no per-visitor schema check: a table outside the source's own
        // schemas is worded so that it names neither it nor its schema (StudioTableText.NoteName).
        string largest = string.Join(", ", unanalysed.Take(RestartNamed).Select(static x => x.Table.NoteName))
            + (unanalysed.Count > RestartNamed ? " and " + SessionText.Number(unanalysed.Count - RestartNamed) + " more" : string.Empty);

        return new HealthFinding
        {
            Code = "T08",
            Variant = SinceRestartVariant,
            Severity = HealthSeverity.Info,

            // The database's, like T12's: a table outside the source's own schemas is named only by NoteName's words, which
            // name neither it nor its schema, so the finding names no gated schema and is shown at the check's gate.
            GatedSchemas = [],
            Title = "Autovacuum's counters began again at " + TableText.When(restart.Since) + " (" + restart.Reason
                + "); rows that died before are invisible to it, and to T07, until each table crosses its trigger again",
            WhyItMatters =
                "Autovacuum decides from the per-table counters - dead rows, inserts and changes since the last analyze - and they began "
                + "again at zero, so what accumulated before is uncounted: a table can be bloated or its statistics stale with nothing "
                + "here to say so. " + SessionText.Count(unanalysed.Count, "table") + " of at least " + TableText.Count(RestartMinRows)
                + " estimated rows have not been analysed since, and they hold " + TableText.Percent(held / total)
                + " of the judged tables' rows. It clears itself once they are analysed. The manual's own post-upgrade step is "
                + "vacuumdb --all --analyze-only, run by whoever runs the server; it rebuilds the statistics and the counters' "
                + "picture with them.",
            Evidence =
            [
                new HealthEvidence("Counters began again", TableText.When(restart.Since) + " - " + restart.Reason),
                new HealthEvidence("Not analysed since", SessionText.Count(unanalysed.Count, "table") + ", "
                    + TableText.Percent(held / total) + " of the judged rows"),
                new HealthEvidence("The largest of them", largest),
            ],
            Caveat = "Estimated rows are reltuples. It is shown even while other checks withhold verdicts that depend on the counters, "
                + "because it is what explains their quiet.",
        };
    }

    private static HealthFinding Finding(TableFacts table, StatisticsProblem problem, TimeSpan held, HealthEvaluation evaluation)
    {
        StudioTable declared = table.Table;
        HealthTableRow row = table.Row;
        bool partitioned = row.IsPartitioned;

        return problem switch
        {
            StatisticsProblem.Stale => Stale(table, held, evaluation),
            StatisticsProblem.ParentNeverAnalysed => new HealthFinding
            {
                Code = "T08",
                Variant = NeverAnalysedVariant,
                Severity = HealthSeverity.Info,
                Subject = declared.Key,
                DocumentTypes = declared.DocumentTypes,
                GatedSchemas = declared.GatedSchemas,
                Title = "The partitioned table " + declared.Key + " has never been analysed as a whole",
                WhyItMatters =
                    "Its partitions have their own statistics, but the parent's - which the planner uses for a query that reads several "
                    + "partitions at once, a join across them or a grouping over the whole table - exist only once somebody runs ANALYZE "
                    + "on the parent itself: autovacuum does not analyse partitioned tables. Whoever owns the schema schedules an "
                    + "ANALYZE of the parent where such queries matter; a query that reads one partition is unaffected.",
                Evidence =
                [
                    new HealthEvidence("Table", declared.Describe()),
                    new HealthEvidence("Partitions", TableText.Count(row.PartitionLeaves)),
                    new HealthEvidence("Rows (its partitions')", TableText.Rows(table.Rows, row.LiveRows is null)),
                    new HealthEvidence("Its own reltuples", "-1 (never analysed)"),
                ],
                Caveat = "Read in pg_class; a partitioned table's own reltuples is set only by an ANALYZE of it.",
            },
            _ => new HealthFinding
            {
                Code = "T08",
                Variant = NeverAnalysedVariant,
                Severity = HealthSeverity.Warning,
                Subject = declared.Key,
                DocumentTypes = declared.DocumentTypes,
                GatedSchemas = declared.GatedSchemas,
                Title = partitioned
                    ? SessionText.Count(row.UnanalyzedLeaves, "partition") + " of " + declared.Key + " have never been analysed"
                    : NeverTitle(table),
                WhyItMatters =
                    "Postgres has no statistics for " + (partitioned ? "them" : "this table") + " at all (reltuples is -1), so the planner "
                    + "guesses how many rows " + (partitioned ? "they hold" : "it has") + " and how its values are spread, and the studio's "
                    + "own ~ counts cannot estimate it either. Autovacuum analyses a table only once enough of it has changed - "
                    + TableText.Count(Autovacuum.Rounded(row.Analyze?.Trigger ?? 50)) + " modifications here - so one that is mostly read, "
                    + "as every table is after pg_upgrade from PostgreSQL 14 to 17, which carries no statistics over, is planned on "
                    + "defaults indefinitely. Whoever runs the server rebuilds them: vacuumdb --all --analyze-in-stages (PostgreSQL 17 and "
                    + "before), or on 18 vacuumdb --all --analyze-in-stages --missing-stats-only followed by vacuumdb --all --analyze-only - "
                    + "or the table's own ANALYZE."
                    + Autovacuum.State(table, "autovacuum_analyze_scale_factor", Autovacuum.Number(row.Analyze?.ScaleFactor ?? 0.1), evaluation.Text),
                Evidence =
                [
                    new HealthEvidence("Table", declared.Describe()),
                    new HealthEvidence("Estimated rows (reltuples)", partitioned
                        ? (row.EstimatedRows < 0 ? "not analysed" : TableText.Rows(row.EstimatedRows) + " in the analysed partitions")
                        : "not analysed"),
                    new HealthEvidence("Live rows", TableText.Count(partitioned ? row.UnanalyzedLiveRows : row.LiveRows ?? 0)
                        + (partitioned ? " in the partitions never analysed" : string.Empty)),
                    new HealthEvidence("Rows read since the counters began", TableText.Count(partitioned
                        ? row.UnanalyzedRowsRead
                        : (row.SequentialRowsRead ?? 0) + (row.IndexRowsFetched ?? 0))),
                    new HealthEvidence("Last analyze", TableText.When(row.LastAnalyze)),
                    new HealthEvidence("Last autoanalyze", TableText.When(row.LastAutoanalyze)),
                ],
                Caveat = Caveat(table, held),
                Remedy = table.AutovacuumOffForTable ? Autovacuum.Remedy(evaluation.TableRemedies, table, "autovacuum_analyze_scale_factor", "0.1", "Autovacuum is switched off for it.") : null,
            },
        };
    }

    private static string NeverTitle(TableFacts table)
    {
        long live = table.Row.LiveRows ?? 0;
        long read = (table.Row.SequentialRowsRead ?? 0) + (table.Row.IndexRowsFetched ?? 0);

        // "has 2,000 rows" while the collector counts them; after pg_upgrade it counts none, and what it has is reads.
        return live >= MinRows
            ? table.Key + " has " + TableText.Count(live) + " rows and has never been analysed"
            : table.Key + " has been read " + TableText.Count(read) + " rows deep and has never been analysed";
    }

    private static HealthFinding Stale(TableFacts table, TimeSpan held, HealthEvaluation evaluation)
    {
        StudioTable declared = table.Table;
        HealthTableRow row = table.Row;
        AutovacuumUnit analyze = row.Analyze!;
        bool partitioned = row.IsPartitioned;
        double? lower = Autovacuum.LowerScaleFactor(analyze.ScaleFactor);
        double times = analyze.Trigger > 0 ? analyze.Counter / analyze.Trigger : double.PositiveInfinity;

        List<HealthEvidence> evidence =
        [
            new("Table", declared.Describe()),
            new("Modified since the last analyze", TableText.Count(analyze.Counter) + (partitioned ? " in the partition furthest behind" : string.Empty)),
            new("Its analyze trigger", Autovacuum.Explain(analyze, "autovacuum_analyze", row.StorageOptions, partitioned)),
            new("Past its trigger", times.ToString("0.#", CultureInfo.InvariantCulture) + " times"),
            new("Estimated rows (reltuples)", row.EstimatedRows < 0 ? "not analysed" : TableText.Rows(row.EstimatedRows)),
            new("Last analyze (either kind)", TableText.When(analyze.Last)),
        ];

        if (partitioned)
        {
            evidence.Add(new HealthEvidence("Partitions past their trigger", TableText.Count(row.PastAnalyzeTrigger) + " of " + TableText.Count(row.PartitionLeaves)));
        }
        else
        {
            evidence.Add(new HealthEvidence("Last autoanalyze", TableText.When(row.LastAutoanalyze)));
        }

        return new HealthFinding
        {
            Code = "T08",
            Variant = StaleVariant,
            Severity = HealthSeverity.Warning,
            Subject = declared.Key,
            DocumentTypes = declared.DocumentTypes,
            GatedSchemas = declared.GatedSchemas,
            Title = TableText.Count(analyze.Counter) + " rows of " + (partitioned ? "a partition of " : string.Empty) + declared.Key
                + " have changed since it was last analysed, " + times.ToString("0.#", CultureInfo.InvariantCulture) + " times the "
                + TableText.Count(Autovacuum.Rounded(analyze.Trigger)) + " that trigger its autoanalyze",
            WhyItMatters =
                "Autoanalyze acts on its own trigger, " + TableText.Count(Autovacuum.Rounded(analyze.Trigger)) + " changed rows for this table, "
                + "and this one is more than twice past it with no analyze in the last hour - so the planner is choosing plans for a table "
                + "that is no longer there, and the studio's ~ counts are off by as much. A wrong row estimate is how a query that should "
                + "use an index ends up scanning the table, or a join picks a nested loop over a million rows."
                + Autovacuum.State(table, "autovacuum_analyze_scale_factor", Autovacuum.Number(lower ?? analyze.ScaleFactor), evaluation.Text),
            Evidence = evidence,
            Caveat = Caveat(table, held),
            Remedy = lower is { } suggested
                ? Autovacuum.Remedy(
                    evaluation.TableRemedies,
                    table,
                    "autovacuum_analyze_scale_factor",
                    Autovacuum.Number(suggested),
                    "Its analyze trigger is now " + TableText.Count(Autovacuum.Rounded(analyze.Trigger)) + "; at " + Autovacuum.Number(suggested)
                    + " it would be " + TableText.Count(Autovacuum.Rounded(analyze.Threshold + (suggested * analyze.Tuples)))
                    + ". A DBA's ANALYZE fixes the statistics now; this keeps them fixed.")
                : table.AutovacuumOffForTable ? Autovacuum.Remedy(evaluation.TableRemedies, table, "autovacuum_analyze_scale_factor", "0.1", "Autovacuum is switched off for it.")
                : null,
        };
    }

    private static string Caveat(TableFacts table, TimeSpan held) =>
        "The modification count and analyze times are the statistics collector's, which a pg_stat_reset() or a crash "
        + "clears. " + (held > TimeSpan.Zero ? "Seen for " + TableText.Span(held) + "." : "Seen in this sweep.")
        + (table.Row.IsPartitioned
            ? " This is a partitioned table, judged by its partitions' own statistics: autovacuum never analyses a "
              + "partitioned parent."
            : string.Empty);
}
