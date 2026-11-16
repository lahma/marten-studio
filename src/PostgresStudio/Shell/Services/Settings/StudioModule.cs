namespace DataStudio.Services.Settings;

/// <summary>
/// A section of the shell: its pages, its rail section, its scope selector, its words and its capabilities (D34). The database - Postgres
/// Studio's pages or Marten Studio's - is one (<see cref="IsRoot" />, <c>DatabaseStudioModule</c>), and a module added beside it is another. Registered
/// by the module's own <c>Add*()</c> with <c>TryAddEnumerable</c>, read once by the composition (<see cref="StudioModuleComposition" />, a
/// singleton), so every member is pure, does no IO and reads only the module's own options.
/// </summary>
internal interface IStudioModule
{
    /// <summary>The module's key: its audit scope kind, and the composition's identity.</summary>
    string Key { get; }

    /// <summary>The module's product name in a sentence and on the rail's credit.</summary>
    string ProductName { get; }

    /// <summary>
    /// Whether the module's pages sit directly under the mount: the studio itself rather than a section added to it. Exactly one module of a
    /// composition may be the root, and the database is.
    /// </summary>
    bool IsRoot { get; }

    /// <summary>
    /// The module's page assemblies. A module that is not the root keeps its root (<see cref="StudioPageAssembly.KeepsRoot" /> must be
    /// <see langword="true" />): its pages never land over the root's. Marten Studio's root has two.
    /// </summary>
    IReadOnlyList<StudioPageAssembly> PageAssemblies { get; }

    /// <summary>The module's rail sections, in order. Titles and labels must be unique across the composed rail.</summary>
    IReadOnlyList<StudioNavSection> Navigation { get; }

    /// <summary>The header component drawn on the module's pages, a component with no parameters; <see langword="null" /> for none.</summary>
    Type? ScopeSelector { get; }

    /// <summary>The component the header draws in its centre on the module's pages, a component with no parameters; <see langword="null" /> for none.</summary>
    Type? HeaderSearch { get; }

    /// <summary>
    /// A component with no markup that the layout renders first, on every page, for the life of the circuit - one per module that has one - with
    /// one parameter, <c>bool Active</c>: the page on screen is this module's. It holds what must outlive a page of the module, and subscribes
    /// before the rail does. <see langword="null" /> for none.
    /// </summary>
    Type? CircuitHost { get; }

    /// <summary>
    /// A component the layout renders in <c>main</c> around the body of a page of this module, with one parameter, <c>ChildContent</c> (the
    /// body): the module's own frame, which may refuse the page. <see langword="null" /> renders the body as it is.
    /// </summary>
    Type? ScopeGate { get; }

    /// <summary>
    /// The module's front page below the mount: <c>""</c> for the root, the module's own segment for any other. Nothing reads it in phase A; it is on the seam
    /// now so that the package split adds no member.
    /// </summary>
    string HomeRoute { get; }

    /// <summary>
    /// One sentence the anonymous banner adds after the shell's own, or <see langword="null" /> for none: the root's is the shell's
    /// <see cref="StudioShellDescriptor.ExposedContent" />.
    /// </summary>
    string? ExposedSentence { get; }

    /// <summary>The module's capabilities as the chip lists them, one heading each, read live on every render; empty for a module with none.</summary>
    IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups { get; }

    /// <summary>The one-line opt-in that turns all of the module's capabilities on (<c>MartenStudioCapabilities.All()</c>), or <see langword="null" /> for a module with none.</summary>
    string? AllCapabilitiesOption { get; }

    /// <summary>The module's keyboard shortcuts for the sheet; empty for a module with none.</summary>
    IReadOnlyList<StudioShortcutGroup> Shortcuts { get; }

    /// <summary>
    /// The module's durable audit event ids as text (a range such as <c>1000-1009</c>), which the Activity page's footer names beside the studio's own; empty for a
    /// module with no events of its own. The text comes from the module, because the studio's source names no module's product (<c>ModuleNeutralityTests</c>).
    /// </summary>
    string AuditEventIds { get; }
}

/// <summary>One module capability as the chip lists it. Whether it is on is computed by the module: not read-only, and configured.</summary>
/// <param name="Name">The capability's name.</param>
/// <param name="Option">The option that switches it on, as a host would write it.</param>
/// <param name="Configured">Whether the host switched it on, without the master switch.</param>
internal sealed record StudioModuleCapability(string Name, string Option, bool Configured);

/// <summary>A heading and the capabilities listed under it in the chip's popover.</summary>
/// <param name="Heading">The group's heading, and its list's accessible name.</param>
/// <param name="Capabilities">The group's capabilities, in order.</param>
internal sealed record StudioModuleCapabilityGroup(string Heading, IReadOnlyList<StudioModuleCapability> Capabilities);

