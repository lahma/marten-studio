using System.Linq.Expressions;
using System.Reflection;

using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Sql;

using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// X2's Marten half, with no database: the index Marten would create for the advisor's line, as Marten writes it - built without
/// registering anything - and which of the advice's lines LINQ mode and the Documents list offer to imagine.
/// </summary>
public class MartenWhatIfTests
{
    private static readonly MemberInfo Name = typeof(SqlTestCustomer).GetProperty(nameof(SqlTestCustomer.Name))!;

    private static readonly MemberInfo OrderCount = typeof(SqlTestCustomer).GetProperty(nameof(SqlTestCustomer.OrderCount))!;

    [Fact]
    public void A_computed_index_is_Martens_own_DDL_over_the_members_JSON_locator()
    {
        IDocumentType mapping = SqlTestStore.DocumentType<SqlTestCustomer>();

        MartenIndexDdl.Computed(mapping, [Name]).Should()
            .Be("CREATE INDEX mt_doc_sqltestcustomer_idx_name ON studio_sql.mt_doc_sqltestcustomer USING btree ((data ->> 'Name'));");

        MartenIndexDdl.Computed(mapping, [OrderCount]).Should().Contain("USING btree ((CAST(data ->> 'OrderCount' as integer)))",
            "Marten casts a number in its LINQ statement, and its index casts it the same way so the planner can match it");
    }

    [Fact]
    public void A_duplicated_members_computed_index_is_on_its_column()
    {
        IDocumentType mapping = SqlTestStore.DocumentType<SqlTestCustomer>(static o => o.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email));
        MemberInfo email = typeof(SqlTestCustomer).GetProperty(nameof(SqlTestCustomer.Email))!;

        MartenIndexDdl.Computed(mapping, [email]).Should()
            .Be("CREATE INDEX mt_doc_sqltestcustomer_idx_email ON studio_sql.mt_doc_sqltestcustomer USING btree ((email));");
    }

    [Fact]
    public void A_metadata_index_is_the_one_IndexLastModified_and_SoftDeletedWithIndex_declare()
    {
        IDocumentType mapping = SqlTestStore.DocumentType<SqlTestCustomer>(static o => o.Schema.For<SqlTestCustomer>().SoftDeleted());

        MartenIndexDdl.OnColumn(mapping, "mt_last_modified").Should()
            .Be("CREATE INDEX mt_doc_sqltestcustomer_idx_mt_last_modified ON studio_sql.mt_doc_sqltestcustomer USING btree (mt_last_modified);");
        MartenIndexDdl.OnColumn(mapping, "mt_deleted_at", "mt_deleted").Should()
            .Be("CREATE INDEX mt_doc_sqltestcustomer_idx_mt_deleted_at ON studio_sql.mt_doc_sqltestcustomer USING btree (mt_deleted_at) WHERE (mt_deleted);");
    }

    [Fact]
    public void Building_an_index_registers_nothing_on_the_mapping()
    {
        IDocumentType mapping = SqlTestStore.DocumentType<SqlTestCustomer>();
        int declared = ((DocumentMapping) mapping).Indexes.Count;

        MartenIndexDdl.Computed(mapping, [Name]);
        MartenIndexDdl.OnColumn(mapping, "mt_last_modified");

        ((DocumentMapping) mapping).Indexes.Should().HaveCount(declared, "the constructor alone is called: the store's configuration is what it was");
    }

    [Fact]
    public void LINQ_modes_advice_offers_Martens_index_for_each_line_that_declares_one_and_the_server_finds_it_again()
    {
        IDocumentType mapping = SqlTestStore.DocumentType<SqlTestCustomer>();
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(mapping);
        IQueryable<SqlTestCustomer> source = Array.Empty<SqlTestCustomer>().AsQueryable();
        Expression composed = source.Where(static x => x.Name == "Alice").OrderBy(static x => x.OrderCount).Expression;

        LinqIndexAdvice advice = LinqIndexAdvisor.Build(composed, source.Expression, mapping.IdMember, table, [], static x => x, null, mapping);

        advice.WhatIf.Select(static x => x.Key).Should().Equal("marten:Name", "marten:order by OrderCount");
        HypotheticalIndex name = advice.WhatIf[0];
        name.Origin.Should().Be(HypotheticalIndexOrigin.Marten);
        name.Definition.Should().Contain("((data ->> 'Name'))");
        name.Caption.Should().Be("The index Marten would create for options.Schema.For<SqlTestCustomer>().Duplicate(x => x.Name);");
        name.Note.Should().StartWith(MartenIndexDdl.DuplicateNote).And.EndWith(MartenIndexDdl.DeclareNote);

        IReadOnlyList<HypotheticalIndex> offered = LinqIndexAdvisor.Hypothetical(composed, source.Expression, mapping.IdMember, table, mapping, static x => x);
        advice.WhatIf.Should().OnlyContain(x => offered.Any(o => o.Same(x)), "the server builds the same index for the same member, by key and definition");

        LinqIndexAdvisor.Build(composed, source.Expression, mapping.IdMember, table, [], static x => x, null).WhatIf.Should()
            .BeEmpty("without the mapping, nothing is offered");
    }

    [Fact]
    public void An_id_filter_and_a_containment_are_not_offered()
    {
        IDocumentType mapping = SqlTestStore.DocumentType<SqlTestCustomer>();
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(mapping);
        IQueryable<SqlTestCustomer> source = Array.Empty<SqlTestCustomer>().AsQueryable();
        Expression composed = source.Where(static x => x.Id == Guid.Empty).Expression;

        LinqIndexAdvisor.Build(composed, source.Expression, mapping.IdMember, table, [], static x => x, null, mapping).WhatIf.Should()
            .BeEmpty("the id is the primary key - a green verdict offers nothing");
    }

    [Fact]
    public void The_Documents_list_offers_only_what_its_own_statement_can_use()
    {
        IDocumentType mapping = SqlTestStore.DocumentType<SqlTestCustomer>(static o => o.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email));
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(mapping);

        DocumentPredicate byEmail = new DocumentPredicate.FieldCompare(["Email"], ComparisonOperator.Equal, new SearchValue.Text("a@b"));
        DocumentPredicate byName = new DocumentPredicate.FieldCompare(["Name"], ComparisonOperator.Equal, new SearchValue.Text("Alice"));
        DocumentColumn sort = new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified);

        // No index on the database at all: the duplicated column has lost its, mt_last_modified never had one.
        IndexAdvice advice = IndexAdvisor.Evaluate(table, [], [byEmail, byName], sort);

        IReadOnlyList<HypotheticalIndex> offered = DocumentDataService.WhatIfIndexes(mapping, table, advice, sort);

        offered.Select(static x => x.Definition).Should().Equal(
            "CREATE INDEX mt_doc_sqltestcustomer_idx_email ON studio_sql.mt_doc_sqltestcustomer USING btree ((email));",
            "CREATE INDEX mt_doc_sqltestcustomer_idx_mt_last_modified ON studio_sql.mt_doc_sqltestcustomer USING btree (mt_last_modified);");
        offered.Select(static x => x.Key).Should().Equal("marten:documents:filter 0", "marten:documents:sort");
        offered.Should().NotContain(static x => x.Definition.Contains("'Name'", StringComparison.Ordinal),
            "a Duplicate line adds a column, and the page reads the JSON with #>>, which no index Marten declares serves");
    }
}
