using System.Reflection;

using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Events.Subscriptions;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Linq.Members;
using Marten.Schema;
using Marten.Services;
using Marten.Storage;
using Marten.Storage.Metadata;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using JasperFxCoordinator = JasperFx.Events.Daemon.IProjectionCoordinator;
using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;
using MartenEventStoreOperations = Marten.Events.IEventStoreOperations;
using MartenMetadataConfig = Marten.Events.IReadonlyMetadataConfig;
using MartenQueryEventStore = Marten.Events.IQueryEventStore;
using MartenReadOnlyEventStoreOptions = Marten.Events.IReadOnlyEventStoreOptions;

namespace MartenStudio.Tests.Marten;

/// <summary>
/// Pins down the Marten 9.35 surface the implementation plan depends on, so that a version bump which
/// renames or removes one of these members fails <em>here</em>, in seconds, rather than three packets
/// later inside a page nobody has written yet.
/// </summary>
/// <remarks>
/// <para>
/// Two mechanisms, deliberately both. <see cref="CompileOnlyAsync"/> is never executed but is written in
/// ordinary C#, so the <em>compiler</em> checks every signature, generic constraint and optional-argument
/// shape. The reflection facts assert member by member, so a failure names the one member that moved
/// instead of producing a wall of CS1061 on a method with nine overloads. Reflection alone would not
/// notice a parameter that changed type under a positional call; compilation alone would not notice a
/// member being re-typed to something still assignable.
/// </para>
/// <para>
/// The comments record what the plan's Appendix A got wrong, verified against the real assemblies on
/// 2026-09-14 (Marten 9.35.0, JasperFx / JasperFx.Events 2.69.3, Weasel.Core 9.32.0, Npgsql 9.0.4).
/// Where the plan named a type or member that does not exist, the real one is asserted and the
/// difference is called out - those comments are as much the deliverable as the assertions.
/// </para>
/// </remarks>
public class MartenApiSurfaceTest
{
    private const BindingFlags MemberFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Never connected to: only service descriptors are inspected, never a built provider.</summary>
    private const string DummyConnectionString =
        "Host=marten-studio-api-surface-test.invalid;Database=none;Username=none;Password=none";

    // --------------------------------------------------------------------------------------------
    // IDocumentStore
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void IDocumentStore_exposes_the_four_entry_points_and_the_session_factories()
    {
        var store = typeof(IDocumentStore);

        RequireProperty(store, "Options").PropertyType.Should().Be<IReadOnlyStoreOptions>();
        RequireProperty(store, "Storage").PropertyType.Should().Be<IMartenStorage>();
        RequireProperty(store, "Advanced").PropertyType.Should().Be<AdvancedOperations>();
        RequireProperty(store, "Diagnostics").PropertyType.Should().Be<IDiagnostics>();

        // Note the trailing optional IsolationLevel: there is no one-argument LightweightSession(string),
        // so this cannot be reached through a delegate that takes only the tenant id.
        RequireMethod(store, "LightweightSession", typeof(string), typeof(System.Data.IsolationLevel))
            .ReturnType.Should().Be<IDocumentSession>();
        RequireMethod(store, "QuerySession").ReturnType.Should().Be<IQuerySession>();
        RequireMethod(store, "QuerySession", typeof(string)).ReturnType.Should().Be<IQuerySession>();

        // U6, first half: a session can be opened against an explicitly chosen database, so writes in a
        // StaticMultiple store are not confined to the default one. See the SessionOptions fact below.
        RequireMethod(store, "LightweightSession", typeof(SessionOptions)).ReturnType.Should().Be<IDocumentSession>();
    }

    /// <summary>U1: does <c>IDocumentStore</c> carry the JasperFx event-store interface?</summary>
    /// <remarks>
    /// <b>It does not.</b> Wolverine 5.19 casts an <c>IDocumentStore</c> to <c>IEventStore</c> against
    /// Marten 9.23; on 9.35 the interface list of <c>Marten.IDocumentStore</c> is
    /// <c>IDisposable, IAsyncDisposable, IDocumentSessionFactory&lt;,&gt;, IDocumentSessionFactory</c> and
    /// nothing else. The <em>concrete</em> <c>Marten.DocumentStore</c> does implement
    /// <c>JasperFx.Events.IEventStore</c>, and <c>AddMarten</c> registers <c>IEventStore</c> as its own
    /// singleton - so the studio must never assume the cast succeeds on the interface it is handed. Store
    /// labels therefore come from the registered service type (plan §4.2), not from an
    /// <c>IEventStore</c> probe.
    /// </remarks>
    [Fact]
    public void IDocumentStore_is_not_assignable_to_the_JasperFx_IEventStore()
    {
        typeof(IEventStore).IsAssignableFrom(typeof(IDocumentStore)).Should().BeFalse(
            "Marten 9.35 keeps IDocumentStore free of JasperFx.Events.IEventStore; only the concrete " +
            "DocumentStore implements it. If this ever flips, plan §4.2's store-label fallback can go.");

        var concrete = typeof(IDocumentStore).Assembly.GetType("Marten.DocumentStore");

        concrete.Should().NotBeNull();
        typeof(IEventStore).IsAssignableFrom(concrete!).Should().BeTrue();
    }

    // --------------------------------------------------------------------------------------------
    // IReadOnlyStoreOptions
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void IReadOnlyStoreOptions_exposes_the_configuration_the_studio_renders()
    {
        var options = typeof(IReadOnlyStoreOptions);

        RequireMethod(options, "AllKnownDocumentTypes").ReturnType.Should().Be<IReadOnlyList<IDocumentType>>();
        RequireMethod(options, "FindOrResolveDocumentType", typeof(Type)).ReturnType.Should().Be<IDocumentType>();
        RequireMethod(options, "Serializer").ReturnType.Should().Be<ISerializer>();

        RequireProperty(options, "Events").PropertyType.Should().Be<MartenReadOnlyEventStoreOptions>();
        RequireProperty(options, "Tenancy").PropertyType.Should().Be<ITenancy>();
        RequireProperty(options, "Schema").PropertyType.Should().Be<IDocumentSchemaResolver>();
        RequireProperty(options, "DatabaseSchemaName").PropertyType.Should().Be<string>();
        RequireProperty(options, "TenantIdStyle").PropertyType.Should().Be<TenantIdStyle>();
    }

    // --------------------------------------------------------------------------------------------
    // IDocumentType and its metadata
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void IDocumentType_exposes_everything_the_document_SQL_builders_read()
    {
        var documentType = typeof(IDocumentType);

        RequireProperty(documentType, "DocumentType").PropertyType.Should().Be<Type>();
        RequireProperty(documentType, "IdType").PropertyType.Should().Be<Type>();
        RequireProperty(documentType, "TableName").PropertyType.Should().Be<Weasel.Core.DbObjectName>();
        RequireProperty(documentType, "Alias").PropertyType.Should().Be<string>();
        RequireProperty(documentType, "DatabaseSchemaName").PropertyType.Should().Be<string>();
        RequireProperty(documentType, "Metadata").PropertyType.Should().Be<DocumentMetadataCollection>();
        RequireProperty(documentType, "DuplicatedFields").PropertyType.Should().Be<IReadOnlyList<DuplicatedField>>();
        RequireProperty(documentType, "Indexes");
        RequireProperty(documentType, "ForeignKeys");
        RequireProperty(documentType, "SubClasses");
        RequireProperty(documentType, "Root").PropertyType.Should().Be<IDocumentType>();
        RequireProperty(documentType, "UseOptimisticConcurrency").PropertyType.Should().Be<bool>();
        RequireProperty(documentType, "TenancyStyle").PropertyType.Should().Be<TenancyStyle>();
        RequireProperty(documentType, "IdStrategy");
        RequireProperty(documentType, "IdMember").PropertyType.Should().Be<MemberInfo>();

        RequireMethod(documentType, "IsHierarchy").ReturnType.Should().Be<bool>();
        RequireMethod(documentType, "AliasFor", typeof(Type)).ReturnType.Should().Be<string>();
        RequireMethod(documentType, "TypeFor", typeof(string)).ReturnType.Should().Be<Type>();
    }

