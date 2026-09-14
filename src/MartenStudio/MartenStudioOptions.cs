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

namespace MartenStudio;

/// <summary>
/// Options for Marten Studio, configured through <c>AddMartenStudio(Action&lt;MartenStudioOptions&gt;)</c>.
/// Validation runs at startup.
/// </summary>
public sealed class MartenStudioOptions
{
    internal const string DefaultPath = "/marten";

    /// <summary>
    /// The path the studio is mounted at. Must start with <c>/</c> and be a plain routable path (no route
    /// parameters, query or fragment). <c>MapMartenStudio(pattern)</c> overrides it.
    /// </summary>
    public string Path { get; set; } = DefaultPath;

    /// <summary>
    /// The title shown in the browser tab and the header.
    /// </summary>
    public string Title { get; set; } = "Marten Studio";

    /// <summary>
    /// When set, the policy applied to every studio endpoint: pages, the Blazor circuit and the static
    /// assets. When <see langword="null"/> the studio adds no policy of its own and refuses to start
    /// unless the mapping calls <c>RequireAuthorization()</c> or <c>AllowAnonymous()</c>, or the host has
    /// an <c>AuthorizationOptions.FallbackPolicy</c>.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>
    /// When set, evaluated against a <see cref="MartenStoreResource"/> (with a <see langword="null"/>
    /// capability) before a store, database or tenant is listed, framed or read.
    /// </summary>
    public string? StoreAuthorizationPolicy { get; set; }

    /// <summary>
    /// When set, evaluated against a <see cref="MartenStoreResource"/> carrying the capability name
    /// before every mutating operation. When <see langword="null"/>, <see cref="StoreAuthorizationPolicy"/>
    /// is used for writes as well.
    /// </summary>
    public string? WriteAuthorizationPolicy { get; set; }

    /// <summary>
    /// Master switch: when <see langword="true"/> every capability is off regardless of
    /// <see cref="Capabilities"/>, mutating controls are not rendered and the services refuse mutations.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Which mutating operations are enabled. All off by default.
    /// </summary>
    public MartenStudioCapabilities Capabilities { get; set; } = new();

    /// <summary>
    /// Rows per page when a list is first shown. Between 1 and <see cref="MaxPageSize"/>.
    /// </summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>
    /// The largest page size a user may pick. Between 1 and 5000.
    /// </summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>
    /// Statement timeout applied to every query the studio issues. Between one second and ten minutes.
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Documents larger than this are not fetched into list views and are shown as raw text only in the
    /// detail view.
    /// </summary>
    public int MaxInlineDocumentBytes { get; set; } = 512 * 1024;

    /// <summary>
    /// Row cap for the SQL console; the reader stops after this many rows. Between 1 and 10000.
    /// </summary>
    public int MaxSqlConsoleRows { get; set; } = 500;

    /// <summary>
    /// When set, the SQL console runs <c>SET LOCAL ROLE</c> to this Postgres role inside its read-only
    /// transaction, which is the only mechanism that narrows what the console can read. Must be a plain
    /// identifier.
    /// </summary>
    public string? SqlConsoleRole { get; set; }

    /// <summary>
    /// Above this estimated row count, document counts are reported as estimates from
    /// <c>pg_class.reltuples</c> rather than <c>count(*)</c>.
    /// </summary>
    public long ExactCountThreshold { get; set; } = 100_000;

    /// <summary>
    /// How often live pages poll for changes. Between one second and five minutes. Polling pauses while
    /// the tab is hidden.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional filter over the registered document types. Applied in the data layer, not only in
    /// navigation.
    /// </summary>
    public Func<Type, bool>? IsDocumentTypeVisible { get; set; }

    /// <summary>
    /// Whether stores registered with <c>AddMartenStore&lt;T&gt;()</c> are shown alongside the default
    /// <c>IDocumentStore</c>.
    /// </summary>
    public bool IncludeAncillaryStores { get; set; } = true;

    /// <summary>
    /// Tenant ids to offer in the tenant selector. When non-empty, discovery is skipped.
    /// </summary>
    public IList<string> KnownTenantIds { get; } = [];

    /// <summary>
    /// When <see cref="KnownTenantIds"/> is empty, whether to discover tenant ids from Marten's database
    /// descriptors and, failing that, a bounded query.
    /// </summary>
    public bool DiscoverTenantIds { get; set; } = true;

    /// <summary>
    /// <see cref="Path"/> normalized to a rooted path without a trailing slash, falling back to
    /// <see cref="DefaultPath"/> when unset or empty.
    /// </summary>
    internal string TrimmedPath => PathCache.Trimmed;

    /// <summary>
    /// Whether <see cref="Path"/> differs from the compile-time default "/marten".
    /// </summary>
    /// <remarks>
    /// A fact, and nothing behaves differently on it. Every mount is studio-rooted now — the studio
    /// always re-roots its own <c>/_blazor</c>, its framework script and its asset mirror under
    /// <see cref="Path"/> and always renders a studio-rooted <c>&lt;base href&gt;</c> — because a
    /// default mount that left a second <c>/_blazor</c> at the application root was an
    /// <c>AmbiguousMatchException</c> in any host with a Blazor app of its own, and because two shapes
    /// meant the shape nobody runs locally was the one that broke in production.
    /// </remarks>
    internal bool HasCustomPath => PathCache.HasCustom;

    /// <summary>
    /// <see cref="TrimmedPath"/> in its percent-encoded form, as browsers emit it in
    /// request URIs and the &lt;base href&gt;. Server-side route patterns keep the raw
    /// form (route matching compares decoded values); client-side URI comparisons need this one.
    /// </summary>
    internal string EscapedPath => PathCache.Escaped;

    private DerivedPath? pathCache;

    /// <summary>
    /// Values derived from <see cref="Path"/>, computed once and reused — they are read
    /// on Blazor render hot paths (links, route matching) while the option itself only changes
    /// during startup configuration. Held behind a single reference so a concurrent reader always
    /// observes a fully-populated instance (reference reads/writes are atomic) even if the option
    /// is mutated mid-render.
    /// </summary>
    private DerivedPath PathCache
    {
        get
        {
            string source = Path;
            DerivedPath? cache = pathCache;
            if (cache is null || !string.Equals(cache.Source, source, StringComparison.Ordinal))
            {
                string trimmed = DefaultPath;
                if (!string.IsNullOrWhiteSpace(source))
                {
                    string candidate = source.Trim().Trim('/');
                    if (candidate.Length > 0)
                    {
                        trimmed = "/" + candidate;
                    }
                }

                string escaped = new Uri("http://localhost" + trimmed).AbsolutePath;
                bool hasCustom = !string.Equals(trimmed, DefaultPath, StringComparison.OrdinalIgnoreCase);
                cache = new DerivedPath(source, trimmed, escaped, hasCustom);
                pathCache = cache;
            }

            return cache;
        }
    }

    private sealed record DerivedPath(string Source, string Trimmed, string Escaped, bool HasCustom);
}
