using PostgresStudio.Services.Settings;

namespace MartenStudio.Services;

/// <summary>
/// The keyboard shortcuts only Marten Studio's pages answer to, for the shortcut sheet (<c>?</c>, 1.2-A11Y). Written from the
/// handlers: <c>DocumentTable.OnRowKeyAsync</c> and the JSON editor's chords in studio.js (<c>martenStudio.json.enhanceTextarea</c>).
/// </summary>
internal static class MartenStudioShortcuts
{
    /// <summary>The groups, in the order the sheet lists them after the shared ones.</summary>
    public static IReadOnlyList<StudioShortcutGroup> Groups { get; } =
    [
        new(
            "Document lists (a row has focus)",
            [
                new StudioShortcut("Space", "Open or close the row's preview"),
                new StudioShortcut("x", "Select the row, where the list can select (for a bulk delete)"),
            ]),
        new(
            "Document editor",
            [
                new StudioShortcut("Ctrl+Enter", "Save, as the Save button does (Cmd+Enter on a Mac)"),
                new StudioShortcut("Esc", "Cancel the edit"),
                new StudioShortcut("Tab", "Insert two spaces"),
                new StudioShortcut("Shift+Tab", "Leave the editor"),
            ]),
    ];
}
