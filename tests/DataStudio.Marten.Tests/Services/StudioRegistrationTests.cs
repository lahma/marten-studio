using System.Reflection;
using System.Text;

using DataStudio.Internal;
using DataStudio.Services.Settings;
using DataStudio.Services;
using Marten;

using DataStudio.Marten.Internal.Sql;
using DataStudio.Marten.Services;
using DataStudio.Marten.Services.Configuration;
using DataStudio.Marten.Services.Connection;
using DataStudio.Postgres.Services.Connection;
using DataStudio.Postgres.Services.Database;
using DataStudio.Marten.Services.Documents;
using DataStudio.Marten.Services.Events;
using DataStudio.Marten.Services.Health.Checks;
using DataStudio.Marten.Services.Live;
using DataStudio.Marten.Services.Projections;
using DataStudio.Marten.Services.Query;
using DataStudio.Marten.Services.Schema;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Postgres.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using DataStudio.Postgres.Internal.Sql;
using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Health.Checks;
using DataStudio.Postgres.Services.Live;
using DataStudio.Postgres.Services.Query;
using DataStudio.Postgres.Services.Relationships;
using DataStudio.Postgres.Services.Replication;
using DataStudio.Postgres.Services.Server;

namespace DataStudio.Marten.Tests.Services;

/// <summary>
/// What <c>AddDataStudio().AddMarten()</c> puts in a host's container, pinned: moving a shared service's registration from
/// <c>AddMarten()</c> into <see cref="StudioCoreRegistration" /> (PS-1e) must leave a Marten module host with the
/// same services, the same lifetimes and the same implementations - and every enumerable service in the same order.
/// </summary>
/// <remarks>
/// The list was taken from <c>AddMarten()</c> at the commit before the move and is compared after it. Only the
/// studio's own registrations are listed - a descriptor whose service, implementation or factory result is a studio type,
/// or a generic over one - because the framework's (<c>AddRazorComponents()</c>, <c>AddHttpContextAccessor()</c>) are
/// the framework's to change. Types are named with <c>typeof</c>, so the namespace move and PS-2's type renames carry
/// the list with them. A factory row says only that the service is a factory registration: the delegate's return type is
/// the service type itself, so what a factory makes is pinned separately, by resolving it
/// (<see cref="Every_factory_makes_what_it_made_before_and_the_console_is_one_instance_per_circuit" />).
/// </remarks>
public class StudioRegistrationTests
{
    private enum How
    {
        Type,
        Instance,
        Factory,
    }