/// <summary>
/// Composes the registered modules into the shell's descriptor (D34): the root first, then the others in key order. A module's page
/// assemblies, rail sections and shortcuts follow the root's, and the shell's identity is the one the registering studio gives (<see cref="StudioShellIdentity" />).
/// </summary>
internal static class StudioModuleComposition
{
    /// <summary>The compile-time roots of the host studios' own pages, which no module that is not the root may claim.</summary>
    private static readonly string[] HostRoots = ["/postgres", "/marten"];

    /// <summary>Composes the shell's descriptor from <paramref name="identity" />'s words and <paramref name="modules" />.</summary>
    /// <param name="identity">The studio's identity: the shell's name, its anonymous banner's clause and its startup guard's words.</param>
    /// <param name="modules">The registered modules, in any order.</param>
    /// <exception cref="InvalidOperationException">
    /// Two modules share a key, two are the root, a module that is not the root has pages that do not keep their root, a root is another
    /// assembly's or a host studio's, or a section title or item label of a module collides with another one on the composed rail.
    /// </exception>
    public static StudioShellDescriptor Compose(StudioShellIdentity identity, IEnumerable<IStudioModule> modules)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(modules);

        IStudioModule[] ordered =
        [
            .. modules.Where(static x => x.IsRoot).OrderBy(static x => x.Key, StringComparer.Ordinal),
            .. modules.Where(static x => !x.IsRoot).OrderBy(static x => x.Key, StringComparer.Ordinal),
        ];

        Validate(ordered);

        return new StudioShellDescriptor
        {
            ProductName = identity.ProductName,
            ExposedContent = identity.ExposedContent,
            Surface = identity.Surface,
            Exposure = identity.Exposure,
            Remedies = identity.Remedies,
            Modules = ordered,
        };
    }

    private static void Validate(IStudioModule[] modules)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        List<string> roots = [];
        IStudioModule? root = null;

        foreach (IStudioModule module in modules)
        {
            if (!keys.Add(module.Key))
            {
                throw new InvalidOperationException($"Two modules are registered under the key '{module.Key}'. Each module has a key of its own.");
            }

            if (module.IsRoot)
            {
                if (root is not null)
                {
                    throw new InvalidOperationException(
                        $"Modules '{root.Key}' and '{module.Key}' are both the root (IStudioModule.IsRoot). Exactly one module's pages sit directly under the mount.");
                }

                root = module;
                roots.AddRange(module.PageAssemblies.Select(static x => Normalized(x.CompileTimeRoot)));
            }
        }

        foreach (IStudioModule module in modules.Where(static x => !x.IsRoot))
        {
            foreach (StudioPageAssembly pages in module.PageAssemblies)
            {
                if (!pages.KeepsRoot)
                {
                    throw new InvalidOperationException(
                        $"The pages of module '{module.Key}' do not keep their root (StudioPageAssembly.KeepsRoot). A module's pages are served below "
                        + "its own segment under the mount, so that they never land over the host studio's.");
                }

                string pageRoot = Normalized(pages.CompileTimeRoot);
                if (HostRoots.Contains(pageRoot, StringComparer.OrdinalIgnoreCase) || roots.Contains(pageRoot, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The pages of module '{module.Key}' are rooted at '{pages.CompileTimeRoot}', which another page assembly of the studio already uses.");
                }

                roots.Add(pageRoot);
            }
        }

        // Every title and label on the composed rail is a sibling @key in one list (NavMenu), so a module's may collide with
        // nothing: not the root's, not another module's, and not another of its own. The root's own are not judged here.
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (StudioNavSection section in root?.Navigation ?? [])
        {
            names.Add(section.Title);
            foreach (StudioNavItem item in section.Items)
            {
                names.Add(item.Label);
            }
        }

        foreach (IStudioModule module in modules.Where(static x => !x.IsRoot))
        {
            foreach (StudioNavSection section in module.Navigation)
            {
                Claim(names, section.Title, module);

                foreach (StudioNavItem item in section.Items)
                {
                    Claim(names, item.Label, module);
                }
            }
        }
    }

    private static void Claim(HashSet<string> names, string name, IStudioModule module)
    {
        if (!names.Add(name))
        {
            throw new InvalidOperationException(
                $"The rail entry '{name}' of module '{module.Key}' is already a section title or a label on the rail. "
                + "Every title and label is unique, ignoring case, because the rail keys its entries on them.");
        }
    }

    private static string Normalized(string root) => root.TrimEnd('/');
}
