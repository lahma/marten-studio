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

    /// <summary>
    /// A member a metadata column stores gets index advice, never "duplicate it" — because Marten would
    /// discard that.
    /// </summary>
    /// <remarks>
    /// <c>Duplicate(x =&gt; x.Version)</c> against a <c>[Version]</c> member does not produce a second
    /// column: Marten re-points that very field at <c>mt_version</c> and marks it search-only. Advice a
    /// person can follow and then find nothing changed is worse than no advice, and this screen's only
    /// claim is that its advice is true.
    /// </remarks>
    [Fact]
    public void A_member_a_metadata_column_stores_is_advised_to_index_that_column_not_to_duplicate_it()
    {
        var value = Guid.NewGuid();

        var verdict = IndexAdvisor.Evaluate(
                SqlTestTables.Versioned(),
                [],
                SearchGrammar.Parse($"version = {value:D}").Predicates)
            .Predicates.Single().Verdict;

        verdict.Level.ToString().Should().Be("Red");
        verdict.Reason.Should().Contain("mt_version is where Marten stores Version");
        verdict.Reason.Should().NotContain("duplicated field");
        verdict.Suggestion.Should().Be("options.Schema.For<SqlTestVersionedNote>().Index(x => x.Version);");
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

    /// <summary>
    /// Sorting by <c>mt_last_modified</c> is red, and says both what fixes it and why the fix is a
    /// declaration rather than a <c>create index</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Index(x =&gt; …)</c> takes a member expression on the document and so cannot name a metadata
    /// column; <c>IndexLastModified()</c> is the one Marten ships for this column, and it is better advice
    /// than the equivalent DDL rather than merely more convenient. An index the configuration does not ask
    /// for is dropped by the next apply — <c>AutoCreate.CreateOrUpdate</c> is not additive, and Weasel's
    /// <c>TableDelta.WriteUpdate</c> writes <c>drop index</c> for every physical index that is not in the
    /// expected table's list. So a person who follows a raw-DDL suggestion loses the index the first time
    /// anybody applies a migration, and never finds out why the page got slow again.
    /// </para>
    /// </remarks>
    [Fact]
    public void Sorting_by_last_modified_without_an_index_suggests_IndexLastModified()
    {
        var verdict = IndexAdvisor.EvaluateSort(
            SqlTestTables.FullyFeatured(),
            [new PostgresIndex("pk", PrimaryKey)],
            new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified));

        verdict.Level.ToString().Should().Be("Red");
        verdict.Suggestion.Should().Be("options.Schema.For<SqlTestCustomer>().IndexLastModified();");
        verdict.Reason.Should().Contain("CreateOrUpdate is not additive")
            .And.Contain("dropped by the next apply");
    }

    /// <summary>An index that leads with the column turns the same sort green and drops the advice.</summary>
    [Fact]
    public void Sorting_by_last_modified_with_an_index_on_it_is_green()
    {
        const string LastModifiedIndex =
            "CREATE INDEX mt_doc_sqltestcustomer_idx_mt_last_modified ON studio_sql.mt_doc_sqltestcustomer " +
            "USING btree (mt_last_modified)";

        var verdict = IndexAdvisor.EvaluateSort(
            SqlTestTables.FullyFeatured(),
            [new PostgresIndex("lm", LastModifiedIndex)],
            new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified));

        verdict.Level.ToString().Should().Be("Green");
        verdict.Suggestion.Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------
    // The subclass verdict (P2-perf deliverable 5)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A subclass filter on a migrated hierarchy is green, because Marten does index <c>mt_doc_type</c>.
    /// </summary>
    /// <remarks>
    /// <c>Marten.Storage.DocumentTable</c>'s constructor runs
    /// <c>if (mapping.IsHierarchy()) { Indexes.Add(new DocumentIndex(_mapping, SchemaConstants.DocumentTypeColumn)); … }</c>
    /// — checked against the 9.35.0 tag on 2026-09-14, and asserted against a real migrated schema by
    /// <c>DocumentSubclassIndexLiveTests</c>. The advisor's own remark used to say the opposite, which
    /// would have had somebody go and create an index Marten had already made.
    /// </remarks>
    [Fact]
    public void A_subclass_filter_is_green_on_a_hierarchy_Marten_has_migrated()
    {
        const string DocTypeIndex =
            "CREATE INDEX mt_doc_sqltestcustomer_idx_mt_doc_type ON studio_sql.mt_doc_sqltestcustomer " +
            "USING btree (mt_doc_type)";

        var verdict = IndexAdvisor.Evaluate(
            SqlTestTables.Hierarchy(),
            [new PostgresIndex("pk", PrimaryKey), new PostgresIndex("doctype", DocTypeIndex)],
            new DocumentPredicate.SubclassIs("sqltestvipcustomer"));

        verdict.Level.ToString().Should().Be("Green");
        verdict.Reason.Should().Contain("mt_doc_type");
    }

    /// <summary>
    /// The same filter is amber when the index is missing, and says that means unapplied drift.
    /// </summary>
    /// <remarks>
    /// Amber rather than red because the column is on every row of a table the page was going to read
    /// anyway — but the wording matters: this is not "Marten does not index this", which was the old text
    /// and was simply untrue. It is "Marten declares this index and your database does not have it", which
    /// is a fact somebody can act on from the Schema screen.
    /// </remarks>
    [Fact]
    public void A_subclass_filter_on_an_unmigrated_hierarchy_is_amber_and_says_the_index_is_missing()
    {
        var verdict = IndexAdvisor.Evaluate(
            SqlTestTables.Hierarchy(),
            [new PostgresIndex("pk", PrimaryKey)],
            new DocumentPredicate.SubclassIs("sqltestvipcustomer"));

        verdict.Level.ToString().Should().Be("Amber");
        verdict.Reason.Should().Contain("declares an index")
            .And.Contain("migration has not been applied");
        verdict.Reason.Should().NotContain("creates no index", "Marten does create one - that was the bug");
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
