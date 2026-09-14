using MartenStudio.Services.Json;

namespace MartenStudio.Tests.Json;

public class RoundTripDifferTests
{
    [Fact]
    public void A_member_the_clr_type_does_not_have_shows_up_as_dropped()
    {
        var diff = RoundTripDiffer.Diff(
            """{"name":"Ada","legacyFlag":true}""",
            """{"name":"Ada"}""");

        diff.Dropped.Should().ContainSingle();
        diff.Dropped[0].Path.Should().Be("$.legacyFlag");
        diff.Dropped[0].Before.Should().Be("true");
        diff.Dropped[0].After.Should().BeNull();
        diff.Changed.Should().BeEmpty();
        diff.Added.Should().BeEmpty();
    }

    [Fact]
    public void A_member_the_type_supplies_a_default_for_shows_up_as_added()
    {
        var diff = RoundTripDiffer.Diff(
            """{"name":"Ada"}""",
            """{"name":"Ada","revision":0}""");

        diff.Added.Should().ContainSingle();
        diff.Added[0].Path.Should().Be("$.revision");
        diff.Added[0].Before.Should().BeNull();
        diff.Added[0].After.Should().Be("0");
    }

    [Fact]
    public void A_value_that_survives_but_differs_is_a_change_with_both_sides()
    {
        var diff = RoundTripDiffer.Diff(
            """{"score":12.5}""",
            """{"score":12}""");

        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Path.Should().Be("$.score");
        diff.Changed[0].Before.Should().Be("12.5");
        diff.Changed[0].After.Should().Be("12");
    }

    [Fact]
    public void Nesting_is_reported_by_path_not_by_replacing_the_whole_object()
    {
        var diff = RoundTripDiffer.Diff(
            """{"address":{"city":"Helsinki","postcode":"00100"}}""",
            """{"address":{"city":"Tampere"}}""");

        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Path.Should().Be("$.address.city");
        diff.Dropped.Should().ContainSingle();
        diff.Dropped[0].Path.Should().Be("$.address.postcode");
    }

    [Fact]
    public void Arrays_diff_by_position_and_report_their_tails()
    {
        var diff = RoundTripDiffer.Diff(
            """{"tags":["a","b","c"]}""",
            """{"tags":["a","z"]}""");

        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Path.Should().Be("$.tags[1]");
        diff.Dropped.Should().ContainSingle();
        diff.Dropped[0].Path.Should().Be("$.tags[2]");
        diff.Dropped[0].Before.Should().Be("\"c\"");
    }

    [Fact]
    public void An_element_appended_by_the_round_trip_is_added()
    {
        var diff = RoundTripDiffer.Diff("""{"tags":["a"]}""", """{"tags":["a","b"]}""");

        diff.Added.Should().ContainSingle();
        diff.Added[0].Path.Should().Be("$.tags[1]");
        diff.Added[0].After.Should().Be("\"b\"");
    }

    [Fact]
    public void A_member_that_only_moved_or_only_changed_spelling_is_not_a_difference()
    {
        RoundTripDiffer.Diff("""{"a":1,"b":2}""", """{"b":2,"a":1}""").IsEmpty.Should().BeTrue();
        RoundTripDiffer.Diff("""{"a":1.0}""", """{"a":1}""").IsEmpty.Should().BeTrue();
        RoundTripDiffer.Diff("""{ "a" : 1 }""", """{"a":1}""").IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_member_that_became_null_is_a_change_and_not_a_drop()
    {
        var diff = RoundTripDiffer.Diff("""{"a":1}""", """{"a":null}""");

        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Before.Should().Be("1");
        diff.Changed[0].After.Should().Be("null");
        diff.Dropped.Should().BeEmpty();
    }

    [Fact]
    public void A_kind_change_is_one_change_rather_than_a_drop_and_an_add()
    {
        var diff = RoundTripDiffer.Diff("""{"a":{"b":1}}""", """{"a":"gone"}""");

        diff.Count.Should().Be(1);
        diff.Changed[0].Path.Should().Be("$.a");
    }

    [Fact]
    public void The_summary_is_the_sentence_the_dialog_leads_with()
    {
        RoundTripDiffer
            .Diff("""{"a":1,"b":2,"c":3,"d":4}""", """{"d":4}""")
            .Summary()
            .Should().Be("3 properties will be dropped.");

        RoundTripDiffer
            .Diff("""{"a":1}""", """{"a":2}""")
            .Summary()
            .Should().Be("1 value will change.");

        RoundTripDiffer
            .Diff("""{"a":1,"b":2}""", """{"a":2,"c":3}""")
            .Summary()
            .Should().Be("1 property will be dropped, 1 value will change, 1 property will be added.");

        JsonDiffResult.Empty.Summary().Should().Be("Nothing will change.");
    }

    [Fact]
    public void Invalid_json_on_either_side_is_no_difference_rather_than_an_exception()
    {
        RoundTripDiffer.Diff("{ nope", """{"a":1}""").IsEmpty.Should().BeTrue();
        RoundTripDiffer.Diff("""{"a":1}""", "{ nope").IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void The_same_call_answers_what_the_user_changed()
    {
        var diff = RoundTripDiffer.Diff(
            """{"name":"Ada","city":"Helsinki"}""",
            """{"name":"Grace","city":"Helsinki"}""");

        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Path.Should().Be("$.name");
        diff.Changed[0].Before.Should().Be("\"Ada\"");
        diff.Changed[0].After.Should().Be("\"Grace\"");
    }
}
