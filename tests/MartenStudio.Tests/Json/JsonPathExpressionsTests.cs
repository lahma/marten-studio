using System.Text.Json;
using System.Text.Json.Serialization;
using MartenStudio.Services.Json;

namespace MartenStudio.Tests.Json;

public class JsonPathExpressionsTests
{
    private static readonly JsonPathSegment[] AddressCity =
    [
        JsonPathSegment.ForKey("address"),
        JsonPathSegment.ForKey("city"),
    ];

    private static readonly JsonPathSegment[] FirstItemSku =
    [
        JsonPathSegment.ForKey("items"),
        JsonPathSegment.ForIndex(0),
        JsonPathSegment.ForKey("sku"),
    ];

    private static readonly JsonPathSegment[] HyphenatedKey =
    [
        JsonPathSegment.ForKey("first-name"),
    ];

    [Fact]
    public void JsonPath_uses_dots_for_simple_keys_and_brackets_for_everything_else()
    {
        JsonPathExpressions.ToJsonPath([]).Should().Be("$");
        JsonPathExpressions.ToJsonPath(AddressCity).Should().Be("$.address.city");
        JsonPathExpressions.ToJsonPath(FirstItemSku).Should().Be("$.items[0].sku");
        JsonPathExpressions.ToJsonPath(HyphenatedKey).Should().Be("$['first-name']");
    }

    [Fact]
    public void The_dotted_path_is_the_one_the_clr_resolver_reads_back()
    {
        JsonPathExpressions.ToDotPath(AddressCity).Should().Be("address.city");
        JsonPathExpressions.ToDotPath(FirstItemSku).Should().Be("items[0].sku");
        JsonPathExpressions.ToDotPath(HyphenatedKey).Should().Be("[\"first-name\"]");
    }

    [Fact]
    public void The_postgres_expression_ends_in_a_text_arrow_only_when_the_value_is_read_as_text()
    {
        JsonPathExpressions.ToPostgresExpression(AddressCity, asText: true)
            .Should().Be("data -> 'address' ->> 'city'");

        JsonPathExpressions.ToPostgresExpression(AddressCity, asText: false)
            .Should().Be("data -> 'address' -> 'city'");

        JsonPathExpressions.ToPostgresExpression(FirstItemSku, asText: true)
            .Should().Be("data -> 'items' -> 0 ->> 'sku'");

        JsonPathExpressions.ToPostgresExpression(HyphenatedKey, asText: true)
            .Should().Be("data ->> 'first-name'");

        JsonPathExpressions.ToPostgresExpression([], asText: true, column: "d.data")
            .Should().Be("d.data");
    }

    [Fact]
    public void A_quote_in_a_key_is_doubled_rather_than_escaped()
    {
        JsonPathSegment[] segments = [JsonPathSegment.ForKey("it's")];

        JsonPathExpressions.ToPostgresExpression(segments, asText: true)
            .Should().Be("data ->> 'it''s'");
    }

    [Fact]
    public void The_path_form_is_one_operator_and_one_array_literal()
    {
        JsonPathExpressions.ToPostgresTextPath(AddressCity).Should().Be("data #>> '{address,city}'");
        JsonPathExpressions.ToPostgresTextPath(FirstItemSku).Should().Be("data #>> '{items,0,sku}'");
        JsonPathExpressions.ToPostgresTextPath(HyphenatedKey).Should().Be("data #>> '{first-name}'");
        JsonPathExpressions.ToPostgresTextPath([]).Should().Be("data");
    }

    [Fact]
    public void An_array_literal_element_that_could_be_misread_is_quoted()
    {
        JsonPathSegment[] segments = [JsonPathSegment.ForKey("first name"), JsonPathSegment.ForKey("a,b")];

        JsonPathExpressions.ToPostgresTextPath(segments)
            .Should().Be("data #>> '{\"first name\",\"a,b\"}'");
    }

    [Fact]
    public void A_containment_filter_nests_objects_and_wraps_arrays()
    {
        JsonPathExpressions.ToContainmentFilter(AddressCity, "\"Helsinki\"")
            .Should().Be("""{"address":{"city":"Helsinki"}}""");

        JsonPathExpressions.ToContainmentFilter(FirstItemSku, "\"A-1\"")
            .Should().Be("""{"items":[{"sku":"A-1"}]}""", "containment asks whether the array contains the element, and has no notion of position");

        JsonPathExpressions.ToContainmentFilter([], """{"a":1}""").Should().Be("""{"a":1}""");
    }

