using System.Globalization;
using System.Text;

using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Query;

/// <summary>The node table's columns (X3, issue #28), which it can be sorted by.</summary>
internal enum PlanTableColumn
{
    /// <summary>The plan's own order: the root first, each node before its children.</summary>
    Plan,

    /// <summary>The node's own time, or its own cost when the plan was not timed.</summary>
    Self,

    /// <summary>The rows it returned, per loop.</summary>
    Rows,

    /// <summary>The rows the planner expected, per loop.</summary>
    Estimated,

    /// <summary>How far off the estimate was.</summary>
    Misestimate,

    /// <summary>How many times it ran.</summary>
    Loops,

    /// <summary>The shared blocks it found in the cache, itself.</summary>
    SharedHit,

    /// <summary>The shared blocks it read from outside the cache, itself.</summary>
    SharedRead,

    /// <summary>The temporary-file blocks it read, itself.</summary>
    TempRead,

    /// <summary>The temporary-file blocks it wrote, itself.</summary>
    TempWritten,

    /// <summary>The time it spent on block I/O, itself.</summary>
    Io,
}

/// <summary>One row of the node table: a node and the numbers its columns show.</summary>
/// <param name="Node">The node.</param>
/// <param name="Self">Its own time in milliseconds, or its own cost when the plan was not timed.</param>
/// <param name="Own">What it itself read and wrote: its buffers less its children's.</param>
internal sealed record PlanTableRow(PlanNode Node, double? Self, PlanNodeOwn? Own)
{
    /// <summary>The value the table sorts this row's <paramref name="column" /> by, or <see langword="null" /> where the plan printed none.</summary>
    public double? Value(PlanTableColumn column) => column switch
    {
        PlanTableColumn.Plan => Node.Id,
        PlanTableColumn.Self => Self,
        PlanTableColumn.Rows => Node.ActualLoops is > 0 ? Node.ActualRows : null,
        PlanTableColumn.Estimated => Node.PlanRows,
        PlanTableColumn.Misestimate => Node.MisestimateRatio,
        PlanTableColumn.Loops => Node.ActualLoops,
        PlanTableColumn.SharedHit => Own?.SharedHit,
        PlanTableColumn.SharedRead => Own?.SharedRead,
        PlanTableColumn.TempRead => Own?.TempRead,
        PlanTableColumn.TempWritten => Own?.TempWritten,
        PlanTableColumn.Io => Own?.IoMs,
        _ => null,
    };
}

/// <summary>
/// The node table's rows, sorted, and the Markdown export that prints them (X3): pure functions of a plan, tested apart from the
/// markup that draws them.
/// </summary>
internal static class PlanTable
{
    /// <summary>The most nodes the Markdown export lists: a pull request is no place for two thousand rows.</summary>
    public const int MaxMarkdownNodes = 200;

    /// <summary>Every node as a row, in <paramref name="sort" /> order; a row with no value in the column comes last, whichever way it sorts.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="sort">The column to sort by.</param>
    /// <param name="descending">Whether the largest comes first.</param>
    public static IReadOnlyList<PlanTableRow> Rows(QueryPlan plan, PlanTableColumn sort = PlanTableColumn.Plan, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(plan);

        bool timed = plan.Timed;

        List<PlanTableRow> rows =
        [
            .. plan.Nodes.Select(x => new PlanTableRow(x, timed ? x.ExclusiveMs : x.ExclusiveCost, PlanNodeOwn.Of(x))),
        ];

        IEnumerable<PlanTableRow> withValue = rows.Where(x => x.Value(sort) is not null);
        IEnumerable<PlanTableRow> without = rows.Where(x => x.Value(sort) is null);

        IOrderedEnumerable<PlanTableRow> ordered = descending
            ? withValue.OrderByDescending(x => x.Value(sort)).ThenBy(static x => x.Node.Id)
            : withValue.OrderBy(x => x.Value(sort)).ThenBy(static x => x.Node.Id);

        return [.. ordered, .. without.OrderBy(static x => x.Node.Id)];
    }

    /// <summary>The heading of the self column: time when the plan was timed, otherwise cost.</summary>
    public static string SelfHeading(QueryPlan plan) => plan.Timed ? "Self time" : "Self cost";

