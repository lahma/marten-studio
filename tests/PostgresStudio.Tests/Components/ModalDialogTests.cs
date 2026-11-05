using AngleSharp.Dom;

using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using PostgresStudio.Components.Shared;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-A11Y: every studio dialog is a native <c>&lt;dialog&gt;</c> the server renders open (today's overlay) and studio.js upgrades to
/// a modal with <c>showModal()</c>. These prove the half the server owns - the markup, which control is marked for first focus, what
/// is asked of the browser and when, Esc reaching the owner's Cancel, and the overlay surviving when the browser cannot be reached.
/// What the browser then does with it is the browser suites' (Playwright).
/// </summary>
public class ModalDialogTests
{
    private const string Present = "martenStudio.dialog.present";
    private const string Release = "martenStudio.dialog.release";

    private static BunitContext Context()
    {
        BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    private static IRenderedComponent<ConfirmDialog> Confirm(
        BunitContext context, bool open = true, string? phrase = null, Action? cancelled = null) =>
        context.Render<ConfirmDialog>(parameters =>
        {
            parameters
                .Add(x => x.IsOpen, open)
                .Add(x => x.Title, "Archive this stream?")
                .Add(x => x.Message, "It can be restored.")
                .Add(x => x.ConfirmText, "Archive")
                .Add(x => x.ConfirmationPhrase, phrase);

            if (cancelled is not null)
            {
                parameters.Add(x => x.OnCancel, EventCallback.Factory.Create(context, cancelled));
            }
        });

    [Fact]
    public void A_closed_dialog_renders_nothing_at_all()
    {
        using BunitContext context = Context();

        IRenderedComponent<ConfirmDialog> dialog = Confirm(context, open: false);

        dialog.FindAll("dialog").Should().BeEmpty("a closed dialog is not in the page for a screen reader or a Tab key to find");
        context.JSInterop.Invocations.Should().BeEmpty("nothing is asked of the browser for a dialog that is not there");
    }

    [Fact]
    public void An_open_confirmation_is_a_native_dialog_rendered_open_around_the_same_panel()
    {
        using BunitContext context = Context();

        IElement dialog = Confirm(context).Find("dialog");

        dialog.GetAttribute("role").Should().Be("alertdialog");
        dialog.GetAttribute("aria-modal").Should().Be("true");
        dialog.GetAttribute("aria-label").Should().Be("Archive this stream?");
        dialog.HasAttribute("open").Should().BeTrue("the overlay the studio always had is what the server renders; the upgrade is the script's");
        dialog.ClassList.Should().Contain(["ms-modal", "ms-confirm-overlay"]);
        dialog.Id.Should().StartWith("ms-dialog-");

        IElement panel = dialog.QuerySelector(".ms-confirm-dialog")!;
        panel.QuerySelector("h3")!.TextContent.Should().Be("Archive this stream?");
        panel.QuerySelector("p")!.TextContent.Should().Be("It can be restored.");
        panel.QuerySelectorAll(".ms-confirm-actions button").Select(static x => x.TextContent.Trim()).Should().Equal("Archive", "Cancel");
    }

    [Fact]
    public void Opening_asks_the_browser_to_present_that_dialog_with_a_reference_to_call_back_on()
    {
        using BunitContext context = Context();

        IElement dialog = Confirm(context).Find("dialog");

        JSRuntimeInvocation call = context.JSInterop.Invocations[Present].Should().ContainSingle().Subject;
        call.Arguments[0].Should().Be(dialog.Id, "the script finds the dialog by the id the server rendered");
        call.Arguments[1].Should().BeOfType<DotNetObjectReference<ModalDialog>>("Esc has to reach the component");
    }

    [Fact]
    public void A_confirmation_with_nothing_to_type_starts_on_Cancel_because_its_action_is_destructive()
    {
        using BunitContext context = Context();

        IElement dialog = Confirm(context).Find("dialog");

        IElement[] marked = [.. dialog.QuerySelectorAll("[data-ms-dialog-focus]")];
        marked.Should().ContainSingle().Which.TextContent.Trim().Should().Be("Cancel");
    }

    [Fact]
    public void A_confirmation_with_a_phrase_to_type_starts_on_the_box_that_takes_it()
    {
        using BunitContext context = Context();

        IElement dialog = Confirm(context, phrase: "order-17").Find("dialog");

        IElement[] marked = [.. dialog.QuerySelectorAll("[data-ms-dialog-focus]")];
        marked.Should().ContainSingle().Which.Id.Should().Be("ms-confirm-input");
    }

    private static IRenderedComponent<ConfirmDialog> Typed(BunitContext context, List<string> confirmed) =>
        context.Render<ConfirmDialog>(parameters => parameters
            .Add(x => x.IsOpen, true)
            .Add(x => x.ConfirmationPhrase, "DailySales")
            .Add(x => x.OnConfirm, EventCallback.Factory.Create<string>(context, confirmed.Add)));

    [Fact]
    public void The_typed_box_commits_on_input_so_Confirm_is_enabled_before_the_box_loses_focus()
    {
        using BunitContext context = Context();
        IRenderedComponent<ConfirmDialog> dialog = Typed(context, []);
        IElement confirm = dialog.Find(".ms-confirm-actions .ms-button-danger");
        confirm.GetAttribute("aria-disabled").Should().Be("true");
        confirm.HasAttribute("disabled").Should().BeFalse("aria-disabled, not disabled: Tab still lands on it before the round trip that enables it (1.2-UX-FIX-FU)");

        // An input event and no change event: the keyboard visitor types and presses Tab, and Tab must land on Confirm.
        dialog.Find("#ms-confirm-input").Input("DailySales");

        confirm = dialog.Find(".ms-confirm-actions .ms-button-danger");
        confirm.HasAttribute("aria-disabled").Should().BeFalse();
        confirm.HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void A_press_on_Confirm_before_the_phrase_matches_confirms_nothing_even_though_it_can_be_pressed()
    {
        using BunitContext context = Context();
        List<string> confirmed = [];
        IRenderedComponent<ConfirmDialog> dialog = Typed(context, confirmed);

        dialog.Find(".ms-confirm-actions .ms-button-danger").Click();
        dialog.Find("#ms-confirm-input").Input("Daily");
        dialog.Find(".ms-confirm-actions .ms-button-danger").Click();

        confirmed.Should().BeEmpty();

        dialog.Find("#ms-confirm-input").Input("DailySales");
        dialog.Find(".ms-confirm-actions .ms-button-danger").Click();

        confirmed.Should().Equal("DailySales");
    }

    [Fact]
    public void A_reason_the_owner_gives_still_disables_Confirm_outright()
    {
        using BunitContext context = Context();
        IRenderedComponent<ConfirmDialog> dialog = context.Render<ConfirmDialog>(parameters => parameters
            .Add(x => x.IsOpen, true)
            .Add(x => x.ConfirmBlockedReason, "Nothing to do."));

        IElement confirm = dialog.Find(".ms-confirm-actions .ms-button-danger");

        confirm.HasAttribute("disabled").Should().BeTrue();
        confirm.HasAttribute("aria-disabled").Should().BeFalse();
    }

    [Fact]
    public void Enter_in_the_box_confirms_when_the_phrase_matches()
    {
        using BunitContext context = Context();
        List<string> confirmed = [];
        IRenderedComponent<ConfirmDialog> dialog = Typed(context, confirmed);

        dialog.Find("#ms-confirm-input").Input(" DailySales ");
        dialog.Find("#ms-confirm-input").KeyDown(Key.Enter);

        confirmed.Should().Equal("DailySales");
    }

    [Fact]
    public void Enter_in_the_box_does_nothing_while_the_phrase_does_not_match()
    {
        using BunitContext context = Context();
        List<string> confirmed = [];
        IRenderedComponent<ConfirmDialog> dialog = Typed(context, confirmed);

        dialog.Find("#ms-confirm-input").KeyDown(Key.Enter);
        dialog.Find("#ms-confirm-input").Input("DailySale");
        dialog.Find("#ms-confirm-input").KeyDown(Key.Enter);

        confirmed.Should().BeEmpty();
    }

    [Fact]
    public void Another_key_in_a_matching_box_does_not_confirm()
    {
        using BunitContext context = Context();
        List<string> confirmed = [];
        IRenderedComponent<ConfirmDialog> dialog = Typed(context, confirmed);

        dialog.Find("#ms-confirm-input").Input("DailySales");
        dialog.Find("#ms-confirm-input").KeyDown(Key.Tab);

        confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task Esc_from_the_browser_runs_the_owners_Cancel()
    {
        using BunitContext context = Context();
        int cancelled = 0;
        Confirm(context, cancelled: () => cancelled++);

        var reference = (DotNetObjectReference<ModalDialog>)context.JSInterop.Invocations[Present].Single().Arguments[1]!;
        await reference.Value.OnEscapeAsync();

        cancelled.Should().Be(1);
    }

    [Fact]
    public void Closing_gives_the_dialog_back_so_focus_can_return_to_the_control_that_opened_it()
    {
        using BunitContext context = Context();
        IRenderedComponent<ConfirmDialog> dialog = Confirm(context);
        string id = dialog.Find("dialog").Id!;

        dialog.Render(parameters => parameters.Add(x => x.IsOpen, false));

        dialog.FindAll("dialog").Should().BeEmpty();
        context.JSInterop.Invocations[Release].Should().ContainSingle().Which.Arguments[0].Should().Be(id);
    }

    [Fact]
    public async Task A_dialog_disposed_while_open_is_given_back_too()
    {
        using BunitContext context = Context();
        string id = Confirm(context).Find("dialog").Id!;

        await context.DisposeComponentsAsync();

        context.JSInterop.Invocations[Release].Should().ContainSingle().Which.Arguments[0].Should().Be(id);
    }

    [Fact]
    public void Opening_twice_presents_it_once_per_opening()
    {
        using BunitContext context = Context();
        IRenderedComponent<ConfirmDialog> dialog = Confirm(context);

        dialog.Render(parameters => parameters.Add(x => x.Message, "Still open; the text changed."));
        context.JSInterop.Invocations[Present].Should().ContainSingle("a re-render of an open dialog does not present it again");

        dialog.Render(parameters => parameters.Add(x => x.IsOpen, false));
        dialog.Render(parameters => parameters.Add(x => x.IsOpen, true));
        context.JSInterop.Invocations[Present].Should().HaveCount(2);
    }

    [Fact]
    public void A_browser_that_cannot_be_reached_leaves_the_overlay_it_always_had()
    {
        using BunitContext context = Context();
        context.JSInterop.Setup<bool>(Present, _ => true).SetException(new JSDisconnectedException("The circuit has been disconnected."));

        IElement dialog = Confirm(context).Find("dialog");

        dialog.HasAttribute("open").Should().BeTrue();
        dialog.QuerySelectorAll("button").Should().HaveCount(2, "the dialog is complete and usable without the upgrade");
    }

    [Fact]
    public async Task A_circuit_that_has_gone_is_not_an_error_when_the_dialog_is_given_back()
    {
        using BunitContext context = Context();
        IRenderedComponent<ConfirmDialog> dialog = Confirm(context);
        context.JSInterop.SetupVoid(Release, _ => true).SetException(new JSDisconnectedException("The circuit has been disconnected."));

        dialog.Render(parameters => parameters.Add(x => x.IsOpen, false));
        Func<Task> dispose = async () => await context.DisposeComponentsAsync();

        await dispose.Should().NotThrowAsync();
    }

    [Fact]
    public void A_script_that_does_not_know_the_dialog_helpers_is_not_an_error_either()
    {
        using BunitContext context = Context();
        context.JSInterop.Setup<bool>(Present, _ => true).SetException(new JSException("Could not find 'martenStudio.dialog.present'."));

        IElement dialog = Confirm(context).Find("dialog");

        dialog.HasAttribute("open").Should().BeTrue();
    }
}
