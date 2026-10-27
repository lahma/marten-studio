using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Support;

using PostgresStudio.Services.Query;

using ListPage = MartenStudio.Components.Pages.Documents.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// M5: newest-first by last modified, created or revision - presets over the list read the studio already has, with no
/// Marten call (Marten 9.52's metadata ordering is the same <c>order by</c>, in a read the studio does not use).
/// </summary>
public class DocumentOrderPresetsTests
{
    private static readonly DocumentColumnHeader IdHeader = DocumentColumnKeys.HeaderFor(DocumentColumn.ById);

    private static DocumentColumnHeader Meta(DocumentMetadataColumn column) =>
        DocumentColumnKeys.HeaderFor(new DocumentColumn.Metadata(column));

    [Fact]
    public void Every_preset_the_table_can_answer_is_offered_after_the_default()
    {
        IReadOnlyList<DocumentOrderPreset> presets = DocumentOrderPresets.For(
        [
            IdHeader,
            Meta(DocumentMetadataColumn.Revision),
            Meta(DocumentMetadataColumn.CreatedAt),
            Meta(DocumentMetadataColumn.LastModified),
        ]);

        presets.Select(static x => x.Label).Should().Equal(
            "Id, highest first",
            "Last modified, newest first",
            "Created, newest first",
            "Revision, highest first");

        presets.Skip(1).Should().OnlyContain(static x => x.Direction == SortDirection.Descending);
        presets.Select(static x => x.SortKey).Should().Equal("id", "meta:LastModified", "meta:CreatedAt", "meta:Revision");
    }

    /// <summary>
    /// <c>mt_created_at</c> is opt-in and <c>DisableInformationalFields()</c> removes <c>mt_last_modified</c>: a preset over a
    /// column the table does not have would be a sort the builder refuses.
    /// </summary>
    [Fact]
    public void A_column_the_table_does_not_have_is_not_offered()
    {
        DocumentOrderPresets.For([IdHeader, Meta(DocumentMetadataColumn.LastModified)])
            .Select(static x => x.Value).Should().Equal("id", "last-modified");

        DocumentOrderPresets.For([IdHeader]).Should().ContainSingle()
            .Which.Should().Be(DocumentOrderPresets.Default);
    }

    /// <summary>A Guid version shares <c>mt_version</c> with the revision and has no order worth asking for, as Marten says too.</summary>
    [Fact]
    public void A_guid_version_is_never_a_newest_first_order()
    {
        DocumentOrderPresets.For([IdHeader, Meta(DocumentMetadataColumn.Version)])
            .Should().ContainSingle().Which.Should().Be(DocumentOrderPresets.Default);
    }

