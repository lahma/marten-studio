using DataStudio.Services;

namespace DataStudio.Postgres.Services.Postgres;

/// <summary>
/// The keyboard shortcuts of the components that live in the Postgres module: the SQL editor, the row grids and the plan view. Written from the
/// handlers - the SQL editor's chords in studio.js (<c>enhanceEditor</c>), <c>RowGrid.OnRowKeyAsync</c> and <c>PlanIcicle.KeyAsync</c> - and listed
/// by the database module (<see cref="Settings.DatabaseStudioModule" />), so that a host with no database module, a Kafka-only one, reads no shortcut
/// it does not have.
/// </summary>
internal static class PostgresStudioShortcuts
{
    /// <summary>The groups, in the order the sheet lists them after the shell's own.</summary>
    public static IReadOnlyList<StudioShortcutGroup> Groups { get; } =
    [
        new(
            "SQL editor",
            [
                new StudioShortcut("Ctrl+Enter", "Run the statement (Cmd+Enter on a Mac)"),
                new StudioShortcut("Esc", "Cancel the statement that is running"),
                new StudioShortcut("Ctrl+S", "Save the statement as a saved query (Cmd+S on a Mac)"),
            ]),
        new(
            "Table rows (a row has focus)",
            [
                new StudioShortcut("Space", "Open or close the row in place"),
                new StudioShortcut("Enter", "Open the row's detail page, for a row that has one"),
            ]),
        new(
            "Plan icicle (a frame has focus)",
            [
                new StudioShortcut("↑", "The frame's parent"),
                new StudioShortcut("↓", "Its first child"),
                new StudioShortcut(["←", "→"], "Previous or next sibling"),
                new StudioShortcut(["Enter", "Space"], "Select the frame"),
            ]),
    ];
}
