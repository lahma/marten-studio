using System.Runtime.CompilerServices;

using DataStudio.Services.Settings;
using DataStudio.Marten;
using DataStudio.Marten.Internal;
using DataStudio.Marten.Internal.Sql;
using DataStudio.Marten.Services;
using DataStudio.Marten.Services.Configuration;
using DataStudio.Marten.Services.Connection;
using DataStudio.Marten.Services.Documents;
using DataStudio.Marten.Services.Events;
using DataStudio.Marten.Services.Find;
using DataStudio.Marten.Services.Health;
using DataStudio.Marten.Services.Health.Checks;
using DataStudio.Marten.Services.Links;
using DataStudio.Marten.Services.Live;
using DataStudio.Marten.Services.Projections;
using DataStudio.Marten.Services.Query;
using DataStudio.Marten.Services.Schema;
using DataStudio.Marten.Services.Shapes;
using DataStudio.Postgres.Services.Connection;
using DataStudio.Postgres.Services.Find;
using DataStudio.Postgres.Services.Query;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Postgres.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Health.Checks;

namespace DataStudio;

/// <summary>
/// Adds the Marten module to Data Studio.
/// </summary>
public static class MartenDataStudioBuilderExtensions
{
    /// <summary>
    /// Adds the Marten module to the studio: its Blazor components, the store registry that discovers this application's Marten
    /// stores, the scope, authorization, capability and audit services, and the database pages over the stores' databases.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No option points the module at a store. It renders the stores <em>this application</em> registered
    /// - the default <c>IDocumentStore</c> and every ancillary store added with
    /// <c>AddMartenStore&lt;T&gt;()</c> - which is why there is no connection string anywhere in the
    /// public API. Call this before or after the host's own Marten registration (<c>services.AddMarten(...)</c>); the scan that finds
    /// the stores runs lazily on first use, when the container is complete.
    /// </para>
    /// <para>
    /// Nothing here changes how the host behaves. No global JSON or SignalR option is touched, no
    /// middleware ordering is required, and every service is registered with <c>TryAdd</c> so an
    /// application that registered its own first is the one that answers. Call
    /// <c>MapDataStudio()</c> on the built application to map the endpoints.
    /// </para>
    /// <para>
    /// <b>One database module per application.</b> Calling this in an application that already called <c>AddPostgres()</c> throws: the two
    /// share their services, and this module already browses its stores' databases. Calling it twice is safe.
    /// </para>
    /// </remarks>
    /// <param name="builder">The builder <c>AddDataStudio()</c> returned.</param>
    /// <param name="configure">Configures the module.</param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The Postgres module is already added to this builder's services, or the application was deployed without the DataStudio.Postgres package
    /// this one was released with, or with another version of it.
    /// </exception>
    public static DataStudioBuilder AddMarten(
        this DataStudioBuilder builder,
        Action<MartenStudioOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Before anything that names a DataStudio.Postgres type: this body names none, so the JIT can compile it and the
        // guard can say what is wrong even when NuGet left DataStudio.Postgres out of the deployment, where the first
        // DataStudio.Postgres type would otherwise be a FileNotFoundException with no word about why. Everything else is in
        // AddMartenServices, which is NoInlining so that it is compiled only once the guard has passed.
        // PostgresStudioVersionGuardTests scans this method's IL to keep it that way.
        PostgresStudioVersionGuard.Ensure();

        AddMartenServices(builder, configure);

        return builder;
    }

