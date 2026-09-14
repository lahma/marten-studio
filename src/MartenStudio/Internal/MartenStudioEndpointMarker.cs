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

namespace MartenStudio.Internal;

/// <summary>
/// Endpoint metadata saying "Marten Studio mapped this", so <see cref="MartenStudioEndpointAuthorizationGuard" /> can
/// tell the endpoints Marten Studio added from the application's own.
/// </summary>
/// <remarks>
/// A marker rather than a route-pattern comparison: both surfaces can be served under a path the
/// application chose, the studio's pages come out of a Razor components data source it does not own
/// outright, and an application is free to map its own routes under the same prefix. What Marten Studio
/// put there is the only thing Marten Studio has any business refusing to start over.
/// </remarks>
internal sealed class MartenStudioEndpointMarker
{
    /// <param name="surface">What this endpoint is part of, named the way the failure message names it.</param>
    /// <param name="remedies">The ways to say what this surface means, listed in the failure message.</param>
    /// <param name="isPage">
    /// Whether this is one of the studio's routable pages rather than its Blazor circuit. Both are the
    /// studio and both are the guard's business; the distinction is for the "served to anyone" banner,
    /// which is a statement about what a person can open in a browser.
    /// </param>
    /// <param name="authorizedByOptions">
    /// Whether a Marten Studio option has already stated an authorization rule for this endpoint that is
    /// enforced elsewhere rather than by <c>IAuthorizeData</c> metadata, so it is invisible to the
    /// metadata check and has to be said here.
    /// </param>
    public MartenStudioEndpointMarker(string surface, string remedies, bool isPage, bool authorizedByOptions = false)
    {
        Surface = surface;
        Remedies = remedies;
        IsPage = isPage;
        AuthorizedByOptions = authorizedByOptions;
    }

    /// <summary>
    /// Whether this endpoint is a routable studio page rather than the Blazor circuit behind it.
    /// </summary>
    public bool IsPage { get; }

    /// <summary>
    /// What this endpoint is part of — "the studio".
    /// </summary>
    public string Surface { get; }

    /// <summary>
    /// The ways to state an intent for this surface, listed verbatim in the startup failure.
    /// </summary>
    public string Remedies { get; }

    /// <summary>
    /// Whether Marten Studio's own options already authorize this endpoint.
    /// </summary>
    public bool AuthorizedByOptions { get; }
}
