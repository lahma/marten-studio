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

namespace MartenStudio;

/// <summary>
/// What <c>MapMartenStudio</c> hands back: everything the studio mapped, as one thing to say something
/// about.
/// </summary>
/// <remarks>
/// <para>
/// Not the Razor components builder itself. A convention put on that builder reaches every endpoint it
/// owns, and a <c>RequireAuthorization()</c> or an <c>AllowAnonymous()</c> the caller wrote is a
/// statement about <em>the studio</em> — so it is applied only to the endpoints whose component comes
/// out of this assembly. The filter costs one metadata scan per convention and is cheap insurance
/// against the day the builder holds something that is not the studio's.
/// </para>
/// <para>
/// The hub slot is empty in v1 and is kept for a reason: pages poll through a snapshot cache instead
/// (D10), and if a hub is ever added it must arrive without changing what this method returns, so that
/// the one statement a caller made about the studio keeps covering all of it.
/// </para>
/// </remarks>
internal sealed class MartenStudioConventionBuilder : IEndpointConventionBuilder
{
    private readonly IEndpointConventionBuilder components;
    private readonly Func<EndpointBuilder, bool>? componentFilter;
    private readonly IEndpointConventionBuilder? hub;

    /// <param name="components">The Razor components builder the studio pages live in.</param>
    /// <param name="componentFilter">
    /// Which of that builder's endpoints a convention reaches, or <see langword="null" /> for all of
    /// them.
    /// </param>
    /// <param name="hub">
    /// The studio's live-events hub, or <see langword="null" /> when there is none. There is none in v1:
    /// pages poll through a snapshot cache rather than opening a second host-visible endpoint (D10), and
    /// the slot is kept because adding the hub later must not change what <c>MapMartenStudio</c> returns.
    /// </param>
    public MartenStudioConventionBuilder(
        IEndpointConventionBuilder components,
        Func<EndpointBuilder, bool>? componentFilter,
        IEndpointConventionBuilder? hub)
    {
        this.components = components;
        this.componentFilter = componentFilter;
        this.hub = hub;
    }

    public void Add(Action<EndpointBuilder> convention)
    {
        components.Add(Restrict(convention));
        hub?.Add(convention);
    }

    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        components.Finally(Restrict(finallyConvention));
        hub?.Finally(finallyConvention);
    }

    private Action<EndpointBuilder> Restrict(Action<EndpointBuilder> convention)
    {
        if (componentFilter is null)
        {
            return convention;
        }

        Func<EndpointBuilder, bool> filter = componentFilter;
        return endpointBuilder =>
        {
            if (filter(endpointBuilder))
            {
                convention(endpointBuilder);
            }
        };
    }
}
