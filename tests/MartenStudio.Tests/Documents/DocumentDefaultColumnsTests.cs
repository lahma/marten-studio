using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Sql;
using MartenStudio.Tests.Support;

using Npgsql;

using ListPage = MartenStudio.Components.Pages.Documents.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// What a collection shows before anybody has chosen anything.
/// </summary>
/// <remarks>
/// <para>
/// It used to be every column the table had, and two of those earned their place on nobody's screen.
/// <c>mt_dotnet_type</c> is the same string on every row of a non-hierarchy collection — 300 px of
/// <c>MartenStudio.SampleDomain.Documents.Customer</c> repeated twenty-five times — and
/// <c>mt_version</c> is a GUID nobody reads at a glance. Between them they squeezed the id column down
/// to a width that truncated a GUID in the middle, which for Marten's sequential ids removes exactly the
/// part that differs: twenty-five rows of <c>74737563-6d6f-7265-0000-00…</c>.
/// </para>
/// <para>
/// Neither is gone. Both are still offered by the chooser and both are still honoured by a pasted
/// <c>?cols=</c>, which is what the last two tests here are about.
/// </para>
/// </remarks>
public class DocumentDefaultColumnsTests
{
    /// <summary>The sample's <c>Customer</c>: the default metadata columns, and one duplicated field.</summary>
    private static DocumentTableInfo Customer() =>
        DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestCustomer>(options =>
            options.Schema.For<SqlTestCustomer>().Duplicate(x => x.Email)));

    private static IReadOnlyList<DocumentColumnHeader> Available(DocumentTableInfo table, bool registered = true) =>
        DocumentDataService.AvailableColumns(table, registered);

    [Fact]
    public void A_collection_opens_on_its_id_its_last_modified_and_its_duplicated_fields()
    {
        DocumentTableInfo table = Customer();

        DocumentDataService.DefaultColumns(Available(table), registered: true)
            .Select(x => x.Key)
            .Should().Equal("id", "meta:LastModified", "dup:email");
    }

    /// <summary>
    /// The two that were dropped, said out loud: the chooser still offers them, so this is about the
    /// default and not about availability.
    /// </summary>
    [Fact]
    public void The_version_and_the_dotnet_type_are_offered_but_not_shown()
    {
        DocumentTableInfo table = Customer();
        IReadOnlyList<DocumentColumnHeader> available = Available(table);

        available.Select(x => x.Key).Should().Contain(["meta:Version", "meta:DotNetType"]);

        DocumentDataService.DefaultColumns(available, registered: true)
            .Select(x => x.Key)
            .Should().NotContain(["meta:Version", "meta:DotNetType"]);
    }

    /// <summary>
    /// A hierarchy is the one collection where <c>mt_dotnet_type</c> varies by row — and it is also the
    /// one where the row already says which subclass it is, in the badge beside its id. So it stays off
    /// there too, and <c>mt_doc_type</c> with it.
    /// </summary>
    [Fact]
    public void A_hierarchy_does_not_get_the_type_columns_back_because_the_badge_already_says_it()
    {
        DocumentTableInfo table = SqlTestTables.Hierarchy();

        Available(table).Select(x => x.Key).Should().Contain(["meta:DotNetType", "meta:DocumentType"]);

        DocumentDataService.DefaultColumns(Available(table), registered: true)
            .Select(x => x.Key)
            .Should().NotContain(["meta:DotNetType", "meta:DocumentType"]);
    }

    /// <summary>
    /// A table nothing maps still gets no duplicated columns: they are guesses about what somebody
    /// else's schema means. That rule is unchanged; only the metadata half of the default moved.
    /// </summary>
    [Fact]
    public void An_unregistered_table_keeps_getting_no_duplicated_columns()
    {
        DocumentTableInfo table = SqlTestTables.FullyFeatured();

        DocumentDataService.DefaultColumns(Available(table, registered: false), registered: false)
            .Select(x => x.Key)
            .Should().Equal("id", "meta:LastModified");
    }

    /// <summary>
    /// A store with <c>DisableInformationalFields()</c> has no <c>mt_last_modified</c> at all
    /// (AGENTS.md hard rule 10), so the default is the id and whatever the mapping duplicated.
    /// </summary>
    [Fact]
    public void A_table_with_no_metadata_at_all_opens_on_its_id_alone()
    {
        DocumentTableInfo table = SqlTestTables.MetadataLess();

        DocumentDataService.DefaultColumns(Available(table), registered: true)
            .Select(x => x.Key)
            .Should().Equal("id");
    }

    /// <summary>
    /// The badges beside an id — deleted, tenant, subclass — are read off three metadata columns that are
    /// no longer in the default set, which is exactly why the list selects them whether or not a header
    /// shows them. Losing that would make a soft-deleted row look live.
    /// </summary>
    [Fact]
    public void The_badge_columns_are_not_default_columns_and_so_are_fetched_on_their_own()
    {
        DocumentDataService.BadgeColumns.Should().Equal(
            DocumentMetadataColumn.IsSoftDeleted,
            DocumentMetadataColumn.TenantId,
            DocumentMetadataColumn.DocumentType);

        // Soft delete and conjoined tenancy on one table, the hierarchy on another: mt_doc_type exists
        // if and only if the type has subclasses, so no single mapping carries all three (hard rule 10).
        foreach (DocumentTableInfo table in new[] { SqlTestTables.FullyFeatured(), SqlTestTables.Hierarchy() })
        {
            IReadOnlyList<DocumentColumnHeader> available = Available(table);
            List<DocumentColumnHeader> defaults = DocumentDataService.DefaultColumns(available, registered: true);

            foreach (DocumentMetadataColumn badge in DocumentDataService.BadgeColumns)
            {
                if (!table.HasMetadata(badge))
                {
                    continue;
                }

                available.Should().Contain(
                    x => x.Column == new DocumentColumn.Metadata(badge),
                    "the chooser still offers it to anybody who wants the column itself");

                defaults.Should().NotContain(
                    x => x.Column == new DocumentColumn.Metadata(badge),
                    "the badge says it in a tenth of the width of a column");
            }
        }

        SqlTestTables.FullyFeatured().HasMetadata(DocumentMetadataColumn.IsSoftDeleted).Should().BeTrue();
        SqlTestTables.FullyFeatured().HasMetadata(DocumentMetadataColumn.TenantId).Should().BeTrue();
        SqlTestTables.Hierarchy().HasMetadata(DocumentMetadataColumn.DocumentType).Should().BeTrue();
    }

    /// <summary>
    /// A collection whose default set is the id and nothing else selects the id and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pair of tables is what made <c>DocumentListQuery.Columns</c> need a third state. The builder
    /// used to read an <em>empty</em> column list as "no preference — select every metadata column and
    /// every duplicated field", which was unreachable while the default set was that same list. It is
    /// reachable now: a type with <c>LastModified</c> disabled and no duplicated field, and a discovered
    /// <c>mt_doc_*</c> table whose only metadata column is <c>mt_version</c>, both default to the id
    /// alone. Left as it was, the read would have selected <c>mt_version</c> and <c>mt_dotnet_type</c>
    /// that no header shows and that "Show SQL" would then have contradicted (D14).
    /// </para>
    /// <para>
    /// The select list is composed here the way <c>ListCoreAsync</c> composes it — the visible headers
    /// minus the id, plus the sort column, plus the badge columns — so this asserts the seam between the
    /// default set and the SQL rather than either half on its own.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_mapping_with_no_last_modified_and_no_duplicated_field_selects_the_id_alone() =>
        SelectsTheIdAlone(DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestNote>(options =>
            options.Schema.For<SqlTestNote>().Metadata(m => m.LastModified.Enabled = false))));

    /// <summary>
    /// The same, for a table no mapping claims. Its one metadata column is <c>mt_version</c>, which is
    /// exactly the column the old branch would have selected behind the grid's back.
    /// </summary>
    [Fact]
    public void A_discovered_table_whose_only_metadata_column_is_mt_version_selects_the_id_alone() =>
        SelectsTheIdAlone(DocumentTableInfo.FromDiscoveredTable("studio_sql", "mt_doc_orphan",
        [
            new PostgresColumn("id", "uuid", "uuid", false),
            new PostgresColumn("data", "jsonb", "jsonb", false),
            new PostgresColumn("mt_version", "uuid", "uuid", false),
        ]));

    private static void SelectsTheIdAlone(DocumentTableInfo table)
    {
        List<DocumentColumnHeader> visible = DocumentDataService.DefaultColumns(Available(table), registered: true);

        visible.Select(x => x.Key).Should().Equal(["id"], "this case is the whole point of the test");

        using NpgsqlCommand command = DocumentQueryBuilder.BuildList(table, new DocumentListQuery
        {
            Columns = QueryColumns(table, visible),
        });

        command.CommandText.Should().Be(
            $"""
            select d."id",
                   case when octet_length(d."data"::text) <= @maxInline then d."data"::text end as data,
                   octet_length(d."data"::text) as data_bytes
            from "{table.Schema}"."{table.Table}" as d
            where 1 = 1
            order by d."id"
            limit @limit offset @offset
            """.ReplaceLineEndings("\n"),
            "the select list is exactly the headers, and the headers are the id");

        foreach (DocumentMetadataColumnInfo metadata in table.MetadataColumns)
        {
            command.CommandText.Should().NotContain(
                metadata.ColumnName,
                "a column no header shows is a column Show SQL would contradict");
        }
    }

    /// <summary>
    /// The other side of the same distinction: <see langword="null"/> still means "whatever this table
    /// has", which is what every ad-hoc read and every builder test relies on.
    /// </summary>
    [Fact]
    public void No_column_preference_at_all_still_selects_everything_the_table_has()
    {
        DocumentTableInfo table = Customer();

        using NpgsqlCommand command = DocumentQueryBuilder.BuildList(table, new DocumentListQuery());

        command.CommandText.Should().Contain("d.\"mt_version\"");
        command.CommandText.Should().Contain("d.\"mt_last_modified\"");
        command.CommandText.Should().Contain("d.\"email\"");
    }

    /// <summary>
    /// The select list <c>ListCoreAsync</c> builds from a set of visible headers: the headers minus the
    /// id, then the sort column if it is not already there, then the badge columns.
    /// </summary>
    private static List<DocumentColumn> QueryColumns(DocumentTableInfo table, List<DocumentColumnHeader> visible)
    {
        List<DocumentColumn> columns = [];

        foreach (DocumentColumnHeader header in visible)
        {
            if (header.Column is not DocumentColumn.Id)
            {
                columns.Add(header.Column);
            }
        }

        // DocumentDataService.DefaultSort is the id, so nothing is added for the sort here.
        DocumentDataService.DefaultSort.Should().Be(DocumentColumn.ById);

        foreach (DocumentMetadataColumn badge in DocumentDataService.BadgeColumns)
        {
            if (table.HasMetadata(badge) && !columns.Contains(new DocumentColumn.Metadata(badge)))
            {
                columns.Add(new DocumentColumn.Metadata(badge));
            }
        }

        return columns;
    }

    /// <summary>A pasted <c>?cols=</c> still reaches both of the columns the default no longer shows.</summary>
    [Fact]
    public void A_pasted_cols_still_shows_the_version_and_the_dotnet_type()
    {
        DocumentTableInfo table = Customer();

        DocumentColumnKeys.ResolveAll(table, ["meta:Version", "meta:DotNetType"])
            .Select(x => x.Key)
            .Should().Equal("id", "meta:Version", "meta:DotNetType");
    }

    /// <summary>
    /// And the same set, all the way through the page: the headers the table draws for the sample's
    /// <c>customer</c> are the id, the last modified, the duplicated <c>email</c>, and size.
    /// </summary>
    [Fact]
    public void The_customer_list_draws_exactly_id_last_modified_email_and_size()
    {
        DocumentTableInfo table = Customer();
        IReadOnlyList<DocumentColumnHeader> available = Available(table);
        List<DocumentColumnHeader> defaults = DocumentDataService.DefaultColumns(available, registered: true);

        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(
            rows: [FakeDocuments.Row("74737563-6d6f-7265-0000-000000000001")],
            columns: defaults) with
        {
            AvailableColumns = available,
            SortKey = string.Empty,
        };

        var page = context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        page.TextOfAll(".ms-doc-table thead th")
            .Should().Equal("Expand", "id", "last modified", "email", "size");

        // The chooser is the other half of "not shown" rather than "not available".
        page.TextOfAll(".ms-column-chooser-label")
            .Should().Contain(["id", "version", "last modified", ".NET type", "email"]);
    }

    /// <summary>
    /// The list's own scroll container (<c>.ms-table-wrap</c>) predates the shared region every other
    /// table sits in; it has to be announced and keyboard-reachable the same way (UX-1).
    /// </summary>
    [Fact]
    public void The_customer_list_sits_in_a_labelled_scroll_region()
    {
        DocumentTableInfo table = Customer();
        IReadOnlyList<DocumentColumnHeader> available = Available(table);
        List<DocumentColumnHeader> defaults = DocumentDataService.DefaultColumns(available, registered: true);

        using var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(
            rows: [FakeDocuments.Row("74737563-6d6f-7265-0000-000000000001")],
            columns: defaults) with
        {
            AvailableColumns = available,
            SortKey = string.Empty,
        };

        var page = context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        page.ShouldPutEveryTableInALabelledScrollRegion();
    }
}
