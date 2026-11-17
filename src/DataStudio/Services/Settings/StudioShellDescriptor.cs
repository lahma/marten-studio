using DataStudio.Components;

namespace DataStudio.Services.Settings;

/// <summary>
/// What the shell reads of itself: its name, the words of its startup guard, and the modules composed into it (D34), every one under a segment of
/// its own below the mount. A singleton, composed once from the registered modules (<see cref="StudioModuleComposition" />) when first resolved,
/// after every <c>Add*()</c>, so registration is order-independent.
/// </summary>
/// <remarks>
/// Nothing on it is about a visitor, and nothing on it does IO. A shell file reads this and never the database's own
/// <c>StudioDescriptor</c>, which keeps what is about a source.
/// </remarks>
internal sealed class StudioShellDescriptor
{
    private readonly Lazy<IReadOnlyList<StudioPageAssembly>> pageAssemblies;
    private readonly Lazy<IReadOnlyList<StudioNavSection>> navigation;
    private readonly Lazy<IReadOnlyList<StudioShortcutGroup>> extraShortcuts;
    private readonly Lazy<StudioRouteTable> routes;
    private readonly Lazy<string> exposure;

    /// <summary>Creates a descriptor; every derived list is built on first use, and the route table is validated then.</summary>
    public StudioShellDescriptor()
    {
        pageAssemblies = new Lazy<IReadOnlyList<StudioPageAssembly>>(BuildPageAssemblies);
        extraShortcuts = new Lazy<IReadOnlyList<StudioShortcutGroup>>(BuildExtraShortcuts);
        navigation = new Lazy<IReadOnlyList<StudioNavSection>>(BuildNavigation);
        routes = new Lazy<StudioRouteTable>(BuildRoutes);
        exposure = new Lazy<string>(BuildExposure);
    }

    /// <summary>The startup guard's lead sentence: what an unauthorized mapping would serve (every module's own sentence), and that the shell refuses to start.</summary>
    public string Exposure => exposure.Value;

    /// <summary>The composed modules, by key (ordinal).</summary>
    public required IReadOnlyList<IStudioModule> Modules { get; init; }

    /// <summary>
    /// Every module's page assemblies, in module order, then the shell's own (<see cref="ShellPages" />: the front page and Activity).
    /// The route table and the endpoint mapping are built from this list and nothing else.
    /// </summary>
    public IReadOnlyList<StudioPageAssembly> PageAssemblies => pageAssemblies.Value;

    /// <summary>
    /// The sidebar, section by section: every module's sections in module order, each stamped with its module's
    /// <see cref="StudioNavSection.ModuleKey" />, then the shell's own section (<see cref="ShellRail" />), which holds Activity and has no module.
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
    /// The module whose pages are <paramref name="pageType" />'s assembly, or <see langword="null" /> for a page of no module: the shell's own
    /// (the front page, Activity) and the one the router answers for an address no page matches (Not found). Such a page has no scope.
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

        return null;
    }

    private string BuildExposure() => StudioShellIdentity.Exposure(Modules.Select(static x => x.ExposedSentence));

    /// <summary>
    /// The shell's own pages (the front page and Activity) as a page assembly rooted at <c>/</c>: <c>@page "/activity"</c> is served at
    /// <c>{mount}/activity</c>, and <c>@page "/"</c> at the mount itself. Like every page its address is its template below the mount.
    /// </summary>
    public static StudioPageAssembly ShellPages { get; } = new(typeof(StudioShellDescriptor).Assembly, "/");

    /// <summary>The route of the Activity page below the mount, and of the rail entry that leads to it.</summary>
    public const string ActivityRoute = "activity";

    /// <summary>
    /// The shell's own rail section, last on the rail: Activity, which is the shell's and no module's. It is titled <c>System</c>, unless a module's
    /// own section already is (both database modules close theirs with one), and then <c>Studio</c>.
    /// </summary>
    /// <param name="modules">The composed modules.</param>
    internal static StudioNavSection ShellRail(IEnumerable<IStudioModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        const string Preferred = "System";
        bool taken = modules.Any(static module => module.Navigation.Any(static section => string.Equals(section.Title, Preferred, StringComparison.OrdinalIgnoreCase)));

        return new(taken ? "Studio" : Preferred, [new StudioNavItem("Activity", ActivityRoute, "list")]);
    }

    private List<StudioPageAssembly> BuildPageAssemblies() =>
        [.. Modules.SelectMany(static x => x.PageAssemblies).Append(ShellPages).Distinct()];

    private List<StudioShortcutGroup> BuildExtraShortcuts() => [.. Modules.SelectMany(static x => x.Shortcuts)];

    private StudioRouteTable BuildRoutes() => new(PageAssemblies);

    private List<StudioNavSection> BuildNavigation() =>
    [
        .. Modules.SelectMany(static module => module.Navigation.Select(section => section with { ModuleKey = module.Key })),
        ShellRail(Modules),
    ];
}
