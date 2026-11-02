using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// P15 to P20 (0.10-A, issue #28) over the corpus: each fires on the plan - with the facts - that has its shape, stays quiet on the
/// same plan with healthy facts and on its quiet fixture, and the four that need the database's facts find nothing, and are listed as
/// not judged, without them. The facts are built by hand: what the reads return is the live suite's.
/// </summary>
public class PlanFactsInsightsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<PlanInsightFinding> Evaluate(string fixture, PlanFacts? facts, StudioText? text = null) =>
        PlanInsights.Evaluate(PlanFixtures.Plan(fixture), text ?? StudioText.Marten, facts);

    private static IReadOnlyList<PlanInsightFinding> Of(string code, string fixture, PlanFacts? facts) =>
        [.. Evaluate(fixture, facts).Where(x => x.Insight.Code == code)];

    private static PlanFacts Facts(params PlanRelationFacts[] relations) => new(Now, relations, []);

    private static PlanRelationFacts Relation(string schema, string name) => new(schema, name, PlanRelationState.Read)
    {
        Kind = "r",
        EstimatedRows = 100_000,
        LiveTuples = 100_000,
        LastAnalyze = Now.AddDays(-1),
        ModifiedSinceAnalyze = 5,
    };

    private static PlanIndexFact Index(string name, params string?[] keys) =>
        new(name, keys, false, false, true, false, keys.Any(static x => x is null), "btree", 8_192_000, 12, Now.AddHours(-3));

    private static PlanColumnFact Column(string name, string type) => new(name, type, true, 0, 100, 0.1);

    // ---- P15 -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void P15_fires_on_a_misestimate_whose_table_has_changed_far_more_than_a_tenth_since_its_last_analyze()
    {
        PlanFacts facts = Facts(Relation("public", "orders") with { EstimatedRows = 1_000, ModifiedSinceAnalyze = 200_000, LastAnalyze = Now.AddDays(-30) });

        PlanInsightFinding finding = Of("P15", "p15-fires", facts).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("public.orders").And.Contain("200,000 rows changed").And.Contain("30 days ago").And.Contain("planned for 100 rows a loop and returned 50,000");
        finding.Remedy.Should().Contain("ANALYZE \"public\".\"orders\";").And.Contain("SHARE UPDATE EXCLUSIVE").And.Contain("autovacuum_analyze_scale_factor")
            .And.Contain("the studio never runs it");
        finding.RelatedPage.Should().Be(PlanInsightPage.Maintenance);
        finding.NodeId.Should().Be(0);
    }

    [Fact]
    public void P15_fires_on_a_table_that_has_never_been_analyzed()
    {
        PlanFacts facts = Facts(Relation("public", "orders") with { EstimatedRows = null, LiveTuples = null, LastAnalyze = null, LastAutoAnalyze = null, ModifiedSinceAnalyze = null });

        Of("P15", "p15-fires", facts).Should().ContainSingle().Which.Message.Should().Contain("has never been analyzed");
    }

    [Fact]
    public void P15_is_quiet_when_the_statistics_are_current_the_relation_is_withheld_or_the_plan_was_not_run()
    {
        // 9,999 of 100,000 rows changed: just under a tenth.
        Of("P15", "p15-fires", Facts(Relation("public", "orders") with { ModifiedSinceAnalyze = 9_999 })).Should().BeEmpty();

        // A withheld relation is nothing: the visitor is told neither its name nor its facts.
        Of("P15", "p15-fires", Facts(new PlanRelationFacts("hr", "orders", PlanRelationState.Withheld))).Should().BeEmpty();

        // Facts for another relation.
        Of("P15", "p15-fires", Facts(Relation("public", "customers") with { ModifiedSinceAnalyze = 200_000 })).Should().BeEmpty();
    }

    [Fact]
    public void P15_names_the_exact_tenth_boundary()
    {
        Of("P15", "p15-fires", Facts(Relation("public", "orders") with { ModifiedSinceAnalyze = 10_000 })).Should().ContainSingle("a tenth of the rows is the threshold, inclusive");
    }

    // ---- P16 -----------------------------------------------------------------------------------------------------------------

    private static PlanFacts AddressFacts(params PlanExtendedStatistic[] extended) =>
        Facts(Relation("public", "addresses") with { Columns = [Column("city", "text"), Column("postcode", "text")], ExtendedStatistics = extended });

    [Fact]
    public void P16_fires_on_a_filter_comparing_two_columns_with_no_extended_statistics_and_offers_CREATE_STATISTICS()
    {
        PlanInsightFinding finding = Of("P16", "p16-fires", AddressFacts()).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("compares city, postcode together").And.Contain("planned for 5 rows a loop and returned 4,000");
        finding.Remedy.Should().Contain("CREATE STATISTICS \"public\".\"addresses_city_postcode_stats\" (dependencies, ndistinct, mcv) ON \"city\", \"postcode\" FROM \"public\".\"addresses\";")
            .And.Contain("ANALYZE \"public\".\"addresses\";").And.Contain("the studio never runs it");
    }

    [Fact]
    public void P16_is_quiet_with_extended_statistics_over_two_of_the_columns_with_OR_and_for_one_column()
    {
        Of("P16", "p16-fires", AddressFacts(new PlanExtendedStatistic("addresses_stats", ["city", "postcode"], ["d", "f", "m"]))).Should().BeEmpty();
        Of("P16", "p16-quiet", AddressFacts()).Should().BeEmpty("OR joins the columns, they are not compared together");
        Of("P16", "p15-fires", AddressFacts()).Should().BeEmpty("one column");
    }

    [Fact]
    public void P16_without_a_nameable_statistics_object_still_names_the_columns_and_gives_no_statement()
    {
        string table = new('t', 60);
        QueryPlan plan = QueryPlanReader.Read(PlanFixtures.Get("p16-fires").Replace("addresses", table, StringComparison.Ordinal)).Plan!;
        PlanFacts facts = Facts(Relation("public", table) with { Columns = [Column("city", "text"), Column("postcode", "text")] });

        PlanInsightFinding finding = PlanInsights.Evaluate(plan, StudioText.Marten, facts).Single(static x => x.Insight.Code == "P16");

        finding.Remedy.Should().Contain("(CREATE STATISTICS) over city, postcode").And.NotContain("CREATE STATISTICS \"");
    }

    // ---- P17 -----------------------------------------------------------------------------------------------------------------

    private static PlanFacts UserFacts(params PlanIndexFact[] indexes) =>
        Facts(Relation("public", "users") with { Columns = [Column("email", "text")], Indexes = indexes });

    [Fact]
    public void P17_fires_on_a_function_around_a_column_that_leads_an_index()
    {
        PlanInsightFinding finding = Of("P17", "p17-fires", UserFacts(Index("users_email_idx", "email"))).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("lower()").And.Contain("public.users.email").And.Contain("users_email_idx");
        finding.Remedy.Should().Contain("expression index").And.Contain("lower(email)");
    }

    [Fact]
    public void P17_is_quiet_without_an_index_led_by_the_column_with_an_invalid_one_and_when_the_filter_wraps_nothing()
    {
        Of("P17", "p17-fires", UserFacts()).Should().BeEmpty("no index to defeat");
        Of("P17", "p17-fires", UserFacts(Index("users_created_idx", "created_at", "email"))).Should().BeEmpty("the column does not lead the index");
        Of("P17", "p17-fires", UserFacts(Index("users_email_idx", "email") with { IsValid = false })).Should().BeEmpty("an invalid index serves nothing either way");
        Of("P17", "p17-quiet", UserFacts(Index("users_email_idx", "email"))).Should().BeEmpty("the column is compared as it is, and an Index Cond is never read");
    }

    [Theory]
    [InlineData("date", "timestamp without time zone", true, "a cast to date")]
    [InlineData("text", "character varying(255)", false, null)]
    [InlineData("character varying", "text", false, null)]
    [InlineData("bigint", "integer", true, "a cast to bigint")]
    [InlineData("integer", "integer", false, null)]
    public void A_cast_defeats_an_index_only_when_it_changes_the_type(string cast, string columnType, bool defeats, string? words)
    {
        PlanConditionColumn reference = new(null, "created_at", cast, null);

        PlanInsights.WrapperText(reference, columnType).Should().Be(words);
        (PlanInsights.WrapperText(reference, columnType) is not null).Should().Be(defeats);
    }

    [Fact]
    public void P17_reads_a_cast_of_an_indexed_column()
    {
        const string filter = "((created_at)::date = '2026-11-01'::date)";
        QueryPlan plan = QueryPlanReader.Read(PlanFixtures.Get("p17-fires").Replace("(lower((email)::text) = 'ann@example.com'::text)", filter, StringComparison.Ordinal)
            .Replace("\"users\"", "\"events\"", StringComparison.Ordinal)).Plan!;
        PlanFacts facts = Facts(Relation("public", "events") with { Columns = [Column("created_at", "timestamp without time zone")], Indexes = [Index("events_created_idx", "created_at")] });

        PlanInsightFinding finding = PlanInsights.Evaluate(plan, StudioText.Marten, facts).Single(static x => x.Insight.Code == "P17");

        finding.Message.Should().Contain("a cast to date").And.Contain("public.events.created_at");
        finding.Remedy.Should().Contain("Cast the other side").And.Contain("timestamp without time zone");
    }

    // ---- P18 -----------------------------------------------------------------------------------------------------------------

    private static PlanFacts PartitionFacts(int total)
    {
        PlanPartitionFact partition = new("public", "events", total, ["created_at"]);

        return Facts([.. Enumerable.Range(1, 6).Select(i => Relation("public", "events_2026_" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture)) with { Partition = partition })]);
    }

    [Fact]
    public void P18_fires_when_most_of_a_partitioned_tables_partitions_are_read_and_none_removed()
    {
        PlanInsightFinding finding = Of("P18", "p18-fires", PartitionFacts(8)).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("reads 6 of the 8 partitions of public.events").And.Contain("no Subplans Removed").And.Contain("nothing in the statement filters on the partition key (created_at)");
        finding.Remedy.Should().Contain("Add a condition on the partition key (created_at)");
    }

    [Fact]
    public void P18_is_quiet_when_pruning_removed_some_when_few_of_the_partitions_are_read_and_for_a_small_scan()
    {
        Of("P18", "p18-quiet", PartitionFacts(8)).Should().BeEmpty("Subplans Removed says run-time pruning worked");
        Of("P18", "p18-fires", PartitionFacts(12)).Should().BeEmpty("six of twelve is not more than half");
        Of("P18", "p18-fires", Facts(Relation("public", "events_2026_1"))).Should().BeEmpty("no partition facts, nothing to judge");

        QueryPlan three = QueryPlanReader.Read(PlanFixtures.Get("p18-fires")).Plan!;
        three.Nodes.Count(static x => x.NodeType == "Append").Should().Be(1);
    }

    [Fact]
    public void P18_says_when_the_statement_does_filter_on_the_key_in_a_form_pruning_cannot_use()
    {
        string json = PlanFixtures.Get("p18-fires").Replace(
            "\"Output\": [",
            "\"Filter\": \"(date_trunc('day'::text, created_at) = '2026-01-01'::date)\",\n        \"Output\": [",
            StringComparison.Ordinal);
        QueryPlan plan = QueryPlanReader.Read(json).Plan!;

        PlanInsightFinding finding = PlanInsights.Evaluate(plan, StudioText.Marten, PartitionFacts(8)).Single(static x => x.Insight.Code == "P18");

        finding.Message.Should().Contain("filters on the partition key (created_at), but not in a form Postgres could prune by");
    }

    // ---- P19 and P20: the plan alone -----------------------------------------------------------------------------------------

    [Fact]
    public void P19_fires_on_a_function_scan_on_the_default_estimate_that_returned_something_else_and_needs_no_facts()
    {
        PlanInsightFinding finding = Of("P19", "p19-fires", null).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("1,000 rows").And.Contain("returned 50");
        finding.Remedy.Should().Contain("ROWS");
    }

    [Fact]
    public void P19_is_quiet_on_an_estimate_somebody_set_and_on_a_guess_that_was_right()
    {
        Of("P19", "p19-quiet", null).Should().BeEmpty();

        string right = PlanFixtures.Get("p19-fires").Replace("\"Actual Rows\": 50.0", "\"Actual Rows\": 1000.0", StringComparison.Ordinal);
        PlanInsights.Evaluate(QueryPlanReader.Read(right).Plan!, StudioText.Marten).Should().NotContain(static x => x.Insight.Code == "P19");
    }

    [Fact]
    public void P20_fires_when_the_triggers_took_a_third_of_the_run_and_names_the_slowest()
    {
        PlanInsightFinding finding = Of("P20", "p20-fires", null).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("800 ms").And.Contain("80%").And.Contain("orders_audit on orders (700 ms, 1,000 calls)").And.Contain("RI_ConstraintTrigger_c_1234");
        finding.NodeId.Should().Be(0);
    }

    [Fact]
    public void P20_is_quiet_when_the_triggers_were_a_small_share_and_on_a_plan_that_was_not_run()
    {
        Of("P20", "p20-quiet", null).Should().BeEmpty();

        QueryPlan plain = QueryPlanReader.Read(PlanFixtures.Get("p20-fires").Replace("\"Execution Time\": 1000.0", "\"Execution Time\": null", StringComparison.Ordinal)).Plan!;
        PlanInsights.Evaluate(plain, StudioText.Marten).Should().NotContain(static x => x.Insight.Code == "P20");
    }

    // ---- Without facts -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("p15-fires")]
    [InlineData("p16-fires")]
    [InlineData("p17-fires")]
    [InlineData("p18-fires")]
    public void Without_facts_the_four_that_need_them_find_nothing_and_are_listed_as_not_judged(string fixture)
    {
        QueryPlan plan = PlanFixtures.Plan(fixture);

        PlanInsights.Evaluate(plan, StudioText.Marten).Select(static x => x.Insight.Code).Should().NotContain(["P15", "P16", "P17", "P18"]);
        PlanInsights.NotJudgedWithoutFacts(plan, null).Select(static x => x.Code).Should().Equal("P15", "P16", "P17", "P18");
        PlanInsights.NotJudgedWithoutFacts(plan, Facts()).Should().BeEmpty("with facts, they are judged");
    }

    [Fact]
    public void A_plain_plan_lists_the_analyze_only_insights_once_and_the_facts_only_ones_once()
    {
        QueryPlan plan = QueryPlanReader.Read(PlanCorpusTests.StripActuals(PlanFixtures.Get("p17-fires"))).Plan!;

        plan.Analyzed.Should().BeFalse();
        PlanInsights.NotJudged(plan).Select(static x => x.Code).Should().Contain(["P15", "P16", "P20"]);
        PlanInsights.NotJudgedWithoutFacts(plan, null).Select(static x => x.Code).Should().Equal("P17", "P18");
    }

    [Fact]
    public void Every_new_insight_has_its_fixtures_and_P19_and_P20_are_not_facts_insights()
    {
        PlanFixtures.Names.Should().Contain(["p15-fires", "p16-fires", "p16-quiet", "p17-fires", "p17-quiet", "p18-fires", "p18-quiet", "p19-fires", "p19-quiet", "p20-fires", "p20-quiet"]);
        PlanInsights.All.Where(static x => x.NeedsFacts).Select(static x => x.Code).Should().Equal("P15", "P16", "P17", "P18");
        PlanInsights.All.Skip(14).Take(7).Select(static x => x.Code).Should().Equal("P15", "P16", "P17", "P18", "P19", "P20", "P21");
    }

    [Fact]
    public void In_Postgres_Studios_words_no_finding_of_the_six_speaks_of_a_store()
    {
        (string Fixture, PlanFacts Facts)[] cases =
        [
            ("p15-fires", Facts(Relation("public", "orders") with { ModifiedSinceAnalyze = 200_000 })),
            ("p16-fires", AddressFacts()),
            ("p17-fires", UserFacts(Index("users_email_idx", "email"))),
            ("p18-fires", PartitionFacts(8)),
            ("p19-fires", Facts()),
            ("p20-fires", Facts()),
        ];

        List<PlanInsightFinding> findings = [.. cases.SelectMany(x => Evaluate(x.Fixture, x.Facts, PostgresStudioText.Instance)).Where(static x => x.Insight.Code is "P15" or "P16" or "P17" or "P18" or "P19" or "P20")];
        findings.Select(static x => x.Insight.Code).Distinct().Should().Equal("P15", "P16", "P17", "P18", "P19", "P20");

        foreach (PlanInsightFinding finding in findings)
        {
            foreach (string word in (string[]) ["Marten", "store", "StoreOptions", "document", "tenant"])
            {
                finding.Message.Should().NotContain(word, finding.Insight.Code);
                finding.Remedy.Should().NotContain(word, finding.Insight.Code);
            }
        }
    }

    [Fact]
    public void The_remedies_are_the_same_in_both_studios_words_where_no_studio_differs()
    {
        PlanFacts facts = Facts(Relation("public", "orders") with { ModifiedSinceAnalyze = 200_000 });

        Evaluate("p15-fires", facts, StudioText.Marten).Single(static x => x.Insight.Code == "P15").Remedy
            .Should().Be(Evaluate("p15-fires", facts, PostgresStudioText.Instance).Single(static x => x.Insight.Code == "P15").Remedy);
    }
}
