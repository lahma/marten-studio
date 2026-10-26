using PostgresStudio.Internal;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Maintenance;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Relationships;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Storage;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace PostgresStudio.Services.Settings;

/// <summary>
/// Registers everything the shared studio renders with - the shell's services, the scope, authorization, capability
/// and audit services, the database browser, Sessions, the health advisor, the SQL console and Relationships - for
/// whichever studio's <c>Add*Studio()</c> calls it.
/// </summary>
/// <remarks>
/// <para>
/// A studio supplies what makes it itself: its <see cref="StudioRegistrationMarker" />, its <see cref="StudioDescriptor" />,
/// its <see cref="IStudioSettings" />, and - registered by its own <c>Add*Studio()</c> - its implementations of the seams.
/// Four are required, because shared services and components take them as dependencies: <see cref="IStudioSourceProvider" />,
/// <see cref="IStudioLinks" />, <see cref="ISessionLockLabeler" /> and <see cref="IDatabaseDeclarationsSource" />. The
/// rest are optional, and a studio that registers none gets what the seam's own documentation says of its absence: the
/// health advisor's <see cref="IHealthTableSource" />, <see cref="IHealthTableRemedies" />, <see cref="IHealthPlanAdvisor" />
/// and <see cref="IHorizonContributor" />, the <see cref="IStudioRuntime" /> a check's required feature is resolved against,
/// the <see cref="IRelationshipNodeSource" />, and any number of <see cref="INavIndicatorContributor" />. The SQL console
/// (<see cref="ISqlConsoleService" />) and the Relationships services are shared, and registered here.
/// </para>
/// <para>
/// <b>Nothing here changes how the host behaves.</b> No global JSON or SignalR option is touched, no middleware ordering
/// is required, and every service is registered with <c>TryAdd</c> so an application that registered its own first is
/// the one that answers. No hosted service is added but the two that already exist: the startup authorization guard and
/// the database browser's configuration notice (event 9230).
/// </para>
/// </remarks>
internal static class StudioCoreRegistration
{
    /// <summary>Registers the shared studio for the studio <paramref name="marker" /> names.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="marker">The studio being registered; a different studio already registered is refused.</param>
    /// <param name="descriptor">The studio's descriptor.</param>
    /// <param name="settings">How the studio's settings are built from the container.</param>
    /// <exception cref="InvalidOperationException">Another studio is already registered in this container.</exception>
    public static void Add(
        IServiceCollection services,
        StudioRegistrationMarker marker,
        StudioDescriptor descriptor,
        Func<IServiceProvider, IStudioSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(settings);

        StudioRegistrationMarker.Claim(services, marker);

        services.TryAddSingleton(descriptor);
        services.TryAddSingleton(settings);

        // Refuses to start an application whose mapped studio nothing authorizes. Registered here rather
        // than at the map site, because a hosted service added to a built application is too late.
        services.TryAddSingleton<StudioMappedEndpoints>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, StudioEndpointAuthorizationGuard>());

        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Deliberately no AddSignalR().AddJsonProtocol(...) and no JsonStringEnumConverter: those options
        // belong to the whole application's SignalR and its own hubs must keep their own format
        // (there is no studio hub in v1 - plan D10).
        services.AddHttpContextAccessor();

        services.TryAddSingleton<StudioCapabilityGuard>();
        services.TryAddSingleton<StudioActionLogService>();

        // DB-0: log hygiene
        // One process-wide memory of which anomalies have already been reported, so a failure on a path
        // every open page polls is a Warning once per source, database and exception type per ten minutes
        // and Debug in between. The host's TimeProvider when it registered one, the system clock when not.
        services.TryAddSingleton(static provider =>
            new StudioLogThrottle(provider.GetService<TimeProvider>() ?? TimeProvider.System));

        // Reads the database's catalog and caches what it said, keyed by database identity. A singleton
        // because what it caches - a table's physical columns - changes only with a schema migration and is
        // the same answer for every circuit; a per-circuit cache would ask the catalog once per visitor per page.
        services.TryAddSingleton<ColumnCatalog>();

        // Scoped, because all of these are about one circuit: what it is pointed at, who is driving it, and
        // what it has been told.
        services.TryAddScoped<StudioState>();
        services.TryAddScoped<IStudioScopeCatalog, StudioScopeCatalog>();
        services.TryAddScoped<StudioAuthorization>();
        services.TryAddScoped<StudioSourceResolver>();
        services.TryAddScoped<StudioActionLog>();
        services.TryAddScoped<ToastService>();

        // One snapshot per interval however many circuits are watching (D10).
        services.TryAddSingleton<StudioSnapshotCache>();

        // A factory rather than the loop itself: StudioLiveUpdates is IAsyncDisposable, and a transient
        // registration would leave the circuit's container tracking one instance per page visit for the
        // life of the circuit. The page owns the loop it creates.
        services.TryAddScoped<StudioLiveUpdatesFactory>();

