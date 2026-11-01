using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The scan that finds column references in the text Postgres deparses a plan's conditions into (0.10-A, issue #28), and the targets
/// built on it: names out, never run, bounded, and honest about what it cannot read.
/// </summary>
public class PlanConditionTextTests
{
    private static IReadOnlyList<string> Names(string text) => [.. PlanConditionText.Columns(text).Select(static x => x.Column)];

    [Theory]
    [InlineData("(status = 'open'::text)", "status")]
    [InlineData("((city = 'Oslo'::text) AND (postcode = '0150'::text))", "city,postcode")]
    [InlineData("(a IS NOT NULL)", "a")]
    [InlineData("((a IS NULL) OR (b = ANY ('{1,2}'::integer[])))", "a,b")]
    [InlineData("(created_at > (now() - '7 days'::interval))", "created_at")]
    [InlineData("((ts)::timestamp with time zone > now())", "ts")]
    [InlineData("(id = $1)", "id")]
    [InlineData("(price >= 1.5e3)", "price")]
    [InlineData("(name ~~ 'a%'::text)", "name")]
    [InlineData("(x BETWEEN 1 AND 5)", "x")]
    public void Column_references_are_found_and_keywords_types_functions_and_literals_are_not(string text, string expected) =>
        Names(text).Should().Equal(expected.Split(','));

    [Fact]
    public void A_string_literal_is_skipped_whole_with_its_doubled_quotes_and_an_escape_strings_backslashes()
    {
        Names("(note = 'it''s status = x'::text)").Should().Equal("note");
        Names("(note = E'a\\'b status'::text)").Should().Equal("note");
        Names("(note = 'back\\' AND other = 1)").Should().Equal("note", "other");
    }

    [Fact]
    public void A_quoted_identifier_is_read_with_its_doubled_quotes_undone()
    {
        Names("(\"CustomerId\" = 5)").Should().Equal("CustomerId");
        Names("(\"we\"\"ird\" = 5)").Should().Equal("we\"ird");
    }

    [Fact]
    public void A_qualifier_belongs_to_the_column_it_dots_into_and_a_schema_qualified_type_is_not_a_column()
    {
        PlanConditionColumn qualified = PlanConditionText.Columns("(o.customer_id = c.id)").Should().HaveCount(2).And.Subject.First();

        qualified.Qualifier.Should().Be("o");
        qualified.Column.Should().Be("customer_id");
        Names("((mood)::public.mood = 'happy'::public.mood)").Should().Equal("mood");
    }

    [Fact]
    public void A_function_and_a_cast_are_seen_where_they_wrap_the_column_and_not_where_they_do_not()
    {
        PlanConditionColumn lower = PlanConditionText.Columns("(lower((email)::text) = 'x'::text)").Single();
        lower.Column.Should().Be("email");
        lower.Function.Should().Be("lower");
        lower.CastType.Should().Be("text");
        lower.Wrap.Should().Be(PlanColumnWrap.Function);

        PlanConditionColumn cast = PlanConditionText.Columns("((created_at)::date = '2026-11-01'::date)").Single();
        cast.Wrap.Should().Be(PlanColumnWrap.Cast);
        cast.CastType.Should().Be("date");

        PlanConditionText.Columns("(email = 'x'::text)").Single().Wrap.Should().Be(PlanColumnWrap.None);

        // The second argument of a function is a false negative by design, never a false finding.
        IReadOnlyList<PlanConditionColumn> coalesce = PlanConditionText.Columns("(coalesce(a, b) = 1)");
        coalesce.Single(static x => x.Column == "a").Function.Should().Be("coalesce");
        coalesce.Single(static x => x.Column == "b").Wrap.Should().Be(PlanColumnWrap.None);
    }

    [Fact]
    public void A_multi_word_type_after_a_cast_is_read_whole()
    {
        PlanConditionText.Columns("((ts)::timestamp without time zone > now())").Single().CastType.Should().Be("timestamp without time zone");
        PlanConditionText.Columns("((n)::character varying(20) = 'x'::text)").Single().CastType.Should().Be("character varying");
    }

    [Fact]
    public void HasWord_finds_a_bare_keyword_and_never_one_inside_a_literal_or_a_quoted_name()
    {
        PlanConditionText.HasWord("((a = 1) OR (b = 2))", "or").Should().BeTrue();
        PlanConditionText.HasWord("(a = 'x or y'::text)", "or").Should().BeFalse();
        PlanConditionText.HasWord("(\"or\" = 1)", "or").Should().BeFalse();
        PlanConditionText.HasWord(null, "or").Should().BeFalse();
    }

    [Fact]
    public void A_text_past_the_bound_is_not_read_and_a_pathological_one_never_throws()
    {
        Names("(a = 1)" + new string(' ', PlanConditionText.MaxCharacters)).Should().BeEmpty("past MaxCharacters nothing is read");
        Names(string.Empty).Should().BeEmpty();
        PlanConditionText.Columns(null).Should().BeEmpty();

        // More tokens than the bound: it reads what it can and stops.
        Names(string.Join(" AND ", Enumerable.Range(0, 3_000).Select(static x => "(c" + x + " = 1)"))).Count.Should().BeLessThanOrEqualTo(PlanConditionText.MaxTokens);

        Random random = new(10);
        const string alphabet = "abc_\"'(),.:$\\ E\n0123=<>[]";

        for (int i = 0; i < 2_000; i++)
        {
            string text = new([.. Enumerable.Range(0, random.Next(0, 80)).Select(_ => alphabet[random.Next(alphabet.Length)])]);

            Action read = () =>
            {
                PlanConditionText.Columns(text);
                PlanConditionText.HasWord(text, "or");
            };

            read.Should().NotThrow("'{0}' is only text", text);
        }
    }

