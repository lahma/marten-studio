using System.Reflection;

namespace PostgresStudio.Services.Settings;

/// <summary>One assembly whose routable components are studio pages, and the compile-time root its <c>@page</c> directives use.</summary>
/// <param name="Assembly">The assembly.</param>
/// <param name="CompileTimeRoot">
/// The rooted prefix every page template in <paramref name="Assembly" /> starts with - <c>/marten</c> - and which the
/// endpoint mapping re-roots under the configured mount path.
/// </param>
internal sealed record StudioPageAssembly(Assembly Assembly, string CompileTimeRoot)
{
    /// <summary>
    /// Whether the assembly's pages keep their compile-time root as their first relative segment (D34): a module's
    /// <c>@page "/orders/list"</c> is served at <c>{mount}/orders/list</c>, where a host studio's <c>/marten/documents</c> is served
    /// at <c>{mount}/documents</c>. <see langword="false" /> for every host studio's own assemblies.
    /// </summary>
    public bool KeepsRoot { get; init; }
}

/// <summary>
/// What makes one studio this studio: its name, its pages, its navigation, how it names a source, and how it builds the
/// resource an authorization handler is asked about.
/// </summary>
/// <remarks>
/// <para>
/// A singleton, registered by a studio's <c>Add*Studio()</c> and read by every shared service and component in place of
/// that studio's own types. Nothing on it is about a visitor, and nothing on it does IO: it is data plus two pure
/// functions (<see cref="CreateResource" />, and each navigation entry's optional <see cref="StudioNavItem.IsShown" />).
/// </para>
/// <para>
/// <b>Text is here so that shared markup says the right noun.</b> A page refusal says "this Marten store" in MartenStudio;
/// the same shared component says what <c>SourceNoun</c> says in any other studio. Every sentence it feeds keeps
/// MartenStudio's wording exactly as it was, so registering the descriptor changed nothing a host or a visitor reads.
/// </para>
/// </remarks>
internal sealed class StudioDescriptor : DiagnosticsDescriptor
{
    /// <summary>
    /// Every assembly whose pages this studio serves, each with its compile-time root: the database module's own
    /// (<see cref="DatabaseStudioModule" />). The shell's route table and the endpoint re-rooting are built from every module's
    /// list (<see cref="StudioShellDescriptor.PageAssemblies" />); a relative route two of them claim is an error.
    /// </summary>
    public required IReadOnlyList<StudioPageAssembly> PageAssemblies { get; init; }

    /// <summary>
    /// The sidebar, section by section, built for the settings the studio is registered with: an entry whose condition needs more than the
    /// master switch (<see cref="StudioNavConditions.RunSqlEnabled" />) closes over them, so the navigation is built once per registration
    /// (by <see cref="DatabaseStudioModule" />) and never held in a static.
    /// </summary>
    public required Func<IStudioSettings, IReadOnlyList<StudioNavSection>> Navigation { get; init; }

    /// <summary>
    /// The body the studio's shared root page (<c>StudioHome</c>) renders - MartenStudio's store overview. A component
    /// with no <c>@page</c> of its own: the root route is the shared page's, so two studios' bodies never claim it twice.
    /// </summary>
    public required Type OverviewComponent { get; init; }

    /// <summary>
    /// The body the studio's shared <c>query</c> page (<c>QueryPage</c>) renders - MartenStudio's three-mode page, or
    /// the shared SQL console panel alone - as <see cref="OverviewComponent" />.
    /// </summary>
    public required Type QueryComponent { get; init; }

    /// <summary>
    /// What a page served to anyone exposes, in the banner that says so: "every document, event stream and schema in
    /// this process".
    /// </summary>
    public required string ExposedContent { get; init; }

    /// <summary>
    /// The resource an authorization handler is shown for a scope and a capability (or
    /// <see langword="null" /> for a read): MartenStudio's public <c>MartenStoreResource</c>. Pure, and called for every
    /// policy question, so it must not allocate more than the resource itself or do any IO.
    /// </summary>
    public required Func<StudioScope, string?, object> CreateResource { get; init; }

    /// <summary>The startup guard's name for the endpoints this studio maps: <c>Marten Studio</c>.</summary>
    public required string Surface { get; init; }

    /// <summary>
    /// The startup guard's lead sentence: what an unauthorized mapping would serve, and that the studio refuses to start.
    /// </summary>
    public required string Exposure { get; init; }

    /// <summary>The startup guard's list of the three ways to say which authorization was meant, one per line.</summary>
    public required string Remedies { get; init; }

    /// <summary>
    /// The keyboard shortcuts only this studio's pages answer to (1.2), listed on the shortcut sheet after
    /// <see cref="StudioShortcuts.Shared" />: MartenStudio's document list and document editor. Empty for a studio with none, so a
    /// Postgres Studio host is never told about a key that does nothing on its pages.
    /// </summary>
    public IReadOnlyList<StudioShortcutGroup> ExtraShortcuts { get; init; } = [];
}

/// <summary>One section of the sidebar.</summary>
/// <param name="Title">The section's heading.</param>
/// <param name="Items">Its entries, in order.</param>
internal sealed record StudioNavSection(string Title, IReadOnlyList<StudioNavItem> Items)
{
    /// <summary>
    /// The key of the module that contributed the section, set by the composition and never by a module (D34): the root's sections carry the
    /// root's key, so "a host section" is "a section of the root module". The rail asks the module's <see cref="IStudioModuleRail" />, if it
    /// has one, for the section's links and entries. <see langword="null" /> only on a section no composition has stamped.
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
    /// The key of the condition the module's rail answers for this entry, or <see langword="null" /> for none (1.2): MartenStudio's Stores
    /// entry is drawn while the visitor may see two or more stores, the same test the header's store picker makes. The module's
    /// <see cref="IStudioModuleRail.IsShown" /> answers it, in memory, on every render, and a key it does not know is not shown (fail
    /// closed). The database's keys are <see cref="Live.DatabaseRailConditions" />.
    /// </summary>
    public string? ShownWhen { get; init; }
}

/// <summary>What one sidebar badge renders as.</summary>
/// <param name="Text">The count, or empty for a dot.</param>
/// <param name="CssClass">The badge's <c>ms-nav-badge-*</c> classes.</param>
/// <param name="Title">Its tooltip and accessible name.</param>
internal sealed record StudioNavBadge(string Text, string CssClass, string Title);

/// <summary>The conditions a sidebar entry may carry (<see cref="StudioNavItem.IsShown" />), shared by both studios' descriptors.</summary>
internal static class StudioNavConditions
{
    /// <summary>
    /// Whether the SQL console's capability is effectively on: <c>RunSql</c> set and the master switch off. The entries that
    /// are only a door to a page that runs SQL (Postgres Studio's Query) are absent otherwise, so an
    /// administrator is not offered a page whose one sentence is addressed to the developer who configures the host
    /// (issue #37). The page keeps its notice for anyone who reaches it by address.
    /// </summary>
    public static bool RunSqlEnabled(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !settings.ReadOnly && settings.IsConfigured(StudioCapability.RunSql);
    }
}

/// <summary>The badge keys the shared sidebar draws itself.</summary>
internal static class StudioNavBadgeKeys
{
    /// <summary>The health glance: the last sweep's unmuted Critical and Warning findings this visitor may see.</summary>
    public const string Health = "health";
}