        // The sidebar's badges. Scoped like the pages they point at, and reading through the same
        // services, so a badge is subject to the same source, database and tenant policies as the screen.
        services.TryAddScoped<INavIndicatorService, NavIndicatorService>();

        // DB-1: database browser. The catalog is a singleton for the same reason ColumnCatalog is: what it
        // caches is a fact about the database, the same for every circuit, and the per-visitor gate is
        // applied after the cache. The gate and the service are about one circuit's visitor, so scoped.
        // The notice logs event 9230 once at host start and changes nothing else about startup.
        services.TryAddSingleton<DatabaseCatalog>();
        services.TryAddScoped<DatabaseAccess>();
        services.TryAddScoped<IDatabaseObjectService, DatabaseObjectService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DatabaseBrowserConfigurationNotice>());

        // DB-3: table rows. Scoped, like the gate it stands behind: it remembers, for this circuit only,
        // which first page of a relation it has already audited, so a refresh is not a second opening.
        services.TryAddScoped<ITableRowService, TableRowService>();

        // DB-5: the Rows tab's per-circuit memory - "Run anyway" answers, keyset history and the page a row
        // was opened from. Scoped, so it is one browser tab's and nobody else's.
        services.TryAddScoped<DatabaseBrowserState>();

        // H1: the health advisor. The checks, their history and the one sweep per database per interval are
        // process-wide singletons - a sweep's facts are about the database and the same for every circuit -
        // and none of them runs anything on its own: no hosted service, no timer. A sweep happens when the
        // Health page or the Overview asks. What a visitor is shown, what they muted and which Critical they
        // have already been toasted about are that circuit's, so scoped.
        services.TryAddSingleton<HealthCheckRegistry>();
        services.TryAddSingleton<HealthHistory>();
        services.TryAddSingleton<HealthSweeper>();
        services.TryAddSingleton<HealthSweepCache>();

        // H7: the statement checks' memory of the texts they have read, so a statement sweep whose statements are all
        // known makes no text call - each one costs pg_stat_statements its whole query-text file.
        services.TryAddSingleton<StatementTextCache>();
        services.TryAddScoped<HealthMutes>();
        services.TryAddScoped<HealthAlerts>();
        services.TryAddScoped<IHealthService, HealthService>();

        // W5-V1: the Maintenance page. Its facts are the regular sweep's own reads, the database's and every circuit's, and so
        // is the one bloat measure per database at once; what a visitor is shown of them is the circuit's.
        services.TryAddSingleton<IMaintenanceFacts, MaintenanceFactsReader>();
        services.TryAddSingleton<BloatMeasureFlights>();
        services.TryAddScoped<IMaintenanceService, MaintenanceService>();

        // H3: Database > Sessions and the connection checks. The pool observer is a singleton because a
        // MeterListener is process-wide, and it listens only while a sweep or an open Sessions page holds a
        // lease. The session snapshot is the database's, shared by every circuit for ten seconds; what a
        // visitor is shown of it, and which first read this circuit has audited, are the circuit's.
        services.TryAddSingleton<SessionPoolObserver>();
        services.TryAddSingleton<ISessionActivityReader, SessionActivityReader>();
        services.TryAddSingleton<SessionActivitySnapshots>();
        services.TryAddScoped<ISessionActivityService, SessionActivityService>();

        // The SQL console. Scoped like every data service, and registered once under both names: the interface forwards to
        // the one console of the circuit, which a studio's own query service may also use.
        services.TryAddScoped<SqlConsoleService>();
        services.TryAddScoped<ISqlConsoleService>(static provider => provider.GetRequiredService<SqlConsoleService>());

        // Relationships: the foreign-key graph and its reads. A studio adds what it knows beside the catalog through its
        // IRelationshipNodeSource, which the data service takes as optional - a studio with none draws plain tables.
        services.TryAddScoped<RelationshipReads>();
        services.TryAddScoped<IRelationshipDataService, RelationshipDataService>();

        // W5-CAP: Storage. The facts are the database's - one read per database per HealthInterval, single flight, the same
        // for every circuit - and what a visitor is shown of them is the circuit's.
        services.TryAddSingleton<IStorageFacts, StorageFactsReader>();
        services.TryAddScoped<IStorageService, StorageService>();

        // W5-R2: the Replication page. The facts are the database's, read once per HealthInterval for every circuit; what a
        // visitor is shown of them, which names it has audited and its one tablespace measurement at a time are the circuit's.
        services.TryAddSingleton<IReplicationFactsReader, ReplicationFactsReader>();
        services.TryAddSingleton<ReplicationFactsCache>();
        services.TryAddScoped<IReplicationService, ReplicationService>();
    }
}