    // ---- The targets ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_plan_with_schemas_names_its_relations_and_the_columns_its_nodes_use()
    {
        PlanFactsTargets targets = PlanFactsTargets.From(PlanFixtures.Plan("p16-fires"));

        targets.Relations.Should().Equal(new PlanRelationRef("public", "addresses"));
        targets.Columns[new PlanRelationRef("public", "addresses")].Should().Equal("city", "postcode");
        targets.ColumnsOf(0).Select(static x => x.Column).Should().Equal("city", "postcode");
        targets.Unqualified.Should().Be(0);
        targets.NotRead.Should().Be(0);
    }

    [Fact]
    public void A_plan_run_without_VERBOSE_names_no_schema_so_nothing_is_resolved_and_the_scans_are_counted()
    {
        QueryPlan plan = QueryPlanReader.Read(PlanFixtures.Get("p16-fires").Replace("\"Schema\": \"public\",", string.Empty, StringComparison.Ordinal)).Plan!;

        PlanFactsTargets targets = PlanFactsTargets.From(plan);

        targets.Relations.Should().BeEmpty("an unqualified name would resolve through a search_path that is not the statement's");
        targets.Unqualified.Should().Be(1);
        targets.Columns.Should().BeEmpty();
    }

    [Fact]
    public void A_join_condition_reaches_the_relation_its_alias_names_and_an_alias_two_relations_share_names_neither()
    {
        const string json = """
            [{"Plan": {"Node Type": "Hash Join", "Join Type": "Inner", "Plan Rows": 10, "Plan Width": 8, "Startup Cost": 0, "Total Cost": 10,
              "Hash Cond": "(o.customer_id = c.id)",
              "Plans": [
                {"Node Type": "Seq Scan", "Parent Relationship": "Outer", "Relation Name": "orders", "Schema": "public", "Alias": "o", "Plan Rows": 10, "Plan Width": 4, "Startup Cost": 0, "Total Cost": 5},
                {"Node Type": "Seq Scan", "Parent Relationship": "Inner", "Relation Name": "customers", "Schema": "public", "Alias": "c", "Plan Rows": 10, "Plan Width": 4, "Startup Cost": 0, "Total Cost": 5}]},
              "Planning Time": 0.1}]
            """;

        PlanFactsTargets targets = PlanFactsTargets.From(QueryPlanReader.Read(json).Plan!);

        targets.Relations.Should().Equal(new PlanRelationRef("public", "orders"), new PlanRelationRef("public", "customers"));
        targets.Columns[new PlanRelationRef("public", "orders")].Should().Equal("customer_id");
        targets.Columns[new PlanRelationRef("public", "customers")].Should().Equal("id");

        string ambiguous = json.Replace("\"Alias\": \"c\"", "\"Alias\": \"o\"", StringComparison.Ordinal).Replace("\"customers\"", "\"archived\"", StringComparison.Ordinal);
        PlanFactsTargets shared = PlanFactsTargets.From(QueryPlanReader.Read(ambiguous).Plan!);

        shared.Columns.Should().BeEmpty("o names two different relations, so a column qualified by it belongs to neither");
    }

    [Fact]
    public void More_relations_than_a_read_takes_are_counted_not_read()
    {
        string scans = string.Join(",", Enumerable.Range(0, PlanFactsTargets.MaxRelations + 3).Select(static i =>
            "{\"Node Type\": \"Seq Scan\", \"Relation Name\": \"t" + i + "\", \"Schema\": \"public\", \"Alias\": \"t" + i + "\", \"Plan Rows\": 1, \"Plan Width\": 4, \"Startup Cost\": 0, \"Total Cost\": 1}"));
        string json = "[{\"Plan\": {\"Node Type\": \"Append\", \"Plan Rows\": 1, \"Plan Width\": 4, \"Startup Cost\": 0, \"Total Cost\": 1, \"Plans\": [" + scans + "]}, \"Planning Time\": 0.1}]";

        PlanFactsTargets targets = PlanFactsTargets.From(QueryPlanReader.Read(json).Plan!);

        targets.Relations.Should().HaveCount(PlanFactsTargets.MaxRelations);
        targets.NotRead.Should().Be(3);
    }

    // ---- The panel's words ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "-")]
    [InlineData(-1.0, "all rows differ")]
    [InlineData(-0.25, "25% of the rows")]
    [InlineData(1234.0, "1,234")]
    public void N_distinct_is_read_as_a_person_does(double? distinct, string expected) => PlanFactsText.Distinct(distinct).Should().Be(expected);

    [Fact]
    public void A_relation_is_one_line_and_a_withheld_one_says_only_that_it_is_withheld()
    {
        DateTimeOffset now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        PlanRelationFacts read = new("public", "orders", PlanRelationState.Read) { EstimatedRows = 1_200_000, LastAnalyze = now.AddDays(-3), ModifiedSinceAnalyze = 168_000 };

        PlanFactsText.RelationLine(read, now).Should().Be("public.orders: ~1,200,000 rows, analyzed 3 days ago, 168,000 changed since (14%)");
        PlanFactsText.RelationLine(read with { LastAnalyze = null, EstimatedRows = null, ModifiedSinceAnalyze = null }, now).Should().Be("public.orders: no row estimate, never analyzed");

        PlanRelationFacts withheld = new("hr", "salaries", PlanRelationState.Withheld);
        PlanFactsText.RelationLine(withheld, now).Should().StartWith("‹withheld›:").And.NotContain("hr").And.NotContain("salaries");
        withheld.Label.Should().Be("‹withheld›");
    }
}
