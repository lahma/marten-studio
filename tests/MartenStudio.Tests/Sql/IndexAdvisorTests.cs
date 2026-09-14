using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The index verdict of plan §3.4, differentiator 2: green when an index serves the filter, amber when one
/// names the column but cannot lead with it, red when every row is read — and the <c>StoreOptions</c> line
/// that would fix it.
/// </summary>
public class IndexAdvisorTests
{
    private const string PrimaryKey =
        "CREATE UNIQUE INDEX mt_doc_sqltestcustomer_pkey ON studio_sql.mt_doc_sqltestcustomer USING btree (tenant_id, id)";

    private const string EmailIndex =
        "CREATE INDEX mt_doc_sqltestcustomer_idx_email ON studio_sql.mt_doc_sqltestcustomer USING btree (email)";

    private const string GinIndex =
        "CREATE INDEX mt_doc_sqltestcustomer_idx_data ON studio_sql.mt_doc_sqltestcustomer USING gin (data jsonb_path_ops)";

    private const string CompositeIndex =
        "CREATE INDEX mt_doc_sqltestcustomer_idx_pair ON studio_sql.mt_doc_sqltestcustomer USING btree (mt_last_modified, order_count)";

    private const string ComputedNicknameIndex =
        "CREATE INDEX mt_doc_sqltestcustomer_idx_nick ON studio_sql.mt_doc_sqltestcustomer USING btree (((data -> 'Profile' ->> 'Nickname')))";

    [Fact]
    public void An_id_filter_is_always_green()
    {
        var verdict = Evaluate("id:7", []);

        verdict.Level.ToString().Should().Be("Green");
        verdict.Reason.Should().Contain("primary key");
        verdict.Suggestion.Should().BeNull();
    }

    [Fact]
    public void A_duplicated_column_that_leads_an_index_is_green()
    {
        var verdict = Evaluate("email = bob@example.com", [EmailIndex]);

        verdict.Level.ToString().Should().Be("Green");
        verdict.Reason.Should().Contain("email");
    }

    [Fact]
    public void A_duplicated_column_that_only_trails_an_index_is_amber()
    {
        var verdict = Evaluate("ordercount = 3", [CompositeIndex]);

        verdict.Level.ToString().Should().Be("Amber");
        verdict.Reason.Should().Contain("does not lead");
    }

    [Fact]
    public void A_duplicated_column_with_no_index_is_red_and_suggests_one()
    {
        var verdict = Evaluate("email = bob@example.com", [PrimaryKey]);

        verdict.Level.ToString().Should().Be("Red");
        verdict.Suggestion.Should().Be("options.Schema.For<SqlTestCustomer>().Index(x => x.Email);");
    }

    [Fact]
    public void A_property_that_is_only_in_the_json_is_red_and_suggests_duplicating_it()
    {
        var verdict = Evaluate("profile.nickname = bob", [PrimaryKey]);

        verdict.Level.ToString().Should().Be("Red");
        verdict.Reason.Should().Contain("only in the JSON");
        verdict.Suggestion.Should().Be("options.Schema.For<SqlTestCustomer>().Duplicate(x => x.Profile.Nickname);");
    }

    /// <summary>
    /// A GIN index on <c>data</c> serves <c>@&gt;</c> and nothing else. Saying otherwise would be the most
    /// misleading thing this advisor could do.
    /// </summary>
    [Fact]
    public void A_gin_index_does_not_make_a_json_path_comparison_green()
    {
        var verdict = Evaluate("profile.nickname = bob", [GinIndex]);

        verdict.Level.ToString().Should().Be("Red");
        verdict.Reason.Should().Contain("containment (@>)");
    }

    [Fact]
    public void A_computed_index_on_the_same_path_is_green()
    {
        var verdict = Evaluate("Profile.Nickname = bob", [ComputedNicknameIndex]);

        verdict.Level.ToString().Should().Be("Green");
        verdict.Reason.Should().Contain("computed index");
    }

    /// <summary>
    /// A duplicated field and a computed index on the same property are different things: the filter runs
    /// against the <em>column</em>, and an index on <c>data -&gt; 'Address' -&gt;&gt; 'City'</c> cannot
    /// serve it. Calling that green would be advice that makes the page slower.
    /// </summary>
    [Fact]
    public void A_computed_index_does_not_cover_a_filter_that_runs_against_the_duplicated_column()
    {
        var computedOnCity =
            "CREATE INDEX i ON studio_sql.mt_doc_sqltestcustomer USING btree (((data -> 'Address' ->> 'City')))";

        Evaluate("Address.City = Helsinki", [computedOnCity]).Level.ToString().Should().Be("Red");
    }

    [Fact]
    public void Containment_is_green_with_a_gin_index_and_red_without_one()
    {
        Evaluate("""@> {"vip":true}""", [GinIndex]).Level.ToString().Should().Be("Green");

        var without = Evaluate("""@> {"vip":true}""", [PrimaryKey]);

        without.Level.ToString().Should().Be("Red");
        without.Suggestion.Should().Be("options.Schema.For<SqlTestCustomer>().GinIndexJsonData();");
    }

