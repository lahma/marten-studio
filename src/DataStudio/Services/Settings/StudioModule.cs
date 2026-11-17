namespace DataStudio.Services.Settings;

/// <summary>
/// A section of the shell: its pages, its rail section, its scope selector, its words and its capabilities (D34). The database - the Postgres
/// module's pages or the Marten module's (<c>DatabaseStudioModule</c>) - is one, and a module added beside it is another; every module's pages sit below a segment of its
/// own under the mount, and none is the studio itself. Registered by the module's own <c>Add*()</c> with <c>TryAddEnumerable</c>, read once by the
/// composition (<see cref="StudioModuleComposition" />, a singleton), so every member is pure, does no IO and reads only the module's own options.
/// </summary>
internal interface IStudioModule
{
    /// <summary>The module's key: its audit scope kind, and the composition's identity.</summary>
    string Key { get; }

    /// <summary>The module's product name in a sentence and on the rail's credit.</summary>
    string ProductName { get; }

    /// <summary>
    /// The module's page assemblies, each with the root its pages' <c>@page</c> directives start with, which is also the segment they are served
    /// under. The Marten module has two (its own, and the Postgres pages over its databases). No two assemblies of a composition share a root,
    /// and none may use a segment the shell keeps (<see cref="StudioModuleComposition.ReservedSegments" />).
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
    /// The module's front page below the mount, as the whole address: <c>postgres</c> for the Postgres module, <c>marten</c> for the Marten module,
    /// the module's own segment for any other. The shell's front page links to it, or sends the visitor there when it is the one module composed.
    /// </summary>
    string HomeRoute { get; }

    /// <summary>One sentence of what the module offers, drawn on the shell's front page under the module's name. Pure: it reads nothing.</summary>
    string Summary { get; }

    /// <summary>
    /// What a page of the module exposes to anyone who can reach it, as one sentence: the anonymous banner draws one <c>&lt;span&gt;</c> of it per
    /// module, and the startup guard's lead sentence joins them. It says what the module's pages show and to whom, so a host reads what
    /// <c>AllowAnonymous()</c> would give away.
    /// </summary>
    string ExposedSentence { get; }

    /// <summary>
    /// The module's capabilities as the chip lists them, one group each, read live on every render; empty for a module with none. The chip
    /// draws each group under the heading <c>{ProductName}: {group name}</c>, the same shape for every module.
    /// </summary>
    IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups { get; }

    /// <summary>
    /// The module's own switch that turns all of its capabilities off, spelled as a host writes it (<c>MartenOptions.ReadOnly</c>), while that
    /// switch is on; <see langword="null" /> while it is off and for a module with no switch of its own. The chip says it under the module's first
    /// group. The shell's own switch is not here: it turns the chip itself into "Read-only".
    /// </summary>
    string? ReadOnlyOption => null;

    /// <summary>The one-line opt-in that turns all of the module's capabilities on (<c>MartenCapabilities.All()</c>), or <see langword="null" /> for a module with none.</summary>
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

/// <summary>A named group of a module's capabilities, listed under the module's name in the chip's popover.</summary>
/// <param name="Name">The group's name without the module's: <c>reads authorized like writes</c>.</param>
/// <param name="Capabilities">The group's capabilities, in order.</param>
internal sealed record StudioModuleCapabilityGroup(string Name, IReadOnlyList<StudioModuleCapability> Capabilities);

/// <summary>
/// Composes the registered modules into the shell's descriptor (D34): every module in registration order, the order of the host's <c>Add…()</c>
/// calls on the builder, so the host that wants a module first writes it first. A module's page assemblies, rail sections, shortcuts, capability
/// groups and front tile follow in that order, then the shell's own rail section; the shell's words are its own (<see cref="StudioShellIdentity" />).
/// What is refused does not depend on the order.
/// </summary>
internal static class StudioModuleComposition
{
    /// <summary>
    /// The first segments of the addresses the shell itself answers below the mount, which no module's pages may be rooted at: its own page
    /// (<c>activity</c>) and the framework's plumbing it mirrors under the mount (<c>_blazor</c>, <c>_framework</c>, <c>_content</c>,
    /// <c>opaque-redirect</c>). A module with no segment at all would sit over the shell's front page.
    /// </summary>
    internal static readonly string[] ReservedSegments = ["activity", "_blazor", "_framework", "_content", "opaque-redirect"];

