using System.Net;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Endpoints;

/// <summary>
/// The endpoint-metadata matrix of plan section 4.3, the sub-path re-rooting, and real
/// <c>TestServer</c> requests for the page, its stylesheet and the framework script.
/// </summary>
/// <remarks>
/// A test here that starts its application says <c>AllowAnonymous()</c> at the map site unless it is
/// about authorization: the startup guard refuses a studio mapping that states nothing, and what these
/// tests are about is paths, assets and markup rather than who may see them. The ones that configure a
/// fail-closed <c>FallbackPolicy</c> have already answered the question and say nothing.
/// </remarks>
public class StudioEndpointsTest
{
    private static WebApplication CreateApp(
        Action<MartenStudioOptions>? configureStudio = null,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMartenStudio(configureStudio);
        configureBuilder?.Invoke(builder);
        return builder.Build();
    }

    /// <summary>The runner's own token, so a cancelled run stops these requests rather than waiting.</summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static List<RouteEndpoint> GetRouteEndpoints(WebApplication app) =>
        ((IEndpointRouteBuilder) app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

    private static RouteEndpoint GetStaticAssetEndpoint(WebApplication app) =>
        GetRouteEndpoints(app).Single(x => x.RoutePattern.RawText == "_content/MartenStudio/{**path}");

    // -------------------------------------------------------------------------------------------
    // The plan section 4.3 matrix, default path
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Static_assets_allow_anonymous_when_no_policy_is_configured()
    {
        await using var app = CreateApp();
        app.MapMartenStudio();

        var assets = GetStaticAssetEndpoint(app);

        assets.Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        assets.Metadata.GetMetadata<IAuthorizeData>().Should().BeNull();
    }

    [Fact]
    public async Task Static_assets_carry_the_configured_policy()
    {
        await using var app = CreateApp(options => options.AuthorizationPolicy = "studio-policy");
        app.MapMartenStudio();

        var assets = GetStaticAssetEndpoint(app);

        assets.Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("studio-policy");
        assets.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
    }

    [Fact]
    public async Task A_configured_policy_covers_pages_and_the_circuit()
    {
        await using var app = CreateApp(options => options.AuthorizationPolicy = "studio-policy");
        app.MapMartenStudio();

        var endpoints = GetRouteEndpoints(app);

        endpoints.First(x => x.RoutePattern.RawText == "/marten")
            .Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("studio-policy");

        // The endpoints not backed by a page component - the /_blazor circuit - must carry the policy too
        // so a fail-closed FallbackPolicy configuration keeps working.
        var circuit = endpoints.Where(x => x.RoutePattern.RawText?.StartsWith("/_blazor", StringComparison.Ordinal) == true).ToList();
        circuit.Should().NotBeEmpty();
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAuthorizeData>() != null);
    }

    [Fact]
    public async Task Without_a_policy_the_circuit_is_anonymous_and_the_pages_carry_no_metadata()
    {
        await using var app = CreateApp();
        app.MapMartenStudio();

        var endpoints = GetRouteEndpoints(app);

        var circuit = endpoints.Where(x => x.RoutePattern.RawText?.StartsWith("/_blazor", StringComparison.Ordinal) == true).ToList();
        circuit.Should().NotBeEmpty();
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAllowAnonymous>() != null);

