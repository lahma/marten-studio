using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;

namespace PostgresStudio;

/// <summary>
/// Registers Postgres Studio's services.
/// </summary>
public static class PostgresStudioServiceCollectionExtensions
{
    /// <summary>The schema browsed when nothing names one (<see cref="PostgresStudioOptions.BrowsableSchemas" />).</summary>
    internal const string DefaultBrowsableSchema = "public";

    /// <summary>
    /// Registers everything Postgres Studio renders with: its Blazor components, the source provider that finds this
    /// application's <c>NpgsqlDataSource</c>s, the scope, authorization, capability and audit services, the database
    /// browser, Sessions, the SQL console, Relationships, the health advisor, and the startup guard that refuses an
    /// unauthorized mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No option points the studio at a database by connection string. It renders the data sources <em>this
    /// application</em> registered - the unkeyed <c>NpgsqlDataSource</c> as <c>default</c>, and whatever
    /// <see cref="PostgresStudioOptions.AddDataSource" /> and <see cref="PostgresStudioOptions.AddDatabaseSource" />
    /// name. Call this before or after <c>AddNpgsqlDataSource()</c>; the scan that finds the default one runs lazily on
    /// first use, when the container is complete, and nothing connects until somebody opens a page.
    /// </para>
    /// <para>
    /// Nothing here changes how the host behaves. No global JSON or SignalR option is touched, no middleware ordering is
    /// required, and every service is registered with <c>TryAdd</c> so an application that registered its own first is
    /// the one that answers. Call <c>MapPostgresStudio()</c> on the built application to map the endpoints.
    /// </para>
    /// <para>
    /// <b>One studio per application.</b> Calling this in an application that already called <c>AddMartenStudio()</c>
    /// throws: the two share their services, and Marten Studio already browses its store's database.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the studio, including the path it is served under and its sources.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    /// <exception cref="InvalidOperationException">Marten Studio is already registered in this container.</exception>
    public static IServiceCollection AddPostgresStudio(
        this IServiceCollection services,
        Action<PostgresStudioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // First, so that a second studio in this container is refused before anything of this one is registered.
        StudioCoreRegistration.Add(
            services,
            PostgresStudioDescriptor.Marker,
            PostgresStudioDescriptor.Instance,
            static provider => new PostgresStudioSettings(provider.GetRequiredService<IOptions<PostgresStudioOptions>>()));

        // The bounds and sentences MartenStudio has, from the one shared chain, in PostgresStudio's own names; then the
        // source names, which only this studio has.
        OptionsBuilder<PostgresStudioOptions> optionsBuilder = services
            .AddOptions<PostgresStudioOptions>()
            .ValidateSharedBounds(nameof(PostgresStudioOptions), nameof(PostgresStudioOptions.MaxInlineValueBytes), Shape)
            .ValidateSharedCapabilities(nameof(PostgresStudioOptions), nameof(PostgresStudioCapabilities), Shape)
            .ValidateSharedSchemas(nameof(PostgresStudioOptions), Shape)
            .Validate(
                static options => SourceNames(options).All(PostgresSourceNames.IsValidKey),
                "PostgresStudioOptions.AddDataSource and AddDatabaseSource names must each be a letter or digit followed by " +
                "at most 63 letters, digits, '.', '_' or '-': the name travels in the URL and keys every cache")
            .Validate(
                static options => !SourceNames(options).Contains(PostgresSourceNames.Default, StringComparer.OrdinalIgnoreCase),
                "PostgresStudioOptions.AddDataSource and AddDatabaseSource cannot name a source 'default': it is the " +
                "application's unkeyed NpgsqlDataSource's, listed unless IncludeDefaultDataSource is false")
            .Validate(
                static options => SourceNames(options).Distinct(StringComparer.OrdinalIgnoreCase).Count() == SourceNames(options).Count(),
                "PostgresStudioOptions.AddDataSource and AddDatabaseSource names must be unique, ignoring case, across both");

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // After every Configure, including configuration binding, which appends to a list rather than replacing it: the
        // default schema is the answer to "nothing named any", never an entry a host has to remove.
        optionsBuilder.PostConfigure(static options =>
        {
            if (options.BrowsableSchemas.Count == 0)
            {
                options.BrowsableSchemas.Add(DefaultBrowsableSchema);
            }
        });

        // The registry captures this collection and scans it lazily, on first use: AddPostgresStudio() may be called
        // before AddNpgsqlDataSource(), and a scan now would find nothing. A singleton built by the container, so the
        // provider it is handed is the root one - what the default source and every factory resolve from.
        services.TryAddSingleton(provider => new PostgresSourceRegistry(services, provider, provider.GetService<TimeProvider>()));

        // The shared seams, answered for plain PostgreSQL. The source provider is scoped, as the seam asks: its refresh
        // generation is the circuit's.
        services.TryAddScoped<IStudioSourceProvider, PostgresStudioSourceProvider>();
        services.TryAddSingleton<IStudioLinks, PostgresStudioLinks>();
        services.TryAddSingleton<ISessionLockLabeler, PostgresSessionLockLabeler>();
        services.TryAddScoped<IDatabaseDeclarationsSource, PostgresDatabaseDeclarationsSource>();
        services.TryAddSingleton<IHealthTableSource, PostgresHealthTableSource>();
        services.TryAddSingleton<IHealthTableRemedies, PostgresHealthTableRemedies>();

        // W5-Q1: the Statements page's groups - a statement's verb and its first table of the judged set.
        services.TryAddSingleton<IStatementClassifier>(PostgresStatementClassifier.Instance);

        // The Overview's server read: scoped, because what it remembers is the circuit's.
        services.TryAddScoped<IPostgresServerReader, PostgresServerReader>();

        // W5-API: the diagnostics API for the host's own code (D31) - a singleton over the studio's own caches, and the
        // process's limit of two fresh reads at a time for it. Inert until host code calls it: no endpoint, no hosted service.
        services.TryAddSingleton<FreshReadLimiter>();
        services.TryAddSingleton<IPostgresDiagnostics, PostgresDiagnosticsService>();

        return services;
    }

    private static IEnumerable<string> SourceNames(PostgresStudioOptions options) =>
        options.DataSources.Concat(options.DatabaseSources).Select(static x => x.Name);

    /// <summary>The options PostgresStudio shares with every studio, as the shared validation reads them.</summary>
    private static StudioOptionsShape Shape(PostgresStudioOptions options) => new(
        options.Path,
        options.DefaultPageSize,
        options.MaxPageSize,
        options.QueryTimeout,
        options.MaxInlineValueBytes,
        options.MaxSqlConsoleRows,
        options.SqlConsoleRole,
        options.ExactCountThreshold,
        options.RefreshInterval,
        options.HealthInterval,
        options.Capabilities is not null,
        options.BrowsableSchemas);
}
