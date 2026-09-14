using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;

using DetailModel = MartenStudio.Services.Documents.DocumentDetail;
using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// Two findings from the P2-fix review that the detail page was not acting on: a type warning nothing
/// rendered, and a second read of a row the page already had.
/// </summary>
public class DocumentDetailPolishTests
{
    private const string Id = "8f1d5a6e-1a2b-4c3d-9e8f-000000000001";

    /// <summary>
    /// <c>CheckDotNetType</c> produces this when <c>IDocumentType.TypeFor</c> refuses the row's
    /// <c>mt_doc_type</c> — a discriminator naming a subclass this store no longer registers, which
    /// Marten cannot deserialize at all. It was computed, carried all the way to the component, and then
    /// dropped on the floor.
    /// </summary>
    [Fact]
    public void A_type_warning_that_is_not_a_mismatch_is_rendered_beside_the_mismatch_alert()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail() with
        {
            DotNetTypeWarning =
                "This row's mt_doc_type is 'scooter', which is not a subclass 'vehicle' registers any more. " +
                "Marten cannot deserialize it.",
        });

        var page = Render(context);

        page.Find(".ms-doc-type-warning").TextContent.Should().Contain("not a subclass 'vehicle' registers any more");
    }

    /// <summary>The overwhelmingly common case says nothing, because there is nothing to say.</summary>
    [Fact]
    public void A_row_whose_type_is_fine_renders_no_warning()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        page.FindAll(".ms-doc-type-warning").Should().BeEmpty();
    }

    /// <summary>
    /// The two findings are separate: a mismatch is a row written by a renamed or moved type, a warning is
    /// a discriminator no subclass claims, and either can happen without the other.
    /// </summary>
    [Fact]
    public void A_mismatch_and_a_warning_are_two_alerts_rather_than_one()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail() with
        {
            StoredDotNetType = "Old.Namespace.Customer, Sample",
            ExpectedDotNetType = "Sample.Customer",
            DotNetTypeMismatch = true,
            DotNetTypeWarning = "This row's mt_doc_type is 'scooter', which is not a subclass 'vehicle' registers any more.",
        });

        var page = Render(context);

        page.Find(".ms-doc-meta").TextContent.Should().Contain("mt_dotnet_type does not match this mapping");
        page.Find(".ms-doc-type-warning").TextContent.Should().Contain("not a subclass");
    }

    /// <summary>
    /// The page has the document in hand by the time it asks what it points at. Asking by id made the
    /// service read the same row a second time, which is two round trips for one screen.
    /// </summary>
    [Fact]
    public void The_related_documents_are_read_from_the_document_already_in_hand()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        Render(context);

        context.Data.RelatedByDetailCalls.Should().Be(1);
        context.Data.RelatedByIdCalls.Should().Be(0, "the row was already read; reading it again is a second round trip");
    }

    /// <summary>A document that is not there has nothing to be related to, and is not asked about.</summary>
    [Fact]
    public void A_missing_document_asks_for_no_related_documents_at_all()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Missing("No row with that id.");

        Render(context);

        context.Data.RelatedByDetailCalls.Should().Be(0);
        context.Data.RelatedByIdCalls.Should().Be(0);
    }

    private static IRenderedComponent<DetailPage> Render(DocumentsComponentContext context)
    {
        context.Navigate($"marten/documents/customer/doc?id={Id}");
        return context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));
    }

    private static DetailModel Detail() => new()
    {
        Alias = "customer",
        Id = Id,
        Json = """{"Name":"Customer 01"}""",
        TextBytes = 22,
        StoredBytes = 22,
        Columns =
        [
            new PhysicalColumnValue("id", Id, PhysicalColumnRole.Identity),
            new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
        ],
        TableName = "\"studio_sample\".\"mt_doc_customer\"",
        ClrType = typeof(DocumentDetailPolishTests),
        IsRegistered = true,
        IdColumnType = DocumentIdColumnType.Uuid,
    };
}
