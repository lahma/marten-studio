using MartenStudio.Services.Shapes;

namespace MartenStudio.Tests.Shapes;

/// <summary>Counts, the first example in key order, the caps, and the rename question (D-SHAPE S6).</summary>
public class ShapeAggregatorTests
{
    private static ShapeItemKey Key(string id) => new(id, null);

    private static ShapeMember Dropped(string path) => new(path, ShapeMemberKind.Dropped);

    private static ShapeMember Defaulted(string path) => new(path, ShapeMemberKind.Defaulted);

    [Fact]
    public void A_finding_counts_the_items_that_have_it_and_keeps_the_first_as_its_example()
    {
        ShapeAggregator aggregator = new();
        aggregator.Add("Order", "Shop.Order", Key("a"), null, [Dropped("$.x")]);
        aggregator.Add("Order", "Shop.Order", Key("b"), null, [Dropped("$.x"), Defaulted("$.y")]);
        aggregator.Add("Order", "Shop.Order", Key("c"), null, []);

        ShapeTypeResult order = aggregator.Build().Single();

        order.Examined.Should().Be(3);
        order.Members.Should().HaveCount(2);
        order.Members[0].Should().Match<ShapeMemberFinding>(x => x.Path == "$.x" && x.Count == 2 && x.Example.Id == "a");
        order.Members[1].Should().Match<ShapeMemberFinding>(x => x.Path == "$.y" && x.Count == 1 && x.Example.Id == "b");
    }

    [Fact]
    public void Failures_are_grouped_by_kind_exception_path_and_detail()
    {
        ShapeAggregator aggregator = new();
        ShapeFailure first = new(ShapeFailureKind.Unreadable, "JsonException", "$.total", System.Text.Json.JsonValueKind.String, null);
        ShapeFailure other = new(ShapeFailureKind.Unreadable, "JsonException", "$.date", null, null);

        aggregator.Add("Order", null, Key("a"), first, []);
        aggregator.Add("Order", null, Key("b"), first, []);
        aggregator.Add("Order", null, Key("c"), other, []);

        ShapeTypeResult order = aggregator.Build().Single();

        order.Examined.Should().Be(3);
        order.Failures.Select(static x => (x.Path, x.Count, x.Example.Id)).Should().Equal(("$.total", 2, "a"), ("$.date", 1, "c"));
    }

    [Fact]
    public void Findings_come_in_the_order_failures_then_dropped_to_defaulted_each_by_count_then_path()
    {
        ShapeAggregator aggregator = new();
        aggregator.Add("Order", null, Key("a"), null,
        [
            Defaulted("$.d"),
            new ShapeMember("$.r", ShapeMemberKind.Rewritten),
            Dropped("$.b"),
            Dropped("$.a"),
        ]);
        aggregator.Add("Order", null, Key("b"), null, [Dropped("$.b")]);

        aggregator.Build().Single().Members.Select(static x => x.Path).Should().Equal("$.b", "$.a", "$.r", "$.d");
    }

    [Fact]
    public void Findings_past_the_caps_are_counted_and_not_listed()
    {
        ShapeAggregator aggregator = new();
        List<ShapeMember> members = [.. Enumerable.Range(0, 60).Select(static i => Dropped("$.m" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)))];

        aggregator.Add("Order", null, Key("a"), null, members);

        for (int i = 0; i < 25; i++)
        {
            aggregator.Add("Order", null, Key("f" + i), new ShapeFailure(ShapeFailureKind.Unreadable, "JsonException", "$.p" + i, null, null), []);
        }

        ShapeTypeResult order = aggregator.Build().Single();

        order.Members.Should().HaveCount(ShapeCheckLimits.MaxMemberFindings);
        order.MoreMembers.Should().Be(10);
        order.Failures.Should().HaveCount(ShapeCheckLimits.MaxFailureGroups);
        order.MoreFailures.Should().Be(5);
    }

    [Fact]
    public void Types_with_a_problem_come_first_then_by_name()
    {
        ShapeAggregator aggregator = new();
        aggregator.Add("Apple", null, Key("a"), null, []);
        aggregator.Add("Zebra", null, Key("z"), null, [Dropped("$.x")]);
        aggregator.Add("Mango", null, Key("m"), null, []);

        aggregator.Build().Select(static x => x.TypeName).Should().Equal("Zebra", "Apple", "Mango");
    }

    [Fact]
    public void A_dropped_and_a_defaulted_sibling_of_about_the_same_count_raise_the_rename_question_and_only_within_ten_percent()
    {
        ShapeAggregator aggregator = new();

        for (int i = 0; i < 100; i++)
        {
            List<ShapeMember> members = [Dropped("$.customerName")];

            if (i < 95)
            {
                members.Add(Defaulted("$.customer"));
            }

            if (i < 50)
            {
                members.Add(Defaulted("$.other"));
            }

            aggregator.Add("Order", null, Key("i" + i), null, members);
        }

        ShapeAggregator.RenameQuestions(aggregator.Build().Single()).Should().Equal(("$.customerName", "$.customer"));
    }

    [Fact]
    public void Siblings_under_different_parents_are_not_a_rename()
    {
        ShapeAggregator aggregator = new();
        aggregator.Add("Order", null, Key("a"), null, [Dropped("$.a.name"), Defaulted("$.b.name")]);

        ShapeAggregator.RenameQuestions(aggregator.Build().Single()).Should().BeEmpty();
    }

    [Fact]
    public void Read_as_another_type_and_binary_are_counted_beside_the_examined_items()
    {
        ShapeAggregator aggregator = new();
        aggregator.Add("Order", null, Key("a"), null, []);
        aggregator.NoteReadAsOtherType("Order", null);
        aggregator.NoteBinary("Order", null);
        aggregator.NoteBinary("Order", null);

        ShapeTypeResult order = aggregator.Build().Single();

        (order.ReadAsOtherType, order.Binary).Should().Be((1, 2));
    }
}
