using DataStudio.Components;

namespace DataStudio.Services.Settings;

/// <summary>
/// What the shell reads of itself: its name, the words of its startup guard, and the modules composed into it (D34) - the database, which is
/// the root while one is registered, and any module added beside it. A singleton, composed once from the registered modules
/// (<see cref="StudioModuleComposition" />) when first resolved, after every <c>Add*()</c>, so registration is order-independent.
/// </summary>
/// <remarks>
/// Nothing on it is about a visitor, and nothing on it does IO. A shell file reads this and never the database's own
/// <c>StudioDescriptor</c>, which keeps what is about a source.
/// </remarks>
internal sealed class StudioShellDescriptor
{
    private readonly Lazy<IStudioModule?> root;
    private readonly Lazy<IReadOnlyList<StudioPageAssembly>> pageAssemblies;
    private readonly Lazy<IReadOnlyList<StudioNavSection>> navigation;
    private readonly Lazy<IReadOnlyList<StudioShortcutGroup>> extraShortcuts;
    private readonly Lazy<StudioRouteTable> routes;
    private readonly Lazy<string> exposure;

    /// <summary>Creates a descriptor; every derived list is built on first use, and the route table is validated then.</summary>
    public StudioShellDescriptor()
    {
        root = new Lazy<IStudioModule?>(FindRoot);
        pageAssemblies = new Lazy<IReadOnlyList<StudioPageAssembly>>(BuildPageAssemblies);
        extraShortcuts = new Lazy<IReadOnlyList<StudioShortcutGroup>>(BuildExtraShortcuts);
        navigation = new Lazy<IReadOnlyList<StudioNavSection>>(BuildNavigation);
        routes = new Lazy<StudioRouteTable>(BuildRoutes);
        exposure = new Lazy<string>(BuildExposure);
    }

    /// <summary>The startup guard's lead sentence: what an unauthorized mapping would serve (every module's own sentence), and that the shell refuses to start.</summary>
    public string Exposure => exposure.Value;

    /// <summary>The composed modules: the root first, then the others by key (ordinal).</summary>
    public required IReadOnlyList<IStudioModule> Modules { get; init; }

    /// <summary>
    /// The module whose pages sit directly under the mount, or <see langword="null" /> when none is the root: a shell with only a module that
    /// keeps its own segment has none, and its front address leads to <see cref="First" />'s home.
    /// </summary>
    public IStudioModule? Root => root.Value;

    /// <summary>
    /// The first composed module (the root, when there is one): the one a shell with no root module sends its front address to
    /// (<see cref="IStudioModule.HomeRoute" />). <see langword="null" /> only for a shell with no module, which refuses to map.
    /// </summary>
    public IStudioModule? First => Modules.Count > 0 ? Modules[0] : null;

    /// <summary>
    /// Every module's page assemblies, in module order, then the shell's own (<see cref="ShellPages" />: the Activity page) when no module lists it.
    /// The route table and the endpoint re-rooting are built from this list and nothing else.
    /// </summary>
    public IReadOnlyList<StudioPageAssembly> PageAssemblies => pageAssemblies.Value;

    /// <summary>
    /// The sidebar, section by section: the root's, with every other module's inserted immediately before the root's last section (System, which
    /// holds Activity), in key order, each stamped with its module's <see cref="StudioNavSection.ModuleKey" /> - the root's too. When no module
    /// contributes the Activity entry (a shell with no database module), the shell closes the rail with a System section of its own that holds it.
    /// </summary>
    public IReadOnlyList<StudioNavSection> Navigation => navigation.Value;

    /// <summary>Every module's keyboard shortcuts, in module order, listed on the sheet after <see cref="StudioShortcuts.Shared" />.</summary>
    public IReadOnlyList<StudioShortcutGroup> ExtraShortcuts => extraShortcuts.Value;

    /// <summary>
    /// The route table built from <see cref="PageAssemblies" />, once. Building it validates every page: a template outside its assembly's
    /// root, or a relative route two pages claim, throws on first use.
    /// </summary>
    public StudioRouteTable Routes => routes.Value;

    /// <summary>
    /// The module whose pages are <paramref name="pageType" />'s assembly, or the root for a page of no module's assembly.
    /// </summary>
    /// <param name="pageType">A routed page component.</param>
    public IStudioModule? ModuleOf(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);

        foreach (IStudioModule module in Modules)
        {
            foreach (StudioPageAssembly pages in module.PageAssemblies)
            {
                if (pages.Assembly == pageType.Assembly)
                {
                    return module;
                }
            }
        }

        return Root;
    }

    private string BuildExposure() => StudioShellIdentity.Exposure(Modules.Select(static x => x.ExposedSentence));

    private IStudioModule? FindRoot() => Modules.FirstOrDefault(static x => x.IsRoot);

    /// <summary>
    /// The shell's own pages (the Activity page) as a page assembly: <c>/postgres/activity</c> is served at <c>{mount}/activity</c>, the address the
    /// database module has always given it, whether or not there is a database module - the compile-time root only says what the
    /// <c>@page</c> directive starts with, and the mapping strips it (<see cref="StudioPageAssembly.KeepsRoot" /> is <see langword="false" />).
    /// </summary>
    public static StudioPageAssembly ShellPages { get; } = new(typeof(StudioShellDescriptor).Assembly, "/postgres");

    /// <summary>The route of the Activity page below the mount, and of the rail entry that leads to it.</summary>
    public const string ActivityRoute = "activity";

    /// <summary>Whether some module's rail already holds the Activity entry (a database module's System section does).</summary>
    internal static bool ModulesHoldActivity(IEnumerable<IStudioModule> modules) =>
        modules.Any(static module => module.Navigation.Any(static section => section.Items.Any(static item => item.Route == ActivityRoute)));

    /// <summary>The section the shell adds to the rail when no module holds the Activity entry.</summary>
    internal static StudioNavSection ShellRail { get; } = new("System", [new StudioNavItem("Activity", ActivityRoute, "list")]);

    private List<StudioPageAssembly> BuildPageAssemblies() =>
        [.. Modules.SelectMany(static x => x.PageAssemblies).Append(ShellPages).Distinct()];

    private List<StudioShortcutGroup> BuildExtraShortcuts() => [.. Modules.SelectMany(static x => x.Shortcuts)];

    private StudioRouteTable BuildRoutes() => new(PageAssemblies);

    private List<StudioNavSection> BuildNavigation()
    {
        IStudioModule? first = Root;

        // The root's sections are stamped with the root's key too: a section's rail is the rail of its module, whichever module it is.
        List<StudioNavSection> sections = [.. (first?.Navigation ?? []).Select(section => section with { ModuleKey = first!.Key })];

        int at = Math.Max(sections.Count - 1, 0);
        sections.InsertRange(
            at,
            Modules
                .Where(x => !ReferenceEquals(x, first))
                .SelectMany(static module => module.Navigation.Select(section => section with { ModuleKey = module.Key })));

        // A shell with no database module has no Activity entry on any rail: the shell's own section holds it, last, as the database's does.
        if (!ModulesHoldActivity(Modules))
        {
            sections.Add(ShellRail);
        }

        return sections;
    }
}
