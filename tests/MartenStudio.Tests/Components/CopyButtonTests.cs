using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Shared;

using Microsoft.JSInterop;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The icon-only copy: the only name it has, what it puts on the clipboard, what it says afterwards,
/// and what it does when there is no browser on the other end of the circuit.
/// </summary>
public class CopyButtonTests
{
    private const string Value = "11111111-1111-1111-1111-111111111111";

    /// <summary>
    /// An icon has no text, so the label is the whole accessible name. It is the tooltip too, because a
    /// sighted mouse user has exactly the same question about an unlabelled icon.
    /// </summary>
    [Fact]
    public void The_label_is_the_buttons_accessible_name_and_its_tooltip()
    {
        using StudioComponentContext context = NewContext();

        IRenderedComponent<CopyButton> button = Render(context, "Copy stream id");

        IElement element = button.Find("button");
        element.GetAttribute("aria-label").Should().Be("Copy stream id");
        element.GetAttribute("title").Should().Be("Copy stream id");
        element.TextContent.Trim().Should().BeEmpty("the control is an icon, not a word");
    }

    [Fact]
    public void Clicking_it_puts_the_value_on_the_clipboard()
    {
        using StudioComponentContext context = NewContext();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(true);

        IRenderedComponent<CopyButton> button = Render(context, "Copy stream id");
        button.Find("button").Click();

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Should().ContainSingle()
            .Which.Arguments[0].Should().Be(Value);
    }

    /// <summary>
    /// The tick is invisible to a screen reader, so the confirmation is a word in a polite live region.
    /// The region is in the DOM before the copy and empty, because one that arrives with its text
    /// already in it is not reliably announced.
    /// </summary>
    [Fact]
    public void A_copy_that_worked_says_so_in_the_live_region()
    {
        using StudioComponentContext context = NewContext();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(true);

        IRenderedComponent<CopyButton> button = Render(context, "Copy stream id");

        IElement region = button.Find("[aria-live='polite']");
        region.ClassList.Should().Contain("ms-sr-only");
        region.TextContent.Trim().Should().BeEmpty();

        button.Find("button").Click();

        button.Find("[aria-live='polite']").TextContent.Trim().Should().Be("Copied");
    }

    /// <summary>
    /// The confirmation takes itself away again, so a list of fifty of these does not end up a list of
    /// fifty ticks. Waited for rather than slept through: the clear runs on a one-shot timer and is
    /// dispatched back onto the renderer, so the only honest question is whether it has happened yet.
    /// </summary>
    [Fact]
    public void The_confirmation_clears_itself()
    {
        using StudioComponentContext context = NewContext();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(true);

        IRenderedComponent<CopyButton> button = Render(context, "Copy stream id");
        button.Find("button").Click();
        button.Find("[aria-live='polite']").TextContent.Trim().Should().Be("Copied");

        button.WaitForAssertion(
            () => button.Find("[aria-live='polite']").TextContent.Trim().Should().BeEmpty(),
            TimeSpan.FromSeconds(5));
    }

    /// <summary>A copy the browser refused says nothing, because nothing was copied.</summary>
    [Fact]
    public void A_copy_the_browser_refused_does_not_claim_to_have_worked()
    {
        using StudioComponentContext context = NewContext();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(false);

        IRenderedComponent<CopyButton> button = Render(context, "Copy stream id");
        button.Find("button").Click();

        button.Find("[aria-live='polite']").TextContent.Trim().Should().BeEmpty();
    }

    /// <summary>
    /// The case that happens on every closed tab. <c>JSDisconnectedException</c> does not derive from
    /// <c>JSException</c>, so the guard has to be the exception filter and not a catch ladder.
    /// </summary>
    [Fact]
    public void A_copy_whose_circuit_has_gone_does_not_take_the_page_with_it()
    {
        using StudioComponentContext context = NewContext();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true)
            .SetException(new JSDisconnectedException("the circuit closed"));

        IRenderedComponent<CopyButton> button = Render(context, "Copy stream id");

        button.Invoking(x => x.Find("button").Click()).Should().NotThrow();
        button.Find("[aria-live='polite']").TextContent.Trim().Should().BeEmpty();
    }

    /// <summary>A real bug still reaches the handler rather than being swallowed with the rest.</summary>
    [Fact]
    public void A_failure_that_is_not_the_browser_going_away_still_surfaces()
    {
        using StudioComponentContext context = NewContext();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true)
            .SetException(new NotSupportedException("a real bug"));

        IRenderedComponent<CopyButton> button = Render(context, "Copy stream id");

        button.Invoking(x => x.Find("button").Click()).Should().Throw<NotSupportedException>();
    }

    private static IRenderedComponent<CopyButton> Render(StudioComponentContext context, string label) =>
        context.Render<CopyButton>(parameters => parameters
            .Add(x => x.Value, Value)
            .Add(x => x.Label, label));

    private static StudioComponentContext NewContext() => new();
}