    [Fact]
    public void Free_text_is_always_red_and_offers_nothing()
    {
        var verdict = Evaluate("urgent", [GinIndex, EmailIndex, PrimaryKey]);

        verdict.Level.ToString().Should().Be("Red");
        verdict.Suggestion.Should().BeNull("no index serves a leading-wildcard match over the whole document");
    }

    [Fact]
    public void A_contains_match_on_a_duplicated_column_is_amber_and_names_the_only_index_that_would_help()
    {
        var verdict = Evaluate("email ~ example", [EmailIndex]);

        verdict.Level.ToString().Should().Be("Amber");
        verdict.Reason.Should().Contain("pg_trgm");
    }

    [Fact]
    public void A_trigram_index_makes_a_contains_match_green()
    {
        var trigram =
            "CREATE INDEX mt_doc_sqltestcustomer_idx_email_trgm ON studio_sql.mt_doc_sqltestcustomer USING gin (email gin_trgm_ops)";

        Evaluate("email ~ example", [trigram]).Level.ToString().Should().Be("Green");
    }

    [Fact]
    public void A_contains_match_on_a_json_only_property_is_red()
    {
        Evaluate("profile.nickname ~ bo", [GinIndex]).Level.ToString().Should().Be("Red");
    }

    [Fact]
    public void The_tenant_filter_is_green_when_tenant_id_leads_the_primary_key()
    {
        Evaluate("tenant:acme", [PrimaryKey]).Level.ToString().Should().Be("Green");
    }

    [Fact]
    public void The_deleted_filter_suggests_the_soft_delete_index_when_nothing_covers_it()
    {
        var verdict = Evaluate("is:deleted", [PrimaryKey]);

        verdict.Level.ToString().Should().Be("Red");
        verdict.Suggestion.Should().Be("options.Schema.For<SqlTestCustomer>().SoftDeletedWithIndex();");
    }

    [Fact]
    public void Sorting_by_last_modified_without_an_index_suggests_IndexLastModified()
    {
        var verdict = IndexAdvisor.EvaluateSort(
            SqlTestTables.FullyFeatured(),
            [new PostgresIndex("pk", PrimaryKey)],
            new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified));

        verdict.Level.ToString().Should().Be("Red");
        verdict.Suggestion.Should().Be("options.Schema.For<SqlTestCustomer>().IndexLastModified();");
    }

    [Fact]
    public void Sorting_by_id_is_green()
    {
        IndexAdvisor.EvaluateSort(SqlTestTables.FullyFeatured(), [], DocumentColumn.ById)
            .Level.ToString().Should().Be("Green");
    }

    [Fact]
    public void The_worst_verdict_wins_for_the_search_as_a_whole()
    {
        var advice = IndexAdvisor.Evaluate(
            SqlTestTables.FullyFeatured(),
            [new PostgresIndex("pk", PrimaryKey), new PostgresIndex("email", EmailIndex)],
            SearchGrammar.Parse("id:7 email = bob urgent").Predicates);

        advice.Predicates.Select(x => x.Verdict.Level.ToString()).Should().Equal("Green", "Green", "Red");
        advice.Worst.ToString().Should().Be("Red");
    }

    [Fact]
    public void The_sort_verdict_counts_towards_the_worst_one()
    {
        var advice = IndexAdvisor.Evaluate(
            SqlTestTables.FullyFeatured(),
            [new PostgresIndex("pk", PrimaryKey)],
            SearchGrammar.Parse("id:7").Predicates,
            new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified));

        advice.Worst.ToString().Should().Be("Red");
        advice.Sort.Should().NotBeNull();
    }

    [Fact]
    public void An_empty_search_on_a_table_with_no_indexes_is_green()
    {
        IndexAdvisor.Evaluate(SqlTestTables.MetadataLess(), [], []).Worst.ToString().Should().Be("Green");
    }

    [Theory]
    [InlineData("CREATE INDEX i ON t USING btree (email)", "email")]
    [InlineData("CREATE INDEX i ON t USING btree (email, name)", "email")]
    [InlineData("CREATE INDEX i ON t USING btree (\"Email\")", "email")]
    [InlineData("CREATE INDEX i ON t USING btree (email DESC)", "email")]
    [InlineData("CREATE INDEX i ON t USING gin (data jsonb_path_ops)", "data")]
    public void The_leading_column_is_read_out_of_the_definition(string definition, string expected) =>
        IndexAdvisor.LeadingColumn(definition.ToLowerInvariant()).Should().Be(expected);

    private static IndexVerdict Evaluate(string search, string[] definitions)
    {
        var indexes = definitions.Select((x, i) => new PostgresIndex("index_" + i, x)).ToArray();

        return IndexAdvisor.Evaluate(
            SqlTestTables.FullyFeatured(), indexes, SearchGrammar.Parse(search).Predicates).Predicates.Single().Verdict;
    }
}