    /// <summary>
    /// U2 (<c>DeleteStyle</c>) and the plan's <c>UseNumericRevisions</c>: <b>neither is on
    /// <c>IDocumentType</c>.</b>
    /// </summary>
    /// <remarks>
    /// <c>DeleteStyle</c> lives on <c>Marten.Schema.IDocumentMapping</c> - which is <c>internal</c>, and
    /// which <c>IDocumentType</c> does not derive from; the two are siblings that the concrete
    /// <c>DocumentMapping</c> happens to implement together. So there is no supported way to read it from
    /// what <c>FindOrResolveDocumentType</c> hands back, and the studio keeps the plan's fallback: soft
    /// deletion is read from <c>Metadata.IsSoftDeleted.Enabled</c>. <c>UseNumericRevisions</c> is only on
    /// the concrete <c>DocumentMapping</c> and on <c>JasperFx.Descriptors.DocumentMappingDescriptor</c>;
    /// its interface-level substitute is <c>Metadata.Revision.Enabled</c>.
    /// </remarks>
    [Fact]
    public void IDocumentType_has_neither_DeleteStyle_nor_UseNumericRevisions()
    {
        typeof(IDocumentType).GetProperty("DeleteStyle", MemberFlags).Should().BeNull(
            "Marten 9.35 keeps DeleteStyle on IDocumentMapping; the studio reads Metadata.IsSoftDeleted.Enabled");
        typeof(IDocumentType).GetProperty("UseNumericRevisions", MemberFlags).Should().BeNull(
            "Marten 9.35 keeps UseNumericRevisions on the concrete DocumentMapping; the studio reads " +
            "Metadata.Revision.Enabled");

        // Where they actually are, so the next reader does not have to go looking again. Both are reached
        // by name because IDocumentMapping is internal to Marten - which is itself the reason the studio
        // cannot use it.
        var mappingInterface = MartenType("Marten.Schema.IDocumentMapping");
        var documentMapping = MartenType("Marten.Schema.DocumentMapping");

        RequireProperty(mappingInterface, "DeleteStyle");
        mappingInterface.IsPublic.Should().BeFalse("IDocumentMapping is internal - do not plan to cast to it");
        mappingInterface.IsAssignableFrom(typeof(IDocumentType)).Should().BeFalse();
        RequireProperty(documentMapping, "UseNumericRevisions").PropertyType.Should().Be<bool>();
    }

    [Fact]
    public void DocumentMetadataCollection_names_every_optional_column_and_each_one_can_be_disabled()
    {
        string[] columns =
        [
            "IsSoftDeleted", "SoftDeletedAt", "Version", "Revision", "LastModified", "CreatedAt",
            "TenantId", "DotNetType", "DocumentType", "CausationId", "CorrelationId", "LastModifiedBy",
            "Headers",
        ];

        foreach (var column in columns)
        {
            RequireProperty(typeof(DocumentMetadataCollection), column)
                .PropertyType.Should().Be<MetadataColumn>($"Metadata.{column} is a MetadataColumn");
        }

        // Every one of them is optional. `Enabled` is what the studio must branch on before it puts a
        // column into a select list; `Name` is the physical column, inherited from Weasel's TableColumn.
        RequireProperty(typeof(MetadataColumn), "Enabled").PropertyType.Should().Be<bool>();
        RequireProperty(typeof(MetadataColumn), "Name").PropertyType.Should().Be<string>();
    }

    /// <summary>U3 and U4.</summary>
    /// <remarks>
    /// <c>DbObjectName</c> is where the plan said it was. <c>DuplicatedField</c> is <b>not</b>: it lives in
    /// <c>Marten.Linq.Members</c>, not <c>Marten.Schema</c>. Its four interesting members are all present,
    /// and <c>DbType</c> is an <c>NpgsqlDbType</c> rather than a string - so a duplicated column's
    /// parameter type is available directly and does not have to be re-derived from <c>PgType</c>.
    /// </remarks>
    [Fact]
    public void DbObjectName_and_DuplicatedField_carry_the_names_the_SQL_builders_quote()
    {
        RequireProperty(typeof(Weasel.Core.DbObjectName), "Schema").PropertyType.Should().Be<string>();
        RequireProperty(typeof(Weasel.Core.DbObjectName), "Name").PropertyType.Should().Be<string>();
        RequireProperty(typeof(Weasel.Core.DbObjectName), "QualifiedName").PropertyType.Should().Be<string>();

        typeof(DuplicatedField).FullName.Should().Be("Marten.Linq.Members.DuplicatedField",
            "the plan called it Marten.Schema.DuplicatedField; it is not there");
        RequireProperty(typeof(DuplicatedField), "ColumnName").PropertyType.Should().Be<string>();
        RequireProperty(typeof(DuplicatedField), "MemberName").PropertyType.Should().Be<string>();
        RequireProperty(typeof(DuplicatedField), "PgType").PropertyType.Should().Be<string>();
        RequireProperty(typeof(DuplicatedField), "DbType").PropertyType.Should().Be<NpgsqlTypes.NpgsqlDbType>();
    }

    // --------------------------------------------------------------------------------------------
    // Event store configuration
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void IReadOnlyEventStoreOptions_exposes_the_event_configuration_cards()
    {
        var events = typeof(MartenReadOnlyEventStoreOptions);

        RequireMethod(events, "Projections").ReturnType.Should().Be<IReadOnlyList<ISubscriptionSource>>();

        // Note the element type: AllKnownEventTypes yields IEventType (Alias / EventType / EventTypeName /
        // DotNetTypeName), not a bare Type, so the event-types screen gets the alias for free.
        RequireMethod(events, "AllKnownEventTypes").ReturnType.Should().Be<IReadOnlyList<IEventType>>();

        RequireProperty(events, "StreamIdentity").PropertyType.Should().Be<StreamIdentity>();
        RequireProperty(events, "TenancyStyle").PropertyType.Should().Be<TenancyStyle>();
        RequireProperty(events, "DatabaseSchemaName").PropertyType.Should().Be<string>();
        RequireProperty(events, "MetadataConfig").PropertyType.Should().Be<MartenMetadataConfig>();
        RequireProperty(events, "AppendMode").PropertyType.Should().Be<EventAppendMode>();
        RequireProperty(events, "Daemon");
    }

    /// <summary>U14: the property names on the event metadata configuration.</summary>
    /// <remarks>
    /// The type is <c>Marten.Events.IReadonlyMetadataConfig</c> - lower-case <c>o</c> in "Readonly",
    /// unlike every neighbouring <c>IReadOnly*</c> type, which is exactly the kind of detail that costs an
    /// hour later. It carries four <c>*Enabled</c> booleans and nothing else, so the rest of the
    /// <c>mt_events</c> column set (plan U8) has to be discovered from <c>information_schema</c> rather
    /// than read out of configuration.
    /// </remarks>
    [Fact]
    public void Event_metadata_config_has_exactly_four_enabled_flags()
    {
        typeof(MartenMetadataConfig).FullName.Should().Be("Marten.Events.IReadonlyMetadataConfig");

        string[] flags = ["CausationIdEnabled", "CorrelationIdEnabled", "HeadersEnabled", "UserNameEnabled"];

        foreach (var flag in flags)
        {
            RequireProperty(typeof(MartenMetadataConfig), flag).PropertyType.Should().Be<bool>();
        }

        typeof(MartenMetadataConfig).GetProperties(MemberFlags).Should().HaveCount(flags.Length,
            "if Marten adds a fifth event metadata flag the Configuration screen has to grow a row for it");
    }

    /// <summary>
    /// The projections table's row source. <c>ISubscriptionSource</c> is in
    /// <c>JasperFx.Events.Subscriptions</c>, not <c>JasperFx.Events.Projections</c> as the plan said.
    /// </summary>
    [Fact]
    public void ISubscriptionSource_describes_a_projection_row()
    {
        typeof(ISubscriptionSource).FullName.Should().Be("JasperFx.Events.Subscriptions.ISubscriptionSource",
            "the plan called it JasperFx.Events.Projections.ISubscriptionSource");

        RequireProperty(typeof(ISubscriptionSource), "Name").PropertyType.Should().Be<string>();
        RequireProperty(typeof(ISubscriptionSource), "Version").PropertyType.Should().Be<uint>();
        RequireProperty(typeof(ISubscriptionSource), "Type").PropertyType.Should()
            .Be<JasperFx.Events.Descriptors.SubscriptionType>();
        RequireProperty(typeof(ISubscriptionSource), "Lifecycle").PropertyType.Should().Be<ProjectionLifecycle>();
        RequireProperty(typeof(ISubscriptionSource), "ImplementationType").PropertyType.Should().Be<Type>();
        RequireMethod(typeof(ISubscriptionSource), "ShardNames").ReturnType.Should().Be<ShardName[]>();
    }

