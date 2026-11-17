using System.Reflection;
using System.Reflection.Emit;

using AngleSharp.Dom;

using Bunit;

using DataStudio.Components.Layout;
using DataStudio.Services.Settings;
using DataStudio.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataStudio.Marten.Tests.Support;

/// <summary>
/// What a module added beside the database module is to the shell, defined by the suites that have the database module (linked into
/// <c>DataStudio.Marten.Tests</c> and <c>DataStudio.Postgres.Tests</c>): a root of its own (<c>/second</c>), one page, a rail section, a capability group with an
/// opt-in of its own, and no gate, no scope selector and no header search. It names no Kafka (<c>ModuleNeutralityTests</c>): the shell between two modules is
/// what it is for - which module's circuit host, gate and header a page meets, the chip, the tiles - and not any particular second module.
/// </summary>
/// <remarks>
/// Its page lives in an assembly built at run time, because a page assembly is every routable component of one assembly under one root, and the test
/// assemblies hold routable components of their own (a host page, route-table fixtures) that are not under <c>/second</c>.
/// </remarks>
internal sealed class SecondModule : IStudioModule
{
    /// <summary>The module's key.</summary>
    public const string ModuleKey = "second";

    /// <summary>The module's product name: the front page's tile, the chip's group heading.</summary>
    public const string Name = "Second";

    /// <summary>The page's address below the mount.</summary>
    public const string PageAddress = "second/page";

    /// <summary>The module's own one-line opt-in, as the chip names it.</summary>
    public const string AllOption = "SecondCapabilities.All()";

    /// <summary>What the module says of itself on the front page.</summary>
    public const string SummaryText = "A second module the tests define.";

    private static readonly IReadOnlyList<StudioNavSection> Rail = [new(Name, [new StudioNavItem("Second page", PageAddress, "cog")])];

    /// <summary>The module's one page, a routable component of an assembly of its own.</summary>
    public static Type PageType { get; } = EmitPage();

    /// <inheritdoc />
    public string Key => ModuleKey;

    /// <inheritdoc />
    public string ProductName => Name;

    /// <inheritdoc />
    public IReadOnlyList<StudioPageAssembly> PageAssemblies { get; } = [new(PageType.Assembly, "/second")];

    /// <inheritdoc />
    public IReadOnlyList<StudioNavSection> Navigation => Rail;

    /// <inheritdoc />
    public Type? ScopeSelector => null;

    /// <inheritdoc />
    public Type? HeaderSearch => null;

    /// <inheritdoc />
    public Type? CircuitHost => null;

    /// <inheritdoc />
    public Type? ScopeGate => null;

    /// <inheritdoc />
    public string HomeRoute => PageAddress;

    /// <inheritdoc />
    public string Summary => SummaryText;

    /// <inheritdoc />
    public string ExposedSentence => "The second page shows nothing.";

    /// <inheritdoc />
    public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups { get; } =
    [
        new("things it can do", [new StudioModuleCapability("SecondRead", "SecondOptions.Capabilities.SecondRead", Configured: false)]),
    ];

    /// <inheritdoc />
    public string? AllCapabilitiesOption => AllOption;

    /// <inheritdoc />
    public IReadOnlyList<StudioShortcutGroup> Shortcuts { get; } = [];

    /// <inheritdoc />
    public string AuditEventIds => string.Empty;

    /// <summary>Adds the module to a container, as a module's own <c>Add…()</c> does: after the database module the context registered.</summary>
    /// <param name="services">The container.</param>
    public static void AddTo(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStudioModule, SecondModule>());

    private static Type EmitPage()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DataStudio.Tests.SecondPages"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("SecondPages");
        TypeBuilder page = module.DefineType(
            "DataStudio.Tests.SecondPages.SecondPage",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(SecondModulePage));
        page.DefineDefaultConstructor(MethodAttributes.Public);
        page.SetCustomAttribute(new CustomAttributeBuilder(typeof(RouteAttribute).GetConstructor([typeof(string)])!, ["/second/page"]));

        return page.CreateType();
    }
}

/// <summary>The behaviour of <see cref="SecondModule" />'s page; the page itself is the routable type derived from it in the module's own assembly.</summary>
public abstract class SecondModulePage : ComponentBase
{
    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "h1");
        builder.AddContent(1, "Second page");
        builder.CloseElement();
    }
}

/// <summary>
/// What the capability chip's popover says in its footnotes, read from the markup: each module's own line (the one under that module's last group, naming the
/// module's own opt-in), the shell's line, and the popover's whole text.
/// </summary>
/// <param name="OwnLines">The code a module's own footnote names, by the module's product name.</param>
/// <param name="ShellLine">The footnote that is the shell's: what is off until the host sets it, and the switch that turns every module's capabilities off.</param>
/// <param name="Chip">The popover's text.</param>
internal sealed record ChipLines(IReadOnlyDictionary<string, string> OwnLines, string ShellLine, string Chip)
{
    /// <summary>Reads the lines of a rendered chip.</summary>
    /// <param name="chip">The rendered chip, with its capabilities off.</param>
    public static ChipLines Read(IRenderedComponent<CapabilityBanner> chip)
    {
        Dictionary<string, string> own = new(StringComparer.Ordinal);
        string? shell = null;
        string module = string.Empty;
        IElement popover = chip.Find(".ms-capability-popover");

        foreach (IElement child in popover.Children)
        {
            if (child.ClassList.Contains("ms-popover-title"))
            {
                module = child.TextContent.Trim().Split(':')[0];
            }
            else if (child.ClassList.Contains("ms-popover-footnote") && child.TextContent.Contains("Every one of these", StringComparison.Ordinal))
            {
                shell = child.TextContent.Trim();
            }
            else if (child.ClassList.Contains("ms-popover-footnote") && child.QuerySelector("code") is { } code && child.TextContent.Contains("turns every capability of this module on", StringComparison.Ordinal))
            {
                own.Add(module, code.TextContent.Trim());
            }
        }

        return new ChipLines(own, shell ?? string.Empty, popover.TextContent);
    }
}
