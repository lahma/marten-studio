using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The analyzer's arithmetic over <c>analyzed-join.json</c>, whose numbers are chosen to be read off by hand: a sort over a hash
/// join of <c>orders</c> and <c>customers</c>, ids 0 to 4 in the order the walk reads them (sort, hash join, orders, hash,
/// customers), own times 8, 2, 30, 8 and 10 ms.
/// </summary>
public class PlanAnalysisTests
{
    private static QueryPlan Join() => PlanFixtures.Plan("analyzed-join");

    [Fact]
    public void The_critical_path_follows_the_largest_inclusive_time_from_the_root()
    {
        // Hash join 50 ms under the sort; under it orders 30 ms against the hash's 18 - and orders is a leaf.
        PlanAnalysis.CriticalPath(Join()).Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Without_timing_the_critical_path_follows_the_total_cost()
    {
        // Costs: hash join 2,000 under the sort; orders 1,500 against the hash's 100.
        QueryPlan plan = QueryPlanReader.Read(ExplainFixtures.Get("17-nestloop-plain")).Plan!;

        plan.Timed.Should().BeFalse();
        PlanAnalysis.CriticalPath(plan).Should().Equal(
            0,
            plan.Root.Children.OrderByDescending(static x => x.TotalCost).First().Id);
    }

    [Fact]
    public void A_path_stops_at_a_node_none_of_whose_children_did_anything()
    {
        QueryPlan plan = QueryPlanReader.Read("""
            [{"Plan": {"Node Type": "Append", "Actual Total Time": 10, "Actual Loops": 1, "Plans": [
              {"Node Type": "Seq Scan", "Actual Total Time": 0, "Actual Loops": 0},
              {"Node Type": "Seq Scan", "Actual Total Time": 4, "Actual Loops": 2, "Plans": [
                {"Node Type": "Result", "Actual Total Time": 0, "Actual Loops": 0}]}]}, "Execution Time": 10}]
            """).Plan!;

        PlanAnalysis.CriticalPath(plan).Should().Equal(0, 2);
    }

    [Fact]
    public void A_node_owns_its_buffers_and_io_less_what_the_nodes_below_it_did()
    {
        QueryPlan plan = Join();

        PlanNodeOwn? sort = PlanNodeOwn.Of(plan.Root);
        sort.Should().Be(new PlanNodeOwn(100, 0, 0, 0, 0, 0));

        PlanNode orders = plan.Nodes.Single(static x => x.RelationName == "orders");
        PlanNodeOwn.Of(orders).Should().Be(new PlanNodeOwn(1900, 350, 0, 0, 0, 4.0));

        PlanNode customers = plan.Nodes.Single(static x => x.RelationName == "customers");
        PlanNodeOwn.Of(customers).Should().Be(new PlanNodeOwn(600, 50, 0, 0, 0, 0.5));

        PlanNodeOwn.Of(plan.Nodes.Single(static x => x.NodeType == "Hash Join")).Should().Be(new PlanNodeOwn(0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public void A_node_that_printed_no_buffers_has_no_own_figures_and_a_total_below_its_children_is_clamped_at_zero()
    {
        QueryPlanReader.Read(ExplainFixtures.Get("14-nestloop-plain")).Plan!.Nodes.Should().OnlyContain(static x => PlanNodeOwn.Of(x) == null);

        QueryPlan odd = QueryPlanReader.Read("""
            [{"Plan": {"Node Type": "Limit", "Shared Hit Blocks": 5, "Shared Read Blocks": 0, "Temp Written Blocks": 0, "Plans": [
              {"Node Type": "Seq Scan", "Shared Hit Blocks": 9, "Shared Read Blocks": 0, "Temp Written Blocks": 0}]}}]
            """).Plan!;

        PlanNodeOwn.Of(odd.Root)!.SharedHit.Should().Be(0);
    }

    [Fact]
    public void Self_time_heat_is_each_nodes_share_of_the_slowest_in_quarters()
    {
        IReadOnlyDictionary<int, PlanHeatCell> heat = PlanAnalysis.HeatMap(Join(), PlanHeat.SelfTime);

        heat.Keys.Should().BeEquivalentTo([0, 1, 2, 3, 4]);
        heat[2].Should().Be(new PlanHeatCell(30, 1, 4, "30 ms of its own"), "the slowest node is as hot as it goes");
        heat[4].Level.Should().Be(2, "10 of 30 is a third, in the second quarter");
        heat[0].Level.Should().Be(2);
        heat[1].Level.Should().Be(1, "2 of 30 is under a quarter");
        heat.Values.Select(static x => x.Text).Should().OnlyContain(static x => x.EndsWith("of its own", StringComparison.Ordinal), "the value is always printed");
    }

    [Fact]
    public void Self_time_heat_of_an_untimed_plan_is_self_cost_and_says_so()
    {
        QueryPlan plan = QueryPlanReader.Read(ExplainFixtures.Get("17-nestloop-plain")).Plan!;

        IReadOnlyDictionary<int, PlanHeatCell> heat = PlanAnalysis.HeatMap(plan, PlanHeat.SelfTime);

        heat.Values.Should().OnlyContain(static x => x.Text.EndsWith("of its own cost", StringComparison.Ordinal));
        PlanAnalysis.Label(PlanHeat.SelfTime, plan).Should().Be("Self cost");
        PlanAnalysis.Label(PlanHeat.SelfTime, Join()).Should().Be("Self time");
        PlanAnalysis.Legend(plan, PlanHeat.SelfTime).Should().Contain("estimated cost");
    }

    [Fact]
    public void Misestimate_heat_is_the_ratio_on_a_log_scale_and_a_plain_plan_has_none()
    {
        QueryPlan plan = Join();
        IReadOnlyDictionary<int, PlanHeatCell> heat = PlanAnalysis.HeatMap(plan, PlanHeat.Misestimate);

        // The sort and the hash join were planned for 100 rows and returned 1,000: ×10, a third of a thousandfold.
        heat[0].Should().Be(new PlanHeatCell(10, 1.0 / 3, 2, "×10 off"));
        heat[1].Level.Should().Be(2);
        heat[2].Level.Should().Be(0, "an exact estimate is not hot");

        PlanAnalysis.HeatMap(QueryPlanReader.Read(ExplainFixtures.Get("17-nestloop-plain")).Plan!, PlanHeat.Misestimate).Should().BeEmpty();
    }

    [Fact]
    public void Buffers_read_heat_is_each_nodes_own_reads_and_temp_io_is_nothing_when_nothing_spilled()
    {
        QueryPlan plan = Join();

        IReadOnlyDictionary<int, PlanHeatCell> reads = PlanAnalysis.HeatMap(plan, PlanHeat.BuffersRead);
        reads[2].Should().Be(new PlanHeatCell(350, 1, 4, "350 blocks read"));
        reads[4].Level.Should().Be(1, "50 of 350");
        reads[0].Level.Should().Be(0);

        PlanAnalysis.HeatMap(plan, PlanHeat.TempIo).Values.Should().OnlyContain(static x => x.Level == 0, "nothing spilled");
        PlanAnalysis.HeatMap(plan, PlanHeat.None).Should().BeEmpty();

        QueryPlan spill = QueryPlanReader.Read(ExplainFixtures.Get("18-sort-spill-analyze")).Plan!;
        PlanAnalysis.HeatMap(spill, PlanHeat.TempIo).Values.Should().Contain(static x => x.Level == 4 && x.Text.Contains("temp blocks", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("none", "None")]
    [InlineData("self-time", "SelfTime")]
    [InlineData("misestimate", "Misestimate")]
    [InlineData("buffers-read", "BuffersRead")]
    [InlineData("temp-io", "TempIo")]
    [InlineData("something else", "None")]
    [InlineData(null, "None")]
    public void A_heats_key_round_trips(string? key, string name)
    {
        PlanHeat heat = Enum.Parse<PlanHeat>(name);

        PlanAnalysis.ParseHeat(key).Should().Be(heat);

        if (heat != PlanHeat.None || key == "none")
        {
            PlanAnalysis.Key(heat).Should().Be(key);
        }
    }

    [Fact]
    public void The_summary_has_every_number_Postgres_printed_and_blank_for_the_rest()
    {
        PlanSummary summary = PlanAnalysis.Summarize(Join());

        summary.Analyzed.Should().BeTrue();
        summary.PlanningMs.Should().Be(0.9);
        summary.ExecutionMs.Should().Be(59.5);
        summary.TriggersMs.Should().Be(1.25);
        summary.RowsReturned.Should().Be(1000);
        summary.SharedHit.Should().Be(2600);
        summary.SharedRead.Should().Be(400);
        summary.HitRatio.Should().BeApproximately(2600d / 3000, 1e-9);
        summary.TempWrittenBlocks.Should().Be(0);
        summary.JitMs.Should().BeNull();

        summary.Items.ToDictionary(static x => x.Label, static x => x.Value).Should().Contain(
        [
            new("Estimated cost", "2,110.00"),
            new("Planning time", "0.9 ms"),
            new("Execution time", "59.5 ms"),
            new("JIT", string.Empty),
            new("Triggers", "1.3 ms"),
            new("Rows returned", "1,000"),
            new("Shared hit", "2,600 blocks (20.3 MiB)"),
            new("Shared read", "400 blocks (3.1 MiB)"),
            new("Cache hit ratio", "87%"),
            new("Temp written", "0 blocks (0 B)"),
            new("WAL records", "0"),
            new("WAL bytes", "0 B"),
        ]);
    }

    [Fact]
    public void A_plain_plan_has_blanks_where_it_has_no_numbers_and_no_wal_or_memory_items()
    {
        PlanSummary summary = PlanAnalysis.Summarize(QueryPlanReader.Read(ExplainFixtures.Get("17-nestloop-plain")).Plan!);

        Dictionary<string, string> items = summary.Items.ToDictionary(static x => x.Label, static x => x.Value);

        items["Execution time"].Should().BeEmpty("a plain plan has no execution time, and \"0 ms\" would say it was fast");
        items["Rows returned"].Should().BeEmpty();
        items["Shared hit"].Should().BeEmpty();
        items["Cache hit ratio"].Should().BeEmpty();
        items["Estimated cost"].Should().NotBeEmpty();
        items.Keys.Should().NotContain(["WAL records", "WAL bytes", "Planning memory", "Serialization"]);
    }

    [Fact]
    public void JIT_is_summarised_with_its_share_of_the_execution()
    {
        PlanSummary summary = PlanAnalysis.Summarize(PlanFixtures.Plan("p12-fires"));

        summary.JitShare.Should().BeApproximately(45.2 / 118.9, 1e-9);
        summary.Items.Single(static x => x.Label == "JIT").Value.Should().Be("45.2 ms (38% of execution)");
    }

    [Fact]
    public void The_formatters_write_what_the_tree_writes()
    {
        PlanAnalysis.Milliseconds(0.0421).Should().Be("0.042 ms");
        PlanAnalysis.Milliseconds(18.24).Should().Be("18.2 ms");
        PlanAnalysis.Milliseconds(1500).Should().Be("1.5 s");
        PlanAnalysis.Ratio(73.14).Should().Be("×73.1");
        PlanAnalysis.Ratio(1234.6).Should().Be("×1,235");
        PlanAnalysis.Ratio(1).Should().Be("×1");
        PlanAnalysis.Cost(2110).Should().Be("2,110.00");
        PlanAnalysis.Blocks(1).Should().Be("1 block (8.0 KiB)");
    }
}
