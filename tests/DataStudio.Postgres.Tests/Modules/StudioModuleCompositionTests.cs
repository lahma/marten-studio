using System.Reflection;

using DataStudio.Components;
using DataStudio.Services.Settings;
using DataStudio.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Tests.Modules;

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

    private static StudioShellDescriptor Compose(params IStudioModule[] others) => StudioModuleComposition.Compose([Database(), .. others]);

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

        public string ExposedSentence => "Fake sentence.";

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

        public string ExposedSentence => string.Empty;

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => [];

        public string? AllCapabilitiesOption => null;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    /// <summary>
    /// What a section says, without the module's stamp, which the composition adds (6.1): the entries' words and, per entry, whether it carries a
    /// condition and which - a factory that dropped Query's <c>RunSqlEnabled</c> or Stores' key would change one of them.
    /// </summary>
    private static (string Title, (string Label, string? Route, string Icon, string? BadgeKey, string? Title, bool HasIsShown, string? ShownWhen)[] Items) Shape(StudioNavSection section) =>
        (section.Title, [.. section.Items.Select(static i => (i.Label, i.Route, i.Icon, i.BadgeKey, i.Title, i.IsShown is not null, i.ShownWhen))]);

    /// <summary>
    /// Every page template of every assembly of the database's descriptor, as the dashboard-relative path that reaches it (a parameter segment is given a
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
                    string relative = route.Template[pageAssembly.CompileTimeRoot.Length..].TrimStart('/');
                    paths.Add(string.Join('/', relative.Length == 0 ? [] : relative.Split('/').Select(static x => x.StartsWith('{') ? "value" : x)));
                }
            }
        }

        return [.. paths.Distinct()];
    }

    [Fact]
    public void The_composed_route_table_matches_every_page_of_the_database_as_the_databases_own_does()
    {
        StudioShellDescriptor shell = Compose();
        StudioRouteTable own = new(Host.PageAssemblies);
        string[] paths = EveryPagePath(Host);

        paths.Should().HaveCountGreaterThan(14, "every page of the assembly is compared, not the rail's entries alone")
            .And.Contain("database/row", "a page only a link reaches is among them");
        foreach (string path in paths)
        {
            Microsoft.AspNetCore.Components.RouteData? expected = own.Match(path);
            expected.Should().NotBeNull("'{0}' is a page of the database's own", path);
            shell.Routes.Match(path)!.PageType.Should().Be(expected!.PageType, "'{0}' is the same page in the composed table", path);
        }
    }

    [Fact]
    public void No_module_registered_resolves_the_database_as_the_one_root_module()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddPostgres();
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

        // The words are the shell's own (v2 step 4): its name, and the guard's lead built from what the module says its pages expose.
        StudioShellIdentity.ProductName.Should().Be("Data Studio");
        StudioShellIdentity.Surface.Should().Be("Data Studio");
        shell.Exposure.Should().Be(
            "Data Studio is mapped with no authorization. " + Database().ExposedSentence
            + " Data Studio refuses to start rather than serve that by accident. Say which you meant:");
        StudioShellIdentity.Remedies.Should().Contain("app.MapDataStudio().RequireAuthorization()").And.Contain("services.AddDataStudio(options => options.AuthorizationPolicy").And.Contain("app.MapDataStudio().AllowAnonymous()");

        // The lists are the database's own, in order, then the shell's own Activity page. The navigation is built once per registration, so the two builds are compared by what they say.
        shell.PageAssemblies.Should().Equal([.. Host.PageAssemblies, StudioShellDescriptor.ShellPages]);
        shell.ExtraShortcuts.Should().Equal(Host.ExtraShortcuts);
        shell.Navigation.Select(Shape).Should().BeEquivalentTo(Host.Navigation(Settings).Select(Shape), static options => options.WithStrictOrdering());

        // Shell A2b, 6.1: the one expected value that moves. The root's sections are stamped with the root's key too, so that a section's rail is
        // the rail of its module, whichever module it is; before, they carried none.
        shell.Navigation.Should().OnlyContain(static x => x.ModuleKey == "postgres", "the root's own sections carry the root's key");

        // The route table is the database's own plus the shell's Activity page: one built from those alone matches every entry's route to the same page.
        StudioRouteTable own = new([.. Host.PageAssemblies, StudioShellDescriptor.ShellPages]);
        string[] routes = [.. Host.Navigation(Settings).SelectMany(static x => x.Items).Select(static x => x.Route).OfType<string>()];
        routes.Should().NotBeEmpty();
        foreach (string route in routes)
        {
            Microsoft.AspNetCore.Components.RouteData? expected = own.Match(route, Settings);
            expected.Should().NotBeNull("'{0}' is an entry of the database's own rail", route);
            shell.Routes.Match(route, Settings)!.PageType.Should().Be(expected!.PageType, "'{0}' is the same page in the composed table", route);
        }
    }

    [Fact]
    public void Module_sections_go_before_the_roots_last_section_in_key_order()
    {
        // Registered out of order on purpose: the composition sorts by key, ordinally.
        StudioShellDescriptor composed = Compose(new FakeModule("b", "/b", "Bravo"), new FakeModule("a", "/a", "Alpha"));

        string[] hostTitles = [.. Host.Navigation(Settings).Select(static x => x.Title)];
        composed.Navigation.Select(static x => x.Title).Should().Equal([.. hostTitles[..^1], "Alpha", "Bravo", hostTitles[^1]]);
        composed.Navigation.Where(static x => x.ModuleKey != "postgres").Select(static x => x.ModuleKey).Should().Equal("a", "b");
        composed.Navigation.Where(static x => x.ModuleKey == "postgres").Should().HaveCount(hostTitles.Length, "the root's own sections carry the root's key (6.1)");
        composed.Modules.Select(static x => x.Key).Should().Equal("postgres", "a", "b");
        composed.PageAssemblies.Should().HaveCount(Host.PageAssemblies.Count + 3, "the two modules' and the shell's own");
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
        services.AddDataStudio().AddPostgres();
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
