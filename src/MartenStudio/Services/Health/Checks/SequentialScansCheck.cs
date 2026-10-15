namespace MartenStudio.Services.Health.Checks;

/// <summary>What T01 reads: every declared table's scan counters.</summary>
/// <param name="ReadAt">When the counters were read.</param>
/// <param name="Tables">The declared tables that exist, with the catalog's row for each.</param>
/// <param name="Database">The database-wide facts: when the counters began, how parallel a scan may be.</param>
internal sealed record SequentialScanFacts(DateTimeOffset ReadAt, IReadOnlyList<TableFacts> Tables, DatabaseStatistics Database);

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
/// <b>A partitioned table is judged by its partitions</b>, from leaf figures the catalog walk rolls up: the
/// size line applies to its largest partition, and one scan covers its mean partition. Fifty tenants of four
/// thousand rows each, every query scanning its own tenant's partition, is the right plan and no finding.
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
        IReadOnlyList<TableFacts> tables = await MartenTables.TablesAsync(context).ConfigureAwait(false);

        return new SequentialScanFacts(context.Now, tables, DatabaseStatistics.Of(tables));
    }

    /// <summary>
    /// Whether a table is large enough to judge: an ordinary table by its live rows, a partitioned one by its
    /// largest partition's - a query that scans one tenant's partition scans that partition, not the table, and
    /// fifty small partitions are fifty small tables.
    /// </summary>
    internal static bool IsLarge(TableFacts table) =>
        (table.Row.IsPartitioned ? table.Row.LargestLeafRows ?? 0 : table.Rows) >= LargeTableRows;

    /// <summary>
    /// The rows one whole scan reads: the table's for an ordinary table, the mean partition's for a partitioned one,
    /// which is what a scan pruned to one partition reads.
    /// </summary>
    internal static double ScanUnit(TableFacts table) =>
        table.Row.IsPartitioned ? (double) table.Rows / Math.Max(1, table.Row.PartitionLeaves) : table.Rows;

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(SequentialScanFacts facts)
    {
        foreach (TableFacts table in facts.Tables)
        {
            if (IsLarge(table) && table.Row is { SequentialScans: { } scans, SequentialRowsRead: { } read })
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
        int large = 0;
        int watched = 0;

        foreach (TableFacts table in facts.Tables)
        {
            if (!IsLarge(table))
            {
                continue;
            }

            large++;

            CounterDelta? scans = TableHistory.Delta(evaluation.History.Trend(ScansSeries + table.Key));
            CounterDelta? read = TableHistory.Delta(evaluation.History.Trend(RowsReadSeries + table.Key));

            if (scans is null || read is null)
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
                findings.Add(Finding(table, scans, read, wholeTableReads, facts, evaluation));
            }
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(large == 0
            ? "No table this store declares (or, for a partitioned table, no partition of one) has "
              + TableText.Count(LargeTableRows) + " live rows or more."
            : watched == 0
                ? "Watching " + TableText.Count(large) + " large table" + (large == 1 ? string.Empty : "s")
                  + ": a scan rate needs two samples, one interval apart."
                : null);
    }

    private static HealthFinding Finding(
        TableFacts table,
        CounterDelta scans,
        CounterDelta read,
        double wholeTableReads,
        SequentialScanFacts facts,
        HealthEvaluation evaluation)
    {
        MartenTable declared = table.Table;
        TimeSpan window = read.Span;
        string times = Math.Floor(wholeTableReads).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

        return new HealthFinding
        {
            Code = "T01",
            Severity = HealthSeverity.Warning,
            Subject = declared.Key,
            DocumentTypes = declared.DocumentTypes,
            Title = table.Row.IsPartitioned
                ? "Partition-wide sequential scans of " + declared.Key + " read the table's worth " + times + " times in "
                  + TableText.Span(window)
                : "Sequential scans read the whole of " + declared.Key + " " + times + " times in " + TableText.Span(window),
            WhyItMatters =
                "A sequential scan reads every page of the table to find the rows a query wants. On "
                + TableText.Rows(table.Rows) + " rows that is the query's whole cost, it grows with the table, and while it runs it "
                + "fills the buffer cache with pages nobody else asked for. A query that filters, sorts or joins on a member no "
                + "index covers does this every time it runs.",
            Evidence =
            [
                new HealthEvidence("Table", declared.Describe()),
                new HealthEvidence("Live rows", TableText.Rows(table.Rows)),
                new HealthEvidence("Sequential scans in the window", TableText.Count(scans.Change)),
                new HealthEvidence("Rows they read", TableText.Count(read.Change)),
                new HealthEvidence("Whole-table reads", wholeTableReads.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)),
                new HealthEvidence("Window", TableText.Span(window) + ", " + TableText.When(read.From) + " to " + TableText.When(read.To)),
            ],
            Caveat =
                TableText.CountersSince(facts.Database.StatsReset, evaluation.Now)
                + " A parallel scan counts once per worker. The studio's own exact counts, pg_dump and a one-off report are "
                + "sequential scans too; which statement it was is T02's to say.",
            Remedy = declared is { Kind: MartenTableKind.Document, TypeName: { } type }
                ? new HealthRemedy(
                    "// The member the slow query filters, sorts or joins on:\n"
                    + "opts.Schema.For<" + type + ">().Index(x => x.FilteredMember);\n"
                    + "// or, where it is also sorted on or compared as a column:\n"
                    + "opts.Schema.For<" + type + ">().Duplicate(x => x.FilteredMember);",
                    "Query in Marten mode, on the " + declared.Alias + " collection, judges a where clause: its index advice says "
                    + "whether an index serves it and names the member. Every write maintains every index, so add the one the slow "
                    + "query needs and no other.")
                : null,
            Link = declared is { Kind: MartenTableKind.Document, Alias: { } alias }
                ? new HealthLink(
                    "Open the index advisor in Query",
                    "query",
                    [new KeyValuePair<string, string?>("mode", "marten"), new KeyValuePair<string, string?>("type", alias)])
                : null,
        };
    }
}
