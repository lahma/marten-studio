using KStudio.Core.Predicates;

namespace KStudio.Tests.Predicates;

/// <summary>
/// The M3 half of the grammar (decision 2 of D-PAYLOAD): <c>value</c> and JSON paths as fields, the ordering operators on numbers, the
/// replaced messages with their positions, a path error surfaced by the parser, and the canonical text of paths and numbers.
/// </summary>
public class PredicateValueGrammarTests
{
    private static PredicateNode? Parse(string text)
    {
        PredicateParseResult result = PredicateParser.Parse(text);
        result.Errors.Should().BeEmpty("'" + text + "' is valid");
        return result.Tree;
    }

    private static string Error(string text)
    {
        PredicateParseResult result = PredicateParser.Parse(text);
        result.Errors.Should().ContainSingle();
        return result.Errors[0].Shown;
    }

    [Theory]
    [InlineData("value = 'x'", "value = 'x'")]
    [InlineData("VALUE ~ 'x'", "value ~ 'x'")]
    [InlineData("value is null", "value is null")]
    [InlineData("$.a.b = 1", "$.a.b = 1")]
    [InlineData("$.a=1", "$.a = 1")]
    [InlineData("$.a[0]>=2", "$.a[0] >= 2")]
    [InlineData("$.a[*].b in ('x','y')", "$.a[*].b in ('x', 'y')")]
    [InlineData("$['customer-email']='x'", "$['customer-email'] = 'x'")]
    [InlineData("$['$type'] = 'T'", "$['$type'] = 'T'")]
    [InlineData("$['plain'] = 'x'", "$.plain = 'x'")]
    [InlineData("$[\"a b\"] = 'x'", "$['a b'] = 'x'")]
    [InlineData("$['it''s'] = 'x'", "$['it''s'] = 'x'")]
    [InlineData("$ = null", "$ = null")]
    [InlineData("$.n < 1.50 and $.n <= 2 and $.n > -3 and $.n >= 4e2", "$.n < 1.50 and $.n <= 2 and $.n > -3 and $.n >= 400")]
    [InlineData("not $.a = true or $.b = false", "not $.a = true or $.b = false")]
    public void The_new_fields_and_operators_parse_and_are_written_in_one_form(string typed, string canonical)
    {
        PredicateFormatter.Format(Parse(typed)).Should().Be(canonical);
    }

    [Fact]
    public void A_field_is_a_value_field_or_a_path_field_with_a_parsed_path()
    {
        ClauseNode value = (ClauseNode) Parse("value ^= 'x'")!;
        ClauseNode path = (ClauseNode) Parse("$.a[0].b >= 5")!;

        value.Field.Should().BeOfType<ValueField>();
        value.Operator.Should().Be(PredicateOperator.Prefix);
        path.Field.Should().BeOfType<JsonPathField>().Which.Path.Steps.Should().HaveCount(3);
        path.Operator.Should().Be(PredicateOperator.GreaterOrEqual);
        path.Literals.Single().Should().BeOfType<NumberLiteral>().Which.Value.Should().Be(5m);
    }

    [Theory]
    [InlineData("$.a = 1.50", "$.a = 1.50")]
    [InlineData("$.a = -0", "$.a = 0")]
    [InlineData("$.a = 1e3", "$.a = 1000")]
    [InlineData("$.a = 0.000001", "$.a = 0.000001")]
    [InlineData("$.a in (1, 'two', true, null)", "$.a in (1, 'two', true, null)")]
    public void Numbers_write_as_invariant_decimals_and_parse_back_to_the_same_tree(string typed, string canonical)
    {
        PredicateNode? tree = Parse(typed);
        string written = PredicateFormatter.Format(tree);

        written.Should().Be(canonical);
        PredicateTree.StructurallyEqual(Parse(written), tree).Should().BeTrue();
    }

