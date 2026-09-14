using Bunit;

using MartenStudio.Components.Shared;
using MartenStudio.Tests.Components;

using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// What the confirm dialog hands out when somebody presses Confirm.
/// </summary>
/// <remarks>
/// <para>
/// P7-fix follow-up 3. <c>SchemaDataService.ApplyAsync</c> re-checks the typed confirmation against the
/// database identity, and that check is only worth anything if the string it receives is the one a person
/// typed. While <c>OnConfirm</c> was a bare <c>EventCallback</c>, the page had nothing to pass but its own
/// server-computed copy of the identity - so the service was comparing a value it had produced against
/// itself, and the check could never fail however the circuit was driven.
/// </para>
/// <para>
/// The dialog is a shared component, but this behaviour is the schema screen's refusal path, which is why
/// the test lives beside it.
/// </para>
/// </remarks>
public class TypedConfirmationTests
{
    [Fact]
    public void Confirming_hands_out_what_was_typed_rather_than_the_phrase_it_was_given()
    {
        using var context = new StudioComponentContext();

        string? received = null;

        var dialog = context.Render<ConfirmDialog>(parameters => parameters
            .Add(x => x.IsOpen, true)
            .Add(x => x.ConfirmationPhrase, "localhost.marten")
            .Add(x => x.OnConfirm, EventCallback.Factory.Create<string>(this, value => received = value)));

        dialog.Find(".ms-confirm-input").Input("  localhost.marten  ");
        dialog.Find(".ms-confirm-actions .ms-button-danger").Click();

        received.Should().Be("localhost.marten", "the trimmed text from the input box is what travels");
    }

    [Fact]
    public void A_mismatching_typed_value_never_reaches_the_callback_at_all()
    {
        using var context = new StudioComponentContext();

        bool confirmed = false;

        var dialog = context.Render<ConfirmDialog>(parameters => parameters
            .Add(x => x.IsOpen, true)
            .Add(x => x.ConfirmationPhrase, "localhost.marten")
            .Add(x => x.OnConfirm, EventCallback.Factory.Create<string>(this, _ => confirmed = true)));

        dialog.Find(".ms-confirm-input").Input("some other database");
        dialog.Find(".ms-confirm-actions .ms-button-danger").Click();

        confirmed.Should().BeFalse();
    }

    /// <summary>
    /// A dialog with no typed confirmation still raises the callback, with an empty string - which is
    /// what lets the other three dialogs in the studio keep their parameterless handlers.
    /// </summary>
    [Fact]
    public void A_plain_confirmation_still_raises_the_callback()
    {
        using var context = new StudioComponentContext();

        string? received = null;

        var dialog = context.Render<ConfirmDialog>(parameters => parameters
            .Add(x => x.IsOpen, true)
            .Add(x => x.OnConfirm, EventCallback.Factory.Create<string>(this, value => received = value)));

        dialog.Find(".ms-confirm-actions .ms-button-danger").Click();

        received.Should().BeEmpty();
    }
}
