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

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MartenStudio.Components;
using MartenStudio.Internal;
using MartenStudio.Services.Settings;

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

        return StudioEndpointMapper.Map<MartenStudioApp>(builder);
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

        if (!StudioPath.IsRoutable(pattern))
        {
            throw new ArgumentException(
                "The route pattern must be a simple URL path: it cannot contain '{', '}', '?', '#', '.' or '..' segments, or empty segments ('//').",
                nameof(pattern));
        }

        // Written onto the resolved options instance rather than kept locally: the studio's own components
        // read the same instance - through the live IStudioSettings adapter - to build their links, their
        // <base href> and their client-side route matching, so a path that only the endpoints knew about would
        // serve pages that navigate somewhere else. Mutating a resolved options object after startup is
        // normally a mistake; it is safe here because MartenStudioOptions derives every path-shaped value
        // through a cache keyed by the raw Path string, so a reader either sees the old triple or the new one,
        // never a mixture.
        MartenStudioOptions options = builder.ServiceProvider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;
        options.Path = pattern;

        return StudioEndpointMapper.Map<MartenStudioApp>(builder);
    }
}
