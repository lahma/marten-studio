using AngleSharp.Dom;

using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Json;

using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using DetailModel = MartenStudio.Services.Documents.DocumentDetail;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The three controls that change a document, and the flow behind each of them.
/// </summary>
/// <remarks>
/// <para>
/// The gating tests are the point of the file. Every one of them asserts what is <em>rendered</em>, and
/// none of them asserts that the write was prevented - the service does that, and
/// <c>DocumentWriteVisibilityLiveTests</c> and the capability tests prove it against a real store. What
/// is checked here is the other half of D4: that a studio mounted read-only does not look like a product
/// with everything greyed out, that a capability that is merely off says which option would turn it on,
/// and that a table nobody mapped offers nothing at all.
/// </para>
/// <para>
/// The flow tests follow the token. The preview reports the row's version; the save has to carry that
/// same version back, and has to acknowledge dropped members only after somebody has been shown them.
/// Neither fact is visible in the markup, so both are read off the recorded calls.
/// </para>
/// </remarks>
public class DocumentWriteUiTests
{
    private const string Id = "8f1d5a6e-0000-0000-0000-000000000001";
    private const string Stored = """{"Name":"Customer 01"}""";

    private static DetailModel Detail(
        bool registered = true,
        bool deleted = false,
        bool softDelete = true,
        Type? clrType = null)
    {
        List<PhysicalColumnValue> columns =
        [
            new PhysicalColumnValue("id", Id, PhysicalColumnRole.Identity),
            new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
            new PhysicalColumnValue("mt_version", "00000000-0000-0000-0000-000000000001", PhysicalColumnRole.Metadata),
        ];

        if (softDelete)
        {
            columns.Add(new PhysicalColumnValue("mt_deleted", deleted ? "True" : "False", PhysicalColumnRole.Metadata));
        }

        return new DetailModel
        {
            Alias = "customer",
            Id = Id,
            Json = Stored,
            TextBytes = 22,
            StoredBytes = 22,
            Columns = columns,
            TableName = "\"studio_sample\".\"mt_doc_customer\"",
            ClrType = clrType ?? typeof(DocumentWriteUiTests),
            IsRegistered = registered,
            IsDeleted = deleted,
            IdColumnType = DocumentIdColumnType.Uuid,
        };
    }

