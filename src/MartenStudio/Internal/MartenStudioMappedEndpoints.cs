#region License

/*
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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MartenStudio.Internal;

/// <summary>
/// Where the startup guard reads endpoints from: the route builder <c>MapMartenStudio</c> was called
/// on.
/// </summary>
/// <remarks>
/// <para>
/// Not the container's <see cref="EndpointDataSource" />, although that is the obvious place to look. It
/// is a composite over the data sources of the route builder the middleware pipeline owns, and that
/// pipeline is built inside the web host's <c>StartAsync</c> — after every hosted service's
/// <c>StartingAsync</c> has run. Read there, it answers zero endpoints and the guard would pass
/// everything in silence. The collection a <c>Map</c> call was made on is populated as the call is made,
/// which for a <c>WebApplication</c> is before the host is started at all.
/// </para>
/// <para>
/// The collections are held rather than their contents: an <see cref="EndpointDataSource" /> builds its
/// endpoints when it is enumerated, so what is captured here is where to look and the looking happens at
/// startup.
/// </para>
/// </remarks>
internal sealed class MartenStudioMappedEndpoints
{
    private readonly List<ICollection<EndpointDataSource>> sources = [];
    private readonly Lock gate = new();

    /// <summary>
    /// Remembers where <paramref name="builder" /> collects its endpoints.
    /// </summary>
    /// <remarks>
    /// A <see cref="RouteGroupBuilder" /> is passed over. Its own data sources answer with the endpoints
    /// as they were mapped — before the group's prefix and the group's conventions, and
    /// <c>MapGroup("/ops").RequireAuthorization()</c> is one of those conventions — so reading them here
    /// would refuse a mapping the application had authorized. The application's own builder holds the
    /// group as a single data source that answers with the finished endpoints, and that is what the
    /// container's <see cref="EndpointDataSource" /> is composed of by the time the host has started.
    /// </remarks>
    public void Track(IEndpointRouteBuilder builder)
    {
        if (builder is RouteGroupBuilder)
        {
            return;
        }

        ICollection<EndpointDataSource> dataSources = builder.DataSources;
        lock (gate)
        {
            foreach (ICollection<EndpointDataSource> tracked in sources)
            {
                if (ReferenceEquals(tracked, dataSources))
                {
                    return;
                }
            }

            sources.Add(dataSources);
        }
    }

    /// <summary>
    /// Whether the studio's pages are served to anyone: they carry <see cref="IAllowAnonymous" />, so
    /// nothing about the visitor is ever checked before Marten data is rendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deliberate state — <c>MapMartenStudio().AllowAnonymous()</c> is one of the three things the
    /// startup guard accepts — and one a person looking at the page has to be told about, because the
    /// studio looks exactly the same either way. The layout draws a banner from this and the sample's
    /// landing page says the same thing, so <c>--anonymous</c> can never be mistaken for a demo that
    /// happened to skip the login.
    /// </para>
    /// <para>
    /// Observed from the finished endpoint metadata rather than from the options, because it is a fact
    /// about what the mapping ended up saying: the caller's <c>AllowAnonymous()</c>, a group above it, or
    /// nothing at all. Sticky once seen, since the mapping may only be finished by the time the host has
    /// started (a <c>MapGroup</c>, or a <c>Startup.Configure</c> that maps inside the pipeline).
    /// </para>
    /// </remarks>
    public bool ServedAnonymously => servedAnonymously;

    private volatile bool servedAnonymously;

    /// <summary>
    /// Reads <see cref="ServedAnonymously" /> off the endpoints the startup guard has just built.
    /// </summary>
    internal void ObserveAnonymousPages(List<Endpoint> endpoints)
    {
        if (servedAnonymously)
        {
            return;
        }

        foreach (Endpoint endpoint in endpoints)
        {
            if (endpoint.Metadata.GetMetadata<MartenStudioEndpointMarker>() is { IsPage: true }
                && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                servedAnonymously = true;
                return;
            }
        }
    }

    /// <summary>
    /// Every endpoint reachable from a tracked route builder, built as this is read.
    /// </summary>
    public List<Endpoint> Endpoints()
    {
        ICollection<EndpointDataSource>[] snapshot;
        lock (gate)
        {
            snapshot = sources.ToArray();
        }

        List<Endpoint> endpoints = [];
        foreach (ICollection<EndpointDataSource> dataSources in snapshot)
        {
            foreach (EndpointDataSource dataSource in dataSources.ToArray())
            {
                endpoints.AddRange(dataSource.Endpoints);
            }
        }

        return endpoints;
    }
}
