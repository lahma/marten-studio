namespace DataStudio.Services.Settings;

/// <summary>One section of the sidebar.</summary>
/// <param name="Title">The section's heading.</param>
/// <param name="Items">Its entries, in order.</param>
internal sealed record StudioNavSection(string Title, IReadOnlyList<StudioNavItem> Items)
{
    /// <summary>
    /// The key of the module that contributed the section, set by the composition and never by a module (D34). The rail asks the module's
    /// <see cref="IStudioModuleRail" />, if it has one, for the section's links and entries. <see langword="null" /> on the shell's own section
    /// and on a section no composition has stamped.
    /// </summary>
    public string? ModuleKey { get; init; }
}

/// <summary>One sidebar entry.</summary>
/// <param name="Label">What the link says, and its accessible name.</param>
/// <param name="Route">The page below the studio root, or <see langword="null" /> for one not built yet (drawn disabled).</param>
/// <param name="Icon">The hand-drawn icon's name (<c>NavMenu.Icon</c>).</param>
/// <param name="BadgeKey">
/// Which badge the entry wears, or <see langword="null" />. <see cref="StudioNavBadgeKeys.Health" /> is drawn by the
/// shared sidebar from the health glance; any other key is looked up in what the indicator service read for it.
/// </param>
/// <param name="Title">The tooltip, when the label alone undersells the page; the label otherwise.</param>
internal sealed record StudioNavItem(
    string Label,
    string? Route,
    string Icon,
    string? BadgeKey = null,
    string? Title = null)
{
    /// <summary>
    /// Whether the entry is drawn, or <see langword="null" /> for always. Pure and cheap: it is asked on every render of
    /// the sidebar, and the answer must fail closed - an entry for something the host hides is absent (D27). It sees the shell's
    /// settings; a condition that needs more closes over the studio's own settings where the entry is built.
    /// </summary>
    public Func<IShellSettings, bool>? IsShown { get; init; }

    /// <summary>
    /// The key of the condition the module's rail answers for this entry, or <see langword="null" /> for none (1.2): the Marten module's Stores
    /// entry is drawn while the visitor may see two or more stores, the same test the header's store picker makes. The module's
    /// <see cref="IStudioModuleRail.IsShown" /> answers it, in memory, on every render, and a key it does not know is not shown (fail
    /// closed). The database's keys are <c>DatabaseRailConditions</c>.
    /// </summary>
    public string? ShownWhen { get; init; }

    /// <summary>
    /// Whether the entry is lit by its own address alone, and not by every page below it: a module's Overview entry sits at the module's home
    /// (<c>marten</c>), which is a prefix of every other page of the module. The other entries are lit by their page and its sub-pages.
    /// </summary>
    public bool Exact { get; init; }
}

/// <summary>What one sidebar badge renders as.</summary>
/// <param name="Text">The count, or empty for a dot.</param>
/// <param name="CssClass">The badge's <c>ms-nav-badge-*</c> classes.</param>
/// <param name="Title">Its tooltip and accessible name.</param>
internal sealed record StudioNavBadge(string Text, string CssClass, string Title);

/// <summary>The badge keys the shared sidebar draws itself.</summary>
internal static class StudioNavBadgeKeys
{
    /// <summary>The health glance: the last sweep's unmuted Critical and Warning findings this visitor may see.</summary>
    public const string Health = "health";
}
