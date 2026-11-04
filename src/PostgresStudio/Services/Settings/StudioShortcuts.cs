namespace PostgresStudio.Services.Settings;

/// <summary>One keyboard shortcut the studio answers to, as the shortcut sheet (<c>?</c>) lists it.</summary>
/// <param name="Chords">
/// The ways to press it, each a <c>+</c>-joined chord (<c>Ctrl+Enter</c>, <c>Shift+Tab</c>) or a single key (<c>Esc</c>, <c>↑</c>);
/// the sheet draws each key as a keycap and separates the alternatives with "/". A key that is itself <c>+</c> is not a chord
/// the studio has.
/// </param>
/// <param name="Does">What it does, in a sentence fragment without a full stop.</param>
internal sealed record StudioShortcut(IReadOnlyList<string> Chords, string Does)
{
    /// <summary>A shortcut with one way to press it.</summary>
    public StudioShortcut(string chord, string does)
        : this([chord], does)
    {
    }
}

/// <summary>A titled run of shortcuts that share a place in the studio.</summary>
/// <param name="Title">The group's heading: where the keys work.</param>
/// <param name="Shortcuts">Its shortcuts, in the order they are listed.</param>
internal sealed record StudioShortcutGroup(string Title, IReadOnlyList<StudioShortcut> Shortcuts);

/// <summary>
/// Every keyboard shortcut the shared studio has (1.2-A11Y), written from the handlers, not from memory: the JSON viewer's
/// <c>OnTreeKeyDownAsync</c>, the search bar's, the menu pattern of <c>MenuButton</c> and <c>JsonCopyMenu</c>, <c>RowGrid</c>'s
/// <c>OnRowKeyAsync</c>, <c>PlanIcicle</c>'s <c>KeyAsync</c>, the SQL editor's chords in studio.js and the dialogs' (ModalDialog).
/// A studio adds its own through <see cref="StudioDescriptor.ExtraShortcuts" />, so a Postgres Studio host is never told about a
/// key only Marten Studio's pages answer to.
/// </summary>
/// <remarks>
/// A shortcut handler and its line here change together: <c>ShortcutSheetMatchesHandlersTests</c> reads the handlers' source for the keys they
/// name, so a key added to one and not the other fails there. The only <em>global</em> keys are <c>?</c> and <c>Esc</c>; every other
/// key on this list works only where focus is on the control that handles it.
/// </remarks>
internal static class StudioShortcuts
{
    /// <summary>The group of keys that work wherever focus is.</summary>
    /// <remarks>
    /// Find by id (<c>/</c> and <c>Ctrl+K</c>, 1.2-SCOPE-3) is a document-level handler in studio.js, like <c>?</c>: it is ignored
    /// while typing in a field and while a dialog is open. A JSON viewer row that has focus answers <c>/</c> itself.
    /// </remarks>
    public static StudioShortcutGroup Anywhere { get; } = new(
        "Anywhere",
        [
            new StudioShortcut("?", "Show this list (not while typing in a field)"),
            new StudioShortcut(["/", "Ctrl+K"], "Find by id: go to the header's box (Cmd+K on a Mac; not while typing in a field)"),
            new StudioShortcut("Esc", "Close this list, or cancel the open dialog"),
        ]);

    /// <summary>The shortcuts every studio has, in the order the sheet lists them after <see cref="Anywhere" />.</summary>
    public static IReadOnlyList<StudioShortcutGroup> Shared { get; } =
    [
        Anywhere,
        new(
            "In a dialog",
            [
                new StudioShortcut(["Tab", "Shift+Tab"], "Move between the dialog's controls; focus stays inside until it closes"),
                new StudioShortcut("Esc", "Cancel, the same as the Cancel button"),
            ]),
        new(
            "Menus",
            [
                new StudioShortcut(["↑", "↓"], "Previous or next item"),
                new StudioShortcut(["Home", "End"], "First or last item"),
                new StudioShortcut("Esc", "Close the menu"),
            ]),
        new(
            "SQL editor",
            [
                new StudioShortcut("Ctrl+Enter", "Run the statement (Cmd+Enter on a Mac)"),
                new StudioShortcut("Esc", "Cancel the statement that is running"),
                new StudioShortcut("Ctrl+S", "Save the statement as a saved query (Cmd+S on a Mac)"),
            ]),
        new(
            "JSON viewer (a row has focus)",
            [
                new StudioShortcut(["↑", "↓"], "Previous or next row"),
                new StudioShortcut(["Home", "End"], "First or last row"),
                new StudioShortcut("→", "Expand the row, or step into its first child"),
                new StudioShortcut("←", "Collapse the row, or move to its parent"),
                new StudioShortcut("*", "Expand everything under the row"),
                new StudioShortcut(["Enter", "Space"], "Open the row's copy menu"),
                new StudioShortcut("/", "Go to the viewer's search box"),
                new StudioShortcut("A letter", "Jump to the next row whose key starts with the letters you type"),
                new StudioShortcut("Esc", "Forget the letters typed so far"),
            ]),
        new(
            "JSON viewer search box",
            [
                new StudioShortcut(["Enter", "Shift+Enter"], "Next or previous match"),
                new StudioShortcut("Esc", "Clear the search"),
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