    // --------------------------------------------------------------------------------------------
    // Storage, databases, schema
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void IMartenStorage_exposes_the_schema_screen()
    {
        var storage = typeof(IMartenStorage);

        RequireProperty(storage, "Database").PropertyType.Should().Be<IMartenDatabase>();
        RequireMethod(storage, "AllDatabases").ReturnType.Should().Be<ValueTask<IReadOnlyList<IMartenDatabase>>>();
        RequireMethod(storage, "FindOrCreateDatabase", typeof(string))
            .ReturnType.Should().Be<ValueTask<IMartenDatabase>>();
        RequireMethod(storage, "AllSchemaNames").ReturnType.Should().Be<string[]>();
        RequireMethod(storage, "AllObjects");
        RequireMethod(storage, "ToDatabaseScript").ReturnType.Should().Be<string>();
        RequireMethod(storage, "ApplyAllConfiguredChangesToDatabaseAsync", typeof(AutoCreate?))
            .ReturnType.Should().Be<Task>();
        RequireMethod(storage, "CreateMigrationAsync").ReturnType.Should().Be<Task<Weasel.Core.SchemaMigration>>();
    }

    /// <summary>U5: how the Schema screen turns a migration into preview SQL.</summary>
    /// <remarks>
    /// The type is <c>Weasel.Core.SchemaMigration</c>, not <c>Weasel.Core.Migrations.SchemaMigration</c>.
    /// It has <b>no <c>UpdateSql()</c> and no <c>ToSql()</c></b>: the only way to render a migration is
    /// <c>WriteAllUpdates(TextWriter, Migrator, AutoCreate)</c> into a <see cref="StringWriter"/>, with the
    /// <c>Migrator</c> taken from <c>IMartenDatabase.Migrator</c>. <c>Difference</c>
    /// (a <c>SchemaPatchDifference</c>) is the drift verdict the Overview tile shows, and <c>Deltas</c> is
    /// the per-object breakdown behind it.
    /// </remarks>
    [Fact]
    public void SchemaMigration_renders_through_WriteAllUpdates_and_not_through_a_ToSql()
    {
        var migration = typeof(Weasel.Core.SchemaMigration);

        RequireProperty(migration, "Difference").PropertyType.Should().Be<Weasel.Core.SchemaPatchDifference>();
        RequireProperty(migration, "Deltas");
        RequireProperty(migration, "Schemas").PropertyType.Should().Be<string[]>();
        RequireMethod(migration, "WriteAllUpdates",
            typeof(TextWriter), typeof(Weasel.Core.Migrator), typeof(AutoCreate));

        migration.GetMethod("UpdateSql", MemberFlags).Should().BeNull();
        migration.GetMethod("ToSql", MemberFlags).Should().BeNull();

        // And the Migrator the writer needs comes off the database.
        RequireProperty(typeof(IMartenDatabase), "Migrator").PropertyType.Should().Be<Weasel.Core.Migrator>();
    }

    [Fact]
    public void IMartenDatabase_exposes_the_per_database_reads_and_an_obsolete_Identifier()
    {
        var database = typeof(IMartenDatabase);

        RequireProperty(database, "Id").PropertyType.Should().Be<DatabaseId>();
        RequireProperty(typeof(DatabaseId), "Identity").PropertyType.Should().Be<string>();
        RequireProperty(typeof(DatabaseId), "Name").PropertyType.Should().Be<string>();
        RequireProperty(typeof(DatabaseId), "Server").PropertyType.Should().Be<string>();

        RequireMethod(database, "DocumentTables").ReturnType.Should()
            .Be<Task<IReadOnlyList<Weasel.Core.DbObjectName>>>();
        RequireMethod(database, "Functions").ReturnType.Should()
            .Be<Task<IReadOnlyList<Weasel.Core.DbObjectName>>>();
        RequireMethod(database, "ExistingTableFor", typeof(Type));
        RequireMethod(database, "SchemaTables", typeof(CancellationToken));
        RequireMethod(database, "FetchEventStoreStatistics", typeof(CancellationToken));
        RequireMethod(database, "AllProjectionProgress", typeof(CancellationToken))
            .ReturnType.Should().Be<Task<IReadOnlyList<ShardState>>>();
        RequireMethod(database, "FetchProjectionProgressFor", typeof(ShardName[]), typeof(CancellationToken));
        RequireMethod(database, "ProjectionProgressFor", typeof(ShardName), typeof(CancellationToken));
        RequireMethod(database, "FetchHighestEventSequenceNumber", typeof(CancellationToken))
            .ReturnType.Should().Be<Task<long>>();
        RequireMethod(database, "MarkEventsAsSkipped", typeof(long[]), typeof(CancellationToken));
        RequireMethod(database, "CreateConnection", typeof(ConnectionUsage)).ReturnType.Should().Be<NpgsqlConnection>();
        RequireProperty(database, "Tracker").PropertyType.Should().Be<ShardStateTracker>();
        RequireMethod(database, "AssertDatabaseMatchesConfigurationAsync", typeof(CancellationToken));

        // The database key in every studio URL is Id.Identity, because Identifier is on its way out.
        RequireProperty(database, "Identifier").GetCustomAttribute<ObsoleteAttribute>().Should().NotBeNull(
            "IDatabase.Identifier is [Obsolete] in Marten 9.35 - scope keys use DatabaseId.Identity");
    }

    [Fact]
    public void ITenancy_answers_how_many_databases_there_are_and_which_tenants_live_in_them()
    {
        RequireProperty(typeof(ITenancy), "Cardinality").PropertyType.Should().Be<DatabaseCardinality>();
        RequireMethod(typeof(ITenancy), "DescribeDatabasesAsync", typeof(CancellationToken))
            .ReturnType.Should().Be<ValueTask<DatabaseUsage>>();

        RequireProperty(typeof(DatabaseUsage), "Databases").PropertyType.Should().Be<List<DatabaseDescriptor>>();
        RequireProperty(typeof(DatabaseDescriptor), "TenantIds").PropertyType.Should().Be<List<string>>();
    }

    // --------------------------------------------------------------------------------------------
    // Advanced operations and the document cleaner
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// U11: there is <b>no</b> non-generic <c>DeleteDocumentsByType(Type)</c>. The member is
    /// <c>DeleteDocumentsByTypeAsync(Type, CancellationToken)</c> - Marten 9 is async-only here too.
    /// </summary>
    [Fact]
    public void AdvancedOperations_and_the_cleaner_expose_the_operations_screens()
    {
        var advanced = typeof(AdvancedOperations);

        RequireProperty(advanced, "Clean").PropertyType.Should().Be<IDocumentCleaner>();
        RequireMethod(advanced, "AllAsyncProjectionShardNames").ReturnType.Should().Be<IReadOnlyList<ShardName>>();
        RequireMethod(advanced, "AdvanceHighWaterMarkToLatestAsync", typeof(CancellationToken));
        RequireMethod(advanced, "TryCorrectProgressInDatabaseAsync", typeof(CancellationToken));

        // U12, answered in passing: the tenant-scoped overloads the plan hoped for are here rather than on
        // IMartenDatabase, which only ever answers for the database it is.
        RequireMethod(advanced, "AdvanceHighWaterMarkToLatestAsync", typeof(string), typeof(CancellationToken));
        RequireMethod(advanced, "AllProjectionProgress", typeof(string), typeof(CancellationToken));

        RequireMethod(typeof(IDocumentCleaner), "DeleteDocumentsByTypeAsync", typeof(Type), typeof(CancellationToken))
            .ReturnType.Should().Be<Task>();
        typeof(IDocumentCleaner).GetMethod("DeleteDocumentsByType", MemberFlags).Should().BeNull(
            "Marten 9.35 has only the async form; the plan's DeleteDocumentsByType(Type) does not exist");
    }

