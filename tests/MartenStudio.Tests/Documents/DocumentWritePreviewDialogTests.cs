using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Documents;
using MartenStudio.Services.Documents;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using PostgresStudio.Components.Shared;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// 1.2-A11Y: the round-trip preview is a native modal like every studio dialog - and it starts on the safe choice: Save when saving
/// loses nothing and Marten agrees, "Back to the editor" when it does not (then Save is the danger button). Esc is "Back to the
/// editor". What the browser does with the upgrade is the browser suites'.
/// </summary>
public class DocumentWritePreviewDialogTests
{
    private const string Present = "martenStudio.dialog.present";

    private static BunitContext Context()
    {
        BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static IRenderedComponent<DocumentWritePreviewDialog> Render(
        BunitContext context, WritePreview? preview, bool open = true, Action? cancelled = null) =>
        context.Render<DocumentWritePreviewDialog>(parameters =>
        {
            parameters.Add(x => x.IsOpen, open).Add(x => x.Model, preview);

            if (cancelled is not null)
            {
                parameters.Add(x => x.OnCancel, EventCallback.Factory.Create(context, cancelled));
            }
        });

    private static IElement[] Marked(IRenderedComponent<DocumentWritePreviewDialog> dialog) =>
        [.. dialog.FindAll("[data-ms-dialog-focus]")];

    [Fact]
    public void It_is_a_native_dialog_rendered_open_with_its_panel_inside()
    {
        using BunitContext context = Context();

        IElement dialog = Render(context, FakeDocumentWriteService.Ready(dropped: "legacyNote")).Find("dialog");

        dialog.GetAttribute("role").Should().Be("dialog");
        dialog.GetAttribute("aria-modal").Should().Be("true");
        dialog.GetAttribute("aria-label").Should().Be("What saving this document would do");
        dialog.HasAttribute("open").Should().BeTrue();
        dialog.ClassList.Should().Contain(["ms-modal", "ms-confirm-overlay", "ms-write-preview-overlay"]);
        dialog.QuerySelector(".ms-confirm-dialog.ms-write-preview h3")!.TextContent.Should().Be("What saving would do");
        context.JSInterop.Invocations[Present].Should().ContainSingle();
    }

    [Fact]
    public void Nothing_is_rendered_until_it_is_open_and_has_a_preview()
    {
        using BunitContext context = Context();

        Render(context, FakeDocumentWriteService.Ready(), open: false).FindAll("dialog").Should().BeEmpty();
        Render(context, null).FindAll("dialog").Should().BeEmpty();
    }

    [Fact]
    public void A_save_that_drops_something_starts_on_Back_to_the_editor_and_Save_is_the_danger_button()
    {
        using BunitContext context = Context();
        IRenderedComponent<DocumentWritePreviewDialog> dialog = Render(context, FakeDocumentWriteService.Ready(dropped: "legacyNote"));

        IElement back = dialog.FindAll(".ms-write-preview-actions button")[0];
        IElement save = dialog.FindAll(".ms-write-preview-actions button")[1];

        Marked(dialog).Should().ContainSingle().Which.Should().BeSameAs(back);
        back.TextContent.Trim().Should().Be("Back to the editor");
        save.ClassList.Should().Contain("ms-button-danger");
    }

    [Fact]
    public void A_save_that_loses_nothing_starts_on_Save()
    {
        using BunitContext context = Context();
        IRenderedComponent<DocumentWritePreviewDialog> dialog = Render(context, FakeDocumentWriteService.Ready());

        IElement save = dialog.FindAll(".ms-write-preview-actions button")[1];

        Marked(dialog).Should().ContainSingle().Which.Should().BeSameAs(save);
        save.ClassList.Should().Contain("ms-button-primary");
    }

    [Fact]
    public async Task Esc_is_Back_to_the_editor()
    {
        using BunitContext context = Context();
        int cancelled = 0;
        Render(context, FakeDocumentWriteService.Ready(dropped: "legacyNote"), cancelled: () => cancelled++);

        var reference = (DotNetObjectReference<ModalDialog>)context.JSInterop.Invocations[Present].Single().Arguments[1]!;
        await reference.Value.OnEscapeAsync();

        cancelled.Should().Be(1);
    }
}
