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

using System.Collections.Frozen;

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
    /// The most connections the studio holds at a time of each pool it reads through - each of the application's own
    /// <c>NpgsqlDataSource</c>s (issue #44). Between 1 and 100; 2 by default, because the studio is there to watch a database
    /// under load, not to add to it. A read that finds every one of them in use waits for one, at most <c>QueryTimeout</c>,
    /// and then says so; a read that is part of one already holding a connection - a nested read - never waits for itself.
    /// The health advisor's C01 says how many of a saturated pool's connections were the studio's. The cap is never above the data
    /// source's own <c>Maximum Pool Size</c> (issue #49): it is the smaller of the two, read from the data source's connection string
    /// when <c>Pooling</c> is on, so a host never copies a pool's size into this option. A data source the connection history
    /// samples through keeps one more connection for it outside the cap, so a dedicated pool for both needs one more.
    /// </summary>
    public int MaxStudioConnections { get; set; } = 2;

    /// <summary>
    /// The <c>application_name</c> the studio's sessions carry in <c>pg_stat_activity</c>, Database › Sessions and an external
    /// monitor, so that the studio's load is never mistaken for the application's (issue #44). <see langword="null" /> - the
    /// default - is the application's own name from its connection string with the studio's after it in parentheses
    /// (<c>orders-api (Marten Studio)</c>), or the studio's alone; an empty string leaves the application's own name. It is set for
    /// each of the studio's read-only transactions only (<c>set_config(…, true)</c>), so a pooled connection goes back to the
    /// application as it came. Printable ASCII, at most 63 characters: what PostgreSQL keeps as written.
    /// </summary>
    public string? StudioApplicationName { get; set; }

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
    /// How often the health advisor re-checks a database while somebody has its Health page or the
    /// Overview open. Between fifteen seconds and one hour; a <see cref="RefreshInterval"/> longer than it
    /// wins, so the advisor never checks more often than the pages poll.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the operators' setting for what the advisor costs the database it watches. Nothing is checked
    /// while no page is open: a check runs when a page asks, and every open page and every circuit shares
    /// the one result for a database until this much time has passed, so ten open tabs cost one check per
    /// interval rather than ten. The default is a minute, because several of the checks read
    /// <c>pg_locks</c> and <c>pg_stat_activity</c>, which scan the server's lock table and process array.
    /// </para>
    /// <para>
    /// The deeper checks behind "Run deeper checks" are cached for five minutes whatever this says.
    /// </para>
    /// </remarks>
    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The cadence the advisor really runs at: <see cref="HealthInterval"/>, or <see cref="RefreshInterval"/>
    /// when that is longer. A clamp rather than a startup error, so a host that already polls slowly is not
    /// refused a start by an option it never set.
    /// </summary>
    internal TimeSpan EffectiveHealthInterval => HealthInterval >= RefreshInterval ? HealthInterval : RefreshInterval;

    private readonly Dictionary<string, string> acceptedChecks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Health checks this application has decided to live with, each by its code (<c>"K05"</c>) with the reason, in the host's
    /// own words. The check still runs and its finding is still shown - as <em>Accepted</em>, muted, with the reason beside it
    /// - but it is out of the Warning and Critical counts, the Health page's state, the navigation badge, the banners and
    /// the ASP.NET Core health check's status; and <c>PostgresFinding.Accepted</c> says so for host code that reads the
    /// diagnostics API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Meant for findings about configuration the host owns and has chosen - K05, the SQL console reading as the store's own
    /// login on a managed database whose roles belong to another team - which carry the same evidence every time. A finding
    /// that moves with the data (projection lag, vacuum, storage) is not one to accept: it would stop counting the day it
    /// matters.
    /// </para>
    /// <para>
    /// The check's row on the Health page shows the reason only beside a finding the visitor may see. The <em>Accepted
    /// checks</em> list on the Server page (and on Marten Studio's Configuration page) is configuration, shown to anyone who
    /// may open that page, so a reason should hold nothing you would not show them.
    /// </para>
    /// <para>
    /// Keys are compared ignoring case. The entries are read once, at startup (a frozen copy is taken when options validation runs):
    /// add them in the <c>AddMartenStudio</c> callback, because a change made afterwards is not seen. Validation at startup refuses a key that is not a check of this studio, and a reason
    /// that is empty, whitespace or longer than 500 characters.
    /// </para>
    /// </remarks>
    public IDictionary<string, string> AcceptedChecks => acceptedChecks;

    private FrozenDictionary<string, string>? acceptedCheckSnapshot;

    /// <summary>
    /// <see cref="AcceptedChecks"/> as the studio reads it: a frozen copy taken the first time anything reads it, which is options
    /// validation at startup. A change a host makes afterwards is not seen, and cannot break an enumeration part-way through.
    /// </summary>
    internal IReadOnlyDictionary<string, string> AcceptedCheckReasons =>
        LazyInitializer.EnsureInitialized(
            ref acceptedCheckSnapshot,
            () => acceptedChecks.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, ProjectionLagThreshold> projectionThresholds = new(StringComparer.Ordinal);

    /// <summary>
    /// How far behind the high-water mark a projection may fall before the studio colours it amber and red, by projection
    /// name: <c>o.ProjectionThresholds["Trips"] = new(WarningBehind: 5_000, CriticalBehind: 50_000);</c>. Never
    /// <see langword="null" />, empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The thresholds colour the lag on the Projections page and the Overview, draw the lines on a shard's chart, and are what
    /// the falling-behind finding (D04) judges a lag against. Precedence, for every one of them: the entry here for the shard's
    /// projection name, then thresholds a host wrote itself into a progression row's <c>warning_behind_threshold</c> and
    /// <c>critical_behind_threshold</c> (Marten creates those columns and writes neither), then the studio's own 1,000 and
    /// 100,000. The Projections page's <em>Thresholds that apply</em> disclosure says which one applies to each projection.
    /// </para>
    /// <para>
    /// The dictionary applies to <b>every store</b> of the application, by projection name alone; a name that no projection of
    /// a store has is not an error (projections are often registered per environment), the disclosure just says so. Keys are
    /// compared exactly, case included. The entries are <b>read once, at startup</b> (options validation freezes a copy): add them in
    /// the <c>AddMartenStudio</c> callback, because a change made afterwards is not seen. Validation at startup refuses an entry whose numbers are not greater than zero or
    /// whose critical threshold is below its warning threshold, naming the projection.
    /// </para>
    /// </remarks>
    public IDictionary<string, ProjectionLagThreshold> ProjectionThresholds => projectionThresholds;

    private FrozenDictionary<string, ProjectionLagThreshold>? projectionThresholdsSnapshot;

    /// <summary>
    /// <see cref="ProjectionThresholds"/> as the studio reads it: a frozen copy taken the first time anything reads it, which is
    /// options validation at startup. An entry a host adds, replaces or removes afterwards is not seen - and cannot break an
    /// enumeration a page or a health sweep is part-way through.
    /// </summary>
    internal IReadOnlyDictionary<string, ProjectionLagThreshold> ProjectionLagThresholdsByName =>
        LazyInitializer.EnsureInitialized(
            ref projectionThresholdsSnapshot,
            () => projectionThresholds.ToFrozenDictionary(StringComparer.Ordinal));

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
    /// <para>
    /// <b>Marten's own <c>DeadLetterEvent</c> is asked too.</b> Hiding it hides the dead-letter screen's navigation
    /// entry and Overview tile, its list, Discard and Rewind - the page, reached by its address, says the dead letters
    /// are hidden - and the health advisor's D06. A delegate written as an
    /// allow-list of the application's own types therefore hides the dead letters as well: answer
    /// <see langword="true" /> for <c>typeof(JasperFx.Events.Daemon.DeadLetterEvent)</c> to keep them. A delegate
    /// that throws for it is read as hiding it.
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
    /// The Postgres schemas beyond the store's own whose objects the database browser may show, and whose
    /// non-Marten rows and definitions it may read, once <see cref="MartenStudioCapabilities.BrowseDatabase"/>
    /// is on - or, for the schemas an entry names exactly, their structure and definitions alone, once
    /// <see cref="MartenStudioCapabilities.ViewDefinitions"/> is. Empty by default. Each entry is an exact schema
    /// name, compared case-sensitively as Postgres stores it, or <c>"*"</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>"*"</c> means every schema the studio's role has <c>USAGE</c> on, except the system schemas
    /// (<c>pg_*</c>, <c>information_schema</c>) and the schemas an extension owns (TimescaleDB's, pg_cron's).
    /// It is meant for a development or QA setup that wants everything in one line. A system schema is never
    /// browsable, even by exact name.
    /// </para>
    /// <para>
    /// The store's own schemas show their structure without the capability, as the Schema screen always
    /// has; the rows and definitions of the non-Marten objects in them follow this list like any other
    /// schema's. <c>"*"</c> never admits a definition to <see cref="MartenStudioCapabilities.ViewDefinitions"/>:
    /// only a schema named by exact name is read for its definitions without
    /// <see cref="MartenStudioCapabilities.BrowseDatabase"/>.
    /// </para>
    /// <para>
    /// <b>This limits what the screens show, not what can be read.</b> The SQL console reads whatever the
    /// connection's role can read; <see cref="SqlConsoleRole"/> is the boundary for both. A <c>"*"</c> with
    /// no <see cref="SqlConsoleRole"/> is logged as a warning at startup.
    /// </para>
    /// <para>
    /// Validated at startup: every entry must be <c>"*"</c> or a schema name of at most 63 bytes with no
    /// double quote and no NUL.
    /// </para>
    /// </remarks>
    public IList<string> BrowsableSchemas { get; } = [];

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

    private PostgresStudio.Services.Settings.StudioPath? pathCache;

    /// <summary>
    /// Values derived from <see cref="Path"/>, computed once and reused — they are read
    /// on Blazor render hot paths (links, route matching) while the option itself only changes
    /// during startup configuration. Held behind a single reference so a concurrent reader always
    /// observes a fully-populated instance (reference reads/writes are atomic) even if the option
    /// is mutated mid-render.
    /// </summary>
    private PostgresStudio.Services.Settings.StudioPath PathCache
    {
        get
        {
            PostgresStudio.Services.Settings.StudioPath cache = PostgresStudio.Services.Settings.StudioPath.Of(Path, DefaultPath, pathCache);
            pathCache = cache;
            return cache;
        }
    }
}