    /// <summary>The self column's cell for a row, blank where the plan printed none.</summary>
    public static string SelfText(QueryPlan plan, PlanTableRow row) =>
        row.Self is { } self ? (plan.Timed ? PlanAnalysis.Milliseconds(self) : PlanAnalysis.Cost(self)) : string.Empty;

    /// <summary>A count cell: blank where the plan printed none.</summary>
    public static string CountText(double? value) => value is { } number ? TableText.Count(number) : string.Empty;

    /// <summary>The misestimate cell: <c>×73</c>, or blank.</summary>
    public static string RatioText(PlanNode node) => node.MisestimateRatio is { } ratio ? PlanAnalysis.Ratio(ratio) : string.Empty;

    /// <summary>The rows cell: the rows returned per loop, blank for a node that did not run.</summary>
    public static string RowsText(PlanNode node) => node.ActualLoops is > 0 ? CountText(node.ActualRows) : string.Empty;

    /// <summary>The I/O cell: the milliseconds a node itself spent on block I/O, blank where not measured.</summary>
    public static string IoText(PlanTableRow row) => row.Own?.IoMs is { } io ? PlanAnalysis.Milliseconds(io) : string.Empty;

    /// <summary>An own-blocks cell, blank where the node printed no buffers and <c>0</c> where it printed them and used none.</summary>
    public static string BlocksText(PlanTableRow row, PlanTableColumn column) =>
        row.Value(column) is { } blocks ? TableText.Count(blocks) : string.Empty;

    /// <summary>
    /// The plan as Markdown for a pull request or an issue: its summary - every item Postgres printed - and then the node table, in
    /// plan order, as GitHub tables. A node's own numbers are its own, less its children's, as the table on screen shows them.
    /// </summary>
    /// <param name="plan">The plan.</param>
    public static string Markdown(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        PlanSummary summary = PlanAnalysis.Summarize(plan);
        StringBuilder markdown = new();

        markdown.Append("**").Append(plan.Analyzed ? "EXPLAIN ANALYZE" : "EXPLAIN").Append("** - ")
            .Append(plan.NodeCount.ToString("N0", CultureInfo.InvariantCulture)).Append(plan.NodeCount == 1 ? " node" : " nodes");

        if (plan.Truncated)
        {
            markdown.Append(" read of a larger plan");
        }

        markdown.Append("\n\n| Summary | |\n|---|---|\n");

        foreach ((string label, string value) in summary.Items.Where(static x => x.Value.Length > 0))
        {
            markdown.Append("| ").Append(Cell(label)).Append(" | ").Append(Cell(value)).Append(" |\n");
        }

        string self = SelfHeading(plan);

        markdown.Append("\n| # | Node | ").Append(self).Append(" | Rows | Estimated | Misestimate | Loops | Shared hit | Shared read | Temp read | Temp written | I/O time |\n")
            .Append("|--:|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|\n");

        IReadOnlyList<PlanTableRow> rows = Rows(plan);

        foreach (PlanTableRow row in rows.Take(MaxMarkdownNodes))
        {
            PlanNode node = row.Node;

            markdown.Append("| ").Append(node.Id.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(string.Concat(Enumerable.Repeat("&emsp;", Math.Min(node.Depth, 10)))).Append(Cell(node.Description))
                .Append(" | ").Append(SelfText(plan, row))
                .Append(" | ").Append(RowsText(node))
                .Append(" | ").Append(CountText(node.PlanRows))
                .Append(" | ").Append(RatioText(node))
                .Append(" | ").Append(node.ActualLoops is { } loops ? TableText.Count(loops) : string.Empty)
                .Append(" | ").Append(BlocksText(row, PlanTableColumn.SharedHit))
                .Append(" | ").Append(BlocksText(row, PlanTableColumn.SharedRead))
                .Append(" | ").Append(BlocksText(row, PlanTableColumn.TempRead))
                .Append(" | ").Append(BlocksText(row, PlanTableColumn.TempWritten))
                .Append(" | ").Append(IoText(row))
                .Append(" |\n");
        }

        if (rows.Count > MaxMarkdownNodes)
        {
            markdown.Append("\n_").Append((rows.Count - MaxMarkdownNodes).ToString("N0", CultureInfo.InvariantCulture)).Append(" more nodes are not listed._\n");
        }

        return markdown.ToString();
    }

    /// <summary>A cell's text: a pipe is escaped and a line break is a space, so one cell is never two.</summary>
    private static string Cell(string text) =>
        text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|", StringComparison.Ordinal);
}
