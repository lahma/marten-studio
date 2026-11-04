using System.Collections.Frozen;

using Npgsql;

using PostgresStudio.Services.Postgres;

namespace PostgresStudio;

/// <summary>
/// Options for the Postgres diagnostics engine, configured through
/// <c>AddPostgresDiagnostics(Action&lt;PostgresDiagnosticsOptions&gt;)</c>, and the base of <c>PostgresStudioOptions</c>:
/// everything about which databases are read and how, with nothing about a UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is read is what the application registered.</b> The unkeyed <see cref="NpgsqlDataSource" /> in the container is
/// the source named <c>default</c> (<see cref="IncludeDefaultDataSource" />); any other is named with
/// <see cref="AddDataSource" />, and a set of databases the application lists itself - one per tenant, say - with
/// <see cref="AddDatabaseSource" />. There is no connection string anywhere here: the engine reads through the
/// application's own data sources, with their role, TLS and settings, and adds no new place that holds credentials.
/// </para>
/// <para>
/// The members keep the defaults and the bounds they have on <c>PostgresStudioOptions</c>, which inherits them, so a
/// host that registers a studio configures them exactly as before.
/// </para>
/// </remarks>
public class PostgresDiagnosticsOptions
{
    private readonly List<PostgresSourceDefinition> dataSources = [];
    private readonly List<PostgresSourceDefinition> databaseSources = [];

    /// <summary>Statement timeout applied to every query the studio issues. Between one second and ten minutes.</summary>
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
    /// (<c>orders-api (Postgres Studio)</c>), or the studio's alone; an empty string leaves the application's own name. It is set for
    /// each of the studio's read-only transactions only (<c>set_config(…, true)</c>), so a pooled connection goes back to the
    /// application as it came. Printable ASCII, at most 63 characters: what PostgreSQL keeps as written.
    /// </summary>
    public string? StudioApplicationName { get; set; }

    /// <summary>
    /// When set, the SQL console, the database browser's reads and the health advisor's plans run
    /// <c>SET LOCAL ROLE</c> to this Postgres role inside their read-only transaction, which is the only mechanism that
    /// narrows what they can read. Must be a plain identifier.
    /// </summary>
    public string? SqlConsoleRole { get; set; }

    /// <summary>
    /// How often the health advisor re-checks a database while somebody has its Health page or the Overview open.
    /// Between fifteen seconds and one hour; a <c>RefreshInterval</c> of a studio longer than it wins. Nothing is checked
    /// while no page is open - unless host code calls <see cref="IPostgresDiagnostics" />, which drives the same reads - and
    /// every open page and every call share one result per database per interval.
    /// </summary>
    public TimeSpan HealthInterval { get; set; } = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, string> acceptedChecks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Health checks this application has decided to live with, each by its code (<c>"S04"</c>) with the reason, in the host's
    /// own words. The check still runs and its finding is still shown - as <em>Accepted</em>, muted, with the reason beside it
    /// - but it is out of the Warning and Critical counts, the Health page's state, the navigation badge, the banners and
    /// the ASP.NET Core health check's status; and <c>PostgresFinding.Accepted</c> says so for host code that reads the
    /// diagnostics API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Meant for findings about configuration the host owns and has chosen - S04, the application connecting as a superuser
    /// on a managed database whose roles belong to another team, or K05, the studio's SQL console reading as the data
    /// source's own role - which carry the same evidence every time. A finding that moves with the data (connections,
    /// vacuum, storage) is not one to accept: it would stop counting the day it matters. The K-checks (K05 to K07) judge the
    /// studio's own configuration and exist only where a studio is registered: <c>AddPostgresDiagnostics()</c> refuses them
    /// here.
    /// </para>
    /// <para>
    /// The check's row on the Health page shows the reason only beside a finding the visitor may see. The <em>Accepted
    /// checks</em> list on the Server page (and on Marten Studio's Configuration page) is configuration, shown to anyone who
    /// may open that page, so a reason should hold nothing you would not show them.
    /// </para>
    /// <para>
    /// Keys are compared ignoring case. The entries are read once, at startup (a frozen copy is taken when options validation
    /// runs): add them in the registration callback, because a change made afterwards is not seen. Validation at startup refuses
    /// a key that is not a check of this studio, and a reason that is empty, whitespace or longer than 500 characters.
    /// </para>
    /// </remarks>
    public IDictionary<string, string> AcceptedChecks => acceptedChecks;

    private FrozenDictionary<string, string>? acceptedCheckSnapshot;

    /// <summary>
    /// <see cref="AcceptedChecks" /> as the studio reads it: a frozen copy taken the first time anything reads it, which is options
    /// validation at startup. A change a host makes afterwards is not seen, and cannot break an enumeration part-way through.
    /// </summary>
    internal IReadOnlyDictionary<string, string> AcceptedCheckReasons =>
        LazyInitializer.EnsureInitialized(
            ref acceptedCheckSnapshot,
            () => acceptedChecks.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The Postgres schemas the database browser shows. Their structure - tables, columns, keys, indexes - is visible to
    /// anyone the source policy admits; their definitions need <c>ViewDefinitions</c> or
    /// <c>BrowseDatabase</c>, and their rows <c>BrowseDatabase</c>,
    /// each with the write policy. Each entry is an exact schema name, compared case-sensitively as Postgres stores it, or
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
    /// <b>Only an exact name makes a schema's structure free.</b> The schemas listed by name are the data source's own: their
    /// structure is shown to anyone the source policy admits. A schema <c>"*"</c> alone admits is shown - structure, rows
    /// and definitions - only with <c>BrowseDatabase</c> and the write policy, because
    /// <c>"*"</c> is a statement about the database rather than this configuration, and what it admits changes as the
    /// database does. <c>ViewDefinitions</c> covers the schemas named exactly and never
    /// one only <c>"*"</c> admits. An exact entry naming a schema an extension owns (<c>cron</c>, TimescaleDB's) is honoured like any
    /// other, which is how a host that wants such a schema browses it: <c>"*"</c> never admits one.
    /// </para>
    /// <para>
    /// <b>This limits what the screens show, not what can be read.</b> The SQL console reads whatever the connection's
    /// role can read; <see cref="SqlConsoleRole" /> is the boundary for both. A <c>"*"</c> with no
    /// <see cref="SqlConsoleRole" /> is logged as a warning at startup.
    /// </para>
    /// <para>
    /// <b>For the engine alone</b> it is the schema filter of the tables the health checks judge and of the objects
    /// <see cref="IPostgresDiagnostics" /> reports on: a table in another schema is neither checked nor named in a finding,
    /// although a check that counts the whole database (transaction-id wraparound, say) still counts it.
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
    public PostgresDiagnosticsOptions AddDataSource(string name, Func<IServiceProvider, NpgsqlDataSource> resolve)
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
    public PostgresDiagnosticsOptions AddDatabaseSource(string name, Func<IServiceProvider, IPostgresDatabaseSource> resolve)
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
}
