using MartenStudio.Services.Query;
using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The one reader of an <c>EXPLAIN (FORMAT JSON)</c> plan, extracted from F7's LINQ advisor for T02: the same scans in the
/// same order as before, now with each scan's filter and index condition.
/// </summary>
public class ExplainPlanScansTests
{
    /// <summary>What PostgreSQL 17 printed for a generic plan of a Marten-shaped select (measured).</summary>
    private const string Plan =
        """
        [
          {
            "Plan": {
              "Node Type": "Limit",
              "Plans": [
                {
                  "Node Type": "Seq Scan",
                  "Relation Name": "mt_doc_target",
                  "Alias": "d",
                  "Filter": "((data @> $5) AND ((color)::text = $4) AND ((data ->> $7) = $1))"
                },
                {
                  "Node Type": "Bitmap Heap Scan",
                  "Relation Name": "mt_doc_other",
                  "Recheck Cond": "(id = $2)",
                  "Plans": [ { "Node Type": "Bitmap Index Scan", "Index Name": "mt_doc_other_pkey", "Index Cond": "(id = $2)" } ]
                }
              ]
            }
          }
        ]
        """;

    [Fact]
    public void Every_scan_is_read_outermost_first_with_its_filter_and_its_index_condition()
    {
        IReadOnlyList<ExplainPlanScan> scans = ExplainPlanScans.Read(Plan);

        scans.Should().Equal(
            new ExplainPlanScan("Seq Scan", "mt_doc_target", null, "((data @> $5) AND ((color)::text = $4) AND ((data ->> $7) = $1))", null),
            new ExplainPlanScan("Bitmap Heap Scan", "mt_doc_other", null, null, "(id = $2)"),
            new ExplainPlanScan("Bitmap Index Scan", null, "mt_doc_other_pkey", null, "(id = $2)"));
    }

    [Fact]
    public void The_linq_advisor_reads_the_same_scans_it_read_before()
    {
        LinqIndexAdvisor.ReadScans(Plan).Should().Equal(
            new LinqPlanScan("Seq Scan", "mt_doc_target", null),
            new LinqPlanScan("Bitmap Heap Scan", "mt_doc_other", null),
            new LinqPlanScan("Bitmap Index Scan", null, "mt_doc_other_pkey"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1, \"x\"]")]
    public void A_plan_that_is_not_postgres_json_has_no_scans(string json)
    {
        ExplainPlanScans.Read(json).Should().BeEmpty();
    }
}
