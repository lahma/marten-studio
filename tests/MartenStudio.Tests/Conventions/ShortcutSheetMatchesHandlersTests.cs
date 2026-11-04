using System.Text.RegularExpressions;

using MartenStudio.Services;

using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// 1.2-A11Y: the shortcut sheet lists every key the studio's handlers answer to, and no key a handler does not answer to.
/// </summary>
/// <remarks>
/// <para>
/// The sheet is data (<see cref="StudioShortcuts" />, <see cref="MartenStudioShortcuts" />) and the handlers are code, in five
/// components and one script, so the two drift unless something reads both. This reads each handler's source for the key names it
/// compares against - <c>case "ArrowDown":</c>, <c>"x"</c>, <c>" "</c> - maps them to the sheet's spelling, and requires the sheet's
/// group for that place to list exactly those, in both directions: a key added to a handler and not the sheet fails, and so does a
/// line on the sheet whose handler has gone. Modifier names (Ctrl, Shift) are the sheet's own words and are not compared.
/// </para>
/// <para>
/// What it cannot read is intent: the order of a group and the sentence beside a key are the sheet's and a reviewer's.
/// </para>
/// </remarks>
public class ShortcutSheetMatchesHandlersTests
{
    private static readonly string[] Modifiers = ["Ctrl", "Shift", "Alt", "Cmd"];

    /// <summary>How a handler spells a key and how the sheet does.</summary>
    private static readonly Dictionary<string, string> Spelling = new(StringComparer.Ordinal)
    {
        ["ArrowUp"] = "↑",
        ["ArrowDown"] = "↓",
        ["ArrowLeft"] = "←",
        ["ArrowRight"] = "→",
        ["Escape"] = "Esc",
        [" "] = "Space",
        ["Spacebar"] = "Space",
    };

    private static readonly IReadOnlyList<StudioShortcutGroup> All = [.. StudioShortcuts.Shared, .. MartenStudioShortcuts.Groups];

    private static string Source(string relativePath) =>
        File.ReadAllText(RepositoryRoot.Combine(relativePath.Split('/'))).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The method that starts at <paramref name="signature" />, up to the closing brace of a four-space-indented member.</summary>
    private static string Method(string relativePath, string signature)
    {
        string text = Source(relativePath);
        int start = text.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{relativePath} should still contain '{signature}'");
        int end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return text[start..end];
    }

    /// <summary>The key names a handler's body compares against, spelled as the sheet spells them.</summary>
    private static HashSet<string> HandlerKeys(string body)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (Match literal in Regex.Matches(body, "\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            string value = literal.Groups[1].Value;

            // A key name is one character, or one capitalised word; "show more" in a comment is neither.
            if (value.Length == 1 || Regex.IsMatch(value, "^[A-Z][A-Za-z]+$"))
            {
                keys.Add(Spelling.GetValueOrDefault(value, value));
            }
        }

        return keys;
    }

    private static HashSet<string> SheetKeys(string group) =>
        [.. All.Single(x => x.Title == group).Shortcuts
            .SelectMany(static x => x.Chords)
            .SelectMany(static x => x.Split('+'))
            .Where(static x => !Modifiers.Contains(x, StringComparer.Ordinal))];

    [Theory]
    [InlineData("src/PostgresStudio/Components/Json/JsonView.razor", "private async Task OnTreeKeyDownAsync", "JSON viewer (a row has focus)", "A letter")]
    [InlineData("src/PostgresStudio/Components/Json/JsonSearchBar.razor", "private async Task OnKeyDownAsync", "JSON viewer search box", null)]
    [InlineData("src/PostgresStudio/Components/Pages/Query/PlanIcicle.razor", "private async Task KeyAsync", "Plan icicle (a frame has focus)", null)]
    [InlineData("src/PostgresStudio/Components/Pages/Database/RowGrid.razor", "private async Task OnRowKeyAsync", "Table rows (a row has focus)", null)]
    [InlineData("src/PostgresStudio/Components/Shared/MenuButton.razor", "private async Task OnKeyDownAsync", "Menus", null)]
    [InlineData("src/MartenStudio/Components/Pages/Documents/DocumentTable.razor", "private async Task OnRowKeyAsync", "Document lists (a row has focus)", null)]
    public void The_sheets_group_lists_the_keys_its_handler_answers_to(string file, string signature, string group, string? typedKey)
    {
        HashSet<string> handled = HandlerKeys(Method(file, signature));
        HashSet<string> listed = SheetKeys(group);

        // A handler key may be one the group spells as a modifier chord (Shift+Enter) or as a typed key.
        listed.Remove(typedKey ?? string.Empty);

        handled.Should().BeEquivalentTo(listed, $"'{group}' on the sheet and {file}'s handler are one list");
    }

    [Fact]
    public void The_copy_menu_answers_to_a_subset_of_the_menus_keys()
    {
        HashSet<string> handled = HandlerKeys(Method("src/PostgresStudio/Components/Json/JsonCopyMenu.razor", "private async Task OnKeyDownAsync"));

        handled.Should().BeSubsetOf(SheetKeys("Menus"));
    }

    [Fact]
    public void The_scripts_chords_are_the_ones_the_sheet_lists()
    {
        string script = Source("src/PostgresStudio/wwwroot/js/studio.js");

        // The SQL editor: Ctrl/Cmd+Enter runs, Esc cancels, Ctrl/Cmd+S saves.
        script.Should().Contain("event.key === \"Enter\" && (event.ctrlKey || event.metaKey)");
        script.Should().Contain("invoke(\"CancelAsync\")");
        script.Should().Contain("(event.key === \"s\" || event.key === \"S\") && (event.ctrlKey || event.metaKey)");
        SheetKeys("SQL editor").Should().BeEquivalentTo("Enter", "Esc", "S");

        // The JSON editor: Ctrl/Cmd+Enter saves, Esc cancels, Tab inserts two spaces and Shift+Tab leaves.
        script.Should().Contain("event.key === \"Enter\" && (event.ctrlKey || event.metaKey) && !event.altKey");
        script.Should().Contain("event.key === \"Escape\" || event.key === \"Esc\"");
        script.Should().Contain("event.key !== \"Tab\"");
        SheetKeys("Document editor").Should().BeEquivalentTo("Enter", "Esc", "Tab");

        // The sheet itself, and the dialogs' own Tab loop.
        script.Should().Contain("event.key !== \"?\"");
        SheetKeys("Anywhere").Should().BeEquivalentTo("?", "Esc");
        SheetKeys("In a dialog").Should().BeEquivalentTo("Tab", "Esc");
    }
}
