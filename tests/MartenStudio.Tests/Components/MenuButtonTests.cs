using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Shared;

using Microsoft.AspNetCore.Components.Web;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The shared menu: a native popover with <c>role="menu"</c>, one tab stop with a roving tabindex inside it,
/// Escape handing focus back to the button, and an offer that fails putting the failure on screen rather
/// than ending the circuit.
/// </summary>
public class MenuButtonTests
{
    private static IRenderedComponent<MenuButton> Render(
        StudioComponentContext context,
        IReadOnlyList<(string Label, string? Detail, Func<Task> OnSelect)> items,
        string elementId = "test-menu") =>
        context.Render<MenuButton>(parameters => parameters
            .Add(x => x.ElementId, elementId)
            .Add(x => x.Label, "Copy from quartz.qrtz_triggers")
            .Add(x => x.Text, "Copy")
            .Add(x => x.ButtonClass, "ms-btn ms-btn-sm test-button")
            .Add(x => x.PopoverClass, "test-popover")
            .Add(x => x.Items, items));

    private static (string Label, string? Detail, Func<Task> OnSelect)[] ThreeItems(List<string> chosen) =>
    [
        ("First", "the first thing", () => { chosen.Add("first"); return Task.CompletedTask; }),
        ("Second", null, () => { chosen.Add("second"); return Task.CompletedTask; }),
        ("Third", "the third thing", () => { chosen.Add("third"); return Task.CompletedTask; }),
    ];

    [Fact]
    public void It_is_a_native_popover_menu_named_for_what_it_is_about()
    {
        using var context = new StudioComponentContext();

        var menu = Render(context, ThreeItems([]));

        IElement button = menu.Find("button.test-button");
        IElement popover = menu.Find(".test-popover");

        popover.GetAttribute("popover").Should().Be("auto", "light dismiss and Escape are the platform's");
        popover.GetAttribute("role").Should().Be("menu");
        popover.GetAttribute("aria-label").Should().Be("Copy from quartz.qrtz_triggers");
        popover.Id.Should().Be("test-menu");

        button.GetAttribute("popovertarget").Should().Be("test-menu");
        button.GetAttribute("aria-haspopup").Should().Be("menu");
        button.TextContent.Trim().Should().Be("Copy");

        menu.FindAll(".ms-menu-item").Should().OnlyContain(x => x.GetAttribute("role") == "menuitem" && x.GetAttribute("popovertargetaction") == "hide");
        menu.TextOfAll(".ms-menu-item-label").Should().Equal("First", "Second", "Third");
        menu.TextOfAll(".ms-menu-item-detail").Should().Equal(["the first thing", "the third thing"], "an item with no detail draws no empty line");
    }

    [Fact]
    public async Task The_arrow_keys_home_and_end_move_the_roving_tabindex_and_the_focus()
    {
        using var context = new StudioComponentContext();

        var menu = Render(context, ThreeItems([]));

        Tabindexes(menu).Should().Equal("0", "-1", "-1");

        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        Tabindexes(menu).Should().Equal("-1", "0", "-1");

        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowUp" });
        Tabindexes(menu).Should().Equal(["-1", "-1", "0"], "the top wraps round to the bottom");

        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "Home" });
        Tabindexes(menu).Should().Equal("0", "-1", "-1");

        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        Tabindexes(menu).Should().Equal("-1", "-1", "0");

        FocusedIds(context).Should().Equal("test-menu-item-1", "test-menu-item-0", "test-menu-item-2", "test-menu-item-0", "test-menu-item-2");
    }

    [Fact]
    public async Task Escape_hands_focus_back_to_the_button_and_the_next_opening_starts_at_the_top()
    {
        using var context = new StudioComponentContext();

        var menu = Render(context, ThreeItems([]));

        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        FocusedIds(context)[^1].Should().Be(menu.Find("button.test-button").Id, "a keyboard user is left where they started");
        Tabindexes(menu).Should().Equal("0", "-1", "-1");
    }

    [Fact]
    public async Task A_modified_key_and_tab_are_left_alone()
    {
        using var context = new StudioComponentContext();

        var menu = Render(context, ThreeItems([]));

        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown", CtrlKey = true });
        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "Tab" });

        Tabindexes(menu).Should().Equal("0", "-1", "-1");
        FocusedIds(context).Should().BeEmpty();
    }

    [Fact]
    public async Task The_button_reports_whether_the_menu_is_open_and_opening_focuses_the_first_item()
    {
        using var context = new StudioComponentContext();

        var menu = Render(context, ThreeItems([]));

        menu.Find("button.test-button").GetAttribute("aria-expanded").Should().Be("false");

        await menu.Find(".test-popover").TriggerEventAsync("ontoggle", EventArgs.Empty);
        menu.Find("button.test-button").GetAttribute("aria-expanded").Should().Be("true");
        FocusedIds(context).Should().Equal("test-menu-item-0");

        await menu.Find(".test-popover").TriggerEventAsync("ontoggle", EventArgs.Empty);
        menu.Find("button.test-button").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public async Task Choosing_an_item_runs_that_item()
    {
        using var context = new StudioComponentContext();
        List<string> chosen = [];

        var menu = Render(context, ThreeItems(chosen));

        await menu.FindAll(".ms-menu-item")[1].ClickAsync(new MouseEventArgs());

        chosen.Should().Equal("second");
    }

    [Fact]
    public async Task An_item_that_fails_puts_the_failure_on_screen_instead_of_ending_the_circuit()
    {
        using var context = new StudioComponentContext();

        var menu = Render(context, [("Explode", null, static () => Task.FromException(new InvalidOperationException("the clipboard is on fire")))]);

        await menu.Find(".ms-menu-item").ClickAsync(new MouseEventArgs());

        context.Toasts.Messages.Should().ContainSingle(x => x.Message == "the clipboard is on fire");
    }

    [Fact]
    public void Two_menus_that_name_no_id_never_share_one()
    {
        using var context = new StudioComponentContext();

        var first = context.Render<MenuButton>(parameters => parameters.Add(x => x.Items, ThreeItems([])));
        var second = context.Render<MenuButton>(parameters => parameters.Add(x => x.Items, ThreeItems([])));

        string firstId = first.Find("[popover]").Id!;
        string secondId = second.Find("[popover]").Id!;

        firstId.Should().NotBe(secondId);
        first.Find("button").GetAttribute("popovertarget").Should().Be(firstId);
        first.Find("[popover]").ClassList.Should().Contain("ms-menu-popover", "a menu whose caller names no classes still gets the shared look");
    }

    [Fact]
    public async Task Focus_is_a_courtesy_a_closed_circuit_does_not_turn_into_an_error()
    {
        using var context = new StudioComponentContext();
        context.JSInterop.SetupVoid("martenStudio.json.focusElement", _ => true)
            .SetException(new Microsoft.JSInterop.JSDisconnectedException("gone"));

        var menu = Render(context, ThreeItems([]));

        await menu.Find(".test-popover").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });

        Tabindexes(menu).Should().Equal("-1", "0", "-1");
    }

    private static List<string> Tabindexes(IRenderedComponent<MenuButton> menu) =>
        [.. menu.FindAll(".ms-menu-item").Select(x => x.GetAttribute("tabindex") ?? string.Empty)];

    private static List<string> FocusedIds(StudioComponentContext context) =>
        [.. context.JSInterop.Invocations["martenStudio.json.focusElement"].Select(x => (string)x.Arguments[0]!)];
}
