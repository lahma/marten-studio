using Npgsql;

using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio;

/// <summary>
/// Options for Postgres Studio, configured through <c>AddPostgresStudio(Action&lt;PostgresStudioOptions&gt;)</c>.
/// Validation runs when the options are first read, which <c>MapPostgresStudio()</c> does at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the studio browses is what the application registered.</b> The unkeyed <see cref="NpgsqlDataSource" /> in
/// the container is the source named <c>default</c> (<see cref="IncludeDefaultDataSource" />); any other is named with
/// <see cref="AddDataSource" />, and a set of databases the application lists itself - one per tenant, say - with
/// <see cref="AddDatabaseSource" />. There is no connection string anywhere here: the studio reads through the
/// application's own data sources, with their role, TLS and settings, and adds no new place that holds credentials.
/// </para>
/// <para>
/// The members mirror <c>MartenStudioOptions</c> where both studios have the setting, with the same defaults and the
/// same bounds, so a host that knows one knows the other.
/// </para>
/// </remarks>
public sealed class PostgresStudioOptions
{
    internal const string DefaultPath = "/postgres";

    private readonly List<PostgresSourceDefinition> dataSources = [];
    private readonly List<PostgresSourceDefinition> databaseSources = [];

    /// <summary>
    /// The path the studio is mounted at. Must start with <c>/</c> and be a plain routable path (no route parameters,
    /// query or fragment). <c>MapPostgresStudio(pattern)</c> overrides it.
    /// </summary>
    public string Path { get; set; } = DefaultPath;

    /// <summary>The title shown in the browser tab and the header.</summary>
    public string Title { get; set; } = "Postgres Studio";

    /// <summary>
    /// When set, the policy applied to every studio endpoint: pages, the Blazor circuit and the static assets. When
    /// <see langword="null" /> the studio adds no policy of its own and refuses to start unless the mapping calls
    /// <c>RequireAuthorization()</c> or <c>AllowAnonymous()</c>, or the host has an
    /// <c>AuthorizationOptions.FallbackPolicy</c>.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>
    /// When set, evaluated against a <see cref="PostgresStudioResource" /> (with a <see langword="null" /> capability)
    /// before a source or database is listed, framed or read.
    /// </summary>
    public string? SourceAuthorizationPolicy { get; set; }

    /// <summary>
    /// When set, evaluated against a <see cref="PostgresStudioResource" /> carrying the capability name before every
    /// read authorized like a write. When <see langword="null" />, <see cref="SourceAuthorizationPolicy" /> is used for
    /// those as well.
    /// </summary>
    public string? WriteAuthorizationPolicy { get; set; }

    /// <summary>
    /// Master switch: when <see langword="true" /> every capability is off regardless of <see cref="Capabilities" />.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>Which reads authorized like writes are enabled. All off by default.</summary>
    public PostgresStudioCapabilities Capabilities { get; set; } = new();

    /// <summary>Rows per page when a list is first shown. Between 1 and <see cref="MaxPageSize" />.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>The largest page size a user may pick. Between 1 and 5000.</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>Statement timeout applied to every query the studio issues. Between one second and ten minutes.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest cell value - a JSON document, a long text, a byte array - rendered inline; a larger one is read on
    /// demand up to this many bytes, and the SQL console reads the rest. Between 1 byte and 64 MiB.
    /// </summary>
    public int MaxInlineValueBytes { get; set; } = 512 * 1024;

    /// <summary>Row cap for the SQL console; the reader stops after this many rows. Between 1 and 10000.</summary>
    public int MaxSqlConsoleRows { get; set; } = 500;

    /// <summary>
    /// When set, the SQL console, the database browser's reads and the health advisor's plans run
    /// <c>SET LOCAL ROLE</c> to this Postgres role inside their read-only transaction, which is the only mechanism that
    /// narrows what they can read. Must be a plain identifier.
    /// </summary>
    public string? SqlConsoleRole { get; set; }

    /// <summary>
    /// Above this row count, the studio reports the <c>pg_class.reltuples</c> estimate and declines to run
    /// <c>count(*)</c>, including when a user asks for the exact number. Zero means "never count, always estimate". It
    /// cannot be negative.
    /// </summary>
    public long ExactCountThreshold { get; set; } = 100_000;

    /// <summary>
    /// How often live pages poll for changes. Between one second and five minutes. Polling pauses while the tab is
    /// hidden.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the health advisor re-checks a database while somebody has its Health page or the Overview open.
    /// Between fifteen seconds and one hour; a <see cref="RefreshInterval" /> longer than it wins. Nothing is checked
    /// while no page is open, and every open page shares one result per database per interval.
    /// </summary>
    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The cadence the advisor really runs at: <see cref="HealthInterval" />, or <see cref="RefreshInterval" /> when that
    /// is longer.
    /// </summary>
    internal TimeSpan EffectiveHealthInterval => HealthInterval >= RefreshInterval ? HealthInterval : RefreshInterval;

