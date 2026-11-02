using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T01 reads: every judged table's scan counters.</summary>
/// <param name="ReadAt">When the counters were read.</param>
/// <param name="Tables">The declared tables that exist, with the catalog's row for each.</param>
/// <param name="Database">The database-wide facts: when the counters began, how parallel a scan may be.</param>
internal sealed record SequentialScanFacts(DateTimeOffset ReadAt, IReadOnlyList<TableFacts> Tables, DatabaseStatistics Database)
{
    /// <summary>
    /// The statistics window's signals and the counters' oldest recorded moment (<see cref="UsageGuard" />, issue #8 G4), or
    /// <see langword="null" /> in a unit test that judges the window through <see cref="HealthEvaluation.Statistics" /> alone.
    /// </summary>
    public UsageWindowReading? Usage { get; init; }
}

/// <summary>
/// T01: sequential scans keep reading the whole of a large Marten table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rates, never totals.</b> <c>seq_scan</c> and <c>seq_tup_read</c> count from the last statistics reset,
/// which may be a year ago, so a total would report last spring's bulk load for ever. The check samples both
/// for every large table into <see cref="HealthHistory" /> and judges the change across the window the
/// history holds - an hour at the default interval - and says nothing until two samples exist.
/// </para>
/// <para>
/// <b>"The whole table" is measured in rows, not scans.</b> A parallel scan counts once per participant and a
/// partitioned table once per partition scanned, so <c>seq_tup_read / seq_scan</c> is not the size of one
/// scan. What is judged is how many times the table's worth of live rows sequential scans read in the window
/// (<see cref="MinWholeTableReads" />), and that the average scan, allowing for
/// <c>max_parallel_workers_per_gather</c>, read a good part of what one scan covers
/// (<see cref="WholeTableFraction" />), so a million cheap <c>limit 1</c> scans do not look like full ones.
/// </para>
/// <para>
/// <b>A partitioned table is judged by its largest partition</b>, from leaf figures the catalog walk rolls up:
/// the size line applies to it, and so does the per-scan test - an average scan must read a good part of the
/// largest partition. Fifty tenants of four thousand rows each, every query scanning its own tenant's partition,
/// is the right plan and no finding; so is one large tenant beside many small ones whose per-tenant scans never
/// touch the large one. The counters are rolled up, so a scan of the large partition and a scan of a small one
/// cannot be told apart: what is lost is a table whose whole-partition scans of its large partitions are
/// outnumbered by scans of small ones, which pull the average down - the conservative side of the line.
/// </para>
/// <para>
/// <b>Never over a short or reset window</b> (issue #8 G4, <see cref="UsageGuard" />): while the counters have counted for
/// less than seven days, or look reset, there is no finding and the status row says why. The samples go on being taken,
/// so the rate is ready the sweep the window covers.
/// </para>
/// </remarks>
[HealthCheck("T01", HealthArea.Tables, HealthCostTier.Catalog, "Sequential scans on a large table")]
internal sealed class SequentialScansCheck : HealthCheck<SequentialScanFacts>
{
    /// <summary>Below this many live rows a sequential scan is often the planner's right answer.</summary>
    public const long LargeTableRows = 100_000;

    /// <summary>How many times the table's worth of rows sequential scans must read in the window.</summary>
    public const double MinWholeTableReads = 3;

    /// <summary>The share of the table an average scan (per participant) must read to count as a whole-table scan.</summary>
    public const double WholeTableFraction = 0.5;

    /// <summary>The series of a table's <c>seq_scan</c>.</summary>
    internal const string ScansSeries = "tables.seq-scans:";

    /// <summary>The series of a table's <c>seq_tup_read</c>.</summary>
    internal const string RowsReadSeries = "tables.seq-rows-read:";

