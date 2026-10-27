using AngleSharp.Dom;

using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;

using PostgresStudio.Services.Json;

using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using DetailModel = MartenStudio.Services.Documents.DocumentDetail;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// M5: Marten's own preview of a save (9.53+) in the edit flow - a section beside the round trip, and a save Marten
/// disagrees with or refuses held at the dialog the way a lossy one is.
/// </summary>
/// <remarks>
/// The verdicts are fed in through the fake write service, so these hold at every Marten; what Marten itself answers is
/// <c>MartenEditPreviewTests</c>' and the live tests'.
/// </remarks>
public class MartenPreviewDialogTests
{
    private const string Id = "8f1d5a6e-0000-0000-0000-000000000001";
    private const string Stored = """{"Name":"Customer 01"}""";
    private const string Edited = """{"Name":"Customer 02"}""";

    [Fact]
    public void A_save_Marten_agrees_with_goes_straight_through()
    {
        using DocumentsComponentContext context = Context(new MartenPreviewCheck
        {
            Verdict = MartenPreviewVerdict.Agrees,
            MartenJson = Edited,
        });

        IRenderedComponent<DetailPage> page = Edit(context);
        page.Find(".ms-editor-save").Click();

        context.Writes.CallsTo("SaveAsync").Should().ContainSingle("agreement is not something to stop and read");
        page.FindAll(".ms-write-preview").Should().BeEmpty();
    }

    /// <summary>
    /// A refusal from Marten's own preview, held at the dialog with Marten's own words, and saved only after the confirm.
    /// </summary>
    [Fact]
    public void A_save_Marten_refuses_opens_the_dialog_first_and_saves_only_after_the_confirm()
    {
        using DocumentsComponentContext context = Context(new MartenPreviewCheck
        {
            Verdict = MartenPreviewVerdict.Refused,
            Reason = "ArgumentException: The id in the JSON does not match the requested id 'x'.",
        });

        IRenderedComponent<DetailPage> page = Edit(context);
        page.Find(".ms-editor-save").Click();

        context.Writes.CallsTo("SaveAsync").Should().BeEmpty("a save Marten would refuse is read before it is made");

        IElement section = page.Find(".ms-write-preview-marten");
        section.GetAttribute("data-verdict").Should().Be("Refused");
        section.QuerySelector(".ms-write-preview-title")!.TextContent.Trim().Should().Be("What Marten's own preview keeps");
        section.QuerySelector(".ms-alert-warning")!.TextContent.Should().Contain("does not match the requested id");

        IElement confirm = page.FindAll(".ms-write-preview-actions button")[1];
        confirm.ClassList.Should().Contain("ms-button-danger");
        confirm.Click();

        context.Writes.CallsTo("SaveAsync").Should().ContainSingle()
            .Which.AcknowledgeDrops.Should().BeFalse("nothing was dropped; the confirm acknowledged Marten's warning, not a loss");
    }

    [Fact]
    public void A_disagreement_shows_where_the_two_differ()
    {
        const string martenKeeps = """{"Name":"Customer 03"}""";

        using DocumentsComponentContext context = Context(new MartenPreviewCheck
        {
            Verdict = MartenPreviewVerdict.Disagrees,
            MartenJson = martenKeeps,
            Differences = RoundTripDiffer.Diff(Edited, martenKeeps),
        });

        IRenderedComponent<DetailPage> page = Edit(context);
        page.Find(".ms-editor-save").Click();

        context.Writes.CallsTo("SaveAsync").Should().BeEmpty();

        IElement section = page.Find(".ms-write-preview-marten");
        section.GetAttribute("data-verdict").Should().Be("Disagrees");
        section.TextContent.Should().Contain("keeps something different from the round trip above");
        section.TextContent.Should().Contain("Customer 03");
    }

    /// <summary>Below 9.53 the section is one quiet line naming the Marten it needs, and nothing is held.</summary>
    [Fact]
    public void Below_9_53_the_section_is_one_line_naming_the_Marten_it_needs_and_holds_nothing()
    {
        const string line = "Marten's own preview of a save (PreviewDocumentJsonAsync) needs Marten 9.53+; this host runs Marten 9.39.0.";

        using DocumentsComponentContext context = Context(new MartenPreviewCheck
        {
            Verdict = MartenPreviewVerdict.Unavailable,
            Reason = line,
        });

        IRenderedComponent<DetailPage> page = Edit(context);
        page.Find(".ms-editor-preview").Click();

        IElement section = page.Find(".ms-write-preview-marten");
        section.QuerySelector(".ms-muted")!.TextContent.Trim().Should().Be(line);
        section.QuerySelectorAll(".ms-alert").Should().BeEmpty();
        page.FindAll(".ms-write-preview-actions button")[1].ClassList.Should().Contain("ms-button-primary");
    }

    private static DocumentsComponentContext Context(MartenPreviewCheck check)
    {
        var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Preview = FakeDocumentWriteService.Ready(Stored, Edited) with { MartenCheck = check };
        context.JSInterop.Setup<string?>("martenStudio.json.readValue", _ => true).SetResult(Edited);
        return context;
    }

    private static IRenderedComponent<DetailPage> Edit(DocumentsComponentContext context)
    {
        context.Navigate($"marten/documents/customer/doc?id={Id}");
        IRenderedComponent<DetailPage> page = context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));
        page.Find(".ms-doc-edit-btn").Click();
        return page;
    }

    private static DetailModel Detail() => new()
    {
        Alias = "customer",
        Id = Id,
        Json = Stored,
        TextBytes = 22,
        StoredBytes = 22,
        Columns =
        [
            new PhysicalColumnValue("id", Id, PhysicalColumnRole.Identity),
            new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
            new PhysicalColumnValue("mt_version", "00000000-0000-0000-0000-000000000001", PhysicalColumnRole.Metadata),
        ],
        TableName = "\"studio_sample\".\"mt_doc_customer\"",
        ClrType = typeof(MartenPreviewDialogTests),
        IsRegistered = true,
        IdColumnType = DocumentIdColumnType.Uuid,
    };
}
