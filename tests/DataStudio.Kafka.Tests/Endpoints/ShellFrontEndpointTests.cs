using System.Net;

using Confluent.Kafka;

using DataStudio.Internal;
using DataStudio.Marten.Tests.Support;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace DataStudio.Kafka.Tests.Endpoints;

/// <summary>
/// The shell's front page, at the mount (v2 step 5), on a real endpoint pipeline: with one module composed it sends the visitor to that module's home,
/// keeping the query string; with two or more it draws one tile per module (a name, one sentence and a link to the home, no number); it is served under
/// the shell's own policy like every other page and the startup guard counts it; and a 1.x address (<c>{mount}/health</c>, <c>{mount}/documents</c>) is a 404.
/// </summary>
public class ShellFrontEndpointTests
{
    private const string ConnectionString = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void AddKafka(DataStudioBuilder studio) =>
        studio.AddKafka(static options => options.AddCluster("primary", new ClientConfig { BootstrapServers = "127.0.0.1:1" }, static cluster => cluster.DisplayName("Primary")));

    private static WebApplication CreateApp(
        bool postgres,
        bool kafka = true,
        bool kafkaFirst = false,
        Action<DataStudioOptions>? configureShell = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        string? pathBase = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
        {
            ["_framework/blazor.web.js"] = "// blazor.web.js"u8.ToArray(),
            ["_content/DataStudio/css/studio.css"] = ":root { }"u8.ToArray(),
        });

        DataStudioBuilder studio = builder.Services.AddDataStudio(configureShell);

        // The order the host calls them in is the order of the rail, the chip and the front tiles.
        if (kafka && kafkaFirst)
        {
            AddKafka(studio);
        }

        if (postgres)
        {
            builder.Services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
            studio.AddPostgres();
        }

        if (kafka && !kafkaFirst)
        {
            AddKafka(studio);
        }

        configureBuilder?.Invoke(builder);
        WebApplication app = builder.Build();

        if (pathBase is not null)
        {
            // The path base is stripped before routing, as a host that mounts the application under a prefix does; so routing is explicit.
            app.UsePathBase(pathBase);
            app.UseRouting();
        }

