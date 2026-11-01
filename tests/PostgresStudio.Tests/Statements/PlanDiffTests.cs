using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 0.10-B (issue #28): two plans lined up node by node - by path, then by a longest common subsequence - on the corpus's pairs, and
/// P21, the one insight that judges a pair. Pure: no database.
/// </summary>
public class PlanDiffTests
{
    [Fact]
    public void A_plan_against_itself_is_unchanged_throughout()
    {
        QueryPlan plan = PlanFixtures.Plan("analyzed-join");

        PlanComparison comparison = PlanDiff.Compare(plan, plan);

        comparison.Entries.Should().HaveCount(plan.NodeCount).And.OnlyContain(static x => x.Kind == PlanDiffKind.Unchanged);
        comparison.Differences.Should().BeEmpty();
        comparison.Partial.Should().BeFalse();
        comparison.CostRatio.Should().Be(1);
        comparison.TimeRatio.Should().Be(1);
        comparison.Entries.Select(static x => x.Path).Should().Contain(["0", "0.0", "0.0.0"]);
    }

    [Fact]
    public void A_node_whose_kind_changed_is_one_pair_with_its_cost_and_row_estimate_moves()
    {
        PlanComparison comparison = PlanDiff.Compare(PlanFixtures.Pair("seq-before"), PlanFixtures.Pair("seq-after"));

        PlanDiffEntry entry = comparison.Entries.Should().ContainSingle().Subject;

        entry.Kind.Should().Be(PlanDiffKind.Changed);
        entry.TypeChanged.Should().BeTrue();
        entry.TargetChanged.Should().BeFalse("it reads orders both times");
        entry.CostDelta.Should().BeApproximately(8.44 - 1500, 1e-9);
        entry.RowsChanged.Should().BeTrue();
        entry.TimeDelta.Should().BeNull("neither plan was analyzed");
        comparison.CostRatio.Should().BeApproximately(8.44 / 1500, 1e-9);
    }

    [Fact]
    public void A_subtree_wrapped_in_a_new_node_keeps_its_scan_and_adds_only_the_wrapper()
    {
        PlanComparison comparison = PlanDiff.Compare(PlanFixtures.Pair("wrap-before"), PlanFixtures.Pair("wrap-after"));

        comparison.Count(PlanDiffKind.Added).Should().Be(1);
        comparison.Entries.Single(static x => x.Kind == PlanDiffKind.Added).After!.NodeType.Should().Be("Limit");
        comparison.Count(PlanDiffKind.Removed).Should().Be(0);

        PlanDiffEntry scan = comparison.Entries.Single(static x => x.After?.NodeType == "Seq Scan");
        scan.Before.Should().NotBeNull("the second pass found the scan again under the new node");
        scan.Kind.Should().Be(PlanDiffKind.Unchanged);
        scan.Path.Should().Be("0.0.0");
    }

    [Fact]
    public void Children_that_swapped_places_are_found_by_the_sequence_and_neither_added_nor_removed()
    {
        PlanComparison comparison = PlanDiff.Compare(PlanFixtures.Pair("swap-before"), PlanFixtures.Pair("swap-after"));

        comparison.Count(PlanDiffKind.Added).Should().Be(0);
        comparison.Count(PlanDiffKind.Removed).Should().Be(0);

        foreach (PlanDiffEntry entry in comparison.Entries.Where(static x => x.After?.NodeType == "Seq Scan"))
        {
            entry.Before!.RelationName.Should().Be(entry.After!.RelationName, "a scan is paired with the same relation's scan, not with the one at its old index");
            entry.TargetChanged.Should().BeFalse();
        }
    }

    [Fact]
    public void A_subtree_only_one_plan_has_is_added_or_removed_and_never_paired_with_a_stranger()
    {
        QueryPlan join = PlanFixtures.Plan("analyzed-join");
        QueryPlan one = PlanFixtures.Pair("seq-before");

        PlanComparison shrinking = PlanDiff.Compare(join, one);
        PlanComparison growing = PlanDiff.Compare(one, join);

        // The Sort at the top of one plan is not the Seq Scan at the top of the other: the roots do not pair. The scan finds its own
        // twin, the join's scan of orders, and every other node of the join is the one plan's alone.
        PlanDiffEntry kept = shrinking.Entries.Single(static x => x.After is not null);
        kept.Before.Should().NotBeNull();
        kept.Before!.NodeType.Should().Be("Seq Scan");
        kept.Before.RelationName.Should().Be("orders");
        kept.After!.RelationName.Should().Be("orders");
        shrinking.Entries.Where(static x => x.Kind == PlanDiffKind.Removed).Select(static x => x.Before!.NodeType)
            .Should().Contain(["Sort", "Hash Join"]);
        shrinking.Count(PlanDiffKind.Removed).Should().Be(join.NodeCount - 1);

        growing.Count(PlanDiffKind.Added).Should().Be(join.NodeCount - 1);
        growing.Count(PlanDiffKind.Removed).Should().Be(0);
        growing.Entries.Single(static x => x.Before is not null).After!.RelationName.Should().Be("orders");
        growing.Entries.Where(static x => x.Kind == PlanDiffKind.Added).Select(static x => x.After!.NodeType).Should().Contain("Sort");
    }

    [Fact]
    public void A_scan_that_gained_a_Gather_above_it_is_one_changed_pair_and_the_Gather_is_added()
    {
        PlanComparison comparison = PlanDiff.Compare(PlanFixtures.Pair("seq-before"), PlanFixtures.Pair("gather-after"));

        PlanDiffEntry gather = comparison.Entries.Single(static x => x.Kind == PlanDiffKind.Added);
        gather.After!.NodeType.Should().Be("Gather");
        gather.Path.Should().Be("0");

        PlanDiffEntry scan = comparison.Entries.Single(static x => x.After!.NodeType == "Seq Scan");
        scan.Before!.NodeType.Should().Be("Seq Scan");
        scan.Before.Label.Should().Be("Seq Scan");
        scan.After!.Label.Should().Be("Parallel Seq Scan");
        scan.Kind.Should().Be(PlanDiffKind.Changed);
        scan.TypeChanged.Should().BeTrue("Seq Scan became a Parallel Seq Scan");
        scan.Path.Should().Be("0.0");
        comparison.Count(PlanDiffKind.Removed).Should().Be(0);
    }

    [Fact]
    public void A_scan_that_became_another_kind_of_scan_under_a_new_node_is_still_one_pair_because_it_reads_the_same_table()
    {
        PlanComparison comparison = PlanDiff.Compare(PlanFixtures.Pair("seq-before"), PlanFixtures.Pair("gather-index-after"));

        comparison.Count(PlanDiffKind.Removed).Should().Be(0, "the Seq Scan on orders is paired with the Index Scan on orders");
        PlanDiffEntry scan = comparison.Entries.Single(static x => x.After!.NodeType == "Index Scan");
        scan.Before!.NodeType.Should().Be("Seq Scan");
        scan.Kind.Should().Be(PlanDiffKind.Changed);
        scan.TypeChanged.Should().BeTrue();
        comparison.Entries.Single(static x => x.Kind == PlanDiffKind.Added).After!.NodeType.Should().Be("Gather");
    }

    [Fact]
    public void Analyzed_plans_carry_time_deltas_on_the_nodes_both_have()
    {
        QueryPlan plan = PlanFixtures.Plan("analyzed-join");

        PlanComparison comparison = PlanDiff.Compare(plan, plan);

        comparison.Entries.Where(static x => x.Before?.InclusiveMs is not null).Should().OnlyContain(static x => x.TimeDelta == 0);
        comparison.ExecutionMsBefore.Should().Be(comparison.ExecutionMsAfter).And.NotBeNull();
    }

    [Fact]
    public void A_plan_far_larger_than_the_reader_reads_is_compared_as_far_as_it_was_read_and_says_so()
    {
        QueryPlan huge = Wide(PlanDiff.MaxNodes + 500);
        QueryPlan small = Wide(10);

        PlanComparison comparison = PlanDiff.Compare(huge, small);

        comparison.Partial.Should().BeTrue();
        comparison.Entries.Count.Should().BeLessThanOrEqualTo(2 * PlanDiff.MaxNodes, "no more than the cap is read of either plan");
        comparison.Entries.Count(static x => x.After is not null).Should().Be(small.NodeCount);
    }

    [Fact]
    public void Every_pair_of_corpus_plans_compares_without_throwing_and_accounts_for_every_node()
    {
        foreach (string left in PlanFixtures.Names)
        {
            foreach (string right in PlanFixtures.Names)
            {
                QueryPlan before = PlanFixtures.Plan(left);
                QueryPlan after = PlanFixtures.Plan(right);

                PlanComparison comparison = PlanDiff.Compare(before, after);

                comparison.Entries.Count(static x => x.After is not null).Should().Be(after.NodeCount, "{0} against {1}: every After node has a line", left, right);
                comparison.Entries.Count(static x => x.Before is not null).Should().Be(before.NodeCount, "{0} against {1}: every Before node is paired or removed", left, right);
            }
        }
    }

    // ---- P21 -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void P21_fires_when_the_generic_plan_costs_ten_times_the_custom_plan_or_more()
    {
        PlanInsightFinding finding = PlanInsights.GenericVersusCustom(PlanFixtures.Pair("generic-fires"), PlanFixtures.Pair("custom")).Should().NotBeNull().And.Subject.As<PlanInsightFinding>();

        finding.Insight.Code.Should().Be("P21");
        finding.Message.Should().Contain("20,000.00").And.Contain("8.44").And.Contain("2370 times");
        finding.Remedy.Should().Contain("force_custom_plan");
        finding.NodeId.Should().Be(0);
    }

    [Fact]
    public void P21_is_quiet_when_the_generic_plan_is_not_ten_times_dearer_or_the_pair_is_the_other_way_round()
    {
        PlanInsights.GenericVersusCustom(PlanFixtures.Pair("generic-quiet"), PlanFixtures.Pair("custom")).Should().BeNull("60 against 8.44 is seven times");
        PlanInsights.GenericVersusCustom(PlanFixtures.Pair("custom"), PlanFixtures.Pair("generic-fires")).Should().BeNull();
        PlanInsights.GenericVersusCustom(PlanFixtures.Pair("custom"), PlanFixtures.Pair("custom")).Should().BeNull();
    }

    [Fact]
    public void P21_judges_a_pair_and_so_is_never_part_of_the_evaluation_of_one_plan_or_its_not_judged_lists()
    {
        PlanInsights.GenericWorseThanCustom.ComparesPlans.Should().BeTrue();
        PlanInsights.All.Single(static x => x.Code == "P21").Should().BeSameAs(PlanInsights.GenericWorseThanCustom);
        PlanInsights.All.Where(static x => x.ComparesPlans).Select(static x => x.Code).Should().Equal("P21");

        foreach (string name in new[] { "generic-fires", "generic-quiet", "custom", "seq-before" })
        {
            QueryPlan plan = PlanFixtures.Pair(name);

            PlanInsights.Evaluate(plan, StudioText.Marten).Should().NotContain(static x => x.Insight.Code == "P21");
            PlanInsights.NotJudged(plan).Should().NotContain(static x => x.Code == "P21");
            PlanInsights.NotJudgedWithoutFacts(plan, null).Should().NotContain(static x => x.Code == "P21");
        }
    }

    /// <summary>An <c>Append</c> of <paramref name="children" /> scans, built by hand: a plan the reader would not make past its cap.</summary>
    private static QueryPlan Wide(int children)
    {
        PlanNode[] scans =
        [
            .. Enumerable.Range(1, children).Select(static i => new PlanNode
            {
                Id = i,
                Depth = 1,
                NodeType = "Seq Scan",
                Label = "Seq Scan",
                RelationName = "t" + i,
                TotalCost = i,
                PlanRows = i,
            }),
        ];

        PlanNode root = new() { Id = 0, Depth = 0, NodeType = "Append", Label = "Append", TotalCost = children, Children = scans };

        return new QueryPlan(root, children + 1, false, 1);
    }
}
