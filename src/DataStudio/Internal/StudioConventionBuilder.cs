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

using Microsoft.AspNetCore.Builder;

namespace DataStudio.Internal;

/// <summary>
/// What <c>MapDataStudio</c> hands back: everything the studio mapped, as one thing to say something
/// about.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint the studio's own <c>MapRazorComponents&lt;DataStudioApp&gt;()</c> produced — its
/// pages <em>and</em> the <c>/_blazor</c> circuit it registered — because the studio owns that root
/// component and therefore owns everything that call mapped. A <c>RequireAuthorization()</c> or an
/// <c>AllowAnonymous()</c> the caller wrote is a statement about <em>the studio</em>, and a statement
/// that reached the pages but not the circuit was the worst of both: the pages answered 401 while
/// <c>POST /_blazor/negotiate</c> answered 200, so a client could open a circuit, replay a prerender
/// descriptor and run the studio's components against Marten without ever passing the policy. It is a
/// narrower claim than "every endpoint of this builder is the studio's", which is what makes it safe:
/// the host's own Razor components live in the host's own builder.
/// </para>
/// <para>
/// The studio's static web assets and its framework-script mirror are deliberately <em>not</em> here.
/// They are separate route handlers, they carry no Marten data, and they have already decided for
/// themselves — the configured policy when there is one, <c>AllowAnonymous</c> when there is not — so
/// that a fail-closed <c>FallbackPolicy</c> cannot leave the studio without its stylesheet.
/// </para>
/// <para>
/// The <c>{Path}/_framework/{**file}</c> forward <em>is</em> here, in a slot of its own. It serves the
/// <em>host's</em> files, not the package's, so it cannot opt out of anything: with an
/// <c>AuthorizationPolicy</c> it carries the policy, and without one it takes whatever the caller says
/// about the studio (<c>RequireAuthorization()</c>, <c>AllowAnonymous()</c>) and, silent, the host's
/// <c>FallbackPolicy</c> — exactly what governs the page that imports those files. At a root mount there is no such endpoint.
/// </para>
/// <para>
/// There is no SignalR hub (D10): pages poll through a snapshot cache. If one is ever added it must arrive without changing what this method
/// returns, so that the one statement a caller made about the studio keeps covering all of it. The front page at the mount is a page like any
/// other, so it is in the components builder.
/// </para>
/// </remarks>
internal sealed class StudioConventionBuilder : IEndpointConventionBuilder
{
    private readonly IEndpointConventionBuilder components;
    private readonly IEndpointConventionBuilder? frameworkFiles;

    /// <param name="components">
    /// The Razor components builder the studio's pages and its Blazor circuit live in.
    /// </param>
    /// <param name="frameworkFiles">
    /// The forward of the host's own <c>_framework</c> files under the mount path: host-owned content, so it is governed
    /// by what the caller says about the studio and not left to opt out like the package's static assets. <see langword="null" />
    /// at a root mount (<c>Path = "/"</c>), where nothing is forwarded: the relative requests already reach the host's own files.
    /// </param>
    public StudioConventionBuilder(
        IEndpointConventionBuilder components,
        IEndpointConventionBuilder? frameworkFiles)
    {
        this.components = components;
        this.frameworkFiles = frameworkFiles;
    }

    public void Add(Action<EndpointBuilder> convention)
    {
        components.Add(convention);
        frameworkFiles?.Add(convention);
    }

    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        components.Finally(finallyConvention);
        frameworkFiles?.Finally(finallyConvention);
    }
}
