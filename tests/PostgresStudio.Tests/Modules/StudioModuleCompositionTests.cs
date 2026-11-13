using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Modules;

/// <summary>
/// D34: the shell's descriptor is the host studio's static descriptor composed with the registered modules when first resolved. With no module it is
/// the very instance; with some it carries every property of the host's, adds the modules' pages, rail sections and shortcuts, and refuses the
/// compositions that would put two entries on one key. The modules here are fakes whose pages name this test assembly, so no route table is built.
/// </summary>
public class StudioModuleCompositionTests
{
    private static readonly StudioDescriptor Host = PostgresStudioDescriptor.Instance;

    private sealed class FakeModule(string key, string root, params string[] sections) : IStudioModule
    {
        public bool KeepRoot { get; init; } = true;

        public string Key => key;

        public string ProductName => "Fake " + key;

        public StudioPageAssembly Pages => new(typeof(FakeModule).Assembly, root) { KeepsRoot = KeepRoot };

        public IReadOnlyList<StudioNavSection> Navigation { get; } =
            [.. sections.Select(static title => new StudioNavSection(title, [new StudioNavItem(title + " item", title.ToLowerInvariant(), "grid")]))];

        public Type ScopeSelector => typeof(object);

        public string ExposedSentence => "Fake sentence.";

        public string CapabilitiesHeading => "Fake reads";

        public IReadOnlyList<StudioModuleCapability> Capabilities { get; } = [];

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [new StudioShortcutGroup(key + " keys", [])];

        public string AuditEventIds => string.Empty;
    }

    private sealed class OtherFakeModule : IStudioModule
    {
        public string Key => "other";

        public string ProductName => "Other";

        public StudioPageAssembly Pages => new(typeof(OtherFakeModule).Assembly, "/other") { KeepsRoot = true };

        public IReadOnlyList<StudioNavSection> Navigation => [];

        public Type ScopeSelector => typeof(object);

        public string ExposedSentence => string.Empty;

        public string CapabilitiesHeading => string.Empty;

        public IReadOnlyList<StudioModuleCapability> Capabilities => [];

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    [Fact]
    public void No_module_registered_resolves_the_very_descriptor_instance()
    {
        ServiceCollection services = new();
        services.AddPostgresStudio();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<StudioDescriptor>().Should().BeSameAs(Host);
        StudioModuleComposition.Compose(Host, []).Should().BeSameAs(Host);
    }

    [Fact]
    public void Every_descriptor_property_is_carried_into_the_composed_one()
    {
        StudioDescriptor composed = StudioModuleComposition.Compose(Host, [new FakeModule("fake", "/fake", "Fake")]);
        string[] composedByModules = [nameof(StudioDescriptor.PageAssemblies), nameof(StudioDescriptor.Navigation), nameof(StudioDescriptor.ExtraShortcuts), nameof(StudioDescriptor.Modules), nameof(StudioDescriptor.Routes)];

        PropertyInfo[] carried = [.. typeof(StudioDescriptor).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => !composedByModules.Contains(x.Name))];

        carried.Select(static x => x.Name).Should().Contain(["Key", "ProductName", "SourceQueryParameter", "OverviewComponent", "CreateResource", "Surface", "Remedies"],
            "the reflection sees the real properties");

        foreach (PropertyInfo property in carried)
        {
            object? expected = property.GetValue(Host);
            object? actual = property.GetValue(composed);

            if (expected is null || expected.GetType().IsValueType || expected is string)
            {
                actual.Should().Be(expected, "property {0} must be copied by the composition's constructor", property.Name);
            }
            else
            {
                actual.Should().BeSameAs(expected, "property {0} must be copied by the composition's constructor", property.Name);
            }
        }
    }

