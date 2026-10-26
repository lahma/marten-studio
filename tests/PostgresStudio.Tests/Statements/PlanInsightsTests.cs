using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// X1b's plan insights, each a pure evaluator: every one fires on its shape's fixtures and stays quiet on every other of the 108
/// (all majors and both modes), and the shapes no fixture holds - a lossy bitmap, a hash that split, a parallel plan short of
/// workers, an estimate off the other way - are built by hand. Then the registry: its codes, its docs headings, its words.
/// </summary>
public class PlanInsightsTests
{
    private static readonly StudioText Marten = StudioText.Marten;

    /// <summary>
    /// Where each insight fires among the fixtures: the shapes the probe asked for, and P01 where the planner's estimate of
    /// <c>c like '%abc%'</c> happened to miss tenfold (20 expected, 1,462 returned on 14, 17 and 19rc1; 2,020 or 4,040 on 15,
    /// 16 and 18, inside it). P03 and P08 fire on none: no fixture holds their shape.
    /// </summary>
    private static readonly Dictionary<string, Func<string, bool>> FiresOn = new(StringComparer.Ordinal)
    {
        ["P01"] = static x => x is "14-seqfilter-analyze" or "17-seqfilter-analyze" or "19rc1-seqfilter-analyze",
        ["P02"] = static x => ExplainFixtures.ShapeOf(x) == "sort-spill" && ExplainFixtures.IsAnalyze(x),
        ["P03"] = static _ => false,
        ["P04"] = static x => ExplainFixtures.ShapeOf(x) == "seqfilter" && ExplainFixtures.IsAnalyze(x),
        ["P05"] = static x => ExplainFixtures.ShapeOf(x) == "nestloop",
        ["P06"] = static x => ExplainFixtures.ShapeOf(x) == "offset",
        ["P07"] = static x => ExplainFixtures.ShapeOf(x) == "seqfilter",
        ["P08"] = static _ => false,
    };

