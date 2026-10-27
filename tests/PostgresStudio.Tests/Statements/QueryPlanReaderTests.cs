using System.Text;

using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The plan reader (X1) over every fixture the PROBE packet measured, and over the JSON no fixture holds: an unknown key is
/// ignored, a missing one is a blank, a wrong type reads as absent, and nothing that is not a plan throws.
/// </summary>
public class QueryPlanReaderTests
{
    private static QueryPlan Plan(string json)
    {
        QueryPlanRead read = QueryPlanReader.Read(json);
        read.Failure.Should().BeNull();
        return read.Plan!;
    }

    [Theory]
    [MemberData(nameof(ExplainFixtures.Names), MemberType = typeof(ExplainFixtures))]
    public void Every_fixture_reads_as_one_plan_with_its_shares_summing_to_the_whole(string name)
    {
        bool analyze = ExplainFixtures.IsAnalyze(name);
        QueryPlan plan = Plan(ExplainFixtures.Get(name));

        plan.Statements.Should().Be(1);
        plan.Truncated.Should().BeFalse();
        plan.NodeCount.Should().Be(plan.Nodes.Count()).And.BePositive();
        plan.Nodes.Select(static x => x.Id).Should().Equal(Enumerable.Range(0, plan.NodeCount), "ids are the walk's order, root first");
        plan.Nodes.Should().OnlyContain(static x => x.NodeType != "(unknown)" && x.TotalCost != null && x.PlanRows != null);

        plan.Analyzed.Should().Be(analyze);
        plan.Timed.Should().Be(analyze, "the probe ran ANALYZE with timing on");
        plan.HasBuffers.Should().Be(analyze, "the probe asked for BUFFERS with ANALYZE");
        (plan.ExecutionMs is not null).Should().Be(analyze);
        (plan.PlanningMs is not null).Should().Be(analyze);

        plan.Nodes.Sum(static x => x.CostShare ?? 0).Should().BeApproximately(1, 1e-9);
        plan.Nodes.Should().OnlyContain(static x => x.CostShare >= 0 && x.CostShare <= 1);

        if (analyze)
        {
            plan.Nodes.Sum(static x => x.TimeShare ?? 0).Should().BeApproximately(1, 1e-9);
            plan.Nodes.Should().OnlyContain(static x => x.ActualRows != null && x.ActualLoops != null && x.ExclusiveMs >= 0);
        }
        else
        {
            plan.Nodes.Should().OnlyContain(static x => x.ActualRows == null && x.TimeShare == null && x.Buffers == null);
        }
    }

    /// <summary>PostgreSQL 18's node keys - <c>Disabled</c> on every node, <c>Index Searches</c> on an index scan - read on 18 and 19 only.</summary>
    [Theory]
    [MemberData(nameof(ExplainFixtures.Names), MemberType = typeof(ExplainFixtures))]
    public void PostgreSQL_18s_node_keys_are_read_where_they_are_printed_and_nowhere_else(string name)
    {
        bool eighteen = ExplainFixtures.MajorOf(name) is "18" or "19rc1";
        QueryPlan plan = Plan(ExplainFixtures.Get(name));

        plan.Nodes.Should().OnlyContain(x => (x.Disabled != null) == eighteen);

        if (!eighteen)
        {
            plan.Nodes.Should().OnlyContain(static x => x.IndexSearches == null && x.Storage == null && x.MaximumStorageKb == null);
        }
    }

    [Theory]
    [InlineData("18-nestloop-analyze", 7693d)]
    [InlineData("18-index-analyze", 1d)]
    [InlineData("19rc1-offset-analyze", 1d)]
    public void PostgreSQL_18_counts_an_index_scans_descents(string name, double searches)
    {
        Plan(ExplainFixtures.Get(name)).Nodes.Should().Contain(x => x.IndexSearches == searches);
    }

    [Fact]
    public void PostgreSQL_18_says_where_a_cte_kept_its_rows()
    {
        PlanNode scan = Plan(ExplainFixtures.Get("18-cte-analyze")).Root;

        scan.NodeType.Should().Be("CTE Scan");
        scan.Storage.Should().Be("Memory");
        scan.MaximumStorageKb.Should().Be(56);
        scan.Target.Should().Be("on x");
    }

    [Fact]
    public void PostgreSQL_18s_fractional_actual_rows_are_read_as_numbers()
    {
        QueryPlan plan = Plan(ExplainFixtures.Get("18-offset-analyze"));

        plan.Root.ActualRows.Should().Be(10);
        plan.Root.Children[0].ActualRows.Should().Be(150_010);
    }

