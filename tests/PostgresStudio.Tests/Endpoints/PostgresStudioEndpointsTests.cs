using System.Net;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal;

namespace PostgresStudio.Tests.Endpoints;

/// <summary>
/// An API-only host with only Npgsql - one <see cref="NpgsqlDataSource" />, no <c>.razor</c> of its own, no
/// <c>UseAntiforgery()</c> - serves Postgres Studio at <c>/postgres</c>: the page, its stylesheet, the framework script
/// and the Blazor circuit's negotiate, all with 200; and its mapping serves PostgresStudio's pages and nothing else.
/// </summary>
/// <remarks>
/// The data source points at a port nothing listens on: serving the shell must not open a connection, because the
/// Overview reads only once interactive. A request that did connect would fail, and slowly, rather than pass.
/// </remarks>
public class PostgresStudioEndpointsTests
{
    private const string ConnectionString = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp(
        Action<PostgresStudioOptions>? configureStudio = null,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
        builder.Services.AddPostgresStudio(configureStudio);
        configureBuilder?.Invoke(builder);
        return builder.Build();
    }

    private static List<RouteEndpoint> RouteEndpoints(WebApplication app) =>
        [.. ((IEndpointRouteBuilder) app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()];

    /// <summary>The stylesheet and the framework script as a host's web root would hold them.</summary>
    private static void WithWebRoot(WebApplicationBuilder builder) =>
        builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
        {
            ["_framework/blazor.web.js"] = "// blazor.web.js"u8.ToArray(),
            ["_content/PostgresStudio/css/studio.css"] = ":root { }"u8.ToArray(),
            ["_content/PostgresStudio/js/studio.js"] = "// studio.js"u8.ToArray(),
            ["_content/PostgresStudio/favicon.svg"] = "<svg xmlns=\"http://www.w3.org/2000/svg\" />"u8.ToArray(),
        });

