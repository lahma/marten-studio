using System.Text.Json;
using System.Text.Json.Serialization;

using Marten;

using MartenStudio.Services.Query;
using MartenStudio.Tests.Conventions;
using MartenStudio.Tests.Sql;

using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Statements;

/// <summary>A document the plan insights are judged against: a plain member, a nested one and a number.</summary>
public sealed class PlanDoc
{
    public Guid Id { get; set; }

    public string Color { get; set; } = string.Empty;

    public int Number { get; set; }

    public PlanAddress Address { get; set; } = new();

    public List<string> Tags { get; set; } = [];
}

/// <summary>A nested member of <see cref="PlanDoc" />.</summary>
public sealed class PlanAddress
{
    public string City { get; set; } = string.Empty;
}

/// <summary>A document with a GIN index on its data.</summary>
public sealed class GinDoc
{
    public Guid Id { get; set; }

    public string Color { get; set; } = string.Empty;
}

/// <summary>A conjoined-tenant document, with a computed index.</summary>
public sealed class TenantPlanDoc
{
    public Guid Id { get; set; }

    public string Color { get; set; } = string.Empty;
}

/// <summary>A soft-deleted document, with a computed index.</summary>
public sealed class SoftPlanDoc
{
    public Guid Id { get; set; }

    public string Color { get; set; } = string.Empty;
}

/// <summary>A document two of whose members are written under one key.</summary>
public sealed class ClashingDoc
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("Name")]
    public string Alias { get; set; } = string.Empty;

    [JsonPropertyName("shade")]
    public string Colour { get; set; } = string.Empty;

    [JsonIgnore]
    public string Secret { get; set; } = string.Empty;
}

/// <summary>
/// 1.0-P3C: Marten Studio's plan insights P26 to P28 - judged against the document tables a store declares, from plans in the shape
/// Marten's own statements have, with no Postgres and no store connection.
/// </summary>
public class MartenPlanInsightsTests
{
    private const string Schema = SqlTestStore.Schema;

    private static MartenPlanTable Table<T>(Action<StoreOptions>? configure = null, JsonNamingPolicy? naming = null) =>
        MartenPlanTable.From(SqlTestStore.DocumentType<T>(configure), naming);

    private static string Plain(string root) => "[{\"Plan\": " + root + "}]";

    private static string Analyzed(string root) => "[{\"Plan\": " + root + ", \"Planning Time\": 0.1, \"Execution Time\": 1.0}]";

    private static QueryPlan Fixture(string name) =>
        QueryPlanReader.Read(File.ReadAllText(RepositoryRoot.Combine("tests", "MartenStudio.Tests", "Statements", name + ".json"))).Plan
        ?? throw new InvalidOperationException(name + " is not a plan");

    private static string Seq(string table, string filter, bool analyzed = true, int kept = 480, int removed = 99_520, string schema = Schema) =>
        (analyzed ? Analyzed : (Func<string, string>) Plain)(
            "{\"Node Type\": \"Seq Scan\", \"Schema\": \"" + schema + "\", \"Relation Name\": \"" + table + "\", \"Alias\": \"d\", \"Plan Rows\": 500, \"Filter\": \""
            + filter.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            + (analyzed ? ", \"Actual Rows\": " + kept + ", \"Actual Loops\": 1, \"Rows Removed by Filter\": " + removed : string.Empty) + "}");

    private static QueryPlan Plan(string json) =>
        QueryPlanReader.Read(json).Plan ?? throw new InvalidOperationException("not a plan");

    private static List<PlanInsightFinding> Findings(QueryPlan plan, params MartenPlanTable[] tables) =>
        [.. MartenPlanInsights.Evaluate(plan, null, tables).Findings];

