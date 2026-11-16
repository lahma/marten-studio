using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Modules;

/// <summary>
/// D34: the shell's descriptor is the database module (the root) composed with the registered modules when first resolved. With no module it
/// carries the database's own lists, element by element; with some it adds the modules' pages, rail sections and shortcuts after the root's, and
/// refuses the compositions that would put two entries on one key. The modules here are fakes whose pages name this test assembly, so no route
/// table is built.
/// </summary>
public class StudioModuleCompositionTests
{
    private static readonly StudioDescriptor Host = PostgresStudioDescriptor.Instance;

    private static readonly PostgresStudioSettings Settings = new(Options.Create(new PostgresStudioOptions()));

    /// <summary>The database module as <c>AddPostgresStudio()</c> registers it, for a composition that is about the modules beside it.</summary>
    private static DatabaseStudioModule Database() => new(Host, Settings, new StudioCapabilityGuard(Settings));

    private static StudioShellDescriptor Compose(params IStudioModule[] others) => StudioModuleComposition.Compose(Host, [Database(), .. others]);

    private sealed class FakeModule(string key, string root, params string[] sections) : IStudioModule
    {
        public bool KeepRoot { get; init; } = true;

        public string Key => key;

        public string ProductName => "Fake " + key;

        public bool IsRoot { get; init; }

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [new(typeof(FakeModule).Assembly, root) { KeepsRoot = KeepRoot }];

        public IReadOnlyList<StudioNavSection> Navigation { get; } =
            [.. sections.Select(static title => new StudioNavSection(title, [new StudioNavItem(title + " item", title.ToLowerInvariant(), "grid")]))];

        public Type? ScopeSelector => typeof(object);

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => key;

        public string? ExposedSentence => "Fake sentence.";

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => [];

        public string? AllCapabilitiesOption => null;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [new StudioShortcutGroup(key + " keys", [])];

        public string AuditEventIds => string.Empty;
    }

    private sealed class OtherFakeModule : IStudioModule
    {
        public string Key => "other";

        public string ProductName => "Other";

        public bool IsRoot => false;

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [new(typeof(OtherFakeModule).Assembly, "/other") { KeepsRoot = true }];

        public IReadOnlyList<StudioNavSection> Navigation => [];

        public Type? ScopeSelector => typeof(object);

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => "other";

        public string? ExposedSentence => string.Empty;

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => [];

        public string? AllCapabilitiesOption => null;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    private static (string Title, string? ModuleKey, (string Label, string? Route, string Icon, string? BadgeKey, string? Title)[] Items) Shape(StudioNavSection section) =>
        (section.Title, section.ModuleKey, [.. section.Items.Select(static i => (i.Label, i.Route, i.Icon, i.BadgeKey, i.Title))]);

    [Fact]
    public void No_module_registered_resolves_the_database_as_the_one_root_module()
    {
        ServiceCollection services = new();
        services.AddPostgresStudio();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<StudioDescriptor>().Should().BeSameAs(Host, "the database's descriptor is registered as itself, never composed");
        StudioShellDescriptor shell = provider.GetRequiredService<StudioShellDescriptor>();
        shell.Modules.Should().ContainSingle().Which.Should().BeOfType<DatabaseStudioModule>().Which.IsRoot.Should().BeTrue();
        shell.Root.Should().BeSameAs(shell.Modules[0]);
    }

    [Fact]
    public void With_no_other_module_the_shell_descriptor_equals_the_databases_own_lists_element_by_element()
    {
        StudioShellDescriptor shell = Compose();

        // The words are the database descriptor's.
        shell.ProductName.Should().Be(Host.ProductName);
        shell.ExposedContent.Should().Be(Host.ExposedContent);
        shell.Surface.Should().Be(Host.Surface);
        shell.Exposure.Should().Be(Host.Exposure);
        shell.Remedies.Should().Be(Host.Remedies);

        // The lists are the database's own, in order. The navigation is built once per registration, so the two builds are compared by what they say.
        shell.PageAssemblies.Should().Equal(Host.PageAssemblies);
        shell.ExtraShortcuts.Should().Equal(Host.ExtraShortcuts);
        shell.Navigation.Select(Shape).Should().BeEquivalentTo(Host.Navigation(Settings).Select(Shape), static options => options.WithStrictOrdering());
        shell.Navigation.Should().OnlyContain(static x => x.ModuleKey == null, "the root's own sections carry no module key");
        shell.Routes.Should().BeSameAs(shell.Routes, "built once");
    }