    /// <inheritdoc />
    public override async Task<SequentialScanFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context).ConfigureAwait(false);
        UsageWindowReading usage = await UsageGuard.ReadAsync(context).ConfigureAwait(false);

        return new SequentialScanFacts(context.Now, tables, DatabaseStatistics.Of(tables)) { Usage = usage };
    }

    /// <summary>
    /// Whether a table is large enough to judge: an ordinary table by its live rows, a partitioned one by its
    /// largest partition's - a query that scans one tenant's partition scans that partition, not the table, and
    /// fifty small partitions are fifty small tables.
    /// </summary>
    internal static bool IsLarge(TableFacts table) =>
        (table.Row.IsPartitioned ? table.Row.LargestLeafRows ?? 0 : table.Rows) >= LargeTableRows;

    /// <summary>
    /// Whether T01 judges a table at all: a large one whose index design is its owner's. A library's table
    /// (<see cref="StudioTable.ExemptFromIndexDesign" />) is scanned however the library designed it, and the index it may
    /// lack is not the host's to add (issue #8, G1).
    /// </summary>
    internal static bool IsJudged(TableFacts table) => !table.Table.ExemptFromIndexDesign && IsLarge(table);

    /// <summary>
    /// The rows one whole scan reads: the table's for an ordinary table, and for a partitioned one its
    /// <b>largest partition's</b> - never the mean. The size line is the largest partition's, so the per-scan
    /// test must be too: against the mean, one large tenant among many small ones makes the mean small, and every
    /// per-tenant scan of a small partition passes for a whole-table scan though the large one was never read.
    /// </summary>
    internal static double ScanUnit(TableFacts table) =>
        table.Row.IsPartitioned ? Math.Max(table.Row.LargestLeafRows ?? 0, 1) : table.Rows;

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(SequentialScanFacts facts)
    {
        foreach (TableFacts table in facts.Tables)
        {
            if (IsJudged(table) && table.Row is { SequentialScans: { } scans, SequentialRowsRead: { } read })
            {
                yield return new HealthSamplePart(ScansSeries + table.Key, scans);
                yield return new HealthSamplePart(RowsReadSeries + table.Key, read);
            }
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(SequentialScanFacts facts, HealthEvaluation evaluation)
    {
        List<HealthFinding> findings = [];
        int large = facts.Tables.Count(IsJudged);
        int watched = 0;

        if (large == 0)
        {
            return HealthVerdict.Quiet(
                "No table the studio judges (or, for a partitioned table, no partition of one) has " + TableText.Count(LargeTableRows)
                + " live rows or more.");
        }

        // Issue #8 G4: no verdict over statistics under seven days old or looking reset.
        if (UsageGuard.For(facts.Usage, evaluation) is not { } usage)
        {
            return HealthVerdict.Quiet(UsageGuard.NotRead);
        }

        if (usage.Withheld(UsageGuard.MinimumWindow) is { } withheld)
        {
            return HealthVerdict.Quiet(withheld);
        }

        int young = 0;

        foreach (TableFacts table in facts.Tables)
        {
            if (!IsJudged(table))
            {
                continue;
            }

            // PostgreSQL 19 keeps a reset time per table. A table whose own counters were reset inside the window is judged over
            // their shorter one: not before they have counted for as long as the guard asks, and never over a span of samples that
            // began before the reset (a rate across it would mix two runs of the counter).
            if (OwnResetWithin(table, usage) is { } ownReset && facts.ReadAt - ownReset < UsageGuard.MinimumWindow)
            {
                young++;
                continue;
            }

            CounterDelta? scans = TableHistory.Delta(evaluation.History.Trend(ScansSeries + table.Key));
            CounterDelta? read = TableHistory.Delta(evaluation.History.Trend(RowsReadSeries + table.Key));

            if (scans is null || read is null || (OwnResetWithin(table, usage) is { } reset && reset > read.From))
            {
                continue;
            }

            watched++;

            if (scans.Change <= 0 || read.Change <= 0)
            {
                continue;
            }

            double wholeTableReads = read.Change / Math.Max(1, table.Rows);
            double participants = 1 + Math.Max(0, facts.Database.MaxParallelWorkersPerGather);
            double perScan = read.Change / scans.Change;
            bool readWholeUnit = perScan >= WholeTableFraction * ScanUnit(table) / participants;

            if (wholeTableReads >= MinWholeTableReads && readWholeUnit)
            {
                findings.Add(Finding(table, scans, read, wholeTableReads, usage, evaluation));
            }
        }

        string? youngNote = young switch
        {
            0 => null,
            1 => "1 table's own counters were reset less than 7 days ago and it was not judged.",
            _ => TableText.Count(young) + " tables' own counters were reset less than 7 days ago and were not judged.",
        };

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings, youngNote);
        }

        return HealthVerdict.Quiet(watched == 0 && large > young
            ? "Watching " + TableText.Count(large - young) + " large table" + (large - young == 1 ? string.Empty : "s")
              + ": a scan rate needs two samples, one interval apart."
              + (youngNote is null ? string.Empty : " " + youngNote)
            : youngNote);
    }

    /// <summary>
    /// When this table's own counters were last reset (PostgreSQL 19), if that is later than the start of the window the
    /// database's counters are judged over - or <see langword="null" /> for a table that counts from the window's start.
    /// </summary>
    internal static DateTimeOffset? OwnResetWithin(TableFacts table, UsageWindow usage) =>
        table.Row.CountersReset is { } own && own > usage.Since ? own : null;

    private static HealthFinding Finding(
        TableFacts table,
        CounterDelta scans,
        CounterDelta read,
        double wholeTableReads,
        UsageWindow usage,
        HealthEvaluation evaluation)
    {
        StudioTable declared = table.Table;
        TimeSpan window = read.Span;
        string times = Math.Floor(wholeTableReads).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset? ownReset = OwnResetWithin(table, usage);

        List<HealthEvidence> evidence =
        [
            new HealthEvidence("Table", declared.Describe()),
            new HealthEvidence("Live rows", TableText.Rows(table.Rows)),
            new HealthEvidence("Sequential scans in the window", TableText.Count(scans.Change)),
            new HealthEvidence("Rows they read", TableText.Count(read.Change)),
            new HealthEvidence("Whole-table reads", wholeTableReads.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
            new HealthEvidence("Window", TableText.Span(window) + ", " + TableText.When(read.From) + " to " + TableText.When(read.To)),
        ];

        // PostgreSQL 19: a table whose own counters were reset since the database's window began is judged over its own.
        if (ownReset is { } own)
        {
            evidence.Add(new HealthEvidence("Counters since", StatisticsWindow.When(own) + " (this table's own reset, PostgreSQL 19)"));
        }

        return new HealthFinding
        {
            Code = "T01",
            Severity = HealthSeverity.Warning,
            Subject = declared.Key,
            DocumentTypes = declared.DocumentTypes,
            GatedSchemas = declared.GatedSchemas,
            Title = table.Row.IsPartitioned
                ? "Partition-wide sequential scans of " + declared.Key + " read the table's worth " + times + " times in "
                  + TableText.Span(window)
                : "Sequential scans read the whole of " + declared.Key + " " + times + " times in " + TableText.Span(window),
            WhyItMatters =
                "A sequential scan reads every page of the table to find the rows a query wants. On "
                + TableText.Rows(table.Rows) + " rows that is the query's whole cost, it grows with the table, and while it runs it "
                + "fills the buffer cache with pages nobody else asked for. A query that filters, sorts or joins on a member no "
                + "index covers does this every time it runs.",
            Evidence = evidence,
            Caveat =
                "Counters " + (ownReset is { } ownSince
                    ? "since " + StatisticsWindow.When(ownSince) + " (this table's own reset, PostgreSQL 19)"
                    : usage.SinceText) + "; a usage verdict is never given over less than 7 days of them, or over counters "
                + "that look reset. A parallel scan counts once per worker. The studio's own exact counts, pg_dump and a one-off report are "
                + "sequential scans too; which statement it was is T02's to say.",
            Remedy = evaluation.TableRemedies?.IndexTheFilteredMember(declared),
            Link = declared is { Kind: StudioTableKind.DocumentTable, Alias: { } alias }
                ? new HealthLink(
                    "Open the index advisor in Query",
                    "query",
                    [new KeyValuePair<string, string?>("mode", "marten"), new KeyValuePair<string, string?>("type", alias)])
                : null,
        };
    }
}
