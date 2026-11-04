using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 1.2-INDEX-A: the indexes one plan of the index advisor's workload suggests - X2's two shapes as they are, P10's shape on a plain
/// plan (no <c>ANALYZE</c>, so read off the node itself), and nothing a builder does not write: no JSON operator, no function, no
/// <c>LIKE</c>, no P17 expression index.
/// </summary>
public class WorkloadIndexCandidatesTests
{
    private static QueryPlan Plan(string json) => QueryPlanReader.Read(json).Plan!;

    private static string Scan(string nodeType, string table, string filter, string schema = "public") =>
        $$$"""[{"Plan": {"Node Type": "{{{nodeType}}}", "Relation Name": "{{{table}}}", "Schema": "{{{schema}}}", "Alias": "{{{table}}}", "Total Cost": 100, "Plan Rows": 10, "Plan Width": 8, "Filter": "{{{filter}}}"}}]""";

    [Fact]
    public void A_sequential_scans_filter_is_the_index_P04_judges()
    {
        HypotheticalIndex index = WorkloadIndexCandidates.FromPlan(Plan(Scan("Seq Scan", "orders", "(orders.customer_id = $1)"))).Should().ContainSingle().Subject;

        index.Definition.Should().Be("CREATE INDEX ON \"public\".\"orders\" (\"customer_id\");");
        index.Columns.Should().Equal("customer_id");
        index.Origin.Should().Be(HypotheticalIndexOrigin.Plan);
    }

    [Fact]
    public void A_nested_loops_sequentially_scanned_inner_side_is_the_index_P05_judges()
    {
        QueryPlan plan = Plan(
            """
            [{"Plan": {"Node Type": "Nested Loop", "Total Cost": 50, "Join Filter": "(l.order_id = o.id)",
              "Plans": [
                {"Node Type": "Seq Scan", "Parent Relationship": "Outer", "Relation Name": "orders", "Schema": "public", "Alias": "o", "Total Cost": 10},
                {"Node Type": "Seq Scan", "Parent Relationship": "Inner", "Relation Name": "order_lines", "Schema": "public", "Alias": "l", "Total Cost": 10}]}}]
            """);

        WorkloadIndexCandidates.FromPlan(plan).Should().ContainSingle().Which.Definition
            .Should().Be("CREATE INDEX ON \"public\".\"order_lines\" (\"order_id\");");
    }

    [Theory]
    [InlineData("Index Scan")]
    [InlineData("Index Only Scan")]
    [InlineData("Bitmap Heap Scan")]
    public void An_indexed_scan_with_a_filter_suggests_the_filters_columns_without_ANALYZE(string nodeType)
    {
        QueryPlan plan = Plan(Scan(nodeType, "orders", "((orders.status = 'open'::text) AND (orders.total > 10))"));

        plan.Analyzed.Should().BeFalse("a plain plan has no rows removed to count: the shape is read off the node");
        WorkloadIndexCandidates.FromPlan(plan).Should().ContainSingle().Which.Columns.Should().Equal("status", "total");
    }

    [Theory]
    [InlineData("((orders.data ->> 'Name'::text) = $1)")]
    [InlineData("(lower((orders.name)::text) = $1)")]
    [InlineData("(orders.name ~~ 'abc%'::text)")]
    [InlineData("(orders.data @> '{a}'::jsonb)")]
    public void A_json_operator_a_function_or_a_like_suggests_no_index(string filter)
    {
        WorkloadIndexCandidates.FromPlan(Plan(Scan("Seq Scan", "orders", filter))).Should().BeEmpty();
        WorkloadIndexCandidates.FromPlan(Plan(Scan("Index Scan", "orders", filter))).Should().BeEmpty();
    }

    [Fact]
    public void A_table_whose_schema_the_plan_does_not_print_is_not_offered()
    {
        string plain = """[{"Plan": {"Node Type": "Index Scan", "Relation Name": "orders", "Alias": "orders", "Total Cost": 100, "Filter": "(a = 1)"}}]""";

        WorkloadIndexCandidates.FromPlan(Plan(plain)).Should().BeEmpty("the advisor finds a table by the schema and name a plan prints");
    }

    [Fact]
    public void No_more_than_four_indexes_come_from_one_plan()
    {
        string[] scans = [.. Enumerable.Range(0, 6).Select(static i => $$"""{"Node Type": "Index Scan", "Relation Name": "t{{i}}", "Schema": "public", "Alias": "t{{i}}", "Total Cost": 10, "Filter": "(t{{i}}.a = 1)"}""")];
        QueryPlan plan = Plan("""[{"Plan": {"Node Type": "Append", "Total Cost": 60, "Plans": [""" + string.Join(",", scans) + "]}}]");

        WorkloadIndexCandidates.FromPlan(plan).Should().HaveCount(HypotheticalIndexCandidates.MaxCandidates);
    }

    [Fact]
    public void The_scan_cost_of_a_table_is_its_costliest_node_found_by_schema_and_name()
    {
        QueryPlan plan = Plan(Scan("Seq Scan", "orders", "(a = 1)"));

        WorkloadIndexCandidates.ScanCost(plan, "public", "orders").Should().Be(100);
        WorkloadIndexCandidates.ScanCost(plan, "other", "orders").Should().Be(0);
        WorkloadIndexCandidates.Reads(plan, "public", "orders").Should().BeTrue();
        WorkloadIndexCandidates.Reads(plan, "public", "order_lines").Should().BeFalse();
    }

    [Fact]
    public void The_compared_columns_are_uncapped_and_bound_to_the_scans_alias()
    {
        HypotheticalIndexCandidates.ColumnsCompared("((o.a = 1) AND (o.b = 2) AND (o.c = 3) AND (o.d = 4) AND (x.e = 5))", "o")
            .Should().Equal("a", "b", "c", "d");
        HypotheticalIndexCandidates.Columns("((o.a = 1) AND (o.b = 2) AND (o.c = 3) AND (o.d = 4))", "o")
            .Should().HaveCount(HypotheticalIndexCandidates.MaxColumns);
        HypotheticalIndexCandidates.ColumnsCompared("(l.order_id = o.id)", "l", prefixedOnly: true).Should().Equal("order_id");
        HypotheticalIndexCandidates.ColumnsCompared("(o.a > 1)", "o").Should().Equal("a");
    }
}
