using System.Net;

using Confluent.Kafka;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio;
using PostgresStudio.Internal;

namespace KStudio.Tests.Endpoints;

/// <summary>
/// D34 and D35 on a real endpoint pipeline: a Postgres Studio host that also called <c>AddKafkaStudio()</c> serves the Kafka pages below the studio's
/// own mount, behind the studio's own policy, counted by its startup guard, with no data source of its own needed, and a host that did not serves none.
/// </summary>
public class KafkaEndpointTests
{
    /// <summary>A data source and a broker nothing listens on: serving a page must not open a connection to either.</summary>
    private const string ConnectionString = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp(
        bool kafka = true,
        bool dataSource = true,
        Action<PostgresStudioOptions>? configureStudio = null,
        Action<KafkaStudioOptions>? configureKafka = null,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
        {
            ["_framework/blazor.web.js"] = "// blazor.web.js"u8.ToArray(),
            ["_content/PostgresStudio/css/studio.css"] = ":root { }"u8.ToArray(),
        });

        if (dataSource)
        {
            builder.Services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
        }

        builder.Services.AddPostgresStudio(configureStudio);

        if (kafka)
        {
            builder.Services.AddKafkaStudio(options =>
            {
                options.AddCluster("primary", new ClientConfig { BootstrapServers = "127.0.0.1:1" }, static cluster => cluster.DisplayName("Primary"));
                configureKafka?.Invoke(options);
            });
        }

        configureBuilder?.Invoke(builder);
        return builder.Build();
    }

    private static List<RouteEndpoint> RouteEndpoints(WebApplication app) =>
        [.. ((IEndpointRouteBuilder) app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>()];

    private static bool IsPageMarker(RouteEndpoint endpoint) => endpoint.Metadata.GetMetadata<StudioEndpointMarker>() is { IsPage: true };

    private static bool IsKafkaPage(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata>()?.Type.Assembly == typeof(KafkaStudioOptions).Assembly;

    [Theory]
    [InlineData("/postgres", "/postgres/kafka")]
    [InlineData("/ops", "/ops/kafka")]
    public async Task Kafka_pages_are_served_below_the_mount(string mount, string expected)
    {
        await using WebApplication app = CreateApp();
        if (mount == "/postgres")
        {
            app.MapPostgresStudio().AllowAnonymous();
        }
        else
        {
            app.MapPostgresStudio(mount).AllowAnonymous();
        }

        RouteEndpoints(app).Where(IsKafkaPage).Select(static x => x.RoutePattern.RawText).Should().Equal([expected], "the stub page, and only it");

        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();
            using HttpResponseMessage page = await client.GetAsync(new Uri(expected, UriKind.Relative), Token);

            page.StatusCode.Should().Be(HttpStatusCode.OK);
            string body = await page.Content.ReadAsStringAsync(Token);
            body.Should().Contain("ms-k-trademark", "the prerendered frame draws the trademark line");
            body.Should().Contain("Apache Kafka is a trademark of the Apache Software Foundation");
            body.Should().Contain("KStudio for Apache Kafka&#xAE;", "the rail names the module");
            body.Should().NotContain("Kafka clusters</h1>", "the body starts only once the circuit is interactive: a prerender reads nothing");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// D-SHELL 2.1: at a root mount a Kafka page is served at <c>/kafka</c>. <c>Path = "/"</c> normalizes to the default mount through the public
    /// options (<c>StudioPath.Of</c> reads a path of slashes only as unset), so no option spells a root mount today; the test reaches the mapper's
    /// own answer (<c>TrimmedPath</c> empty) as Postgres Studio's root-mount test does, by seeding the options' derived-path cache.
    /// </summary>
    [Fact]
    public async Task A_kafka_page_is_served_at_a_root_mount()
    {
        await using WebApplication app = CreateApp(configureStudio: static options =>
        {
            options.Path = "/";
            typeof(PostgresStudioOptions)
                .GetField("pathCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(options, new PostgresStudio.Services.Settings.StudioPath("/", string.Empty, string.Empty, HasCustom: true));
        });
        app.MapPostgresStudio().AllowAnonymous();

        RouteEndpoints(app).Where(IsKafkaPage).Select(static x => x.RoutePattern.RawText).Should().Equal(["/kafka"], "the stub page, at the root of the host");

        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();
            using HttpResponseMessage page = await client.GetAsync(new Uri("/kafka", UriKind.Relative), Token);

            page.StatusCode.Should().Be(HttpStatusCode.OK);
            (await page.Content.ReadAsStringAsync(Token)).Should().Contain("ms-k-trademark");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task A_host_without_AddKafkaStudio_serves_no_kafka_route()
    {
        await using WebApplication app = CreateApp(kafka: false);
        app.MapPostgresStudio().AllowAnonymous();

        RouteEndpoints(app).Where(IsKafkaPage).Should().BeEmpty();

        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();
            using HttpResponseMessage page = await client.GetAsync(new Uri("/postgres/kafka", UriKind.Relative), Token);

            page.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task A_host_with_no_data_source_at_all_renders_the_kafka_page_and_the_rail()
    {
        await using WebApplication app = CreateApp(dataSource: false);
        app.MapPostgresStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();
            using HttpResponseMessage page = await client.GetAsync(new Uri("/postgres/kafka", UriKind.Relative), Token);

            page.StatusCode.Should().Be(HttpStatusCode.OK);
            (await page.Content.ReadAsStringAsync(Token)).Should().Contain("ms-k-trademark").And.Contain("href=\"kafka\"");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task Kafka_page_endpoints_carry_the_studio_policy_and_the_page_marker()
    {
        await using WebApplication app = CreateApp(
            configureStudio: static options => options.AuthorizationPolicy = "ops",
            configureBuilder: static builder => builder.Services.AddAuthorization(static options =>
                options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        app.MapPostgresStudio();

        RouteEndpoint[] pages = [.. RouteEndpoints(app).Where(IsKafkaPage)];

        pages.Should().NotBeEmpty();
        pages.Should().OnlyContain(static x => x.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(static y => y.Policy == "ops"));
        pages.Should().OnlyContain(static x => IsPageMarker(x));
    }

    [Fact]
    public async Task An_unauthorized_mapping_with_kafka_is_still_refused_at_startup()
    {
        await using WebApplication app = CreateApp();
        app.MapPostgresStudio();

        Func<Task> start = () => app.StartAsync(Token);

        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Postgres Studio");
    }

    [Fact]
    public async Task RequireAuthorization_on_the_builder_reaches_the_kafka_pages()
    {
        await using WebApplication app = CreateApp(configureBuilder: static builder =>
        {
            builder.Services
                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
            builder.Services.AddAuthorization();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPostgresStudio().RequireAuthorization();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();
            using HttpResponseMessage anonymous = await client.GetAsync(new Uri("/postgres/kafka", UriKind.Relative), Token);

            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }
}