    // --------------------------------------------------------------------------------------------
    // The async daemon
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void IProjectionDaemon_exposes_every_control_the_projections_screen_offers()
    {
        var daemon = typeof(IProjectionDaemon);

        RequireMethod(daemon, "StartAllAsync").ReturnType.Should().Be<Task>();
        RequireMethod(daemon, "StopAllAsync").ReturnType.Should().Be<Task>();
        RequireMethod(daemon, "StartAgentAsync", typeof(string), typeof(CancellationToken));
        RequireMethod(daemon, "StartAgentAsync", typeof(ShardName), typeof(CancellationToken));
        RequireMethod(daemon, "StopAgentAsync", typeof(string), typeof(Exception));
        RequireMethod(daemon, "StopAgentAsync", typeof(ShardName), typeof(Exception));
        RequireMethod(daemon, "CurrentAgents").ReturnType.Should().Be<IReadOnlyList<ISubscriptionAgent>>();
        RequireProperty(daemon, "Tracker").PropertyType.Should().Be<ShardStateTracker>();
        RequireProperty(daemon, "IsRunning").PropertyType.Should().Be<bool>();
        RequireMethod(daemon, "HasAnyPaused").ReturnType.Should().Be<bool>();
        RequireMethod(daemon, "EjectPausedShard", typeof(string));

        // Three parameters, not two: pausing names the projection AND the tenant.
        RequireMethod(daemon, "PauseShardAsync", typeof(string), typeof(string), typeof(CancellationToken));

        // The rewind the dead-letter screen offers. sequenceFloor and timestamp are optional and the
        // CancellationToken sits in the middle rather than last, so it cannot be passed positionally last.
        RequireMethod(daemon, "RewindSubscriptionAsync",
            typeof(string), typeof(CancellationToken), typeof(long?), typeof(DateTimeOffset?));
        RequireMethod(daemon, "RestartHighWaterAgentAsync", typeof(CancellationToken));

        // Eight RebuildProjectionAsync overloads - by name, by type and by view, each with and without a
        // shard timeout, plus two tenant-scoped ones. The studio drives the string-named ones, so a
        // rebuild can be started from a projection name read straight off the screen.
        RequireMethod(daemon, "RebuildProjectionAsync", typeof(string), typeof(CancellationToken));
        RequireMethod(daemon, "RebuildProjectionAsync", typeof(string), typeof(string), typeof(CancellationToken));
        RequireMethod(daemon, "RebuildProjectionAsync", typeof(string), typeof(TimeSpan), typeof(CancellationToken));
        RequireMethod(daemon, "RebuildProjectionAsync",
            typeof(string), typeof(string), typeof(TimeSpan), typeof(CancellationToken));
        daemon.GetMethods(MemberFlags).Count(x => x.Name == "RebuildProjectionAsync").Should().Be(8);
    }

    /// <summary>U15: which coordinator type to resolve, and when it is registered at all.</summary>
    /// <remarks>
    /// All four candidate types exist. <c>Marten.Events.Daemon.Coordination.IProjectionCoordinator</c>
    /// <em>derives from</em> <c>JasperFx.Events.Daemon.IProjectionCoordinator</c>, so either one resolves
    /// the main store's daemon; the studio asks for the Marten one, as the plan preferred. Both are
    /// registered only by <c>AddAsyncDaemon</c> - their absence is precisely the studio's
    /// "not hosted in this process", and it is a value, not an error.
    /// </remarks>
    [Fact]
    public void The_coordinator_interfaces_agree_and_only_AddAsyncDaemon_registers_them()
    {
        typeof(JasperFxCoordinator).IsAssignableFrom(typeof(MartenCoordinator)).Should().BeTrue();

        foreach (var coordinator in new[] { typeof(MartenCoordinator), typeof(JasperFxCoordinator) })
        {
            RequireMethod(coordinator, "DaemonForMainDatabase").ReturnType.Should().Be<IProjectionDaemon>();
            RequireMethod(coordinator, "DaemonForDatabase", typeof(string))
                .ReturnType.Should().Be<ValueTask<IProjectionDaemon>>();
        }

        // Descriptors only. Nothing is built and no connection is opened, so the dummy connection string
        // is never used - building the provider would compile the store and reach for Postgres.
        var withDaemon = new ServiceCollection();
        withDaemon.AddMarten(x => x.Connection(DummyConnectionString)).AddAsyncDaemon(DaemonMode.Solo);

        withDaemon.Should().Contain(x => x.ServiceType == typeof(MartenCoordinator));
        withDaemon.Should().Contain(x => x.ServiceType == typeof(JasperFxCoordinator));

        var withoutDaemon = new ServiceCollection();
        withoutDaemon.AddMarten(x => x.Connection(DummyConnectionString));

        withoutDaemon.Should().NotContain(x => x.ServiceType == typeof(MartenCoordinator),
            "no AddAsyncDaemon means no coordinator, which is DaemonHosting.NotHostedInThisProcess");
        withoutDaemon.Should().NotContain(x => x.ServiceType == typeof(JasperFxCoordinator));
    }

    /// <summary>
    /// Store discovery (plan §4.2): does <c>AddMartenStore&lt;T&gt;</c> register the marker interface?
    /// </summary>
    /// <remarks>
    /// <b>Yes.</b> The marker interface itself is a singleton registration, so scanning the
    /// <c>IServiceCollection</c> for descriptors whose <c>ServiceType</c> is assignable to
    /// <c>IDocumentStore</c> finds the default store (registered as <c>IDocumentStore</c>) and every
    /// ancillary one (registered as its own marker), which is exactly what the registry needs. An
    /// ancillary store's daemon coordinator is the <em>generic</em> one: <c>AddMartenStore</c> adds no
    /// non-generic coordinator registration at all, so asking for the non-generic interface on behalf of
    /// an ancillary store would quietly hand back the main store's daemon or nothing.
    /// </remarks>
    [Fact]
    public void AddMartenStore_registers_the_marker_interface_and_only_a_generic_coordinator()
    {
        var services = new ServiceCollection();
        services.AddMartenStore<ISampleAncillaryStore>(x => x.Connection(DummyConnectionString))
            .AddAsyncDaemon(DaemonMode.Solo);

        services.Should().Contain(x => x.ServiceType == typeof(ISampleAncillaryStore));
        services.Should().NotContain(x => x.ServiceType == typeof(IDocumentStore),
            "an ancillary store is reachable only through its own marker interface");

        var genericCoordinator = typeof(global::Marten.Events.Daemon.Coordination.IProjectionCoordinator<>)
            .MakeGenericType(typeof(ISampleAncillaryStore));

        services.Should().Contain(x => x.ServiceType == genericCoordinator);
        services.Should().NotContain(x => x.ServiceType == typeof(MartenCoordinator),
            "AddMartenStore never registers the non-generic coordinator");

        // And the default store really is registered under IDocumentStore, which is what the scan keys on.
        var defaultStore = new ServiceCollection();
        defaultStore.AddMarten(x => x.Connection(DummyConnectionString));

        defaultStore.Should().Contain(x => x.ServiceType == typeof(IDocumentStore));
        typeof(IDocumentStore).IsAssignableFrom(typeof(ISampleAncillaryStore)).Should().BeTrue();
    }

    // --------------------------------------------------------------------------------------------
    // Sessions, writes and serialization
    // --------------------------------------------------------------------------------------------

    /// <summary>U6: opening a session against a chosen database or tenant.</summary>
    /// <remarks>
    /// The type is <c>Marten.Services.SessionOptions</c>, not <c>Marten.SessionOptions</c>, and
    /// <c>ForDatabase</c> is a <b>static factory</b>, not an instance builder. There is no
    /// <c>ForTenant</c>: the tenant is the settable <c>TenantId</c> property, or the second argument of
    /// <c>ForDatabase(string tenantId, IMartenDatabase database)</c>. Because the overload takes a
    /// resolved <c>IMartenDatabase</c> rather than a name, the studio can write to a non-default database
    /// in a <c>StaticMultiple</c> store <b>without</b> ever passing a browser-supplied string to
    /// <c>FindOrCreateDatabase</c> - so the plan's "refuse with a message" fallback is not needed.
    /// </remarks>
    [Fact]
    public void SessionOptions_can_select_a_database_and_a_tenant()
    {
        typeof(SessionOptions).FullName.Should().Be("Marten.Services.SessionOptions",
            "the plan called it Marten.SessionOptions");

        var forDatabase = RequireMethod(typeof(SessionOptions), "ForDatabase", typeof(IMartenDatabase));
        forDatabase.ReturnType.Should().Be<SessionOptions>();
        forDatabase.IsStatic.Should().BeTrue("ForDatabase is a static factory, not an instance builder");

        RequireMethod(typeof(SessionOptions), "ForDatabase", typeof(string), typeof(IMartenDatabase))
            .ReturnType.Should().Be<SessionOptions>();
        RequireProperty(typeof(SessionOptions), "TenantId").CanWrite.Should().BeTrue();
        RequireProperty(typeof(SessionOptions), "AllowAnyTenant").PropertyType.Should().Be<bool>();
        RequireProperty(typeof(SessionOptions), "Timeout").PropertyType.Should().Be<int?>();

        typeof(SessionOptions).GetMethod("ForTenant", MemberFlags).Should().BeNull(
            "there is no ForTenant; the tenant is the TenantId property or the two-argument ForDatabase");
    }