    /// <summary>Composes the shell's descriptor from <paramref name="modules" />.</summary>
    /// <param name="modules">The registered modules, in registration order.</param>
    /// <exception cref="InvalidOperationException">
    /// Two modules share a key, two page assemblies share a root (ignoring case), a root is more than one segment, empty or one the shell uses,
    /// or a section title or item label of a module collides with another one on the composed rail, with the shell's own title (<c>Studio</c>) or
    /// with its label (<c>Activity</c>).
    /// </exception>
    public static StudioShellDescriptor Compose(IEnumerable<IStudioModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        IStudioModule[] ordered = [.. modules];

        Validate(ordered);

        return new StudioShellDescriptor { Modules = ordered };
    }

    private static void Validate(IStudioModule[] modules)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);

        // The shell's own root is in the table too, so that no module can take it.
        Dictionary<string, string> roots = new(StringComparer.OrdinalIgnoreCase)
        {
            [Normalized(StudioShellDescriptor.ShellPages.CompileTimeRoot)] = "the shell",
        };

        foreach (IStudioModule module in modules)
        {
            if (!keys.Add(module.Key))
            {
                throw new InvalidOperationException($"Two modules are registered under the key '{module.Key}'. Each module has a key of its own.");
            }

            foreach (StudioPageAssembly pages in module.PageAssemblies)
            {
                string pageRoot = Normalized(pages.CompileTimeRoot);

                // One segment, so that a root is its own and never inside another's: '/postgres/sub' would put pages inside the Postgres module's
                // segment, where the router and the layout would attribute them to a module by assembly all the same.
                if (pages.Segment.Contains('/', StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The pages of module '{module.Key}' are rooted at '{pages.CompileTimeRoot}', which is more than one segment. A page assembly's root is "
                        + "exactly one segment below the mount, so that no assembly's pages are inside another's.");
                }

                string firstSegment = pages.Segment;

                if (firstSegment.Length == 0 || ReservedSegments.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The pages of module '{module.Key}' are rooted at '{pages.CompileTimeRoot}', which the shell keeps for itself. A module's pages are "
                        + "served below a segment of their own under the mount, and it is none of: " + string.Join(", ", ReservedSegments) + ".");
                }

                if (!roots.TryAdd(pageRoot, module.Key))
                {
                    throw new InvalidOperationException(
                        $"The pages of module '{module.Key}' are rooted at '{pages.CompileTimeRoot}', which the pages of '{roots[pageRoot]}' already use. "
                        + "Every page assembly of a studio has a root of its own.");
                }
            }
        }

        // Every title and label on the composed rail is a sibling @key in one list (NavMenu), so a module's may collide with nothing: not another
        // module's, and not another of its own. The shell's own section is on the rail too, last, and is judged with them.
        // A module may therefore use neither the shell's section title ("Studio") nor its label ("Activity").
        StudioNavSection shell = StudioShellDescriptor.ShellRail();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { shell.Title };
        names.UnionWith(shell.Items.Select(static x => x.Label));

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
                $"The rail entry '{name}' of module '{module.Key}' is already a section title or a label on the rail (the shell's own section is titled "
                + $"'{StudioShellDescriptor.RailTitle}' and holds the label 'Activity'). Every title and label is unique, ignoring case, because the rail keys its entries on them.");
        }
    }

    private static string Normalized(string root) => root.TrimEnd('/');
}
