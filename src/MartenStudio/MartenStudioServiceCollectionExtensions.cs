using System.Runtime.CompilerServices;

using MartenStudio.Internal;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;
using MartenStudio.Services.Query;
using MartenStudio.Services.Schema;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

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
    /// <exception cref="InvalidOperationException">
    /// The application was deployed without the PostgresStudio package this one was released with, or with
    /// another version of it.
    /// </exception>
    public static IServiceCollection AddMartenStudio(
        this IServiceCollection services,
        Action<MartenStudioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Before anything that names a PostgresStudio type: this body names none, so the JIT can compile it and the
        // guard can say what is wrong even when NuGet left PostgresStudio out of the deployment, where the first
        // PostgresStudio type would otherwise be a FileNotFoundException with no word about why. Everything else is in
        // AddMartenStudioServices, which is NoInlining so that it is compiled only once the guard has passed.
        // PostgresStudioVersionGuardTests scans this method's IL to keep it that way.
        PostgresStudioVersionGuard.Ensure();

        return AddMartenStudioServices(services, configure);
    }

    /// <summary>The body of <see cref="AddMartenStudio" />, once the shared studio is known to be the right one.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IServiceCollection AddMartenStudioServices(
        IServiceCollection services,
        Action<MartenStudioOptions>? configure)
    {
        // First, so that a second studio in this container is refused before anything of this one is registered.
        // Everything the shared studio needs - the shell, the scope and audit services, the database browser,
        // Sessions, the health advisor - is registered there; what follows is Marten Studio's own.
        StudioCoreRegistration.Add(
            services,
            MartenStudioDescriptor.Marker,
            MartenStudioDescriptor.Instance,
            static provider => new MartenStudioSettings(provider.GetRequiredService<IOptions<MartenStudioOptions>>()));

        // The options both studios share are validated by one chain (StudioOptionsValidation), in MartenStudio's names and
        // with its sentences unchanged; MartenStudio's own validations sit between its parts, where they always were, so the
        // failures an OptionsValidationException lists keep their order (pinned by MartenStudioOptionsTest).
        OptionsBuilder<MartenStudioOptions> optionsBuilder = services
            .AddOptions<MartenStudioOptions>()
            .ValidateSharedBounds(nameof(MartenStudioOptions), nameof(MartenStudioOptions.MaxInlineDocumentBytes), Shape)
            .Validate(
                static options => options.RebuildShardTimeout >= TimeSpan.FromMinutes(1),
                "MartenStudioOptions.RebuildShardTimeout must be at least one minute: it is the per-shard replay budget, and a rebuild that exceeds it stops with the projection's tables already emptied")
            .ValidateSharedCapabilities(nameof(MartenStudioOptions), nameof(MartenStudioCapabilities), Shape)
            .Validate(
                static options => options.KnownTenantIds.All(static tenantId => !string.IsNullOrWhiteSpace(tenantId)),
                "MartenStudioOptions.KnownTenantIds cannot contain an empty or whitespace tenant id")
            .ValidateSharedSchemas(nameof(MartenStudioOptions), Shape);

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

        // PS-1c: the rest of the health engine's seams, in Marten's terms - the remedy lines the table checks offer, what a
        // plan of a document table filters on, the daemon's part in T14, and the Marten this host runs (a test may register
        // a made-up MartenRuntime first, which this one then reads).
        services.TryAddSingleton<IHealthTableRemedies>(MartenHealthRemedyProvider.Instance);
        services.TryAddSingleton<IHealthPlanAdvisor>(MartenStatementPlanAdvisor.Instance);

        // W5-Q1: the Statements page's groups - a Marten operation and its document type, read off Marten's own SQL.
        services.TryAddSingleton<IStatementClassifier>(MartenStatementClassifier.Instance);
        services.TryAddSingleton<IHorizonContributor, MartenHorizonContributor>();
        services.TryAddSingleton<IStudioRuntime>(static provider =>
            new MartenStudioRuntime(provider.GetService<MartenRuntime>() ?? MartenRuntime.Current));
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

        // Projections and the async daemon. The three singletons are process-wide on purpose: one tracker
        // subscription per database however many pages are open, a rebuild that outlives the circuit that
        // started it, and one record of which coordinators this process has paused - a pause is a fact about
        // the process, and the circuit that reads it back is rarely the one that issued it.
        services.TryAddSingleton<StudioLiveState>();
        services.TryAddSingleton<StudioOperationTracker>();
        services.TryAddSingleton<DaemonControlState>();

        // M2: the projections screen's own samples, fed by its single-flight snapshot read and shared by every circuit -
        // bounded per shard, per database and, through HealthDatabaseBound, per process.
        services.TryAddSingleton<ProjectionLagHistory>();
        services.TryAddScoped<DaemonAccessor>();
        services.TryAddScoped<IProjectionDataService, ProjectionDataService>();

        // W5-API: the diagnostics API for the host's own code (D31), the same implementation as Postgres Studio's over this
        // studio's stores, checks and descriptor - one per process, as the studio is - with its limit of two fresh reads at a
        // time. Inert until host code calls it: no endpoint, no hosted service.
        services.TryAddSingleton<FreshReadLimiter>();
        services.TryAddSingleton<PostgresStudio.IPostgresDiagnostics, PostgresDiagnosticsService>();

        return services;
    }

    /// <summary>The options MartenStudio shares with every studio, as the shared validation reads them.</summary>
    private static StudioOptionsShape Shape(MartenStudioOptions options) => new(
        options.Path,
        options.DefaultPageSize,
        options.MaxPageSize,
        options.QueryTimeout,
        options.MaxInlineDocumentBytes,
        options.MaxSqlConsoleRows,
        options.SqlConsoleRole,
        options.ExactCountThreshold,
        options.RefreshInterval,
        options.HealthInterval,
        options.Capabilities is not null,
        options.BrowsableSchemas);
}
