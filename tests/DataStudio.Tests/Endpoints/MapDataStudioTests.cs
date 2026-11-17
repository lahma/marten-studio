using DataStudio.Internal;
using DataStudio.Services;
using DataStudio.Services.Settings;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DataStudio.Tests.Endpoints;

/// <summary>
/// <c>MapDataStudio()</c> and the shell's startup guard (v2 step 4), on real endpoint metadata with no server: the mount, the pattern that wins over it,
/// the refusals, the guard's words with one module and with two, and a host whose only module is Kafka (v2 step 5) - which has the shell's front page at
/// the mount, whose page sends the visitor to the module, and holds Activity on a rail of the shell's own.
/// </summary>
public class MapDataStudioTests
{
    private const string Cluster = "127.0.0.1:1";

    /// <summary>A module with no page and no rail, whose one sentence says what it would expose: the second module of a guard's message.</summary>
    private sealed class OtherModule : IStudioModule
    {
        public string Key => "other";

        public string ProductName => "Other";

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [];

        public IReadOnlyList<StudioNavSection> Navigation => [];

        public Type? ScopeSelector => null;

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => "other";

        public string Summary => "Other things.";

        public string ExposedSentence => "Its pages show the other thing.";

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => [];

        public string? AllCapabilitiesOption => null;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    private static WebApplication CreateApp(
        Action<DataStudioOptions>? configureShell = null,
        Action<IServiceCollection>? configureServices = null,
        bool kafka = true)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        DataStudioBuilder studio = builder.Services.AddDataStudio(configureShell);

        if (kafka)
        {
            studio.AddKafka(static options => options.AddCluster("primary", new Confluent.Kafka.ClientConfig { BootstrapServers = Cluster }));
        }

        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }

