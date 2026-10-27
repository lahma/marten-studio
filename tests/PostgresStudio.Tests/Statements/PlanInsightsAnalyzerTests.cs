using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 0.9's six insights (P09 to P14) at their edges - the number a threshold is, one either side of it, a plain plan - over plans
/// built by hand, where <see cref="PlanCorpusTests" /> reads them off whole plans.
/// </summary>
public class PlanInsightsAnalyzerTests
{
    private static IReadOnlyList<PlanInsightFinding> Evaluate(string json) => PlanInsights.Evaluate(QueryPlanReader.Read(json).Plan!, StudioText.Marten);

    private static List<string> Codes(string json) => [.. Evaluate(json).Select(static x => x.Insight.Code)];

    /// <summary>A plan of one statement around <paramref name="root" />, as EXPLAIN (ANALYZE) prints one.</summary>
    private static string Analyzed(string root, double planning = 0.1, double execution = 50) =>
        "[{\"Plan\": " + root + ", \"Planning Time\": " + planning.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ", \"Execution Time\": " + execution.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}]";

    private static string Plain(string root) => "[{\"Plan\": " + root + "}]";

    // ---- P09 -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1000, 10000, true)]
    [InlineData(999, 9990, false)]
    [InlineData(1000, 10001, false)]
    [InlineData(5000, 50000, true)]
    [InlineData(5000, 50001, false)]
    [InlineData(1000, 0, true)]
    public void P09_fires_at_ten_percent_of_the_rows_and_a_thousand_fetches(int fetches, int rows, bool fires)
    {
        string json = Analyzed("{\"Node Type\": \"Index Only Scan\", \"Heap Fetches\": " + fetches + ", \"Actual Rows\": " + rows + ", \"Actual Loops\": 1}");

        Codes(json).Contains("P09").Should().Be(fires);
    }

    [Fact]
    public void P09_counts_the_rows_over_every_loop_and_reads_only_index_only_scans()
    {
        // 1,500 fetches against 100 rows a loop over 20 loops: 2,000 rows, and 75% is over a tenth.
        Codes(Analyzed("{\"Node Type\": \"Index Only Scan\", \"Heap Fetches\": 1500, \"Actual Rows\": 100, \"Actual Loops\": 20}")).Should().Contain("P09");

        // 1,500 fetches against 100 rows a loop over 200 loops: 20,000 rows, and 7.5% is under it.
        Codes(Analyzed("{\"Node Type\": \"Index Only Scan\", \"Heap Fetches\": 1500, \"Actual Rows\": 100, \"Actual Loops\": 200}")).Should().NotContain("P09");

        // A plain index scan has no Heap Fetches, and a scan that never ran has nothing to judge.
        Codes(Analyzed("{\"Node Type\": \"Index Scan\", \"Heap Fetches\": 5000, \"Actual Rows\": 1, \"Actual Loops\": 1}")).Should().NotContain("P09");
        Codes(Analyzed("{\"Node Type\": \"Index Only Scan\", \"Actual Rows\": 0, \"Actual Loops\": 0}")).Should().NotContain("P09");
    }

    [Fact]
    public void P09_is_not_judged_without_ANALYZE()
    {
        Codes(Plain("{\"Node Type\": \"Index Only Scan\", \"Heap Fetches\": 5000}")).Should().NotContain("P09");
    }

    // ---- P10 -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Index Scan", 10000, 1000, true)]
    [InlineData("Index Scan", 9999, 999, false)]
    [InlineData("Index Scan", 10000, 1001, false)]
    [InlineData("Index Only Scan", 50000, 5000, true)]
    [InlineData("Bitmap Heap Scan", 50000, 5000, true)]
    [InlineData("Seq Scan", 500000, 1, false)]
    [InlineData("Bitmap Index Scan", 500000, 1, false)]
    public void P10_fires_at_ten_times_the_rows_and_ten_thousand_discarded_on_the_three_index_scans(string type, int removed, int kept, bool fires)
    {
        string json = Analyzed("{\"Node Type\": \"" + type + "\", \"Filter\": \"(a = 1)\", \"Rows Removed by Filter\": " + removed + ", \"Actual Rows\": " + kept + ", \"Actual Loops\": 1}");

        Codes(json).Contains("P10").Should().Be(fires);
    }

    [Fact]
    public void P10_counts_both_over_every_loop_and_is_not_judged_without_ANALYZE()
    {
        // 600 removed and 10 kept a loop over 20 loops: 12,000 against 200.
        Codes(Analyzed("{\"Node Type\": \"Index Scan\", \"Rows Removed by Filter\": 600, \"Actual Rows\": 10, \"Actual Loops\": 20}")).Should().Contain("P10");

        // The same a loop over 5 loops is 3,000 removed: under ten thousand.
        Codes(Analyzed("{\"Node Type\": \"Index Scan\", \"Rows Removed by Filter\": 600, \"Actual Rows\": 10, \"Actual Loops\": 5}")).Should().NotContain("P10");

        Codes(Plain("{\"Node Type\": \"Index Scan\", \"Filter\": \"(a = 1)\"}")).Should().NotContain("P10");
    }

    // ---- P11 -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    public void P11_fires_on_a_hash_in_more_than_one_batch(int batches, int original, bool fires)
    {
        string json = Analyzed("{\"Node Type\": \"Hash\", \"Hash Batches\": " + batches + ", \"Original Hash Batches\": " + original + ", \"Peak Memory Usage\": 100, \"Actual Rows\": 1, \"Actual Loops\": 1}");

        Codes(json).Contains("P11").Should().Be(fires);
    }

    [Fact]
    public void P11_fires_when_a_hash_ended_with_more_batches_than_it_was_planned_with_and_rounds_the_memory_up()
    {
        PlanInsightFinding finding = Evaluate(Analyzed("{\"Node Type\": \"Hash\", \"Hash Batches\": 3, \"Original Hash Batches\": 1, \"Peak Memory Usage\": 2000, \"Actual Rows\": 1, \"Actual Loops\": 1}"))
            .Single(static x => x.Insight.Code == "P11");

        // 2,000 kB at its peak in 3 batches is 6,000 kB: 5.86 MB, rounded up to 6.
        finding.Remedy.Should().Contain("about 6 MB").And.Contain("SET work_mem = '6MB'");

        // Without the peak the remedy still says what to raise, and names no number.
        Evaluate(Analyzed("{\"Node Type\": \"Hash\", \"Hash Batches\": 4, \"Actual Rows\": 1, \"Actual Loops\": 1}")).Single(static x => x.Insight.Code == "P11")
            .Remedy.Should().Contain("work_mem").And.NotContain(" MB ");

        Codes(Plain("{\"Node Type\": \"Hash\", \"Hash Batches\": 8}")).Should().NotContain("P11");
        Codes(Analyzed("{\"Node Type\": \"Seq Scan\", \"Hash Batches\": 8, \"Actual Rows\": 1, \"Actual Loops\": 1}")).Should().NotContain("P11", "only a Hash node builds a hash");
    }

    // ---- P12 -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(20, 100, true)]
    [InlineData(19.9, 100, false)]
    [InlineData(200, 999, true)]
    [InlineData(300, 1000, false)]
    [InlineData(1, 5, true)]
    public void P12_fires_at_a_fifth_of_the_execution_time_when_it_ran_under_a_second(double jit, double execution, bool fires)
    {
        string json = Analyzed("{\"Node Type\": \"Result\", \"Actual Rows\": 1, \"Actual Loops\": 1}", execution: execution)
            .Replace("\"Planning Time\"", "\"JIT\": {\"Functions\": 2, \"Timing\": {\"Total\": " + jit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}, \"Planning Time\"", StringComparison.Ordinal);

        Codes(json).Contains("P12").Should().Be(fires);
    }

    [Fact]
    public void P12_is_quiet_without_JIT_and_not_judged_without_ANALYZE()
    {
        Codes(Analyzed("{\"Node Type\": \"Result\", \"Actual Rows\": 1, \"Actual Loops\": 1}", execution: 5)).Should().NotContain("P12");
        Codes("[{\"Plan\": {\"Node Type\": \"Result\"}, \"JIT\": {\"Functions\": 2, \"Timing\": {\"Total\": 40}}}]").Should().NotContain("P12");
    }

    // ---- P13 -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(10.1, 10.0, true)]
    [InlineData(10.0, 5.0, false)]
    [InlineData(50, 50, false)]
    [InlineData(50, 49.9, true)]
    [InlineData(9, 1, false)]
    public void P13_fires_when_planning_took_longer_than_running_and_longer_than_ten_milliseconds(double planning, double execution, bool fires)
    {
        Codes(Analyzed("{\"Node Type\": \"Result\", \"Actual Rows\": 1, \"Actual Loops\": 1}", planning, execution)).Contains("P13").Should().Be(fires);
    }

    [Fact]
    public void P13_is_not_judged_without_an_execution_time()
    {
        Codes("[{\"Plan\": {\"Node Type\": \"Result\"}, \"Planning Time\": 90}]").Should().NotContain("P13");
    }

    // ---- P14 -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100, 100, 1, true)]
    [InlineData(100, 101, 1, true)]
    [InlineData(101, 100, 1, false)]
    [InlineData(100, 900, 0, false)]
    [InlineData(0, 10, 5, true)]
    public void P14_fires_when_the_cache_missed_as_often_as_it_hit_and_evicted(int hits, int misses, int evictions, bool fires)
    {
        string json = Analyzed("{\"Node Type\": \"Memoize\", \"Cache Hits\": " + hits + ", \"Cache Misses\": " + misses + ", \"Cache Evictions\": " + evictions
            + ", \"Actual Rows\": 1, \"Actual Loops\": 1}");

        Codes(json).Contains("P14").Should().Be(fires);
    }

    [Fact]
    public void P14_is_not_judged_without_ANALYZE_and_reads_only_memoize()
    {
        Codes(Plain("{\"Node Type\": \"Memoize\", \"Cache Misses\": 10, \"Cache Evictions\": 5}")).Should().NotContain("P14");
        Codes(Analyzed("{\"Node Type\": \"Materialize\", \"Cache Misses\": 10, \"Cache Evictions\": 5, \"Actual Rows\": 1, \"Actual Loops\": 1}")).Should().NotContain("P14");
    }

    // ---- The registry --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_six_are_registered_in_code_order_and_every_one_needs_ANALYZE()
    {
        PlanInsights.All.Select(static x => x.Code).Should().Equal("P01", "P02", "P03", "P04", "P05", "P06", "P07", "P08", "P09", "P10", "P11", "P12", "P13", "P14");
        PlanInsights.All.Skip(8).Should().OnlyContain(static x => x.NeedsAnalyze);
    }

    [Fact]
    public void An_insight_reports_at_most_five_findings_of_the_six_too()
    {
        string children = string.Join(",", Enumerable.Range(1, 9).Select(static i =>
            "{\"Node Type\": \"Index Only Scan\", \"Heap Fetches\": " + (2000 + i) + ", \"Actual Rows\": 100, \"Actual Loops\": 1}"));

        List<PlanInsightFinding> findings =
        [
            .. Evaluate(Analyzed("{\"Node Type\": \"Append\", \"Actual Rows\": 1, \"Actual Loops\": 1, \"Plans\": [" + children + "]}")).Where(static x => x.Insight.Code == "P09"),
        ];

        findings.Should().HaveCount(PlanInsights.MaxPerInsight);
        findings[0].Message.Should().Contain("2,009 times", "the worst first");
    }
}
