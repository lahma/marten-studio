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
/// D34: the shell's descriptor is the registered modules composed in registration order when first resolved, every one under a segment of its own. With the
/// database alone it carries the database's own lists, element by element, then the shell's own rail section; with more it adds the modules' pages,
/// rail sections and shortcuts in that order, and refuses the compositions that would put two entries on one key or two pages on one root, whatever the
/// order. The modules here are fakes whose pages name this test assembly, so no route table is built.
/// </summary>
public class StudioModuleCompositionTests
{
    private static readonly StudioDescriptor Host = PostgresDescriptor.Instance;

    private static readonly PostgresSettings Settings = PostgresSettings.Of(Options.Create(new PostgresOptions()));

    /// <summary>The database module as <c>AddPostgresStudio()</c> registers it, for a composition that is about the modules beside it.</summary>
    private static DatabaseStudioModule Database() => new(Host, Settings, new StudioCapabilityGuard(Settings));

    private static StudioShellDescriptor Compose(params IStudioModule[] others) => StudioModuleComposition.Compose([Database(), .. others]);

    private sealed class FakeModule(string key, string root, params string[] sections) : IStudioModule
    {
        public string Key => key;

        public string ProductName => "Fake " + key;

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [new(typeof(FakeModule).Assembly, root)];

        public IReadOnlyList<StudioNavSection> Navigation { get; } =
            [.. sections.Select(static title => new StudioNavSection(title, [new StudioNavItem(title + " item", title.ToLowerInvariant(), "grid")]))];

        public Type? ScopeSelector => typeof(object);

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => key;

        public string Summary => "A fake.";

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

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [new(typeof(OtherFakeModule).Assembly, "/other")];

        public IReadOnlyList<StudioNavSection> Navigation => [];

        public Type? ScopeSelector => typeof(object);

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => "other";

