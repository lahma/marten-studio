using System.Globalization;
using System.Text.RegularExpressions;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Query;

/// <summary>One kind of plan insight: its stable code, its title (also its docs heading), and whether it needs <c>ANALYZE</c>.</summary>
/// <param name="Code">The code: <c>P</c> and two digits, never reused (hard rule 7, <c>docs/health/codes.md</c>).</param>
/// <param name="Title">What it finds, which is also its heading in <c>docs/health/plans.md</c>.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="NeedsAnalyze">Whether it reads what only <c>EXPLAIN ANALYZE</c> prints, so a plain plan cannot be judged.</param>
internal sealed record PlanInsightInfo(string Code, string Title, HealthSeverity Severity, bool NeedsAnalyze)
{
    /// <summary>The heading <c>docs/health/plans.md</c> must carry for it.</summary>
    public string DocsHeading => "## " + Code + " " + Title;

    /// <summary>The heading's anchor, as GitHub derives it.</summary>
    public string DocsAnchor => HealthCheckInfo.Anchor(Code + " " + Title);

    /// <summary>The published page and anchor, pinned to this build's release as a health check's is.</summary>
    public string DocsUrl => HealthCheckInfo.DocsRoot + PlanInsights.DocsFileName + "#" + DocsAnchor;
}

/// <summary>One insight about one node of one plan.</summary>
/// <param name="Insight">Which insight.</param>
/// <param name="NodeId">The node it is about (<see cref="PlanNode.Id" />).</param>
/// <param name="Node">The node in words: <c>Seq Scan on public.t</c>.</param>
/// <param name="Message">What the plan says, with its numbers.</param>
/// <param name="Remedy">What would change it.</param>
internal sealed record PlanInsightFinding(PlanInsightInfo Insight, int NodeId, string Node, string Message, string Remedy);

/// <summary>
/// X1b: plan insights - pure evaluators over a <see cref="QueryPlan" />, each with a stable code, a docs anchor and a remedy, in
/// the health checks' style. A registry of their own rather than health checks: see the remarks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the health registry.</b> A health check is the database's: a sweep gathers its facts once per interval under no
/// visitor's identity, keeps a history, is muted per browser, is gated per finding, and is what the diagnostics API returns.
/// An insight is none of those. It reads one plan one visitor asked for, through whatever gate that plan came through (the SQL
/// console's <c>RunSql</c>, Mode A's, the Statements page's activity gate), and it is gone when the plan is. Registered as
/// checks, they would be listed on the Health page as never run, counted by the health export and returned by nobody. So the
/// codes share the health codes' contract - <c>docs/health/codes.md</c>, a letter of their own (<c>P</c>, area
/// <c>Plans</c>), never reused (hard rule 7) - and nothing else.
/// </para>
/// <para>
/// <b>Plain plans and analyzed ones.</b> Five insights read what only <c>EXPLAIN ANALYZE</c> prints - a row count, a spill, a
/// lossy page, the rows a filter discarded, the workers given - and are listed as not judged on a plain plan
/// (<see cref="NotJudged" />). The other three read the planner's estimates when there is nothing better.
/// </para>
/// <para>
/// <b>Bounded.</b> At most <see cref="MaxPerInsight" /> findings an insight, the worst first, so a plan of a thousand nodes
/// reads as a list a person finishes.
/// </para>
/// </remarks>
internal static partial class PlanInsights
{
    /// <summary>The docs file every insight's heading is in.</summary>
    public const string DocsFileName = "plans.md";

    /// <summary>What <c>docs/health/codes.md</c> calls their area.</summary>
    public const string Area = "Plans";

    /// <summary>Their codes' letter.</summary>
    public const char Letter = 'P';

    /// <summary>The most findings one insight makes on one plan.</summary>
    public const int MaxPerInsight = 5;

    /// <summary>P01's ratio between the rows expected and the rows returned.</summary>
    public const double MisestimateRatio = 10;

