using System.Reflection;

using DataStudio.Components;
using DataStudio.Services.Settings;
using DataStudio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Marten.Tests.Settings;

/// <summary>
/// D34 over Marten Studio's descriptor: with no other module the shell has the Marten module alone; with one the modules compose in key order, the
/// module's own sections and the static descriptor are untouched, the shell's own section closes the rail, and the module's pages join the assemblies.
/// The module is a fake whose pages name this test assembly, so no route table is built.
/// </summary>
public class MartenModuleCompositionTests
{
    private sealed class FakeModule : IStudioModule
    {
        public string Key => "fake";

        public string ProductName => "Fake";

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [new(typeof(FakeModule).Assembly, "/fake")];

        public IReadOnlyList<StudioNavSection> Navigation { get; } =
            [new StudioNavSection("Fake section", [new StudioNavItem("Fake entry", "fake", "grid")])];

        public Type? ScopeSelector => typeof(object);

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => "fake";

        public string Summary => "A fake.";

        public string ExposedSentence => string.Empty;

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => [];

        public string? AllCapabilitiesOption => null;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    private static readonly StudioDescriptor Host = DataStudio.Marten.Services.MartenStudioDescriptor.Instance;

    private static readonly DataStudio.Marten.Services.MartenStudioSettings Settings = DataStudio.Marten.Services.MartenStudioSettings.Of(Options.Create(new MartenOptions()));

    private static DatabaseStudioModule Database() => new(Host, Settings, new StudioCapabilityGuard(Settings));

    /// <summary>
    /// Every page template of every assembly of the database's descriptor, as the address below the mount that reaches it (a parameter segment is given a
    /// value): not only the rail's routes, but the pages a link alone reaches, such as a detail page.
    /// </summary>
    private static string[] EveryPagePath(StudioDescriptor descriptor)
    {
        List<string> paths = [];

        foreach (StudioPageAssembly pageAssembly in descriptor.PageAssemblies)
        {
            foreach (Type type in pageAssembly.Assembly.GetTypes().Where(static x => typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(x)))
            {
                foreach (Microsoft.AspNetCore.Components.RouteAttribute route in type.GetCustomAttributes<Microsoft.AspNetCore.Components.RouteAttribute>())
                {
                    string relative = route.Template.TrimStart('/');
                    paths.Add(string.Join('/', relative.Length == 0 ? [] : relative.Split('/').Select(static x => x.StartsWith('{') ? "value" : x)));
                }
            }
        }

        return [.. paths.Distinct()];
    }

    [Fact]
    public void The_composed_route_table_matches_every_page_of_both_assemblies_as_the_databases_own_does()
    {
        StudioShellDescriptor shell = StudioModuleComposition.Compose([Database()]);
        StudioRouteTable own = new(Host.PageAssemblies);
        string[] paths = EveryPagePath(Host);

        paths.Should().HaveCountGreaterThan(28, "Marten Studio's own pages and PostgresStudio's are all compared, not the rail's entries alone")
            .And.Contain("marten/documents/value/doc", "a page only a link reaches is among them").And.Contain("postgres/database/row");
        foreach (string path in paths)
        {
            Microsoft.AspNetCore.Components.RouteData? expected = own.Match(path);
            expected.Should().NotBeNull("'{0}' is a page of the database's own", path);
            shell.Routes.Match(path)!.PageType.Should().Be(expected!.PageType, "'{0}' is the same page in the composed table", path);
        }
    }

    [Fact]
    public void No_other_module_registered_resolves_the_database_as_the_one_module()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddMarten();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<StudioDescriptor>().Should().BeSameAs(Host);
        StudioShellDescriptor shell = provider.GetRequiredService<StudioShellDescriptor>();
        shell.Modules.Should().ContainSingle().Which.Should().BeOfType<DatabaseStudioModule>().Which.HomeRoute.Should().Be("marten");
        shell.PageAssemblies.Should().Equal([.. Host.PageAssemblies, StudioShellDescriptor.ShellPages], "the Marten module owns both its own assembly and DataStudio.Postgres's, and the shell adds its own");
    }

    [Fact]
    public void Modules_compose_in_registration_order_with_the_shells_own_section_last_and_nothing_of_the_marten_module_moves()
    {
        StudioShellDescriptor composed = StudioModuleComposition.Compose([Database(), new FakeModule()]);

        string[] hostTitles = [.. Host.Navigation(Settings).Select(static x => x.Title)];
        hostTitles[^1].Should().Be("System", "the Marten module closes its own rail with System, and the shell's own section is titled Studio whatever it holds");
        composed.Navigation.Select(static x => x.Title).Should().Equal([.. hostTitles, "Fake section", "Studio"]);
        composed.Navigation[^1].Items.Select(static x => (x.Label, x.Route)).Should().Equal(("Activity", "activity"));
        composed.Navigation[^1].ModuleKey.Should().BeNull("the shell's own section is no module's");
        composed.Navigation.Single(static x => x.ModuleKey == "fake").Title.Should().Be("Fake section");

        // Every module's sections carry its key.
        composed.Navigation.Where(static x => x.ModuleKey == Host.Key).Should().HaveCount(hostTitles.Length);
        composed.ExtraShortcuts.Should().HaveSameCount(Host.ExtraShortcuts, "Marten Studio's document shortcuts are carried");
        composed.PageAssemblies.Should().HaveCount(Host.PageAssemblies.Count + 2, "the fake module's and the shell's own");
    }

    [Fact]
    public void A_module_label_that_collides_with_a_marten_one_is_refused()
    {
        string label = Host.Navigation(Settings).SelectMany(static x => x.Items).First().Label;
        FakeModule module = new();
        StudioNavSection[] colliding = [new StudioNavSection(label.ToUpperInvariant(), [])];

        Action compose = () => StudioModuleComposition.Compose([Database(), new Colliding(module, colliding)]);

        compose.Should().Throw<InvalidOperationException>().WithMessage("*unique, ignoring case*");
    }

    private sealed class Colliding(FakeModule inner, IReadOnlyList<StudioNavSection> navigation) : IStudioModule
    {
        public string Key => inner.Key;

        public string ProductName => inner.ProductName;

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => inner.PageAssemblies;

        public IReadOnlyList<StudioNavSection> Navigation => navigation;

        public Type? ScopeSelector => inner.ScopeSelector;

        public Type? HeaderSearch => inner.HeaderSearch;

        public Type? CircuitHost => inner.CircuitHost;

        public Type? ScopeGate => inner.ScopeGate;

        public string HomeRoute => inner.HomeRoute;

        public string Summary => inner.Summary;

        public string ExposedSentence => inner.ExposedSentence;

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => inner.CapabilityGroups;

        public string? AllCapabilitiesOption => inner.AllCapabilitiesOption;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => inner.Shortcuts;

        public string AuditEventIds => inner.AuditEventIds;
    }
}