    public static TheoryData<string, string> EveryInsightOnEveryFixture()
    {
        TheoryData<string, string> data = [];

        foreach (PlanInsightInfo insight in PlanInsights.All)
        {
            foreach (string name in ExplainFixtures.All.Keys.Order(StringComparer.Ordinal))
            {
                data.Add(insight.Code, name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryInsightOnEveryFixture))]
    public void An_insight_fires_on_its_shapes_fixtures_and_stays_quiet_on_every_other(string code, string name)
    {
        List<PlanInsightFinding> findings = [.. Evaluate(ExplainFixtures.Get(name)).Where(x => x.Insight.Code == code)];

        if (FiresOn[code](name))
        {
            findings.Should().NotBeEmpty("{0} is the shape {1} is about", name, code);
        }
        else
        {
            findings.Should().BeEmpty("{0} is not {1}'s shape: {2}", name, code, string.Join(" | ", findings.Select(static x => x.Message)));
        }
    }

    [Fact]
    public void Every_insight_is_held_to_its_fixtures_on_every_major()
    {
        FiresOn.Keys.Should().BeEquivalentTo(PlanInsights.All.Select(static x => x.Code), "every insight's shape is written down");
        ExplainFixtures.All.Should().HaveCount(ExplainFixtures.Majors.Length * ExplainFixtures.Shapes.Length * 2);
    }

    [Theory]
    [MemberData(nameof(ExplainFixtures.Names), MemberType = typeof(ExplainFixtures))]
    public void A_plain_plan_lists_the_insights_it_cannot_be_judged_by_and_an_analyzed_one_none(string name)
    {
        QueryPlan plan = QueryPlanReader.Read(ExplainFixtures.Get(name)).Plan!;

        PlanInsights.NotJudged(plan).Select(static x => x.Code).Should().Equal(
            ExplainFixtures.IsAnalyze(name) ? [] : ["P01", "P02", "P03", "P04", "P08"]);
    }

    [Theory]
    [InlineData("14-seqfilter-analyze", "Seq Scan on t was planned for 20 rows a loop and returned 1,462: 73.1 times more")]
    [InlineData("18-sort-spill-analyze", "Sort sorted on disk (external merge, 8.2 MiB).")]
    [InlineData("16-seqfilter-analyze", "Seq Scan on t read 200,000 rows to keep 1,462 (0.7%): its filter, (c ~~ '%abc%'::text),")]
    [InlineData("17-nestloop-plain", "Nested Loop runs its inner side once for each of an estimated 7,807 rows from Seq Scan on u.")]
    [InlineData("17-nestloop-analyze", "Nested Loop runs its inner side once for each of 7,693 rows from Seq Scan on u.")]
    [InlineData("14-offset-plain", "Limit is expected to read and discard about 150,000 rows from Index Scan using t_pkey on t")]
    [InlineData("18-offset-analyze", "Limit read and discarded 150,000 rows from Index Scan using t_pkey on t")]
    [InlineData("15-seqfilter-plain", "Seq Scan on t tests LIKE '%abc%', a pattern that starts with a wildcard")]
    public void A_finding_says_what_the_plan_says_with_its_numbers(string name, string message)
    {
        Evaluate(ExplainFixtures.Get(name)).Select(static x => x.Message).Should().Contain(x => x.StartsWith(message, StringComparison.Ordinal),
            string.Join(" | ", Evaluate(ExplainFixtures.Get(name)).Select(static x => x.Message)));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The shapes no fixture holds
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void P01_fires_on_an_estimate_too_high_and_stays_quiet_on_a_small_one_or_one_inside_tenfold()
    {
        Codes(Analyzed("""{"Node Type": "Index Scan", "Plan Rows": 50000, "Actual Rows": 12, "Actual Loops": 1}""")).Should().Contain("P01");
        Codes(Analyzed("""{"Node Type": "Index Scan", "Plan Rows": 1, "Actual Rows": 90, "Actual Loops": 1}""")).Should().NotContain("P01", "90 rows is no plan's problem");
        Codes(Analyzed("""{"Node Type": "Index Scan", "Plan Rows": 1, "Actual Rows": 90, "Actual Loops": 20}""")).Should().Contain("P01", "1,800 rows over its loops is");
        Codes(Analyzed("""{"Node Type": "Seq Scan", "Plan Rows": 2000, "Actual Rows": 19000, "Actual Loops": 1}""")).Should().NotContain("P01", "9.5 times is inside tenfold");
        Codes(Analyzed("""{"Node Type": "Seq Scan", "Plan Rows": 2000, "Actual Rows": 0, "Actual Loops": 0}""")).Should().NotContain("P01", "a node that never ran returned nothing to compare");
    }

    [Fact]
    public void P02_fires_on_a_hash_that_split_a_hash_aggregate_on_disk_and_a_workers_sort()
    {
        Codes(Analyzed("""
            {"Node Type": "Hash Join", "Actual Rows": 1, "Actual Loops": 1, "Plans": [
              {"Node Type": "Seq Scan", "Parent Relationship": "Outer", "Actual Rows": 1, "Actual Loops": 1},
              {"Node Type": "Hash", "Parent Relationship": "Inner", "Hash Buckets": 65536, "Hash Batches": 8, "Original Hash Batches": 2, "Peak Memory Usage": 4096, "Actual Rows": 1, "Actual Loops": 1}]}
            """)).Should().Contain("P02");

        Codes(Analyzed("""{"Node Type": "Aggregate", "Strategy": "Hashed", "HashAgg Batches": 5, "Disk Usage": 2048, "Actual Rows": 1, "Actual Loops": 1}"""))
            .Should().Contain("P02");

        Codes(Analyzed("""
            {"Node Type": "Sort", "Sort Method": "quicksort", "Sort Space Type": "Memory", "Sort Space Used": 25, "Actual Rows": 1, "Actual Loops": 1,
             "Workers": [{"Worker Number": 0, "Sort Method": "external merge", "Sort Space Used": 900, "Sort Space Type": "Disk"}]}
            """)).Should().Contain("P02", "a worker's sort that spilled is a spill");

        Codes(Analyzed("""{"Node Type": "Hash", "Hash Batches": 1, "Original Hash Batches": 1, "Actual Rows": 1, "Actual Loops": 1}""")).Should().BeEmpty();
        Codes(Analyzed("""{"Node Type": "Aggregate", "Strategy": "Hashed", "HashAgg Batches": 1, "Disk Usage": 0, "Actual Rows": 1, "Actual Loops": 1}""")).Should().BeEmpty();
        Codes(Plain("""{"Node Type": "Hash", "Hash Batches": 8}""")).Should().NotContain("P02", "a plain plan's numbers are not a run's");
    }

    [Fact]
    public void P02_says_how_many_batches_a_hash_was_planned_with_and_grew_to()
    {
        PlanInsightFinding finding = Evaluate(Analyzed("""{"Node Type": "Hash", "Hash Batches": 8, "Original Hash Batches": 2, "Actual Rows": 1, "Actual Loops": 1}"""))
            .Single(static x => x.Insight.Code == "P02");

        finding.Message.Should().Be("Hash split into 8 batches, where 2 were planned, writing every batch but the first to temporary files.");
    }

    [Fact]
    public void P03_fires_on_a_lossy_bitmap_and_stays_quiet_on_an_exact_one()
    {
        PlanInsightFinding finding = Evaluate(Analyzed("""
            {"Node Type": "Bitmap Heap Scan", "Relation Name": "t", "Exact Heap Blocks": 120, "Lossy Heap Blocks": 2500,
             "Rows Removed by Index Recheck": 140000, "Actual Rows": 60000, "Actual Loops": 1}
            """)).Single(static x => x.Insight.Code == "P03");

        finding.Message.Should().Be(
            "Bitmap Heap Scan on t kept 2,500 heap pages only as whole pages (and 120 exactly), so every row on them was checked "
            + "again and 140,000 rows a loop thrown away.");
        finding.Remedy.Should().Contain("work_mem");

        Codes(Analyzed("""{"Node Type": "Bitmap Heap Scan", "Exact Heap Blocks": 120, "Lossy Heap Blocks": 0, "Actual Rows": 1, "Actual Loops": 1}""")).Should().BeEmpty();
    }

    [Fact]
    public void P04_stays_quiet_below_a_large_table_and_above_a_selective_share()
    {
        Codes(Analyzed("""{"Node Type": "Seq Scan", "Filter": "(a = 1)", "Actual Rows": 5, "Rows Removed by Filter": 99000, "Actual Loops": 1}"""))
            .Should().NotContain("P04", "99,005 rows is under T01's large table");
        Codes(Analyzed("""{"Node Type": "Seq Scan", "Filter": "(a = 1)", "Actual Rows": 11000, "Rows Removed by Filter": 89000, "Actual Loops": 1}"""))
            .Should().NotContain("P04", "11% is not selective");
        Codes(Analyzed("""{"Node Type": "Seq Scan", "Actual Rows": 100, "Actual Loops": 1}""")).Should().NotContain("P04", "no filter, nothing to index");
        Codes(Analyzed("""{"Node Type": "Seq Scan", "Filter": "(a = 1)", "Actual Rows": 100, "Rows Removed by Filter": 40000, "Actual Loops": 3}"""))
            .Should().Contain("P04", "three loops read 120,300 rows to keep 300");
    }

    [Fact]
    public void P04s_remedy_is_each_studios_own()
    {
        string json = Analyzed("""{"Node Type": "Seq Scan", "Filter": "(a = 1)", "Actual Rows": 1, "Rows Removed by Filter": 200000, "Actual Loops": 1}""");

        Evaluate(json, Marten).Single(static x => x.Insight.Code == "P04").Remedy.Should().Be(Marten.PlanIndexRemedy).And.Contain("CreateOrUpdate");
        Evaluate(json, PostgresStudioText.Instance).Single(static x => x.Insight.Code == "P04").Remedy.Should().Be(PostgresStudioText.Instance.PlanIndexRemedy)
            .And.NotContain("Marten");
    }

    [Fact]
    public void P05_stays_quiet_on_a_small_outer_side_and_reads_the_outer_by_its_relationship()
    {
        Codes(Plain("""
            {"Node Type": "Nested Loop", "Plans": [{"Node Type": "Seq Scan", "Parent Relationship": "Outer", "Plan Rows": 999},
                                                   {"Node Type": "Index Scan", "Parent Relationship": "Inner", "Plan Rows": 1}]}
            """)).Should().NotContain("P05");

        Codes(Plain("""
            {"Node Type": "Nested Loop", "Plans": [{"Node Type": "Index Scan", "Parent Relationship": "Inner", "Plan Rows": 1},
                                                   {"Node Type": "Seq Scan", "Parent Relationship": "Outer", "Plan Rows": 5000}]}
            """)).Should().Contain("P05");
    }

    [Fact]
    public void P06_stays_quiet_on_a_limit_without_offset_and_on_the_planners_guess_for_a_parameter()
    {
        // LIMIT 10 alone: the limit's startup is its input's.
        Codes(Plain("""
            {"Node Type": "Limit", "Startup Cost": 0.42, "Total Cost": 0.81, "Plan Rows": 10, "Plans": [
              {"Node Type": "Index Scan", "Startup Cost": 0.42, "Total Cost": 7874.42, "Plan Rows": 200000}]}
            """)).Should().BeEmpty();

        // OFFSET $1 in a generic plan: the planner assumes a tenth of the input, 20,000 rows, and says nothing about the real one.
        Codes(Plain("""
            {"Node Type": "Limit", "Startup Cost": 787.82, "Total Cost": 788.21, "Plan Rows": 10, "Plans": [
              {"Node Type": "Index Scan", "Startup Cost": 0.42, "Total Cost": 7874.42, "Plan Rows": 200000}]}
            """)).Should().BeEmpty();

        PlanInsights.EstimatedOffset(
            new PlanNode { Id = 0, Depth = 0, NodeType = "Limit", Label = "Limit", StartupCost = 5905.92 },
            new PlanNode { Id = 1, Depth = 1, NodeType = "Index Scan", Label = "Index Scan", StartupCost = 0.42, TotalCost = 7874.42, PlanRows = 200000 })
            .Should().BeApproximately(150_000, 1, "the probe's offset 150000, read back from the costs");
    }

    [Theory]
    [InlineData("(c ~~* '%abc%'::text)", true)]
    [InlineData("(c ~~ '_bc%'::text)", true)]
    [InlineData("((data ->> 'Name'::text) ~~ '%it''s%'::text)", true)]
    [InlineData("(c ~~ 'abc%'::text)", false)]
    [InlineData("(c !~~ '%abc%'::text)", false)]
    [InlineData("(c !~~* '%abc%'::text)", false)]
    [InlineData("(c ~~ $1)", false)]
    [InlineData("(c = '%abc%'::text)", false)]
    public void P07_reads_a_like_or_ilike_whose_pattern_starts_with_a_wildcard(string filter, bool fires)
    {
        string json = Plain("""{"Node Type": "Seq Scan", "Relation Name": "t", "Filter": """ + System.Text.Json.JsonSerializer.Serialize(filter) + "}");

        Codes(json).Contains("P07").Should().Be(fires);
    }

    [Fact]
    public void P07_leaves_a_pattern_an_index_serves_alone()
    {
        Codes(Plain("""{"Node Type": "Bitmap Heap Scan", "Recheck Cond": "(c ~~ '%abc%'::text)", "Plans": [{"Node Type": "Bitmap Index Scan", "Index Cond": "(c ~~ '%abc%'::text)"}]}"""))
            .Should().NotContain("P07", "a trigram index already answers it");
    }

    [Fact]
    public void P08_fires_on_a_gather_given_fewer_workers_than_planned()
    {
        PlanInsightFinding finding = Evaluate(Analyzed("""
            {"Node Type": "Gather", "Workers Planned": 4, "Workers Launched": 1, "Actual Rows": 1, "Actual Loops": 1, "Plans": [
              {"Node Type": "Seq Scan", "Parallel Aware": true, "Actual Rows": 1, "Actual Loops": 2}]}
            """)).Single(static x => x.Insight.Code == "P08");

        finding.Message.Should().Be("Gather was planned with 4 parallel workers and given 1, so the rest of its work fell to the processes it got.");

        Codes(Analyzed("""{"Node Type": "Gather", "Workers Planned": 2, "Workers Launched": 2, "Actual Rows": 1, "Actual Loops": 1}""")).Should().BeEmpty();
        Codes(Plain("""{"Node Type": "Gather", "Workers Planned": 2}""")).Should().BeEmpty("a plain plan launches nothing");
    }

    [Fact]
    public void An_insight_reports_at_most_five_findings_the_worst_first()
    {
        string children = string.Join(",", Enumerable.Range(1, 9).Select(static i =>
            "{\"Node Type\": \"Bitmap Heap Scan\", \"Lossy Heap Blocks\": " + (i * 10) + ", \"Actual Rows\": 1, \"Actual Loops\": 1}"));

        List<PlanInsightFinding> findings =
        [
            .. Evaluate(Analyzed("{\"Node Type\": \"Append\", \"Actual Rows\": 1, \"Actual Loops\": 1, \"Plans\": [" + children + "]}"))
                .Where(static x => x.Insight.Code == "P03"),
        ];

        findings.Should().HaveCount(PlanInsights.MaxPerInsight);
        findings[0].Message.Should().Contain("kept 90 heap pages");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The registry
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_code_is_P_and_two_digits_unique_and_in_order_with_a_title()
    {
        PlanInsights.All.Select(static x => x.Code).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal)
            .And.OnlyContain(static x => Regex.IsMatch(x, "^P[0-9]{2}$", RegexOptions.None, TimeSpan.FromSeconds(1)));
        PlanInsights.All.Should().OnlyContain(static x => x.Title.Length > 0 && x.Severity != HealthSeverity.Critical,
            "an insight is advice about one plan; nothing in a plan is broken now");
        HealthCheckRegistry.AreaOf(PlanInsights.Letter).Should().BeNull("P is no health area's letter, so no check can take a plan insight's code");
    }

    [Fact]
    public void Every_insight_has_its_heading_in_the_plans_docs_and_links_to_it()
    {
        string docs = File.ReadAllText(RepositoryRoot.Combine("docs", "health", PlanInsights.DocsFileName));

        foreach (PlanInsightInfo insight in PlanInsights.All)
        {
            docs.Split('\n').Select(static x => x.TrimEnd()).Should().Contain(insight.DocsHeading);
            insight.DocsUrl.Should().EndWith("/docs/health/plans.md#" + insight.DocsAnchor);
        }

        PlanInsights.All[0].DocsAnchor.Should().Be("p01-row-estimate-off-tenfold-or-more");
    }

    [Fact]
    public void In_Postgres_Studios_words_no_finding_speaks_of_a_store()
    {
        // One plan that fires every insight.
        string json = Analyzed("""
            {"Node Type": "Limit", "Startup Cost": 100, "Total Cost": 101, "Plan Rows": 1, "Actual Rows": 10, "Actual Loops": 1, "Plans": [
              {"Node Type": "Nested Loop", "Startup Cost": 0, "Total Cost": 200, "Plan Rows": 5000, "Actual Rows": 5010, "Actual Loops": 1, "Plans": [
                {"Node Type": "Seq Scan", "Parent Relationship": "Outer", "Relation Name": "t", "Filter": "(c ~~ '%x%'::text)", "Plan Rows": 2, "Actual Rows": 5000, "Rows Removed by Filter": 300000, "Actual Loops": 1},
                {"Node Type": "Gather", "Parent Relationship": "Inner", "Workers Planned": 2, "Workers Launched": 0, "Actual Rows": 1, "Actual Loops": 5000, "Plans": [
                  {"Node Type": "Sort", "Sort Method": "external merge", "Sort Space Type": "Disk", "Sort Space Used": 100, "Actual Rows": 1, "Actual Loops": 5000, "Plans": [
                    {"Node Type": "Bitmap Heap Scan", "Lossy Heap Blocks": 3, "Actual Rows": 1, "Actual Loops": 5000}]}]}]}]}
            """);

        IReadOnlyList<PlanInsightFinding> findings = Evaluate(json, PostgresStudioText.Instance);
        findings.Select(static x => x.Insight.Code).Distinct().Should().BeEquivalentTo(PlanInsights.All.Select(static x => x.Code));

        foreach (PlanInsightFinding finding in findings)
        {
            foreach (string word in (string[]) ["Marten", "store", "StoreOptions", "document", "tenant"])
            {
                finding.Message.Should().NotContain(word);
                finding.Remedy.Should().NotContain(word);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<PlanInsightFinding> Evaluate(string json, StudioText? text = null) =>
        PlanInsights.Evaluate(QueryPlanReader.Read(json).Plan!, text ?? Marten);

    private static List<string> Codes(string json) => [.. Evaluate(json).Select(static x => x.Insight.Code)];

    /// <summary>A plan of one statement around <paramref name="root" />, as EXPLAIN (ANALYZE) prints one.</summary>
    private static string Analyzed(string root) => "[{\"Plan\": " + root + ", \"Planning Time\": 0.1, \"Execution Time\": 1.0}]";

    /// <summary>A plan of one statement around <paramref name="root" />, as plain EXPLAIN prints one.</summary>
    private static string Plain(string root) => "[{\"Plan\": " + root + "}]";
}
