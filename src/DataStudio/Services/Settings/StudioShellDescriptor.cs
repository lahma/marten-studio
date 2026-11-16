using DataStudio.Components;

namespace DataStudio.Services.Settings;

/// <summary>
/// What the shell reads of the studio it is: its name, the words of its anonymous banner and its startup guard, and the modules composed into
/// it (D34) - the database, which is the root, and any module added beside it. A singleton, composed once from the registered modules
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

    /// <summary>Creates a descriptor; every derived list is built on first use, and the route table is validated then.</summary>
    public StudioShellDescriptor()
    {
        root = new Lazy<IStudioModule?>(FindRoot);
        pageAssemblies = new Lazy<IReadOnlyList<StudioPageAssembly>>(BuildPageAssemblies);
        extraShortcuts = new Lazy<IReadOnlyList<StudioShortcutGroup>>(BuildExtraShortcuts);
        navigation = new Lazy<IReadOnlyList<StudioNavSection>>(BuildNavigation);
        routes = new Lazy<StudioRouteTable>(BuildRoutes);
    }

    /// <summary>The product's name in a sentence: the sidebar's accessible name and the rail's credit.</summary>
    public required string ProductName { get; init; }

    /// <summary>What a page served to anyone exposes, in the anonymous banner's clause: "every document, event stream and schema in this process"; <see langword="null" /> for none.</summary>
    public required string? ExposedContent { get; init; }

    /// <summary>The startup guard's name for the endpoints this studio maps: <c>Marten Studio</c>.</summary>
    public required string Surface { get; init; }

    /// <summary>The startup guard's lead sentence: what an unauthorized mapping would serve, and that the studio refuses to start.</summary>
    public required string Exposure { get; init; }

    /// <summary>The startup guard's list of the three ways to say which authorization was meant, one per line.</summary>
    public required string Remedies { get; init; }

    /// <summary>The composed modules: the root first, then the others by key (ordinal).</summary>
    public required IReadOnlyList<IStudioModule> Modules { get; init; }

    /// <summary>The module whose pages sit directly under the mount, or <see langword="null" /> when none is the root.</summary>
    public IStudioModule? Root => root.Value;

    /// <summary>Every module's page assemblies, in module order. The route table and the endpoint re-rooting are built from this list and nothing else.</summary>
    public IReadOnlyList<StudioPageAssembly> PageAssemblies => pageAssemblies.Value;

    /// <summary>
    /// The sidebar, section by section: the root's, with every other module's inserted immediately before the root's last section (System, which
    /// holds Activity), in key order, each stamped with its module's <see cref="StudioNavSection.ModuleKey" /> - the root's too.
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

    private IStudioModule? FindRoot() => Modules.FirstOrDefault(static x => x.IsRoot);

    private List<StudioPageAssembly> BuildPageAssemblies() => [.. Modules.SelectMany(static x => x.PageAssemblies)];

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

        return sections;
    }
}