    [Fact]
    public void The_write_path_is_the_untyped_object_pair_on_IDocumentOperations()
    {
        RequireMethod(typeof(IDocumentOperations), "StoreObjects", typeof(IEnumerable<object>))
            .ReturnType.Should().Be(typeof(void));
        RequireMethod(typeof(IDocumentOperations), "DeleteObjects", typeof(IEnumerable<object>))
            .ReturnType.Should().Be(typeof(void));
        RequireMethod(typeof(IDocumentSession), "SaveChangesAsync", typeof(CancellationToken))
            .ReturnType.Should().Be<Task>();
    }

    /// <summary>
    /// The serializer the studio must round-trip through. There is <b>no</b> <c>FromJson(Type, string)</c>:
    /// deserializing a raw document means handing the serializer a <see cref="Stream"/> (or the open
    /// <c>DbDataReader</c>), which is why the write path in plan §4.4 reads through a stream.
    /// </summary>
    [Fact]
    public void ISerializer_deserializes_from_a_stream_and_never_from_a_string()
    {
        RequireMethod(typeof(ISerializer), "ToJson", typeof(object)).ReturnType.Should().Be<string>();
        RequireMethod(typeof(ISerializer), "FromJson", typeof(Type), typeof(Stream))
            .ReturnType.Should().Be<object>();
        RequireMethod(typeof(ISerializer), "FromJsonAsync", typeof(Type), typeof(Stream), typeof(CancellationToken))
            .ReturnType.Should().Be<ValueTask<object>>();

        var hasStringOverload = AllMethods(typeof(ISerializer))
            .Where(x => x.Name == "FromJson")
            .Any(x => Matches(x, [typeof(Type), typeof(string)]));

        hasStringOverload.Should().BeFalse(
            "Marten 9.35 offers no string overload - the studio deserializes through a MemoryStream");
    }

    /// <summary>U7: the <c>IJsonLoader</c> overload set behind <c>IQuerySession.Json</c>.</summary>
    /// <remarks>
    /// Five <c>FindByIdAsync&lt;T&gt;</c> overloads - <c>object</c>, <c>string</c>, <c>int</c>,
    /// <c>long</c>, <c>Guid</c> - all returning <c>Task&lt;string&gt;</c>, plus five matching
    /// <c>StreamById&lt;T&gt;</c>. Every one of them is generic in the document type, so the studio - which
    /// only ever knows a runtime <c>Type</c> - cannot call them without reflection. That is the reason
    /// plan §4.4 reads documents with its own <c>data::text</c> SQL instead of through this seam.
    /// </remarks>
    [Fact]
    public void IJsonLoader_is_generic_in_the_document_type_on_every_overload()
    {
        var overloads = AllMethods(typeof(IJsonLoader)).Where(x => x.Name == "FindByIdAsync").ToArray();

        overloads.Should().HaveCount(5);
        overloads.Should().OnlyContain(x => x.IsGenericMethodDefinition);
        overloads.Should().OnlyContain(x => x.ReturnType == typeof(Task<string>));
        overloads.Select(x => x.GetParameters()[0].ParameterType)
            .Should().BeEquivalentTo(new[] { typeof(object), typeof(string), typeof(int), typeof(long), typeof(Guid) });

        RequireProperty(typeof(IQuerySession), "Json").PropertyType.Should().Be<IJsonLoader>();
    }

    // --------------------------------------------------------------------------------------------
    // Events
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void The_event_session_surface_covers_streams_the_feed_and_time_travel()
    {
        RequireProperty(typeof(IQuerySession), "Events").PropertyType.Should().Be<MartenQueryEventStore>();

        RequireMethod(typeof(MartenQueryEventStore), "FetchStreamAsync",
            typeof(Guid), typeof(long), typeof(DateTimeOffset?), typeof(long), typeof(CancellationToken));
        RequireMethod(typeof(MartenQueryEventStore), "FetchStreamAsync",
            typeof(string), typeof(long), typeof(DateTimeOffset?), typeof(long), typeof(CancellationToken));
        RequireMethod(typeof(MartenQueryEventStore), "FetchStreamStateAsync", typeof(Guid), typeof(CancellationToken));
        RequireMethod(typeof(MartenQueryEventStore), "QueryAllRawEvents");

        // Time travel: the second parameter is the version the aggregate is replayed to. `null` in the
        // expected list is the generic `T state` parameter, which no typeof() can name.
        var aggregate = RequireMethod(typeof(MartenQueryEventStore), "AggregateStreamAsync",
            typeof(Guid), typeof(long), typeof(DateTimeOffset?), null, typeof(long), typeof(CancellationToken));

        aggregate.IsGenericMethodDefinition.Should().BeTrue();
        aggregate.GetParameters()[1].Name.Should().Be("version");

        // Archiving is queued on the session until SaveChangesAsync, not an immediate call - which is why
        // it returns void and needs no token.
        RequireMethod(typeof(MartenEventStoreOperations), "ArchiveStream", typeof(Guid))
            .ReturnType.Should().Be(typeof(void));
        RequireMethod(typeof(MartenEventStoreOperations), "ArchiveStream", typeof(string));
        RequireProperty(typeof(IDocumentSession), "Events").PropertyType.Should().Be<MartenEventStoreOperations>();
    }

    [Fact]
    public void DeadLetterEvent_and_ShardState_carry_the_columns_the_screens_show()
    {
        typeof(DeadLetterEvent).FullName.Should().Be("JasperFx.Events.Daemon.DeadLetterEvent");
        RequireProperty(typeof(DeadLetterEvent), "Id").PropertyType.Should().Be<Guid>();
        RequireProperty(typeof(DeadLetterEvent), "ProjectionName").PropertyType.Should().Be<string>();
        RequireProperty(typeof(DeadLetterEvent), "ShardName").PropertyType.Should().Be<string>();
        RequireProperty(typeof(DeadLetterEvent), "Timestamp").PropertyType.Should().Be<DateTimeOffset>();
        RequireProperty(typeof(DeadLetterEvent), "ExceptionMessage").PropertyType.Should().Be<string>();
        RequireProperty(typeof(DeadLetterEvent), "ExceptionType").PropertyType.Should().Be<string>();
        RequireProperty(typeof(DeadLetterEvent), "EventSequence").PropertyType.Should().Be<long>();
        RequireProperty(typeof(DeadLetterEvent), "TenantId").PropertyType.Should().Be<string>();

        // ShardState is in JasperFx.Events.Projections, not .Daemon as the plan said; its tracker is.
        typeof(ShardState).FullName.Should().Be("JasperFx.Events.Projections.ShardState");
        RequireProperty(typeof(ShardState), "ShardName").PropertyType.Should().Be<string>();
        RequireProperty(typeof(ShardState), "Sequence").PropertyType.Should().Be<long>();
        RequireProperty(typeof(ShardState), "TenantId").PropertyType.Should().Be<string>();
        RequireProperty(typeof(ShardState), "Action").PropertyType.Should().Be<ShardAction>();
        RequireProperty(typeof(ShardState), "LastAdvanced").PropertyType.Should().Be<DateTimeOffset?>();
        RequireProperty(typeof(ShardState), "AgentStatus").PropertyType.Should().Be<string>();
        RequireProperty(typeof(ShardState), "PauseReason").PropertyType.Should().Be<string>();
        RequireProperty(typeof(ShardState), "Failure").PropertyType.Should().Be<ShardFailure>();
        RequireProperty(typeof(ShardState), "SkippedEventsCount").PropertyType.Should().Be<long?>();

        typeof(IObservable<ShardState>).IsAssignableFrom(typeof(ShardStateTracker)).Should().BeTrue(
            "the lazy per-database observer in plan §4.5 subscribes to the tracker directly");
    }

    // --------------------------------------------------------------------------------------------
    // Diagnostics and LINQ
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// U10: <c>GetPostgresVersion()</c> is <b>synchronous</b> and returns a <see cref="System.Version"/>.
    /// It still opens a connection, so the Overview tile has to call it off the render path like any other
    /// query rather than treating it as a cheap property read.
    /// </summary>
    [Fact]
    public void GetPostgresVersion_is_synchronous()
    {
        RequireMethod(typeof(IDiagnostics), "GetPostgresVersion").ReturnType.Should().Be<Version>();

        AllMethods(typeof(IDiagnostics)).Should().NotContain(x => x.Name == "GetPostgresVersionAsync");
    }