    private static IRenderedComponent<DetailPage> Render(DocumentsComponentContext context)
    {
        context.Navigate($"marten/documents/customer/doc?id={Id}");
        return context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));
    }

    /// <summary>Answers the editor's live-value call the way a browser would.</summary>
    private static void LiveValue(DocumentsComponentContext context, string text) =>
        context.JSInterop.Setup<string?>("martenStudio.json.readValue", _ => true).SetResult(text);

    private static IElement? Action(IRenderedComponent<DetailPage> page, string selector) =>
        page.Nodes.QuerySelector(selector);

    // --------------------------------------------------------------------------------------------
    // Gating
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void A_read_only_studio_renders_no_write_controls_at_all()
    {
        using var context = new DocumentsComponentContext();
        context.Options.ReadOnly = true;
        context.Options.Capabilities = MartenStudioCapabilities.All();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        page.FindAll(".ms-doc-write-actions").Should().BeEmpty(
            "a studio a host mounted read-only should not read like a product with the buttons greyed out");
    }

    [Fact]
    public void A_capability_that_is_merely_off_renders_the_control_disabled_and_names_the_option()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        var edit = Action(page, ".ms-doc-edit-btn");
        edit.Should().NotBeNull();
        edit!.HasAttribute("disabled").Should().BeTrue();
        edit.GetAttribute("title").Should().Be("Requires MartenStudioOptions.Capabilities.EditDocuments");

        var delete = Action(page, ".ms-doc-delete-btn");
        delete!.HasAttribute("disabled").Should().BeTrue();
        delete.GetAttribute("title").Should().Be("Requires MartenStudioOptions.Capabilities.DeleteDocuments");
    }

    [Fact]
    public void Both_controls_are_live_once_the_capabilities_are_granted()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        Action(page, ".ms-doc-edit-btn")!.HasAttribute("disabled").Should().BeFalse();
        Action(page, ".ms-doc-delete-btn")!.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void A_discovered_table_offers_nothing_because_there_is_no_type_to_write_it_as()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(registered: false));

        var page = Render(context);

        page.FindAll(".ms-doc-write-actions").Should().BeEmpty();
    }

    /// <summary>
    /// Dead letters have a screen of their own gated on <c>ManageDeadLetters</c>. Reaching the same rows
    /// through the document path would put <c>EditDocuments</c> in charge of data another capability
    /// answers for, which is exactly what D4 is arranged to prevent.
    /// </summary>
    [Fact]
    public void Martens_own_bookkeeping_offers_nothing_even_with_every_capability_granted()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(
            Detail(clrType: typeof(JasperFx.Events.Daemon.DeadLetterEvent)));

        var page = Render(context);

        page.FindAll(".ms-doc-write-actions").Should().BeEmpty();
    }

    [Fact]
    public void A_soft_deleted_document_offers_undelete_instead_of_delete()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(deleted: true));

        var page = Render(context);

        Action(page, ".ms-doc-undelete-btn").Should().NotBeNull();
        Action(page, ".ms-doc-delete-btn").Should().BeNull("it is already deleted");
        page.Find(".ms-doc-deleted-banner").TextContent.Should().Contain("soft-deleted");
    }

    [Fact]
    public void A_collection_that_is_not_soft_deleted_says_the_row_is_removed()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(softDelete: false));

        var page = Render(context);

        Action(page, ".ms-doc-delete-btn")!.TextContent.Trim().Should().Be("Delete");
    }

    // --------------------------------------------------------------------------------------------
    // Edit
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void Pressing_edit_swaps_the_viewer_for_the_editor_after_probing_the_round_trip()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Preview = FakeDocumentWriteService.Ready(Stored, Stored);

        var page = Render(context);
        page.Find(".ms-doc-edit-btn").Click();

        page.FindAll("textarea.ms-editor-input").Should().ContainSingle();
        page.FindAll(".ms-json-view").Should().BeEmpty("the editor takes the viewer's place, it does not cover it");

        var probe = context.Writes.CallsTo("PreviewAsync").Should().ContainSingle().Subject;
        probe.Json.Should().Be(Stored, "the probe previews the document exactly as it is stored");
    }

    /// <summary>
    /// A type the round trip cannot construct is refused before anybody types into it, not after.
    /// </summary>
    [Fact]
    public void An_unconstructible_type_never_opens_the_editor_and_the_button_carries_the_reason()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Preview = WritePreview.Refused(
            "customer",
            Id,
            "'Customer' has no parameterless constructor the serializer can use.",
            typeIsConstructible: false);

        var page = Render(context);
        page.Find(".ms-doc-edit-btn").Click();

        page.FindAll("textarea.ms-editor-input").Should().BeEmpty();
        page.Find(".ms-doc-write-error").TextContent.Should().Contain("parameterless constructor");

        var edit = Action(page, ".ms-doc-edit-btn")!;
        edit.HasAttribute("disabled").Should().BeTrue();
        edit.GetAttribute("title").Should().Contain("parameterless constructor");
    }

    [Fact]
    public void Saving_a_clean_edit_carries_the_previewed_version_and_acknowledges_nothing()
    {
        const string edited = """{"Name":"Customer 02"}""";

        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Preview = FakeDocumentWriteService.Ready(Stored, edited);
        LiveValue(context, edited);

        var page = Render(context);
        page.Find(".ms-doc-edit-btn").Click();
        page.Find(".ms-editor-save").Click();

        var save = context.Writes.CallsTo("SaveAsync").Should().ContainSingle().Subject;
        save.Json.Should().Be(edited);
        save.ExpectedToken.Should().Be(FakeDocumentWriteService.Token(1), "the version the preview reported");
        save.AcknowledgeDrops.Should().BeFalse("nothing was dropped, so there was nothing to acknowledge");

        context.Toasts.Messages.Should().ContainSingle(x => x.Message.Contains("Saved", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole of D7 in one test: a save that would cost something does not happen until the dialog has
    /// said what, and the acknowledgement only goes across after the confirm.
    /// </summary>
    [Fact]
    public void A_save_that_would_drop_members_opens_the_dialog_first_and_saves_only_after_the_confirm()
    {
        const string edited = """{"Name":"Customer 02"}""";

        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Preview = FakeDocumentWriteService.Ready(Stored, edited, "legacyNote", "oldField");
        LiveValue(context, edited);

        var page = Render(context);
        page.Find(".ms-doc-edit-btn").Click();
        page.Find(".ms-editor-save").Click();

        context.Writes.CallsTo("SaveAsync").Should().BeEmpty("nothing is written until the loss has been read");

        var dialog = page.Find(".ms-write-preview");
        dialog.QuerySelector(".ms-write-preview-loss")!.TextContent.Should().Contain("legacyNote");
        dialog.QuerySelectorAll(".ms-write-preview-title").Select(x => x.TextContent.Trim())
            .Should().Equal(["What the round trip loses", "What your edit changes"]);

        var confirm = page.FindAll(".ms-write-preview-actions button")[1];
        confirm.TextContent.Trim().Should().Be("Save (2 properties will be dropped)");
        confirm.Click();

        var save = context.Writes.CallsTo("SaveAsync").Should().ContainSingle().Subject;
        save.AcknowledgeDrops.Should().BeTrue();
    }

    [Fact]
    public void The_save_button_states_the_price_once_a_preview_has_found_one()
    {
        const string edited = """{"Name":"Customer 02"}""";

        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Preview = FakeDocumentWriteService.Ready(Stored, edited, "legacyNote");
        LiveValue(context, edited);

        var page = Render(context);
        page.Find(".ms-doc-edit-btn").Click();

        page.Find(".ms-editor-save").TextContent.Trim().Should().Be(
            "Save", "before any preview the studio has not checked what a save would cost");

        page.Find(".ms-editor-preview").Click();

        page.Find(".ms-editor-save").TextContent.Trim().Should().Be("Save (1 property will be dropped)");
        context.Writes.CallsTo("SaveAsync").Should().BeEmpty("preview previews; it does not save");
    }

    [Fact]
    public void A_conflict_is_an_inline_notice_with_reload_and_overwrite_and_never_a_lost_update()
    {
        const string edited = """{"Name":"Customer 02"}""";

        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Preview = FakeDocumentWriteService.Ready(Stored, edited);
        context.Writes.Saves.Enqueue(WriteResult.Conflict(FakeDocumentWriteService.Token(9)));
        LiveValue(context, edited);

        var page = Render(context);
        page.Find(".ms-doc-edit-btn").Click();
        page.Find(".ms-editor-save").Click();

        var notice = page.Find(".ms-doc-conflict");
        notice.TextContent.Should().Contain("This document changed while you were editing.");
        notice.QuerySelectorAll("button").Select(x => x.TextContent.Trim()).Should().Equal(["Reload", "Overwrite"]);

        page.FindAll("textarea.ms-editor-input").Should().ContainSingle("the edit is still there to act on");
    }

    /// <summary>
    /// Overwrite is not a retry: the preview is re-run first, so the token the second save carries is the
    /// one the other writer left rather than the one the editor opened with.
    /// </summary>
    [Fact]
    public void Overwrite_re_previews_against_the_row_as_it_is_now_and_then_saves()
    {
        const string edited = """{"Name":"Customer 02"}""";

        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var first = FakeDocumentWriteService.Ready(Stored, edited);
        var second = WritePreview.Ready(
            "customer", Id, "Customer", """{"Name":"Somebody else"}""", edited, edited,
            JsonDiffResult.Empty, JsonDiffResult.Empty, FakeDocumentWriteService.Token(9));

        context.Writes.Previews.Enqueue(first);   // the probe
        context.Writes.Previews.Enqueue(first);   // the save's own preview
        context.Writes.Previews.Enqueue(second);  // the overwrite's re-preview
        context.Writes.Saves.Enqueue(WriteResult.Conflict(FakeDocumentWriteService.Token(9)));
        LiveValue(context, edited);

        var page = Render(context);
        page.Find(".ms-doc-edit-btn").Click();
        page.Find(".ms-editor-save").Click();
        page.FindAll(".ms-doc-conflict-actions button")[1].Click();

        var saves = context.Writes.CallsTo("SaveAsync");
        saves.Should().HaveCount(2);
        saves[0].ExpectedToken.Should().Be(FakeDocumentWriteService.Token(1));
        saves[1].ExpectedToken.Should().Be(
            FakeDocumentWriteService.Token(9),
            "the overwrite carries the version the other writer left");
    }

    [Fact]
    public void A_refusal_is_rendered_where_the_editor_is_rather_than_in_a_toast_that_scrolls_away()
    {
        const string edited = """{"Name":"Customer 02"}""";

        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Preview = FakeDocumentWriteService.Ready(Stored, edited);
        context.Writes.Saves.Enqueue(WriteResult.Refused(
            "This row's version column is empty, so there is nothing to check the save against."));
        LiveValue(context, edited);

        var page = Render(context);
        page.Find(".ms-doc-edit-btn").Click();
        page.Find(".ms-editor-save").Click();

        page.Find(".ms-doc-edit-refusal").TextContent.Should().Contain("version column is empty");
    }

    /// <summary>
    /// The write authorization policy says no on a circuit that has been open since before it did. That
    /// is a sentence on the page, never an unhandled exception that takes the circuit with it (§4.8).
    /// </summary>
    [Fact]
    public void A_service_that_throws_is_a_message_on_the_page_and_not_a_broken_circuit()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Failure = new StudioNotAuthorizedException(new StudioScope("default", string.Empty, null));

        var page = Render(context);
        page.Find(".ms-doc-edit-btn").Click();

        page.Find(".ms-doc-write-error").TextContent.Should().NotBeEmpty();
        page.FindAll("textarea.ms-editor-input").Should().BeEmpty();
    }

    // --------------------------------------------------------------------------------------------
    // Delete and undelete
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void The_delete_dialog_asks_for_the_document_id_and_says_which_kind_of_delete_it_is()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);
        page.Find(".ms-doc-delete-btn").Click();

        page.Find(".ms-confirm-label").TextContent.Should().Contain(Id);
        page.Find(".ms-confirm-dialog p").TextContent.Should().Contain("can be undeleted");
        page.Find(".ms-confirm-actions button").HasAttribute("disabled").Should().BeTrue(
            "the phrase has not been typed yet");
    }

    [Fact]
    public void A_hard_delete_dialog_says_the_row_is_removed_and_that_there_is_no_undo()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(softDelete: false));

        var page = Render(context);
        page.Find(".ms-doc-delete-btn").Click();

        page.Find(".ms-confirm-dialog p").TextContent.Should().Contain("There is no undo");
    }

    [Fact]
    public void Confirming_the_delete_calls_the_service_and_the_toast_names_the_outcome()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Delete = DeleteResult.SoftDeleted("customer", Id);

        var page = Render(context);
        page.Find(".ms-doc-delete-btn").Click();
        page.Find(".ms-confirm-input").Input(Id);
        page.Find(".ms-confirm-actions button").Click();

        context.Writes.CallsTo("DeleteAsync").Should().ContainSingle().Which.Id.Should().Be(Id);
        context.Toasts.Messages.Should().ContainSingle(x => x.Message.Contains("marked deleted", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_delete_is_reported_as_refused_rather_than_as_done()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Writes.Delete = DeleteResult.Refused("customer", Id, "The row is locked by another transaction.");

        var page = Render(context);
        page.Find(".ms-doc-delete-btn").Click();
        page.Find(".ms-confirm-input").Input(Id);
        page.Find(".ms-confirm-actions button").Click();

        page.Find(".ms-doc-write-error").TextContent.Should().Contain("locked");
    }

    [Fact]
    public void Undelete_goes_through_the_service_and_says_what_happened()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(deleted: true));
        context.Writes.Undelete = DeleteResult.Undeleted("customer", Id);

        var page = Render(context);
        page.Find(".ms-doc-undelete-btn").Click();

        context.Writes.CallsTo("UndeleteAsync").Should().ContainSingle().Which.Id.Should().Be(Id);
        context.Toasts.Messages.Should().ContainSingle();
    }

    /// <summary>
    /// The one undelete the studio cannot do. It is a refusal naming the id type rather than a body
    /// rewrite, because a type the studio cannot address by id is the type most likely to lose something
    /// on the way through a round trip.
    /// </summary>
    [Fact]
    public void An_undelete_the_studio_cannot_express_is_reported_as_refused()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(deleted: true));
        context.Writes.Undelete = DeleteResult.Refused(
            "customer",
            Id,
            "The studio cannot construct an id value of type 'CustomerId' for 'Customer'. " +
            "Undelete it through Marten. Nothing was changed.");

        var page = Render(context);
        page.Find(".ms-doc-undelete-btn").Click();

        page.Find(".ms-doc-write-error").TextContent.Should().Contain("Undelete it through Marten");
    }
}