    [Theory]
    [InlineData("14-parallel-analyze", new[] { "Finalize Aggregate", "Gather", "Partial Aggregate", "Parallel Seq Scan" })]
    [InlineData("17-sort-spill-plain", new[] { "Gather Merge", "Sort", "Parallel Seq Scan" })]
    [InlineData("16-cte-plain", new[] { "CTE Scan", "Finalize HashAggregate", "Gather", "Partial HashAggregate", "Parallel Seq Scan" })]
    [InlineData("18-cte-plain", new[] { "CTE Scan", "HashAggregate", "Seq Scan" })]
    [InlineData("15-hashjoin-plain", new[] { "Merge Join", "Index Scan", "Sort", "Seq Scan" })]
    [InlineData("19rc1-nestloop-plain", new[] { "Nested Loop", "Seq Scan", "Index Scan" })]
    [InlineData("14-offset-plain", new[] { "Limit", "Index Scan" })]
    [InlineData("18-index-plain", new[] { "Bitmap Heap Scan", "Bitmap Index Scan" })]
    public void A_node_is_headed_as_EXPLAINs_text_format_heads_it(string name, string[] labels)
    {
        Plan(ExplainFixtures.Get(name)).Nodes.Select(static x => x.Label).Should().Equal(labels);
    }

    [Theory]
    [InlineData("14-offset-plain", 1, "Index Scan using t_pkey on t")]
    [InlineData("18-index-plain", 1, "Bitmap Index Scan on t_a")]
    [InlineData("18-index-plain", 0, "Bitmap Heap Scan on t")]
    [InlineData("16-cte-plain", 0, "CTE Scan on x")]
    [InlineData("14-hashjoin-plain", 0, "Merge Join")]
    public void A_nodes_description_says_what_it_reads(string name, int id, string description)
    {
        Plan(ExplainFixtures.Get(name)).Nodes.Single(x => x.Id == id).Description.Should().Be(description);
    }

    [Fact]
    public void A_cte_subplan_keeps_its_name_and_relationship()
    {
        PlanNode aggregate = Plan(ExplainFixtures.Get("14-cte-analyze")).Root.Children[0];

        aggregate.SubplanName.Should().Be("CTE x");
        aggregate.ParentRelationship.Should().Be("InitPlan");
        aggregate.GroupKey.Should().Equal("t.a");
    }

    [Fact]
    public void A_sort_that_spilled_reads_its_method_space_and_the_workers_disk()
    {
        PlanNode sort = Plan(ExplainFixtures.Get("18-sort-spill-analyze")).Nodes.Single(static x => x.NodeType == "Sort");

        sort.SortMethod.Should().Be("external merge");
        sort.SortSpaceType.Should().Be("Disk");
        sort.SortSpaceUsedKb.Should().Be(8360);
        sort.WorkerSortDiskKb.Should().Be(8208);
        sort.SortKey.Should().Equal("c");
        sort.Buffers!.TempWritten.Should().Be(8706);
    }

    [Fact]
    public void A_parallel_nodes_time_is_shared_by_the_processes_that_ran_it()
    {
        QueryPlan plan = Plan(ExplainFixtures.Get("18-parallel-analyze"));
        PlanNode scan = plan.Nodes.Single(static x => x.NodeType == "Seq Scan");

        scan.ActualLoops.Should().Be(3, "two workers and the leader");
        scan.InclusiveMs.Should().BeApproximately(scan.ActualTotalMs!.Value, 1e-9, "three loops' average over three processes is one process' wall time");
        plan.Root.InclusiveMs.Should().Be(plan.Root.ActualTotalMs);
    }

    [Fact]
    public void A_nested_loops_inner_side_is_its_loops_times_its_average()
    {
        QueryPlan plan = Plan(ExplainFixtures.Get("18-nestloop-analyze"));
        PlanNode inner = plan.Nodes.Single(static x => x.NodeType == "Index Scan");

        inner.InclusiveMs.Should().BeApproximately(0.002 * 7693, 1e-9);
        inner.Buffers!.SharedHit.Should().Be(30772);
    }

