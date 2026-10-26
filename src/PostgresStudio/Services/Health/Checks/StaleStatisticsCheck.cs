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
/// When and why the counters began again, while the statistics window says they look reset - or <see langword="null" />.
/// </param>
internal sealed record StatisticsFacts(DateTimeOffset ReadAt, IReadOnlyList<TableFacts> Tables, StatisticsRestart? Restart = null);

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
/// (a crash, a failover, <c>pg_upgrade</c>, a reset), and the tables of at least <see cref="RestartMinRows" /> estimated rows
/// not analysed since hold at least half the judged rows: what explains a quiet T07.</item>
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
    public const long MinRows = HealthTableQueries.UnanalyzedMinRows;

    /// <summary>How far past twice its analyze trigger <c>n_mod_since_analyze</c> must be.</summary>
    public const long MinRowsPast = 10_000;

    /// <summary>The estimated rows from which a table counts towards <c>since-restart</c>.</summary>
    public const long RestartMinRows = 10_000;

    /// <summary>How many of the unanalysed tables <c>since-restart</c> names.</summary>
    public const int RestartNamed = 5;

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

        // since-restart's input is the statistics window's (W5-CAP's StatisticsWindow), which this packet does not have
        // yet: until it is wired here, the variant is judged on what a test states and never in a sweep.
        return new StatisticsFacts(context.Now, await HealthTables.TablesAsync(context).ConfigureAwait(false));
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

        // A partitioned table: the statement judged each partition on its own and counted the ones past the line - never a
        // sum, so many small tenants' partitions are not one big table never analysed (W5-V1 review).
        if (row.IsPartitioned)
        {
            return row.UnanalyzedLeaves > 0;
        }

        return row.EstimatedRows < 0 && table.LastAnalyzeOfAnyKind is null && BigEnough(row);
    }

    /// <summary>
    /// Whether a table never analysed is big enough for that to matter: <see cref="MinRows" /> live rows, or that many read
    /// by sequential scans that read that many on average. Rows read alone are not a size: a table whose indexes were built
    /// while it was empty keeps <c>reltuples = -1</c> - every Marten document table - and a five-row table read by its key two
    /// thousand times has read two thousand rows; nor are rows read with no live rows counted, which is the pg_upgrade shape
    /// but also every table's after a crash, a failover or <c>pg_stat_reset()</c> - which <c>since-restart</c> speaks for
    /// (W5-V1 review). <see cref="HealthTableQueries.TablesSql" /> applies the same rule to each partition.
    /// </summary>
    internal static bool BigEnough(HealthTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        long live = row.LiveRows ?? 0;
        long read = (row.SequentialRowsRead ?? 0) + (row.IndexRowsFetched ?? 0);
        long scans = row.SequentialScans ?? 0;

        return live >= MinRows
            || (read >= MinRows && scans > 0 && (row.SequentialRowsRead ?? 0) >= MinRows * scans);
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

        if (SinceRestart(facts) is { } restart)
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
        double times = analyze.Trigger > 0 ? analyze.Counter / analyze.Trigger : double.PositiveInfinity;

        // As T07's (W5-V1 review): past its trigger, a lower scale factor changes nothing; what keeps autoanalyze away is
        // named from what the sweep read - a lock it skips, every worker busy (T16), throttling - and a scale factor is
        // offered only where it would at least halve the trigger. No workers read here: the server's own throttling is T07's.
        AutovacuumHindrance hindrance = table.AutovacuumOff
            ? AutovacuumHindrance.None
            : AutovacuumHindrances.Of(table, analyze, workers: null, evaluation.History);
        (double ScaleFactor, double Trigger)? halving = AutovacuumHindrances.HalvingScaleFactor(analyze, 1, cap: null);

        List<HealthEvidence> evidence =
        [
            new("Table", declared.Describe()),
            new("Modified since the last analyze", TableText.Count(analyze.Counter) + (partitioned ? " in the partition furthest behind" : string.Empty)),
            new("Its analyze trigger", Autovacuum.Explain(analyze, "autovacuum_analyze", row.StorageOptions, partitioned)),
            new("Past its trigger", times.ToString("0.#", CultureInfo.InvariantCulture) + " times"),
            new("Estimated rows (reltuples)", row.EstimatedRows < 0 ? "not analysed" : TableText.Rows(row.EstimatedRows)),
            new("Last analyze (either kind)", TableText.When(analyze.Last)),
        ];

        if (!table.AutovacuumOff)
        {
            evidence.Add(new HealthEvidence("What keeps autoanalyze away", AutovacuumHindrances.Label(hindrance, table, workers: null)));
        }

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
                + "use an index ends up scanning the table, or a join picks a nested loop over a million rows. A lower scale factor would "
                + "not change that, because the table is already past its trigger."
                + (table.AutovacuumOff ? string.Empty : AutovacuumHindrances.Sentence(hindrance, table, workers: null))
                + Autovacuum.State(table, "autovacuum_analyze_scale_factor", Autovacuum.Number(halving?.ScaleFactor ?? analyze.ScaleFactor), evaluation.Text),
            Evidence = evidence,
            Caveat = Caveat(table, held),
            Remedy = table.AutovacuumOffForTable
                ? Autovacuum.Remedy(evaluation.TableRemedies, table, "autovacuum_analyze_scale_factor", "0.1", "Autovacuum is switched off for it.")
                : !row.AutovacuumEnabled
                    ? null
                    : hindrance != AutovacuumHindrance.None
                        ? AutovacuumHindrances.Remedy(hindrance, table, workers: null, PostgresPlatform.SelfHosted, evaluation)
                        : halving is { } lower
                            ? Autovacuum.Remedy(
                                evaluation.TableRemedies,
                                table,
                                "autovacuum_analyze_scale_factor",
                                Autovacuum.Number(lower.ScaleFactor),
                                "Its analyze trigger is now " + TableText.Count(Autovacuum.Rounded(analyze.Trigger)) + "; at "
                                + Autovacuum.Number(lower.ScaleFactor) + " it would be " + TableText.Count(Autovacuum.Rounded(lower.Trigger))
                                + ", so autoanalyze starts sooner next time. A DBA's ANALYZE fixes the statistics now.")
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
