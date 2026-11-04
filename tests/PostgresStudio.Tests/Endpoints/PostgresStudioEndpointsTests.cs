using System.Net;
using System.Security.Claims;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

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

        // As MapStaticAssets registers them: a literal route per file, with or without the leading slash, for GET and HEAD.
        app.MapMethods("/_framework/" + Initializer, ["GET", "HEAD"], static () => Results.Text("export function beforeWebStart() { }", "text/javascript")).AllowAnonymous();
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

    /// <summary>
    /// A path that could climb out of <c>_framework</c> is a 404 that never reaches the web root's file provider, and so is
    /// a file the host does not have. The rows are the request paths as the server sees them <em>after</em> decoding
    /// (<c>%2e%2e/</c> is <c>../</c>, <c>%5C</c> a backslash, <c>%00</c> a NUL), and they are sent through the test server
    /// directly: an ordinary client <see cref="Uri" /> would fold <c>../</c> away before it was sent and never reach the
    /// guard. The recording provider proves the guard, not the router, refused them.
    /// </summary>
    [Theory]
    [InlineData("/postgres/_framework/nothing-serves-this.js")]
    [InlineData("/postgres/_framework/../../appsettings.json")]
    [InlineData("/postgres/_framework/nested/../../appsettings.json")]
    [InlineData("/postgres/_framework/..\\..\\appsettings.json")]
    [InlineData("/postgres/_framework/nested/..\\..\\appsettings.json")]
    [InlineData("/postgres/_framework/a\0b.js")]
    [InlineData("/postgres/_framework/a\\b.js")]
    [InlineData("/postgres/_framework//appsettings.json")]
    [InlineData("/postgres/_framework/%2e%2e/appsettings.json")]
    [InlineData("/postgres/_framework/nested")]
    public async Task A_framework_file_the_host_does_not_serve_and_any_path_that_climbs_is_404_never_500(string path)
    {
        RecordingFileProvider webRoot = new(new TestFileProvider(new Dictionary<string, byte[]>
        {
            ["_framework/nested/other.js"] = "// other"u8.ToArray(),
            ["appsettings.json"] = "{ \"secret\": true }"u8.ToArray(),
        }));
        await using WebApplication app = CreateApp(configureBuilder: builder => builder.Environment.WebRootFileProvider = webRoot);
        string? matched = null;
        app.Use(async (context, next) =>
        {
            matched = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            await next(context);
        });
        app.MapPostgresStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            HttpContext response = await app.GetTestServer().SendAsync(
                context =>
                {
                    context.Request.Method = HttpMethods.Get;
                    context.Request.Path = new PathString(path);
                },
                Token);

            matched.Should().Be("/postgres/_framework/{**file}", "the row must reach the forward's guard, not be routed elsewhere");
            response.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
            using StreamReader reader = new(response.Response.Body);
            (await reader.ReadToEndAsync(Token)).Should().NotContain("secret");
            webRoot.Asked.Should().NotContain(static x => x.Contains("..", StringComparison.Ordinal)
                    || x.Contains('\\', StringComparison.Ordinal)
                    || x.Contains('\0', StringComparison.Ordinal)
                    || x.Contains("//", StringComparison.Ordinal)
                    || x == "appsettings.json" || x == "_framework/appsettings.json",
                "the guard refuses these before the provider is asked");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>The control for the theory above: a legitimate nested file does reach the provider, so the recorder sees real asks.</summary>
    [Fact]
    public async Task The_recording_provider_sees_the_legitimate_nested_ask()
    {
        RecordingFileProvider webRoot = new(new TestFileProvider(new Dictionary<string, byte[]>
        {
            ["_framework/nested/other.js"] = "// other"u8.ToArray(),
        }));
        await using WebApplication app = CreateApp(configureBuilder: builder => builder.Environment.WebRootFileProvider = webRoot);
        app.MapPostgresStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri("/postgres/_framework/nested/other.js", UriKind.Relative), Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            webRoot.Asked.Should().Contain("_framework/nested/other.js");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// The literal endpoints keep their own handlers by route precedence: <c>blazor.web.js</c> is still served by the
    /// studio's three-tier handler (the web root's copy wins over what a root endpoint serves), and the enhanced-navigation
    /// <c>opaque-redirect</c> request is still matched to the framework's own endpoint, not to the file forward.
    /// </summary>
    [Fact]
    public async Task The_literal_framework_endpoints_keep_their_own_handlers_beside_the_file_forward()
    {
        await using WebApplication app = CreateApp(configureBuilder: WithWebRoot);
        app.MapGet("/_framework/blazor.web.js", static () => Results.Text("// the root endpoint's", "text/javascript")).AllowAnonymous();
        string? matched = null;
        app.Use(async (context, next) =>
        {
            matched = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            await next(context);
        });
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
            matched.Should().Be("/postgres/_framework/blazor.web.js");

            using HttpResponseMessage redirect = await client.GetAsync(new Uri("/postgres/_framework/opaque-redirect?url=x", UriKind.Relative), Token);
            matched.Should().Be("/postgres/_framework/opaque-redirect", "the framework's own endpoint is matched, not the file forward");
            redirect.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
            redirect.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    // -------------------------------------------------------------------------------------------
    // The forward serves the host's files: it is governed as the page that imports them is (1.2-NET11-FIX)
    // -------------------------------------------------------------------------------------------

    /// <summary>Without the option the forward is no anonymous asset endpoint: it says nothing, so the caller and the FallbackPolicy decide.</summary>
    [Fact]
    public async Task Without_a_policy_the_framework_file_forward_says_nothing_and_the_builder_governs_it()
    {
        await using WebApplication silent = CreateApp();
        silent.MapPostgresStudio();
        RouteEndpoint untouched = RouteEndpoints(silent).Single(static x => x.RoutePattern.RawText == "/postgres/_framework/{**file}");
        untouched.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull("the host's files may not be opted out by the studio");
        untouched.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().BeEmpty();

        await using WebApplication required = CreateApp();
        required.MapPostgresStudio().RequireAuthorization("x");
        RouteEndpoint governed = RouteEndpoints(required).Single(static x => x.RoutePattern.RawText == "/postgres/_framework/{**file}");
        governed.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().Contain(static x => x.Policy == "x");

        await using WebApplication anonymous = CreateApp();
        anonymous.MapPostgresStudio().AllowAnonymous();
        RouteEndpoint open = RouteEndpoints(anonymous).Single(static x => x.RoutePattern.RawText == "/postgres/_framework/{**file}");
        open.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull("the caller said so about the mapping");
    }

    [Fact]
    public async Task With_the_option_set_the_framework_file_forward_carries_the_studios_policy()
    {
        await using WebApplication governed = CreateApp(
            static options => options.AuthorizationPolicy = "ops",
            static builder => builder.Services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        governed.MapPostgresStudio();

        RouteEndpoint withPolicy = RouteEndpoints(governed).Single(static x => x.RoutePattern.RawText == "/postgres/_framework/{**file}");

        withPolicy.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().Contain(static x => x.Policy == "ops");
        withPolicy.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
    }

    private static WebApplication CreateFailClosedApp(Action<WebApplicationBuilder>? configureBuilder = null) =>
        CreateApp(configureBuilder: builder =>
        {
            builder.Services
                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
            builder.Services.AddAuthorization(static options =>
            {
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
                options.AddPolicy("nobody", static policy => policy.RequireAssertion(static _ => false));
            });
            configureBuilder?.Invoke(builder);
        });

    private static HttpRequestMessage AsOperator(string path, HttpMethod? method = null)
    {
        HttpRequestMessage request = new(method ?? HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthenticationHandler.UserHeader, "operator");
        return request;
    }

    /// <summary>
    /// The review's finding, as a host is built: a fail-closed <c>FallbackPolicy</c> and the studio mapped with
    /// <c>RequireAuthorization()</c> on the builder. The host's own file is 401 at the root and was 200 to anyone under the
    /// mount; an anonymous visitor now gets neither the file nor a 200, and a visitor the studio admits gets exactly what
    /// the host would give them: a file with no metadata of its own (the initializer), and a 404 for one whose own policy
    /// refuses them.
    /// </summary>
    [Fact]
    public async Task A_fail_closed_host_serves_its_framework_files_under_the_mount_only_to_who_the_host_would_serve()
    {
        await using WebApplication app = CreateFailClosedApp();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("_framework/App.Client.wasm", static () => Results.Text("host-owned wasm", "application/wasm"));
        app.MapGet("/_framework/" + Initializer, static () => Results.Text("export function beforeWebStart() { }", "text/javascript"));
        app.MapGet("/_framework/admin-only.js", static () => Results.Text("host-owned admin", "text/javascript")).RequireAuthorization("nobody");
        app.MapPostgresStudio().RequireAuthorization();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage root = await client.GetAsync(new Uri("/_framework/App.Client.wasm", UriKind.Relative), Token);
            root.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using HttpResponseMessage anonymous = await client.GetAsync(new Uri("/postgres/_framework/App.Client.wasm", UriKind.Relative), Token);
            anonymous.StatusCode.Should().NotBe(HttpStatusCode.OK);
            (await anonymous.Content.ReadAsStringAsync(Token)).Should().NotContain("host-owned");

            using HttpRequestMessage initializerRequest = AsOperator("/postgres/_framework/" + Initializer);
            using HttpResponseMessage initializer = await client.SendAsync(initializerRequest, Token);
            initializer.StatusCode.Should().Be(HttpStatusCode.OK, "a visitor the studio admits gets the file a file with no metadata gives them");
            (await initializer.Content.ReadAsStringAsync(Token)).Should().Contain("beforeWebStart");

            using HttpRequestMessage wasmRequest = AsOperator("/postgres/_framework/App.Client.wasm");
            using HttpResponseMessage wasm = await client.SendAsync(wasmRequest, Token);
            wasm.StatusCode.Should().Be(HttpStatusCode.OK);

            using HttpRequestMessage atRoot = AsOperator("/_framework/admin-only.js");
            using HttpResponseMessage adminAtRoot = await client.SendAsync(atRoot, Token);
            adminAtRoot.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the host itself refuses this visitor");

            using HttpRequestMessage underMount = AsOperator("/postgres/_framework/admin-only.js");
            using HttpResponseMessage adminUnderMount = await client.SendAsync(underMount, Token);
            adminUnderMount.StatusCode.Should().Be(HttpStatusCode.NotFound, "the studio never serves what the host would refuse");
            (await adminUnderMount.Content.ReadAsStringAsync(Token)).Should().NotContain("host-owned");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>A host's authorization handler that judges the request it is asked about by its path and its resource.</summary>
    private sealed class RootPathRequirement : IAuthorizationRequirement
    {
        public const string Policy = "root-path";

        public const string RootPath = "/_framework/rooted.js";
    }

    private sealed class RootPathHandler : AuthorizationHandler<RootPathRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RootPathRequirement requirement)
        {
            if (context.Resource is HttpContext http && http.Request.Path == RootPathRequirement.RootPath)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>An endpoint that states its authorization only as requirement data, as an attribute implementing it would.</summary>
    private sealed class FailingRequirementData : IAuthorizationRequirementData
    {
        public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new AssertionRequirement(static _ => false)];
    }

    /// <summary>
    /// <c>UseAuthorization</c> judges a request to the root path: a handler of the host's reads the <see cref="HttpContext" />
    /// as the resource and the path as the root's. The forward evaluates the root endpoint's policy the same way, so a handler
    /// that admits the root request admits the mirrored one (the studio sees what the host would see), while the same handler
    /// refuses a request whose path is the mirror's.
    /// </summary>
    [Fact]
    public async Task The_roots_authorization_is_evaluated_as_the_middleware_would_with_the_http_context_and_the_root_path()
    {
        await using WebApplication app = CreateFailClosedApp(static builder =>
        {
            builder.Services.AddSingleton<IAuthorizationHandler, RootPathHandler>();
            builder.Services.Configure<AuthorizationOptions>(static options =>
                options.AddPolicy(RootPathRequirement.Policy, static policy => policy.AddRequirements(new RootPathRequirement())));
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet(RootPathRequirement.RootPath, static () => Results.Text("host-owned rooted", "text/javascript")).RequireAuthorization(RootPathRequirement.Policy);
        app.MapPostgresStudio().RequireAuthorization();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpRequestMessage rootRequest = AsOperator(RootPathRequirement.RootPath);
            using HttpResponseMessage root = await client.SendAsync(rootRequest, Token);
            root.StatusCode.Should().Be(HttpStatusCode.OK, "the handler admits the root request");

            using HttpRequestMessage mirrorRequest = AsOperator("/postgres" + RootPathRequirement.RootPath);
            using HttpResponseMessage mirror = await client.SendAsync(mirrorRequest, Token);
            mirror.StatusCode.Should().Be(HttpStatusCode.OK, "the evaluation sees the root path and the HttpContext, as the middleware would");
            (await mirror.Content.ReadAsStringAsync(Token)).Should().Contain("host-owned rooted");

            // The control: the very same policy refuses a request that carries the mirror's path.
            DefaultHttpContext asMirror = new();
            asMirror.Request.Path = "/postgres" + RootPathRequirement.RootPath;
            ClaimsPrincipal user = new(new ClaimsIdentity("test"));
            IAuthorizationService authorization = app.Services.GetRequiredService<IAuthorizationService>();
            (await authorization.AuthorizeAsync(user, asMirror, RootPathRequirement.Policy)).Succeeded.Should().BeFalse();

            // Whatever the evaluation did to the request, it put the mirror's path and endpoint back.
            using HttpRequestMessage again = AsOperator("/postgres/_framework/nothing-here.js");
            using HttpResponseMessage missing = await client.SendAsync(again, Token);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>An endpoint that states only an <see cref="IAuthorizationRequirementData" /> is judged by it, not by the fallback.</summary>
    [Fact]
    public async Task A_root_endpoint_that_states_only_requirement_data_is_refused_through_the_mirror_when_the_requirement_fails()
    {
        await using WebApplication app = CreateFailClosedApp();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/_framework/requirement-data.js", static () => Results.Text("host-owned data", "text/javascript")).WithMetadata(new FailingRequirementData());
        app.MapPostgresStudio().RequireAuthorization();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpRequestMessage rootRequest = AsOperator("/_framework/requirement-data.js");
            using HttpResponseMessage root = await client.SendAsync(rootRequest, Token);
            root.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the host's own middleware refuses an authenticated visitor");

            using HttpRequestMessage mirrorRequest = AsOperator("/postgres/_framework/requirement-data.js");
            using HttpResponseMessage mirror = await client.SendAsync(mirrorRequest, Token);
            mirror.StatusCode.Should().Be(HttpStatusCode.NotFound, "the studio never serves what the host would refuse");
            (await mirror.Content.ReadAsStringAsync(Token)).Should().NotContain("host-owned");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>The web-root branch is held to the <c>FallbackPolicy</c> too, even under an <c>AllowAnonymous()</c> mapping.</summary>
    [Fact]
    public async Task A_web_root_framework_file_is_held_to_the_fallback_policy_under_an_anonymous_mapping()
    {
        await using WebApplication app = CreateFailClosedApp(static builder =>
            builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
            {
                ["_framework/host-owned.js"] = "// host-owned"u8.ToArray(),
            }));
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPostgresStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage anonymous = await client.GetAsync(new Uri("/postgres/_framework/host-owned.js", UriKind.Relative), Token);
            anonymous.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await anonymous.Content.ReadAsStringAsync(Token)).Should().NotContain("host-owned");

            using HttpRequestMessage request = AsOperator("/postgres/_framework/host-owned.js");
            using HttpResponseMessage authenticated = await client.SendAsync(request, Token);
            authenticated.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>A GET to the mirror never runs a handler the host mapped for another method at the same path.</summary>
    [Fact]
    public async Task A_get_to_the_mirror_does_not_run_a_root_endpoint_for_another_method()
    {
        int ran = 0;
        await using WebApplication app = CreateApp();
        app.MapPost("/_framework/x.js", () =>
        {
            ran++;
            return Results.Text("posted", "text/javascript");
        }).AllowAnonymous();
        app.MapPostgresStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri("/postgres/_framework/x.js", UriKind.Relative), Token);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            ran.Should().Be(0);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>Records every path the web root is asked for, so a test can prove what the guard kept from the provider.</summary>
    private sealed class RecordingFileProvider(IFileProvider inner) : IFileProvider
    {
        private readonly List<string> asked = [];

        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (asked)
                {
                    return [.. asked];
                }
            }
        }

        public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);

        public IFileInfo GetFileInfo(string subpath)
        {
            lock (asked)
            {
                asked.Add(subpath);
            }

            return inner.GetFileInfo(subpath);
        }

        public IChangeToken Watch(string filter) => inner.Watch(filter);
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
