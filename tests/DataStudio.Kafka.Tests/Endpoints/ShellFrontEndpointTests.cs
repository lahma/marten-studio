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

    private static WebApplication CreateApp(bool postgres, bool kafka = true, Action<DataStudioOptions>? configureShell = null, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
        {
            ["_framework/blazor.web.js"] = "// blazor.web.js"u8.ToArray(),
            ["_content/DataStudio/css/studio.css"] = ":root { }"u8.ToArray(),
        });

        DataStudioBuilder studio = builder.Services.AddDataStudio(configureShell);

        if (postgres)
        {
            builder.Services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
            studio.AddPostgres();
        }

        if (kafka)
        {
            studio.AddKafka(static options => options.AddCluster("primary", new ClientConfig { BootstrapServers = "127.0.0.1:1" }, static cluster => cluster.DisplayName("Primary")));
        }

        configureBuilder?.Invoke(builder);
        return builder.Build();
    }

    private static List<RouteEndpoint> RouteEndpoints(WebApplication app) =>
        [.. ((IEndpointRouteBuilder) app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()];

    [Fact]
    public async Task With_one_module_the_front_address_sends_the_visitor_to_its_home_keeping_the_query()
    {
        await using WebApplication app = CreateApp(postgres: false);
        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage plain = await client.GetAsync(new Uri("/studio", UriKind.Relative), Token);
            using HttpResponseMessage withQuery = await client.GetAsync(new Uri("/studio?cluster=primary", UriKind.Relative), Token);

            plain.StatusCode.Should().Be(HttpStatusCode.Found, "a redirect that can change, never a permanent one");
            plain.Headers.Location!.ToString().Should().EndWith("/studio/kafka");
            withQuery.StatusCode.Should().Be(HttpStatusCode.Found);
            withQuery.Headers.Location!.ToString().Should().EndWith("/studio/kafka?cluster=primary");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task The_front_address_follows_the_mount_and_the_path_base()
    {
        await using WebApplication app = CreateApp(postgres: false, configureShell: static options => options.Path = "/ops/data");
        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri("/ops/data?cluster=primary", UriKind.Relative), Token);

            response.StatusCode.Should().Be(HttpStatusCode.Found);
            response.Headers.Location!.ToString().Should().EndWith("/ops/data/kafka?cluster=primary");
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
            body.Should().Contain("href=\"kafka\"").And.Contain("href=\"postgres\"");
            body.IndexOf("href=\"kafka\"", StringComparison.Ordinal).Should().BeLessThan(body.IndexOf("href=\"postgres\"", StringComparison.Ordinal), "module order is key order");
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

    [Fact]
    public async Task An_anonymous_visitor_is_refused_the_front_page_whichever_modules_there_are()
    {
        await using WebApplication app = CreateApp(postgres: true, configureBuilder: static builder =>
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
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task An_unauthorized_mapping_is_refused_at_startup_with_the_front_page_among_the_pages()
    {
        await using WebApplication app = CreateApp(postgres: true);
        app.MapDataStudio();

        Func<Task> start = () => app.StartAsync(Token);

        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith("Data Studio is mapped with no authorization.").And.Contain("/studio");
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
