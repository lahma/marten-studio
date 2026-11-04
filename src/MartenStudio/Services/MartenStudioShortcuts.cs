using PostgresStudio.Services.Settings;

namespace MartenStudio.Services;

/// <summary>
/// The keyboard shortcuts only Marten Studio's pages answer to, for the shortcut sheet (<c>?</c>, 1.2-A11Y). Written from the
/// handlers: <c>DocumentTable.OnRowKeyAsync</c>, the JSON editor's chords in studio.js (<c>martenStudio.json.enhanceTextarea</c>) and the LINQ
/// panel's (its predicate box is the SQL editor's <c>enhanceEditor</c>; its Order by box is <c>enhanceRunField</c>).
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
            "LINQ query (a box has focus)",
            [
                new StudioShortcut("Ctrl+Enter", "Run the query, from the predicate or the Order by box (Cmd+Enter on a Mac)"),
                new StudioShortcut("Esc", "Cancel the query that is running (predicate box)"),
                new StudioShortcut("Ctrl+S", "Save the query (predicate box; Cmd+S on a Mac)"),
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