        // The pages stay subject to the host's own policies: neither anonymous nor explicitly authorized,
        // so Marten data is never silently exposed.
        var page = endpoints.First(x => x.RoutePattern.RawText == "/marten");
        page.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
        page.Metadata.GetMetadata<IAuthorizeData>().Should().BeNull();
    }

    // -------------------------------------------------------------------------------------------
    // Sub-path mounting
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_custom_path_moves_every_studio_route_and_leaves_nothing_behind()
    {
        await using var app = CreateApp(options => options.Path = "/ops/marten");
        app.MapMartenStudio();

        var patterns = GetRouteEndpoints(app).Select(x => x.RoutePattern.RawText).ToList();

        patterns.Should().Contain("/ops/marten");
        patterns.Should().Contain("/ops/marten/activity");

        patterns.Should().NotContain("/marten");
        patterns.Where(x => x?.StartsWith("/marten/", StringComparison.Ordinal) == true).Should().BeEmpty();

        // The circuit endpoints move too, so a reverse proxy that forwards only the studio prefix reaches
        // them.
        patterns.Where(x => x?.StartsWith("/_blazor", StringComparison.Ordinal) == true).Should().BeEmpty();
        patterns.Should().Contain(x => x != null && x.StartsWith("/ops/marten/_blazor", StringComparison.Ordinal));
        patterns.Should().Contain(x => x != null && x.StartsWith("/ops/marten/_blazor", StringComparison.Ordinal) && x.EndsWith("negotiate", StringComparison.Ordinal));

        patterns.Should().Contain("/ops/marten/_framework/blazor.web.js");
        patterns.Should().Contain("/ops/marten/_framework/opaque-redirect");
        patterns.Should().Contain("/ops/marten/_content/MartenStudio/{**path}");
        patterns.Should().Contain("_content/MartenStudio/{**path}");
    }

    [Fact]
    public async Task A_custom_path_policy_covers_the_re_rooted_endpoints()
    {
        await using var app = CreateApp(options =>
        {
            options.Path = "/ops/marten";
            options.AuthorizationPolicy = "studio-policy";
        });
        app.MapMartenStudio();

        var endpoints = GetRouteEndpoints(app);

        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten")
            .Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("studio-policy");

        var circuit = endpoints.Where(x => x.RoutePattern.RawText?.StartsWith("/ops/marten/_blazor", StringComparison.Ordinal) == true).ToList();
        circuit.Should().NotBeEmpty();
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAuthorizeData>() != null);

        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten/_framework/blazor.web.js")
            .Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("studio-policy");
        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten/_framework/opaque-redirect")
            .Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("studio-policy");
        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten/_content/MartenStudio/{**path}")
            .Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("studio-policy");
    }

    [Fact]
    public async Task A_custom_path_without_a_policy_keeps_the_plumbing_anonymous_and_the_pages_bare()
    {
        await using var app = CreateApp(options => options.Path = "/ops/marten");
        app.MapMartenStudio();

        var endpoints = GetRouteEndpoints(app);

        var circuit = endpoints.Where(x => x.RoutePattern.RawText?.StartsWith("/ops/marten/_blazor", StringComparison.Ordinal) == true).ToList();
        circuit.Should().NotBeEmpty();
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAllowAnonymous>() != null);

        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten/_framework/blazor.web.js")
            .Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten/_framework/opaque-redirect")
            .Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten/_content/MartenStudio/{**path}")
            .Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();

        var page = endpoints.First(x => x.RoutePattern.RawText == "/ops/marten");
        page.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
        page.Metadata.GetMetadata<IAuthorizeData>().Should().BeNull();
    }

    /// <summary>
    /// The convention filter, which is the reason <c>MapMartenStudio()</c> returns a builder of its own
    /// rather than the Razor components builder: what a caller says about the studio is about the studio.
    /// </summary>
    [Fact]
    public async Task A_hosts_own_page_gains_no_metadata_from_the_studios_conventions()
    {
        await using var app = CreateApp();
        app.MapRazorComponents<TestHostComponents>().AddInteractiveServerRenderMode();
        app.MapMartenStudio().RequireAuthorization("studio-policy");

        var endpoints = GetRouteEndpoints(app);

        endpoints.First(x => x.RoutePattern.RawText == "/host-page")
            .Metadata.GetMetadata<IAuthorizeData>().Should().BeNull();

        endpoints.First(x => x.RoutePattern.RawText == "/marten")
            .Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("studio-policy");
    }

    [Fact]
    public async Task A_pattern_at_the_map_site_beats_the_configured_path()
    {
        await using var app = CreateApp(options => options.Path = "/configured");
        app.MapMartenStudio("/mapped");

        var patterns = GetRouteEndpoints(app).Select(x => x.RoutePattern.RawText).ToList();

        patterns.Should().Contain("/mapped");
        patterns.Should().NotContain("/configured", "the pattern at the map site is the more specific of the two");
    }

    [Theory]
    [InlineData("")]
    [InlineData("marten")]
    [InlineData("/tenant{env}")]
    [InlineData("/ops/../marten")]
    [InlineData("/ops?x=1")]
    [InlineData("/ops#frag")]
    [InlineData("/ops//marten")]
    public async Task An_invalid_pattern_at_the_map_site_is_rejected(string pattern)
    {
        await using var app = CreateApp();

        var act = () => app.MapMartenStudio(pattern);

        act.Should().Throw<ArgumentException>().WithParameterName(nameof(pattern));
    }

    [Theory]
    [InlineData("/tenant{env}")]
    [InlineData("/ops/../marten")]
    [InlineData("/ops?x=1")]
    [InlineData("/ops#frag")]
    [InlineData("/ops//marten")]
    public async Task An_invalid_configured_path_fails_validation(string path)
    {
        await using var app = CreateApp(options => options.Path = path);

        var act = () => app.MapMartenStudio();

        act.Should().Throw<OptionsValidationException>().WithMessage("*Path*");
    }

    // -------------------------------------------------------------------------------------------
    // Real requests
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_default_path_serves_the_shell_with_a_path_base_rooted_base_href()
    {
        await using var app = CreateApp();
        app.UseAntiforgery();
        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var response = await client.GetAsync(new Uri("/marten", UriKind.Relative), Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync(Token);
            body.Should().Contain("<base href=\"/\"");
            body.Should().Contain("_framework/blazor.web.js");
            body.Should().Contain("<title>Marten Studio</title>");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task A_custom_path_serves_the_shell_with_a_studio_rooted_base_href()
    {
        await using var app = CreateApp(options => options.Path = "/ops/marten");
        app.UseAntiforgery();
        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var response = await client.GetAsync(new Uri("/ops/marten", UriKind.Relative), Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Token)).Should().Contain("<base href=\"/ops/marten/\"");

            // Route matching tolerates the trailing-slash form of the studio root.
            using var slash = await client.GetAsync(new Uri("/ops/marten/", UriKind.Relative), Token);
            slash.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// Server-side route matching is case-insensitive, so the request URL may carry different casing than
    /// the configured path. The base href keeps the request's casing, or every relative navigation the
    /// browser makes falls outside the document base.
    /// </summary>
    [Fact]
    public async Task A_custom_path_keeps_the_requests_casing_in_the_base_href()
    {
        await using var app = CreateApp(options => options.Path = "/ops/marten");
        app.UseAntiforgery();
        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var response = await client.GetAsync(new Uri("/Ops/Marten", UriKind.Relative), Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Token)).Should().Contain("<base href=\"/Ops/Marten/\"");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task A_custom_path_serves_the_stylesheet_and_the_framework_script_from_the_web_root()
    {
        await using var app = CreateApp(
            options => options.Path = "/ops/marten",
            builder => builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
            {
                ["_framework/blazor.web.js"] = "// blazor.web.js"u8.ToArray(),
                ["_content/MartenStudio/css/marten-studio.css"] = ":root { }"u8.ToArray(),
            }));

        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var css = await client.GetAsync(new Uri("/ops/marten/_content/MartenStudio/css/marten-studio.css", UriKind.Relative), Token);
            css.StatusCode.Should().Be(HttpStatusCode.OK);
            css.Content.Headers.ContentType!.MediaType.Should().Be("text/css");

            using var script = await client.GetAsync(new Uri("/ops/marten/_framework/blazor.web.js", UriKind.Relative), Token);
            script.StatusCode.Should().Be(HttpStatusCode.OK);
            script.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
            (await script.Content.ReadAsByteArrayAsync(Token)).Should().NotBeEmpty();

            // The root-level asset endpoint stays for hosts that do not path-forward.
            using var rootCss = await client.GetAsync(new Uri("/_content/MartenStudio/css/marten-studio.css", UriKind.Relative), Token);
            rootCss.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// R1: on .NET 10 <c>blazor.web.js</c> is not in the web root and is not embedded in the ASP.NET Core
    /// assemblies either - it is a static web asset the host serves from its own root endpoint (which is
    /// why a host with no <c>.razor</c> files of its own needs
    /// <c>&lt;RequiresAspNetWebAssets&gt;true&lt;/RequiresAspNetWebAssets&gt;</c> plus
    /// <c>MapStaticAssets()</c>). So with nothing in the web root, the mirrored route has to find the
    /// root endpoint and run it - which is the studio's own logic and what this pins.
    /// </summary>
    [Fact]
    public async Task A_custom_path_forwards_the_framework_script_to_the_hosts_own_root_endpoint()
    {
        await using var app = CreateApp(options => options.Path = "/ops/marten");

        // Stands in for the host's MapStaticAssets() endpoint: the studio must find whatever serves the
        // script at the root and run it under the mirrored path, rather than 404.
        app.MapGet("/_framework/blazor.web.js", static (HttpContext context) =>
        {
            context.Response.ContentType = "text/javascript";
            return context.Response.WriteAsync("// served by the host's root endpoint");
        }).AllowAnonymous();

        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var script = await client.GetAsync(new Uri("/ops/marten/_framework/blazor.web.js", UriKind.Relative), Token);

            script.StatusCode.Should().Be(HttpStatusCode.OK);
            script.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
            (await script.Content.ReadAsStringAsync(Token)).Should().Be("// served by the host's root endpoint");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// The other half of R1: with nothing serving the script anywhere, the mirrored route answers 404
    /// rather than throwing or hanging. A host in that state has forgotten
    /// <c>RequiresAspNetWebAssets</c> or <c>MapStaticAssets()</c>, and a 404 is the symptom its own
    /// documentation names.
    /// </summary>
    [Fact]
    public async Task A_custom_path_answers_404_for_the_framework_script_when_the_host_serves_none()
    {
        await using var app = CreateApp(options => options.Path = "/ops/marten");
        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var script = await client.GetAsync(new Uri("/ops/marten/_framework/blazor.web.js", UriKind.Relative), Token);

            script.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task A_custom_path_serves_the_blazor_negotiate_endpoint()
    {
        await using var app = CreateApp(options => options.Path = "/ops/marten");
        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            // Proves the re-rooted SignalR circuit endpoints still dispatch.
            using var response = await client.PostAsync(new Uri("/ops/marten/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), content: null, Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Token)).Should().Contain("availableTransports");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task A_custom_path_forwards_the_opaque_redirect_to_the_frameworks_own_endpoint()
    {
        await using var app = CreateApp(options => options.Path = "/ops/marten");
        app.MapMartenStudio().AllowAnonymous();

        GetRouteEndpoints(app).Select(x => x.RoutePattern.RawText)
            .Should().Contain("/ops/marten/_framework/opaque-redirect");

        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            // The framework endpoint rejects the missing protected payload; a 404 would mean the mirror
            // found nothing to forward to, which is the failure this is about.
            using var response = await client.GetAsync(new Uri("/ops/marten/_framework/opaque-redirect", UriKind.Relative), Token);

            response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task Static_assets_are_served_under_a_fail_closed_fallback_policy_while_pages_are_not()
    {
        await using var app = CreateApp(
            options => options.Path = "/ops/marten",
            builder =>
            {
                builder.Services
                    .AddAuthentication(TestAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
                builder.Services.AddAuthorization(options =>
                    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAssertion(static _ => false).Build());
                builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
                {
                    ["_framework/blazor.web.js"] = "// blazor.web.js"u8.ToArray(),
                    ["_content/MartenStudio/css/marten-studio.css"] = ":root { }"u8.ToArray(),
                });
            });

        app.MapMartenStudio();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var css = await client.GetAsync(new Uri("/ops/marten/_content/MartenStudio/css/marten-studio.css", UriKind.Relative), Token);
            css.StatusCode.Should().Be(HttpStatusCode.OK);

            using var script = await client.GetAsync(new Uri("/ops/marten/_framework/blazor.web.js", UriKind.Relative), Token);
            script.StatusCode.Should().Be(HttpStatusCode.OK);

            using var page = await client.GetAsync(new Uri("/ops/marten", UriKind.Relative), Token);
            page.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }
}