    // -----------------------------------------------------------------------------------------------
    // P26
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void P26_fires_on_a_sequential_scan_filtering_a_json_member_and_writes_the_Duplicate_line()
    {
        QueryPlan plan = Fixture("p26-fires");

        PlanInsightFinding finding = Findings(plan, Table<PlanDoc>()).Should().ContainSingle().Subject;

        finding.Insight.Should().BeSameAs(MartenPlanInsights.JsonFilterWithoutIndex);
        finding.Insight.Code.Should().Be("P26");
        finding.Remedy.Should().Contain("options.Schema.For<PlanDoc>().Duplicate(x => x.Color);");
        finding.Remedy.Should().Contain("drops an index its configuration does not declare");
        finding.Message.Should().Contain("PlanDoc documents").And.Contain("Color").And.Contain("100,000").And.Contain("480");
        finding.Remedy.Should().NotContain("CREATE INDEX", "a remedy is a StoreOptions line, never DDL");
    }

    [Fact]
    public void P26_fires_on_a_plain_plan_too()
    {
        MartenPlanInsights.Evaluate(Plan(Seq("mt_doc_plandoc", "((d.data ->> 'Color'::text) = $1)", analyzed: false)), null, [Table<PlanDoc>()])
            .Findings.Should().ContainSingle(static x => x.Insight.Code == "P26");
    }

    [Fact]
    public void P26_maps_a_nested_path_back_to_its_members()
    {
        QueryPlan plan = Plan(Seq("mt_doc_plandoc", "(((d.data -> 'Address'::text) ->> 'City'::text) = $1)"));

        Findings(plan, Table<PlanDoc>()).Single().Remedy.Should().Contain("Duplicate(x => x.Address.City);");
    }

    [Fact]
    public void P26_reads_the_JSON_name_through_the_serializers_casing()
    {
        QueryPlan plan = Plan(Seq("mt_doc_plandoc", "((d.data ->> 'color'::text) = $1)"));

        Findings(plan, Table<PlanDoc>(naming: JsonNamingPolicy.CamelCase)).Single().Remedy.Should().Contain("Duplicate(x => x.Color);");

        // Under the default casing there is no member written as 'color': the studio does not guess one.
        PlanInsightFinding ambiguous = Findings(plan, Table<PlanDoc>()).Single();
        ambiguous.Remedy.Should().Contain("The JSON path color is not exactly one member of PlanDoc").And.NotContain("x.Color").And.NotContain("Duplicate(x =>");
    }

    [Fact]
    public void P26_names_the_JSON_path_when_two_members_are_written_under_one_key_or_none_is()
    {
        MartenPlanTable table = Table<ClashingDoc>();

        Findings(Plan(Seq("mt_doc_clashingdoc", "((d.data ->> 'Name'::text) = $1)")), table).Single().Remedy
            .Should().Contain("The JSON path Name is not exactly one member of ClashingDoc");

        // [JsonPropertyName] writes Colour as 'shade', and [JsonIgnore] leaves Secret out of the JSON altogether.
        Findings(Plan(Seq("mt_doc_clashingdoc", "((d.data ->> 'shade'::text) = $1)")), table).Single().Remedy
            .Should().Contain("Duplicate(x => x.Colour);");
        Findings(Plan(Seq("mt_doc_clashingdoc", "((d.data ->> 'Secret'::text) = $1)")), table).Single().Remedy
            .Should().Contain("The JSON path Secret is not exactly one member");
    }

    [Fact]
    public void P26_asks_for_a_computed_index_for_a_pattern_and_for_a_gin_index_for_containment()
    {
        Findings(Plan(Seq("mt_doc_plandoc", "((d.data ->> 'Color'::text) ~~ 'red%'::text)")), Table<PlanDoc>()).Single().Remedy
            .Should().Contain("Index(x => x.Color);").And.NotContain("Duplicate(x =>");

        Findings(Plan(Seq("mt_doc_plandoc", "(d.data @> '{\"Color\": \"red\"}'::jsonb)")), Table<PlanDoc>()).Single().Remedy
            .Should().Contain("options.Schema.For<PlanDoc>().GinIndexJsonData();");
    }

