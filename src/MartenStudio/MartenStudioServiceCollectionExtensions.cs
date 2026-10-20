using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Database;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;
using MartenStudio.Services.Query;
using MartenStudio.Services.Relationships;
using MartenStudio.Services.Schema;
using MartenStudio.Services.Settings;
using MartenStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MartenStudio;

/// <summary>
/// Registers Marten Studio's services.
/// </summary>
public static partial class MartenStudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything Marten Studio renders with: its Blazor components, the store registry that
    /// discovers this application's Marten stores, the scope, authorization, capability and audit
    /// services, and the startup guard that refuses an unauthorized mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No option points the studio at a store. It renders the stores <em>this application</em> registered
    /// - the default <c>IDocumentStore</c> and every ancillary store added with
    /// <c>AddMartenStore&lt;T&gt;()</c> - which is why there is no connection string anywhere in the
    /// public API. Call this before or after <c>AddMarten()</c>; the scan that finds the stores runs
    /// lazily on first use, when the container is complete.
    /// </para>
    /// <para>
    /// Nothing here changes how the host behaves. No global JSON or SignalR option is touched, no
    /// middleware ordering is required, and every service is registered with <c>TryAdd</c> so an
    /// application that registered its own first is the one that answers. Call
    /// <c>MapMartenStudio()</c> on the built application to map the endpoints.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the studio, including the path it is served under.</param>
    /// <returns>The same collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    public static IServiceCollection AddMartenStudio(
        this IServiceCollection services,
        Action<MartenStudioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // First, so that a second studio in this container is refused before anything of this one is registered.
        // Everything the shared studio needs - the shell, the scope and audit services, the database browser,
        // Sessions, the health advisor - is registered there; what follows is Marten Studio's own.
        StudioCoreRegistration.Add(
            services,
            MartenStudioDescriptor.Marker,
            MartenStudioDescriptor.Instance,
            static provider => new MartenStudioSettings(provider.GetRequiredService<IOptions<MartenStudioOptions>>()));

        OptionsBuilder<MartenStudioOptions> optionsBuilder = services
            .AddOptions<MartenStudioOptions>()
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.Path) && options.Path.StartsWith('/'),
                "MartenStudioOptions.Path must start with '/'")
            .Validate(
                static options => StudioPath.IsRoutable(options.Path),
                "MartenStudioOptions.Path must be a simple URL path: it cannot contain '{', '}', '?', '#', '.' or '..' segments, or empty segments ('//')")
            .Validate(
                static options => options.DefaultPageSize >= 1 && options.DefaultPageSize <= options.MaxPageSize,
                "MartenStudioOptions.DefaultPageSize must be between 1 and MaxPageSize")
            .Validate(
                static options => options.MaxPageSize >= 1 && options.MaxPageSize <= 5000,
                "MartenStudioOptions.MaxPageSize must be between 1 and 5000: a larger page is a sequential scan rendered into a browser")
            .Validate(
                static options => options.QueryTimeout >= TimeSpan.FromSeconds(1) && options.QueryTimeout <= TimeSpan.FromMinutes(10),
                "MartenStudioOptions.QueryTimeout must be between one second and ten minutes")
            .Validate(
                static options => options.MaxInlineDocumentBytes > 0 && options.MaxInlineDocumentBytes <= 64 * 1024 * 1024,
                "MartenStudioOptions.MaxInlineDocumentBytes must be between 1 and 64 MiB")
            .Validate(
                static options => options.MaxSqlConsoleRows >= 1 && options.MaxSqlConsoleRows <= 10_000,
                "MartenStudioOptions.MaxSqlConsoleRows must be between 1 and 10000")
            .Validate(
                static options => options.SqlConsoleRole is null || PostgresRoleName().IsMatch(options.SqlConsoleRole),
                "MartenStudioOptions.SqlConsoleRole must be a plain Postgres identifier: it is written into SET LOCAL ROLE, where it cannot be a parameter")
            .Validate(
                static options => options.ExactCountThreshold >= 0,
                "MartenStudioOptions.ExactCountThreshold cannot be negative")
            .Validate(
                static options => options.RefreshInterval >= TimeSpan.FromSeconds(1) && options.RefreshInterval <= TimeSpan.FromMinutes(5),
                "MartenStudioOptions.RefreshInterval must be between one second and five minutes")
            .Validate(
                static options => options.HealthInterval >= TimeSpan.FromSeconds(15) && options.HealthInterval <= TimeSpan.FromHours(1),
                "MartenStudioOptions.HealthInterval must be between fifteen seconds and one hour: several health checks scan the server's lock table and process array, and a page polls them no faster than this")
            .Validate(
                static options => options.RebuildShardTimeout >= TimeSpan.FromMinutes(1),
                "MartenStudioOptions.RebuildShardTimeout must be at least one minute: it is the per-shard replay budget, and a rebuild that exceeds it stops with the projection's tables already emptied")
            .Validate(
                static options => options.Capabilities is not null,
                "MartenStudioOptions.Capabilities cannot be null: use new MartenStudioCapabilities() for none, or MartenStudioCapabilities.All()")
            .Validate(
                static options => options.KnownTenantIds.All(static tenantId => !string.IsNullOrWhiteSpace(tenantId)),
                "MartenStudioOptions.KnownTenantIds cannot contain an empty or whitespace tenant id")
            .Validate(
                static options => options.BrowsableSchemas.All(BrowsableSchemaMatcher.IsValidEntry),
                "MartenStudioOptions.BrowsableSchemas entries must each be \"*\" or a Postgres schema name: not " +
                "empty or whitespace, at most 63 bytes in UTF-8, and with no double quote (\") and no NUL - a " +
                "name the studio could never quote is a name no schema it can read has");

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // The registry captures this collection and scans it lazily, on first use: AddMartenStudio() is
        // routinely called before AddMarten(), and a scan now would find no stores at all.
        services.TryAddSingleton(new MartenStoreRegistry(services));

        // The shared seams, answered in Marten's terms. The source provider is scoped like the tenant
        // discovery it holds: a circuit's discovered tenants and its refresh button are its own.
        services.TryAddScoped<IStudioSourceProvider, MartenStudioSourceProvider>();
        services.TryAddSingleton<IStudioLinks, MartenStudioLinks>();
        services.TryAddSingleton<ISessionLockLabeler, MartenSessionLockLabeler>();
        services.TryAddScoped<IDatabaseDeclarationsSource, MartenDatabaseDeclarationsSource>();
        services.TryAddSingleton<IHealthTableSource, MartenHealthTableSource>();
        services.TryAddSingleton<IHealthRemedyProvider, MartenHealthRemedyProvider>();
        services.TryAddScoped<ISqlConsoleService, MartenSqlConsoleService>();
        services.TryAddScoped<IRelationshipNodeSource, MartenRelationshipNodeSource>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<INavIndicatorContributor, MartenNavIndicatorContributor>());

        // Reads a table's indexes for the Mode A index verdict and caches what it said, keyed by database
        // identity, for the same reason as the shared column catalog.
        services.TryAddSingleton<IndexCatalog>();

        // Scoped, because each is about one circuit.
        services.TryAddScoped<StudioScopeResolver>();
        services.TryAddScoped<TenantDiscovery>();
        services.TryAddScoped<IStoreInfoService, StoreInfoService>();
        services.TryAddScoped<IDocumentDataService, DocumentDataService>();
        services.TryAddScoped<DocumentBrowserState>();
        services.TryAddScoped<IDocumentWriteService, DocumentWriteService>();
        services.TryAddScoped<IEventDataService, EventDataService>();
        services.TryAddScoped<ISchemaDataService, SchemaDataService>();
        services.TryAddScoped<IConfigurationService, ConfigurationService>();
        services.TryAddScoped<IQueryService, QueryService>();

        // F7: LINQ mode. Scoped like every data service: it keeps one circuit's audit ledger, so a page turn
        // is not a second opening.
        services.TryAddScoped<ILinqQueryService, LinqQueryService>();
        services.TryAddScoped<IRelationshipDataService, RelationshipDataService>();

        // Projections and the async daemon. The three singletons are process-wide on purpose: one tracker
        // subscription per database however many pages are open, a rebuild that outlives the circuit that
        // started it, and one record of which coordinators this process has paused - a pause is a fact about
        // the process, and the circuit that reads it back is rarely the one that issued it.
        services.TryAddSingleton<StudioLiveState>();
        services.TryAddSingleton<StudioOperationTracker>();
        services.TryAddSingleton<DaemonControlState>();
        services.TryAddScoped<DaemonAccessor>();
        services.TryAddScoped<IProjectionDataService, ProjectionDataService>();

        return services;
    }

    /// <summary>
    /// A Postgres role name as <c>SET LOCAL ROLE</c> will accept it. The console's role cannot be a
    /// parameter - it is an identifier - so it is held to the shape of an unquoted identifier here, at
    /// startup, rather than escaped later.
    /// </summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_$]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex PostgresRoleName();
}