    [Fact]
    public void Module_sections_go_before_the_roots_last_section_in_key_order()
    {
        // Registered out of order on purpose: the composition sorts by key, ordinally.
        StudioShellDescriptor composed = Compose(new FakeModule("b", "/b", "Bravo"), new FakeModule("a", "/a", "Alpha"));

        string[] hostTitles = [.. Host.Navigation(Settings).Select(static x => x.Title)];
        composed.Navigation.Select(static x => x.Title).Should().Equal([.. hostTitles[..^1], "Alpha", "Bravo", hostTitles[^1]]);
        composed.Navigation.Where(static x => x.ModuleKey is not null).Select(static x => x.ModuleKey).Should().Equal("a", "b");
        composed.Navigation.Where(static x => x.ModuleKey is null).Should().HaveCount(hostTitles.Length, "the root's own sections never carry a module key");
        composed.Modules.Select(static x => x.Key).Should().Equal("postgres", "a", "b");
        composed.PageAssemblies.Should().HaveCount(Host.PageAssemblies.Count + 2);
        composed.ExtraShortcuts.Select(static x => x.Title).Should().Equal([.. Host.ExtraShortcuts.Select(static x => x.Title), "a keys", "b keys"]);
    }

    [Fact]
    public void ModuleOf_answers_the_module_whose_assembly_the_page_is_in_and_the_root_for_any_other()
    {
        StudioShellDescriptor composed = Compose(new FakeModule("fake", "/fake", "Fake"));

        composed.ModuleOf(typeof(StudioModuleCompositionTests))!.Key.Should().Be("fake");
        composed.ModuleOf(typeof(PostgresStudioOptions))!.Key.Should().Be("postgres", "a page of the database's own assembly");
        composed.ModuleOf(typeof(string))!.Key.Should().Be("postgres", "a page of no module's assembly is the root's");
    }

    [Fact]
    public void Two_registrations_of_one_module_type_compose_once()
    {
        ServiceCollection services = new();
        services.AddPostgresStudio();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStudioModule, OtherFakeModule>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStudioModule, OtherFakeModule>());
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<StudioShellDescriptor>().Modules.Select(static x => x.Key).Should().Equal("postgres", "other");
    }

    [Fact]
    public void A_module_that_does_not_keep_its_root_is_refused()
    {
        Action compose = () => Compose(new FakeModule("fake", "/fake", "Fake") { KeepRoot = false });

        compose.Should().Throw<InvalidOperationException>().WithMessage("*do not keep their root*");
    }

    [Theory]
    [InlineData("/postgres")]
    [InlineData("/marten")]
    [InlineData("/MARTEN/")]
    public void A_module_root_equal_to_a_host_root_is_refused(string root)
    {
        Action compose = () => Compose(new FakeModule("fake", root, "Fake"));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*another page assembly of the studio already uses*");
    }

    [Fact]
    public void Two_modules_with_one_key_or_one_root_are_refused()
    {
        Action sameKey = () => Compose(new FakeModule("fake", "/one", "One"), new FakeModule("fake", "/two", "Two"));
        Action sameRoot = () => Compose(new FakeModule("one", "/same", "One"), new FakeModule("two", "/same", "Two"));

        sameKey.Should().Throw<InvalidOperationException>().WithMessage("*under the key 'fake'*");
        sameRoot.Should().Throw<InvalidOperationException>().WithMessage("*already uses*");
    }

    [Fact]
    public void Two_root_modules_are_refused()
    {
        Action compose = () => Compose(new FakeModule("fake", "/fake", "Fake") { IsRoot = true });

        compose.Should().Throw<InvalidOperationException>().WithMessage("*both the root*");
    }

    [Theory]
    [InlineData("Overview")]
    [InlineData("overview")]
    [InlineData("OVERVIEW")]
    [InlineData("System")]
    public void A_rail_label_two_sections_share_is_refused_ignoring_case(string title)
    {
        // The root's rail has an "Overview" entry and a "System" section.
        Action compose = () => Compose(new FakeModule("fake", "/fake", title));

        compose.Should().Throw<InvalidOperationException>().WithMessage($"*'{title}' of module 'fake'*unique, ignoring case*");
    }

    [Fact]
    public void A_title_two_modules_share_is_refused()
    {
        Action compose = () => Compose(new FakeModule("a", "/a", "Shared"), new FakeModule("b", "/b", "shared"));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*'shared' of module 'b'*");
    }
}
