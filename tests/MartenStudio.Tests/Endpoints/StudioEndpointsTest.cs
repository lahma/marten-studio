using System.Net;

using MartenStudio.Internal;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Antiforgery;
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

    /// <summary>Every studio mount is studio-rooted, so the circuit lives under the studio path.</summary>
    private static List<RouteEndpoint> GetCircuitEndpoints(WebApplication app, string studioPath = "/marten") =>
        GetRouteEndpoints(app)
            .Where(x => x.RoutePattern.RawText?.StartsWith(studioPath + "/_blazor", StringComparison.Ordinal) == true)
            .ToList();

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
        var circuit = GetCircuitEndpoints(app);
        circuit.Should().NotBeEmpty();
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAuthorizeData>() != null);
    }

    /// <summary>
    /// The plan section 4.3 matrix, as it reads after the circuit stopped being stamped anonymous: with
    /// no policy and nothing said at the map site, the pages and the circuit alike carry no metadata, so
    /// a host <c>FallbackPolicy</c> governs both. The old behaviour - <c>AllowAnonymous</c> on
    /// <c>/_blazor</c> - meant an application that authorized the studio with
    /// <c>RequireAuthorization()</c> on the builder answered 401 for its pages and 200 for
    /// <c>POST /_blazor/negotiate</c>, which is a circuit anyone could open.
    /// </summary>
    [Fact]
    public async Task Without_a_policy_neither_the_pages_nor_the_circuit_carry_metadata()
    {
        await using var app = CreateApp();
        app.MapMartenStudio();

        var circuit = GetCircuitEndpoints(app);
        circuit.Should().NotBeEmpty();
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAllowAnonymous>() == null);
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAuthorizeData>() == null);

        // The pages stay subject to the host's own policies: neither anonymous nor explicitly authorized,
        // so Marten data is never silently exposed.
        var page = GetRouteEndpoints(app).First(x => x.RoutePattern.RawText == "/marten");
        page.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();
        page.Metadata.GetMetadata<IAuthorizeData>().Should().BeNull();
    }

    [Fact]
    public async Task RequireAuthorization_on_the_builder_reaches_the_circuit()
    {
        await using var app = CreateApp();
        app.MapMartenStudio().RequireAuthorization("studio-policy");

        var circuit = GetCircuitEndpoints(app);
        circuit.Should().NotBeEmpty();
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAuthorizeData>() != null);
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAllowAnonymous>() == null);

        GetRouteEndpoints(app).First(x => x.RoutePattern.RawText == "/marten")
            .Metadata.GetMetadata<IAuthorizeData>()!.Policy.Should().Be("studio-policy");
    }

    [Fact]
    public async Task AllowAnonymous_on_the_builder_reaches_the_circuit()
    {
        await using var app = CreateApp();
        app.MapMartenStudio().AllowAnonymous();

        GetCircuitEndpoints(app).Should().NotBeEmpty()
            .And.OnlyContain(x => x.Metadata.GetMetadata<IAllowAnonymous>() != null);
    }

    /// <summary>
    /// The startup guard's marker now covers the circuit as well as the pages: an unauthorized circuit is
    /// the more dangerous of the two, because it is the one that runs components.
    /// </summary>
    [Fact]
    public async Task The_guards_marker_covers_the_pages_and_the_circuit()
    {
        await using var app = CreateApp();
        app.MapMartenStudio().AllowAnonymous();

        var endpoints = GetRouteEndpoints(app);

        endpoints.First(x => x.RoutePattern.RawText == "/marten")
            .Metadata.GetMetadata<MartenStudioEndpointMarker>()!.IsPage.Should().BeTrue();

        GetCircuitEndpoints(app).Should().OnlyContain(
            x => x.Metadata.GetMetadata<MartenStudioEndpointMarker>() != null
                && !x.Metadata.GetMetadata<MartenStudioEndpointMarker>()!.IsPage);
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
    public async Task A_custom_path_without_a_policy_keeps_the_assets_anonymous_and_the_rest_bare()
    {
        await using var app = CreateApp(options => options.Path = "/ops/marten");
        app.MapMartenStudio();

        var endpoints = GetRouteEndpoints(app);

        // The circuit is the studio, so it is governed by whatever governs the studio - not by a blanket
        // AllowAnonymous of the studio's own.
        var circuit = GetCircuitEndpoints(app, "/ops/marten");
        circuit.Should().NotBeEmpty();
        circuit.Should().OnlyContain(x => x.Metadata.GetMetadata<IAllowAnonymous>() == null);

        // Package content: the stylesheet and the framework script stay reachable under a fail-closed
        // FallbackPolicy, because a studio without its stylesheet is not a security win.
        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten/_framework/blazor.web.js")
            .Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten/_content/MartenStudio/{**path}")
            .Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();

        // The enhanced-navigation redirect endpoint comes out of the studio's own MapRazorComponents, so
        // it is governed with the rest of the studio rather than opting itself out.
        endpoints.First(x => x.RoutePattern.RawText == "/ops/marten/_framework/opaque-redirect")
            .Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull();

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
    public async Task The_default_path_serves_the_shell_with_a_studio_rooted_base_href()
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

            // Studio-rooted, at the default path too: the studio always takes its own /_blazor, framework
            // script and asset mirror under Path, and the document base is what points the browser there.
            body.Should().Contain("<base href=\"/marten/\"");
            body.Should().Contain("<script src=\"_framework/blazor.web.js\">");
            body.Should().Contain("<link rel=\"stylesheet\" href=\"_content/MartenStudio/css/marten-studio.css\"");
            body.Should().Contain("<title>Marten Studio</title>");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// The studio must not require <c>app.UseAntiforgery()</c> on the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the bare host an API-only project has: <c>AddMartenStudio()</c>, <c>MapMartenStudio()</c>
    /// and nothing else in the pipeline. <c>MapRazorComponents</c> stamps <c>IAntiforgeryMetadata</c> that
    /// requires validation on every page endpoint, and the framework then refuses to serve one when the
    /// antiforgery middleware is not in the pipeline - so before <c>DisableAntiforgery()</c> this request
    /// was a 500 with "Endpoint … contains anti-forgery metadata, but a middleware was not found".
    /// </para>
    /// <para>
    /// Asserted as a real request rather than as metadata, because the failure was the framework's own
    /// check at request time and a metadata assertion would have passed throughout.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_host_that_never_calls_UseAntiforgery_still_serves_the_studio()
    {
        await using var app = CreateApp();
        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var response = await client.GetAsync(new Uri("/marten", UriKind.Relative), Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync(Token);
            body.Should().Contain("<base href=\"/marten/\"");

            // The studio has no form, so there is no token in the document either - the shell used to
            // render one, which is what made the middleware load-bearing.
            body.Should().NotContain("__RequestVerificationToken");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// The metadata half of the same decision: every endpoint the studio maps says validation is not
    /// required, so a host that <em>does</em> use the middleware simply has nothing to validate here.
    /// </summary>
    [Fact]
    public async Task Every_studio_endpoint_says_antiforgery_validation_is_not_required()
    {
        await using var app = CreateApp();
        app.MapMartenStudio().AllowAnonymous();

        var studioEndpoints = GetRouteEndpoints(app)
            .Where(x => x.Metadata.GetMetadata<MartenStudioEndpointMarker>() is not null)
            .ToList();

        studioEndpoints.Should().NotBeEmpty();
        studioEndpoints.Should().AllSatisfy(endpoint =>
        {
            IAntiforgeryMetadata? metadata = endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>();

            metadata.Should().NotBeNull(endpoint.RoutePattern.RawText);
            metadata!.RequiresValidation.Should().BeFalse(endpoint.RoutePattern.RawText);
        });
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

    // -------------------------------------------------------------------------------------------
    // Every mount is studio-rooted (P1.2 review finding B2)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The default mount takes its plumbing with it, exactly as a custom path does. Before this, the
    /// studio's <c>MapRazorComponents</c> left a second <c>/_blazor</c> at the application root; in a
    /// host that has a Blazor application of its own that is two endpoints on one route, and
    /// <em>every</em> <c>POST /_blazor/negotiate</c> in the process - the host's included - answers 500
    /// with an <c>AmbiguousMatchException</c>.
    /// </summary>
    [Fact]
    public async Task The_default_mount_leaves_no_endpoint_at_the_application_root()
    {
        await using var app = CreateApp();
        app.MapMartenStudio().AllowAnonymous();

        var patterns = GetRouteEndpoints(app).Select(x => x.RoutePattern.RawText).ToList();

        patterns.Where(x => x?.StartsWith("/_blazor", StringComparison.Ordinal) == true).Should().BeEmpty();
        patterns.Should().NotContain("/_framework/opaque-redirect");

        patterns.Should().Contain("/marten");
        patterns.Should().Contain("/marten/_blazor/negotiate");
        patterns.Should().Contain("/marten/_framework/opaque-redirect");
        patterns.Should().Contain("/marten/_framework/blazor.web.js");
        patterns.Should().Contain("/marten/_content/MartenStudio/{**path}");
        patterns.Should().Contain("_content/MartenStudio/{**path}");
    }

    [Fact]
    public async Task A_host_with_its_own_blazor_app_keeps_its_own_circuit()
    {
        await using var app = CreateApp();
        app.UseAntiforgery();
        app.MapRazorComponents<TestHostComponents>().AddInteractiveServerRenderMode();
        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var hostCircuit = await client.PostAsync(new Uri("/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), content: null, Token);
            hostCircuit.StatusCode.Should().Be(HttpStatusCode.OK, "the host's own circuit must be untouched by mapping the studio");

            using var studioCircuit = await client.PostAsync(new Uri("/marten/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), content: null, Token);
            studioCircuit.StatusCode.Should().Be(HttpStatusCode.OK);

            using var hostPage = await client.GetAsync(new Uri("/host-page", UriKind.Relative), Token);
            hostPage.StatusCode.Should().Be(HttpStatusCode.OK);

            using var studioPage = await client.GetAsync(new Uri("/marten", UriKind.Relative), Token);
            studioPage.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// The framework script is mapped for every mount, not only a custom path (P1.2 review finding B1).
    /// A host with no <c>.razor</c> files of its own never resolves the ASP.NET Core web-assets pack, so
    /// nothing serves <c>/_framework/blazor.web.js</c> and the studio prerenders and stops there - with
    /// the default path it did not even have a route of its own to answer from.
    /// </summary>
    [Fact]
    public async Task The_default_path_serves_the_framework_script_from_the_hosts_web_root()
    {
        await using var app = CreateApp(
            configureBuilder: builder => builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
            {
                ["_framework/blazor.web.js"] = "// the host's own"u8.ToArray(),
            }));

        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var script = await client.GetAsync(new Uri("/marten/_framework/blazor.web.js", UriKind.Relative), Token);

            script.StatusCode.Should().Be(HttpStatusCode.OK);
            (await script.Content.ReadAsStringAsync(Token)).Should().Be("// the host's own");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// The last tier: the copy the package ships, served when the host has neither a web-root copy nor an
    /// endpoint of its own. The host's copy is preferred where there is one, because it is the one that
    /// matches the host's runtime.
    /// </summary>
    [Fact]
    public async Task The_packaged_framework_script_is_the_fallback_when_the_host_serves_none()
    {
        await using var app = CreateApp(
            configureBuilder: builder => builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
            {
                ["_content/MartenStudio/_framework/blazor.web.js"] = "// the package's own"u8.ToArray(),
            }));

        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var script = await client.GetAsync(new Uri("/marten/_framework/blazor.web.js", UriKind.Relative), Token);

            script.StatusCode.Should().Be(HttpStatusCode.OK);
            script.Content.Headers.ContentType!.MediaType.Should().Be("text/javascript");
            (await script.Content.ReadAsStringAsync(Token)).Should().Be("// the package's own");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    // -------------------------------------------------------------------------------------------
    // The circuit is authorized with the studio (P1.2 review finding B3)
    // -------------------------------------------------------------------------------------------

    private static void AddTestAuthentication(WebApplicationBuilder builder)
    {
        builder.Services
            .AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
        builder.Services.AddAuthorization();
    }

    /// <summary>
    /// The finding itself: with <c>RequireAuthorization()</c> on the returned builder and no
    /// <c>AuthorizationPolicy</c> option, the pages answered 401 and <c>POST /_blazor/negotiate</c>
    /// answered 200, so anyone could open a circuit, replay a prerender descriptor and run the studio's
    /// components against Marten without ever passing the policy.
    /// </summary>
    [Fact]
    public async Task An_anonymous_client_cannot_open_a_circuit_when_the_builder_requires_authorization()
    {
        await using var app = CreateApp(configureBuilder: AddTestAuthentication);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMartenStudio().RequireAuthorization();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var anonymous = await client.PostAsync(new Uri("/marten/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), content: null, Token);
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using var page = await client.GetAsync(new Uri("/marten", UriKind.Relative), Token);
            page.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the pages were already refused; the circuit is what leaked");

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/marten/_blazor/negotiate?negotiateVersion=1", UriKind.Relative));
            request.Headers.Add(TestAuthenticationHandler.UserHeader, "operator");
            using var authenticated = await client.SendAsync(request, Token);
            authenticated.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// <c>AllowAnonymous()</c> at the map site wins over a configured <c>AuthorizationPolicy</c>, because
    /// the studio's endpoints carry both and that is what the authorization middleware honours. The
    /// sample's <c>--anonymous</c> switch relied on it and got a 401 negotiate instead, because the
    /// statement never reached the circuit.
    /// </summary>
    [Fact]
    public async Task AllowAnonymous_at_the_map_site_wins_over_a_configured_policy()
    {
        await using var app = CreateApp(
            options => options.AuthorizationPolicy = "studio-policy",
            builder =>
            {
                AddTestAuthentication(builder);
                builder.Services.AddAuthorizationBuilder()
                    .AddPolicy("studio-policy", policy => policy.RequireAssertion(static _ => false));
            });

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using var client = app.GetTestClient();

            using var negotiate = await client.PostAsync(new Uri("/marten/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), content: null, Token);
            negotiate.StatusCode.Should().Be(HttpStatusCode.OK);

            using var page = await client.GetAsync(new Uri("/marten", UriKind.Relative), Token);
            page.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    /// <summary>
    /// The "served to anyone" fact the layout's red banner is drawn from, read off the finished endpoint
    /// metadata by the startup guard.
    /// </summary>
    [Fact]
    public async Task An_anonymous_mapping_is_observed_so_the_studio_can_say_so()
    {
        await using var app = CreateApp();
        app.MapMartenStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            app.Services.GetRequiredService<MartenStudioMappedEndpoints>().ServedAnonymously.Should().BeTrue();
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task An_authorized_mapping_is_not_reported_as_anonymous()
    {
        await using var app = CreateApp();
        app.MapMartenStudio().RequireAuthorization("studio-policy");
        await app.StartAsync(Token);
        try
        {
            app.Services.GetRequiredService<MartenStudioMappedEndpoints>().ServedAnonymously.Should().BeFalse();
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