    [Theory]
    [InlineData("$.a.b = 1 and $['c d'] ^= 'x' or not $.e[*].f ~ 'y'")]
    [InlineData("value in ('a', 'b') and $.items[3][*].sku = 'x'")]
    [InlineData("$['it''s'] = 'x'")]
    public void The_canonical_text_of_a_path_search_parses_back_to_the_same_tree(string typed)
    {
        PredicateNode? tree = Parse(typed);

        PredicateTree.StructurallyEqual(Parse(PredicateFormatter.Format(tree)), tree).Should().BeTrue();
    }

    [Fact]
    public void Paths_that_differ_only_in_how_they_were_written_are_the_same_tree()
    {
        PredicateTree.StructurallyEqual(Parse("$['a'].b = 1"), Parse("$.a.b = 1")).Should().BeTrue();
        PredicateTree.StructurallyEqual(Parse("$.a.b = 1"), Parse("$.a.B = 1")).Should().BeFalse("a search compares member names exactly");
    }

    [Theory]
    [InlineData("key = 'a' and $.a.", "Position 18: the JSON path '$.a.' has an empty step: write $.name, $.name[0] or $.name[*].")]
    [InlineData("$a = 1", "Position 2: the JSON path '$a' must go on with . or [ after $.")]
    [InlineData("$.a[x] = 1", "Position 4: the JSON path '$.a[x]' has an index that is not a whole number: write [0], [12] or [*].")]
    [InlineData("$.1a = 1", "Position 2: the JSON path '$.1a' has a member the search cannot write: write it in brackets, $['1a'].")]
    [InlineData("$['a b = 1", "Position 2: the JSON path '$['a b = 1' has a '[' with no matching ']'.")]
    public void A_path_error_comes_from_the_parser_with_its_position_in_the_whole_text(string text, string message)
    {
        Error(text).Should().Be(message);
        PredicateParser.Parse(text).Tree.Should().BeNull();
    }

    [Theory]
    [InlineData("foo = 'a'", "Position 1: unknown field 'foo'. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("valu = 'a'", "Position 1: unknown field 'valu'. Search fields are key, header.<name>, ce.<attribute>, any, value and $.json.paths.")]
    [InlineData("key = 'a' and", "Position 14: expected a field (key, header.<name>, ce.<attribute>, any, value or a $.json.path), found the end.")]
    [InlineData("= 'a'", "Position 1: expected a field (key, header.<name>, ce.<attribute>, any, value or a $.json.path), found '='.")]
    [InlineData("key 'a'", "Position 5: expected an operator ('=', '^=', '~', '<', '<=', '>', '>=', 'in' or 'is'), found a string.")]
    [InlineData("$.a 'x'", "Position 5: expected an operator ('=', '^=', '~', '<', '<=', '>', '>=', 'in' or 'is'), found a string.")]
    public void The_replaced_sentences_list_the_new_fields_and_operators(string text, string message)
    {
        Error(text).Should().Be(message);
    }

    [Fact]
    public void A_ce_attribute_is_one_to_64_characters_like_a_redaction_rules()
    {
        string sixtyFour = new('a', 64);

        Parse($"ce.{sixtyFour} = 'x'");
        Error($"ce.{sixtyFour}a = 'x'").Should().StartWith("Position 1: unknown field 'ce.aaaa");
    }

    [Theory]
    [InlineData("$['a = 'SECRET'")]
    [InlineData("$['abc and key = 'SECRET'")]
    [InlineData("key = 'x' and $['a = 'SECRET' and key = 'SECRET2'")]
    public void A_path_error_never_shows_the_text_after_a_names_closing_quote(string text)
    {
        PredicateParseResult result = PredicateParser.Parse(text);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().OnlyContain(static x => !x.Message.Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public void A_path_error_never_contains_a_literal_of_the_search()
    {
        string message = Error("$.a. = 'SECRET-literal'");

        message.Should().NotContain("SECRET");
    }
}