    /// <summary>
    /// <c>AddMarten()</c>'s registrations at the commit before PS-1e, less the one PS-1e removed on purpose:
    /// <c>IHealthRemedyProvider</c>, folded into <see cref="IHealthTableRemedies" /> (its one instance,
    /// <c>MartenHealthRemedyProvider</c>, is still registered as that).
    /// </summary>
    private static readonly (Type Service, ServiceLifetime Lifetime, How How, Type Implementation)[] Expected =
    [
        (typeof(StudioRegistrationMarker), ServiceLifetime.Singleton, How.Instance, typeof(StudioRegistrationMarker)),

        // 0.11-C: the diagnostics engine, registered by the studio as part of itself (DiagnosticsCoreRegistration): its marker,
        // and the same descriptor and settings the studio's own services read, under the engine's own interfaces.
        (typeof(DiagnosticsRegistrationMarker), ServiceLifetime.Singleton, How.Instance, typeof(DiagnosticsRegistrationMarker)),
        (typeof(DiagnosticsDescriptor), ServiceLifetime.Singleton, How.Instance, typeof(StudioDescriptor)),
        (typeof(IDiagnosticsSettings), ServiceLifetime.Singleton, How.Factory, typeof(IDiagnosticsSettings)),

        // Shell A2a: the database's own descriptor is registered as itself, never composed; the shell's is composed from the registered modules,
        // of which the database is one (the root), registered beside the settings it is built from.
        (typeof(StudioDescriptor), ServiceLifetime.Singleton, How.Instance, typeof(StudioDescriptor)),
        (typeof(StudioShellDescriptor), ServiceLifetime.Singleton, How.Factory, typeof(StudioShellDescriptor)),
        (typeof(IStudioSettings), ServiceLifetime.Singleton, How.Factory, typeof(IStudioSettings)),
        (typeof(IStudioModule), ServiceLifetime.Singleton, How.Type, typeof(DatabaseStudioModule)),

        // v2 step 4: the shell's own settings, over the shell's options, registered by AddDataStudio() - never a module's object - with its validator
        // and its marker, so that a map call without the shell can say so.
        (typeof(IShellSettings), ServiceLifetime.Singleton, How.Type, typeof(ShellSettings)),
        (typeof(IValidateOptions<DataStudioOptions>), ServiceLifetime.Singleton, How.Type, typeof(DataStudioOptionsValidator)),
        (typeof(ShellRegistrationMarker), ServiceLifetime.Singleton, How.Type, typeof(ShellRegistrationMarker)),
        (typeof(StudioMappedEndpoints), ServiceLifetime.Singleton, How.Type, typeof(StudioMappedEndpoints)),
        (typeof(IHostedService), ServiceLifetime.Singleton, How.Type, typeof(StudioEndpointAuthorizationGuard)),
        (typeof(StudioCapabilityGuard), ServiceLifetime.Singleton, How.Type, typeof(StudioCapabilityGuard)),
        (typeof(StudioActionLogService), ServiceLifetime.Singleton, How.Type, typeof(StudioActionLogService)),
        (typeof(StudioLogThrottle), ServiceLifetime.Singleton, How.Factory, typeof(StudioLogThrottle)),
        (typeof(ColumnCatalog), ServiceLifetime.Singleton, How.Type, typeof(ColumnCatalog)),
        (typeof(StudioPreferences), ServiceLifetime.Scoped, How.Type, typeof(StudioPreferences)),
        (typeof(StudioState), ServiceLifetime.Scoped, How.Type, typeof(StudioState)),
        (typeof(DatabaseScopeAccess), ServiceLifetime.Scoped, How.Type, typeof(DatabaseScopeAccess)),
        (typeof(IStudioScopeCatalog), ServiceLifetime.Scoped, How.Type, typeof(StudioScopeCatalog)),
        (typeof(StudioAuthorization), ServiceLifetime.Scoped, How.Type, typeof(StudioAuthorization)),
        (typeof(StudioSourceResolver), ServiceLifetime.Scoped, How.Type, typeof(StudioSourceResolver)),
        // Shell A2c: the ring's neutral way in and out, and the database's own gate on what a visitor may read of it.
        (typeof(StudioActivity), ServiceLifetime.Scoped, How.Type, typeof(StudioActivity)),
        (typeof(StudioActionLog), ServiceLifetime.Scoped, How.Type, typeof(StudioActionLog)),
        (typeof(IStudioAuditScopeGate), ServiceLifetime.Scoped, How.Type, typeof(DatabaseAuditGate)),
        (typeof(ToastService), ServiceLifetime.Scoped, How.Type, typeof(ToastService)),
        (typeof(StudioSnapshotCache), ServiceLifetime.Singleton, How.Type, typeof(StudioSnapshotCache)),
        // The shell's live-update loop logs in the studio's words: this studio's own log methods, chosen by its product.
        (typeof(StudioLiveLog), ServiceLifetime.Singleton, How.Factory, typeof(StudioLiveLog)),
        (typeof(StudioLiveUpdatesFactory), ServiceLifetime.Scoped, How.Type, typeof(StudioLiveUpdatesFactory)),
        (typeof(INavIndicatorService), ServiceLifetime.Scoped, How.Type, typeof(NavIndicatorService)),

        // Shell A2b: the database's half of the rail - its links, two entries' conditions and its badges.
        (typeof(IStudioModuleRail), ServiceLifetime.Scoped, How.Type, typeof(DatabaseModuleRail)),
        (typeof(DatabaseCatalog), ServiceLifetime.Singleton, How.Type, typeof(DatabaseCatalog)),
        (typeof(DatabaseAccess), ServiceLifetime.Scoped, How.Type, typeof(DatabaseAccess)),
        (typeof(IDatabaseObjectService), ServiceLifetime.Scoped, How.Type, typeof(DatabaseObjectService)),
        (typeof(IHostedService), ServiceLifetime.Singleton, How.Type, typeof(DatabaseBrowserConfigurationNotice)),
        (typeof(ITableRowService), ServiceLifetime.Scoped, How.Type, typeof(TableRowService)),
        (typeof(DatabaseBrowserState), ServiceLifetime.Scoped, How.Type, typeof(DatabaseBrowserState)),
        (typeof(HealthCheckRegistry), ServiceLifetime.Singleton, How.Type, typeof(HealthCheckRegistry)),

        // W5-API: the bound on databases with state (API design §3.5).
        (typeof(HealthDatabaseBound), ServiceLifetime.Singleton, How.Type, typeof(HealthDatabaseBound)),
        (typeof(HealthHistory), ServiceLifetime.Singleton, How.Type, typeof(HealthHistory)),
        (typeof(HealthSweeper), ServiceLifetime.Singleton, How.Type, typeof(HealthSweeper)),
        (typeof(HealthSweepCache), ServiceLifetime.Singleton, How.Type, typeof(HealthSweepCache)),
        (typeof(StatementTextCache), ServiceLifetime.Singleton, How.Type, typeof(StatementTextCache)),

        // Wave 6 (SEC2): the protector a browser's mute key is kept under, once per process, from the host's Data Protection.
        (typeof(HealthMuteKeys), ServiceLifetime.Singleton, How.Factory, typeof(HealthMuteKeys)),
        (typeof(HealthMutes), ServiceLifetime.Scoped, How.Type, typeof(HealthMutes)),
        (typeof(HealthAlerts), ServiceLifetime.Scoped, How.Type, typeof(HealthAlerts)),
        (typeof(IHealthService), ServiceLifetime.Scoped, How.Type, typeof(HealthService)),

        // 1.2-H-C: a finding's on-demand actions - the runner is the circuit's, one run of an action per database is the process's.
        (typeof(HealthActionFlights), ServiceLifetime.Singleton, How.Type, typeof(HealthActionFlights)),
        (typeof(IHealthActionService), ServiceLifetime.Scoped, How.Type, typeof(HealthActionService)),
        (typeof(IHealthFindingAction), ServiceLifetime.Scoped, How.Type, typeof(ForeignKeyWhatIfAction)),
        (typeof(IHealthFindingAction), ServiceLifetime.Scoped, How.Type, typeof(SequentialScanWhatIfAction)),

        // 1.2-SCOPE-3: Find by id - the circuit's service and the Postgres module's own two kinds.
        (typeof(DataStudio.Postgres.Services.Find.FindService), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Find.FindService)),
        (typeof(DataStudio.Postgres.Services.Find.IFindContributor), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Find.RelationFindContributor)),
        (typeof(DataStudio.Postgres.Services.Find.IFindContributor), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Find.StatementFindContributor)),
        (typeof(DataStudio.Postgres.Services.Maintenance.IMaintenanceFacts), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Maintenance.MaintenanceFactsReader)),
        (typeof(DataStudio.Postgres.Services.Maintenance.BloatMeasureFlights), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Maintenance.BloatMeasureFlights)),
        (typeof(DataStudio.Postgres.Services.Maintenance.IMaintenanceNameGate), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Maintenance.DatabaseAccessNameGate)),
        (typeof(DataStudio.Postgres.Services.Maintenance.IMaintenanceService), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Maintenance.MaintenanceService)),
        (typeof(SessionPoolObserver), ServiceLifetime.Singleton, How.Type, typeof(SessionPoolObserver)),
        (typeof(ISessionActivityReader), ServiceLifetime.Singleton, How.Type, typeof(SessionActivityReader)),
        (typeof(SessionActivitySnapshots), ServiceLifetime.Singleton, How.Type, typeof(SessionActivitySnapshots)),
        (typeof(SessionHolderNames), ServiceLifetime.Scoped, How.Type, typeof(SessionHolderNames)),
        (typeof(ISessionActivityService), ServiceLifetime.Scoped, How.Type, typeof(SessionActivityService)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        // 1.0 (#44): MaxStudioConnections and StudioApplicationName, two more of the shared bounds.
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        // 1.2-HEADER: ApplicationPath, one more of the shared bounds.
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenOptions>)),
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Factory, typeof(IValidateOptions<MartenOptions>)),
        // 1.2-PROJ: ProjectionThresholds' validator, a type of its own because its message names the projection.
        (typeof(IValidateOptions<MartenOptions>), ServiceLifetime.Singleton, How.Type, typeof(ProjectionThresholdsValidator)),
        (typeof(MartenStoreRegistry), ServiceLifetime.Singleton, How.Instance, typeof(MartenStoreRegistry)),
        (typeof(IStudioSourceProvider), ServiceLifetime.Scoped, How.Type, typeof(MartenSourceProvider)),
        (typeof(IStudioLinks), ServiceLifetime.Singleton, How.Type, typeof(MartenLinks)),
        (typeof(IStudioRouteLinks), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Marten.Services.Links.MartenRouteLinks)),
        (typeof(ISessionLockLabeler), ServiceLifetime.Singleton, How.Type, typeof(MartenSessionLockLabeler)),
        (typeof(IDatabaseDeclarationsSource), ServiceLifetime.Scoped, How.Type, typeof(MartenDatabaseDeclarationsSource)),
        (typeof(IHealthTableSource), ServiceLifetime.Singleton, How.Type, typeof(MartenHealthTableSource)),
        (typeof(IHealthTableRemedies), ServiceLifetime.Singleton, How.Instance, typeof(MartenHealthRemedyProvider)),
        (typeof(IHealthPlanAdvisor), ServiceLifetime.Singleton, How.Instance, typeof(MartenStatementPlanAdvisor)),

        // W5-Q1: the Statements page's groups, in Marten's terms - added in 0.5, not moved.
        (typeof(IStatementClassifier), ServiceLifetime.Singleton, How.Instance, typeof(MartenStatementClassifier)),
        (typeof(IHorizonContributor), ServiceLifetime.Singleton, How.Type, typeof(MartenHorizonContributor)),
        (typeof(IStudioRuntime), ServiceLifetime.Singleton, How.Factory, typeof(IStudioRuntime)),
        (typeof(SqlConsoleService), ServiceLifetime.Scoped, How.Type, typeof(SqlConsoleService)),
        (typeof(ISqlConsoleService), ServiceLifetime.Scoped, How.Factory, typeof(ISqlConsoleService)),

        // 0.10-A: the facts beside a plan the Plan page ran, a new shared service and the only registration 0.10-A adds.
        (typeof(IPlanFactsService), ServiceLifetime.Scoped, How.Type, typeof(PlanFactsService)),
        (typeof(IRelationshipNodeSource), ServiceLifetime.Scoped, How.Type, typeof(MartenRelationshipNodeSource)),
        (typeof(INavIndicatorContributor), ServiceLifetime.Scoped, How.Type, typeof(MartenNavIndicatorContributor)),

        // 1.0-P3C: Data Studio's plan insights P26 to P28, the one registration the packet adds.
        (typeof(IPlanInsightContributor), ServiceLifetime.Scoped, How.Type, typeof(MartenPlanInsightContributor)),

        // 1.2-INDEX-A: the index advisor's Marten half, the one registration the packet adds.
        (typeof(IIndexCandidateContributor), ServiceLifetime.Scoped, How.Type, typeof(MartenIndexCandidateContributor)),

        // 1.2-H-C: E06's on-demand action, the one finding action 1.2 registers.
        (typeof(IHealthFindingAction), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Marten.Services.Health.LongestStreamsAction)),

        // 1.2-SCOPE-3: Find by id, in Marten's terms - three kinds and the helper they share.
        (typeof(DataStudio.Marten.Services.Find.IFindScopes), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Marten.Services.Find.FindScopes)),
        (typeof(DataStudio.Postgres.Services.Find.IFindContributor), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Marten.Services.Find.DocumentFindContributor)),
        (typeof(DataStudio.Postgres.Services.Find.IFindContributor), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Marten.Services.Find.StreamFindContributor)),
        (typeof(DataStudio.Postgres.Services.Find.IFindContributor), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Marten.Services.Find.EventSequenceFindContributor)),

        // 1.1-NP4: "How Marten uses this connection" - the data-source registry, the deeper checks' average sizes and the contributor.
        (typeof(MartenDataSourceRegistry), ServiceLifetime.Singleton, How.Instance, typeof(MartenDataSourceRegistry)),
        (typeof(MartenRowSizeCache), ServiceLifetime.Singleton, How.Type, typeof(MartenRowSizeCache)),
        (typeof(IConnectionInsightContributor), ServiceLifetime.Scoped, How.Type, typeof(MartenConnectionInsightContributor)),
        (typeof(IndexCatalog), ServiceLifetime.Singleton, How.Type, typeof(IndexCatalog)),
        (typeof(StudioScopeResolver), ServiceLifetime.Scoped, How.Type, typeof(StudioScopeResolver)),
        (typeof(TenantDiscovery), ServiceLifetime.Scoped, How.Type, typeof(TenantDiscovery)),
        (typeof(IStoreInfoService), ServiceLifetime.Scoped, How.Type, typeof(StoreInfoService)),
        (typeof(DataStudio.Marten.Services.Tenants.ITenantOverviewService), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Marten.Services.Tenants.TenantOverviewService)),
        (typeof(IDocumentDataService), ServiceLifetime.Scoped, How.Type, typeof(DocumentDataService)),
        (typeof(DocumentBrowserState), ServiceLifetime.Scoped, How.Type, typeof(DocumentBrowserState)),
        (typeof(IDocumentWriteService), ServiceLifetime.Scoped, How.Type, typeof(DocumentWriteService)),
        (typeof(IEventDataService), ServiceLifetime.Scoped, How.Type, typeof(EventDataService)),

        // M3: the grouped dead-letter view's read, shared across circuits for one refresh interval - added in 0.6.
        (typeof(DeadLetterGroupCache), ServiceLifetime.Singleton, How.Type, typeof(DeadLetterGroupCache)),

        // 1.2 (M6): the event flow's model, one per store, rebuilt only when the configuration moved.
        (typeof(EventFlowCache), ServiceLifetime.Singleton, How.Type, typeof(EventFlowCache)),

        // 1.2-SHAPE: the shape check - the documents' check service, and the cache that makes it one read per scope and subject.
        (typeof(DataStudio.Marten.Services.Shapes.ShapeCheckCache), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Marten.Services.Shapes.ShapeCheckCache)),
        (typeof(EventTypeListingCache), ServiceLifetime.Singleton, How.Type, typeof(EventTypeListingCache)),
        (typeof(DataStudio.Marten.Services.Shapes.IDocumentShapeCheck), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Marten.Services.Shapes.DocumentShapeCheckService)),
        (typeof(DataStudio.Marten.Services.Shapes.IEventShapeCheck), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Marten.Services.Shapes.EventShapeCheckService)),
        (typeof(ISchemaDataService), ServiceLifetime.Scoped, How.Type, typeof(SchemaDataService)),
        (typeof(IConfigurationService), ServiceLifetime.Scoped, How.Type, typeof(ConfigurationService)),
        (typeof(IQueryService), ServiceLifetime.Scoped, How.Type, typeof(QueryService)),
        (typeof(ILinqQueryService), ServiceLifetime.Scoped, How.Type, typeof(LinqQueryService)),
        (typeof(RelationshipReads), ServiceLifetime.Scoped, How.Type, typeof(RelationshipReads)),
        (typeof(IRelationshipDataService), ServiceLifetime.Scoped, How.Type, typeof(RelationshipDataService)),

        // W5-CAP: Storage - added in 0.5, not moved; the facts are the database's, the view the circuit's.
        (typeof(DataStudio.Postgres.Services.Storage.IStorageFacts), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Storage.StorageFactsReader)),
        (typeof(DataStudio.Postgres.Services.Storage.IStorageService), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Storage.StorageService)),

        // W5-R2: Replication.
        (typeof(IReplicationFactsReader), ServiceLifetime.Singleton, How.Type, typeof(ReplicationFactsReader)),
        (typeof(ReplicationFactsCache), ServiceLifetime.Singleton, How.Type, typeof(ReplicationFactsCache)),
        (typeof(IReplicationService), ServiceLifetime.Scoped, How.Type, typeof(ReplicationService)),

        // W5-S2: Server.
        (typeof(IServerSettingsFactsReader), ServiceLifetime.Singleton, How.Type, typeof(ServerSettingsFactsReader)),
        (typeof(ServerSettingsFactsCache), ServiceLifetime.Singleton, How.Type, typeof(ServerSettingsFactsCache)),
        (typeof(IServerPageService), ServiceLifetime.Scoped, How.Type, typeof(ServerPageService)),
        (typeof(ExtensionUpdatePaths), ServiceLifetime.Singleton, How.Type, typeof(ExtensionUpdatePaths)),

        // W5-AUR: the host's capacity source, and the Server page's Aurora section.
        (typeof(DataStudio.Postgres.Services.Aurora.CapacityReader), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Aurora.CapacityReader)),
        (typeof(DataStudio.Postgres.Services.Aurora.IAuroraFactsReader), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Aurora.AuroraFactsReader)),
        (typeof(DataStudio.Postgres.Services.Aurora.AuroraFactsCache), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Aurora.AuroraFactsCache)),
        (typeof(DataStudio.Postgres.Services.Aurora.IAuroraService), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Aurora.AuroraService)),

        // 1.1-NP3: Database > Connection - the facts the sweep's own where a fresh sweep holds them, the view the circuit's.
        (typeof(DataStudio.Postgres.Services.Connection.IConnectionFactsSource), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Connection.ConnectionFactsSource)),
        (typeof(DataStudio.Postgres.Services.Connection.IConnectionInsightService), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Connection.ConnectionInsightService)),

        // 1.1-PASTE: "Compare a connection string" on that page - the same gate, no logger, nothing kept.
        (typeof(DataStudio.Postgres.Services.Connection.IConnectionCompareService), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Connection.ConnectionCompareService)),

        // W5-Q1: Statements - the facts are the statement sweep's, the view the circuit's.
        (typeof(DataStudio.Postgres.Services.Statements.IStatementFacts), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Statements.StatementFactsReader)),
        // 0.6 (X1): the Statements drill-in's planner, stateless and the process's.
        (typeof(DataStudio.Postgres.Services.Statements.StatementPlanReader), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Statements.StatementPlanReader)),
        (typeof(DataStudio.Postgres.Services.Statements.IStatementsService), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Statements.StatementsService)),

        // 1.2-H-A: Mark on Statements, Storage and Maintenance.
        (typeof(SinceMarks), ServiceLifetime.Scoped, How.Type, typeof(SinceMarks)),
        // 0.6 (X2): the what-if engine - hypopg's availability cached per database, every hypothetical index reset - the process's.
        (typeof(DataStudio.Postgres.Services.Query.HypotheticalIndexReader), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Query.HypotheticalIndexReader)),
        (typeof(DataStudio.Postgres.Services.Statements.IndexAdvisorFlights), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Statements.IndexAdvisorFlights)),

        // W5-API: the Health page's Snapshot menu, the circuit's - built from the page services as the visitor.
        (typeof(DataStudio.Postgres.Services.Diagnostics.VisitorSnapshotBuilder), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Diagnostics.VisitorSnapshotBuilder)),

        // 1.2-H-B: "Compare with an earlier snapshot", the circuit's - the visitor's gated view first, then the file.
        (typeof(DataStudio.Postgres.Services.Diagnostics.ISnapshotCompareService), ServiceLifetime.Scoped, How.Type, typeof(DataStudio.Postgres.Services.Diagnostics.SnapshotCompareService)),
        (typeof(StudioLiveState), ServiceLifetime.Singleton, How.Type, typeof(StudioLiveState)),
        (typeof(StudioOperationTracker), ServiceLifetime.Singleton, How.Type, typeof(StudioOperationTracker)),
        (typeof(DaemonControlState), ServiceLifetime.Singleton, How.Type, typeof(DaemonControlState)),

        // 0.6, M2: the projections screen's own samples - a singleton fed by the snapshot read, bounded per database.
        (typeof(ProjectionLagHistory), ServiceLifetime.Singleton, How.Type, typeof(ProjectionLagHistory)),
        (typeof(DaemonAccessor), ServiceLifetime.Scoped, How.Type, typeof(DaemonAccessor)),
        (typeof(IProjectionDataService), ServiceLifetime.Scoped, How.Type, typeof(ProjectionDataService)),

        // W5-API: the diagnostics API for the host's own code (D31) - added in 0.5, inert until host code calls it.
        (typeof(DataStudio.Postgres.Services.Diagnostics.FreshReadLimiter), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Diagnostics.FreshReadLimiter)),
        (typeof(DataStudio.Postgres.IPostgresDiagnostics), ServiceLifetime.Singleton, How.Type, typeof(DataStudio.Postgres.Services.Diagnostics.PostgresDiagnosticsService)),
    ];

    [Fact]
    public void AddDataStudio_and_AddMarten_register_the_same_services_with_the_same_lifetimes_and_order_as_before_the_move()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddMarten();

        string[] actual = Describe(services.Select(Read));
        string[] expected = Describe(Expected);

        expected.Should().HaveCount(Expected.Length, "the studio filter keeps every row of the expected list, or the comparison skips some");
        actual.Should().Equal(expected, "the registrations a Marten module host gets are unchanged in effect:" + Environment.NewLine + Listing(services));
    }

    /// <summary>What each factory registration of the studio makes, by its concrete type.</summary>
    private static readonly Dictionary<Type, Type> FactoryResults = new()
    {
        [typeof(IDiagnosticsSettings)] = typeof(MartenSettings),
        [typeof(IStudioSettings)] = typeof(MartenSettings),
        [typeof(StudioLogThrottle)] = typeof(StudioLogThrottle),
        [typeof(IStudioRuntime)] = typeof(MartenHealthRuntime),

        // D34: the shell's descriptor is composed from the registered modules, the database among them, when first resolved.
        [typeof(StudioShellDescriptor)] = typeof(StudioShellDescriptor),

        // A3: the live-update loop's log words, this studio's own log methods, chosen by its product.
        [typeof(StudioLiveLog)] = typeof(StudioLiveLog),
        [typeof(ISqlConsoleService)] = typeof(SqlConsoleService),
        [typeof(HealthMuteKeys)] = typeof(HealthMuteKeys),

        // 1.1 (K10's retirement): the accepted-checks validator takes the container's logger factory, to say once that an entry
        // names a retired check. One of several validators of the options, so it is found among them.
        [typeof(IValidateOptions<MartenOptions>)] = typeof(AcceptedChecksValidator<MartenOptions>),
    };

    /// <summary>
    /// The factory rows above compare only that a factory is registered; this resolves each one. Every factory makes the
    /// type it made before the move, and <see cref="ISqlConsoleService" /> is the circuit's one
    /// <see cref="SqlConsoleService" />, not a second console.
    /// </summary>
    [Fact]
    public async Task Every_factory_makes_what_it_made_before_and_the_console_is_one_instance_per_circuit()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorizationCore();
        services.AddMarten(static options => options.Connection("Host=marten-studio-registration.invalid;Database=none;Username=none;Password=none"));
        services.AddDataStudio().AddMarten();

        services.Where(static x => x.ImplementationFactory is not null && !x.IsKeyedService && IsStudio(x.ServiceType))
            .Select(static x => x.ServiceType)
            .Should().BeEquivalentTo(FactoryResults.Keys, "a new factory registration is pinned here by what it makes");

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        foreach ((Type service, Type made) in FactoryResults)
        {
            if (service.IsGenericType && service.GetGenericTypeDefinition() == typeof(IValidateOptions<>))
            {
                // One of several validators of the options, so it is found among them.
                circuit.ServiceProvider.GetServices(service).Select(static x => x!.GetType())
                    .Should().Contain(made, service.Name + "'s factory");
            }
            else
            {
                // Registered once: the factory makes exactly the type it made before the move.
                circuit.ServiceProvider.GetRequiredService(service).Should().BeOfType(made, service.Name + "'s factory");
            }
        }

        circuit.ServiceProvider.GetRequiredService<ISqlConsoleService>()
            .Should().BeSameAs(circuit.ServiceProvider.GetRequiredService<SqlConsoleService>());
    }

    [Fact]
    public void The_comparison_sees_a_lifetime_an_implementation_and_an_order_change()
    {
        (Type, ServiceLifetime, How, Type)[] one =
        [
            (typeof(IDisposable), ServiceLifetime.Singleton, How.Type, typeof(MemoryStream)),
            (typeof(IDisposable), ServiceLifetime.Scoped, How.Type, typeof(StringWriter)),
            (typeof(IFormattable), ServiceLifetime.Scoped, How.Factory, typeof(IFormattable)),
        ];

        string[] described = Describe(one, studioOnly: false);

        described.Should().HaveCount(3);
        described.Should().Equal(Describe([one[2], one[0], one[1]], studioOnly: false), "only the order within one service type is significant");
        described.Should().NotEqual(Describe([one[1], one[0], one[2]], studioOnly: false), "two registrations of one service resolve in order");
        described.Should().NotEqual(Describe([one[0], one[1], (typeof(IFormattable), ServiceLifetime.Singleton, How.Factory, typeof(IFormattable))], studioOnly: false));
        described.Should().NotEqual(Describe([one[0], (typeof(IDisposable), ServiceLifetime.Scoped, How.Type, typeof(StreamWriter)), one[2]], studioOnly: false));
        Describe(one).Should().BeEmpty("none of these is a studio type");
    }

    private static readonly Assembly[] StudioAssemblies =
        [.. new[] { typeof(MartenOptions).Assembly, typeof(StudioCoreRegistration).Assembly, typeof(DiagnosticsCoreRegistration).Assembly, typeof(StudioShellDescriptor).Assembly }.Distinct()];

    private static (Type Service, ServiceLifetime Lifetime, How How, Type Implementation) Read(ServiceDescriptor descriptor) =>
        descriptor switch
        {
            { ImplementationType: { } type } => (descriptor.ServiceType, descriptor.Lifetime, How.Type, type),
            { ImplementationInstance: { } instance } => (descriptor.ServiceType, descriptor.Lifetime, How.Instance, instance.GetType()),
            { ImplementationFactory: { } factory } => (descriptor.ServiceType, descriptor.Lifetime, How.Factory, factory.Method.ReturnType),
            _ => (descriptor.ServiceType, descriptor.Lifetime, How.Factory, typeof(object)),
        };

    private static bool IsStudio(Type type) =>
        StudioAssemblies.Contains(type.Assembly) || (type.IsGenericType && type.GetGenericArguments().Any(IsStudio));

    /// <summary>The studio's registrations, grouped by service type, each group in registration order.</summary>
    private static string[] Describe(
        IEnumerable<(Type Service, ServiceLifetime Lifetime, How How, Type Implementation)> registrations,
        bool studioOnly = true) =>
    [
        .. registrations
            .Where(x => !studioOnly || IsStudio(x.Service) || IsStudio(x.Implementation))
            .GroupBy(static x => x.Service)
            .OrderBy(static x => x.Key.FullName, StringComparer.Ordinal)
            .SelectMany(static group => group.Select(static x =>
                x.Service.FullName + " | " + x.Lifetime + " | " + x.How + " | " + x.Implementation.FullName)),
    ];

    /// <summary>The actual list in this file's own syntax, for a reviewer who has to update it deliberately.</summary>
    private static string Listing(IServiceCollection services)
    {
        StringBuilder text = new();

        foreach ((Type service, ServiceLifetime lifetime, How how, Type implementation) in services.Select(Read)
                     .Where(static x => IsStudio(x.Service) || IsStudio(x.Implementation)))
        {
            text.Append("        (typeof(").Append(Name(service)).Append("), ServiceLifetime.").Append(lifetime)
                .Append(", How.").Append(how).Append(", typeof(").Append(Name(implementation)).AppendLine(")),");
        }

        return text.ToString();
    }

    private static string Name(Type type) => type.IsGenericType
        ? type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)] + "<" + string.Join(", ", type.GetGenericArguments().Select(Name)) + ">"
        : type.Name;
}
