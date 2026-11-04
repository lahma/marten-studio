using System.Reflection;
using System.Text;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Connection;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Live;
using MartenStudio.Services.Projections;
using MartenStudio.Services.Query;
using MartenStudio.Services.Schema;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PostgresStudio.Internal;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Relationships;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

namespace MartenStudio.Tests.Services;

/// <summary>
/// What <c>AddMartenStudio()</c> puts in a host's container, pinned: moving a shared service's registration from
/// <c>AddMartenStudio()</c> into <see cref="StudioCoreRegistration" /> (PS-1e) must leave a MartenStudio host with the
/// same services, the same lifetimes and the same implementations - and every enumerable service in the same order.
/// </summary>
/// <remarks>
/// The list was taken from <c>AddMartenStudio()</c> at the commit before the move and is compared after it. Only the
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
    /// <c>AddMartenStudio()</c>'s registrations at the commit before PS-1e, less the one PS-1e removed on purpose:
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
        (typeof(StudioDescriptor), ServiceLifetime.Singleton, How.Instance, typeof(StudioDescriptor)),
        (typeof(IStudioSettings), ServiceLifetime.Singleton, How.Factory, typeof(IStudioSettings)),
        (typeof(StudioMappedEndpoints), ServiceLifetime.Singleton, How.Type, typeof(StudioMappedEndpoints)),
        (typeof(IHostedService), ServiceLifetime.Singleton, How.Type, typeof(StudioEndpointAuthorizationGuard)),
        (typeof(StudioCapabilityGuard), ServiceLifetime.Singleton, How.Type, typeof(StudioCapabilityGuard)),
        (typeof(StudioActionLogService), ServiceLifetime.Singleton, How.Type, typeof(StudioActionLogService)),
        (typeof(StudioLogThrottle), ServiceLifetime.Singleton, How.Factory, typeof(StudioLogThrottle)),
        (typeof(ColumnCatalog), ServiceLifetime.Singleton, How.Type, typeof(ColumnCatalog)),
        (typeof(StudioState), ServiceLifetime.Scoped, How.Type, typeof(StudioState)),
        (typeof(IStudioScopeCatalog), ServiceLifetime.Scoped, How.Type, typeof(StudioScopeCatalog)),
        (typeof(StudioAuthorization), ServiceLifetime.Scoped, How.Type, typeof(StudioAuthorization)),
        (typeof(StudioSourceResolver), ServiceLifetime.Scoped, How.Type, typeof(StudioSourceResolver)),
        (typeof(StudioActionLog), ServiceLifetime.Scoped, How.Type, typeof(StudioActionLog)),
        (typeof(ToastService), ServiceLifetime.Scoped, How.Type, typeof(ToastService)),
        (typeof(StudioSnapshotCache), ServiceLifetime.Singleton, How.Type, typeof(StudioSnapshotCache)),
        (typeof(StudioLiveUpdatesFactory), ServiceLifetime.Scoped, How.Type, typeof(StudioLiveUpdatesFactory)),
        (typeof(INavIndicatorService), ServiceLifetime.Scoped, How.Type, typeof(NavIndicatorService)),
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
        (typeof(PostgresStudio.Services.Maintenance.IMaintenanceFacts), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Maintenance.MaintenanceFactsReader)),
        (typeof(PostgresStudio.Services.Maintenance.BloatMeasureFlights), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Maintenance.BloatMeasureFlights)),
        (typeof(PostgresStudio.Services.Maintenance.IMaintenanceNameGate), ServiceLifetime.Scoped, How.Type, typeof(PostgresStudio.Services.Maintenance.DatabaseAccessNameGate)),
        (typeof(PostgresStudio.Services.Maintenance.IMaintenanceService), ServiceLifetime.Scoped, How.Type, typeof(PostgresStudio.Services.Maintenance.MaintenanceService)),
        (typeof(SessionPoolObserver), ServiceLifetime.Singleton, How.Type, typeof(SessionPoolObserver)),
        (typeof(ISessionActivityReader), ServiceLifetime.Singleton, How.Type, typeof(SessionActivityReader)),
        (typeof(SessionActivitySnapshots), ServiceLifetime.Singleton, How.Type, typeof(SessionActivitySnapshots)),
        (typeof(SessionHolderNames), ServiceLifetime.Scoped, How.Type, typeof(SessionHolderNames)),
        (typeof(ISessionActivityService), ServiceLifetime.Scoped, How.Type, typeof(SessionActivityService)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        // 1.0 (#44): MaxStudioConnections and StudioApplicationName, two more of the shared bounds.
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Instance, typeof(ValidateOptions<MartenStudioOptions>)),
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Factory, typeof(IValidateOptions<MartenStudioOptions>)),
        // 1.2-PROJ: ProjectionThresholds' validator, a type of its own because its message names the projection.
        (typeof(IValidateOptions<MartenStudioOptions>), ServiceLifetime.Singleton, How.Type, typeof(ProjectionThresholdsValidator)),
        (typeof(MartenStoreRegistry), ServiceLifetime.Singleton, How.Instance, typeof(MartenStoreRegistry)),
        (typeof(IStudioSourceProvider), ServiceLifetime.Scoped, How.Type, typeof(MartenStudioSourceProvider)),
        (typeof(IStudioLinks), ServiceLifetime.Singleton, How.Type, typeof(MartenStudioLinks)),
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

        // 1.0-P3C: Marten Studio's plan insights P26 to P28, the one registration the packet adds.
        (typeof(IPlanInsightContributor), ServiceLifetime.Scoped, How.Type, typeof(MartenPlanInsightContributor)),

        // 1.2-INDEX-A: the index advisor's Marten half, the one registration the packet adds.
        (typeof(IIndexCandidateContributor), ServiceLifetime.Scoped, How.Type, typeof(MartenIndexCandidateContributor)),

        // 1.2-H-C: E06's on-demand action, the one finding action 1.2 registers.
        (typeof(IHealthFindingAction), ServiceLifetime.Singleton, How.Type, typeof(MartenStudio.Services.Health.LongestStreamsAction)),

        // 1.1-NP4: "How Marten uses this connection" - the data-source registry, the deeper checks' average sizes and the contributor.
        (typeof(MartenDataSourceRegistry), ServiceLifetime.Singleton, How.Instance, typeof(MartenDataSourceRegistry)),
        (typeof(MartenRowSizeCache), ServiceLifetime.Singleton, How.Type, typeof(MartenRowSizeCache)),
        (typeof(IConnectionInsightContributor), ServiceLifetime.Scoped, How.Type, typeof(MartenConnectionInsightContributor)),
        (typeof(IndexCatalog), ServiceLifetime.Singleton, How.Type, typeof(IndexCatalog)),
        (typeof(StudioScopeResolver), ServiceLifetime.Scoped, How.Type, typeof(StudioScopeResolver)),
        (typeof(TenantDiscovery), ServiceLifetime.Scoped, How.Type, typeof(TenantDiscovery)),
        (typeof(IStoreInfoService), ServiceLifetime.Scoped, How.Type, typeof(StoreInfoService)),
        (typeof(IDocumentDataService), ServiceLifetime.Scoped, How.Type, typeof(DocumentDataService)),
        (typeof(DocumentBrowserState), ServiceLifetime.Scoped, How.Type, typeof(DocumentBrowserState)),
        (typeof(IDocumentWriteService), ServiceLifetime.Scoped, How.Type, typeof(DocumentWriteService)),
        (typeof(IEventDataService), ServiceLifetime.Scoped, How.Type, typeof(EventDataService)),

        // M3: the grouped dead-letter view's read, shared across circuits for one refresh interval - added in 0.6.
        (typeof(DeadLetterGroupCache), ServiceLifetime.Singleton, How.Type, typeof(DeadLetterGroupCache)),

        // 1.2 (M6): the event flow's model, one per store, rebuilt only when the configuration moved.
        (typeof(EventFlowCache), ServiceLifetime.Singleton, How.Type, typeof(EventFlowCache)),

        // 1.2-SHAPE: the shape check - the documents' check service, and the cache that makes it one read per scope and subject.
        (typeof(MartenStudio.Services.Shapes.ShapeCheckCache), ServiceLifetime.Singleton, How.Type, typeof(MartenStudio.Services.Shapes.ShapeCheckCache)),
        (typeof(EventTypeListingCache), ServiceLifetime.Singleton, How.Type, typeof(EventTypeListingCache)),
        (typeof(MartenStudio.Services.Shapes.IDocumentShapeCheck), ServiceLifetime.Scoped, How.Type, typeof(MartenStudio.Services.Shapes.DocumentShapeCheckService)),
        (typeof(MartenStudio.Services.Shapes.IEventShapeCheck), ServiceLifetime.Scoped, How.Type, typeof(MartenStudio.Services.Shapes.EventShapeCheckService)),
        (typeof(ISchemaDataService), ServiceLifetime.Scoped, How.Type, typeof(SchemaDataService)),
        (typeof(IConfigurationService), ServiceLifetime.Scoped, How.Type, typeof(ConfigurationService)),
        (typeof(IQueryService), ServiceLifetime.Scoped, How.Type, typeof(QueryService)),
        (typeof(ILinqQueryService), ServiceLifetime.Scoped, How.Type, typeof(LinqQueryService)),
        (typeof(RelationshipReads), ServiceLifetime.Scoped, How.Type, typeof(RelationshipReads)),
        (typeof(IRelationshipDataService), ServiceLifetime.Scoped, How.Type, typeof(RelationshipDataService)),

        // W5-CAP: Storage - added in 0.5, not moved; the facts are the database's, the view the circuit's.
        (typeof(PostgresStudio.Services.Storage.IStorageFacts), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Storage.StorageFactsReader)),
        (typeof(PostgresStudio.Services.Storage.IStorageService), ServiceLifetime.Scoped, How.Type, typeof(PostgresStudio.Services.Storage.StorageService)),

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
        (typeof(PostgresStudio.Services.Aurora.CapacityReader), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Aurora.CapacityReader)),
        (typeof(PostgresStudio.Services.Aurora.IAuroraFactsReader), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Aurora.AuroraFactsReader)),
        (typeof(PostgresStudio.Services.Aurora.AuroraFactsCache), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Aurora.AuroraFactsCache)),
        (typeof(PostgresStudio.Services.Aurora.IAuroraService), ServiceLifetime.Scoped, How.Type, typeof(PostgresStudio.Services.Aurora.AuroraService)),

        // 1.1-NP3: Database > Connection - the facts the sweep's own where a fresh sweep holds them, the view the circuit's.
        (typeof(PostgresStudio.Services.Connection.IConnectionFactsSource), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Connection.ConnectionFactsSource)),
        (typeof(PostgresStudio.Services.Connection.IConnectionInsightService), ServiceLifetime.Scoped, How.Type, typeof(PostgresStudio.Services.Connection.ConnectionInsightService)),

        // 1.1-PASTE: "Compare a connection string" on that page - the same gate, no logger, nothing kept.
        (typeof(PostgresStudio.Services.Connection.IConnectionCompareService), ServiceLifetime.Scoped, How.Type, typeof(PostgresStudio.Services.Connection.ConnectionCompareService)),

        // W5-Q1: Statements - the facts are the statement sweep's, the view the circuit's.
        (typeof(PostgresStudio.Services.Statements.IStatementFacts), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Statements.StatementFactsReader)),
        // 0.6 (X1): the Statements drill-in's planner, stateless and the process's.
        (typeof(PostgresStudio.Services.Statements.StatementPlanReader), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Statements.StatementPlanReader)),
        (typeof(PostgresStudio.Services.Statements.IStatementsService), ServiceLifetime.Scoped, How.Type, typeof(PostgresStudio.Services.Statements.StatementsService)),

        // 1.2-H-A: Mark on Statements, Storage and Maintenance.
        (typeof(SinceMarks), ServiceLifetime.Scoped, How.Type, typeof(SinceMarks)),
        // 0.6 (X2): the what-if engine - hypopg's availability cached per database, every hypothetical index reset - the process's.
        (typeof(PostgresStudio.Services.Query.HypotheticalIndexReader), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Query.HypotheticalIndexReader)),
        (typeof(PostgresStudio.Services.Statements.IndexAdvisorFlights), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Statements.IndexAdvisorFlights)),

        // W5-API: the Health page's Snapshot menu, the circuit's - built from the page services as the visitor.
        (typeof(PostgresStudio.Services.Diagnostics.VisitorSnapshotBuilder), ServiceLifetime.Scoped, How.Type, typeof(PostgresStudio.Services.Diagnostics.VisitorSnapshotBuilder)),

        // 1.2-H-B: "Compare with an earlier snapshot", the circuit's - the visitor's gated view first, then the file.
        (typeof(PostgresStudio.Services.Diagnostics.ISnapshotCompareService), ServiceLifetime.Scoped, How.Type, typeof(PostgresStudio.Services.Diagnostics.SnapshotCompareService)),
        (typeof(StudioLiveState), ServiceLifetime.Singleton, How.Type, typeof(StudioLiveState)),
        (typeof(StudioOperationTracker), ServiceLifetime.Singleton, How.Type, typeof(StudioOperationTracker)),
        (typeof(DaemonControlState), ServiceLifetime.Singleton, How.Type, typeof(DaemonControlState)),

        // 0.6, M2: the projections screen's own samples - a singleton fed by the snapshot read, bounded per database.
        (typeof(ProjectionLagHistory), ServiceLifetime.Singleton, How.Type, typeof(ProjectionLagHistory)),
        (typeof(DaemonAccessor), ServiceLifetime.Scoped, How.Type, typeof(DaemonAccessor)),
        (typeof(IProjectionDataService), ServiceLifetime.Scoped, How.Type, typeof(ProjectionDataService)),

        // W5-API: the diagnostics API for the host's own code (D31) - added in 0.5, inert until host code calls it.
        (typeof(PostgresStudio.Services.Diagnostics.FreshReadLimiter), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Diagnostics.FreshReadLimiter)),
        (typeof(PostgresStudio.IPostgresDiagnostics), ServiceLifetime.Singleton, How.Type, typeof(PostgresStudio.Services.Diagnostics.PostgresDiagnosticsService)),
    ];

    [Fact]
    public void AddMartenStudio_registers_the_same_services_with_the_same_lifetimes_and_order_as_before_the_move()
    {
        ServiceCollection services = new();
        services.AddMartenStudio();

        string[] actual = Describe(services.Select(Read));
        string[] expected = Describe(Expected);

        expected.Should().HaveCount(Expected.Length, "the studio filter keeps every row of the expected list, or the comparison skips some");
        actual.Should().Equal(expected, "the registrations a MartenStudio host gets are unchanged in effect:" + Environment.NewLine + Listing(services));
    }

    /// <summary>What each factory registration of the studio makes, by its concrete type.</summary>
    private static readonly Dictionary<Type, Type> FactoryResults = new()
    {
        [typeof(IDiagnosticsSettings)] = typeof(MartenStudioSettings),
        [typeof(IStudioSettings)] = typeof(MartenStudioSettings),
        [typeof(StudioLogThrottle)] = typeof(StudioLogThrottle),
        [typeof(IStudioRuntime)] = typeof(MartenStudioRuntime),
        [typeof(ISqlConsoleService)] = typeof(SqlConsoleService),
        [typeof(HealthMuteKeys)] = typeof(HealthMuteKeys),

        // 1.1 (K10's retirement): the accepted-checks validator takes the container's logger factory, to say once that an entry
        // names a retired check. One of several validators of the options, so it is found among them.
        [typeof(IValidateOptions<MartenStudioOptions>)] = typeof(AcceptedChecksValidator<MartenStudioOptions>),
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
        services.AddMartenStudio();

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
        [.. new[] { typeof(MartenStudioOptions).Assembly, typeof(StudioCoreRegistration).Assembly, typeof(DiagnosticsCoreRegistration).Assembly }.Distinct()];

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