    [Fact]
    public void P26_does_not_offer_the_jsonb_path_ops_index_for_a_key_existence_test()
    {
        PlanInsightFinding finding = Findings(Plan(Seq("mt_doc_plandoc", "(d.data ? 'Color'::text)")), Table<PlanDoc>()).Single();

        finding.Message.Should().Contain("key-existence");
        finding.Remedy.Should().Contain("jsonb_path_ops").And.NotContain("options.Schema.For<PlanDoc>().GinIndexJsonData();");
    }

    [Fact]
    public void P26_is_quiet_when_the_store_declares_an_index_that_serves_the_filter()
    {
        QueryPlan plan = Plan(Seq("mt_doc_plandoc", "((d.data ->> 'Color'::text) = $1)"));

        Findings(plan, Table<PlanDoc>(static o => o.Schema.For<PlanDoc>().Duplicate(x => x.Color))).Should().NotContain(static x => x.Insight.Code == "P26");
        Findings(plan, Table<PlanDoc>(static o => o.Schema.For<PlanDoc>().Index(x => x.Color))).Should().NotContain(static x => x.Insight.Code == "P26");
        Findings(Plan(Seq("mt_doc_gindoc", "(d.data @> $1)")), Table<GinDoc>(static o => o.Schema.For<GinDoc>().GinIndexJsonData()))
            .Should().NotContain(static x => x.Insight.Code == "P26");
    }

    [Fact]
    public void P26_is_quiet_for_a_filter_that_reads_no_json()
    {
        Findings(Plan(Seq("mt_doc_plandoc", "(d.id = $1)")), Table<PlanDoc>()).Should().BeEmpty();
        Findings(Plan(Seq("mt_doc_plandoc", "((d.data ->> $2) = $1)")), Table<PlanDoc>()).Should().BeEmpty("a key that is a placeholder names nothing");
    }

    [Fact]
    public void P26_on_an_indexed_scan_needs_the_filter_to_discard_most_rows_at_P10s_threshold()
    {
        static string Index(int kept, int removed) => Analyzed(
            "{\"Node Type\": \"Index Scan\", \"Schema\": \"" + Schema + "\", \"Relation Name\": \"mt_doc_plandoc\", \"Index Name\": \"mt_doc_plandoc_pkey\", "
            + "\"Index Cond\": \"(d.id > $1)\", \"Filter\": \"((d.data ->> 'Color'::text) = $2)\", \"Actual Rows\": " + kept + ", \"Actual Loops\": 1, \"Rows Removed by Filter\": " + removed + "}");

        MartenPlanTable table = Table<PlanDoc>();

        Findings(Plan(Index(1_000, 10_000)), table).Should().ContainSingle(static x => x.Insight.Code == "P26");
        Findings(Plan(Index(1_000, 9_999)), table).Should().BeEmpty("below P10's ten thousand rows");
        Findings(Plan(Index(1_001, 10_000)), table).Should().BeEmpty("fewer than ten discarded for each one kept");

        // A plain plan prints no counts, so an indexed scan cannot be judged by what it threw away.
        MartenPlanInsights.Evaluate(
            Plan(Plain("{\"Node Type\": \"Index Scan\", \"Schema\": \"" + Schema + "\", \"Relation Name\": \"mt_doc_plandoc\", \"Filter\": \"((d.data ->> 'Color'::text) = $2)\"}")),
            null,
            [table]).Findings.Should().BeEmpty();
    }

    [Fact]
    public void P26_to_P28_never_fire_for_a_table_the_store_does_not_declare_or_a_hidden_type()
    {
        QueryPlan plan = Plan(Seq("mt_doc_plandoc", "((d.data ->> 'Color'::text) = $1)"));

        // A hidden type has no MartenPlanTable at all (MartenPlanTables.Read leaves it out), so its table is any table.
        PlanContribution contribution = MartenPlanInsights.Evaluate(plan, null, [Table<GinDoc>()]);

        contribution.Findings.Should().BeEmpty();
        contribution.NotJudged.Select(static x => x.Code).Should().Equal("P26", "P27", "P28");
    }