    /// <summary>
    /// The LINQ helpers the Query screen shows. <c>ToJsonArrayAsync</c> does <b>not</b> exist: the member
    /// is <c>ToJsonArray</c>, which is already asynchronous (<c>Task&lt;string&gt;</c>) despite the name.
    /// There is no synchronous <c>Explain</c> either, only <c>ExplainAsync</c> - hard rule 10 in the small.
    /// </summary>
    [Fact]
    public void The_queryable_extensions_are_ToJsonArray_ExplainAsync_and_ToCommand()
    {
        var extensions = typeof(QueryableExtensions);

        extensions.Namespace.Should().Be("Marten");

        RequireMethod(extensions, "ToJsonArray").ReturnType.Should().Be<Task<string>>();
        extensions.GetMethod("ToJsonArrayAsync", MemberFlags).Should().BeNull(
            "the plan called it ToJsonArrayAsync; the real name is ToJsonArray and it already returns a Task");

        RequireMethod(extensions, "ExplainAsync");
        extensions.GetMethod("Explain", MemberFlags).Should().BeNull(
            "Marten 9 has no synchronous Explain");

        RequireMethod(extensions, "ToCommand").ReturnType.Should().Be<NpgsqlCommand>();
    }

    /// <summary>
    /// The one string-query overload that takes a runtime <see cref="Type"/>, which is the whole reason the
    /// Query screen can run a <c>where</c> clause at all.
    /// </summary>
    /// <remarks>
    /// Every other <c>Query</c>/<c>QueryAsync</c> on <c>IQuerySession</c> is generic in the document type,
    /// and the studio only ever holds an <c>IDocumentType</c> - so without this member the Query screen
    /// would need <c>MakeGenericMethod</c> over a helper, which is what the plan assumed. It does not:
    /// <c>Marten.QuerySessionExtensions.QueryAsync(session, type, sql, token, parameters)</c> exists and
    /// returns <c>Task&lt;IReadOnlyList&lt;object&gt;&gt;</c>. If it ever moves, Mode A is what breaks.
    /// </remarks>
    [Fact]
    public void The_string_query_by_runtime_type_is_QuerySessionExtensions_QueryAsync()
    {
        var extensions = typeof(QuerySessionExtensions);

        extensions.Namespace.Should().Be("Marten");

        var method = RequireMethod(
            extensions,
            "QueryAsync",
            typeof(IQuerySession),
            typeof(Type),
            typeof(string),
            typeof(CancellationToken),
            typeof(object[]));

        method.IsStatic.Should().BeTrue("it is an extension method on IQuerySession");
        method.IsGenericMethod.Should().BeFalse("the studio only ever has a runtime Type");
        method.ReturnType.Should().Be<Task<IReadOnlyList<object>>>();
        method.GetParameters()[4].IsDefined(typeof(ParamArrayAttribute), inherit: false).Should().BeTrue();
    }

    // --------------------------------------------------------------------------------------------
    // Declared schema, without a connection (P7-fix)
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// The declared index surface <c>SchemaDeclarationReader</c> reads instead of calling
    /// <c>AllObjects()</c>.
    /// </summary>
    /// <remarks>
    /// AGENTS.md hard rule 14: <c>AllSchemaNames()</c> and <c>AllObjects()</c> run Weasel migrations
    /// through Marten's lazy <c>Sequences</c> feature, so a navigation that wanted the list of indexes
    /// created <c>mt_hilo</c> as a side effect. These members are in-memory object graphs and are the
    /// whole of what a read path is allowed to use. <c>Indexes</c> and <c>ForeignKeys</c> are
    /// <c>IList&lt;&gt;</c> rather than read-only collections, which is Marten's shape and not a
    /// suggestion that a reader may add to them.
    /// </remarks>
    [Fact]
    public void IDocumentType_declares_its_indexes_and_foreign_keys_in_memory()
    {
        var documentType = typeof(IDocumentType);

        RequireProperty(documentType, "Indexes").PropertyType
            .Should().Be<IList<Weasel.Postgresql.Tables.IndexDefinition>>();
        RequireProperty(documentType, "ForeignKeys").PropertyType
            .Should().Be<IList<Weasel.Postgresql.Tables.ForeignKey>>();
        RequireProperty(documentType, "TenancyStyle").PropertyType.Should().Be<TenancyStyle>();

        RequireMethod(documentType, "IndexesFor", typeof(string)).ReturnType
            .Should().Be<IEnumerable<DocumentIndex>>();
    }

    /// <summary>
    /// The four <c>DocumentMapping</c> members that decide which indexes and columns a table really has.
    /// </summary>
    /// <remarks>
    /// None of them is on <see cref="IDocumentType" />, so reading them means casting to the concrete
    /// public <see cref="DocumentMapping" /> - which AGENTS.md hard rule 10 already requires for
    /// <c>DeleteStyle</c>. <c>IgnoredIndexes</c> is the escape hatch a host uses to build an index out of
    /// band; an index on that list is one no migration will drop, which is the difference between advice
    /// that is true and advice that is alarming. <c>PrimaryKeyTenancyOrdering</c> decides whether a
    /// conjoined table gets a separate <c>tenant_id</c> index at all.
    /// </remarks>
    [Fact]
    public void DocumentMapping_carries_the_ignored_indexes_the_delete_style_and_the_tenancy_shape()
    {
        var mapping = typeof(DocumentMapping);

        mapping.IsPublic.Should().BeTrue("the studio has to cast IDocumentType to it");

        RequireProperty(mapping, "IgnoredIndexes").PropertyType.Should().Be<IList<string>>();
        RequireProperty(mapping, "DeleteStyle").PropertyType.Should().Be<DeleteStyle>();
        RequireProperty(mapping, "TenancyStyle").PropertyType.Should().Be<TenancyStyle>();
        RequireProperty(mapping, "PrimaryKeyTenancyOrdering").PropertyType
            .Should().Be<PrimaryKeyTenancyOrdering>();

        RequireMethod(mapping, "IgnoreIndex", typeof(string)).ReturnType.Should().Be(typeof(void));
    }

    /// <summary>
    /// <c>DocumentIndex</c>'s constructor, which is how the studio renders the DDL for a structural index
    /// Marten adds without declaring it on <c>IDocumentType.Indexes</c>.
    /// </summary>
    /// <remarks>
    /// <c>mt_doc_type</c> on a hierarchy, <c>mt_deleted</c> on a soft-deleted type and <c>tenant_id</c> on
    /// a conjoined one are added inside <c>Marten.Storage.DocumentTable</c>'s constructor, which is
    /// <c>internal</c> and builds a table by connecting to nothing - but only reachable by building the
    /// table. Constructing the same <see cref="DocumentIndex" /> the mapping would produce gives the
    /// identical name and DDL with no database and no internal type.
    /// </remarks>
    [Fact]
    public void DocumentIndex_is_constructible_from_a_mapping_and_columns()
    {
        var index = typeof(DocumentIndex);

        index.IsPublic.Should().BeTrue();
        index.Should().BeAssignableTo<Weasel.Postgresql.Tables.IndexDefinition>();

        var constructor = index.GetConstructor([typeof(DocumentMapping), typeof(string[])]);

        constructor.Should().NotBeNull("the studio builds the structural indexes DocumentTable would add");
        constructor!.GetParameters()[1].IsDefined(typeof(ParamArrayAttribute), inherit: false)
            .Should().BeTrue("the columns are a params array");
    }

    /// <summary>
    /// A table carries its own ignored-index list, which is what the event-store tables hand back.
    /// </summary>
    [Fact]
    public void Weasel_Table_carries_its_ignored_indexes()
    {
        RequireProperty(typeof(Weasel.Postgresql.Tables.Table), "IgnoredIndexes").PropertyType
            .Should().BeAssignableTo<System.Collections.IEnumerable>();

        RequireProperty(typeof(Weasel.Postgresql.Tables.Table), "Indexes").PropertyType
            .Should().BeAssignableTo<System.Collections.IEnumerable>();
    }