    /// <summary>
    /// The Postgres schemas the database browser shows. Their structure - tables, columns, keys, indexes - is visible to
    /// anyone the source policy admits; their rows and definitions need <see cref="PostgresStudioCapabilities.BrowseDatabase" />
    /// and the write policy. Each entry is an exact schema name, compared case-sensitively as Postgres stores it, or
    /// <c>"*"</c>. When nothing names any schema it is <c>public</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The default is applied last.</b> The list starts empty, and only when it is still empty after every configuration
    /// - code and bound configuration alike - does the studio add <c>public</c>. So a list that names schemas replaces
    /// the default rather than adding to it: <c>"BrowsableSchemas": ["legacy"]</c> in <c>appsettings.json</c> browses
    /// <c>legacy</c> alone, and to keep <c>public</c> beside it, name it too.
    /// </para>
    /// <para>
    /// <c>"*"</c> means every schema the studio's role has <c>USAGE</c> on, except the system schemas and the schemas an
    /// extension owns. A system schema is never browsable, even by exact name.
    /// </para>
    /// <para>
    /// <b>This limits what the screens show, not what can be read.</b> The SQL console reads whatever the connection's
    /// role can read; <see cref="SqlConsoleRole" /> is the boundary for both. A <c>"*"</c> with no
    /// <see cref="SqlConsoleRole" /> is logged as a warning at startup.
    /// </para>
    /// <para>
    /// A Marten table (<c>mt_</c>) seen here is listed as Marten's and its rows are never read: they are browsed in
    /// Marten Studio, where its tenancy and soft deletes apply.
    /// </para>
    /// </remarks>
    public IList<string> BrowsableSchemas { get; } = [];

    /// <summary>
    /// Whether the application's unkeyed <see cref="NpgsqlDataSource" /> - what <c>AddNpgsqlDataSource(connectionString)</c>
    /// or <c>AddSingleton(NpgsqlDataSource.Create(…))</c> registers - is listed as the source named <c>default</c>.
    /// The last unkeyed registration decides, because it is the one the container resolves, and only a singleton is
    /// listed: a scoped or transient one would build a pool per visitor, or per resolution.
    /// </summary>
    public bool IncludeDefaultDataSource { get; set; } = true;

    /// <summary>
    /// Lists one more of the application's own data sources under <paramref name="name" />: a keyed one
    /// (<c>sp =&gt; sp.GetRequiredKeyedService&lt;NpgsqlDataSource&gt;("orders")</c>), or one an EF Core or Dapper host keeps
    /// somewhere else. Listed after <c>default</c>, in the order added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The factory is called once, when the source is first used - with the application's <b>root</b> service provider,
    /// however many visitors ask at the same moment - and its result is kept for the life of the process. It must return
    /// the application's own instance: the studio never disposes it, and a factory that built a new data source would
    /// leak a pool.
    /// </para>
    /// <para>
    /// <b>Singletons only.</b> Because the result is kept for the process, the provider the factory is handed refuses a
    /// data source the container registers as scoped or transient, and the source is then unavailable, saying why.
    /// </para>
    /// </remarks>
    /// <param name="name">
    /// The source's key, as the URL carries it: a letter or digit, then up to 63 letters, digits, <c>.</c>, <c>_</c> or
    /// <c>-</c>; unique, ignoring case, across every source; and not <c>default</c>.
    /// </param>
    /// <param name="resolve">Returns the application's own data source, usually from the container.</param>
    /// <returns>These options, so calls can be chained.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolve" /> is null.</exception>
    public PostgresStudioOptions AddDataSource(string name, Func<IServiceProvider, NpgsqlDataSource> resolve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resolve);

        dataSources.Add(PostgresSourceDefinition.ForDataSource(name, resolve));
        return this;
    }

    /// <summary>
    /// Lists a host-supplied set of databases under <paramref name="name" />: per-tenant databases, say. Listed after the
    /// data sources, in the order added. The factory is called once, with the application's root service provider, and
    /// its result is kept for the process and never disposed by the studio; as for <see cref="AddDataSource" />, the
    /// provider refuses a database source the container registers as scoped or transient.
    /// </summary>
    /// <param name="name">
    /// The source's key, as the URL carries it: the data-source key pattern, unique across every source, and not
    /// <c>default</c>.
    /// </param>
    /// <param name="resolve">Returns the application's own source, usually from the container.</param>
    /// <returns>These options, so calls can be chained.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolve" /> is null.</exception>
    public PostgresStudioOptions AddDatabaseSource(string name, Func<IServiceProvider, IPostgresDatabaseSource> resolve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(resolve);

        databaseSources.Add(PostgresSourceDefinition.ForDatabaseSource(name, resolve));
        return this;
    }

    /// <summary>Every source named by <see cref="AddDataSource" />, in call order.</summary>
    internal IReadOnlyList<PostgresSourceDefinition> DataSources => dataSources;

    /// <summary>Every source named by <see cref="AddDatabaseSource" />, in call order.</summary>
    internal IReadOnlyList<PostgresSourceDefinition> DatabaseSources => databaseSources;

    /// <summary>
    /// <see cref="Path" /> normalized to a rooted path without a trailing slash, falling back to
    /// <see cref="DefaultPath" /> when unset or empty.
    /// </summary>
    internal string TrimmedPath => PathCache.Trimmed;

    /// <summary>Whether <see cref="Path" /> differs from the compile-time default <c>/postgres</c>.</summary>
    internal bool HasCustomPath => PathCache.HasCustom;

    /// <summary><see cref="TrimmedPath" /> percent-encoded, as browsers emit it in request URIs and the &lt;base href&gt;.</summary>
    internal string EscapedPath => PathCache.Escaped;

    private StudioPath? pathCache;

    /// <summary>
    /// Values derived from <see cref="Path" />, computed once per value and held behind one reference, so a concurrent
    /// reader always observes a fully populated instance even if the option is written mid-render.
    /// </summary>
    private StudioPath PathCache
    {
        get
        {
            StudioPath cache = StudioPath.Of(Path, DefaultPath, pathCache);
            pathCache = cache;
            return cache;
        }
    }
}
