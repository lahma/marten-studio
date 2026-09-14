using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The index verdict, from parsed search to the chips the strip renders (plan §3.4, differentiator 2).
/// </summary>
/// <remarks>
/// These are the tests that keep the verdict honest. A green strip over a filter that reads every row is
/// worse than no strip at all: it is the studio saying something false about the database it is supposed
/// to explain.
/// </remarks>
public class SearchVerdictTests
{
    private static readonly PostgresIndex EmailIndex =
        new("mt_doc_sqltestcustomer_uidx_email", "create unique index mt_doc_sqltestcustomer_uidx_email on studio_sql.mt_doc_sqltestcustomer using btree (email)");

    private static readonly PostgresIndex GinIndex =
        new("mt_doc_sqltestcustomer_idx_data", "create index mt_doc_sqltestcustomer_idx_data on studio_sql.mt_doc_sqltestcustomer using gin (data jsonb_path_ops)");

    private static SearchVerdict Evaluate(string search, params PostgresIndex[] indexes)
    {
        var table = SqlTestTables.FullyFeatured();
        var parse = SearchGrammar.Parse(search);
        var advice = IndexAdvisor.Evaluate(table, indexes, parse.Predicates);

        return DocumentDataService.BuildVerdict(parse, advice, parse.Predicates);
    }

    [Fact]
    public void An_empty_search_has_no_chips_and_no_complaint()
    {
        var verdict = Evaluate(string.Empty);

        verdict.Chips.Should().BeEmpty();
        verdict.Level.Should().Be(IndexVerdictLevel.Green);
        verdict.IsRed.Should().BeFalse();
    }

    [Fact]
    public void An_id_filter_is_green_because_id_is_the_primary_key()
    {
        var verdict = Evaluate("id:8f1d5a6e-0000-0000-0000-000000000001");

        verdict.Level.Should().Be(IndexVerdictLevel.Green);
        verdict.Chips.Should().ContainSingle()
            .Which.Text.Should().StartWith("id:");
    }

    [Fact]
    public void A_duplicated_field_with_an_index_behind_it_is_green()
    {
        var verdict = Evaluate("Email = a@b.c", EmailIndex);

        verdict.Level.Should().Be(IndexVerdictLevel.Green);
        verdict.Suggestion.Should().BeNull();
    }

    [Fact]
    public void A_duplicated_field_with_no_index_is_red_and_says_which_index_to_add()
    {
        var verdict = Evaluate("Email = a@b.c");

        verdict.Level.Should().Be(IndexVerdictLevel.Red);
        verdict.IsRed.Should().BeTrue();
        verdict.FilterIsRed.Should().BeTrue("a filter nothing can serve is what withholds a read");
        verdict.Suggestion.Should().Contain("Index(x => x.Email)");
    }

    [Fact]
    public void A_property_that_is_only_in_the_json_is_red_and_suggests_duplicating_it()
    {
        var verdict = Evaluate("Nickname = bob");

        verdict.Level.Should().Be(IndexVerdictLevel.Red);
        verdict.Suggestion.Should().Contain("Duplicate(x => x.Nickname)");
        verdict.Chips.Should().ContainSingle().Which.Text.Should().Be("Nickname = bob");
    }

    [Fact]
    public void Free_text_is_red_because_no_index_can_serve_a_leading_wildcard()
    {
        var verdict = Evaluate("helsinki");

        verdict.Level.Should().Be(IndexVerdictLevel.Red);
        verdict.Chips.Should().ContainSingle().Which.Reason.Should().Contain("no index");
    }

    [Fact]
    public void Containment_is_green_when_there_is_a_gin_index_on_data_and_red_when_there_is_not()
    {
        Evaluate("""@> {"Status":"open"}""", GinIndex).Level.Should().Be(IndexVerdictLevel.Green);

        var without = Evaluate("""@> {"Status":"open"}""");
        without.Level.Should().Be(IndexVerdictLevel.Red);
        without.Suggestion.Should().Contain("GinIndexJsonData()");
    }

    [Fact]
    public void The_worst_term_decides_the_strip_and_every_term_keeps_its_own_chip()
    {
        var verdict = Evaluate("id:8f1d5a6e-0000-0000-0000-000000000001 Nickname = bob");

        verdict.Level.Should().Be(IndexVerdictLevel.Red, "the worst term wins");
        verdict.Chips.Should().HaveCount(2);
        verdict.Chips[0].Level.Should().Be(IndexVerdictLevel.Green);
        verdict.Chips[1].Level.Should().Be(IndexVerdictLevel.Red);
    }