    [Fact]
    public void A_dotted_path_parses_back_into_the_segments_it_came_from()
    {
        JsonPathExpressions.ParseDotPath("items[0].sku").Should().Equal(FirstItemSku);
        JsonPathExpressions.ParseDotPath("address.city").Should().Equal(AddressCity);
        JsonPathExpressions.ParseDotPath("[\"first-name\"]").Should().Equal(HyphenatedKey);
        JsonPathExpressions.ParseDotPath(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void A_clr_path_resolves_through_the_naming_policy()
    {
        var resolution = JsonPathExpressions.ResolveClrPath(
            typeof(Person),
            JsonPathExpressions.ParseDotPath("address.city"),
            JsonNamingPolicy.CamelCase);

        resolution.Resolved.Should().BeTrue();
        resolution.Path.Should().Be("x.Address.City");
        resolution.Note.Should().BeNull();
    }

    [Fact]
    public void A_snake_case_policy_finds_the_pascal_case_member()
    {
        var resolution = JsonPathExpressions.ResolveClrPath(
            typeof(Person),
            JsonPathExpressions.ParseDotPath("first_name"),
            JsonNamingPolicy.SnakeCaseLower);

        resolution.Resolved.Should().BeTrue();
        resolution.Path.Should().Be("x.FirstName");
    }

    [Fact]
    public void An_explicit_json_property_name_wins_over_the_policy()
    {
        var resolution = JsonPathExpressions.ResolveClrPath(
            typeof(Person),
            JsonPathExpressions.ParseDotPath("nick"),
            JsonNamingPolicy.CamelCase);

        resolution.Resolved.Should().BeTrue();
        resolution.Path.Should().Be("x.Nickname");
    }

    [Fact]
    public void An_array_step_becomes_an_indexer_and_keeps_resolving_through_the_element_type()
    {
        var resolution = JsonPathExpressions.ResolveClrPath(
            typeof(Person),
            JsonPathExpressions.ParseDotPath("items[0].sku"),
            JsonNamingPolicy.CamelCase);

        resolution.Resolved.Should().BeTrue();
        resolution.Path.Should().Be("x.Items[0].Sku");
    }

    [Fact]
    public void A_member_the_type_does_not_have_still_yields_a_path_and_says_why_it_may_not_compile()
    {
        var resolution = JsonPathExpressions.ResolveClrPath(
            typeof(Person),
            JsonPathExpressions.ParseDotPath("address.postcode"),
            JsonNamingPolicy.CamelCase);

        resolution.Resolved.Should().BeFalse();
        resolution.Path.Should().Be("x.Address.postcode");
        resolution.Note.Should().Be("could not resolve 'postcode' on Address");
    }

    [Fact]
    public void With_no_clr_type_the_path_is_the_raw_keys_and_says_so()
    {
        var resolution = JsonPathExpressions.ResolveClrPath(
            null,
            JsonPathExpressions.ParseDotPath("address.city"),
            null);

        resolution.Resolved.Should().BeFalse();
        resolution.Path.Should().Be("x.address.city");
        resolution.Note.Should().Be("the document's CLR type is not known here");
    }

    [Fact]
    public void A_json_string_literal_is_escaped_but_not_over_escaped()
    {
        JsonPathExpressions.ToJsonStringLiteral("Helsinki").Should().Be("\"Helsinki\"");
        JsonPathExpressions.ToJsonStringLiteral("a \"b\"").Should().Be("\"a \\\"b\\\"\"");
        JsonPathExpressions.ToJsonStringLiteral("a+b").Should().Be("\"a+b\"", "relaxed escaping keeps a copied filter readable");
    }

    private sealed class Person
    {
        public string FirstName { get; set; } = string.Empty;

        [JsonPropertyName("nick")]
        public string Nickname { get; set; } = string.Empty;

        public Address Address { get; set; } = new();

        public List<Item> Items { get; set; } = [];
    }

    private sealed class Address
    {
        public string City { get; set; } = string.Empty;
    }

    private sealed class Item
    {
        public string Sku { get; set; } = string.Empty;
    }
}
