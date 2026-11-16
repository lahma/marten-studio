using DataStudio.Services.Settings;
using DataStudio.Services;
using DataStudio.Postgres.Internal.Sql;
using DataStudio.Postgres.Services.Database;
using DataStudio.Postgres.Services.Find;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Health.Checks;
using DataStudio.Postgres.Services.Live;
using DataStudio.Postgres.Services.Maintenance;
using DataStudio.Postgres.Services.Query;
using DataStudio.Postgres.Services.Relationships;
using DataStudio.Postgres.Services.Replication;
using DataStudio.Postgres.Services.Server;
using DataStudio.Postgres.Services.Sources;
using DataStudio.Postgres.Services.Statements;
using DataStudio.Postgres.Services.Storage;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DataStudio.Postgres.Services.Settings;

/// <summary>
/// Registers everything the database pages render with - the scope, authorization, capability and audit services, the database
/// browser, Sessions, the health advisor, the SQL console and Relationships - for whichever database module's <c>Add…()</c> calls it. The
/// shell's own services (the settings, the Blazor registration, the guard, the preferences, the Activity ring) are <c>AddDataStudio()</c>'s.
/// </summary>
/// <remarks>
/// <para>
/// A module supplies what makes it itself: its <see cref="StudioRegistrationMarker" />, its <see cref="StudioDescriptor" />,
/// its <see cref="IStudioSettings" />, and - registered by its own <c>Add…()</c> - its implementations of the seams.
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
/// the one that answers. The one hosted service is the database browser's configuration notice (event 9230).
/// </para>
/// </remarks>
internal static class StudioCoreRegistration
{
    /// <summary>Registers the database pages' services for the module <paramref name="marker" /> names.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="marker">The module being registered; a different database module already registered is refused.</param>
    /// <param name="descriptor">The module's descriptor.</param>
    /// <param name="settings">How the module's settings are built from the container.</param>
    /// <exception cref="InvalidOperationException">Another database module is already registered in this container.</exception>
    public static void Add(
        IServiceCollection services,
        StudioRegistrationMarker marker,
        StudioDescriptor descriptor,
        Func<IServiceProvider, IStudioSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(settings);

        // The engine's own marker first: a module added beside AddPostgresDiagnostics() is refused before any marker or service of
        // the module is registered.
        DiagnosticsRegistrationMarker.Claim(services, marker.RegisteredBy, byStudio: true);
        StudioRegistrationMarker.Claim(services, marker);

        // The diagnostics engine, which a database module registers as part of itself: refused beside AddPostgresDiagnostics(), in
        // either order. The one settings object the module's registration builds answers the engine's interface too.
        DiagnosticsCoreRegistration.Add(
            services,
            descriptor,
            static provider => provider.GetRequiredService<IStudioSettings>(),
            marker.RegisteredBy,
            byStudio: true);

        // The database's own descriptor, as itself: the static instance, never composed.
        services.TryAddSingleton(descriptor);

        services.TryAddSingleton(settings);

        // The database as a module of the shell: the root, always there (D34).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStudioModule, DatabaseStudioModule>());

        // The shell's IShellSettings is the shell's own object, never this module's: the module's settings answer the shell's members from it.

        services.TryAddSingleton<StudioCapabilityGuard>();
        services.TryAddSingleton<StudioActionLogService>();

        // Reads the database's catalog and caches what it said, keyed by database identity. A singleton because what it
        // caches - a table's physical columns - changes only with a schema migration and is the same answer for every circuit;
        // a per-circuit cache would ask the catalog once per visitor per page.
        services.TryAddSingleton<ColumnCatalog>();

        // Scoped, because all of these are about one circuit: what it is pointed at and who is driving it.
        services.TryAddScoped<StudioState>();

        // What the database's scope gate answered for this circuit: written by the module's circuit host, read by its frame.
        services.TryAddScoped<DatabaseScopeAccess>();
        services.TryAddScoped<IStudioScopeCatalog, StudioScopeCatalog>();
        services.TryAddScoped<StudioAuthorization>();
        services.TryAddScoped<StudioSourceResolver>();
        services.TryAddScoped<StudioActionLog>();

        // The database's half of what a visitor may read of the ring: its entries are judged here, as every module's are by its own gate.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStudioAuditScopeGate, DatabaseAuditGate>());

        // The words the shell's live-update loop logs in are this module's: its own log methods, chosen by its product, in place of the
        // shell's default - Replace, so that the answer does not depend on which of the two registered first. The shell's loop names no throttle:
        // the database side closes over the container's, so event 9220 takes its level where it is logged.
        services.Replace(ServiceDescriptor.Singleton(static provider => DatabaseLiveLogs.For(
            provider.GetRequiredService<IStudioSettings>().Text.Product,
            provider.GetService<StudioLogThrottle>())));

        // The sidebar's badges. Scoped like the pages they point at, and reading through the same
        // services, so a badge is subject to the same source, database and tenant policies as the screen.
        services.TryAddScoped<INavIndicatorService, NavIndicatorService>();

        // The database's half of the rail (D34): its links carry the scope, two entries follow the scope's state, and the badges are read here.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStudioModuleRail, DatabaseModuleRail>());

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

        // H1: what a visitor is shown of the sweep, what they muted and which Critical they have already been toasted about
        // are that circuit's, so scoped.
        // Wave 6 (SEC2): the protector a browser's mute key is kept under, created once per process from the host's Data
        // Protection - which AddRazorComponents() above has already registered - or, in a container without it, from an
        // in-memory provider this constructs and registers nowhere.
        services.TryAddSingleton(static provider =>
            new HealthMuteKeys(provider.GetService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
        services.TryAddScoped<HealthMutes>();
        services.TryAddScoped<HealthAlerts>();
        services.TryAddScoped<IHealthService, HealthService>();

        // 1.2-H-C: a finding's on-demand actions. The seam's runner is the circuit's (one press at a time in a tab); one run of an
        // action per database at once is the process's. With no IHealthFindingAction registered a finding offers nothing and the page
        // draws as it always has; 1.2-INDEX-B registers the two index what-ifs below, which both studios have (T01 and T15 are in both).
        services.TryAddSingleton<HealthActionFlights>();
        services.TryAddScoped<IHealthActionService, HealthActionService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IHealthFindingAction, ForeignKeyWhatIfAction>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IHealthFindingAction, SequentialScanWhatIfAction>());

        // W5-V1: the Maintenance page. Its facts are the engine's (the regular sweep's own reads); the one bloat measure per
        // database at once is the process's, and what a visitor is shown of them is the circuit's.
        services.TryAddSingleton<BloatMeasureFlights>();
        services.TryAddScoped<IMaintenanceNameGate, DatabaseAccessNameGate>();
        services.TryAddScoped<IMaintenanceService, MaintenanceService>();

        // H3: Database > Sessions: what a visitor is shown of the engine's session snapshot, and which first read this circuit has
        // audited, are the circuit's.
        services.TryAddScoped<SessionHolderNames>();
        services.TryAddScoped<ISessionActivityService, SessionActivityService>();

        // The SQL console. Scoped like every data service, and registered once under both names: the interface forwards to
        // the one console of the circuit, which a studio's own query service may also use.
        services.TryAddScoped<SqlConsoleService>();
        services.TryAddScoped<ISqlConsoleService>(static provider => provider.GetRequiredService<SqlConsoleService>());

        // 0.10-A: the facts beside a plan the Plan page ran - the console's gate, then each relation's own.
        services.TryAddScoped<IPlanFactsService, PlanFactsService>();

        // Relationships: the foreign-key graph and its reads. A studio adds what it knows beside the catalog through its
        // IRelationshipNodeSource, which the data service takes as optional - a studio with none draws plain tables.
        services.TryAddScoped<RelationshipReads>();
        services.TryAddScoped<IRelationshipDataService, RelationshipDataService>();

        // W5-CAP: Storage. The facts are the engine's; what a visitor is shown of them is the circuit's.
        services.TryAddScoped<IStorageService, StorageService>();

        // W5-R2: the Replication page. The facts are the engine's; what a visitor is shown of them, which names it has
        // audited and its one tablespace measurement at a time are the circuit's.
        services.TryAddScoped<IReplicationService, ReplicationService>();

        // W5-S2: the Server page. The facts are the engine's; what a visitor is shown of them is the circuit's.
        services.TryAddScoped<IServerPageService, ServerPageService>();

        // W5-AUR: the Server page's Aurora section, whose facts are the engine's and whose view is the circuit's.
        services.TryAddScoped<Aurora.IAuroraService, Aurora.AuroraService>();

        // 1.1-NP3: Database > Connection. Its facts are the sweep's own shared read where a fresh sweep holds it and one read per
        // HealthInterval otherwise (a singleton: a fact about the database, the same for every circuit); what a visitor is shown of
        // them, and which first reveal of application sessions this circuit has audited, are the circuit's. A studio adds its own
        // knowledge through IConnectionInsightContributor, which the service takes as any number of.
        services.TryAddSingleton<Connection.IConnectionFactsSource, Connection.ConnectionFactsSource>();
        services.TryAddScoped<Connection.IConnectionInsightService, Connection.ConnectionInsightService>();

        // 1.1-PASTE: "Compare a connection string" on that page. The same gate, no audit, no logger: the pasted text is a parameter
        // that is parsed and dropped.
        services.TryAddScoped<Connection.IConnectionCompareService, Connection.ConnectionCompareService>();
        // W5-Q1: the Statements page. The facts are the engine's; what a visitor is shown of them, and which first read this
        // circuit has audited, are the circuit's.
        // X1: the drill-in's planner - stateless, its plans the visitor's own, run as SqlConsoleRole on a connection of each
        // plan's own - and the per-visitor service it answers to.
        services.TryAddSingleton<StatementPlanReader>();
        services.TryAddScoped<IStatementsService, StatementsService>();
        // 1.2-H-A: "Mark" on Statements, Storage and Maintenance. The circuit's, in memory, numbers only - never stored, never shared.
        services.TryAddScoped<SinceMarks>();
        // X2: the what-if engine - hypopg's availability cached per database, every hypothetical index reset on the connection
        // it was made on - shared by the console, the drill-in and MartenStudio's advisors.
        services.TryAddSingleton<HypotheticalIndexReader>();
        // 1.2-INDEX-A: the index advisor on Statements. One run per database at once is the process's; what a visitor is shown of it
        // is the circuit's. The contributors (IIndexCandidateContributor) are a studio's own and need no registration here: Postgres
        // Studio has none, and the service takes any number of them.
        services.TryAddSingleton<IndexAdvisorFlights>();

        // W5-API: the Health page's Snapshot menu. The circuit's, because it asks each page's service as this visitor; it never
        // reaches the diagnostics API, which each Add*Studio registers for host code alone.
        services.TryAddScoped<Diagnostics.VisitorSnapshotBuilder>();

        // 1.2-H-B: "Compare with an earlier snapshot". The visitor's gate first, then the file; no logger, no audit, no cache.
        services.TryAddScoped<Diagnostics.ISnapshotCompareService, Diagnostics.SnapshotCompareService>();

        // 1.2-SCOPE-3: Find by id, the header's box. The service is the circuit's (one Find at a time, a newer one cancelling the one
        // in flight); each kind is a contributor that asks the service of the page its hit opens, under that page's gates. These two
        // are PostgresStudio's own; a studio adds its kinds beside them with TryAddEnumerable, and a studio with none draws no box.
        services.TryAddScoped<FindService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IFindContributor, RelationFindContributor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IFindContributor, StatementFindContributor>());
    }
}
