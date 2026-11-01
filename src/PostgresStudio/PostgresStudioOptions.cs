using Npgsql;

using PostgresStudio.Services.Settings;

namespace PostgresStudio;

/// <summary>
/// Options for Postgres Studio, configured through <c>AddPostgresStudio(Action&lt;PostgresStudioOptions&gt;)</c>.
/// Validation runs when the options are first read, which <c>MapPostgresStudio()</c> does at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the studio browses is what the application registered.</b> The unkeyed <c>NpgsqlDataSource</c> in
/// the container is the source named <c>default</c> (<c>IncludeDefaultDataSource</c>); any other is named with
/// <c>AddDataSource</c>, and a set of databases the application lists itself - one per tenant, say - with
/// <c>AddDatabaseSource</c>. There is no connection string anywhere here: the studio reads through the
/// application's own data sources, with their role, TLS and settings, and adds no new place that holds credentials.
/// </para>
/// <para>
/// The members mirror <c>MartenStudioOptions</c> where both studios have the setting, with the same defaults and the
/// same bounds, so a host that knows one knows the other.
/// </para>
/// </remarks>
public sealed class PostgresStudioOptions : PostgresDiagnosticsOptions
{
    internal const string DefaultPath = "/postgres";

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

    /// <summary>
    /// The largest cell value - a JSON document, a long text, a byte array - rendered inline; a larger one is read on
    /// demand up to this many bytes, and the SQL console reads the rest. Between 1 byte and 64 MiB.
    /// </summary>
    public int MaxInlineValueBytes { get; set; } = 512 * 1024;

    /// <summary>Row cap for the SQL console; the reader stops after this many rows. Between 1 and 10000.</summary>
    public int MaxSqlConsoleRows { get; set; } = 500;

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
    /// The cadence the advisor really runs at: <c>HealthInterval</c>, or <see cref="RefreshInterval" /> when that
    /// is longer.
    /// </summary>
    internal TimeSpan EffectiveHealthInterval => HealthInterval >= RefreshInterval ? HealthInterval : RefreshInterval;

    /// <summary>
    /// Lists one more of the application's own data sources under <paramref name="name" />, as
    /// <see cref="PostgresDiagnosticsOptions.AddDataSource" /> does, returning these options so a chain written against this
    /// type keeps compiling.
    /// </summary>
    /// <param name="name">The source's key, as <see cref="PostgresDiagnosticsOptions.AddDataSource" /> describes it.</param>
    /// <param name="resolve">Returns the application's own data source, usually from the container.</param>
    /// <returns>These options, so calls can be chained.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolve" /> is null.</exception>
    public new PostgresStudioOptions AddDataSource(string name, Func<IServiceProvider, NpgsqlDataSource> resolve)
    {
        base.AddDataSource(name, resolve);
        return this;
    }

    /// <summary>
    /// Lists a host-supplied set of databases under <paramref name="name" />, as
    /// <see cref="PostgresDiagnosticsOptions.AddDatabaseSource" /> does, returning these options so a chain written against this
    /// type keeps compiling.
    /// </summary>
    /// <param name="name">The source's key, as <see cref="PostgresDiagnosticsOptions.AddDatabaseSource" /> describes it.</param>
    /// <param name="resolve">Returns the application's own source, usually from the container.</param>
    /// <returns>These options, so calls can be chained.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolve" /> is null.</exception>
    public new PostgresStudioOptions AddDatabaseSource(string name, Func<IServiceProvider, IPostgresDatabaseSource> resolve)
    {
        base.AddDatabaseSource(name, resolve);
        return this;
    }

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
