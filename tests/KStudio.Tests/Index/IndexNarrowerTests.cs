using KStudio.Core.Index;
using KStudio.Core.Windows;

namespace KStudio.Tests.Index;

/// <summary>
/// The pure narrowing of a plan by a host's hints (AGENTS.md D48, D-M5 decision 5.3): what is dropped, the union and intersection per partition, the
/// cap of 64, and the sentences. A hint only narrows; nothing here can add an offset the plan did not have.
/// </summary>
public class IndexNarrowerTests
{
    private static readonly string[] Orders = ["orders"];

    private static PartitionRange Range(string topic, int partition, long start, long end) =>
        new(topic, partition, start, end, 0, end, null, []);

    private static PartitionRange[] Plan(params PartitionRange[] ranges) => ranges;

    private static KafkaIndexAnswer Answer(params KafkaIndexHint[] hints) => KafkaIndexAnswer.Hints(hints);

    [Fact]
    public void One_hint_narrows_its_partition_and_leaves_the_others_as_empty_ranges()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 0, 100), Range("orders", 1, 0, 100));

        (IReadOnlyList<PartitionRange> ranges, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(plan, Answer(new KafkaIndexHint("orders", 1, 40, 45)), Orders);

        ranges.Should().HaveCount(2);
        ranges[0].IsEmpty.Should().BeTrue("a partition no hint reaches is read as nothing");
        ranges[1].Start.Should().Be(40);
        ranges[1].End.Should().Be(45);
        narrowing.IsNarrowed.Should().BeTrue();
        narrowing.OffsetsRead.Should().Be(5);
        narrowing.OffsetsInWindow.Should().Be(200);
        narrowing.Ranges.Should().ContainSingle().Which.Should().Be(new KafkaIndexRange("orders", 1, 40, 45, 0, 100));
        narrowing.HintsUsed.Should().Be(1);
    }

    [Fact]
    public void Hints_of_one_partition_become_one_range_from_the_least_start_to_the_greatest_end()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 0, 1_000));

        (IReadOnlyList<PartitionRange> ranges, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(
            plan,
            Answer(new KafkaIndexHint("orders", 0, 300, 310), new KafkaIndexHint("orders", 0, 20, 25), new KafkaIndexHint("orders", 0, 100, 101)),
            Orders);

        ranges.Should().ContainSingle().Which.Should().Match<PartitionRange>(static x => x.Start == 20 && x.End == 310);
        narrowing.HintsUsed.Should().Be(3);
        narrowing.OffsetsRead.Should().Be(290);
    }

    [Fact]
    public void A_range_is_intersected_with_the_plans_own_and_never_reaches_outside_it()
    {
        // The plan's range here is a window's: offsets 50 to 80 of a partition that holds more.
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 50, 80));

        (IReadOnlyList<PartitionRange> ranges, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(plan, Answer(new KafkaIndexHint("orders", 0, 10, 200)), Orders);

        ranges.Should().ContainSingle().Which.Should().Match<PartitionRange>(static x => x.Start == 50 && x.End == 80);
        narrowing.OffsetsRead.Should().Be(30);
        narrowing.OffsetsInWindow.Should().Be(30);
    }

    [Fact]
    public void A_hint_wholly_outside_the_plans_range_is_dropped_and_the_whole_window_is_read()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 50, 80));

        (IReadOnlyList<PartitionRange> ranges, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(plan, Answer(new KafkaIndexHint("orders", 0, 100, 120)), Orders);

        ranges.Should().BeSameAs(plan, "the plan is returned as it was");
        narrowing.Outcome.Should().Be(KafkaIndexOutcome.PointedNowhere);
        narrowing.Note.Should().Be("The host's record index pointed nowhere in this window; the whole window is read.");
        narrowing.IsNarrowed.Should().BeFalse();
        narrowing.AuditSuffix.Should().BeEmpty();
    }

    [Fact]
    public void An_answer_of_none_points_nowhere()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 0, 10));

        (IReadOnlyList<PartitionRange> ranges, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(plan, KafkaIndexAnswer.None, Orders);

        ranges.Should().BeSameAs(plan);
        narrowing.Outcome.Should().Be(KafkaIndexOutcome.PointedNowhere);
    }

    [Fact]
    public void A_hint_about_a_topic_the_search_did_not_name_is_dropped_before_anything_counts_it()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 0, 100), Range("secret", 0, 0, 100));

        (IReadOnlyList<PartitionRange> ranges, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(
            plan,
            Answer(new KafkaIndexHint("secret", 0, 10, 20), new KafkaIndexHint("orders", 0, 5, 6)),
            Orders);

        narrowing.Ranges.Should().ContainSingle().Which.Topic.Should().Be("orders");
        narrowing.HintsUsed.Should().Be(1, "the hint about the other topic was never a hint of this search");
        narrowing.HintsNotUsed.Should().Be(0);
        ranges.Single(static x => x.Topic == "secret").IsEmpty.Should().BeTrue("the topic the search did not name is not read through a hint");
        narrowing.Caveat.Should().NotContain("secret");
        narrowing.AuditSuffix.Should().NotContain("secret");
        narrowing.Note.Should().NotContain("secret");
    }

    [Fact]
    public void A_hint_about_nothing_but_another_topic_is_a_pointer_to_nowhere_that_names_nothing()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 0, 100), Range("secret", 0, 0, 100));

        (_, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(plan, Answer(new KafkaIndexHint("secret", 0, 10, 20)), Orders);

        narrowing.Outcome.Should().Be(KafkaIndexOutcome.PointedNowhere);
        narrowing.Note.Should().NotContain("secret");
    }

    [Fact]
    public void A_hint_about_a_partition_the_plan_does_not_read_or_with_an_empty_or_reversed_range_is_dropped()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 0, 100), Range("orders", 1, 40, 40));

        (_, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(
            plan,
            Answer(
                new KafkaIndexHint("orders", 7, 0, 10),
                new KafkaIndexHint("orders", 1, 40, 41),
                new KafkaIndexHint("orders", 0, 30, 30),
                new KafkaIndexHint("orders", 0, 31, 30)),
            Orders);

        narrowing.Outcome.Should().Be(KafkaIndexOutcome.PointedNowhere, "a partition not in the plan, an empty range there, and an empty or reversed hint all name nothing");
    }

    [Fact]
    public void At_most_64_hints_are_used_and_the_rest_are_counted_and_said()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 0, 10_000));
        KafkaIndexHint[] hints = [.. Enumerable.Range(0, 100).Select(static x => new KafkaIndexHint("orders", 0, x * 10, (x * 10) + 1))];

        (IReadOnlyList<PartitionRange> ranges, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(plan, KafkaIndexAnswer.Hints(hints), Orders);

        narrowing.HintsUsed.Should().Be(64);
        narrowing.HintsNotUsed.Should().Be(36);
        ranges.Single().End.Should().Be((63 * 10) + 1, "only the first 64 hints make the range");
        narrowing.Caveat.Should().Contain("36 further hints were not used.");
    }

    [Fact]
    public void A_hint_about_another_topic_does_not_use_up_the_cap()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders", 0, 0, 10_000), Range("secret", 0, 0, 10_000));
        List<KafkaIndexHint> hints = [.. Enumerable.Range(0, 80).Select(static x => new KafkaIndexHint("secret", 0, x, x + 1))];
        hints.Add(new KafkaIndexHint("orders", 0, 500, 501));

        (_, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(plan, KafkaIndexAnswer.Hints(hints), Orders);

        narrowing.IsNarrowed.Should().BeTrue();
        narrowing.HintsUsed.Should().Be(1);
        narrowing.HintsNotUsed.Should().Be(0);
    }

    [Fact]
    public void The_sentences_are_the_designs_word_for_word()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("orders.v1", 0, 0, 1_000), Range("orders.v1", 1, 0, 1_000));

        (_, KafkaIndexNarrowing narrowing) = IndexNarrower.Narrow(
            plan,
            Answer(new KafkaIndexHint("orders.v1", 0, 10, 40), new KafkaIndexHint("orders.v1", 1, 100, 130)),
            ["orders.v1"]);

        narrowing.Caveat.Should().Be("Read only where the host's record index pointed (60 of 2,000 offsets); other matches in the window were not looked for.");
        narrowing.NoMatch.Should().Be("No match in the 2 ranges the host's record index named (60 offsets). The rest of the window was not read.");
        narrowing.AuditSuffix.Should().Be("; narrowed by the host's record index to 2 ranges of orders.v1 (60 offsets)");
        narrowing.Note.Should().Be("The host's record index narrowed this search to 2 ranges (60 of 2,000 offsets of the window).");
    }

    [Fact]
    public void One_range_reads_in_the_singular_and_the_audit_names_every_topic_it_narrowed()
    {
        IReadOnlyList<PartitionRange> plan = Plan(Range("a", 0, 0, 100), Range("b", 0, 0, 100));

        (_, KafkaIndexNarrowing one) = IndexNarrower.Narrow(plan, Answer(new KafkaIndexHint("a", 0, 5, 6)), ["a", "b"]);
        (_, KafkaIndexNarrowing two) = IndexNarrower.Narrow(plan, Answer(new KafkaIndexHint("a", 0, 5, 6), new KafkaIndexHint("b", 0, 7, 9)), ["a", "b"]);

        one.NoMatch.Should().Be("No match in the range the host's record index named (1 offset). The rest of the window was not read.");
        one.AuditSuffix.Should().Be("; narrowed by the host's record index to 1 range of a (1 offset)");
        two.AuditSuffix.Should().Be("; narrowed by the host's record index to 2 ranges of a and b (3 offsets)");
    }

    [Fact]
    public void The_notes_of_an_index_that_did_not_answer_and_of_one_that_narrowed_nothing_are_the_designs()
    {
        KafkaIndexNarrowing.DidNotAnswer(10).Note.Should().Be("The host's record index did not answer; the whole window is read.");
        KafkaIndexNarrowing.DidNotAnswer(10).IsNarrowed.Should().BeFalse();
        KafkaIndexNarrowing.DidNotAnswer(10).Caveat.Should().BeNull("an index that did not answer left the whole window read, so there is nothing a negative must not claim");
        KafkaIndexNarrowing.PointedNowhere(10).NoMatch.Should().BeNull();
    }

    [Fact]
    public void An_answer_built_from_nothing_is_none_and_a_null_hint_is_ignored()
    {
        KafkaIndexAnswer.Hints([]).Should().BeSameAs(KafkaIndexAnswer.None);
        KafkaIndexAnswer.Hints([null!]).Should().BeSameAs(KafkaIndexAnswer.None);
        KafkaIndexAnswer.Hints([new KafkaIndexHint("t", 0, 1, 2), null!]).Ranges.Should().ContainSingle();
    }
}
