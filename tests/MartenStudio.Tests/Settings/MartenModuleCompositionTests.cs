using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Settings;

/// <summary>
/// D34 over Marten Studio's descriptor: with no other module the shell has the database as its one root module; with one the module's section goes before
/// the root's last (System), the root's own sections and the static descriptor are untouched, and the module's pages join the assemblies. The module is a fake
/// whose pages name this test assembly, so no route table is built.
/// </summary>
public class MartenModuleCompositionTests
{
    private sealed class FakeModule : IStudioModule
    {
        public string Key => "fake";

        public string ProductName => "Fake";

        public bool IsRoot => false;

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [new(typeof(FakeModule).Assembly, "/fake") { KeepsRoot = true }];

        public IReadOnlyList<StudioNavSection> Navigation { get; } =
            [new StudioNavSection("Fake section", [new StudioNavItem("Fake entry", "fake", "grid")])];

        public Type? ScopeSelector => typeof(object);

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => "fake";

        public string? ExposedSentence => string.Empty;

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => [];

        public string? AllCapabilitiesOption => null;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    private static readonly StudioDescriptor Host = MartenStudio.Services.MartenStudioDescriptor.Instance;

    private static readonly MartenStudio.Services.MartenStudioSettings Settings = MartenStudio.Services.MartenStudioSettings.Of(Options.Create(new MartenStudioOptions()));

    private static DatabaseStudioModule Database() => new(Host, Settings, new StudioCapabilityGuard(Settings));

    [Fact]
    public void No_other_module_registered_resolves_the_database_as_the_one_root_module()
    {
        ServiceCollection services = new();
        services.AddMartenStudio();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<StudioDescriptor>().Should().BeSameAs(Host);
        StudioShellDescriptor shell = provider.GetRequiredService<StudioShellDescriptor>();
        shell.Modules.Should().ContainSingle().Which.Should().BeOfType<DatabaseStudioModule>().Which.IsRoot.Should().BeTrue();
        shell.PageAssemblies.Should().Equal(Host.PageAssemblies, "Marten Studio's root owns both its own assembly and PostgresStudio's");
    }

    [Fact]
    public void A_module_section_goes_before_the_roots_last_and_nothing_of_the_roots_moves()
    {
        StudioShellDescriptor composed = StudioModuleComposition.Compose(Host, [Database(), new FakeModule()]);

        string[] hostTitles = [.. Host.Navigation(Settings).Select(static x => x.Title)];
        composed.Navigation.Select(static x => x.Title).Should().Equal([.. hostTitles[..^1], "Fake section", hostTitles[^1]]);
        composed.Navigation.Single(static x => x.ModuleKey is not null).Title.Should().Be("Fake section");
        composed.Navigation.Where(static x => x.ModuleKey is null).Should().HaveCount(hostTitles.Length);
        composed.ProductName.Should().Be(Host.ProductName);
        composed.ExtraShortcuts.Should().HaveSameCount(Host.ExtraShortcuts, "Marten Studio's document shortcuts are carried");
        composed.PageAssemblies.Should().HaveCount(Host.PageAssemblies.Count + 1);
    }

    [Fact]
    public void A_module_label_that_collides_with_a_marten_one_is_refused()
    {
        string label = Host.Navigation(Settings).SelectMany(static x => x.Items).First().Label;
        FakeModule module = new();
        StudioNavSection[] colliding = [new StudioNavSection(label.ToUpperInvariant(), [])];

        Action compose = () => StudioModuleComposition.Compose(Host, [Database(), new Colliding(module, colliding)]);

        compose.Should().Throw<InvalidOperationException>().WithMessage("*unique, ignoring case*");
    }

    private sealed class Colliding(FakeModule inner, IReadOnlyList<StudioNavSection> navigation) : IStudioModule
    {
        public string Key => inner.Key;

        public string ProductName => inner.ProductName;

        public bool IsRoot => inner.IsRoot;

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => inner.PageAssemblies;

        public IReadOnlyList<StudioNavSection> Navigation => navigation;

        public Type? ScopeSelector => inner.ScopeSelector;

        public Type? HeaderSearch => inner.HeaderSearch;

        public Type? CircuitHost => inner.CircuitHost;

        public Type? ScopeGate => inner.ScopeGate;

        public string HomeRoute => inner.HomeRoute;

        public string? ExposedSentence => inner.ExposedSentence;

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => inner.CapabilityGroups;

        public string? AllCapabilitiesOption => inner.AllCapabilitiesOption;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => inner.Shortcuts;

        public string AuditEventIds => inner.AuditEventIds;
    }
}
