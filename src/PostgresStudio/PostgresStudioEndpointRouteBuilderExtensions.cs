using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PostgresStudio.Components;
using PostgresStudio.Internal;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio;

/// <summary>
/// Maps Postgres Studio's pages and assets into an application's routes.
/// </summary>
public static class PostgresStudioEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps Postgres Studio under the path <see cref="PostgresStudioOptions.Path" /> was configured with, which defaults
    /// to <c>/postgres</c>.
    /// </summary>
    /// <remarks>
    /// What comes back covers everything the studio mapped, so <c>RequireAuthorization()</c> or <c>AllowAnonymous()</c>
    /// on it is a statement about the studio rather than about half of it. Saying neither, and configuring neither
    /// <see cref="PostgresStudioOptions.AuthorizationPolicy" /> nor a fallback policy, fails startup.
    /// </remarks>
    /// <param name="builder">The endpoint route builder.</param>
    /// <returns>A convention builder that covers every endpoint the studio mapped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The application registered Marten Studio, not Postgres Studio.</exception>
    public static IEndpointConventionBuilder MapPostgresStudio(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        StudioEndpointMapper.RequireRegistered(builder, PostgresStudioDescriptor.Marker, "MapPostgresStudio()");

        return StudioEndpointMapper.Map<PostgresStudioApp>(builder);
    }

    /// <summary>
    /// Maps Postgres Studio under the given route pattern.
    /// </summary>
    /// <remarks>
    /// The pattern given here wins over <see cref="PostgresStudioOptions.Path" />, however that was set, and is held to
    /// the same rule: a plain URL path starting with <c>/</c>, with no <c>{</c>, <c>}</c>, <c>?</c>, <c>#</c>, <c>.</c> or
    /// <c>..</c> segments and no empty ones.
    /// </remarks>
    /// <param name="builder">The endpoint route builder.</param>
    /// <param name="pattern">The path the studio is served under, for example <c>/ops/db</c>.</param>
    /// <returns>A convention builder that covers every endpoint the studio mapped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="pattern" /> is not a plain rooted URL path.</exception>
    /// <exception cref="InvalidOperationException">The application registered Marten Studio, not Postgres Studio.</exception>
    public static IEndpointConventionBuilder MapPostgresStudio(this IEndpointRouteBuilder builder, string pattern)
    {
        ArgumentNullException.ThrowIfNull(builder);

        StudioEndpointMapper.RequireRegistered(builder, PostgresStudioDescriptor.Marker, "MapPostgresStudio()");

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

        // Written onto the resolved options instance rather than kept locally: the studio's own components read the same
        // instance - through the live IStudioSettings adapter - to build their links, their <base href> and their
        // client-side route matching. Safe after startup because every path-shaped value is derived through a cache
        // keyed by the raw Path string, so a reader sees the old triple or the new one, never a mixture.
        PostgresStudioOptions options = builder.ServiceProvider.GetRequiredService<IOptions<PostgresStudioOptions>>().Value;
        options.Path = pattern;

        return StudioEndpointMapper.Map<PostgresStudioApp>(builder);
    }
}
