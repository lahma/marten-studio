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
    /// When set, the SQL console <em>and</em> the Query page's Marten <c>where</c> clause run
    /// <c>SET LOCAL ROLE</c> to this Postgres role inside their read-only transaction, which is the only
    /// mechanism that narrows what either can read. Must be a plain identifier, and it must be able to
    /// <c>select</c> from the document tables, or the <c>where</c> clause - which needs no capability -
    /// answers <c>42501</c> for everybody.
    /// </summary>
    public string? SqlConsoleRole { get; set; }

    /// <summary>
    /// Above this row count, the studio reports the <c>pg_class.reltuples</c> estimate and declines to run
    /// <c>count(*)</c> — including when a user asks for the exact number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It governs every exact count the documents browser would take: the collections rail's automatic
    /// upgrade for a table Postgres has never analysed, the number in the list header, and the "=" button
    /// beside a collection. Above the threshold each of those comes back as an estimate carrying "estimate
    /// only, exact count refused above N" — a value the page renders, not an error — because an exact
    /// <c>count(*)</c> on a large collection is a sequential scan, and a button that starts one is the
    /// denial of service D8 exists to prevent.
    /// </para>
    /// <para>
    /// A table Postgres has never analysed reports <c>reltuples = -1</c> and so has no estimate to compare.
    /// The studio settles that case with <c>select 1 from … offset N limit 1</c>, whose cost is bounded by
    /// this number rather than by the collection, and counts only when the probe comes back empty.
    /// </para>
    /// <para>
    /// Zero means "never count, always estimate"; the default is a hundred thousand rows, at which an exact
    /// count costs a fraction of a second. It cannot be negative.
    /// </para>
    /// </remarks>
    public long ExactCountThreshold { get; set; } = 100_000;

    /// <summary>
    /// How often live pages poll for changes. Between one second and five minutes. Polling pauses while
    /// the tab is hidden.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The per-shard replay timeout handed to Marten's rebuild. At least one minute.
    /// </summary>
    /// <remarks>
    /// A rebuild tears the projection's tables down <em>before</em> this timeout starts applying to the
    /// replay, so a rebuild that exceeds it stops with the projection's tables already emptied and the
    /// operation recorded as failed. Set it generously: the default is one hour, where Marten's own
    /// no-timeout overload would have silently used five minutes.
    /// </remarks>
    public TimeSpan RebuildShardTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Optional filter over the store's <b>root</b> document types. Applied in the data layer, not only in
    /// navigation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Roots only, and a subclass cannot be hidden on its own.</b> The delegate is asked about each type
    /// in <c>StoreOptions.AllKnownDocumentTypes()</c>, which is the registered roots — a hierarchy appears
    /// once, as its root. A subclass shares the root's table and is told apart only by <c>mt_doc_type</c>,
    /// so "hide <c>Car</c> but show <c>Vehicle</c>" is not a thing this can do: hiding the root hides the
    /// whole table, and showing it shows every row in it.
    /// </para>
    /// <para>
    /// It is a gate rather than a display filter. A hidden type's table is also kept out of the discovered
    /// band, out of every list and out of every detail page — otherwise the same rows came back as
    /// "Discovered (unregistered)" with a row count and a detail page, which is the data the delegate was
    /// set to keep off the screen.
    /// </para>
    /// <para>
    /// It is called more than once per page. Answer from the type alone and answer the same way every
    /// time; a delegate that consults mutable state can hand the rail one set of collections and the
    /// counts another.
    /// </para>
    /// </remarks>
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