    /// <summary>
    /// The event store declares its own schema objects, and its ignored indexes, without a connection.
    /// </summary>
    /// <remarks>
    /// <c>EventGraph</c> implements <c>Weasel.Core.Migrations.IFeatureSchema</c> in a partial file, and
    /// its <c>Objects</c> are built by constructors alone - <c>StreamsTable</c>, <c>EventsTable</c> and
    /// the progression tables. That is the event-store half of reading the declared schema off the
    /// options rather than off the database. <c>IReadOnlyEventStoreOptions.IgnoredIndexes</c> is the
    /// read side of <c>IEventStoreOptions.IgnoreIndex</c>, which a host calls to build an index itself.
    /// </remarks>
    [Fact]
    public void The_event_store_is_a_feature_schema_and_declares_its_ignored_indexes()
    {
        var eventGraph = MartenType("Marten.Events.EventGraph");

        eventGraph.Should().BeAssignableTo<Weasel.Core.Migrations.IFeatureSchema>(
            "the declared event-store objects are read off the options, never off the database");

        // Implemented explicitly, so the cast is the only way in: there is no EventGraph.Objects.
        eventGraph.GetProperty("Objects", MemberFlags).Should().BeNull(
            "EventGraph implements IFeatureSchema explicitly - read Objects through the interface");
        RequireProperty(typeof(Weasel.Core.Migrations.IFeatureSchema), "Objects").PropertyType
            .Should().Be<Weasel.Core.ISchemaObject[]>();

        RequireProperty(typeof(MartenReadOnlyEventStoreOptions), "IgnoredIndexes").PropertyType
            .Should().Be<IReadOnlyList<string>>();

        RequireMethod(typeof(global::Marten.Events.IEventStoreOptions), "IgnoreIndex", typeof(string))
            .ReturnType.Should().Be<global::Marten.Events.IEventStoreOptions>();

        RequireMethod(typeof(MartenRegistry.DocumentMappingExpression<SampleDocument>), "IgnoreIndex", typeof(string))
            .ReturnType.Should().Be<MartenRegistry.DocumentMappingExpression<SampleDocument>>();
    }

    /// <summary>
    /// What a refused migration throws, and what it does <em>not</em> derive from.
    /// </summary>
    /// <remarks>
    /// A direct subclass of <see cref="Exception" /> - neither a <c>NpgsqlException</c> nor a
    /// <c>MartenCommandException</c> - so the apply path has to name it to tell "Weasel judged this
    /// migration invalid" apart from "the database refused the statement". Catching it by a base type
    /// that happens to work today is how the two get conflated.
    /// </remarks>
    [Fact]
    public void SchemaMigrationException_is_a_plain_Exception()
    {
        var exception = typeof(Weasel.Core.SchemaMigrationException);

        exception.BaseType.Should().Be<Exception>(
            "the apply path distinguishes a refused migration from a database error");
        exception.Namespace.Should().Be("Weasel.Core");
    }

    /// <summary>
    /// The tracker knows which database it belongs to, which is how a daemon is matched to a scope.
    /// </summary>
    /// <remarks>
    /// It holds <c>IDatabase.Identifier</c> - the coordinator's own identity - and not
    /// <c>DatabaseId.Identity</c>, which is derived from the connection string. Matching on the wrong one
    /// silently hands a multi-database store the main database's daemon (P5 review).
    /// </remarks>
    [Fact]
    public void ShardStateTracker_names_its_database()
    {
        RequireProperty(typeof(ShardStateTracker), "DatabaseIdentifier").PropertyType.Should().Be<string>();
    }

    // --------------------------------------------------------------------------------------------
    // The compile-time half
    // --------------------------------------------------------------------------------------------

    /// <summary>Takes a delegate to <see cref="CompileOnlyAsync"/> without calling it.</summary>
    /// <remarks>
    /// The point is the <c>Func&lt;&gt;</c> conversion: it makes the method reachable, so no
    /// unused-member analyzer has to be suppressed and no <c>NoWarn</c> is added to the csproj, while the
    /// body is still never executed - it would need a live Postgres. Everything the body touches has
    /// already been checked by the compiler by the time this assembly exists at all.
    /// </remarks>
    [Fact]
    public void The_compile_only_probe_is_referenced_so_nothing_has_to_be_suppressed()
    {
        Func<IDocumentStore, CancellationToken, Task> probe = CompileOnlyAsync;

        probe.Should().NotBeNull();
        probe.Method.Name.Should().Be(nameof(CompileOnlyAsync));
    }

