using KStudio.Core.Predicates;

namespace KStudio.Tests.Predicates;

/// <summary>
/// The JSON path of the search and of a redaction rule (decision 2.1 of D-PAYLOAD): <c>$</c>, then up to 32 steps of <c>.member</c>,
/// <c>['member']</c>, <c>[n]</c> or <c>[*]</c>; every error names its position and says what to write; and "covers" is what a redaction
/// rule says of a path (member names ignoring case, fail closed).
/// </summary>
public class JsonPathTests
{
    private static JsonPathExpression Parse(string text)
    {
        JsonPathExpression.TryParse(text, 0, out JsonPathExpression? path, out PredicateError? error).Should().BeTrue(error?.Message);
        return path!;
    }

    private static string Error(string text, int start = 0)
    {
        JsonPathExpression.TryParse(text, start, out JsonPathExpression? path, out PredicateError? error).Should().BeFalse();
        path.Should().BeNull();
        return error!.Message;
    }

    [Theory]
    [InlineData("$", "$")]
    [InlineData("$.a", "$.a")]
    [InlineData("$.a.b_2", "$.a.b_2")]
    [InlineData("$._x", "$._x")]
    [InlineData("$.a[0]", "$.a[0]")]
    [InlineData("$.a[12][*].b", "$.a[12][*].b")]
    [InlineData("$.items[*].sku", "$.items[*].sku")]
    [InlineData("$[0]", "$[0]")]
    [InlineData("$[*]", "$[*]")]
    [InlineData("$['customer-email']", "$['customer-email']")]
    [InlineData("$['$type']", "$['$type']")]
    [InlineData("$['plain']", "$.plain")]
    [InlineData("$[\"double\"]", "$.double")]
    [InlineData("$['it''s']", "$['it''s']")]
    [InlineData("$.a['b c'].d", "$.a['b c'].d")]
    [InlineData("$[999999999]", "$[999999999]")]
    public void A_path_parses_and_its_canonical_text_parses_back_to_the_same_path(string text, string canonical)
    {
        JsonPathExpression path = Parse(text);

        path.Text.Should().Be(canonical);
        Parse(path.Text).Text.Should().Be(canonical);
        Parse(path.Text).Steps.Should().Equal(path.Steps);
    }

    [Fact]
    public void The_steps_are_members_indexes_and_wildcards()
    {
        Parse("$.a[3][*]['b-c']").Steps.Should().Equal(
            new JsonMemberStep("a"), new JsonIndexStep(3), new JsonWildcardStep(), new JsonMemberStep("b-c"));
        Parse("$").Steps.Should().BeEmpty();
    }

    [Fact]
    public void Thirty_two_steps_are_allowed_and_a_member_of_128_characters()
    {
        Parse("$" + string.Concat(Enumerable.Repeat(".a", 32))).Steps.Should().HaveCount(32);
        Parse("$." + new string('m', 128)).Steps.Single().Should().Be(new JsonMemberStep(new string('m', 128)));
        Parse("$['" + new string('m', 128) + "']").Steps.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("$a", "The JSON path '$a' at position 2 must go on with . or [ after $.")]
    [InlineData("$.", "The JSON path '$.' at position 2 has an empty step: write $.name, $.name[0] or $.name[*].")]
    [InlineData("$..a", "The JSON path '$..a' at position 2 has an empty step: write $.name, $.name[0] or $.name[*].")]
    [InlineData("$.a.", "The JSON path '$.a.' at position 4 has an empty step: write $.name, $.name[0] or $.name[*].")]
    [InlineData("$.a.*", "The JSON path '$.a.*' at position 4 has an empty step: write $.name, $.name[0] or $.name[*].")]
    [InlineData("$.[0]", "The JSON path '$.[0]' at position 2 has an empty step: write $.name, $.name[0] or $.name[*].")]
    [InlineData("$.1a", "The JSON path '$.1a' at position 2 has a member the search cannot write: write it in brackets, $['1a'].")]
    [InlineData("$.a[x]", "The JSON path '$.a[x]' at position 4 has an index that is not a whole number: write [0], [12] or [*].")]
    [InlineData("$.a[01]", "The JSON path '$.a[01]' at position 4 has an index that is not a whole number: write [0], [12] or [*].")]
    [InlineData("$.a[-1]", "The JSON path '$.a[-1]' at position 4 has an index that is not a whole number: write [0], [12] or [*].")]
    [InlineData("$.a[1.5]", "The JSON path '$.a[1.5]' at position 4 has an index that is not a whole number: write [0], [12] or [*].")]
    [InlineData("$.a[1000000000]", "The JSON path '$.a[1000000000]' at position 4 has an index that is not a whole number: write [0], [12] or [*].")]
    [InlineData("$.a[]", "The JSON path '$.a[]' at position 4 has an index that is not a whole number: write [0], [12] or [*].")]
    [InlineData("$.a[0", "The JSON path '$.a[0' at position 4 has a '[' with no matching ']'.")]
    [InlineData("$.a[", "The JSON path '$.a[' at position 4 has a '[' with no matching ']'.")]
    [InlineData("$['a'", "The JSON path '$['a'' at position 2 has a '[' with no matching ']'.")]
    [InlineData("$['a", "The JSON path '$['a' at position 2 has a '[' with no matching ']'.")]
    [InlineData("$.a]", "The JSON path '$.a]' at position 4 has an empty step: write $.name, $.name[0] or $.name[*].")]
    public void Every_error_names_its_position_and_says_what_to_write(string text, string message)
    {
        Error(text).Should().Be(message);
    }