    /// <summary>The body of <see cref="AddMarten" />, once the shared database module is known to be the right one.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddMartenServices(
        DataStudioBuilder builder,
        Action<MartenStudioOptions>? configure)
    {
        IServiceCollection services = builder.Services;

        // First, so that a second database module in this container is refused before anything of this one is registered.
        // Everything the database pages need - the scope and audit services, the database browser, Sessions, the health
        // advisor - is registered there; what follows is the Marten module's own.
        StudioCoreRegistration.Add(
            services,
            MartenStudioDescriptor.Marker,
            MartenStudioDescriptor.Instance,
            static provider => new MartenStudioSettings(
                provider.GetRequiredService<IOptions<MartenStudioOptions>>(),
                provider.GetRequiredService<IShellSettings>()));

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
            .ValidateSharedSchemas(nameof(MartenStudioOptions), Shape)
            .ValidateSharedAcceptedChecks(nameof(MartenStudioOptions), MartenStudioDescriptor.Instance, static options => options.AcceptedCheckReasons);

        // 1.2-PROJ: the per-projection lag thresholds (a type of its own: a failure names the projection).
        services.AddSingleton<IValidateOptions<MartenStudioOptions>, ProjectionThresholdsValidator>();

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // The registry captures this collection and scans it lazily, on first use: AddMarten() is
        // routinely called before the host's own Marten registration, and a scan now would find no stores at all.
        services.TryAddSingleton(new MartenStoreRegistry(services));

        // The shared seams, answered in Marten's terms. The source provider is scoped like the tenant
        // discovery it holds: a circuit's discovered tenants and its refresh button are its own.
        services.TryAddScoped<IStudioSourceProvider, MartenStudioSourceProvider>();
        services.TryAddSingleton<IStudioLinks, MartenStudioLinks>();

        // D-CORR 7: the links a module's page makes into Documents and Streams, by type and id, with a store and a tenant and no database.
        services.TryAddSingleton<IStudioRouteLinks, MartenStudioRouteLinks>();
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

        // 1.0-P3C: Marten Studio's plan insights (P26 to P28), judged against the document tables the store in scope declares.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IPlanInsightContributor, MartenPlanInsightContributor>());

        // 1.2-INDEX-A: the Statements page's index advisor, in Marten's terms - a document table's indexes are the StoreOptions lines
        // that declare them (Postgres Studio's service takes any number of these; it has none of its own).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IIndexCandidateContributor, MartenIndexCandidateContributor>());

        // 1.2-H-C: E06's on-demand action, "Find the 20 longest streams". The runner and its flights are PostgresStudio's
        // (StudioCoreRegistration); a finding offers an action only if one is registered here.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthFindingAction, LongestStreamsAction>());

        // 1.2-SCOPE-3: Find by id, in Marten's terms. Each kind asks the data service of the page its hit opens (documents, streams and
        // natural keys, event sequences), so every probe runs under that page's gates; the scoped helper reads the event store's shape
        // once per Find and asks the store policy about the tenants a hit names.
        services.TryAddScoped<IFindScopes, FindScopes>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IFindContributor, DocumentFindContributor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IFindContributor, StreamFindContributor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IFindContributor, EventSequenceFindContributor>());

        // 1.1-NP4: "How Marten uses this connection" on Database > Connection. The data-source registry captures this collection and
        // scans it lazily, like the store registry: AddMarten() is routinely called before AddNpgsqlDataSource().
        services.TryAddSingleton(new MartenDataSourceRegistry(services));
        services.TryAddSingleton<MartenRowSizeCache>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConnectionInsightContributor, MartenConnectionInsightContributor>());

        // Reads a table's indexes for the Mode A index verdict and caches what it said, keyed by database
        // identity, for the same reason as the shared column catalog.
        services.TryAddSingleton<IndexCatalog>();

        // Scoped, because each is about one circuit.
        services.TryAddScoped<StudioScopeResolver>();
        services.TryAddScoped<TenantDiscovery>();
        services.TryAddScoped<IStoreInfoService, StoreInfoService>();
        services.TryAddScoped<DataStudio.Marten.Services.Tenants.ITenantOverviewService, DataStudio.Marten.Services.Tenants.TenantOverviewService>();
        services.TryAddScoped<IDocumentDataService, DocumentDataService>();
        services.TryAddScoped<DocumentBrowserState>();
        services.TryAddScoped<IDocumentWriteService, DocumentWriteService>();
        services.TryAddScoped<IEventDataService, EventDataService>();

        // M3: the grouped dead-letter view's read, one per database and tenant per refresh interval across circuits.
        services.TryAddSingleton<DeadLetterGroupCache>();

        // 1.2: the event flow's model, one per store, rebuilt only when the configuration moved.
        services.TryAddSingleton<EventFlowCache>();

        // 1.2-SHAPE: the shape check - a plain read under the store policy, one check per resolved scope and subject per five
        // minutes across circuits.
        services.TryAddSingleton<ShapeCheckCache>();
        services.TryAddSingleton<EventTypeListingCache>();
        services.TryAddScoped<IDocumentShapeCheck, DocumentShapeCheckService>();
        services.TryAddScoped<IEventShapeCheck, EventShapeCheckService>();
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

        // W5-API: the diagnostics API for the host's own code (D31) is the engine's, registered by StudioCoreRegistration above,
        // over this module's stores, checks and descriptor.
    }

    /// <summary>The options the Postgres and Marten modules share, as the shared validation reads them.</summary>
    private static StudioOptionsShape Shape(MartenStudioOptions options) => new(
        options.DefaultPageSize,
        options.MaxPageSize,
        options.QueryTimeout,
        options.MaxInlineDocumentBytes,
        options.MaxSqlConsoleRows,
        options.SqlConsoleRole,
        options.ExactCountThreshold,
        options.HealthInterval,
        options.Capabilities is not null,
        options.BrowsableSchemas,
        options.MaxStudioConnections,
        options.StudioApplicationName);
}