    /// <summary>P01's least rows, the larger side over every loop: a 1 against 12 is no plan's problem.</summary>
    public const double MisestimateMinRows = 1_000;

    /// <summary>P04's "large": the rows a sequential scan read, as T01 counts a large table.</summary>
    public const double LargeScanRows = SequentialScansCheck.LargeTableRows;

    /// <summary>P04's "selective": the scan kept this share of what it read, or less.</summary>
    public const double SelectiveShare = 0.10;

    /// <summary>P05's "large outer side": the inner side runs once for each of this many rows, or more.</summary>
    public const double LargeOuterRows = 1_000;

    /// <summary>P06's "large OFFSET": the rows read and discarded before the first one returned.</summary>
    public const double LargeOffsetRows = 1_000;

    /// <summary>The share of its input the planner guesses an <c>OFFSET</c> it cannot see (a parameter) skips: <c>adjust_limit_rows_costs</c>.</summary>
    internal const double GuessedOffsetShare = 0.10;

    /// <summary>P01.</summary>
    public static readonly PlanInsightInfo RowEstimate = new("P01", "Row estimate off tenfold or more", HealthSeverity.Warning, true);

    /// <summary>P02.</summary>
    public static readonly PlanInsightInfo Spill = new("P02", "Sort or hash spilled to disk", HealthSeverity.Warning, true);

    /// <summary>P03.</summary>
    public static readonly PlanInsightInfo LossyBitmap = new("P03", "Lossy bitmap heap scan", HealthSeverity.Info, true);

    /// <summary>P04.</summary>
    public static readonly PlanInsightInfo SelectiveScan = new("P04", "Sequential scan with a selective filter", HealthSeverity.Warning, true);

    /// <summary>P05.</summary>
    public static readonly PlanInsightInfo LargeNestedLoop = new("P05", "Nested loop over a large outer side", HealthSeverity.Info, false);

    /// <summary>P06.</summary>
    public static readonly PlanInsightInfo LargeOffset = new("P06", "Large OFFSET", HealthSeverity.Info, false);

    /// <summary>P07.</summary>
    public static readonly PlanInsightInfo LeadingWildcard = new("P07", "Leading-wildcard LIKE", HealthSeverity.Info, false);

    /// <summary>P08.</summary>
    public static readonly PlanInsightInfo ShortWorkers = new("P08", "Parallel workers short of the plan", HealthSeverity.Info, true);

    /// <summary>Every insight, by code.</summary>
    public static IReadOnlyList<PlanInsightInfo> All { get; } =
        [RowEstimate, Spill, LossyBitmap, SelectiveScan, LargeNestedLoop, LargeOffset, LeadingWildcard, ShortWorkers];

    /// <summary>Every insight's findings on <paramref name="plan" />, in code order, each insight's worst first.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="text">The studio's words, for the remedies that differ between studios.</param>
    public static IReadOnlyList<PlanInsightFinding> Evaluate(QueryPlan plan, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(text);

        return
        [
            .. RowEstimates(plan),
            .. Spills(plan),
            .. LossyBitmaps(plan),
            .. SelectiveScans(plan, text),
            .. LargeNestedLoops(plan),
            .. LargeOffsets(plan),
            .. LeadingWildcards(plan, text),
            .. ShortParallelWorkers(plan),
        ];
    }

    /// <summary>The insights a plain plan cannot be judged by: those that read what only <c>EXPLAIN ANALYZE</c> prints.</summary>
    public static IReadOnlyList<PlanInsightInfo> NotJudged(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Analyzed ? [] : [.. All.Where(static x => x.NeedsAnalyze)];
    }

