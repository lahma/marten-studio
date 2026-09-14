using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The search grammar of plan §3.4. It is typed a character at a time, so the contract is that it never
/// throws, never refuses a whole search because one term is half-written, and always says where a problem
/// is.
/// </summary>
public class SearchGrammarTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public void An_empty_search_parses_to_nothing(string? text)
    {
        var result = SearchGrammar.Parse(text);

        result.Predicates.Should().BeEmpty();
        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void A_bare_word_is_free_text()
    {
        var result = SearchGrammar.Parse("urgent");

        var predicate = result.Predicates.Single().Should().BeOfType<DocumentPredicate.FreeText>().Subject;

        predicate.Text.Should().Be("urgent");
    }

    [Fact]
    public void Several_terms_are_ANDed_in_the_order_typed()
    {
        var result = SearchGrammar.Parse("alpha beta");

        result.Predicates.Should().HaveCount(2);
        result.Predicates.OfType<DocumentPredicate.FreeText>().Select(x => x.Text)
            .Should().Equal("alpha", "beta");
    }

    [Fact]
    public void A_quoted_term_is_one_piece_of_free_text_without_its_quotes()
    {
        SearchGrammar.Parse("\"two words\"").Predicates.Single()
            .Should().BeOfType<DocumentPredicate.FreeText>().Subject.Text.Should().Be("two words");
    }

    [Fact]
    public void An_id_term_keeps_the_id_exactly_as_typed()
    {
        var id = Guid.NewGuid().ToString("D");

        SearchGrammar.Parse("id:" + id).Predicates.Single()
            .Should().BeOfType<DocumentPredicate.IdEquals>().Subject.Id.Should().Be(id);

        SearchGrammar.Parse("ID: " + id).Predicates.Single()
            .Should().BeOfType<DocumentPredicate.IdEquals>();
    }

    /// <summary>
    /// On a Marten document the JSON <c>Id</c> property and the indexed <c>id</c> column hold the same
    /// value, and only one of them has an index on it.
    /// </summary>
    [Fact]
    public void An_equality_on_id_is_the_primary_key_and_not_a_json_path()
    {
        SearchGrammar.Parse("id = 42").Predicates.Single().Should().BeOfType<DocumentPredicate.IdEquals>();
        SearchGrammar.Parse("id > 42").Predicates.Single().Should().BeOfType<DocumentPredicate.FieldCompare>();
    }

    [Theory]
    [InlineData("is:deleted", true)]
    [InlineData("IS:DELETED", true)]
    [InlineData("is:not-deleted", false)]
    [InlineData("is:live", false)]
    public void The_deleted_state_is_a_term_of_its_own(string text, bool expected) =>
        SearchGrammar.Parse(text).Predicates.Single()
            .Should().BeOfType<DocumentPredicate.IsDeleted>().Subject.Value.Should().Be(expected);

    [Fact]
    public void An_unknown_state_is_an_error_that_names_the_two_that_work()
    {
        var result = SearchGrammar.Parse("is:archived");

        result.Predicates.Should().BeEmpty();

        var error = result.Errors.Single();

        error.Message.Should().Contain("is:deleted");
        error.Position.Should().Be(3);
    }

    [Fact]
    public void A_tenant_term_carries_the_tenant_id()
    {
        SearchGrammar.Parse("tenant:acme").Predicates.Single()
            .Should().BeOfType<DocumentPredicate.Tenant>().Subject.TenantId.Should().Be("acme");
    }

    [Theory]
    [InlineData("status = open", "Equal")]
    [InlineData("status != open", "NotEqual")]
    [InlineData("status <> open", "NotEqual")]
    [InlineData("total > 1", "GreaterThan")]
    [InlineData("total >= 1", "GreaterThanOrEqual")]
    [InlineData("total < 1", "LessThan")]
    [InlineData("total <= 1", "LessThanOrEqual")]
    [InlineData("status: open", "Equal")]
    public void Each_comparison_operator_parses(string text, string expected) =>
        SearchGrammar.Parse(text).Predicates.Single()
            .Should().BeOfType<DocumentPredicate.FieldCompare>().Subject.Operator.ToString().Should().Be(expected);

    [Fact]
    public void A_dotted_path_becomes_segments()
    {
        var compare = SearchGrammar.Parse("address.city = Helsinki").Predicates.Single()
            .Should().BeOfType<DocumentPredicate.FieldCompare>().Subject;

        compare.Path.Should().Equal("address", "city");
        compare.Value.Should().BeOfType<SearchValue.Text>().Subject.Value.Should().Be("Helsinki");
    }

    [Fact]
    public void A_quoted_path_segment_reaches_a_key_with_a_dot_or_a_space_in_it()
    {
        var compare = SearchGrammar.Parse("\"odd key\".\"a.b\" = 1").Predicates.Single()
            .Should().BeOfType<DocumentPredicate.FieldCompare>().Subject;

        compare.Path.Should().Equal("odd key", "a.b");
    }

    [Fact]
    public void A_literal_is_classified_so_the_builder_knows_what_to_cast_to()
    {
        Value("n = 42").Should().BeOfType<SearchValue.Number>().Subject.Value.Should().Be(42m);
        Value("n = -1.5").Should().BeOfType<SearchValue.Number>().Subject.Value.Should().Be(-1.5m);
        Value("b = true").Should().BeOfType<SearchValue.Boolean>().Subject.Value.Should().BeTrue();
        Value("b = FALSE").Should().BeOfType<SearchValue.Boolean>().Subject.Value.Should().BeFalse();
        Value("x = null").Should().BeOfType<SearchValue.Null>();
        Value("d = 2026-09-14").Should().BeOfType<SearchValue.Timestamp>();
        Value("d = 2026-09-14T10:30:00Z").Should().BeOfType<SearchValue.Timestamp>();
        Value("s = hello").Should().BeOfType<SearchValue.Text>();

        // "12-34" is not a date and must not become one.
        Value("s = 12-34").Should().BeOfType<SearchValue.Text>();
    }

    /// <summary>Quoting is the only way a user can say "compare this as text".</summary>
    [Fact]
    public void Quoting_a_literal_pins_it_to_text()
    {
        Value("n = \"42\"").Should().BeOfType<SearchValue.Text>().Subject.Value.Should().Be("42");
        Value("b = 'true'").Should().BeOfType<SearchValue.Text>();
        Value("x = \"null\"").Should().BeOfType<SearchValue.Text>();
    }

    [Fact]
    public void A_contains_match_uses_the_tilde()
    {
        var like = SearchGrammar.Parse("address.city ~ helsin").Predicates.Single()
            .Should().BeOfType<DocumentPredicate.FieldLike>().Subject;

        like.Path.Should().Equal("address", "city");
        like.Text.Should().Be("helsin");
    }

    [Fact]
    public void A_containment_term_keeps_the_json_verbatim()
    {
        var contains = SearchGrammar.Parse("""@> {"status": "open", "nested": {"a": 1}}""")
            .Predicates.Single().Should().BeOfType<DocumentPredicate.Contains>().Subject;

        contains.Json.Should().Be("""{"status": "open", "nested": {"a": 1}}""");
    }

    [Fact]
    public void A_containment_term_may_be_an_array()
    {
        SearchGrammar.Parse("@> [1, 2]").Predicates.Single().Should().BeOfType<DocumentPredicate.Contains>();
    }

    [Fact]
    public void A_brace_that_holds_a_string_with_a_brace_in_it_still_closes()
    {
        SearchGrammar.Parse("""@> {"text": "a } brace"}""").Errors.Should().BeEmpty();
    }

    [Fact]
    public void Containment_that_is_not_JSON_is_an_error_and_not_a_filter()
    {
        var result = SearchGrammar.Parse("@> {not json}");

        result.Predicates.Should().BeEmpty();
        result.Errors.Single().Message.Should().Contain("does not parse");
    }

    [Fact]
    public void Containment_without_a_document_after_it_says_what_one_looks_like()
    {
        SearchGrammar.Parse("@>").Errors.Single().Message.Should().Contain("@> {\"status\":\"open\"}");
    }

    [Fact]
    public void An_unclosed_brace_is_an_error_that_points_at_the_opening_one()
    {
        var result = SearchGrammar.Parse("""@> {"a": 1""");

        result.Errors.Single().Message.Should().Contain("closing '}'");
        result.Errors.Single().Position.Should().Be(3);
    }

    [Fact]
    public void An_unclosed_quote_is_an_error_with_its_position()
    {
        var result = SearchGrammar.Parse("name = \"bob");

        result.Errors.Single().Message.Should().Contain("never closed");
        result.Errors.Single().Position.Should().Be(7);
    }

    [Fact]
    public void A_quote_may_be_escaped_by_doubling_or_by_a_backslash()
    {
        Value("""n = "say ""hi"" " """).Should().BeOfType<SearchValue.Text>().Subject.Value
            .Should().Be("say \"hi\" ");
        Value("""n = "say \"hi\"" """).Should().BeOfType<SearchValue.Text>().Subject.Value
            .Should().Be("say \"hi\"");
    }

    [Fact]
    public void An_operator_with_nothing_after_it_is_an_error_and_the_rest_still_parses()
    {
        var result = SearchGrammar.Parse("status =");

        result.Predicates.Should().BeEmpty();
        result.Errors.Single().Message.Should().Contain("needs a value");
    }

    [Fact]
    public void One_bad_character_does_not_silence_the_terms_after_it()
    {
        var result = SearchGrammar.Parse("= urgent");

        result.Errors.Should().ContainSingle();
        result.Predicates.Single().Should().BeOfType<DocumentPredicate.FreeText>().Subject.Text.Should().Be("urgent");
    }

    [Fact]
    public void An_email_address_stays_one_word()
    {
        var compare = SearchGrammar.Parse("email = bob@example.com").Predicates.Single()
            .Should().BeOfType<DocumentPredicate.FieldCompare>().Subject;

        compare.Value.Should().BeOfType<SearchValue.Text>().Subject.Value.Should().Be("bob@example.com");
    }

    [Fact]
    public void A_whole_search_parses_into_one_term_of_each_kind()
    {
        var result = SearchGrammar.Parse(
            """id:7 status = open address.city ~ hels @> {"vip":true} urgent is:deleted tenant:acme""");

        result.Errors.Should().BeEmpty();
        result.Predicates.Select(x => x.GetType().Name).Should().Equal(
            nameof(DocumentPredicate.IdEquals),
            nameof(DocumentPredicate.FieldCompare),
            nameof(DocumentPredicate.FieldLike),
            nameof(DocumentPredicate.Contains),
            nameof(DocumentPredicate.FreeText),
            nameof(DocumentPredicate.IsDeleted),
            nameof(DocumentPredicate.Tenant));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("@>")]
    [InlineData("@> {")]
    [InlineData(":")]
    [InlineData("...")]
    [InlineData("a = ")]
    [InlineData("~~~")]
    [InlineData("{}")]
    public void Half_typed_input_never_throws(string text)
    {
        var act = () => SearchGrammar.Parse(text);

        act.Should().NotThrow();
    }

    private static SearchValue Value(string text) =>
        SearchGrammar.Parse(text).Predicates.OfType<DocumentPredicate.FieldCompare>().Single().Value;
}