    [Fact]
    public void A_term_that_does_not_parse_becomes_an_error_chip_and_makes_the_whole_search_red()
    {
        var verdict = Evaluate("Email = \"never closed");

        verdict.HasErrors.Should().BeTrue();
        verdict.Level.Should().Be(IndexVerdictLevel.Red);
        verdict.Chips.Should().Contain(x => x.Kind == SearchChipKind.Error);
    }

    [Fact]
    public void A_sort_verdict_is_its_own_chip()
    {
        var table = SqlTestTables.FullyFeatured();
        var parse = SearchGrammar.Parse(string.Empty);
        var advice = IndexAdvisor.Evaluate(table, [], parse.Predicates, new DocumentColumn.Duplicated("email"));

        var verdict = DocumentDataService.BuildVerdict(parse, advice, parse.Predicates);

        verdict.Chips.Should().ContainSingle().Which.Kind.Should().Be(SearchChipKind.Sort);
        verdict.Level.Should().Be(IndexVerdictLevel.Red);

        // ... but a sort nothing indexes must not withhold the read: Marten indexes no metadata column by
        // default, so that would mean pressing "Run anyway" to open any collection at all.
        verdict.FilterLevel.Should().Be(IndexVerdictLevel.Green);
        verdict.FilterIsRed.Should().BeFalse();
    }

    [Theory]
    [InlineData("is:deleted", "is:deleted")]
    [InlineData("is:not-deleted", "is:not-deleted")]
    [InlineData("tenant:acme", "tenant:acme")]
    [InlineData("Email ~ example", "Email ~ example")]
    [InlineData("OrderCount >= 3", "OrderCount >= 3")]
    public void Every_term_is_echoed_back_in_the_grammar_s_own_words(string search, string expected)
    {
        // The echo is what makes a typo visible before it becomes an empty result set.
        Evaluate(search).Chips.Should().Contain(x => x.Text == expected);
    }

    [Fact]
    public void A_subclass_filter_is_amber_because_marten_creates_no_index_on_the_discriminator()
    {
        var table = DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestCustomer>(options =>
            options.Schema.For<SqlTestCustomer>().AddSubClass<SqlTestVipCustomer>()));

        var predicates = new DocumentPredicate[] { new DocumentPredicate.SubclassIs("sqltestvipcustomer") };
        var advice = IndexAdvisor.Evaluate(table, [], predicates);
        var verdict = DocumentDataService.BuildVerdict(SearchGrammarResult.Empty, advice, predicates);

        verdict.Level.Should().Be(IndexVerdictLevel.Amber);
        verdict.Chips.Should().ContainSingle().Which.Text.Should().Be("type:sqltestvipcustomer");
    }

    /// <summary>
    /// A builder refusal lands in the verdict, not beside it.
    /// </summary>
    /// <remarks>
    /// P2-fix follow-up 2: a predicate the collection cannot answer used to throw after the strip had been
    /// composed, so the page rendered an exception message under a green badge. Folded in here it is an
    /// error chip like any other, the rest of the verdict survives, and the badge turns red.
    /// </remarks>
    [Fact]
    public void A_builder_refusal_becomes_an_error_chip_and_turns_the_badge_red()
    {
        SearchVerdict green = Evaluate("id:00000000-0000-0000-0000-000000000001");

        green.Level.Should().Be(IndexVerdictLevel.Green, "the precondition for this test to mean anything");

        SearchVerdict refused = DocumentDataService.WithRefusal(
            green, "'sqltestcustomer' is not soft-deleted, so it has no mt_deleted column to filter on.");

        refused.Level.Should().Be(IndexVerdictLevel.Red);
        refused.FilterLevel.Should().Be(IndexVerdictLevel.Red);
        refused.HasErrors.Should().BeTrue();

        refused.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("not soft-deleted").And.NotContain("Parameter");

        refused.Chips.Should().HaveCount(green.Chips.Count + 1, "the terms that did parse are still shown");
        refused.Chips[^1].Kind.Should().Be(SearchChipKind.Error);
    }
}
