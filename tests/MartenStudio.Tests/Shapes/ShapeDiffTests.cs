using System.Text.Json;

using MartenStudio.Services.Shapes;

namespace MartenStudio.Tests.Shapes;

/// <summary>
/// The classifier of the shape check (D-SHAPE S6): each kind, nested objects and arrays, and the one property that matters most -
/// nothing it returns can carry a value.
/// </summary>
public class ShapeDiffTests
{
    private const string Sentinel = "SENTINEL-7f3a";

    private static IReadOnlyList<ShapeMember> Diff(string stored, string read) => ShapeDiff.Compare(stored, read);

    [Fact]
    public void A_stored_member_the_round_trip_lacks_is_dropped()
    {
        Diff("""{"a":1,"b":2}""", """{"a":1}""").Should().Equal(new ShapeMember("$.b", ShapeMemberKind.Dropped));
    }

    [Fact]
    public void A_stored_value_the_round_trip_writes_as_null_is_nulled()
    {
        Diff("""{"a":1}""", """{"a":null}""").Should().Equal(new ShapeMember("$.a", ShapeMemberKind.Nulled));
    }

    [Fact]
    public void A_value_of_another_json_kind_is_retyped_and_names_both_kinds()
    {
        Diff("""{"a":"5"}""", """{"a":5}""").Should().Equal(
            new ShapeMember("$.a", ShapeMemberKind.Retyped, null, JsonValueKind.String, JsonValueKind.Number));
    }

    [Fact]
    public void A_scalar_that_comes_back_different_is_rewritten()
    {
        Diff("""{"a":"x"}""", """{"a":"y"}""").Should().Equal(new ShapeMember("$.a", ShapeMemberKind.Rewritten));
    }

    [Fact]
    public void A_member_the_stored_item_lacks_is_defaulted()
    {
        Diff("""{"a":1}""", """{"a":1,"b":0}""").Should().Equal(new ShapeMember("$.b", ShapeMemberKind.Defaulted));
    }

    [Fact]
    public void A_stored_null_where_the_round_trip_writes_a_value_is_defaulted_because_the_item_has_nothing_there()
    {
        Diff("""{"a":null}""", """{"a":0}""").Should().Equal(new ShapeMember("$.a", ShapeMemberKind.Defaulted));
    }

    [Fact]
    public void A_stored_null_the_round_trip_omits_is_not_a_loss_and_is_not_reported()
    {
        Diff("""{"a":1,"b":null}""", """{"a":1}""").Should().BeEmpty();
    }

    [Fact]
    public void A_number_written_another_way_is_no_change()
    {
        Diff("""{"a":1.50,"b":10}""", """{"a":1.5,"b":1e1}""").Should().BeEmpty();
    }

    [Fact]
    public void The_same_value_under_another_spelling_is_one_respelled_finding_and_not_a_drop_and_a_default()
    {
        Diff("""{"CustomerName":"x"}""", """{"customerName":"x"}""").Should().Equal(
            new ShapeMember("$.CustomerName", ShapeMemberKind.Respelled, "$.customerName"));
    }

    [Fact]
    public void A_respelled_member_needs_equal_values_or_it_is_a_drop_and_a_default()
    {
        Diff("""{"CustomerName":"x"}""", """{"customerName":"y"}""").Should().BeEquivalentTo(
        [
            new ShapeMember("$.CustomerName", ShapeMemberKind.Dropped),
            new ShapeMember("$.customerName", ShapeMemberKind.Defaulted),
        ]);
    }

    [Fact]
    public void A_nested_member_is_reported_at_its_path()
    {
        Diff("""{"address":{"city":"a","zip":"1"}}""", """{"address":{"city":"a"}}""").Should().Equal(
            new ShapeMember("$.address.zip", ShapeMemberKind.Dropped));
    }

    [Fact]
    public void An_array_index_is_a_star_and_an_item_counts_a_path_once_however_many_elements_show_it()
    {
        Diff(
                """{"items":[{"a":1,"x":2},{"a":1,"x":3},{"a":1,"x":4}]}""",
                """{"items":[{"a":1},{"a":1},{"a":1}]}""")
            .Should().Equal(new ShapeMember("$.items[*].x", ShapeMemberKind.Dropped));
    }

    [Fact]
    public void A_shrinking_array_drops_its_elements_and_a_growing_one_defaults_them()
    {
        Diff("""{"items":[1,2,3]}""", """{"items":[1]}""").Should().Equal(new ShapeMember("$.items[*]", ShapeMemberKind.Dropped));
        Diff("""{"items":[1]}""", """{"items":[1,2,3]}""").Should().Equal(new ShapeMember("$.items[*]", ShapeMemberKind.Defaulted));
    }

    [Fact]
    public void An_object_where_an_array_was_stored_is_retyped()
    {
        Diff("""{"a":[1]}""", """{"a":{"x":1}}""").Should().Equal(
            new ShapeMember("$.a", ShapeMemberKind.Retyped, null, JsonValueKind.Array, JsonValueKind.Object));
    }

    [Fact]
    public void A_path_is_cut_at_the_bound()
    {
        string key = new('k', 400);

        ShapeMember member = Diff("{\"" + key + "\":1}", "{}").Single();

        member.Path.Length.Should().Be(ShapeCheckLimits.MaxPathLength);
    }

    [Fact]
    public void A_sentinel_stored_in_every_kind_of_finding_appears_in_no_string_of_the_result()
    {
        // Stored values for a drop, a retype, a rewrite, a respelling and a null, all the sentinel.
        string stored = $$"""
            {"gone":"{{Sentinel}}","kind":"{{Sentinel}}","same":"{{Sentinel}}","Old":"{{Sentinel}}","nulled":"{{Sentinel}}"}
            """;
        string read = """{"kind":5,"same":"other","old":"SENTINEL-7f3a","nulled":null}""".Replace("SENTINEL-7f3a", Sentinel, StringComparison.Ordinal);

        IReadOnlyList<ShapeMember> members = Diff(stored, read);

        members.Should().NotBeEmpty();
        ShapeSentinel.Strings(members).Should().NotContain(x => x.Contains(Sentinel, StringComparison.Ordinal));
    }
}
