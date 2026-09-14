using System.Collections.Immutable;

using Bunit;

using MartenStudio.Services.Documents;
using MartenStudio.Tests.Support;

using ListPage = MartenStudio.Components.Pages.Documents.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// Selecting documents on the list and deleting the selection.
/// </summary>
/// <remarks>
/// <para>
/// The checkboxes are drawn only where they could do something (D4). A studio without
/// <c>DeleteDocuments</c> does not get a column of ticks leading to a disabled button, and a table the
/// studio only discovered does not get one at all - there is no mapping to delete through, and no amount
/// of configuration changes that.
/// </para>
/// <para>
/// The cap is here rather than only in the service because a refusal after ticking six hundred boxes is a
/// refusal that arrives too late to be useful. The service still refuses; this is the part somebody reads
/// before pressing anything.
/// </para>
/// </remarks>
public class DocumentBulkDeleteTests
{
    private static DocumentPage Page(int rows = 3) =>
        FakeDocuments.Page(rows: [.. Enumerable.Range(1, rows).Select(i => FakeDocuments.Row("id-" + i))]);

    private static IRenderedComponent<ListPage> Render(DocumentsComponentContext context, bool softDeleted = true)
    {
        context.Data.Rail = FakeDocuments.Rail(
            documents: [FakeDocuments.Collection("customer", softDeleted: softDeleted)]);

        context.Navigate("marten/documents/customer");
        return context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));
    }

    [Fact]
    public void Without_the_capability_there_is_no_selection_column_and_no_bar()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Page = Page();

        var page = Render(context);

        page.FindAll(".ms-doc-col-select").Should().BeEmpty();
        page.FindAll(".ms-doc-bulk-bar").Should().BeEmpty();
    }

    [Fact]
    public void A_read_only_studio_has_no_selection_column_either()
    {
        using var context = new DocumentsComponentContext();
        context.Options.ReadOnly = true;
        context.Options.Capabilities = MartenStudioCapabilities.All();
        context.Data.Page = Page();

        var page = Render(context);

        page.FindAll(".ms-doc-col-select").Should().BeEmpty();
    }

    [Fact]
    public void A_discovered_table_has_no_selection_column_because_there_is_nothing_to_delete_through()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();
        context.Data.Rail = FakeDocuments.Rail(
            documents: [],
            discovered: [FakeDocuments.Collection("customer", registered: false)]);

        context.Navigate("marten/documents/customer");
        var page = context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        page.FindAll(".ms-doc-col-select").Should().BeEmpty();
    }

    [Fact]
    public void The_bar_appears_with_the_count_only_once_something_is_selected()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();

        var page = Render(context);

        page.FindAll(".ms-doc-bulk-bar").Should().BeEmpty("nothing is selected yet");

        page.FindAll(".ms-doc-select")[0].Change(true);

        page.Find(".ms-doc-bulk-count").TextContent.Trim().Should().Be("1 selected");
        page.Find(".ms-doc-row-selected").Should().NotBeNull();
    }

    [Fact]
    public void The_header_checkbox_selects_the_page_and_then_clears_it()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();

        var page = Render(context);

        page.Find(".ms-doc-select-all").Change(true);
        page.Find(".ms-doc-bulk-count").TextContent.Trim().Should().Be("3 selected");

        page.Find(".ms-doc-select-all").Change(false);
        page.FindAll(".ms-doc-bulk-bar").Should().BeEmpty();
    }

    [Fact]
    public void Pressing_x_on_a_row_selects_it()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();

        var page = Render(context);

        page.FindAll("tr.ms-doc-row")[1].KeyDown("x");

        page.Find(".ms-doc-bulk-count").TextContent.Trim().Should().Be("1 selected");
    }

    [Fact]
    public void The_confirmation_is_typed_with_the_alias_and_says_which_kind_of_delete_it_is()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();

        var page = Render(context);
        page.Find(".ms-doc-select-all").Change(true);
        page.Find(".ms-doc-bulk-delete").Click();

        page.Find(".ms-confirm-dialog h3").TextContent.Should().Be("Delete 3 documents?");
        page.Find(".ms-confirm-label").TextContent.Should().Contain("customer");
        page.Find(".ms-confirm-dialog p").TextContent.Should().Contain("can be undeleted");
    }

    [Fact]
    public void A_collection_that_is_not_soft_deleted_says_the_rows_are_removed()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();

        var page = Render(context, softDeleted: false);
        page.Find(".ms-doc-select-all").Change(true);
        page.Find(".ms-doc-bulk-delete").Click();

        page.Find(".ms-confirm-dialog p").TextContent.Should().Contain("There is no undo");
    }

    [Fact]
    public void Confirming_sends_the_selected_ids_and_the_toast_summarises_the_outcomes()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();
        context.Writes.Bulk = BulkDeleteResult.Completed(
        [
            DeleteResult.SoftDeleted("customer", "id-1"),
            DeleteResult.SoftDeleted("customer", "id-2"),
            DeleteResult.NotFound("customer", "id-3"),
        ]);

        var page = Render(context);
        page.Find(".ms-doc-select-all").Change(true);
        page.Find(".ms-doc-bulk-delete").Click();
        page.Find(".ms-confirm-input").Input("customer");
        page.Find(".ms-confirm-actions button").Click();

        var call = context.Writes.CallsTo("BulkDeleteAsync").Should().ContainSingle().Subject;
        call.Alias.Should().Be("customer");
        call.Ids.Should().Equal("id-1", "id-2", "id-3");

        context.Toasts.Messages.Should().ContainSingle()
            .Which.Message.Should().Contain("2 deleted").And.Contain("1 not found");
    }

    /// <summary>
    /// A refused id stays ticked. That is what lets somebody see which ones did not go and try them
    /// again, rather than losing the selection and having to work out what happened from a toast.
    /// </summary>
    [Fact]
    public void A_refused_id_stays_selected_while_the_deleted_ones_do_not()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();
        context.Writes.Bulk = BulkDeleteResult.Completed(
        [
            DeleteResult.SoftDeleted("customer", "id-1"),
            DeleteResult.SoftDeleted("customer", "id-2"),
            DeleteResult.Refused("customer", "id-3", "The row is locked by another transaction."),
        ]);

        var page = Render(context);
        page.Find(".ms-doc-select-all").Change(true);
        page.Find(".ms-doc-bulk-delete").Click();
        page.Find(".ms-confirm-input").Input("customer");
        page.Find(".ms-confirm-actions button").Click();

        page.Find(".ms-doc-bulk-count").TextContent.Trim().Should().Be("1 selected");
    }

    [Fact]
    public void A_refused_bulk_delete_is_reported_rather_than_reloaded_over()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();
        context.Writes.Bulk = BulkDeleteResult.Refused("'customer' is read-only in this studio.");

        var page = Render(context);
        page.Find(".ms-doc-select-all").Change(true);
        page.Find(".ms-doc-bulk-delete").Click();
        page.Find(".ms-confirm-input").Input("customer");
        page.Find(".ms-confirm-actions button").Click();

        page.Find(".ms-error-alert").TextContent.Should().Contain("read-only");
    }

    /// <summary>
    /// Over the cap the bar says the number and the button is dead, so nobody finds out by being refused
    /// after ticking six hundred boxes.
    /// </summary>
    [Fact]
    public void Past_the_cap_the_bar_says_so_and_the_delete_button_is_disabled()
    {
        int over = DocumentWriteService.MaxBulkDeleteIds + 1;

        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = FakeDocuments.Page(
            rows: [.. Enumerable.Range(1, over).Select(i => FakeDocuments.Row("id-" + i))]);

        var page = Render(context);
        page.Find(".ms-doc-select-all").Change(true);

        page.Find(".ms-doc-bulk-count").TextContent.Trim().Should().Be($"{over:N0} selected");
        page.Find(".ms-doc-bulk-warning").TextContent.Should().Contain("at most 500");
        page.Find(".ms-doc-bulk-delete").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Clear_drops_the_selection_and_copy_ids_puts_them_on_the_clipboard()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();

        var page = Render(context);
        page.Find(".ms-doc-select-all").Change(true);

        page.FindAll(".ms-doc-bulk-actions button")[1].Click();
        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Should().ContainSingle()
            .Which.Arguments[0].Should().Be("id-1\nid-2\nid-3");

        page.FindAll(".ms-doc-bulk-actions button")[2].Click();
        page.FindAll(".ms-doc-bulk-bar").Should().BeEmpty();
    }

    /// <summary>
    /// The selection survives paging - building one across two pages is the only way to act on more than
    /// a page - but not a change of collection, where an id from the last one means nothing.
    /// </summary>
    [Fact]
    public void Changing_collection_drops_the_selection()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = Page();
        context.Data.Rail = FakeDocuments.Rail(
            documents: [FakeDocuments.Collection("customer"), FakeDocuments.Collection("order")]);

        context.Navigate("marten/documents/customer");
        var page = context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));

        page.Find(".ms-doc-select-all").Change(true);
        page.Find(".ms-doc-bulk-count").TextContent.Trim().Should().Be("3 selected");

        context.Navigate("marten/documents/order");
        page.Render(parameters => parameters.Add(x => x.Alias, "order"));

        page.FindAll(".ms-doc-bulk-bar").Should().BeEmpty();
    }

    /// <summary>The empty-state colspan has to grow with the column, or the empty row is one cell short.</summary>
    [Fact]
    public void The_empty_row_spans_the_selection_column_too()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Page = FakeDocuments.Page(rows: ImmutableArray<DocumentRow>.Empty);

        var page = Render(context);

        page.Find(".ms-table-empty").GetAttribute("colspan").Should().Be("5");
    }
}