    [Fact]
    public async Task An_api_only_host_with_one_data_source_serves_the_page_its_assets_and_the_circuit()
    {
        await using WebApplication app = CreateApp(configureBuilder: WithWebRoot);
        app.MapPostgresStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage page = await client.GetAsync(new Uri("/postgres", UriKind.Relative), Token);
            page.StatusCode.Should().Be(HttpStatusCode.OK);

            string body = await page.Content.ReadAsStringAsync(Token);
            body.Should().Contain("<base href=\"/postgres/\"");
            body.Should().Contain("<title>Postgres Studio</title>");
            body.Should().Contain("<link rel=\"stylesheet\" href=\"_content/PostgresStudio/css/studio.css\"");
            body.Should().Contain("<script src=\"_framework/blazor.web.js\">");
            body.Should().Contain("<link rel=\"icon\" type=\"image/svg+xml\" href=\"_content/PostgresStudio/favicon.svg\"", "the document names its own icon (issue #33)");
            body.Should().Contain("<h1 class=\"ms-page-title\">Overview</h1>", "the root page renders PostgresStudio's overview");
            body.Should().NotContain("__RequestVerificationToken");

            using HttpResponseMessage css = await client.GetAsync(new Uri("/postgres/_content/PostgresStudio/css/studio.css", UriKind.Relative), Token);
            css.StatusCode.Should().Be(HttpStatusCode.OK);
            css.Content.Headers.ContentType!.MediaType.Should().Be("text/css");

            // The icon the document names, relative to its studio-rooted <base href>: under the mount path's mirror, so a host
            // that serves no /favicon.ico is asked for nothing.
            using HttpResponseMessage icon = await client.GetAsync(new Uri("/postgres/_content/PostgresStudio/favicon.svg", UriKind.Relative), Token);
            icon.StatusCode.Should().Be(HttpStatusCode.OK);
            icon.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
            (await icon.Content.ReadAsByteArrayAsync(Token)).Should().NotBeEmpty();

            using HttpResponseMessage script = await client.GetAsync(new Uri("/postgres/_framework/blazor.web.js", UriKind.Relative), Token);
            script.StatusCode.Should().Be(HttpStatusCode.OK);
            script.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");

            using HttpResponseMessage negotiate = await client.PostAsync(
                new Uri("/postgres/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), content: null, Token);
            negotiate.StatusCode.Should().Be(HttpStatusCode.OK);
            (await negotiate.Content.ReadAsStringAsync(Token)).Should().Contain("availableTransports");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    private const string Initializer = "Microsoft.NET.Sdk.BlazorWeb.DotNetWatch.abc123.lib.module.js";

    /// <summary>
    /// The studio-rooted <c>&lt;base href&gt;</c> sends every relative framework request under the mount path. A host built
    /// in Debug with the .NET 11 SDK lists a hot-reload initializer module among the page's Blazor initializers, and a 404 on
    /// it stops <c>Blazor.start</c>: whatever the host serves at <c>/_framework/{file}</c> is served under the mount, at the
    /// top level and nested.
    /// </summary>
    [Fact]
    public async Task Any_framework_file_the_host_serves_at_its_root_endpoint_is_served_under_the_mount()
    {
        await using WebApplication app = CreateApp();

        // As MapStaticAssets registers them: a literal route per file, with or without the leading slash.
        app.MapGet("/_framework/" + Initializer, static () => Results.Text("export function beforeWebStart() { }", "text/javascript")).AllowAnonymous();
        app.MapGet("_framework/dotnet-browser-tools/BrowserTools.js", static () => Results.Text("// tools", "text/javascript")).AllowAnonymous();
        app.MapPostgresStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage initializer = await client.GetAsync(new Uri("/postgres/_framework/" + Initializer, UriKind.Relative), Token);
            initializer.StatusCode.Should().Be(HttpStatusCode.OK);
            (await initializer.Content.ReadAsStringAsync(Token)).Should().Contain("beforeWebStart");

            using HttpResponseMessage nested = await client.GetAsync(new Uri("/postgres/_framework/dotnet-browser-tools/BrowserTools.js", UriKind.Relative), Token);
            nested.StatusCode.Should().Be(HttpStatusCode.OK);
            (await nested.Content.ReadAsStringAsync(Token)).Should().Be("// tools");

            using HttpResponseMessage head = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, new Uri("/postgres/_framework/" + Initializer, UriKind.Relative)), Token);
            head.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>The other host: <c>UseStaticFiles</c> has no endpoint per file, and the web root has the file.</summary>
    [Fact]
    public async Task A_framework_file_in_the_hosts_web_root_is_served_under_the_mount()
    {
        await using WebApplication app = CreateApp(configureBuilder: static builder =>
            builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
            {
                ["_framework/" + Initializer] = "export function beforeWebStart() { }"u8.ToArray(),
                ["_framework/nested/other.js"] = "// other"u8.ToArray(),
            }));
        app.MapPostgresStudio("/ops/db").AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage initializer = await client.GetAsync(new Uri("/ops/db/_framework/" + Initializer, UriKind.Relative), Token);
            initializer.StatusCode.Should().Be(HttpStatusCode.OK);
            initializer.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
            (await initializer.Content.ReadAsStringAsync(Token)).Should().Contain("beforeWebStart");

            using HttpResponseMessage nested = await client.GetAsync(new Uri("/ops/db/_framework/nested/other.js", UriKind.Relative), Token);
            nested.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Theory]
    [InlineData("/postgres/_framework/nothing-serves-this.js")]
    [InlineData("/postgres/_framework/..%2F..%2Fappsettings.json")]
    [InlineData("/postgres/_framework/%2e%2e/appsettings.json")]
    [InlineData("/postgres/_framework/..%5C..%5Cappsettings.json")]
    [InlineData("/postgres/_framework/nested/../../appsettings.json")]
    [InlineData("/postgres/_framework/nested")]
    public async Task A_framework_file_the_host_does_not_serve_and_any_path_that_climbs_is_404_never_500(string path)
    {
        await using WebApplication app = CreateApp(configureBuilder: static builder =>
            builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
            {
                ["_framework/nested/other.js"] = "// other"u8.ToArray(),
                ["appsettings.json"] = "{ \"secret\": true }"u8.ToArray(),
            }));
        app.MapPostgresStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync(Token)).Should().NotContain("secret");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// The literal endpoints keep their own handlers by route precedence: <c>blazor.web.js</c> is still served by the
    /// studio's three-tier handler (the web root's copy wins over what a root endpoint serves), and the enhanced-navigation
    /// <c>opaque-redirect</c> is still the framework's own endpoint, not a file lookup.
    /// </summary>
    [Fact]
    public async Task The_literal_framework_endpoints_keep_their_own_handlers_beside_the_file_forward()
    {
        await using WebApplication app = CreateApp(configureBuilder: WithWebRoot);
        app.MapGet("/_framework/blazor.web.js", static () => Results.Text("// the root endpoint's", "text/javascript")).AllowAnonymous();
        app.MapPostgresStudio().AllowAnonymous();

        List<RouteEndpoint> framework = [.. RouteEndpoints(app).Where(static x => x.RoutePattern.RawText!.StartsWith("/postgres/_framework/", StringComparison.Ordinal))];
        framework.Select(static x => x.RoutePattern.RawText).Should().BeEquivalentTo(
            ["/postgres/_framework/blazor.web.js", "/postgres/_framework/opaque-redirect", "/postgres/_framework/{**file}"]);

        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage script = await client.GetAsync(new Uri("/postgres/_framework/blazor.web.js", UriKind.Relative), Token);
            script.StatusCode.Should().Be(HttpStatusCode.OK);
            (await script.Content.ReadAsStringAsync(Token)).Should().Be("// blazor.web.js", "the host's web root wins (D22)");

            using HttpResponseMessage redirect = await client.GetAsync(new Uri("/postgres/_framework/opaque-redirect?url=x", UriKind.Relative), Token);
            redirect.StatusCode.Should().NotBe(HttpStatusCode.NotFound, "the framework's own endpoint answers, not the file forward");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>The forward is behind the studio's authorization, like the other mirrored endpoints.</summary>
    [Fact]
    public async Task The_framework_file_forward_carries_the_studios_policy_or_stays_anonymous_like_the_other_assets()
    {
        await using WebApplication governed = CreateApp(
            static options => options.AuthorizationPolicy = "ops",
            static builder => builder.Services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        governed.MapPostgresStudio();

        RouteEndpoint withPolicy = RouteEndpoints(governed).Single(static x => x.RoutePattern.RawText == "/postgres/_framework/{**file}");
        withPolicy.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().Contain(static x => x.Policy == "ops");

        await using WebApplication open = CreateApp();
        open.MapPostgresStudio();

        RouteEndpoint anonymous = RouteEndpoints(open).Single(static x => x.RoutePattern.RawText == "/postgres/_framework/{**file}");
        anonymous.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull("package-adjacent plumbing, like the stylesheet, so a fail-closed FallbackPolicy host still loads it");
    }

    [Fact]
    public async Task Every_page_of_the_shared_studio_is_mapped_under_postgres_and_no_Marten_page_is()
    {
        await using WebApplication app = CreateApp();
        app.MapPostgresStudio().AllowAnonymous();

        string[] pages =
        [
            .. RouteEndpoints(app)
                .Where(static x => x.Metadata.GetMetadata<StudioEndpointMarker>() is { IsPage: true })
                .Select(static x => x.RoutePattern.RawText!)
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

        pages.Should().Contain(["/postgres", "/postgres/query", "/postgres/database", "/postgres/database/object",
            "/postgres/database/row", "/postgres/database/sessions", "/postgres/relationships", "/postgres/health",
            "/postgres/activity"]);
        pages.Should().OnlyContain(static x => x == "/postgres" || x.StartsWith("/postgres/", StringComparison.Ordinal));
        pages.Should().NotContain(static x => x.Contains("documents", StringComparison.Ordinal)
            || x.Contains("events", StringComparison.Ordinal)
            || x.Contains("projections", StringComparison.Ordinal)
            || x.Contains("schema", StringComparison.Ordinal)
            || x.Contains("config", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pattern_at_the_map_site_moves_the_studio()
    {
        await using WebApplication app = CreateApp();
        app.MapPostgresStudio("/ops/db").AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage page = await client.GetAsync(new Uri("/ops/db", UriKind.Relative), Token);
            page.StatusCode.Should().Be(HttpStatusCode.OK);
            (await page.Content.ReadAsStringAsync(Token)).Should().Contain("<base href=\"/ops/db/\"");

            using HttpResponseMessage old = await client.GetAsync(new Uri("/postgres", UriKind.Relative), Token);
            old.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("postgres")]
    [InlineData("/a/{id}")]
    [InlineData("/a//b")]
    public async Task An_invalid_pattern_at_the_map_site_is_rejected(string pattern)
    {
        await using WebApplication app = CreateApp();

        Action map = () => app.MapPostgresStudio(pattern);

        map.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task A_configured_policy_covers_the_pages_and_the_circuit()
    {
        await using WebApplication app = CreateApp(
            static options => options.AuthorizationPolicy = "ops",
            static builder => builder.Services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        app.MapPostgresStudio();

        List<RouteEndpoint> studio = [.. RouteEndpoints(app).Where(static x => x.Metadata.GetMetadata<StudioEndpointMarker>() is not null)];

        studio.Should().NotBeEmpty();
        studio.Should().OnlyContain(static x => x.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(static y => y.Policy == "ops"));
    }

    /// <summary>
    /// Registering the studio and never mapping it changes nothing about how the host starts, as MartenStudio's twin
    /// (<c>BrowseDatabaseOptionsTests</c>) holds: no options are built (the configure callback never runs), the
    /// application's data source is never resolved, and nothing is logged at Warning or above - with <c>"*"</c> and no
    /// <c>SqlConsoleRole</c> configured, which a host that did map the studio would be warned about (event 9230).
    /// </summary>
    [Fact]
    public async Task Registering_without_mapping_changes_nothing_and_starts()
    {
        CapturingLoggerProvider logs = new();
        int configured = 0;
        int resolved = 0;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(_ =>
        {
            Interlocked.Increment(ref resolved);
            return NpgsqlDataSource.Create(ConnectionString);
        });
        builder.Services.AddPostgresStudio(options =>
        {
            Interlocked.Increment(ref configured);
            options.BrowsableSchemas.Add("*");
        });

        await using WebApplication app = builder.Build();

        await app.StartAsync(Token);
        await app.StopAsync(Token);

        RouteEndpoints(app).Should().BeEmpty("nothing is mapped until the host maps it");
        configured.Should().Be(0, "nothing built PostgresStudioOptions for a host that never mapped the studio");
        resolved.Should().Be(0, "nothing resolved the application's NpgsqlDataSource for a studio nobody can open");
        logs.Entries.Should().NotBeEmpty("the capture is the host's own logger, and the host logged its start through it");
        logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Warning);
    }
}