    [Fact]
    public void Costs_are_its_own_its_total_less_its_childrens()
    {
        QueryPlan plan = Plan(ExplainFixtures.Get("14-offset-plain"));

        plan.Root.ExclusiveCost.Should().Be(0, "a limit whose input costs more than it does is charged nothing of its own");
        plan.Root.Children[0].ExclusiveCost.Should().Be(7874.42);
        plan.Root.Children[0].CostShare.Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Defensive: what no fixture holds
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_text_is_no_plan(string? json)
    {
        QueryPlanRead read = QueryPlanReader.Read(json);

        read.Plan.Should().BeNull();
        read.Failure.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[{\"Plan\": 3}]")]
    [InlineData("[{\"Plan\": []}]")]
    [InlineData("42")]
    [InlineData("\"QUERY PLAN\"")]
    public void Text_that_is_not_a_plan_says_so_and_never_throws(string json)
    {
        QueryPlanRead read = QueryPlanReader.Read(json);

        read.Plan.Should().BeNull();
        read.Failure.Should().Be(QueryPlanReader.NotAPlan);
        QueryPlanReader.Relations(json).Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_key_is_ignored_and_a_missing_one_is_a_blank()
    {
        QueryPlan plan = Plan("""[{"Plan": {"Node Type": "Seq Scan", "Some Future Key": {"x": [1, 2]}, "Relation Name": "t"}, "Another": true}]""");

        plan.Root.NodeType.Should().Be("Seq Scan");
        plan.Root.RelationName.Should().Be("t");
        plan.Root.TotalCost.Should().BeNull();
        plan.Root.CostShare.Should().BeNull("no node has a cost to share");
        plan.Root.Buffers.Should().BeNull();
        plan.Root.Children.Should().BeEmpty();
        plan.Analyzed.Should().BeFalse();
    }

    [Fact]
    public void A_node_with_no_type_is_unknown_and_drawn_anyway()
    {
        Plan("""[{"Plan": {"Total Cost": 1}}]""").Root.Label.Should().Be("(unknown)");
    }

    [Fact]
    public void A_property_of_the_wrong_type_reads_as_absent_and_numeric_text_as_a_number()
    {
        PlanNode node = Plan("""
            [{"Plan": {"Node Type": 7, "Relation Name": ["t"], "Total Cost": "12.5", "Plan Rows": true, "Parallel Aware": "yes",
                       "Sort Key": "c", "Group Key": [1, "a"], "Plans": {"Node Type": "Sort"}, "Actual Rows": "NaN"}}]
            """).Root;

        node.NodeType.Should().Be("(unknown)");
        node.RelationName.Should().BeNull();
        node.TotalCost.Should().Be(12.5);
        node.PlanRows.Should().BeNull();
        node.ParallelAware.Should().BeFalse();
        node.SortKey.Should().BeEmpty();
        node.GroupKey.Should().Equal("a");
        node.Children.Should().BeEmpty();
        node.ActualRows.Should().BeNull("NaN is not a row count");
    }

    [Fact]
    public void One_statements_object_without_its_array_is_read_too()
    {
        Plan("""{"Plan": {"Node Type": "Result", "Total Cost": 0.01}}""").Root.NodeType.Should().Be("Result");
    }

    [Fact]
    public void A_plan_of_several_statements_reads_the_first_and_counts_them()
    {
        QueryPlan plan = Plan("""[{"Plan": {"Node Type": "Insert"}}, {"Plan": {"Node Type": "Update"}}]""");

        plan.Statements.Should().Be(2);
        plan.Root.NodeType.Should().Be("Insert");
    }

    [Fact]
    public void A_plan_deeper_than_the_bound_is_read_to_the_bound_and_says_so()
    {
        QueryPlan plan = Plan(Chain(QueryPlanReader.MaxDepth + 50));

        plan.Truncated.Should().BeTrue();
        plan.NodeCount.Should().Be(QueryPlanReader.MaxDepth + 1);
        plan.Nodes.Max(static x => x.Depth).Should().Be(QueryPlanReader.MaxDepth);
    }

    [Fact]
    public void A_plan_deeper_than_the_json_parsers_default_reads_whole()
    {
        // 40 plan levels are 80 JSON levels: past System.Text.Json's default of 64.
        QueryPlan plan = Plan(Chain(40));

        plan.Truncated.Should().BeFalse();
        plan.NodeCount.Should().Be(41);
    }

    [Fact]
    public void A_plan_with_more_nodes_than_the_bound_is_read_to_the_bound_and_says_so()
    {
        StringBuilder json = new("""[{"Plan": {"Node Type": "Append", "Total Cost": 1, "Plans": [""");

        for (int i = 0; i < QueryPlanReader.MaxNodes + 100; i++)
        {
            json.Append(i == 0 ? string.Empty : ",").Append("""{"Node Type": "Seq Scan", "Total Cost": 1}""");
        }

        json.Append("]}}]");

        QueryPlan plan = Plan(json.ToString());

        plan.Truncated.Should().BeTrue();
        plan.NodeCount.Should().Be(QueryPlanReader.MaxNodes);
    }

    [Fact]
    public void A_plan_larger_than_the_bound_is_not_read()
    {
        string json = "[{\"Plan\": {\"Node Type\": \"Result\", \"Filter\": \"" + new string('x', QueryPlanReader.MaxCharacters) + "\"}}]";

        QueryPlanRead read = QueryPlanReader.Read(json);

        read.Plan.Should().BeNull();
        read.Failure.Should().Contain("more than the");
    }

    [Fact]
    public void A_join_type_a_strategy_and_parallelism_head_a_node()
    {
        PlanNode node = Plan("""
            [{"Plan": {"Node Type": "Hash Join", "Join Type": "Anti", "Plans": [
                {"Node Type": "Nested Loop", "Join Type": "Left"},
                {"Node Type": "Aggregate", "Strategy": "Mixed"},
                {"Node Type": "SetOp", "Strategy": "Hashed"},
                {"Node Type": "ModifyTable", "Operation": "Delete", "Relation Name": "t"},
                {"Node Type": "Append", "Parallel Aware": true, "Async Capable": true}]}}]
            """).Root;

        node.Label.Should().Be("Hash Anti Join");
        node.Children.Select(static x => x.Label).Should().Equal("Nested Loop Left Join", "MixedAggregate", "HashSetOp", "Delete", "Async Parallel Append");
        node.Children[3].Description.Should().Be("Delete on t");
    }

    [Fact]
    public void Verbose_prints_a_schema_and_the_relations_are_read_unmasked()
    {
        const string Json = """
            [{"Plan": {"Node Type": "Hash Join", "Plans": [
                {"Node Type": "Seq Scan", "Schema": "legacy", "Relation Name": "employees", "Alias": "e"},
                {"Node Type": "Hash", "Plans": [{"Node Type": "Index Scan", "Schema": "public", "Relation Name": "orders", "Alias": "orders", "Index Name": "orders_pkey"}]}]}}]
            """;

        QueryPlanReader.Relations(Json).Should().Equal(("legacy", "employees"), ("public", "orders"));
        Plan(Json).Nodes.Select(static x => x.Description).Should().Contain(["Seq Scan on legacy.employees e", "Index Scan using orders_pkey on public.orders"]);
    }

    [Fact]
    public void The_mask_reaches_every_name_and_condition_and_never_postgres_own_words()
    {
        const string Json = """
            [{"Plan": {"Node Type": "Sort", "Sort Key": ["hr.x"], "Plans": [
                {"Node Type": "Seq Scan", "Parallel Aware": true, "Schema": "hr", "Relation Name": "people", "Alias": "hr",
                 "Filter": "(hr.f(p) > 1)", "Subplan Name": "CTE hr"}]}}]
            """;

        // "Seq" too: were the mask applied to a node's type, its label would read "Parallel ‹withheld› Scan".
        QueryPlan plan = QueryPlanReader.Read(
            Json,
            static x => x.Replace("hr", "‹withheld›", StringComparison.Ordinal).Replace("Seq", "‹withheld›", StringComparison.Ordinal)).Plan!;
        PlanNode scan = plan.Root.Children[0];

        plan.Root.SortKey.Should().Equal("‹withheld›.x");
        scan.Schema.Should().Be("‹withheld›");
        scan.Alias.Should().Be("‹withheld›");
        scan.Filter.Should().Be("(‹withheld›.f(p) > 1)");
        scan.SubplanName.Should().Be("CTE ‹withheld›");
        scan.Label.Should().Be("Parallel Seq Scan", "Postgres' vocabulary is never masked");
    }

    [Fact]
    public void Triggers_and_jit_are_read_where_analyze_prints_them()
    {
        QueryPlan plan = Plan("""
            [{"Plan": {"Node Type": "ModifyTable", "Operation": "Insert", "Actual Loops": 1, "Actual Total Time": 2.5},
              "Triggers": [{"Trigger Name": "audit", "Relation": "orders", "Time": 1.25, "Calls": 3}, {"Time": 1}],
              "JIT": {"Functions": 4, "Timing": {"Total": 12.5}}}]
            """);

        plan.Triggers.Should().ContainSingle().Which.Should().Be(new PlanTrigger("audit", "orders", 1.25, 3));
        plan.JitFunctions.Should().Be(4);
        plan.JitMs.Should().Be(12.5);
    }

    /// <summary>A plan that is one chain of <paramref name="levels" /> nodes below its root.</summary>
    internal static string Chain(int levels)
    {
        StringBuilder json = new("[{\"Plan\": ");

        for (int i = 0; i < levels; i++)
        {
            json.Append("{\"Node Type\": \"Materialize\", \"Total Cost\": 1, \"Plans\": [");
        }

        json.Append("{\"Node Type\": \"Result\", \"Total Cost\": 1}");

        for (int i = 0; i < levels; i++)
        {
            json.Append("]}");
        }

        return json.Append("}]").ToString();
    }
}
