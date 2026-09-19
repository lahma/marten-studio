using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Sql;
using MartenStudio.Tests.Support;

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
}
