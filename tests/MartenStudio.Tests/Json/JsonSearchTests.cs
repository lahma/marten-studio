using MartenStudio.Services.Json;

namespace MartenStudio.Tests.Json;

public class JsonSearchTests
{
    private const string Sample = """
        {
          "city": "Helsinki",
          "address": { "city": "Tampere", "citation": "none" },
          "cities": [ "Espoo", "Vantaa" ]
        }
        """;

    [Fact]
    public void Search_matches_keys_and_values_and_ignores_case()
    {
        var model = JsonModelBuilder.Build(Sample);

        var matches = JsonSearch.Find(model, "cit");

        matches.Select(m => (model.Nodes[m.NodeId].Key, m.Target))
            .Should().Contain(("city", JsonMatchTarget.Key))
            .And.Contain(("citation", JsonMatchTarget.Key))
            .And.Contain(("cities", JsonMatchTarget.Key));
    }

    [Fact]
    public void A_value_match_records_where_in_the_text_it_starts()
    {
        var model = JsonModelBuilder.Build("""{"note":"a Helsinki day in Helsinki"}""");

        var matches = JsonSearch.Find(model, "helsinki");

        matches.Should().HaveCount(2);
        matches[0].Target.Should().Be(JsonMatchTarget.Value);
        matches[0].Start.Should().Be(2);
        matches[0].Length.Should().Be(8);
        matches[1].Start.Should().Be(18);
    }

    [Fact]
    public void Matches_come_back_in_document_order_not_in_array_order()
    {
        var model = JsonModelBuilder.Build("""{"outer":{"hit":1},"hit":2}""");

        var matches = JsonSearch.Find(model, "hit");

        matches.Should().HaveCount(2);
        model.Nodes[matches[0].NodeId].Path.Should().Be("outer.hit", "the nested one is read first");
        model.Nodes[matches[1].NodeId].Path.Should().Be("hit");
    }

    [Fact]
    public void A_container_never_matches_on_its_own_empty_display()
    {
        var model = JsonModelBuilder.Build("""{"a":{}}""");

        JsonSearch.Find(model, "a").Should().HaveCount(1, "only the key matches; the object has no value text");
    }

    [Fact]
    public void An_empty_term_matches_nothing()
    {
        var model = JsonModelBuilder.Build(Sample);

        JsonSearch.Find(model, string.Empty).Should().BeEmpty();
        JsonSearch.Find(model, null).Should().BeEmpty();
        JsonSearch.Find(JsonModel.Empty, "x").Should().BeEmpty();
    }

    [Fact]
    public void Ancestor_counts_say_how_many_rows_a_collapsed_container_is_hiding()
    {
        var model = JsonModelBuilder.Build(Sample);
        var matches = JsonSearch.Find(model, "cit");

        var counts = JsonSearch.CountByAncestor(model, matches);

        var address = model.Nodes.Single(n => n.Key == "address");
        counts[address.Id].Should().Be(2, "the address object hides the city and citation members");
        counts[0].Should().Be(4, "the root hides every match");
    }

    [Fact]
    public void A_node_with_two_hits_counts_once_towards_its_ancestors()
    {
        var model = JsonModelBuilder.Build("""{"a":{"note":"xx"}}""");
        var matches = JsonSearch.Find(model, "x");

        matches.Should().HaveCount(2);
        JsonSearch.CountByAncestor(model, matches)[0].Should().Be(1);
    }

    [Fact]
    public void Highlighting_splits_text_into_matched_and_unmatched_runs()
    {
        var runs = JsonSearch.Highlight("a Helsinki day", "helsinki");

        runs.Should().HaveCount(3);
        runs[0].Should().Be(new JsonTextRun("a ", false));
        runs[1].Should().Be(new JsonTextRun("Helsinki", true), "the original casing is kept, not the term's");
        runs[2].Should().Be(new JsonTextRun(" day", false));
    }

    [Fact]
    public void Highlighting_without_a_term_is_one_plain_run()
    {
        JsonSearch.Highlight("Helsinki", null).Should().ContainSingle()
            .Which.Should().Be(new JsonTextRun("Helsinki", false));

        JsonSearch.Highlight(string.Empty, "x").Should().BeEmpty();
    }
}
