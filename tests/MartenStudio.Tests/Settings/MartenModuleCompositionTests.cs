using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Settings;

/// <summary>
/// D34 over Marten Studio's descriptor: with no module the container resolves the very instance; with one the module's section goes before the host's
/// last (System), the host's own sections and the static descriptor are untouched, and the module's pages join the assemblies. The module is a fake whose
/// pages name this test assembly, so no route table is built.
/// </summary>
public class MartenModuleCompositionTests
{
    private sealed class FakeModule : IStudioModule
    {
        public string Key => "fake";

        public string ProductName => "Fake";

        public StudioPageAssembly Pages => new(typeof(FakeModule).Assembly, "/fake") { KeepsRoot = true };

        public IReadOnlyList<StudioNavSection> Navigation { get; } =
            [new StudioNavSection("Fake section", [new StudioNavItem("Fake entry", "fake", "grid")])];

        public Type ScopeSelector => typeof(object);

        public string ExposedSentence => string.Empty;

        public string CapabilitiesHeading => string.Empty;

        public IReadOnlyList<StudioModuleCapability> Capabilities => [];

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    private static readonly StudioDescriptor Host = MartenStudio.Services.MartenStudioDescriptor.Instance;

    [Fact]
    public void No_module_registered_resolves_the_very_descriptor_instance()
    {
        ServiceCollection services = new();
        services.AddMartenStudio();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<StudioDescriptor>().Should().BeSameAs(Host);
    }

    [Fact]
    public void A_module_section_goes_before_the_hosts_last_and_nothing_of_the_hosts_moves()
    {
        StudioDescriptor composed = StudioModuleComposition.Compose(Host, [new FakeModule()]);

        string[] hostTitles = [.. Host.Navigation.Select(static x => x.Title)];
        composed.Navigation.Select(static x => x.Title).Should().Equal([.. hostTitles[..^1], "Fake section", hostTitles[^1]]);
        composed.Navigation.Single(static x => x.ModuleKey is not null).Title.Should().Be("Fake section");
        Host.Navigation.Should().OnlyContain(static x => x.ModuleKey == null);
        composed.Key.Should().Be(Host.Key);
        composed.SourceQueryParameter.Should().Be(Host.SourceQueryParameter);
        composed.ExtraShortcuts.Should().HaveSameCount(Host.ExtraShortcuts, "Marten Studio's document shortcuts are carried");
        composed.PageAssemblies.Should().HaveCount(Host.PageAssemblies.Count + 1);
    }

    [Fact]
    public void A_module_label_that_collides_with_a_marten_one_is_refused()
    {
        string label = Host.Navigation.SelectMany(static x => x.Items).First().Label;
        FakeModule module = new();
        StudioNavSection[] colliding = [new StudioNavSection(label.ToUpperInvariant(), [])];

        Action compose = () => StudioModuleComposition.Compose(Host, [new Colliding(module, colliding)]);

        compose.Should().Throw<InvalidOperationException>().WithMessage("*unique, ignoring case*");
    }

    private sealed class Colliding(FakeModule inner, IReadOnlyList<StudioNavSection> navigation) : IStudioModule
    {
        public string Key => inner.Key;

        public string ProductName => inner.ProductName;

        public StudioPageAssembly Pages => inner.Pages;

        public IReadOnlyList<StudioNavSection> Navigation => navigation;

        public Type ScopeSelector => inner.ScopeSelector;

        public string ExposedSentence => inner.ExposedSentence;

        public string CapabilitiesHeading => inner.CapabilitiesHeading;

        public IReadOnlyList<StudioModuleCapability> Capabilities => inner.Capabilities;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => inner.Shortcuts;

        public string AuditEventIds => inner.AuditEventIds;
    }
}
