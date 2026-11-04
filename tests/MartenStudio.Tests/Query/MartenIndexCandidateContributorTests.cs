using MartenStudio.Services.Query;
using MartenStudio.Tests.Sql;

using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// 1.2-INDEX-A: MartenStudio's half of the index advisor, with no database and no store connection. On a Marten document table an
/// index read off a plan becomes the index Marten would create for a <c>StoreOptions</c> line, carrying that line, or is dropped;
/// P26's JSON members are added; a hidden type's table yields nothing and is never named; and when the store cannot be learned every
/// candidate on a Marten-named table is dropped.
/// </summary>
public class MartenIndexCandidateContributorTests
{
    private const string Table = "mt_doc_sqltestcustomer";

    private static QueryPlan Scan(string filter, string table = Table, string schema = SqlTestStore.Schema, string nodeType = "Seq Scan") =>
        QueryPlanReader.Read(
            "[{\"Plan\": {\"Node Type\": \"" + nodeType + "\", \"Relation Name\": \"" + table + "\", \"Schema\": \"" + schema + "\", \"Alias\": \"d\", "
            + "\"Total Cost\": 100, \"Plan Rows\": 10, \"Plan Width\": 8, \"Filter\": \"" + filter + "\"}}]").Plan!;

    private static IReadOnlyList<MartenPlanTable> Tables(Action<global::Marten.StoreOptions>? configure = null) =>
        [MartenPlanTable.From(SqlTestStore.DocumentType<SqlTestCustomer>(configure), null)];

    private static List<HypotheticalIndex> Offer(QueryPlan plan, IReadOnlyList<MartenPlanTable>? tables) =>
        MartenIndexCandidateContributor.Offer(plan, WorkloadIndexCandidates.FromPlan(plan), tables);

    [Fact]
    public void A_duplicated_fields_column_becomes_the_Index_line_with_Martens_own_DDL()
    {
        QueryPlan plan = Scan("(d.email = $1)");

        HypotheticalIndex index = Offer(plan, Tables(static o => o.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email))).Should().ContainSingle().Subject;

        index.Origin.Should().Be(HypotheticalIndexOrigin.Marten);
        index.Line.Should().Be("options.Schema.For<SqlTestCustomer>().Index(x => x.Email);");
        index.Definition.Should().Be("CREATE INDEX mt_doc_sqltestcustomer_idx_email ON studio_sql.mt_doc_sqltestcustomer USING btree ((email));");
        index.Note.Should().Contain("Declare it in StoreOptions");
    }

    [Fact]
    public void The_last_modified_column_becomes_IndexLastModified()
    {
        HypotheticalIndex index = Offer(Scan("(d.mt_last_modified > $1)"), Tables()).Should().ContainSingle().Subject;

        index.Line.Should().Be("options.Schema.For<SqlTestCustomer>().IndexLastModified();");
        index.Definition.Should().Be("CREATE INDEX mt_doc_sqltestcustomer_idx_mt_last_modified ON studio_sql.mt_doc_sqltestcustomer USING btree (mt_last_modified);");
    }

    [Fact]
    public void Any_other_column_on_a_document_table_is_dropped_because_no_line_declares_it()
    {
        QueryPlan plan = Scan("(d.tenant_id = $1)");

        WorkloadIndexCandidates.FromPlan(plan).Should().ContainSingle("the plan does suggest it");
        Offer(plan, Tables()).Should().BeEmpty("one made by hand is dropped by the next apply");
    }

    [Fact]
    public void A_json_comparison_is_offered_as_the_Duplicate_line_imagined_as_the_computed_index()
    {
        HypotheticalIndex index = Offer(Scan("((d.data ->> 'Name'::text) = $1)"), Tables()).Should().ContainSingle().Subject;

        index.Line.Should().Be("options.Schema.For<SqlTestCustomer>().Duplicate(x => x.Name);");
        index.Definition.Should().Be("CREATE INDEX mt_doc_sqltestcustomer_idx_name ON studio_sql.mt_doc_sqltestcustomer USING btree ((data ->> 'Name'));");
        index.Note.Should().Contain(MartenStudio.Internal.Sql.MartenIndexDdl.DuplicateNote);
    }

    [Fact]
    public void A_json_member_a_declared_index_serves_yields_nothing()
    {
        Offer(Scan("((d.data ->> 'Name'::text) = $1)"), Tables(static o => o.Schema.For<SqlTestCustomer>().Index(x => x.Name))).Should().BeEmpty();
    }

    [Fact]
    public void Containment_and_key_existence_yield_nothing_because_hypopg_imagines_no_GIN()
    {
        Offer(Scan("(d.data @> $1)"), Tables()).Should().BeEmpty();
        Offer(Scan("(d.data ? 'Name'::text)"), Tables()).Should().BeEmpty();
    }

    [Fact]
    public void A_hidden_types_table_yields_nothing_and_is_never_named()
    {
        IReadOnlyList<MartenPlanTable> none = [];

        List<HypotheticalIndex> offered = Offer(Scan("((d.data ->> 'Name'::text) = $1)"), none);

        offered.Should().BeEmpty();
        Offer(Scan("(d.email = $1)"), none).Should().BeEmpty("a hidden type has no table here: its candidates are dropped");
    }

    [Fact]
    public void When_the_store_cannot_be_learned_every_candidate_on_a_Marten_table_is_dropped_and_the_rest_stay()
    {
        QueryPlan marten = Scan("(d.email = $1)");
        QueryPlan other = Scan("(d.customer_id = $1)", table: "orders", schema: "public");

        Offer(marten, null).Should().BeEmpty();
        Offer(other, null).Should().ContainSingle().Which.Origin.Should().Be(HypotheticalIndexOrigin.Plan);
    }

    [Fact]
    public void A_table_that_is_not_Martens_is_passed_through_unchanged()
    {
        QueryPlan other = Scan("(d.customer_id = $1)", table: "orders", schema: "public");

        List<HypotheticalIndex> offered = Offer(other, Tables());

        offered.Should().ContainSingle().Which.Definition.Should().Be("CREATE INDEX ON \"public\".\"orders\" (\"customer_id\");");
    }

    [Fact]
    public void A_table_in_another_schema_than_the_stores_is_not_its_table()
    {
        Offer(Scan("(d.email = $1)", schema: "elsewhere"), Tables(static o => o.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email))).Should().BeEmpty();
    }

    [Fact]
    public void Building_the_indexes_registers_nothing_on_the_mapping()
    {
        IReadOnlyList<MartenPlanTable> tables = Tables(static o => o.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email));
        int declared = tables[0].Mapping!.Indexes.Count;

        Offer(Scan("(d.email = $1)"), tables);
        Offer(Scan("((d.data ->> 'Name'::text) = $1)"), tables);

        tables[0].Mapping!.Indexes.Should().HaveCount(declared);
    }
}
