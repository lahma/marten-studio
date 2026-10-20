using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio;

/// <summary>
/// Registers Postgres Studio's services.
/// </summary>
public static partial class PostgresStudioServiceCollectionExtensions
{
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

        OptionsBuilder<PostgresStudioOptions> optionsBuilder = services
            .AddOptions<PostgresStudioOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.Path) && options.Path.StartsWith('/'),
                "PostgresStudioOptions.Path must start with '/'")
            .Validate(
                static options => StudioPath.IsRoutable(options.Path),
                "PostgresStudioOptions.Path must be a simple URL path: it cannot contain '{', '}', '?', '#', '.' or '..' segments, or empty segments ('//')")
            .Validate(
                static options => options.DefaultPageSize >= 1 && options.DefaultPageSize <= options.MaxPageSize,
                "PostgresStudioOptions.DefaultPageSize must be between 1 and MaxPageSize")
            .Validate(
                static options => options.MaxPageSize >= 1 && options.MaxPageSize <= 5000,
                "PostgresStudioOptions.MaxPageSize must be between 1 and 5000: a larger page is a sequential scan rendered into a browser")
            .Validate(
                static options => options.QueryTimeout >= TimeSpan.FromSeconds(1) && options.QueryTimeout <= TimeSpan.FromMinutes(10),
                "PostgresStudioOptions.QueryTimeout must be between one second and ten minutes")
            .Validate(
                static options => options.MaxInlineValueBytes > 0 && options.MaxInlineValueBytes <= 64 * 1024 * 1024,
                "PostgresStudioOptions.MaxInlineValueBytes must be between 1 and 64 MiB")
            .Validate(
                static options => options.MaxSqlConsoleRows >= 1 && options.MaxSqlConsoleRows <= 10_000,
                "PostgresStudioOptions.MaxSqlConsoleRows must be between 1 and 10000")
            .Validate(
                static options => options.SqlConsoleRole is null || PostgresRoleName().IsMatch(options.SqlConsoleRole),
                "PostgresStudioOptions.SqlConsoleRole must be a plain Postgres identifier: it is written into SET LOCAL ROLE, where it cannot be a parameter")
            .Validate(
                static options => options.ExactCountThreshold >= 0,
                "PostgresStudioOptions.ExactCountThreshold cannot be negative")
            .Validate(
                static options => options.RefreshInterval >= TimeSpan.FromSeconds(1) && options.RefreshInterval <= TimeSpan.FromMinutes(5),
                "PostgresStudioOptions.RefreshInterval must be between one second and five minutes")
            .Validate(
                static options => options.HealthInterval >= TimeSpan.FromSeconds(15) && options.HealthInterval <= TimeSpan.FromHours(1),
                "PostgresStudioOptions.HealthInterval must be between fifteen seconds and one hour: several health checks scan the server's lock table and process array, and a page polls them no faster than this")
            .Validate(
                static options => options.Capabilities is not null,
                "PostgresStudioOptions.Capabilities cannot be null: use new PostgresStudioCapabilities() for none, or PostgresStudioCapabilities.All()")
            .Validate(
                static options => options.BrowsableSchemas.All(BrowsableSchemaMatcher.IsValidEntry),
                "PostgresStudioOptions.BrowsableSchemas entries must each be \"*\" or a Postgres schema name: not " +
                "empty or whitespace, at most 63 bytes in UTF-8, and with no double quote (\") and no NUL - a " +
                "name the studio could never quote is a name no schema it can read has")
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

        // The registry captures this collection and scans it lazily, on first use: AddPostgresStudio() may be called
        // before AddNpgsqlDataSource(), and a scan now would find nothing.
        services.TryAddSingleton(new PostgresSourceRegistry(services));

        // The shared seams, answered for plain PostgreSQL. The source provider is scoped, as the seam asks: its refresh
        // generation is the circuit's.
        services.TryAddScoped<IStudioSourceProvider, PostgresStudioSourceProvider>();
        services.TryAddSingleton<IStudioLinks, PostgresStudioLinks>();
        services.TryAddSingleton<ISessionLockLabeler, PostgresSessionLockLabeler>();
        services.TryAddScoped<IDatabaseDeclarationsSource, PostgresDatabaseDeclarationsSource>();
        services.TryAddSingleton<IHealthTableSource, PostgresHealthTableSource>();
        services.TryAddSingleton<IHealthTableRemedies, PostgresHealthTableRemedies>();

        // The Overview's server read: scoped, because what it remembers is the circuit's.
        services.TryAddScoped<IPostgresServerReader, PostgresServerReader>();

        return services;
    }

    private static IEnumerable<string> SourceNames(PostgresStudioOptions options) =>
        options.DataSources.Concat(options.DatabaseSources).Select(static x => x.Name);

    /// <summary>
    /// A Postgres role name as <c>SET LOCAL ROLE</c> will accept it. The console's role cannot be a parameter - it is an
    /// identifier - so it is held to the shape of an unquoted identifier here, at startup, rather than escaped later.
    /// </summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_$]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex PostgresRoleName();
}
