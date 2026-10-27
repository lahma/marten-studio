using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>The node table's rows and the Markdown export, over <c>analyzed-join.json</c> (see <see cref="PlanAnalysisTests" /> for its numbers).</summary>
public class PlanTableTests
{
    private static QueryPlan Join() => PlanFixtures.Plan("analyzed-join");

    private static int[] Order(PlanTableColumn column, bool descending) =>
        [.. PlanTable.Rows(Join(), column, descending).Select(static x => x.Node.Id)];

    [Fact]
    public void The_default_order_is_the_plans_own()
    {
        Order(PlanTableColumn.Plan, false).Should().Equal(0, 1, 2, 3, 4);
        Order(PlanTableColumn.Plan, true).Should().Equal(4, 3, 2, 1, 0);
    }

    [Fact]
    public void Self_time_sorts_the_slowest_first_and_breaks_ties_by_the_plans_order()
    {
        // 30 (orders), 10 (customers), then the sort and the hash at 8 each - the sort is earlier in the plan - then the join at 2.
        Order(PlanTableColumn.Self, true).Should().Equal(2, 4, 0, 3, 1);
        Order(PlanTableColumn.Self, false).Should().Equal(1, 0, 3, 4, 2);
    }

    [Fact]
    public void The_misestimate_sorts_by_ratio_and_the_blocks_by_what_a_node_itself_read()
    {
        // ×10 for the sort and the join, then the exact estimates in the plan's order.
        Order(PlanTableColumn.Misestimate, true).Should().Equal(0, 1, 2, 3, 4);
        Order(PlanTableColumn.SharedRead, true).Should().Equal(2, 4, 0, 1, 3);
        Order(PlanTableColumn.SharedHit, true).Should().Equal(2, 4, 0, 1, 3);
        Order(PlanTableColumn.Rows, true).Should().Equal(2, 3, 4, 0, 1);
        Order(PlanTableColumn.Estimated, true).Should().Equal(2, 3, 4, 0, 1);
        Order(PlanTableColumn.Io, true).Should().Equal(2, 4, 0, 1, 3);
    }

    [Fact]
    public void A_row_with_no_value_in_the_sorted_column_comes_last_whichever_way_it_sorts()
    {
        QueryPlan plan = QueryPlanReader.Read(ExplainFixtures.Get("17-nestloop-plain")).Plan!;

        foreach (bool descending in new[] { false, true })
        {
            PlanTable.Rows(plan, PlanTableColumn.Rows, descending).Select(static x => x.Node.Id).Should().Equal(
                PlanTable.Rows(plan, PlanTableColumn.Plan).Select(static x => x.Node.Id), "a plain plan ran nothing, so every row is without a value");
        }
    }

    [Fact]
    public void The_cells_are_blank_where_Postgres_printed_nothing_and_a_zero_where_it_printed_zero()
    {
        QueryPlan plain = QueryPlanReader.Read(ExplainFixtures.Get("17-nestloop-plain")).Plan!;
        PlanTableRow row = PlanTable.Rows(plain)[0];

        PlanTable.RowsText(row.Node).Should().BeEmpty();
        PlanTable.RatioText(row.Node).Should().BeEmpty();
        PlanTable.BlocksText(row, PlanTableColumn.SharedHit).Should().BeEmpty();
        PlanTable.IoText(row).Should().BeEmpty();
        PlanTable.SelfText(plain, row).Should().NotBeEmpty("a plain plan's own cost is there");
        PlanTable.SelfHeading(plain).Should().Be("Self cost");

        QueryPlan analyzed = Join();
        PlanTableRow hashJoin = PlanTable.Rows(analyzed)[1];
        PlanTable.BlocksText(hashJoin, PlanTableColumn.SharedRead).Should().Be("0");
        PlanTable.SelfHeading(analyzed).Should().Be("Self time");
    }

    [Fact]
    public void The_Markdown_is_the_summary_then_the_node_table_as_GitHub_tables()
    {
        string markdown = PlanTable.Markdown(Join());

        markdown.Should().StartWith("**EXPLAIN ANALYZE** - 5 nodes\n\n| Summary | |\n|---|---|\n| Estimated cost | 2,110.00 |\n| Planning time | 0.9 ms |\n| Execution time | 59.5 ms |\n");
        markdown.Should().Contain("| Triggers | 1.3 ms |").And.Contain("| Cache hit ratio | 87% |");
        markdown.Should().NotContain("| JIT |", "a blank item is left out of a pull request");

        markdown.Should().Contain("\n| # | Node | Self time | Rows | Estimated | Misestimate | Loops | Shared hit | Shared read | Temp read | Temp written | I/O time |\n"
            + "|--:|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|\n");

        markdown.Should().Contain("| 0 | Sort | 8 ms | 1,000 | 100 | ×10 | 1 | 100 | 0 | 0 | 0 | 0 ms |\n");
        markdown.Should().Contain("| 2 | &emsp;&emsp;Seq Scan on orders o | 30 ms | 100,000 | 100,000 | ×1 | 1 | 1,900 | 350 | 0 | 0 | 4 ms |\n");
        markdown.Split('\n').Count(static x => x.StartsWith("| ", StringComparison.Ordinal) && x.Length > 2 && char.IsAsciiDigit(x[2])).Should().Be(5, "a row for each node");
    }

    [Fact]
    public void A_plain_plan_exports_blanks_and_a_cost()
    {
        string markdown = PlanTable.Markdown(QueryPlanReader.Read(ExplainFixtures.Get("17-nestloop-plain")).Plan!);

        markdown.Should().StartWith("**EXPLAIN** - 3 nodes").And.Contain("| Self cost |").And.NotContain("Execution time").And.NotContain("| Rows returned |");
    }

    [Fact]
    public void A_pipe_or_a_line_break_in_a_node_never_makes_two_cells()
    {
        QueryPlan plan = QueryPlanReader.Read("""[{"Plan": {"Node Type": "Seq Scan", "Relation Name": "a|b\nc", "Alias": "x"}}]""").Plan!;

        string row = PlanTable.Markdown(plan).Split('\n').Single(static x => x.StartsWith("| 0 |", StringComparison.Ordinal));

        row.Should().Contain("a\\|b c x");
        row.Split(" | ").Length.Should().Be(12, "twelve cells, whatever the name holds");
    }

    [Fact]
    public void A_plan_of_more_nodes_than_the_export_lists_says_how_many_it_left_out()
    {
        string children = string.Join(",", Enumerable.Range(0, PlanTable.MaxMarkdownNodes + 25).Select(static _ => "{\"Node Type\": \"Seq Scan\", \"Total Cost\": 1}"));
        QueryPlan plan = QueryPlanReader.Read("[{\"Plan\": {\"Node Type\": \"Append\", \"Total Cost\": 1, \"Plans\": [" + children + "]}}]").Plan!;

        string markdown = PlanTable.Markdown(plan);

        markdown.Should().Contain("_26 more nodes are not listed._");
        markdown.Split('\n').Count(static x => x.StartsWith("| ", StringComparison.Ordinal) && x.Length > 2 && char.IsAsciiDigit(x[2])).Should().Be(PlanTable.MaxMarkdownNodes);
    }
}
