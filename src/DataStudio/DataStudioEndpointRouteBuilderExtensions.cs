using DataStudio.Components;
using DataStudio.Internal;
using DataStudio.Services.Settings;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataStudio;

/// <summary>
/// Maps the Data Studio shell's pages and assets into an application's routes.
/// </summary>
public static class DataStudioEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps Data Studio, with every module added to it, under the path <see cref="DataStudioOptions.Path" /> was configured with, which
    /// defaults to <c>/studio</c>.
    /// </summary>
    /// <remarks>
    /// What comes back covers everything the studio mapped, so <c>RequireAuthorization()</c> or <c>AllowAnonymous()</c> on it is a statement about
    /// the studio rather than about half of it. Saying neither, and configuring neither <see cref="DataStudioOptions.AuthorizationPolicy" /> nor a
    /// fallback policy, fails startup.
    /// </remarks>
    /// <param name="builder">The endpoint route builder.</param>
    /// <returns>A convention builder that covers every endpoint the studio mapped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <exception cref="InvalidOperationException"><c>AddDataStudio()</c> was not called, or no module was added to it.</exception>
    public static IEndpointConventionBuilder MapDataStudio(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        StudioEndpointMapper.RequireRegistered(builder);

        return StudioEndpointMapper.Map<DataStudioApp>(builder);
    }

    /// <summary>
    /// Maps Data Studio under the given route pattern.
    /// </summary>
    /// <remarks>
    /// The pattern given here wins over <see cref="DataStudioOptions.Path" />, however that was set, and is held to the same rule: a plain URL path
    /// starting with <c>/</c>, with no <c>{</c>, <c>}</c>, <c>?</c>, <c>#</c>, <c>.</c> or <c>..</c> segments and no empty ones.
    /// </remarks>
    /// <param name="builder">The endpoint route builder.</param>
    /// <param name="pattern">The path the studio is served under, for example <c>/admin</c>.</param>
    /// <returns>A convention builder that covers every endpoint the studio mapped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pattern" /> is not a plain rooted URL path.</exception>
    /// <exception cref="InvalidOperationException"><c>AddDataStudio()</c> was not called, or no module was added to it.</exception>
    public static IEndpointConventionBuilder MapDataStudio(this IEndpointRouteBuilder builder, string pattern)
    {
        ArgumentNullException.ThrowIfNull(builder);

        StudioEndpointMapper.RequireRegistered(builder);

        if (string.IsNullOrWhiteSpace(pattern) || !pattern.StartsWith('/'))
        {
            throw new ArgumentException($"The route pattern is required and must start with '/', was '{pattern}'.", nameof(pattern));
        }

        if (!StudioPath.IsRoutable(pattern))
        {
            throw new ArgumentException(
                "The route pattern must be a simple URL path: it cannot contain '{', '}', '?', '#', '.' or '..' segments, or empty segments ('//').",
                nameof(pattern));
        }

        // Written onto the resolved options instance rather than kept locally: the studio's own components read the same instance - through the
        // live settings adapter - to build their links, their <base href> and their client-side route matching. Safe after startup because every
        // path-shaped value is derived through a cache keyed by the raw Path string, so a reader sees the old triple or the new one, never a mixture.
        DataStudioOptions options = builder.ServiceProvider.GetRequiredService<IOptions<DataStudioOptions>>().Value;
        options.Path = pattern;

        return StudioEndpointMapper.Map<DataStudioApp>(builder);
    }
}