    /// <summary>
    /// P01: a node that returned ten times more or fewer rows than the planner expected - per loop, over at least
    /// <see cref="MisestimateMinRows" /> rows on the larger side - and so was planned for a different statement than ran.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> RowEstimates(QueryPlan plan)
    {
        if (!plan.Analyzed)
        {
            return [];
        }

        return plan.Nodes
            .Where(static x => x is { PlanRows: not null, ActualRows: not null, ActualLoops: > 0 })
            .Select(static x =>
            {
                double expected = x.PlanRows!.Value;
                double actual = x.ActualRows!.Value;
                double ratio = Math.Max(expected, actual) / Math.Max(Math.Min(expected, actual), 1);
                return (Node: x, Expected: expected, Actual: actual, Ratio: ratio);
            })
            .Where(static x => x.Ratio >= MisestimateRatio && Math.Max(x.Expected, x.Actual) * x.Node.ActualLoops!.Value >= MisestimateMinRows)
            .OrderByDescending(static x => x.Ratio)
            .ThenBy(static x => x.Node.Id)
            .Take(MaxPerInsight)
            .Select(static x => Finding(
                RowEstimate,
                x.Node,
                x.Node.Description + " was planned for " + Rows(x.Expected) + " rows a loop and returned " + Rows(x.Actual)
                + Loops(x.Node) + ": " + Times(x.Ratio) + (x.Actual > x.Expected ? " more" : " fewer")
                + " than expected, so the join and scan above it were chosen for a different statement than ran.",
                "Plan it again after ANALYZE on the table it reads, so its statistics are current. Where one column's values are "
                + "skewed, a higher statistics target for it (ALTER TABLE ... ALTER COLUMN ... SET STATISTICS) lets the planner "
                + "see the skew; where columns move together, extended statistics (CREATE STATISTICS) let it see that."));
    }

    /// <summary>
    /// P02: a sort that went to disk (an external sort, or a worker's), a hash that split into batches, or a hash aggregate
    /// that wrote to disk - each one ran out of <c>work_mem</c>.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> Spills(QueryPlan plan)
    {
        if (!plan.Analyzed)
        {
            return [];
        }

        const string Remedy =
            "It ran out of work_mem, the memory each sort and hash may use before it writes to temporary files. A larger work_mem "
            + "for the role or the session that runs it (SET work_mem) keeps it in memory; so do fewer rows reaching it - a more "
            + "selective filter, or an index that returns them already in order - or a narrower select list.";

        List<(PlanNode Node, double Kb, string Message)> spills = [];

        foreach (PlanNode node in plan.Nodes)
        {
            if (node.SortSpaceType == "Disk" || node.SortMethod?.StartsWith("external", StringComparison.Ordinal) == true)
            {
                double kb = node.SortSpaceUsedKb ?? 0;
                spills.Add((node, kb, node.Description + " sorted on disk (" + node.SortMethod + ", " + Kilobytes(kb) + ")."));
            }
            else if (node.WorkerSortDiskKb is { } workerKb)
            {
                spills.Add((node, workerKb, node.Description + "'s parallel workers sorted on disk (up to " + Kilobytes(workerKb) + " each)."));
            }
            else if (node.NodeType == "Hash" && node.HashBatches is > 1 and { } batches)
            {
                string planned = node.OriginalHashBatches is { } original && original < batches
                    ? ", where " + Count(original) + " " + Plural(original, "was", "were") + " planned"
                    : string.Empty;
                spills.Add((node, node.PeakMemoryKb ?? 0, node.Description + " split into " + Count(batches) + " batches" + planned
                    + ", writing every batch but the first to temporary files."));
            }
            else if (node.DiskUsageKb is > 0 and { } disk)
            {
                string inBatches = node.HashAggBatches is { } aggregated ? " in " + Count(aggregated) + " batches" : string.Empty;
                spills.Add((node, disk, node.Description + " wrote " + Kilobytes(disk) + " to disk" + inBatches + "."));
            }
        }

        return spills
            .OrderByDescending(static x => x.Kb)
            .ThenBy(static x => x.Node.Id)
            .Take(MaxPerInsight)
            .Select(x => Finding(Spill, x.Node, x.Message, Remedy));
    }

