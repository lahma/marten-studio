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

using Microsoft.AspNetCore.Authorization;
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
        bool hasCustomPath = options.HasCustomPath;

        // The pages already carry their compile-time /marten prefix in their @page directives, so the
        // components root is mapped at the application root and re-rooted below when the path is custom.
        RazorComponentsEndpointConventionBuilder components = builder
            .MapRazorComponents<MartenStudioApp>()
            .AddInteractiveServerRenderMode();

        if (hasCustomPath)
        {
            // Re-root the studio page endpoints from the compile-time /marten prefix to the configured
            // path so the initial page load and enhanced navigations resolve. Interactive navigation is
            // handled by the studio's own route matching in Routes.razor.
            components.Add(endpointBuilder =>
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
                    // The /_blazor circuit endpoints move under the studio path so a reverse proxy that
                    // forwards only the studio prefix can reach them; SignalR dispatch does not depend on
                    // the route pattern. Other framework-owned endpoints stay put: the runtime registers
                    // the blazor.web.js script endpoint through this data source and it serves content
                    // keyed by its original path, so re-rooting it breaks it - the studio maps its own
                    // copy under the studio path instead.
                    if (rawText == "/_blazor" || rawText.StartsWith("/_blazor/", StringComparison.Ordinal))
                    {
                        routeEndpointBuilder.RoutePattern = RoutePatternFactory.Parse(studioPath + rawText);
                    }

                    return;
                }

                if (componentType.Assembly != StudioAssembly
                    || !rawText.StartsWith(MartenStudioOptions.DefaultPath, StringComparison.OrdinalIgnoreCase)
                    || (rawText.Length > MartenStudioOptions.DefaultPath.Length && rawText[MartenStudioOptions.DefaultPath.Length] != '/'))
                {
                    return;
                }

                routeEndpointBuilder.RoutePattern = RoutePatternFactory.Parse(
                    string.Concat(studioPath, rawText.AsSpan(MartenStudioOptions.DefaultPath.Length)));
            });
        }

        // Serve the studio's static web assets through endpoint routing as a fallback for hosts that do
        // not configure UseStaticFiles()/MapStaticAssets() (API-only projects, for instance).
        List<IEndpointConventionBuilder> assetEndpoints =
        [
            MapStudioStaticAssets(builder, pathPrefix: string.Empty)
        ];

        if (hasCustomPath)
        {
            // With a custom path the shell renders a studio-rooted <base href>, so the browser requests
            // the static assets and the Blazor framework plumbing under the studio path. Mirror them
            // there so a reverse proxy that forwards only the studio prefix can reach them; the root
            // asset endpoint stays for hosts that do not path-forward.
            assetEndpoints.Add(MapStudioStaticAssets(builder, pathPrefix: studioPath));
            assetEndpoints.Add(MapStudioFrameworkScript(builder, studioPath));
            assetEndpoints.Add(MapStudioOpaqueRedirect(builder, studioPath));
        }

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

            // Standalone: the components builder holds only the studio's endpoints - its pages, the
            // framework script and the circuit endpoints - so the policy can cover them all, which is also
            // what keeps /_framework and /_blazor reachable under a fail-closed FallbackPolicy.
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

            // The non-page endpoints (/_blazor) are marked anonymous so the circuit stays reachable under
            // a fail-closed FallbackPolicy. The pages are deliberately left without metadata so they stay
            // governed by the host's own policies and no Marten data is silently exposed to anonymous
            // users.
            components.Add(static endpointBuilder =>
            {
                if (GetComponentType(endpointBuilder) is null)
                {
                    endpointBuilder.Metadata.Add(new AllowAnonymousAttribute());
                }
            });
        }

        // Says "Marten Studio mapped this" on the endpoints that answer with Marten data - the pages. The
        // static assets have already decided for themselves above, and the Blazor circuit is plumbing that
        // carries nothing on its own, so neither is the guard's business.
        MartenStudioEndpointMarker marker = new(StudioSurface, StudioRemedies);
        components.Add(endpointBuilder =>
        {
            if (IsStudioPage(endpointBuilder))
            {
                endpointBuilder.Metadata.Add(marker);
            }
        });

        return new MartenStudioConventionBuilder(components, IsStudioPage, hub: null);
    }

    private const string StudioSurface = "Marten Studio";

    private const string StudioRemedies = """
          - app.MapMartenStudio().RequireAuthorization() authorizes its pages;
          - services.AddMartenStudio(options => options.AuthorizationPolicy = "...") authorizes those and the static assets and the Blazor circuit with them;
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

    private static IEndpointConventionBuilder MapStudioFrameworkScript(IEndpointRouteBuilder builder, string studioPath)
    {
        const string scriptPath = "/_framework/blazor.web.js";
        return builder.MapMethods(studioPath + scriptPath, GetAndHeadMethods, static async (HttpContext context) =>
        {
            IWebHostEnvironment env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
            IFileInfo fileInfo = env.WebRootFileProvider.GetFileInfo("_framework/blazor.web.js");
            if (fileInfo.Exists)
            {
                // parity with the framework-owned script endpoint
                context.Response.Headers.CacheControl = "no-cache";
                await WriteFileAsync(context, fileInfo, "text/javascript", etagCacheKey: fileInfo.PhysicalPath ?? "_framework/blazor.web.js").ConfigureAwait(false);
                return;
            }

            // On .NET 10 the script is a static web asset served through the framework's own endpoint
            // rather than from the web root, so forward to it. The .NET 8/9 ManifestEmbeddedFileProvider
            // tier the Quartz original carried is gone: this package targets net10.0 only.
            if (!await TryForwardToRootEndpointAsync(context, scriptPath).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }).ExcludeFromDescription();
    }

    private static IEndpointConventionBuilder MapStudioOpaqueRedirect(IEndpointRouteBuilder builder, string studioPath)
    {
        // The framework emits enhanced-navigation redirects as URLs relative to the document base, which
        // the studio-rooted <base href> resolves under the studio path; forward those requests to the
        // framework-owned root endpoint so the redirect flow completes.
        const string redirectPath = "/_framework/opaque-redirect";
        return builder.MapGet(studioPath + redirectPath, static async (HttpContext context) =>
        {
            if (!await TryForwardToRootEndpointAsync(context, redirectPath).ConfigureAwait(false))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }).ExcludeFromDescription();
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
