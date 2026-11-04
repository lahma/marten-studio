using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Shared;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
// ShortcutSheet reads StudioDescriptor; the Marten half is MartenStudio.Tests' (ShortcutSheetMartenTests).

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-A11Y: the keyboard shortcut sheet <c>?</c> opens. The sheet is part of the shell and shown by studio.js, so what the server owns
/// is its markup (a closed native dialog with a Close button the script answers to) and its list - the shared groups, then the
/// studio's own, so a Postgres Studio host is never told about a key only a Marten page has.
/// </summary>
public class ShortcutSheetTests
{
    private static IRenderedComponent<ShortcutSheet> Render(StudioDescriptor descriptor)
    {
        BunitContext context = new();
        context.Services.AddSingleton(descriptor);
        return context.Render<ShortcutSheet>();
    }

    private static string[] Titles(IRenderedComponent<ShortcutSheet> sheet) =>
        [.. sheet.FindAll(".ms-shortcuts-title").Select(static x => x.TextContent.Trim())];

    private static string[] Does(IRenderedComponent<ShortcutSheet> sheet, string group) =>
        [.. sheet.FindAll(".ms-shortcuts-group")
            .Single(x => x.QuerySelector(".ms-shortcuts-title")!.TextContent.Trim() == group)
            .QuerySelectorAll(".ms-shortcuts-does")
            .Select(static x => x.TextContent.Trim())];

    [Fact]
    public void The_sheet_is_a_native_dialog_that_is_in_the_page_and_closed()
    {
        IRenderedComponent<ShortcutSheet> sheet = Render(PostgresStudioDescriptor.Instance);

        IElement dialog = sheet.Find("dialog#ms-shortcuts");
        dialog.HasAttribute("open").Should().BeFalse("studio.js shows it with showModal() on '?'");
        dialog.ClassList.Should().Contain("ms-modal");
        dialog.GetAttribute("aria-labelledby").Should().Be("ms-shortcuts-title");
        sheet.Find("#ms-shortcuts-title").TextContent.Should().Be("Keyboard shortcuts");
    }

    [Fact]
    public void Close_is_a_plain_button_the_script_answers_to_and_the_one_that_gets_focus()
    {
        IRenderedComponent<ShortcutSheet> sheet = Render(PostgresStudioDescriptor.Instance);

        IElement close = sheet.Find("button[data-ms-dialog-close]");
        close.TextContent.Trim().Should().Be("Close");
        close.GetAttribute("type").Should().Be("button", "the studio has no forms (D23)");
        close.HasAttribute("data-ms-dialog-focus").Should().BeTrue();
        sheet.FindAll("[data-ms-dialog-focus]").Should().ContainSingle();
    }

    [Fact]
    public void The_scrolling_list_can_be_reached_from_the_keyboard_and_is_named()
    {
        IElement body = Render(PostgresStudioDescriptor.Instance).Find(".ms-shortcuts-body");

        body.GetAttribute("tabindex").Should().Be("0");
        body.GetAttribute("role").Should().Be("region");
        body.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_two_global_keys_come_first_and_every_other_key_names_where_it_works()
    {
        IRenderedComponent<ShortcutSheet> sheet = Render(PostgresStudioDescriptor.Instance);

        Titles(sheet).Should().Equal(
            "Anywhere",
            "In a dialog",
            "Menus",
            "SQL editor",
            "JSON viewer (a row has focus)",
            "JSON viewer search box",
            "Table rows (a row has focus)",
            "Plan icicle (a frame has focus)");

        Does(sheet, "Anywhere").Should().Equal(
            "Show this list (not while typing in a field)",
            "Close this list, or cancel the open dialog");
    }

    [Fact]
    public void A_chord_is_drawn_as_keycaps_and_alternatives_are_told_apart()
    {
        IRenderedComponent<ShortcutSheet> sheet = Render(PostgresStudioDescriptor.Instance);

        IElement run = sheet.FindAll(".ms-shortcuts-row")
            .Single(static x => x.QuerySelector(".ms-shortcuts-does")!.TextContent.StartsWith("Run the statement", StringComparison.Ordinal));
        run.QuerySelectorAll("kbd.ms-kbd").Select(static x => x.TextContent).Should().Equal("Ctrl", "Enter");

        IElement next = sheet.FindAll(".ms-shortcuts-row")
            .Single(static x => x.QuerySelector(".ms-shortcuts-does")!.TextContent == "Next or previous match");
        next.QuerySelectorAll("kbd.ms-kbd").Select(static x => x.TextContent).Should().Equal("Enter", "Shift", "Enter");
        next.QuerySelectorAll(".ms-shortcuts-or").Should().ContainSingle("two ways to press it");
    }

    [Fact]
    public void Postgres_Studio_lists_nothing_that_only_a_Marten_page_answers_to()
    {
        IRenderedComponent<ShortcutSheet> sheet = Render(PostgresStudioDescriptor.Instance);

        PostgresStudioDescriptor.Instance.ExtraShortcuts.Should().BeEmpty();
        Titles(sheet).Should().NotContain(static x => x.Contains("Document", StringComparison.Ordinal));
    }
}