    [Fact]
    public void A_relation_the_facts_withhold_is_never_judged_or_named()
    {
        QueryPlan plan = Fixture("p26-fires");
        PlanFacts facts = new(DateTimeOffset.UnixEpoch, [new PlanRelationFacts(string.Empty, string.Empty, PlanRelationState.Withheld)], [])
        {
            Requested = new Dictionary<PlanRelationRef, int> { [new PlanRelationRef(Schema, "mt_doc_plandoc")] = 0 },
        };

        PlanContribution contribution = MartenPlanInsights.Evaluate(plan, facts, [Table<PlanDoc>()]);

        contribution.Findings.Should().BeEmpty();
        contribution.NotJudged.Should().NotBeEmpty();
    }

    [Fact]
    public void A_pasted_plan_without_a_schema_is_not_judged_and_says_nothing_of_the_store()
    {
        // No VERBOSE: the scan names no schema, and an unqualified name would resolve through somebody's search_path.
        QueryPlan plan = Plan(Analyzed("{\"Node Type\": \"Seq Scan\", \"Relation Name\": \"mt_doc_plandoc\", \"Filter\": \"((data ->> 'Color'::text) = $1)\", \"Actual Rows\": 1, \"Actual Loops\": 1, \"Rows Removed by Filter\": 100000}"));

        PlanContribution contribution = MartenPlanInsights.Evaluate(plan, null, [Table<PlanDoc>()]);

        contribution.Findings.Should().BeEmpty();
        contribution.NotJudged.Select(static x => x.Code).Should().Equal("P26", "P27", "P28");
        contribution.NotJudged.Should().OnlyContain(static x => !x.Title.Contains("store", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_plan_that_reads_no_document_table_is_neither_judged_nor_listed()
    {
        QueryPlan plan = Plan(Analyzed("{\"Node Type\": \"Seq Scan\", \"Schema\": \"public\", \"Relation Name\": \"orders\", \"Filter\": \"(data ->> 'Color') = 'x'\", \"Actual Rows\": 1, \"Actual Loops\": 1, \"Rows Removed by Filter\": 100000}"));

        MartenPlanInsights.Evaluate(plan, null, [Table<PlanDoc>()]).Should().BeSameAs(PlanContribution.None);
        MartenPlanInsights.Evaluate(plan, null, null).Should().BeSameAs(PlanContribution.None);
    }

    [Fact]
    public void A_scope_whose_store_could_not_be_learned_is_not_judged()
    {
        PlanContribution contribution = MartenPlanInsights.Evaluate(Fixture("p26-fires"), null, null);

        contribution.Findings.Should().BeEmpty();
        contribution.NotJudged.Select(static x => x.Code).Should().Equal("P26", "P27", "P28");
    }

    [Fact]
    public void A_plain_plan_leaves_P28_to_the_list_of_insights_that_need_ANALYZE()
    {
        PlanContribution contribution = MartenPlanInsights.Evaluate(Plan(Seq("mt_doc_plandoc", "(d.id = $1)", analyzed: false)), null, null);

        contribution.NotJudged.Select(static x => x.Code).Should().Equal("P26", "P27");
        MartenPlanInsights.FilteredAfterIndex.NeedsAnalyze.Should().BeTrue();
    }

    // -----------------------------------------------------------------------------------------------
    // P27
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void P27_fires_on_a_sequential_scan_testing_containment_when_the_type_declares_a_GIN_index()
    {
        MartenPlanTable table = Table<GinDoc>(static o => o.Schema.For<GinDoc>().GinIndexJsonData());

        PlanInsightFinding finding = Findings(Fixture("p27-fires"), table).Should().ContainSingle().Subject;

        finding.Insight.Code.Should().Be("P27");
        finding.Message.Should().Contain("GIN index on data").And.Contain("gindoc_idx_data").And.Contain("40,000");
        finding.Remedy.Should().Contain("jsonb_path_ops").And.Contain("stale").And.Contain("small");
        finding.Remedy.Should().NotContain("CREATE INDEX");
    }

    [Fact]
    public void P27_is_quiet_without_a_declared_GIN_index_and_P26_speaks_instead()
    {
        List<PlanInsightFinding> findings = Findings(Fixture("p27-fires"), Table<GinDoc>());

        findings.Select(static x => x.Insight.Code).Should().Equal("P26");
    }

    [Fact]
    public void P27_counts_a_GIN_index_the_facts_list()
    {
        PlanFacts facts = new(
            DateTimeOffset.UnixEpoch,
            [
                new PlanRelationFacts(Schema, "mt_doc_gindoc", PlanRelationState.Read)
                {
                    Indexes = [new PlanIndexFact("by_data", ["data"], false, false, true, false, false, "gin", null, null, null)],
                },
            ],
            [])
        {
            Requested = new Dictionary<PlanRelationRef, int> { [new PlanRelationRef(Schema, "mt_doc_gindoc")] = 0 },
        };

        MartenPlanInsights.Evaluate(Fixture("p27-fires"), facts, [Table<GinDoc>()]).Findings.Select(static x => x.Insight.Code)
            .Should().Equal("P26", "P27");  // P26 too: the database has the index, but the store does not declare it, and a hand-made index is dropped by the next apply
    }

    [Fact]
    public void P27_is_quiet_for_an_indexed_scan_and_for_a_filter_without_containment()
    {
        MartenPlanTable table = Table<GinDoc>(static o => o.Schema.For<GinDoc>().GinIndexJsonData());

        Findings(Plan(Seq("mt_doc_gindoc", "((d.data ->> 'Color'::text) = $1)")), table).Select(static x => x.Insight.Code).Should().Equal("P26");
        Findings(Plan(Analyzed("{\"Node Type\": \"Bitmap Heap Scan\", \"Schema\": \"" + Schema + "\", \"Relation Name\": \"mt_doc_gindoc\", \"Recheck Cond\": \"(d.data @> $1)\", \"Actual Rows\": 5, \"Actual Loops\": 1}")), table)
            .Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------
    // P28
    // -----------------------------------------------------------------------------------------------

    private static void Tenanted(StoreOptions o) => o.Schema.For<TenantPlanDoc>().MultiTenanted().Index(x => x.Color);

    private static void Soft(StoreOptions o) => o.Schema.For<SoftPlanDoc>().SoftDeleted().Index(x => x.Color);

    [Fact]
    public void P28_fires_when_the_tenant_predicate_is_filtered_after_the_index_and_names_the_Marten_line()
    {
        PlanInsightFinding finding = Findings(Fixture("p28-fires"), Table<TenantPlanDoc>(Tenanted)).Should().ContainSingle().Subject;

        finding.Insight.Code.Should().Be("P28");
        finding.Message.Should().Contain("tenant_id").And.Contain("49,900").And.Contain("100 rows");
        finding.Remedy.Should().Contain("options.Schema.For<TenantPlanDoc>().StartIndexesByTenantId();");
        finding.Remedy.Should().Contain("drops an index its configuration does not declare").And.NotContain("CREATE INDEX");
    }

    [Fact]
    public void P28_offers_a_tenant_index_when_the_index_used_is_not_a_computed_one()
    {
        QueryPlan plan = Plan(P28("mt_doc_tenantplandoc", "mt_doc_tenantplandoc_pkey", "(d.tenant_id = 'acme'::text)"));

        Findings(plan, Table<TenantPlanDoc>(Tenanted)).Single().Remedy.Should().Contain("options.Schema.For<TenantPlanDoc>().IndexTenantId();");
    }

    [Fact]
    public void P28_fires_for_the_soft_delete_predicate_and_writes_a_partial_index_line_for_the_member_the_index_is_on()
    {
        QueryPlan plan = Plan(P28("mt_doc_softplandoc", "mt_doc_softplandoc_idx_color", "(NOT d.mt_deleted)"));

        PlanInsightFinding finding = Findings(plan, Table<SoftPlanDoc>(Soft)).Should().ContainSingle().Subject;

        finding.Message.Should().Contain("mt_deleted");
        finding.Remedy.Should().Contain("options.Schema.For<SoftPlanDoc>().Index(x => x.Color, i => i.Predicate = \"mt_deleted = false\");");
    }

    [Fact]
    public void P28_names_no_member_when_it_cannot_tell_which_the_index_is_on()
    {
        QueryPlan plan = Plan(P28("mt_doc_softplandoc", "mt_doc_softplandoc_pkey", "(NOT d.mt_deleted)"));

        Findings(plan, Table<SoftPlanDoc>(Soft)).Single().Remedy.Should().Contain("Index(x => x.<the member it is on>");
    }

    [Fact]
    public void P28_has_P10s_boundary_and_needs_ANALYZE()
    {
        MartenPlanTable table = Table<TenantPlanDoc>(Tenanted);

        Findings(Plan(P28("mt_doc_tenantplandoc", "i", "(d.tenant_id = 'a'::text)", kept: 1_000, removed: 10_000)), table).Should().ContainSingle();
        Findings(Plan(P28("mt_doc_tenantplandoc", "i", "(d.tenant_id = 'a'::text)", kept: 1_000, removed: 9_999)), table).Should().BeEmpty();
        Findings(Plan(P28("mt_doc_tenantplandoc", "i", "(d.tenant_id = 'a'::text)", kept: 1_001, removed: 10_000)), table).Should().BeEmpty();
        MartenPlanInsights.FilteredAfterIndexes(Plan(Plain("{\"Node Type\": \"Index Scan\", \"Schema\": \"" + Schema + "\", \"Relation Name\": \"mt_doc_tenantplandoc\", \"Filter\": \"(d.tenant_id = 'a'::text)\"}")), null, [table])
            .Should().BeEmpty();
    }

    [Fact]
    public void P28_is_quiet_when_the_predicate_is_in_the_index_condition_or_the_type_has_no_such_column()
    {
        // tenant_id in Index Cond, and a Filter that tests something else.
        QueryPlan served = Plan(Analyzed(
            "{\"Node Type\": \"Index Scan\", \"Schema\": \"" + Schema + "\", \"Relation Name\": \"mt_doc_tenantplandoc\", \"Index Name\": \"i\", "
            + "\"Index Cond\": \"(d.tenant_id = 'a'::text)\", \"Filter\": \"((d.data ->> 'Number'::text) = '1'::text)\", \"Actual Rows\": 1, \"Actual Loops\": 1, \"Rows Removed by Filter\": 50000}"));

        MartenPlanInsights.FilteredAfterIndexes(served, null, [Table<TenantPlanDoc>(Tenanted)]).Should().BeEmpty();

        // The type is not conjoined, so a tenant_id in the text is a literal or somebody else's column.
        MartenPlanInsights.FilteredAfterIndexes(Plan(P28("mt_doc_softplandoc", "i", "(d.tenant_id = 'a'::text)")), null, [Table<SoftPlanDoc>(Soft)]).Should().BeEmpty();

        // A quoted 'tenant_id' is text, not the column.
        MartenPlanInsights.FilteredAfterIndexes(Plan(P28("mt_doc_tenantplandoc", "i", "((d.data ->> 'x'::text) = 'tenant_id'::text)")), null, [Table<TenantPlanDoc>(Tenanted)])
            .Should().BeEmpty();
    }

    private static string P28(string table, string index, string filter, int kept = 100, int removed = 49_900) => Analyzed(
        "{\"Node Type\": \"Index Scan\", \"Schema\": \"" + Schema + "\", \"Relation Name\": \"" + table + "\", \"Index Name\": \"" + index + "\", "
        + "\"Index Cond\": \"((d.data ->> 'Color'::text) = 'red'::text)\", \"Filter\": \"" + filter.Replace("\"", "\\\"", StringComparison.Ordinal)
        + "\", \"Actual Rows\": " + kept + ", \"Actual Loops\": 1, \"Rows Removed by Filter\": " + removed + "}");

    // -----------------------------------------------------------------------------------------------
    // The pieces
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_insights_are_P26_to_P28_with_headings_the_docs_carry()
    {
        MartenPlanInsights.All.Select(static x => x.Code).Should().Equal("P26", "P27", "P28");
        MartenPlanInsights.All.Select(static x => x.DocsHeading).Should().Equal(
            "## P26 JSON filter on a document table with no usable index",
            "## P27 GIN index on data not used for a containment filter",
            "## P28 Tenant or soft-delete predicate filtered after the index");

        string docs = File.ReadAllText(RepositoryRoot.Combine("docs", "health", PlanInsights.DocsFileName));

        foreach (PlanInsightInfo insight in MartenPlanInsights.All)
        {
            docs.Split('\n').Select(static x => x.TrimEnd()).Should().Contain(insight.DocsHeading);
        }

        PlanInsights.All.Select(static x => x.Code).Should().NotIntersectWith(MartenPlanInsights.All.Select(static x => x.Code), "a code is never shared");
    }

    [Fact]
    public void The_scan_of_JSON_reads_members_containment_and_key_existence_and_what_they_are_compared_with()
    {
        JsonUses.Read("((d.data ->> 'Color'::text) = $1)").Should().ContainSingle()
            .Which.Should().Match<JsonUse>(static x => x.Kind == JsonUseKind.Member && x.Path.Count == 1 && x.Path[0] == "Color" && x.Operator == JsonOperator.Comparison);
        JsonUses.Read("(((d.data -> 'A'::text) ->> 'B'::text) ~~ 'x%'::text)").Should().ContainSingle()
            .Which.Should().Match<JsonUse>(static x => string.Join('.', x.Path) == "A.B" && x.Operator == JsonOperator.Pattern);
        JsonUses.Read("((d.data #>> '{A,B}'::text[]) = $1)").Should().ContainSingle().Which.Path.Should().Equal("A", "B");
        JsonUses.Read("(d.data @> $1)").Single().Kind.Should().Be(JsonUseKind.Containment);
        JsonUses.Read("(d.data ? $1)").Single().Kind.Should().Be(JsonUseKind.KeyExists);
        JsonUses.Read("(d.id = $1)").Should().BeEmpty();
        JsonUses.Read("((d.data ->> 'it''s'::text) = $1)").Single().Path.Should().Equal("it's");
    }

    [Fact]
    public void A_condition_of_adversarial_length_is_read_within_bounds()
    {
        string hostile = string.Concat(Enumerable.Repeat("d.data ->> 'a' ", 20_000));

        JsonUses.Read(hostile).Count.Should().BeLessThanOrEqualTo(32);
        JsonUses.Read(new string('(', 100_000)).Should().BeEmpty();
    }

    [Fact]
    public void The_tables_come_from_the_mapping_and_not_from_a_connection()
    {
        MartenPlanTable table = Table<GinDoc>(static o => o.Schema.For<GinDoc>().GinIndexJsonData().Index(x => x.Color));

        table.Schema.Should().Be(Schema);
        table.Table.Should().Be("mt_doc_gindoc");
        table.GinOnData.Should().NotBeNull();
        table.Indexes.Should().Contain(static x => x.Computed);
        table.Indexes.Should().Contain(static x => !x.Computed);
    }
}
