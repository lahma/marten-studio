using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Sql;

using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// 1.2-INDEX-A: MartenStudio's half of the index advisor, with no database and no store connection. On a Marten document table an
/// index read off a plan becomes the index Marten would create for a <c>StoreOptions</c> line, carrying that line, or - where the
/// store already declares it - the same index with no line and a note; any other is dropped; a hidden type's table yields nothing and
/// is never named; and when the store cannot be learned every candidate on a Marten-named table is dropped. A JSON member is never
/// offered: a recorded statement names none.
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
        MartenIndexCandidateContributor.Offer(WorkloadIndexCandidates.FromPlan(plan), tables);

    [Fact]
    public void A_duplicated_fields_column_the_store_does_not_declare_an_index_on_becomes_the_Index_line_with_Martens_own_DDL()
    {
        // A mapping read straight from StoreOptions: the duplicated field Marten always indexes, with its index taken out of the
        // declared set, is what a database that lost it looks like to the advisor.
        MartenPlanTable declared = Tables(static o => o.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email))[0];
        MartenPlanTable withoutIt = new()
        {
            Schema = declared.Schema,
            Table = declared.Table,
            TypeName = declared.TypeName,
            DocumentType = declared.DocumentType,
            Info = declared.Info,
            Mapping = declared.Mapping,
            Indexes = [.. declared.Indexes.Where(static x => !x.Name.EndsWith("_idx_email", StringComparison.Ordinal))],
        };

        HypotheticalIndex index = Offer(Scan("(d.email = $1)"), [withoutIt]).Should().ContainSingle().Subject;

        index.Origin.Should().Be(HypotheticalIndexOrigin.Marten);
        index.Line.Should().Be("options.Schema.For<SqlTestCustomer>().Index(x => x.Email);");
        index.Definition.Should().Be("CREATE INDEX mt_doc_sqltestcustomer_idx_email ON studio_sql.mt_doc_sqltestcustomer USING btree ((email));");
        index.Note.Should().Contain("Declare it in StoreOptions");
    }

    [Fact]
    public void A_duplicated_field_the_store_already_declares_an_index_on_has_no_line_and_imagines_the_declared_index_itself()
    {
        IReadOnlyList<MartenPlanTable> tables = Tables(static o => o.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email));
        MartenDeclaredIndex declared = tables[0].Indexes.Should().ContainSingle(static x => x.Name == "mt_doc_sqltestcustomer_idx_email", "a Duplicate() always declares its own index").Subject;

        HypotheticalIndex index = Offer(Scan("(d.email = $1)"), tables).Should().ContainSingle().Subject;

        index.Origin.Should().Be(HypotheticalIndexOrigin.Marten);
        index.Line.Should().BeNull("the line is already written in the configuration");
        index.Note.Should().Be(MartenIndexDdl.AlreadyDeclaredNote);
        index.Definition.Should().Be(declared.Definition, "what an apply would create is what the what-if tests");
        index.Caption.Should().Contain("already declares");
    }

    [Fact]
    public void A_declared_index_is_found_by_its_column_whatever_it_is_named()
    {
        IReadOnlyList<MartenPlanTable> tables = Tables(static o => o.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email, configure: static i => i.Name = "my_email_idx"));
        MartenDeclaredIndex declared = tables[0].Indexes.Should().ContainSingle(static x => x.Name == "my_email_idx").Subject;

        HypotheticalIndex index = Offer(Scan("(d.email = $1)"), tables).Should().ContainSingle().Subject;

        index.Line.Should().BeNull("a custom name does not make the line any less declared");
        index.Note.Should().Be(MartenIndexDdl.AlreadyDeclaredNote);
        index.Definition.Should().Be(declared.Definition).And.Contain("my_email_idx");
    }

    [Fact]
    public void A_partial_declared_index_is_not_the_index_a_line_would_declare()
    {
        IReadOnlyList<MartenPlanTable> tables = Tables(static o => o.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email, configure: static i => i.Predicate = "email is not null"));

        tables[0].Indexes.Should().ContainSingle(static x => x.Predicate != null);

        HypotheticalIndex index = Offer(Scan("(d.email = $1)"), tables).Should().ContainSingle().Subject;

        index.Line.Should().Be("options.Schema.For<SqlTestCustomer>().Index(x => x.Email);", "a partial index does not serve every lookup of the column");
        index.Definition.Should().Be("CREATE INDEX mt_doc_sqltestcustomer_idx_email ON studio_sql.mt_doc_sqltestcustomer USING btree ((email));");
    }

    [Fact]
    public void The_last_modified_column_becomes_IndexLastModified_where_the_store_does_not_declare_it_and_has_no_line_where_it_does()
    {
        HypotheticalIndex undeclared = Offer(Scan("(d.mt_last_modified > $1)"), Tables()).Should().ContainSingle().Subject;

        undeclared.Line.Should().Be("options.Schema.For<SqlTestCustomer>().IndexLastModified();");
        undeclared.Definition.Should().Be("CREATE INDEX mt_doc_sqltestcustomer_idx_mt_last_modified ON studio_sql.mt_doc_sqltestcustomer USING btree (mt_last_modified);");

        HypotheticalIndex declared = Offer(Scan("(d.mt_last_modified > $1)"), Tables(static o => o.Schema.For<SqlTestCustomer>().IndexLastModified())).Should().ContainSingle().Subject;

        declared.Line.Should().BeNull();
        declared.Note.Should().Be(MartenIndexDdl.AlreadyDeclaredNote);
    }

    [Fact]
    public void Any_other_column_on_a_document_table_is_dropped_because_no_line_declares_it()
    {
        QueryPlan plan = Scan("(d.tenant_id = $1)");

        WorkloadIndexCandidates.FromPlan(plan).Should().ContainSingle("the plan does suggest it");
        Offer(plan, Tables()).Should().BeEmpty("one made by hand is dropped by the next apply");
    }

    [Fact]
    public void A_json_member_yields_nothing_because_a_recorded_statement_names_none_and_hypopg_imagines_no_GIN()
    {
        Offer(Scan("((d.data ->> $2) = $1)"), Tables()).Should().BeEmpty();
        Offer(Scan("((d.data ->> 'Name'::text) = $1)"), Tables()).Should().BeEmpty("the advisor adds nothing of its own");
        Offer(Scan("(d.data @> $1)"), Tables()).Should().BeEmpty();
        Offer(Scan("(d.data ? 'Name'::text)"), Tables()).Should().BeEmpty();
    }

    [Fact]
    public void A_hidden_types_table_yields_nothing_and_is_never_named()
    {
        IReadOnlyList<MartenPlanTable> none = [];

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
        Offer(Scan("(d.mt_last_modified > $1)"), tables);

        tables[0].Mapping!.Indexes.Should().HaveCount(declared);
    }
}