    /// <summary>P03: a bitmap heap scan whose bitmap outgrew <c>work_mem</c> and kept heap pages only as whole pages.</summary>
    internal static IEnumerable<PlanInsightFinding> LossyBitmaps(QueryPlan plan)
    {
        if (!plan.Analyzed)
        {
            return [];
        }

        return plan.Nodes
            .Where(static x => x.LossyHeapBlocks is > 0)
            .OrderByDescending(static x => x.LossyHeapBlocks)
            .ThenBy(static x => x.Id)
            .Take(MaxPerInsight)
            .Select(static x => Finding(
                LossyBitmap,
                x,
                x.Description + " kept " + Count(x.LossyHeapBlocks!.Value) + " heap pages only as whole pages"
                + (x.ExactHeapBlocks is { } exact ? " (and " + Count(exact) + " exactly)" : string.Empty)
                + ", so every row on them was checked again"
                + (x.RowsRemovedByIndexRecheck is > 0 and { } removed ? " and " + Rows(removed) + " rows a loop thrown away" : string.Empty) + ".",
                "Its bitmap outgrew work_mem. A larger work_mem for the role or the session that runs it keeps the bitmap exact; a "
                + "more selective index condition makes it smaller."));
    }

    /// <summary>
    /// P04: a sequential scan that read <see cref="LargeScanRows" /> rows or more and kept <see cref="SelectiveShare" /> of them
    /// or less - the shape an index serves. Only under <c>ANALYZE</c>: a plain plan prints the rows a scan keeps, not those it
    /// reads.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> SelectiveScans(QueryPlan plan, StudioText text)
    {
        if (!plan.Analyzed)
        {
            return [];
        }

        return plan.Nodes
            .Where(static x => x.NodeType == "Seq Scan" && x.Filter is not null && x is { ActualRows: not null, ActualLoops: > 0 })
            .Select(static x =>
            {
                double loops = x.ActualLoops!.Value;
                double kept = x.ActualRows!.Value * loops;
                double read = kept + ((x.RowsRemovedByFilter ?? 0) * loops);
                return (Node: x, Kept: kept, Read: read);
            })
            .Where(static x => x.Read >= LargeScanRows && x.Kept <= x.Read * SelectiveShare)
            .OrderByDescending(static x => x.Read - x.Kept)
            .ThenBy(static x => x.Node.Id)
            .Take(MaxPerInsight)
            .Select(x => Finding(
                SelectiveScan,
                x.Node,
                x.Node.Description + " read " + Rows(x.Read) + " rows to keep " + Rows(x.Kept) + " (" + TableText.Percent(x.Kept / x.Read)
                + "): its filter, " + x.Node.Filter + ", discards almost everything it reads.",
                text.PlanIndexRemedy));
    }

    /// <summary>
    /// P05: a nested loop whose outer side has <see cref="LargeOuterRows" /> rows or more, so its inner side runs that many
    /// times - the rows it returned under <c>ANALYZE</c>, the planner's estimate otherwise.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> LargeNestedLoops(QueryPlan plan)
    {
        return plan.Nodes
            .Where(static x => x.NodeType == "Nested Loop" && x.Children.Count >= 2 && !x.NeverExecuted)
            .Select(x =>
            {
                PlanNode outer = x.Children.FirstOrDefault(static c => c.ParentRelationship == "Outer") ?? x.Children[0];
                double? rows = plan.Analyzed
                    ? (outer.ActualRows is { } actual && outer.ActualLoops is { } loops ? actual * loops : null)
                    : outer.PlanRows;
                return (Node: x, Outer: outer, Rows: rows);
            })
            .Where(static x => x.Rows >= LargeOuterRows)
            .OrderByDescending(static x => x.Rows)
            .ThenBy(static x => x.Node.Id)
            .Take(MaxPerInsight)
            .Select(x => Finding(
                LargeNestedLoop,
                x.Node,
                x.Node.Description + " runs its inner side once for each of " + (plan.Analyzed ? string.Empty : "an estimated ")
                + Rows(x.Rows!.Value) + " rows from " + x.Outer.Description + ".",
                "A hash or a merge join reads each side once. A nested loop is the right join for a few outer rows; for many it "
                + "usually means the outer side was expected to be small (see P01 under EXPLAIN ANALYZE), or the inner side has no "
                + "index on the join key, so each run of it costs more than it should."));
    }