        return app;
    }

    private static List<RouteEndpoint> RouteEndpoints(WebApplication app) =>
        [.. ((IEndpointRouteBuilder) app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()];

    /// <summary>
    /// The one-module redirect is a path, whole: the path base, the mount, the module's home and the request's own query string - never an absolute URL
    /// built from the request's scheme and host, which a host behind a proxy whose forwarded headers the application does not apply would get wrong.
    /// </summary>
    [Theory]
    [InlineData("/studio", null, "/studio", "/studio/kafka")]
    [InlineData("/studio", null, "/studio?cluster=primary", "/studio/kafka?cluster=primary")]
    [InlineData("/studio", null, "/studio?cluster=primary&topic=a%2Fb", "/studio/kafka?cluster=primary&topic=a%2Fb")]
    [InlineData("/ops/data", null, "/ops/data", "/ops/data/kafka")]
    [InlineData("/ops/data", null, "/ops/data?cluster=primary", "/ops/data/kafka?cluster=primary")]
    [InlineData("/studio", "/app", "/app/studio", "/app/studio/kafka")]
    [InlineData("/studio", "/app", "/app/studio?cluster=primary", "/app/studio/kafka?cluster=primary")]
    [InlineData("/ops/data", "/app", "/app/ops/data?cluster=primary", "/app/ops/data/kafka?cluster=primary")]
    public async Task With_one_module_the_front_address_sends_the_visitor_to_its_home_as_a_path_keeping_the_query(string mount, string? pathBase, string request, string location)
    {
        await using WebApplication app = CreateApp(postgres: false, configureShell: options => options.Path = mount, pathBase: pathBase);
        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(request, UriKind.Relative), Token);

            response.StatusCode.Should().Be(HttpStatusCode.Found, "a redirect that can change, never a permanent one");
            response.Headers.Location!.OriginalString.Should().Be(location);
            response.Headers.Location.IsAbsoluteUri.Should().BeFalse("a path holds no scheme and no host: the proxy in front decides those");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task The_redirect_names_no_scheme_and_no_host_whatever_the_request_arrived_as()
    {
        await using WebApplication app = CreateApp(postgres: false);
        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri("/studio?cluster=primary", UriKind.Relative));
            request.Headers.Host = "internal-host.example:8443";
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "public.example");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

            using HttpResponseMessage response = await client.SendAsync(request, Token);

            response.StatusCode.Should().Be(HttpStatusCode.Found);
            response.Headers.Location!.OriginalString.Should().Be("/studio/kafka?cluster=primary");
            response.Headers.Location.OriginalString.Should().NotContain("example").And.NotContain("://");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task With_two_modules_the_front_page_has_one_tile_per_registered_module_in_module_order_and_names_no_other()
    {
        await using WebApplication app = CreateApp(postgres: true);
        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri("/studio", UriKind.Relative), Token);
            string body = await response.Content.ReadAsStringAsync(Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            body.Should().Contain("ms-front-tiles");
            body.Split("ms-front-tile-name").Length.Should().Be(3, "two modules, two tiles");
            TileOrder(body).Should().Equal(["postgres", "kafka"], "the tiles follow the order the host registered the modules in");
            body.Should().Contain("Topics, record search and live tail of the application&#x27;s clusters.");
            body.Should().Contain("Health, queries, tables and sessions of the application&#x27;s data sources.");
            body.Should().NotContain("Marten", "the host registered no Marten module");
            body.Should().NotContain("Documents, event streams");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task The_front_page_tiles_follow_registration_order_whichever_module_the_host_added_first()
    {
        await using WebApplication kafkaFirst = CreateApp(postgres: true, kafkaFirst: true);
        kafkaFirst.MapDataStudio().AllowAnonymous();
        await kafkaFirst.StartAsync(Token);
        try
        {
            using HttpClient client = kafkaFirst.GetTestClient();
            string body = await client.GetStringAsync(new Uri("/studio", UriKind.Relative), Token);

            TileOrder(body).Should().Equal(["kafka", "postgres"], "the host called AddKafka first");
        }
        finally
        {
            await kafkaFirst.StopAsync(Token);
        }
    }

    /// <summary>
    /// The modules' tiles, in the order the front page lists them: each tile's link, read from the tile list alone - the rail is drawn before the
    /// body and carries links of its own to the same segments, so a search of the whole document would read the rail's order.
    /// </summary>
    private static readonly string[] Segments = ["kafka", "postgres"];

    private static string[] TileOrder(string body)
    {
        int list = body.IndexOf("ms-front-tiles", StringComparison.Ordinal);
        list.Should().BeGreaterThan(-1, "the front page draws its tile list");

        string tiles = body[list..];

        return
        [
            .. Segments
                .Select(segment => (segment, at: tiles.IndexOf("href=\"" + segment + "\"", StringComparison.Ordinal)))
                .Where(static x => x.at >= 0)
                .OrderBy(static x => x.at)
                .Select(static x => x.segment),
        ];
    }

    [Fact]
    public async Task The_front_page_is_served_under_the_shells_policy_and_the_startup_guard_counts_it()
    {
        await using WebApplication app = CreateApp(
            postgres: true,
            configureShell: static options => options.AuthorizationPolicy = "ops",
            configureBuilder: static builder => builder.Services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        app.MapDataStudio();

        RouteEndpoint front = RouteEndpoints(app).Single(static x => x.RoutePattern.RawText == "/studio");

        front.Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("ops");
        front.Metadata.GetMetadata<StudioEndpointMarker>().Should().NotBeNull().And.Match<StudioEndpointMarker>(static x => x.IsPage, "the guard counts it as a page");
    }

    /// <summary>One module composed is the case that redirects: the authorization middleware answers before the endpoint's delegate runs, so no redirect names a module for a visitor the policy refuses.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_anonymous_visitor_is_refused_the_front_page_whichever_modules_there_are(bool postgres)
    {
        await using WebApplication app = CreateApp(postgres: postgres, configureBuilder: static builder =>
        {
            builder.Services
                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
            builder.Services.AddAuthorization();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDataStudio().RequireAuthorization();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri("/studio", UriKind.Relative), Token);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Headers.Location.Should().BeNull("a refused visitor is told nothing about where the module's home is");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task An_unauthorized_mapping_is_refused_at_startup_with_the_front_page_among_the_pages()
    {
        await using WebApplication app = CreateApp(postgres: true, kafka: false, configureShell: static options => options.Path = "/only-front");
        app.MapDataStudio();

        // With the front page and the pages of one module there is no way to tell them apart by a path that is under the mount: the front page's is the
        // mount itself, so the sentence lists it as such. The marker test above is the one that holds that the guard counts it.
        Func<Task> start = () => app.StartAsync(Token);

        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith("Data Studio is mapped with no authorization.")
            .And.Contain("Unauthorized: /only-front,");
    }

    [Theory]
    [InlineData("/studio/health")]
    [InlineData("/studio/query")]
    [InlineData("/studio/database")]
    [InlineData("/studio/documents")]
    [InlineData("/studio/events/streams")]
    public async Task A_1x_address_is_a_404_not_a_wrong_page(string address)
    {
        await using WebApplication app = CreateApp(postgres: true);
        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(address, UriKind.Relative), Token);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Theory]
    [InlineData("/studio/postgres")]
    [InlineData("/studio/postgres/health")]
    [InlineData("/studio/kafka")]
    [InlineData("/studio/activity")]
    public async Task A_2_0_address_is_a_page(string address)
    {
        await using WebApplication app = CreateApp(postgres: true);
        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(address, UriKind.Relative), Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }
}
