namespace PostgresStudio.Services.Settings;

/// <summary>
/// A section of the shell that is not about the host studio's source: its pages, its rail section, its scope selector, its
/// words and its capabilities (D34). Registered by the module's own <c>Add*()</c> with <c>TryAddEnumerable</c>, read once by
/// the descriptor composition (<see cref="StudioModuleComposition" />, a singleton), so every member is pure, does no IO and
/// reads only the module's own options.
/// </summary>
internal interface IStudioModule
{
    /// <summary>The module's key: its audit scope kind, and the composition's identity.</summary>
    string Key { get; }

    /// <summary>The module's product name in a sentence and on the rail's credit.</summary>
    string ProductName { get; }

    /// <summary>The module's page assembly. <see cref="StudioPageAssembly.KeepsRoot" /> must be <see langword="true" />: a module's pages never land over the host's.</summary>
    StudioPageAssembly Pages { get; }

    /// <summary>The module's rail sections, in order. Titles and labels must be unique across the composed rail.</summary>
    IReadOnlyList<StudioNavSection> Navigation { get; }

    /// <summary>The header component drawn in place of <c>ScopeSelector</c> on the module's pages: a component with no parameters.</summary>
    Type ScopeSelector { get; }

    /// <summary>One sentence the anonymous banner adds after the host's.</summary>
    string ExposedSentence { get; }

    /// <summary>The capability chip and banner's heading for the module's capabilities.</summary>
    string CapabilitiesHeading { get; }

    /// <summary>The module's capabilities as the chip lists them, read live on every render.</summary>
    IReadOnlyList<StudioModuleCapability> Capabilities { get; }

    /// <summary>The module's keyboard shortcuts for the sheet; empty for a module with none.</summary>
    IReadOnlyList<StudioShortcutGroup> Shortcuts { get; }
}

/// <summary>One module capability as the chip lists it. Whether it is on is computed by the shell: not read-only, and configured.</summary>
/// <param name="Name">The capability's name.</param>
/// <param name="Option">The option that switches it on, as a host would write it.</param>
/// <param name="Configured">Whether the host switched it on, without the master switch.</param>
internal sealed record StudioModuleCapability(string Name, string Option, bool Configured);

/// <summary>
/// Composes the registered modules into the host studio's descriptor (D34). With no module the result is the host's descriptor
/// itself, the very instance, so a host that never registered a module is byte for byte what it was.
/// </summary>
internal static class StudioModuleComposition
{
    /// <summary>The compile-time roots of the host studios' own pages, which no module may claim.</summary>
    private static readonly string[] HostRoots = ["/postgres", "/marten"];

    /// <summary>Composes <paramref name="host" /> with <paramref name="modules" />.</summary>
    /// <param name="host">The studio's static descriptor.</param>
    /// <param name="modules">The registered modules, in any order.</param>
    /// <exception cref="InvalidOperationException">
    /// Two modules share a key, a module's pages do not keep their root, a root is another assembly's or a host studio's, or a
    /// section title or item label of a module collides with another one on the composed rail.
    /// </exception>
    public static StudioDescriptor Compose(StudioDescriptor host, IEnumerable<IStudioModule> modules)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(modules);

        IStudioModule[] ordered = [.. modules.OrderBy(static x => x.Key, StringComparer.Ordinal)];
        if (ordered.Length == 0)
        {
            return host;
        }

        Validate(host, ordered);
        return host.WithModules(ordered);
    }

    private static void Validate(StudioDescriptor host, IStudioModule[] modules)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        List<string> roots = [.. HostRoots, .. host.PageAssemblies.Select(static x => Normalized(x.CompileTimeRoot))];

        foreach (IStudioModule module in modules)
        {
            if (!keys.Add(module.Key))
            {
                throw new InvalidOperationException($"Two modules are registered under the key '{module.Key}'. Each module has a key of its own.");
            }

            if (!module.Pages.KeepsRoot)
            {
                throw new InvalidOperationException(
                    $"The pages of module '{module.Key}' do not keep their root (StudioPageAssembly.KeepsRoot). A module's pages are served below "
                    + "its own segment under the mount, so that they never land over the host studio's.");
            }

            string root = Normalized(module.Pages.CompileTimeRoot);
            if (roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The pages of module '{module.Key}' are rooted at '{module.Pages.CompileTimeRoot}', which another page assembly of the studio already uses.");
            }

            roots.Add(root);
        }

        // Every title and label on the composed rail is a sibling @key in one list (NavMenu), so a module's may collide with
        // nothing: not the host's, not another module's, and not another of its own. The host's own rail is not judged here.
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (StudioNavSection section in host.Navigation)
        {
            names.Add(section.Title);
            foreach (StudioNavItem item in section.Items)
            {
                names.Add(item.Label);
            }
        }

        foreach (IStudioModule module in modules)
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