    /// <summary>
    /// P06: a <c>Limit</c> that reads and throws away <see cref="LargeOffsetRows" /> rows or more before its first - counted
    /// under <c>ANALYZE</c>, and read back from the planner's own <c>OFFSET</c> arithmetic otherwise, except where the planner
    /// was guessing because the <c>OFFSET</c> is a parameter (it assumes a tenth of its input).
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> LargeOffsets(QueryPlan plan)
    {
        List<(PlanNode Node, double Skipped)> offsets = [];

        foreach (PlanNode node in plan.Nodes.Where(static x => x.NodeType == "Limit" && x.Children.Count > 0 && !x.NeverExecuted))
        {
            PlanNode input = node.Children[0];

            if (plan.Analyzed)
            {
                if (input is { ActualRows: { } inputRows, ActualLoops: { } loops } && node.ActualRows is { } returned)
                {
                    offsets.Add((node, Math.Max(0, inputRows - returned) * loops));
                }
            }
            else if (EstimatedOffset(node, input) is { } skipped)
            {
                offsets.Add((node, skipped));
            }
        }

        return offsets
            .Where(static x => x.Skipped >= LargeOffsetRows)
            .OrderByDescending(static x => x.Skipped)
            .ThenBy(static x => x.Node.Id)
            .Take(MaxPerInsight)
            .Select(x => Finding(
                LargeOffset,
                x.Node,
                x.Node.Description + (plan.Analyzed ? " read and discarded " + Rows(x.Skipped) : " is expected to read and discard about " + About(x.Skipped))
                + " rows from " + Input(x.Node) + " before the first one it returned.",
                "Postgres reads every row an OFFSET skips, so each page costs more than the one before. Paging by the last key "
                + "seen - WHERE (sort key) > (the last page's last) ORDER BY the same key LIMIT n, keyset pagination - reads only "
                + "the page, given an index on that key."));

        static string Input(PlanNode limit) => limit.Children[0].Description;
    }

    /// <summary>
    /// The rows a plain plan's <c>Limit</c> skips, read back from <c>adjust_limit_rows_costs</c>: the planner adds
    /// <c>(input total cost - input startup cost) * offset / input rows</c> to the limit's startup cost. <see langword="null" />
    /// when the costs cannot say, and when the share is the planner's own guess for an <c>OFFSET</c> it cannot see.
    /// </summary>
    internal static double? EstimatedOffset(PlanNode limit, PlanNode input)
    {
        if (limit.StartupCost is not { } limitStartup || input is not { StartupCost: { } inputStartup, TotalCost: { } inputTotal, PlanRows: { } inputRows })
        {
            return null;
        }

        double run = inputTotal - inputStartup;

        if (run <= 0 || inputRows <= 0)
        {
            return null;
        }

        double skipped = Math.Min(inputRows, Math.Max(0, (limitStartup - inputStartup) / run * inputRows));

        // A guess, not an OFFSET: clamp_row_est(rows * 0.10) - the costs are printed to two decimals, so the share is read
        // back to within a tenth of a percent of it.
        return Math.Abs((skipped / inputRows) - GuessedOffsetShare) < 0.001 ? null : skipped;
    }

