#region License
/*
 * Ported from Quartz.NET's Quartz.Dashboard (Apache-2.0, same author).
 *
 * All content copyright Marko Lahma, unless otherwise indicated. All rights reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy
 * of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations
 * under the License.
 *
 */
#endregion

using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using MartenStudio.Components;
using MartenStudio.Internal;

namespace MartenStudio;

/// <summary>
/// Maps Marten Studio's pages and assets into an application's routes.
/// </summary>
public static class MartenStudioEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps Marten Studio under the path <see cref="MartenStudioOptions.Path"/> was configured with,
    /// which defaults to <c>/marten</c>.
    /// </summary>
    /// <remarks>
    /// What comes back covers everything the studio mapped, so <c>RequireAuthorization()</c> or
    /// <c>AllowAnonymous()</c> on it is a statement about the studio rather than about half of it. Saying
    /// neither, and configuring neither <see cref="MartenStudioOptions.AuthorizationPolicy" /> nor a
    /// fallback policy, fails startup.
    /// </remarks>
    /// <param name="builder">The endpoint route builder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    public static IEndpointConventionBuilder MapMartenStudio(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return MapStandaloneStudio(builder);
    }

    /// <summary>
    /// Maps Marten Studio under the given route pattern.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Naming the path where the endpoints are mapped is how the rest of ASP.NET Core reads -
    /// <c>MapHealthChecks("/health")</c> - and it puts the route beside an application's other routes
    /// rather than in a registration callback somewhere else.
    /// </para>
    /// <para>
    /// The pattern given here wins over <see cref="MartenStudioOptions.Path"/>, however that was set, and
    /// is held to the same rule: a plain URL path starting with <c>/</c>, with no <c>{</c>, <c>}</c>,
    /// <c>?</c>, <c>#</c>, <c>.</c> or <c>..</c> segments and no empty ones.
    /// </para>
    /// </remarks>
    /// <param name="builder">The endpoint route builder.</param>
    /// <param name="pattern">The path the studio is served under, for example <c>/ops/marten</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pattern" /> is not a plain rooted URL path.</exception>
    public static IEndpointConventionBuilder MapMartenStudio(this IEndpointRouteBuilder builder, string pattern)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (string.IsNullOrWhiteSpace(pattern) || !pattern.StartsWith('/'))
        {
            throw new ArgumentException($"The route pattern is required and must start with '/', was '{pattern}'.", nameof(pattern));
        }

        if (!MartenStudioServiceCollectionExtensions.IsRoutableDashboardPath(pattern))
        {
            throw new ArgumentException(
                "The route pattern must be a simple URL path: it cannot contain '{', '}', '?', '#', '.' or '..' segments, or empty segments ('//').",
                nameof(pattern));
        }

        // Written onto the resolved options instance rather than kept locally: the studio's own components
        // read the same instance to build their links, their <base href> and their client-side route
        // matching, so a path that only the endpoints knew about would serve pages that navigate somewhere
        // else. Mutating a resolved options object after startup is normally a mistake; it is safe here
        // because MartenStudioOptions derives every path-shaped value through a cache keyed by the raw
        // Path string, so a reader either sees the old triple or the new one, never a mixture.
        MartenStudioOptions options = builder.ServiceProvider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;
        options.Path = pattern;

        return MapStandaloneStudio(builder);
    }

    private static MartenStudioConventionBuilder MapStandaloneStudio(IEndpointRouteBuilder builder)
    {
        builder.ServiceProvider.GetRequiredService<MartenStudioMappedEndpoints>().Track(builder);

        MartenStudioOptions options = builder.ServiceProvider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;
        string studioPath = options.TrimmedPath;

        // The pages already carry their compile-time /marten prefix in their @page directives, so the
        // components root is mapped at the application root and everything it produced is re-rooted below.
        RazorComponentsEndpointConventionBuilder components = builder
            .MapRazorComponents<MartenStudioApp>()
            .AddInteractiveServerRenderMode();

        // Every mount is studio-rooted, the default /marten included. MapRazorComponents registers the
        // Blazor circuit at the application's own /_blazor, so a host that has its own Blazor app - and
        // therefore its own MapRazorComponents - ends up with two endpoints on the same route and every
        // POST /_blazor/negotiate in the process becomes an AmbiguousMatchException. There is no variant
        // of "mount the studio and change nothing about the host" that leaves those at the root, so the
        // studio always takes its plumbing with it: its circuit, its opaque-redirect endpoint, its
        // framework script and its asset mirror all live under Path, and the shell renders a
        // studio-rooted <base href> so the browser asks for them there. It is also what a reverse proxy
        // that forwards only the studio prefix needs.
        components.Add(endpointBuilder => ReRootUnderStudioPath(endpointBuilder, studioPath));

        // The studio has no form, so it needs no antiforgery token - and requiring one would be a
        // middleware-ordering requirement on the host, which is the one thing registering the studio must
        // never impose. MapRazorComponents stamps IAntiforgeryMetadata that *requires* validation on every
        // page endpoint, and the framework then refuses to serve an endpoint carrying it when
        // UseAntiforgery() is not in the pipeline: an API-only host that mapped the studio got 500 for
        // GET /marten with "Endpoint … contains anti-forgery metadata, but a middleware was not found".
        // Saying it here rather than leaving it to the host keeps the mutations covered by what actually
        // covers them - every one of them is a Blazor event over the circuit, and SignalR's own
        // same-origin check is what stands between a cross-site page and that circuit. A host that does
        // call UseAntiforgery() is unaffected: the middleware simply has nothing to validate here.
        components.DisableAntiforgery();

        // Serve the studio's static web assets through endpoint routing as a fallback for hosts that do
        // not configure UseStaticFiles()/MapStaticAssets() (API-only projects, for instance). The root
        // copy stays for hosts that do not path-forward; the mirror under the studio path is what the
        // studio-rooted <base href> actually resolves to.
        List<IEndpointConventionBuilder> assetEndpoints =
        [
            MapStudioStaticAssets(builder, pathPrefix: string.Empty),
            MapStudioStaticAssets(builder, pathPrefix: studioPath),
            MapStudioFrameworkScript(builder, studioPath)
        ];

        if (!string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
        {
            string policyName = options.AuthorizationPolicy;

            // Gate the static assets with the same policy as the rest of the studio so the endpoints carry
            // explicit authorization metadata and keep working in applications that use a fail-closed
            // FallbackPolicy.
            foreach (IEndpointConventionBuilder assetEndpoint in assetEndpoints)
            {
                assetEndpoint.RequireAuthorization(policyName);
            }

            // The components builder holds only the studio's endpoints - its pages and its circuit - so
            // the policy covers them all, which is also what keeps /_blazor reachable under a fail-closed
            // FallbackPolicy.
            components.RequireAuthorization(policyName);
        }
        else
        {
            // Without a studio policy the studio adds no authorization of its own. The static asset
            // endpoints opt out so applications enforcing a fail-closed FallbackPolicy can still load the
            // studio's CSS and JS; the files are public package content.
            foreach (IEndpointConventionBuilder assetEndpoint in assetEndpoints)
            {
                assetEndpoint.AllowAnonymous();
            }

            // The pages and the circuit are deliberately left without metadata, so a host FallbackPolicy
            // or a RequireAuthorization() on the returned builder governs both. Stamping AllowAnonymous on
            // /_blazor here - which is what this used to do, to keep the circuit reachable under a
            // fail-closed FallbackPolicy - opened a circuit to anyone in every application that authorized
            // the studio with RequireAuthorization() on the builder instead of the option: the pages
            // answered 401 while POST /_blazor/negotiate answered 200, and a replayed prerender descriptor
            // then ran the studio's components against Marten. The circuit is reachable because whatever
            // the caller said about the studio now reaches it too (MartenStudioConventionBuilder).
        }

        // Says "Marten Studio mapped this", on the pages and on the circuit alike: both are the studio,
        // every remedy the failure message lists reaches both, and an unauthorized circuit is the more
        // dangerous of the two because it is the one that runs components. The static assets have already
        // decided for themselves above and are package content, so they are not the guard's business.
        MartenStudioEndpointMarker pageMarker = new(StudioSurface, StudioRemedies, isPage: true);
        MartenStudioEndpointMarker circuitMarker = new(StudioSurface, StudioRemedies, isPage: false);
        components.Add(endpointBuilder =>
            endpointBuilder.Metadata.Add(IsStudioPage(endpointBuilder) ? pageMarker : circuitMarker));

        return new MartenStudioConventionBuilder(components, hub: null);
    }

    /// <summary>
    /// Moves one endpoint of the studio's own <c>MapRazorComponents</c> call under
    /// <see cref="MartenStudioOptions.Path" />.
    /// </summary>
    /// <remarks>
    /// Three kinds of endpoint come out of that data source. The studio's pages carry their compile-time
    /// <c>/marten</c> prefix and are rebased onto the configured path. The SignalR circuit
    /// (<c>/_blazor</c> and its sub-routes) moves wholesale: dispatch does not depend on the route
    /// pattern. So does the enhanced-navigation <c>opaque-redirect</c> endpoint, whose handler reads the
    /// protected URL out of the query string and is equally path-independent - and whose emitted URL is
    /// document-relative, so the studio-rooted <c>&lt;base href&gt;</c> points at the moved copy. Anything
    /// else is left alone.
    /// </remarks>
    private static void ReRootUnderStudioPath(EndpointBuilder endpointBuilder, string studioPath)
    {
        if (endpointBuilder is not RouteEndpointBuilder routeEndpointBuilder)
        {
            return;
        }

        string? rawText = routeEndpointBuilder.RoutePattern.RawText;
        if (string.IsNullOrEmpty(rawText) || rawText[0] != '/')
        {
            return;
        }

        Type? componentType = GetComponentType(endpointBuilder);
        if (componentType is null)
        {
            if (IsUnder(rawText, BlazorCircuitPath) || IsUnder(rawText, OpaqueRedirectPath))
            {
                routeEndpointBuilder.RoutePattern = RoutePatternFactory.Parse(studioPath + rawText);
            }

            return;
        }

        if (componentType.Assembly != StudioAssembly || !IsUnder(rawText, MartenStudioOptions.DefaultPath))
        {
            return;
        }

        routeEndpointBuilder.RoutePattern = RoutePatternFactory.Parse(
            string.Concat(studioPath, rawText.AsSpan(MartenStudioOptions.DefaultPath.Length)));
    }

    /// <summary>Whether <paramref name="rawText" /> is <paramref name="prefix" /> or a route below it.</summary>
    private static bool IsUnder(string rawText, string prefix) =>
        rawText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (rawText.Length == prefix.Length || rawText[prefix.Length] == '/');

    private const string BlazorCircuitPath = "/_blazor";

    private const string OpaqueRedirectPath = "/_framework/opaque-redirect";

    private const string StudioSurface = "Marten Studio";

    private const string StudioRemedies = """
          - app.MapMartenStudio().RequireAuthorization() authorizes its pages and its Blazor circuit;
          - services.AddMartenStudio(options => options.AuthorizationPolicy = "...") authorizes those and the static assets with them;
          - app.MapMartenStudio().AllowAnonymous() serves it to anyone, deliberately.
        """;

    private static readonly Assembly StudioAssembly = typeof(MartenStudioApp).Assembly;

    private static bool IsStudioPage(EndpointBuilder endpointBuilder) =>
        GetComponentType(endpointBuilder)?.Assembly == StudioAssembly;

    private static Type? GetComponentType(EndpointBuilder endpointBuilder)
    {
        foreach (object metadata in endpointBuilder.Metadata)
        {
            if (metadata is ComponentTypeMetadata componentTypeMetadata)
            {
                return componentTypeMetadata.Type;
            }
        }

        return null;
    }

    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    private static readonly string[] GetAndHeadMethods = ["GET", "HEAD"];

    private static RouteHandlerBuilder MapStudioStaticAssets(IEndpointRouteBuilder builder, string pathPrefix)
    {
        string pattern = pathPrefix.Length == 0
            ? "_content/MartenStudio/{**path}"
            : pathPrefix + "/_content/MartenStudio/{**path}";

        return builder.MapMethods(pattern, GetAndHeadMethods, static async (HttpContext context, string path) =>
        {
            IWebHostEnvironment env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
            string assetPath = $"_content/MartenStudio/{path}";
            IFileInfo fileInfo = env.WebRootFileProvider.GetFileInfo(assetPath);
            if (!fileInfo.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!ContentTypeProvider.TryGetContentType(path, out string? contentType))
            {
                contentType = "application/octet-stream";
            }

            // Key the ETag cache by the resolved physical path (canonical, not the request path); a
            // non-physical provider yields null, which disables caching rather than caching under
            // attacker-controlled input.
            await WriteFileAsync(context, fileInfo, contentType, etagCacheKey: fileInfo.PhysicalPath).ConfigureAwait(false);
        }).ExcludeFromDescription();
    }

    /// <summary>
    /// Serves <c>blazor.web.js</c> at <c>{Path}/_framework/blazor.web.js</c>, which is where the shell's
    /// studio-rooted <c>&lt;base href&gt;</c> makes the browser ask for it.
    /// </summary>
    /// <remarks>
    /// Mapped for every mount, not only a custom path: the studio always serves its own script so that a
    /// host which never configured Blazor web assets still gets an interactive studio. Three tiers, in
    /// the order that keeps the host in charge — the host's own web root, then whatever endpoint the host
    /// serves the script from (<c>MapStaticAssets()</c>), and only then the copy this package ships. The
    /// host's copy is the one that matches the host's runtime, so it always wins where there is one; see
    /// the long comment in MartenStudio.csproj for why a copy is shipped at all.
    /// </remarks>
    private static IEndpointConventionBuilder MapStudioFrameworkScript(IEndpointRouteBuilder builder, string studioPath)
    {
        return builder.MapMethods(studioPath + FrameworkScriptPath, GetAndHeadMethods, static async (HttpContext context) =>
        {
            IWebHostEnvironment env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
            IFileInfo fileInfo = env.WebRootFileProvider.GetFileInfo(HostFrameworkScriptAssetPath);
            if (fileInfo.Exists)
            {
                await WriteFrameworkScriptAsync(context, fileInfo, HostFrameworkScriptAssetPath).ConfigureAwait(false);
                return;
            }

            // The host may serve it through its own static-asset endpoint rather than from the web root.
            if (await TryForwardToRootEndpointAsync(context, FrameworkScriptPath).ConfigureAwait(false))
            {
                return;
            }

            IFileInfo packaged = env.WebRootFileProvider.GetFileInfo(PackagedFrameworkScriptAssetPath);
            if (packaged.Exists)
            {
                await WriteFrameworkScriptAsync(context, packaged, PackagedFrameworkScriptAssetPath).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }).ExcludeFromDescription();
    }

    private const string FrameworkScriptPath = "/_framework/blazor.web.js";

    /// <summary>Where the host's own copy sits in its web root, when it has one.</summary>
    private const string HostFrameworkScriptAssetPath = "_framework/blazor.web.js";

    /// <summary>Where this package's copy sits, as a static web asset of the RCL.</summary>
    private const string PackagedFrameworkScriptAssetPath = "_content/MartenStudio/_framework/blazor.web.js";

    private static Task WriteFrameworkScriptAsync(HttpContext context, IFileInfo fileInfo, string logicalPath)
    {
        // Parity with the framework-owned script endpoint: revalidate rather than cache, so a runtime
        // upgrade is picked up without a hard refresh.
        context.Response.Headers.CacheControl = "no-cache";
        return WriteFileAsync(context, fileInfo, "text/javascript", etagCacheKey: fileInfo.PhysicalPath ?? logicalPath);
    }

    private static async Task<bool> TryForwardToRootEndpointAsync(HttpContext context, string rootPath)
    {
        EndpointDataSource dataSource = context.RequestServices.GetRequiredService<EndpointDataSource>();
        RouteEndpoint? rootEndpoint = null;
        foreach (Endpoint endpoint in dataSource.Endpoints)
        {
            if (endpoint is RouteEndpoint { RequestDelegate: not null } routeEndpoint)
            {
                string? rawText = routeEndpoint.RoutePattern.RawText;
                if (rawText is not null
                    && (rootPath.Equals(rawText, StringComparison.OrdinalIgnoreCase)
                        || (rawText.Length == rootPath.Length - 1 && rootPath.EndsWith(rawText, StringComparison.OrdinalIgnoreCase))))
                {
                    rootEndpoint = routeEndpoint;
                    break;
                }
            }
        }

        if (rootEndpoint is null)
        {
            return false;
        }

        // Handlers resolve content from the request path and the endpoint metadata, so both must describe
        // the root endpoint while its delegate runs.
        PathString originalPath = context.Request.Path;
        Endpoint? originalEndpoint = context.GetEndpoint();
        context.Request.Path = rootPath;
        context.SetEndpoint(rootEndpoint);
        try
        {
            await rootEndpoint.RequestDelegate!(context).ConfigureAwait(false);
        }
        finally
        {
            context.Request.Path = originalPath;
            context.SetEndpoint(originalEndpoint);
        }

        return true;
    }

    private static readonly ConcurrentDictionary<string, (long Length, DateTimeOffset LastModified, EntityTagHeaderValue ETag)> ETagCache = new();

    private static async Task WriteFileAsync(HttpContext context, IFileInfo fileInfo, string contentType, string? etagCacheKey)
    {
        // Stable validator so browsers can revalidate with If-None-Match and get a 304 instead of
        // re-downloading the body on every full page load.
        EntityTagHeaderValue etag = await GetETagAsync(fileInfo, etagCacheKey, context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.ETag = etag.ToString();

        foreach (EntityTagHeaderValue requestTag in context.Request.GetTypedHeaders().IfNoneMatch)
        {
            // RFC 9110: "*" matches any current representation
            if (EntityTagHeaderValue.Any.Equals(requestTag) || requestTag.Compare(etag, useStrongComparison: false))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
        }

        context.Response.ContentType = contentType;
        context.Response.ContentLength = fileInfo.Length;

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await context.Response.SendFileAsync(fileInfo, context.RequestAborted).ConfigureAwait(false);
    }

    private static async ValueTask<EntityTagHeaderValue> GetETagAsync(IFileInfo fileInfo, string? cacheKey, CancellationToken cancellationToken)
    {
        // The cache is keyed by a canonical identity supplied by the caller (the file's physical path, or
        // a fixed logical key) - never the request path, so an unauthenticated client cannot grow it
        // without bound by requesting the same file under many non-canonical path variants (casing,
        // duplicate slashes) that all resolve to one file. The ETag itself is content-derived so identical
        // files validate identically across machines and restarts; Length + LastModified only guard the
        // per-process cache entry.
        if (cacheKey is not null
            && ETagCache.TryGetValue(cacheKey, out (long Length, DateTimeOffset LastModified, EntityTagHeaderValue ETag) cached)
            && cached.Length == fileInfo.Length
            && cached.LastModified == fileInfo.LastModified)
        {
            return cached.ETag;
        }

        byte[] hash;
        Stream stream = fileInfo.CreateReadStream();
        await using (stream.ConfigureAwait(false))
        {
            hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        EntityTagHeaderValue etag = new($"\"{Convert.ToHexString(hash)}\"");
        if (cacheKey is not null)
        {
            ETagCache[cacheKey] = (fileInfo.Length, fileInfo.LastModified, etag);
        }

        return etag;
    }
}