        public string Summary => "Other.";

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
    public void The_composed_route_table_matches_every_page_of_the_database_as_the_databases_own_does()
    {
        StudioShellDescriptor shell = Compose();
        StudioRouteTable own = new(Host.PageAssemblies);
        string[] paths = EveryPagePath(Host);

        paths.Should().HaveCountGreaterThan(14, "every page of the assembly is compared, not the rail's entries alone")
            .And.Contain("postgres/database/row", "a page only a link reaches is among them");
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
        services.AddDataStudio().AddPostgres();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<StudioDescriptor>().Should().BeSameAs(Host, "the database's descriptor is registered as itself, never composed");
        StudioShellDescriptor shell = provider.GetRequiredService<StudioShellDescriptor>();
        shell.Modules.Should().ContainSingle().Which.Should().BeOfType<DatabaseStudioModule>().Which.HomeRoute.Should().Be("postgres");
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

        // The lists are the database's own, in order, then the shell's own pages (the front page and Activity). The navigation is built once per registration,
        // so the two builds are compared by what they say, and the shell's own section closes it.
        shell.PageAssemblies.Should().Equal([.. Host.PageAssemblies, StudioShellDescriptor.ShellPages]);
        shell.ExtraShortcuts.Should().Equal([.. PostgresShortcuts.Groups, .. Host.ExtraShortcuts]);
        shell.Navigation.Take(shell.Navigation.Count - 1).Select(Shape).Should().BeEquivalentTo(Host.Navigation(Settings).Select(Shape), static options => options.WithStrictOrdering());
        shell.Navigation[^1].Title.Should().Be("Studio", "the shell's own section is always titled Studio");
        shell.Navigation[^1].Items.Select(static x => (x.Label, x.Route)).Should().Equal(("Activity", "activity"));
        Host.Navigation(Settings).SelectMany(static x => x.Items).Should().NotContain(static x => x.Route == "activity");

        // Every module's sections are stamped with the module's key, so that a section's rail is the rail of its module; the shell's own has none.
        shell.Navigation.Take(shell.Navigation.Count - 1).Should().OnlyContain(static x => x.ModuleKey == "postgres");
        shell.Navigation[^1].ModuleKey.Should().BeNull();

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
    public void Modules_compose_in_registration_order_and_the_shells_own_section_closes_the_rail()
    {
        // Registered out of key order on purpose: the composition keeps the order the host added them in, the database among the rest.
        StudioShellDescriptor composed = Compose(new FakeModule("b", "/b", "Bravo"), new FakeModule("a", "/a", "Alpha"));

        string[] hostTitles = [.. Host.Navigation(Settings).Select(static x => x.Title)];
        composed.Navigation.Select(static x => x.Title).Should().Equal([.. hostTitles, "Bravo", "Alpha", "Studio"]);
        composed.Navigation.Where(static x => x.ModuleKey is not null).Select(static x => x.ModuleKey).Distinct().Should().Equal("postgres", "b", "a");
        composed.Navigation.Where(static x => x.ModuleKey == "postgres").Should().HaveCount(hostTitles.Length, "the database's own sections carry its key");
        composed.Modules.Select(static x => x.Key).Should().Equal("postgres", "b", "a");
        composed.PageAssemblies.Should().HaveCount(Host.PageAssemblies.Count + 3, "the two modules' and the shell's own");
        composed.ExtraShortcuts.Select(static x => x.Title).Should().Equal([.. PostgresShortcuts.Groups.Concat(Host.ExtraShortcuts).Select(static x => x.Title), "b keys", "a keys"]);
    }

    [Fact]
    public void The_database_module_is_where_the_host_added_it_not_where_its_key_sorts()
    {
        StudioShellDescriptor first = StudioModuleComposition.Compose([new FakeModule("b", "/b", "Bravo"), Database(), new FakeModule("a", "/a", "Alpha")]);
        StudioShellDescriptor last = StudioModuleComposition.Compose([new FakeModule("b", "/b", "Bravo"), new FakeModule("a", "/a", "Alpha"), Database()]);

        first.Modules.Select(static x => x.Key).Should().Equal("b", "postgres", "a");
        last.Modules.Select(static x => x.Key).Should().Equal("b", "a", "postgres");
        first.Navigation.Select(static x => x.Title).First().Should().Be("Bravo");
        last.Navigation.Select(static x => x.Title).Last().Should().Be("Studio");
        last.Navigation.Select(static x => x.Title).SkipLast(1).Last().Should().Be(Host.Navigation(Settings)[^1].Title);
    }

    [Fact]
    public void The_database_modules_capability_groups_carry_the_modules_name()
    {
        DatabaseStudioModule module = Database();

        module.ProductName.Should().Be("Postgres");
        module.CapabilityGroups.Should().NotBeEmpty();
        module.CapabilityGroups[0].Name.Should().Be("reads authorized like writes");
    }

    [Fact]
    public void ModuleOf_answers_the_module_whose_assembly_the_page_is_in_and_null_for_a_page_of_no_module()
    {
        StudioShellDescriptor composed = Compose(new FakeModule("fake", "/fake", "Fake"));

        composed.ModuleOf(typeof(StudioModuleCompositionTests))!.Key.Should().Be("fake");
        composed.ModuleOf(typeof(PostgresOptions))!.Key.Should().Be("postgres", "a page of the database's own assembly");
        composed.ModuleOf(typeof(string)).Should().BeNull("a page of no module's assembly is no module's");
        composed.ModuleOf(typeof(StudioShellDescriptor)).Should().BeNull("the shell's own pages (the front page, Activity) are of no module");
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

    [Theory]
    [InlineData("/postgres")]
    [InlineData("/POSTGRES/")]
    public void A_module_root_equal_to_the_databases_own_is_refused(string root)
    {
        Action compose = () => Compose(new FakeModule("fake", root, "Fake"));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*'fake' are rooted at*which the pages of 'postgres' already use*");
    }

    [Theory]
    [InlineData("/activity")]
    [InlineData("/ACTIVITY")]
    [InlineData("/_blazor")]
    [InlineData("/_framework")]
    [InlineData("/_content")]
    [InlineData("/opaque-redirect")]
    [InlineData("/")]
    [InlineData("")]
    public void A_module_root_the_shell_keeps_for_itself_is_refused(string root)
    {
        Action compose = () => Compose(new FakeModule("fake", root, "Fake"));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*'fake' are rooted at*which the shell keeps for itself*");
    }

    [Theory]
    [InlineData("/postgres/sub")]
    [InlineData("/activity/more")]
    [InlineData("/fake/sub")]
    [InlineData("/a/b/c")]
    public void A_page_assemblys_root_is_one_segment_and_a_nested_one_is_refused_with_the_reason(string root)
    {
        Action compose = () => Compose(new FakeModule("fake", root, "Fake"));
        Action composeFirst = () => StudioModuleComposition.Compose([new FakeModule("fake", root, "Fake"), Database()]);

        foreach (Action action in new[] { compose, composeFirst })
        {
            action.Should().Throw<InvalidOperationException>().WithMessage("*'fake' are rooted at*which is more than one segment*inside another's*");
        }
    }

    [Fact]
    public void Two_modules_with_one_key_or_one_root_are_refused()
    {
        Action sameKey = () => Compose(new FakeModule("fake", "/one", "One"), new FakeModule("fake", "/two", "Two"));
        Action sameRoot = () => Compose(new FakeModule("one", "/same", "One"), new FakeModule("two", "/SAME/", "Two"));

        sameKey.Should().Throw<InvalidOperationException>().WithMessage("*under the key 'fake'*");
        sameRoot.Should().Throw<InvalidOperationException>().WithMessage("*which the pages of 'one' already use*");
    }

    [Fact]
    public void The_shells_own_section_is_always_titled_Studio()
    {
        StudioShellDescriptor withoutDatabase = StudioModuleComposition.Compose([new FakeModule("fake", "/fake", "Fake")]);
        StudioShellDescriptor withDatabase = Compose();

        withoutDatabase.Navigation.Select(static x => x.Title).Should().Equal("Fake", "Studio");
        withDatabase.Navigation.Select(static x => x.Title).Last().Should().Be("Studio");
    }

    [Fact]
    public void A_module_that_takes_the_shells_Activity_label_is_refused()
    {
        Action label = () => StudioModuleComposition.Compose([new LabelledModule("labelled", "/labelled", "Activity")]);

        label.Should().Throw<InvalidOperationException>().WithMessage("*'Activity' of module 'labelled'*unique, ignoring case*");
    }

    private sealed class LabelledModule(string key, string root, string label) : IStudioModule
    {
        public string Key => key;

        public string ProductName => "Labelled";

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [new(typeof(LabelledModule).Assembly, root)];

        public IReadOnlyList<StudioNavSection> Navigation { get; } = [new StudioNavSection("Labelled", [new StudioNavItem(label, "labelled", "grid")])];

        public Type? ScopeSelector => null;

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => key;

        public string Summary => "Labelled.";

        public string ExposedSentence => string.Empty;

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => [];

        public string? AllCapabilitiesOption => null;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    [Theory]
    [InlineData("Overview")]
    [InlineData("overview")]
    [InlineData("OVERVIEW")]
    [InlineData("System")]
    [InlineData("Studio")]
    [InlineData("STUDIO")]
    [InlineData("Activity")]
    public void A_rail_label_two_sections_share_is_refused_ignoring_case(string title)
    {
        // The database's rail has an "Overview" entry and a "System" section, and the shell's own section is "Studio" and holds "Activity". The fake is
        // added after the database, so it is the one that is refused.
        Action compose = () => Compose(new FakeModule("zeta", "/zeta", title));

        compose.Should().Throw<InvalidOperationException>().WithMessage($"*'{title}' of module 'zeta'*unique, ignoring case*");
    }

    [Fact]
    public void A_title_two_modules_share_is_refused()
    {
        Action compose = () => Compose(new FakeModule("a", "/a", "Shared"), new FakeModule("b", "/b", "shared"));

        compose.Should().Throw<InvalidOperationException>().WithMessage("*'shared' of module 'b'*");
    }
}