    /// <summary>
    /// Never invoked. Every line exists so the C# compiler verifies one signature the plan depends on.
    /// </summary>
    private static async Task CompileOnlyAsync(IDocumentStore store, CancellationToken token)
    {
        // --- store, options, document types ---------------------------------------------------------
        IReadOnlyStoreOptions options = store.Options;
        IMartenStorage storage = store.Storage;
        AdvancedOperations advanced = store.Advanced;
        IDiagnostics diagnostics = store.Diagnostics;

        Version postgresVersion = diagnostics.GetPostgresVersion();
        _ = postgresVersion.Major;

        IReadOnlyList<IDocumentType> documentTypes = options.AllKnownDocumentTypes();
        IDocumentType documentType = options.FindOrResolveDocumentType(typeof(SampleDocument));
        ISerializer serializer = options.Serializer();
        IDocumentSchemaResolver schemaResolver = options.Schema;
        TenantIdStyle tenantIdStyle = options.TenantIdStyle;
        _ = (documentTypes.Count, options.DatabaseSchemaName, tenantIdStyle, schemaResolver);

        Weasel.Core.DbObjectName tableName = documentType.TableName;
        _ = (tableName.Schema, tableName.Name, tableName.QualifiedName);
        _ = (documentType.DocumentType, documentType.IdType, documentType.Alias, documentType.DatabaseSchemaName);
        _ = (documentType.Root, documentType.UseOptimisticConcurrency, documentType.TenancyStyle);
        _ = (documentType.IdStrategy, documentType.IdMember, documentType.SubClasses, documentType.Indexes);
        _ = (documentType.ForeignKeys, documentType.IsHierarchy());
        _ = documentType.AliasFor(typeof(SampleDocument));
        _ = documentType.TypeFor("sample_document");

        DocumentMetadataCollection metadata = documentType.Metadata;
        MetadataColumn[] everyMetadataColumn =
        [
            metadata.IsSoftDeleted, metadata.SoftDeletedAt, metadata.Version, metadata.Revision,
            metadata.LastModified, metadata.CreatedAt, metadata.TenantId, metadata.DotNetType,
            metadata.DocumentType, metadata.CausationId, metadata.CorrelationId, metadata.LastModifiedBy,
            metadata.Headers,
        ];
        foreach (MetadataColumn column in everyMetadataColumn)
        {
            _ = (column.Enabled, column.Name);
        }

        foreach (DuplicatedField duplicated in documentType.DuplicatedFields)
        {
            _ = (duplicated.ColumnName, duplicated.MemberName, duplicated.PgType, duplicated.DbType);
        }

        // --- event store configuration ----------------------------------------------------------------
        MartenReadOnlyEventStoreOptions events = options.Events;
        IReadOnlyList<ISubscriptionSource> projections = events.Projections();
        IReadOnlyList<IEventType> eventTypes = events.AllKnownEventTypes();
        _ = (events.StreamIdentity, events.TenancyStyle, events.DatabaseSchemaName, events.AppendMode, events.Daemon);

        MartenMetadataConfig metadataConfig = events.MetadataConfig;
        _ = (metadataConfig.CausationIdEnabled, metadataConfig.CorrelationIdEnabled,
            metadataConfig.HeadersEnabled, metadataConfig.UserNameEnabled);

        foreach (ISubscriptionSource source in projections)
        {
            ShardName[] shards = source.ShardNames();
            _ = (source.Name, source.Version, source.Type, source.Lifecycle, source.ImplementationType, shards.Length);
        }

        foreach (IEventType eventType in eventTypes)
        {
            _ = (eventType.Alias, eventType.EventType);
        }

        // --- storage and schema ------------------------------------------------------------------------
        IMartenDatabase database = storage.Database;
        IReadOnlyList<IMartenDatabase> databases = await storage.AllDatabases();
        _ = await storage.FindOrCreateDatabase(database.Id.Identity);
        _ = (storage.AllSchemaNames(), storage.AllObjects(), storage.ToDatabaseScript(), databases.Count);
        await storage.ApplyAllConfiguredChangesToDatabaseAsync();

        Weasel.Core.SchemaMigration migration = await storage.CreateMigrationAsync();
        Weasel.Core.SchemaPatchDifference difference = migration.Difference;
        _ = (migration.Deltas, migration.Schemas, difference);

        // U5: no UpdateSql() and no ToSql() - this is how the Schema screen gets its preview SQL.
        using var previewWriter = new StringWriter();
        migration.WriteAllUpdates(previewWriter, database.Migrator, AutoCreate.CreateOrUpdate);

        // --- one database ------------------------------------------------------------------------------
        DatabaseId databaseId = database.Id;
        _ = (databaseId.Identity, databaseId.Name, databaseId.Server);
        _ = await database.DocumentTables();
        _ = await database.Functions();
        _ = await database.ExistingTableFor(typeof(SampleDocument));
        _ = await database.SchemaTables(token);
        _ = await database.FetchEventStoreStatistics(token);
        IReadOnlyList<ShardState> progress = await database.AllProjectionProgress(token);
        ShardName[] shardNames = [.. progress.Select(x => new ShardName(x.ShardName))];
        _ = await database.FetchProjectionProgressFor(shardNames, token);
        _ = await database.ProjectionProgressFor(shardNames[0], token);
        long highWaterMark = await database.FetchHighestEventSequenceNumber(token);
        await database.MarkEventsAsSkipped([highWaterMark], token);
        await database.AssertDatabaseMatchesConfigurationAsync(token);

        ShardStateTracker tracker = database.Tracker;
        using (((IObservable<ShardState>)tracker).Subscribe(new NullShardObserver()))
        {
            await using NpgsqlConnection connection = database.CreateConnection(ConnectionUsage.Read);
            _ = connection.State;
        }

        // --- tenancy ------------------------------------------------------------------------------------
        ITenancy tenancy = options.Tenancy;
        DatabaseCardinality cardinality = tenancy.Cardinality;
        DatabaseUsage usage = await tenancy.DescribeDatabasesAsync(token);
        foreach (DatabaseDescriptor descriptor in usage.Databases)
        {
            List<string> tenantIds = descriptor.TenantIds;
            _ = (descriptor.Identifier, tenantIds.Count, cardinality);
        }

        // --- advanced operations and the daemon ----------------------------------------------------------
        IDocumentCleaner cleaner = advanced.Clean;
        await cleaner.DeleteDocumentsByTypeAsync(typeof(SampleDocument), token);
        _ = advanced.AllAsyncProjectionShardNames();
        await advanced.AdvanceHighWaterMarkToLatestAsync(token);
        await advanced.TryCorrectProgressInDatabaseAsync(token);

        MartenCoordinator coordinator = null!;
        IProjectionDaemon daemon = coordinator.DaemonForMainDatabase();
        daemon = await coordinator.DaemonForDatabase(databaseId.Identity);
        await daemon.StartAllAsync();
        await daemon.StopAllAsync();
        await daemon.StartAgentAsync("Projection:All", token);
        await daemon.StopAgentAsync("Projection:All");
        await daemon.RebuildProjectionAsync("Projection", token);
        await daemon.PauseShardAsync("Projection", "*DEFAULT*", token);
        await daemon.RewindSubscriptionAsync("Projection", token, sequenceFloor: 0);
        await daemon.RestartHighWaterAgentAsync(token);
        daemon.EjectPausedShard("Projection:All");
        _ = (daemon.CurrentAgents(), daemon.Tracker, daemon.IsRunning, daemon.HasAnyPaused());

        // --- sessions, writes, serialization --------------------------------------------------------------
        SessionOptions sessionOptions = SessionOptions.ForDatabase("acme", database);
        sessionOptions.TenantId = "acme";

        await using IDocumentSession session = store.LightweightSession(sessionOptions);
        await using IDocumentSession tenantSession = store.LightweightSession("acme");
        await using IQuerySession querySession = store.QuerySession();

        // Deliberately a runtime Type rather than typeof(...): the studio only ever has the Type it read
        // off IDocumentType, which is exactly why the non-generic overloads have to exist.
        Type runtimeDocumentType = documentType.DocumentType;

        using var documentStream = new MemoryStream();
        object document = serializer.FromJson(runtimeDocumentType, documentStream);
        document = await serializer.FromJsonAsync(runtimeDocumentType, documentStream, token);
        _ = serializer.ToJson(document);

        session.StoreObjects([document]);
        session.DeleteObjects([document]);
        await session.SaveChangesAsync(token);
        _ = tenantSession.TenantId;

        IJsonLoader jsonLoader = querySession.Json;
        _ = await jsonLoader.FindByIdAsync<SampleDocument>(Guid.Empty, token);

        // --- events -----------------------------------------------------------------------------------------
        MartenQueryEventStore eventQueries = querySession.Events;
        _ = await eventQueries.FetchStreamAsync(Guid.Empty, token: token);
        _ = await eventQueries.FetchStreamStateAsync(Guid.Empty, token);
        _ = eventQueries.QueryAllRawEvents();
        _ = await eventQueries.AggregateStreamAsync<SampleAggregate>(Guid.Empty, version: 3, token: token);
        session.Events.ArchiveStream(Guid.Empty);

        // --- LINQ -------------------------------------------------------------------------------------------
        IQueryable<SampleDocument> queryable = querySession.Query<SampleDocument>();
        _ = await queryable.ToJsonArray(token);
        _ = await queryable.ExplainAsync(token);
        using NpgsqlCommand command = queryable.ToCommand();
        _ = command.CommandText;
    }

    // --------------------------------------------------------------------------------------------
    // Fixtures and helpers
    // --------------------------------------------------------------------------------------------

    internal interface ISampleAncillaryStore : IDocumentStore;

    internal class SampleDocument
    {
        public Guid Id { get; set; }
    }

    internal class SampleAggregate
    {
        public Guid Id { get; set; }
    }

    private sealed class NullShardObserver : IObserver<ShardState>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(ShardState value)
        {
        }
    }

    private static Type MartenType(string fullName) =>
        typeof(IDocumentStore).Assembly.GetType(fullName, throwOnError: true)!;

    /// <summary>
    /// <see cref="Type.GetProperty(string, BindingFlags)"/> does not look into base <em>interfaces</em>,
    /// so an interface search has to walk them explicitly. Classes the runtime already handles.
    /// </summary>
    private static IEnumerable<Type> SearchTypes(Type type) =>
        type.IsInterface ? [type, .. type.GetInterfaces()] : [type];

    private static IEnumerable<MethodInfo> AllMethods(Type type) =>
        SearchTypes(type).SelectMany(x => x.GetMethods(MemberFlags));

    private static PropertyInfo RequireProperty(Type type, string name)
    {
        var property = SearchTypes(type)
            .Select(x => x.GetProperty(name, MemberFlags))
            .FirstOrDefault(x => x is not null);

        property.Should().NotBeNull($"{type.FullName}.{name} is a Marten 9.35 member the plan depends on");
        return property!;
    }

    /// <summary>
    /// Finds a method by name and parameter types. A <c>null</c> entry in
    /// <paramref name="parameterTypes"/> matches any parameter, which is how a generic parameter
    /// (<c>T state</c> on <c>AggregateStreamAsync&lt;T&gt;</c>) is skipped over. Passing no types at all
    /// asks for the parameterless overload, or for the only overload when there is just one.
    /// </summary>
    private static MethodInfo RequireMethod(Type type, string name, params Type?[] parameterTypes)
    {
        var candidates = AllMethods(type).Where(x => x.Name == name).ToArray();

        candidates.Should().NotBeEmpty($"{type.FullName}.{name} is a Marten 9.35 member the plan depends on");

        if (parameterTypes.Length == 0)
        {
            var parameterless = candidates.FirstOrDefault(x => x.GetParameters().Length == 0);
            if (parameterless is not null)
            {
                return parameterless;
            }

            candidates.Should().ContainSingle($"{type.FullName}.{name} has no parameterless overload");
            return candidates[0];
        }

        var match = candidates.FirstOrDefault(x => Matches(x, parameterTypes));

        match.Should().NotBeNull(
            $"{type.FullName}.{name} should take ({string.Join(", ", parameterTypes.Select(x => x?.Name ?? "?"))}); " +
            $"the overloads are {string.Join(" | ", candidates.Select(Describe))}");

        return match!;
    }

    private static bool Matches(MethodInfo method, Type?[] parameterTypes)
    {
        var parameters = method.GetParameters();

        return parameters.Length == parameterTypes.Length &&
               parameters.Zip(parameterTypes).All(x => x.Second is null || x.First.ParameterType == x.Second);
    }

    private static string Describe(MethodInfo method) =>
        $"({string.Join(", ", method.GetParameters().Select(x => x.ParameterType.Name))})";
}