    [Fact]
    public void Module_sections_go_before_the_hosts_last_section_in_key_order()
    {
        // Registered out of order on purpose: the composition sorts by key, ordinally.
        StudioDescriptor composed = StudioModuleComposition.Compose(Host, [new FakeModule("b", "/b", "Bravo"), new FakeModule("a", "/a", "Alpha")]);

        string[] hostTitles = [.. Host.Navigation.Select(static x => x.Title)];
        composed.Navigation.Select(static x => x.Title).Should().Equal([.. hostTitles[..^1], "Alpha", "Bravo", hostTitles[^1]]);
        composed.Navigation.Where(static x => x.ModuleKey is not null).Select(static x => x.ModuleKey).Should().Equal("a", "b");
        composed.Navigation.Where(static x => x.ModuleKey is null).Should().HaveCount(Host.Navigation.Count, "the host's own sections never carry a module key");
        Host.Navigation.Should().OnlyContain(static x => x.ModuleKey == null, "the static descriptor is never touched");
        composed.Modules.Select(static x => x.Key).Should().Equal("a", "b");
        composed.PageAssemblies.Should().HaveCount(Host.PageAssemblies.Count + 2);
        composed.ExtraShortcuts.Select(static x => x.Title).Should().Equal([.. Host.ExtraShortcuts.Select(static x => x.Title), "a keys", "b keys"]);
    }

    [Fact]
    public void ModuleOf_answers_the_module_whose_assembly_the_page_is_in()
    {
        StudioDescriptor composed = StudioModuleComposition.Compose(Host, [new FakeModule("fake", "/fake", "Fake")]);

        composed.ModuleOf(typeof(StudioModuleCompositionTests))!.Key.Should().Be("fake");
        composed.ModuleOf(typeof(PostgresStudioOptions)).Should().BeNull("a page of the host studio's own assembly");
        Host.ModuleOf(typeof(StudioModuleCompositionTests)).Should().BeNull("the static descriptor has no modules");
    }

    [Fact]
    public void Two_registrations_of_one_module_type_compose_once()
    {
        ServiceCollection services = new();
        services.AddPostgresStudio();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStudioModule, OtherFakeModule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStudioModule, OtherFakeModule>());
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<StudioDescriptor>().Modules.Should().ContainSingle().Which.Key.Should().Be("other");
    }

    [Fact]
    public void A_module_that_does_not_keep_its_root_is_refused()
    {
        Action compose = () => StudioModuleComposition.Compose(Host, [new FakeModule("fake", "/fake", "Fake") { KeepRoot = false }]);

        compose.Should().Throw<InvalidOperationException>().WithMessage("*do not keep their root*");
    }

    [Theory]
    [InlineData("/postgres")]
    [InlineData("/marten")]
    [InlineData("/MARTEN/")]
    public void A_module_root_equal_to_a_host_root_is_refused(string root)
    {
        Action compose = () => StudioModuleComposition.Compose(Host, [new FakeModule("fake", root, "Fake")]);

        compose.Should().Throw<InvalidOperationException>().WithMessage("*another page assembly of the studio already uses*");
    }

    [Fact]
    public void Two_modules_with_one_key_or_one_root_are_refused()
    {
        Action sameKey = () => StudioModuleComposition.Compose(Host, [new FakeModule("fake", "/one", "One"), new FakeModule("fake", "/two", "Two")]);
        Action sameRoot = () => StudioModuleComposition.Compose(Host, [new FakeModule("one", "/same", "One"), new FakeModule("two", "/same", "Two")]);

        sameKey.Should().Throw<InvalidOperationException>().WithMessage("*under the key 'fake'*");
        sameRoot.Should().Throw<InvalidOperationException>().WithMessage("*already uses*");
    }

    [Theory]
    [InlineData("Overview")]
    [InlineData("overview")]
    [InlineData("OVERVIEW")]
    [InlineData("System")]
    public void A_rail_label_two_sections_share_is_refused_ignoring_case(string title)
    {
        // The host's rail has an "Overview" entry and a "System" section.
        Action compose = () => StudioModuleComposition.Compose(Host, [new FakeModule("fake", "/fake", title)]);

        compose.Should().Throw<InvalidOperationException>().WithMessage($"*'{title}' of module 'fake'*unique, ignoring case*");
    }

    [Fact]
    public void A_title_two_modules_share_is_refused()
    {
        Action compose = () => StudioModuleComposition.Compose(Host, [new FakeModule("a", "/a", "Shared"), new FakeModule("b", "/b", "shared")]);

        compose.Should().Throw<InvalidOperationException>().WithMessage("*'shared' of module 'b'*");
    }
}
