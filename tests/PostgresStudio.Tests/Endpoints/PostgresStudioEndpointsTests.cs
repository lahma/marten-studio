using System.Net;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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
            body.Should().Contain("<h1 class=\"ms-page-title\">Overview</h1>", "the root page renders PostgresStudio's overview");
            body.Should().NotContain("__RequestVerificationToken");

            using HttpResponseMessage css = await client.GetAsync(new Uri("/postgres/_content/PostgresStudio/css/studio.css", UriKind.Relative), Token);
            css.StatusCode.Should().Be(HttpStatusCode.OK);
            css.Content.Headers.ContentType!.MediaType.Should().Be("text/css");

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

    [Fact]
    public async Task Registering_without_mapping_changes_nothing_and_starts()
    {
        await using WebApplication app = CreateApp();

        await app.StartAsync(Token);
        await app.StopAsync(Token);

        RouteEndpoints(app).Should().BeEmpty("nothing is mapped until the host maps it");
    }
}