    private static List<RouteEndpoint> RouteEndpoints(WebApplication app) =>
        [.. ((IEndpointRouteBuilder) app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()];

    private static IEnumerable<string?> Patterns(WebApplication app) => RouteEndpoints(app).Select(static x => x.RoutePattern.RawText);

    /// <summary>The guard's own check, as the host runs it before it listens: the exception it would stop the start with, or none.</summary>
    private static async Task<Exception?> StartingAsync(WebApplication app)
    {
        StudioEndpointAuthorizationGuard guard = app.Services.GetServices<IHostedService>().OfType<StudioEndpointAuthorizationGuard>().Single();

        try
        {
            await guard.StartingAsync(TestContext.Current.CancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    // -------------------------------------------------------------------------------------------
    // The mount, and the refusals
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_shell_is_mounted_at_the_default_path_and_serves_its_assets_and_circuit_there()
    {
        await using WebApplication app = CreateApp();
        app.MapDataStudio().AllowAnonymous();

        IEnumerable<string?> patterns = Patterns(app);

        patterns.Should().Contain("/studio").And.Contain("/studio/kafka").And.Contain("/studio/activity");
        patterns.Should().Contain("/studio/_blazor/negotiate").And.Contain("/studio/_framework/blazor.web.js").And.Contain("/studio/_content/DataStudio/{**path}");
        patterns.Where(static x => x is not null && x.StartsWith("/postgres", StringComparison.Ordinal)).Should().BeEmpty("no page is served at its compile-time root: every address is below the mount");
    }

    [Fact]
    public async Task The_configured_path_is_where_it_is_mounted()
    {
        await using WebApplication app = CreateApp(static options => options.Path = "/ops/data");
        app.MapDataStudio().AllowAnonymous();

        Patterns(app).Should().Contain("/ops/data/kafka").And.Contain("/ops/data/activity").And.NotContain("/studio/kafka");
    }

    [Fact]
    public async Task The_pattern_wins_over_the_configured_path_and_is_written_where_the_components_read_it()
    {
        await using WebApplication app = CreateApp(static options => options.Path = "/configured");
        app.MapDataStudio("/admin").AllowAnonymous();

        Patterns(app).Should().Contain("/admin/kafka").And.NotContain("/configured/kafka");
        app.Services.GetRequiredService<IShellSettings>().TrimmedPath.Should().Be("/admin");
    }

    [Theory]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("/a/{id}")]
    [InlineData("/a/../b")]
    [InlineData("/a//b")]
    public async Task A_pattern_that_is_not_a_plain_rooted_path_is_refused(string pattern)
    {
        await using WebApplication app = CreateApp();

        Action map = () => app.MapDataStudio(pattern);

        map.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Mapping_without_AddDataStudio_is_refused_in_one_sentence_naming_it()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        await using WebApplication app = builder.Build();

        Action map = () => app.MapDataStudio();

        map.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("AddDataStudio()");
    }

    [Fact]
    public async Task Mapping_a_shell_with_no_module_is_refused_in_one_sentence_naming_the_calls_that_add_one()
    {
        await using WebApplication app = CreateApp(kafka: false);

        Action map = () => app.MapDataStudio();
        Action mapAt = () => app.MapDataStudio("/admin");

        foreach (Action call in new[] { map, mapAt })
        {
            string message = call.Should().Throw<InvalidOperationException>().Which.Message;
            message.Should().Contain("no module").And.Contain("AddPostgres()").And.Contain("AddMarten()");
            message.Should().EndWith(".").And.NotContain(". ", "one sentence");
        }
    }

    // -------------------------------------------------------------------------------------------
    // The startup guard's words
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_mapping_that_answers_nothing_refuses_to_start_naming_the_shell_the_module_and_the_three_remedies()
    {
        await using WebApplication app = CreateApp();
        app.MapDataStudio();
        string kafkaSentence = app.Services.GetRequiredService<StudioShellDescriptor>().Modules.Single().ExposedSentence;

        Exception? failure = await StartingAsync(app);

        failure.Should().BeOfType<InvalidOperationException>();
        failure!.Message.Should().StartWith("Data Studio is mapped with no authorization. " + kafkaSentence + " Data Studio refuses to start rather than serve that by accident. Say which you meant:");
        failure.Message.Should().Contain("app.MapDataStudio().RequireAuthorization() authorizes its pages and its Blazor circuit;");
        failure.Message.Should().Contain("services.AddDataStudio(options => options.AuthorizationPolicy = \"...\") authorizes those and the static assets with them;");
        failure.Message.Should().Contain("app.MapDataStudio().AllowAnonymous() serves it to anyone, deliberately.");
        failure.Message.Should().Contain("FallbackPolicy").And.Contain("Unauthorized: /studio, /studio/_blazor");
    }

    [Fact]
    public async Task With_two_modules_the_guard_joins_every_modules_sentence_in_module_order()
    {
        await using WebApplication app = CreateApp(configureServices: static services => services.AddSingleton<IStudioModule, OtherModule>());
        app.MapDataStudio();
        IReadOnlyList<IStudioModule> modules = app.Services.GetRequiredService<StudioShellDescriptor>().Modules;
        modules.Select(static x => x.Key).Should().Equal("kafka", "other");

        Exception? failure = await StartingAsync(app);

        failure!.Message.Should().StartWith(
            "Data Studio is mapped with no authorization. " + modules[0].ExposedSentence + " Its pages show the other thing. Data Studio refuses to start rather than serve that by accident.");
    }

    [Fact]
    public async Task Each_of_the_ways_of_answering_satisfies_the_guard()
    {
        await using WebApplication anonymous = CreateApp();
        anonymous.MapDataStudio().AllowAnonymous();
        (await StartingAsync(anonymous)).Should().BeNull();

        await using WebApplication required = CreateApp(configureServices: static services => services.AddAuthorization(static options =>
            options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        required.MapDataStudio().RequireAuthorization("ops");
        (await StartingAsync(required)).Should().BeNull();

        await using WebApplication option = CreateApp(
            static shell => shell.AuthorizationPolicy = "ops",
            static services => services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        option.MapDataStudio();
        (await StartingAsync(option)).Should().BeNull();
    }

    // -------------------------------------------------------------------------------------------
    // A host with only the Kafka module (decision 8)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_kafka_only_host_has_the_one_module_and_the_shells_front_page_at_the_mount()
    {
        await using WebApplication app = CreateApp();
        app.MapDataStudio().AllowAnonymous();
        StudioShellDescriptor descriptor = app.Services.GetRequiredService<StudioShellDescriptor>();

        descriptor.Modules.Should().ContainSingle().Which.Key.Should().Be("kafka");
        descriptor.PageAssemblies.Select(static x => x.Assembly).Should().Contain(typeof(StudioShellDescriptor).Assembly, "the shell's own pages are there whatever the modules are");
        RouteEndpoints(app).Where(static x => x.RoutePattern.RawText == "/studio").Should().ContainSingle()
            .Which.Metadata.OfType<Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata>().Should().ContainSingle()
            .Which.Type.FullName.Should().Be("DataStudio.Components.Pages.Front", "the one endpoint at the mount is a page of the shell, which sends the visitor to the one module");
        descriptor.Routes.Match("")!.PageType.FullName.Should().Be("DataStudio.Components.Pages.Front");
    }

    [Fact]
    public async Task The_front_page_is_governed_like_every_page()
    {
        await using WebApplication governed = CreateApp(
            static shell => shell.AuthorizationPolicy = "ops",
            static services => services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        governed.MapDataStudio();
        await using WebApplication silent = CreateApp();
        silent.MapDataStudio();

        RouteEndpoint withPolicy = RouteEndpoints(governed).Single(static x => x.RoutePattern.RawText == "/studio");
        RouteEndpoint withNothing = RouteEndpoints(silent).Single(static x => x.RoutePattern.RawText == "/studio");

        withPolicy.Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("ops");
        withNothing.Metadata.GetMetadata<StudioEndpointMarker>().Should().NotBeNull("the guard counts it as a page");
        withNothing.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
        withNothing.Metadata.GetMetadata<IAuthorizeData>().Should().BeNull();
    }

    [Fact]
    public async Task A_database_less_host_gets_a_Studio_section_holding_Activity_and_the_page_at_the_mount_activity()
    {
        await using WebApplication app = CreateApp();
        app.MapDataStudio().AllowAnonymous();
        StudioShellDescriptor descriptor = app.Services.GetRequiredService<StudioShellDescriptor>();

        descriptor.Navigation.Select(static x => x.Title).Should().Equal("Kafka", "Studio");
        descriptor.Navigation[^1].Items.Select(static x => (x.Label, x.Route)).Should().Equal(("Activity", "activity"));
        descriptor.Navigation[^1].ModuleKey.Should().BeNull("the section is the shell's, not a module's");
        descriptor.Routes.Match("activity")!.PageType.FullName.Should().Be("DataStudio.Components.Pages.Activity");
        RouteEndpoints(app).Select(static x => x.RoutePattern.RawText).Should().Contain("/studio/activity");
    }

    [Fact]
    public async Task A_module_with_an_Activity_entry_of_its_own_leaves_the_shells_section_in_place()
    {
        await using WebApplication app = CreateApp(configureServices: static services => services.AddSingleton<IStudioModule, ActivityModule>());
        StudioShellDescriptor descriptor = app.Services.GetRequiredService<StudioShellDescriptor>();

        descriptor.Navigation.Select(static x => x.Title).Should().Equal("Kafka", "Mine", "Studio");
    }

    [Theory]
    [InlineData("Studio", "Something")]
    [InlineData("Studio", "Activity")]
    [InlineData("Mine", "Activity")]
    [InlineData("studio", "Something")]
    public async Task A_module_may_not_take_the_shells_section_title_or_its_Activity_label(string title, string label)
    {
        await using WebApplication app = CreateApp(configureServices: services => services.AddSingleton<IStudioModule>(new ActivityModule(
            new StudioNavSection(title, [new StudioNavItem(label, "mine/page", "list")]))));

        Func<StudioShellDescriptor> compose = () => app.Services.GetRequiredService<StudioShellDescriptor>();

        compose.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Studio").And.Contain("Activity").And.Contain("is already a section title or a label");
    }

    [Fact]
    public async Task The_shells_section_is_titled_Studio_whatever_the_modules_are()
    {
        await using WebApplication kafkaOnly = CreateApp();
        await using WebApplication beside = CreateApp(configureServices: static services => services.AddSingleton<IStudioModule, ActivityModule>());

        kafkaOnly.Services.GetRequiredService<StudioShellDescriptor>().Navigation[^1].Title.Should().Be("Studio");
        beside.Services.GetRequiredService<StudioShellDescriptor>().Navigation[^1].Title.Should().Be("Studio");
    }

    [Fact]
    public async Task Modules_compose_in_the_order_the_host_added_them_and_the_order_decides_nothing_else()
    {
        await using WebApplication kafkaFirst = CreateApp(configureServices: static services => services.AddSingleton<IStudioModule, OtherModule>());

        ServiceCollection reversed = new();
        reversed.AddLogging();
        reversed.AddSingleton<IStudioModule, OtherModule>();
        reversed.AddDataStudio().AddKafka(static options => options.AddCluster("primary", new Confluent.Kafka.ClientConfig { BootstrapServers = Cluster }));
        using ServiceProvider otherFirst = reversed.BuildServiceProvider();

        kafkaFirst.Services.GetRequiredService<StudioShellDescriptor>().Modules.Select(static x => x.Key).Should().Equal("kafka", "other");
        otherFirst.GetRequiredService<StudioShellDescriptor>().Modules.Select(static x => x.Key).Should().Equal("other", "kafka");

        // A module with no rail adds no section, and the shell's own is last.
        otherFirst.GetRequiredService<StudioShellDescriptor>().Navigation.Select(static x => x.Title).Should().Equal("Kafka", "Studio");
    }

    [Fact]
    public void A_refusal_does_not_depend_on_the_order_the_modules_were_added_in()
    {
        ServiceCollection forward = new();
        forward.AddLogging();
        forward.AddDataStudio().AddKafka();
        forward.AddSingleton<IStudioModule>(new ActivityModule(new StudioNavSection("Studio", [])));
        ServiceCollection backward = new();
        backward.AddLogging();
        backward.AddSingleton<IStudioModule>(new ActivityModule(new StudioNavSection("Studio", [])));
        backward.AddDataStudio().AddKafka();
        using ServiceProvider first = forward.BuildServiceProvider();
        using ServiceProvider second = backward.BuildServiceProvider();

        Action composeForward = () => first.GetRequiredService<StudioShellDescriptor>();
        Action composeBackward = () => second.GetRequiredService<StudioShellDescriptor>();

        composeForward.Should().Throw<InvalidOperationException>();
        composeBackward.Should().Throw<InvalidOperationException>();
    }

    private sealed class ActivityModule(StudioNavSection? section) : IStudioModule
    {
        public ActivityModule()
            : this(null)
        {
        }

        public string Key => "mine";

        public string ProductName => "Mine";

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [];

        public IReadOnlyList<StudioNavSection> Navigation => [section ?? new StudioNavSection("Mine", [new StudioNavItem("Activity of mine", "mine/activity", "list")])];

        public Type? ScopeSelector => null;

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => "mine";

        public string Summary => "Mine.";

        public string ExposedSentence => "Mine.";

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups => [];

        public string? AllCapabilitiesOption => null;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    /// <summary>
    /// The whole point of the Kafka-only host: its output folder, the files this host runs from, holds no Npgsql and no DataStudio.Postgres*.
    /// It can only be true of a project that references neither, which is why the test lives in the shell's suite.
    /// </summary>
    [Fact]
    public void A_kafka_only_hosts_output_folder_holds_no_Npgsql_and_no_DataStudio_Postgres_assembly()
    {
        string[] files = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(static x => Path.GetFileName(x))];

        files.Should().Contain("DataStudio.dll").And.Contain("DataStudio.Kafka.dll").And.Contain("DataStudio.Kafka.Core.dll");
        files.Should().NotContain(static x => x.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase));
        files.Should().NotContain(static x => x.StartsWith("DataStudio.Postgres", StringComparison.OrdinalIgnoreCase));
        files.Should().NotContain(static x => x.StartsWith("DataStudio.Marten", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void And_the_assemblies_it_loaded_to_build_the_host_are_not_those()
    {
        using ServiceProvider provider = new ServiceCollection().AddLogging().AddDataStudio().AddKafka().Services.BuildServiceProvider();
        _ = provider.GetRequiredService<StudioShellDescriptor>();

        AppDomain.CurrentDomain.GetAssemblies().Select(static x => x.GetName().Name!)
            .Should().NotContain(static x => x.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase) || x.StartsWith("DataStudio.Postgres", StringComparison.OrdinalIgnoreCase));
    }
}