    /// <summary>
    /// P07: a filter that tests a <c>LIKE</c> or <c>ILIKE</c> pattern starting with <c>%</c> or <c>_</c> - which no btree
    /// index can serve, so every row is tested. Only a filter: a pattern an index condition or a recheck carries is already
    /// served by an index (a trigram one). A pattern that is a parameter (<c>~~ $1</c>) says nothing and is not judged.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> LeadingWildcards(QueryPlan plan, StudioText text)
    {
        return plan.Nodes
            .Select(static x => (Node: x, Match: LeadingWildcardPattern().Match(x.Filter ?? string.Empty) is { Success: true } filter
                ? filter
                : LeadingWildcardPattern().Match(x.JoinFilter ?? string.Empty)))
            .Where(static x => x.Match.Success)
            .Take(MaxPerInsight)
            .Select(x => Finding(
                LeadingWildcard,
                x.Node,
                x.Node.Description + " tests " + (x.Match.Groups["op"].Value.EndsWith('*') ? "ILIKE " : "LIKE ")
                + x.Match.Groups["pattern"].Value + ", a pattern that starts with a wildcard, against every row it reads.",
                text.PlanPatternRemedy));
    }

    /// <summary>P08: a <c>Gather</c> that was given fewer parallel workers than it was planned with.</summary>
    internal static IEnumerable<PlanInsightFinding> ShortParallelWorkers(QueryPlan plan)
    {
        if (!plan.Analyzed)
        {
            return [];
        }

        return plan.Nodes
            .Where(static x => x is { WorkersPlanned: { } planned, WorkersLaunched: { } launched } && launched < planned && !x.NeverExecuted)
            .Take(MaxPerInsight)
            .Select(static x => Finding(
                ShortWorkers,
                x,
                x.Description + " was planned with " + Count(x.WorkersPlanned!.Value) + " parallel workers and given "
                + Count(x.WorkersLaunched!.Value) + ", so the rest of its work fell to the processes it got.",
                "Parallel workers come from max_parallel_workers and max_worker_processes, which other parallel queries were "
                + "holding when it ran. Raising them in the server's settings, or running fewer parallel queries at once, gives a "
                + "plan the workers it was costed for."));
    }

    private static PlanInsightFinding Finding(PlanInsightInfo insight, PlanNode node, string message, string remedy) =>
        new(insight, node.Id, node.Description, message, remedy);

    private static string Rows(double rows) => Count(Math.Round(rows));

    /// <summary>
    /// A count read back from the planner's costs, to three significant figures: the costs are printed to two decimals, so
    /// the probe's <c>OFFSET 150000</c> comes back as 149,999.4.
    /// </summary>
    private static string About(double rows)
    {
        if (rows < 1_000)
        {
            return Rows(rows);
        }

        double scale = Math.Pow(10, Math.Floor(Math.Log10(rows)) - 2);
        return Count(Math.Round(rows / scale) * scale);
    }

    private static string Count(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Loops(PlanNode node) =>
        node.ActualLoops is > 1 and { } loops ? " over " + Count(loops) + " loops" : string.Empty;

    private static string Times(double ratio) =>
        (ratio >= 100 ? Math.Round(ratio) : Math.Round(ratio, 1)).ToString("0.#", CultureInfo.InvariantCulture) + " times";

    private static string Kilobytes(double kb) => TableText.Bytes((long) Math.Round(kb * 1024));

    private static string Plural(double count, string one, string many) => count == 1 ? one : many;

    /// <summary>
    /// A <c>LIKE</c> (<c>~~</c>) or <c>ILIKE</c> (<c>~~*</c>) test against a quoted pattern whose first character is a wildcard,
    /// as Postgres deparses it: <c>(c ~~ '%abc%'::text)</c>. Not <c>!~~</c>: a <c>NOT LIKE</c> is never served by an index anyway.
    /// </summary>
    [GeneratedRegex(@"(?<![!~])(?<op>~~\*?)\s*(?<pattern>'[%_](?:[^']|'')*')", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingWildcardPattern();
}