    [Fact]
    public void Positions_are_counted_in_the_search_text_not_in_the_path()
    {
        Error("$.a.", start: 20).Should().Be("The JSON path '$.a.' at position 24 has an empty step: write $.name, $.name[0] or $.name[*].");

        JsonPathExpression.TryParse("$.a.", 20, out _, out PredicateError? error);
        error!.Start.Should().Be(23, "an error's start is counted from 0");
    }

    [Fact]
    public void A_member_over_128_characters_and_a_path_over_32_steps_are_refused()
    {
        Error("$." + new string('m', 129)).Should().Contain("at position 2 has a member longer than 128 characters.");
        Error("$['" + new string('m', 129) + "']").Should().Contain("has a member longer than 128 characters.");

        string tooMany = "$" + string.Concat(Enumerable.Repeat(".a", 33));
        string message = Error(tooMany);

        message.Should().Be("The JSON path '" + tooMany[..64] + "' at position 66 has more than 32 steps.");
        message.Length.Should().BeLessThan(130, "the path is shown cut at 64 characters");
    }

    [Theory]
    [InlineData("$.a", "$.a.b", true)]
    [InlineData("$.a", "$.a", true)]
    [InlineData("$.a.b", "$.a", false)]
    [InlineData("$.A.B", "$.a.b", true)]
    [InlineData("$.customer.email", "$.Customer.EMAIL", true)]
    [InlineData("$.a", "$.b", false)]
    [InlineData("$.a[0]", "$.a[0].x", true)]
    [InlineData("$.a[0]", "$.a[1]", false)]
    [InlineData("$.a[*]", "$.a[7]", true)]
    [InlineData("$.a[*]", "$.a[*]", true)]
    [InlineData("$.a[0]", "$.a[*]", false)]
    [InlineData("$.a[*].b", "$.a[2].b.c", true)]
    [InlineData("$.a[*].b", "$.a[2]", false)]
    [InlineData("$.a[*]", "$.a.b", false)]
    [InlineData("$.a.b", "$.a[0]", false)]
    [InlineData("$", "$.anything[0].at.all", true)]
    [InlineData("$", "$", true)]
    public void A_rule_covers_a_clauses_path_when_every_node_it_can_select_is_covered(string rule, string clause, bool covered)
    {
        Parse(rule).CoversClause(Parse(clause)).Should().Be(covered);
    }

    [Fact]
    public void Covers_takes_the_steps_of_a_concrete_node()
    {
        JsonPathExpression rule = Parse("$.items[*].secret");

        rule.Covers([new JsonMemberStep("ITEMS"), new JsonIndexStep(4), new JsonMemberStep("Secret")]).Should().BeTrue();
        rule.Covers([new JsonMemberStep("items"), new JsonIndexStep(4), new JsonMemberStep("secret"), new JsonMemberStep("deeper")]).Should().BeTrue("a rule masks the whole subtree");
        rule.Covers([new JsonMemberStep("items"), new JsonIndexStep(4)]).Should().BeFalse("an ancestor is not masked");
        rule.Covers([new JsonMemberStep("items"), new JsonMemberStep("x"), new JsonMemberStep("secret")]).Should().BeFalse("[*] is an index, not a member");
    }
}