    [Fact]
    public void The_current_preset_is_the_one_whose_key_and_direction_the_list_has()
    {
        IReadOnlyList<DocumentOrderPreset> presets = DocumentOrderPresets.For([IdHeader, Meta(DocumentMetadataColumn.LastModified)]);

        DocumentOrderPresets.Current(presets, "meta:lastmodified", SortDirection.Descending)
            .Should().Be(DocumentOrderPresets.LastModified, "keys compare as the URL's do, without case");
        DocumentOrderPresets.Current(presets, null, SortDirection.Descending)
            .Should().Be(DocumentOrderPresets.Default, "no sort in the URL is the id, as the list reads it");
        DocumentOrderPresets.Current(presets, "meta:LastModified", SortDirection.Ascending)
            .Should().BeNull("oldest first is a column header's order, not a preset");
        DocumentOrderPresets.Current(presets, "json:Name", SortDirection.Descending).Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------
    // The toolbar
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_toolbar_offers_the_presets_and_shows_the_one_the_list_is_in()
    {
        using DocumentsComponentContext context = Loaded(
            [IdHeader, Meta(DocumentMetadataColumn.LastModified)],
            [IdHeader, Meta(DocumentMetadataColumn.LastModified), Meta(DocumentMetadataColumn.CreatedAt)],
            sortKey: "meta:LastModified");

        IRenderedComponent<ListPage> page = Render(context);

        page.FindAll("#ms-doc-order option[selected]").Should().ContainSingle()
            .Which.GetAttribute("value").Should().Be("last-modified");
        page.FindAll("#ms-doc-order option").Select(static x => x.TextContent.Trim()).Should().Equal(
            "Id, highest first", "Last modified, newest first", "Created, newest first");
        page.Find("label[for=ms-doc-order]").TextContent.Trim().Should().Be("Order");
    }

    /// <summary>A list a column header sorted some other way says so, rather than claiming a preset it is not in.</summary>
    [Fact]
    public void A_list_sorted_by_a_column_header_reads_as_that()
    {
        using DocumentsComponentContext context = Loaded(
            [IdHeader, Meta(DocumentMetadataColumn.LastModified)],
            [IdHeader, Meta(DocumentMetadataColumn.LastModified)],
            sortKey: "meta:LastModified",
            direction: SortDirection.Ascending);

        IRenderedComponent<ListPage> page = Render(context);

        AngleSharp.Dom.IElement custom = page.FindAll("#ms-doc-order option[selected]").Should().ContainSingle().Subject;
        custom.GetAttribute("value").Should().BeEmpty();
        custom.HasAttribute("disabled").Should().BeTrue();
        custom.TextContent.Trim().Should().Be("By a column header");
    }

    /// <summary>
    /// Choosing a preset sorts from the first page - and puts the column on the grid when it is not there, so the
    /// times the list is in order of are on screen - without touching the saved column choice.
    /// </summary>
    [Fact]
    public void Choosing_created_newest_first_sorts_by_it_from_the_first_page_and_shows_the_column()
    {
        using DocumentsComponentContext context = Loaded(
            [IdHeader, Meta(DocumentMetadataColumn.LastModified)],
            [IdHeader, Meta(DocumentMetadataColumn.LastModified), Meta(DocumentMetadataColumn.CreatedAt)]);

        IRenderedComponent<ListPage> page = Render(context);

        page.Find("#ms-doc-order").Change("created");

        context.CurrentUri.Should().Contain("sort=meta%3ACreatedAt");
        context.CurrentUri.Should().Contain("dir=desc");
        context.CurrentUri.Should().Contain("cols=id,meta%3ALastModified,meta%3ACreatedAt");
        context.CurrentUri.Should().NotContain("cursor=");
    }

    [Fact]
    public void Choosing_a_preset_whose_column_is_on_the_grid_leaves_the_columns_alone()
    {
        using DocumentsComponentContext context = Loaded(
            [IdHeader, Meta(DocumentMetadataColumn.LastModified)],
            [IdHeader, Meta(DocumentMetadataColumn.LastModified)]);

        IRenderedComponent<ListPage> page = Render(context);

        page.Find("#ms-doc-order").Change("last-modified");

        context.CurrentUri.Should().Contain("sort=meta%3ALastModified");
        context.CurrentUri.Should().Contain("dir=desc");
        context.CurrentUri.Should().NotContain("cols=");
    }

    /// <summary>A table with nothing to put newest first gets no control at all.</summary>
    [Fact]
    public void A_table_with_no_metadata_column_to_order_by_has_no_order_control()
    {
        using DocumentsComponentContext context = Loaded([IdHeader], [IdHeader]);

        IRenderedComponent<ListPage> page = Render(context);

        page.FindAll("#ms-doc-order").Should().BeEmpty();
    }

    private static DocumentsComponentContext Loaded(
        IReadOnlyList<DocumentColumnHeader> columns,
        IReadOnlyList<DocumentColumnHeader> available,
        string sortKey = "id",
        SortDirection direction = SortDirection.Descending)
    {
        var context = new DocumentsComponentContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row("8f1d5a6e-1a2b-4c3d-9e8f-000000000001")], columns: columns) with
        {
            AvailableColumns = available,
            SortKey = sortKey,
            Direction = direction,
        };

        return context;
    }

    private static IRenderedComponent<ListPage> Render(DocumentsComponentContext context) =>
        context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));
}
