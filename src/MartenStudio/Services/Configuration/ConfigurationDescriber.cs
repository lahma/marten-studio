using System.Globalization;

using JasperFx;
using JasperFx.Descriptors;
using Marten;
using Marten.Linq.Members;
using Marten.Schema;
using MartenStudio.Internal.Sql;

using Weasel.Postgresql.Tables;

namespace MartenStudio.Services.Configuration;

/// <summary>
/// Turns Marten's read-only configuration into the cards the Configuration page renders.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static, so it can be tested against a <c>DocumentStore.For(...)</c> built on an unreachable
/// host - the same trick <c>MartenApiSurfaceTest</c> and <c>SqlTestStore</c> use. That matters more here
/// than anywhere else in the studio: this screen's whole claim is that it reports what the host actually
/// configured, and a test against a hand-written fake would agree with the test and disagree with Marten.
/// </para>
/// <para>
/// <b>No connection string, ever.</b> Not the store's, not a database descriptor's <c>Properties</c>
/// list, not <c>DatabaseDescriptor.SubjectUri</c>. The five safe fields of a descriptor are copied one at
/// a time into <see cref="ConfiguredDatabase" /> and the rest are not carried at all, so there is nothing
/// for a later refactor to print by accident. A unit test builds a store whose connection string has a
/// password in it and greps every rendered string for it.
/// </para>
/// </remarks>
internal static class ConfigurationDescriber
{
    /// <summary>Describes the store card.</summary>
    /// <param name="storeKey">The registration key.</param>
    /// <param name="displayName">What the selector calls the store.</param>
    /// <param name="options">The store's read-only options.</param>
    /// <param name="autoCreate">
    /// The database's own <c>AutoCreate</c>. It is read from <c>IMartenDatabase</c> rather than from
    /// <c>IReadOnlyStoreOptions</c>, because <c>AutoCreateSchemaObjects</c> is on the mutable
    /// <c>StoreOptions</c> only - the read-only interface does not carry it.
    /// </param>
    /// <param name="postgresVersion">The server version, or <see langword="null" />.</param>
    /// <param name="databases">The databases, already reduced to their safe fields.</param>
    /// <param name="databaseNotice">Why the database list is empty or partial.</param>
    public static StoreConfiguration DescribeStore(
        string storeKey,
        string displayName,
        IReadOnlyStoreOptions options,
        AutoCreate? autoCreate,
        string? postgresVersion,
        IReadOnlyList<ConfiguredDatabase> databases,
        string? databaseNotice)
    {
        ArgumentNullException.ThrowIfNull(options);

        ISerializer serializer = options.Serializer();
        IReadOnlyAdvancedOptions advanced = options.Advanced;

        List<ConfigurationValue> values =
        [
            new("Document schema", options.DatabaseSchemaName, "StoreOptions.DatabaseSchemaName"),
            new("Auto-create", autoCreate?.ToString() ?? "not reported", "StoreOptions.AutoCreateSchemaObjects"),
            new("Serializer", serializer.GetType().Name, "StoreOptions.Serializer(...)"),
            new("Serializer casing", serializer.Casing.ToString(), "StoreOptions.UseSystemTextJsonForSerialization(casing: ...)"),
            new("Enum storage", serializer.EnumStorage.ToString(), "StoreOptions.UseSystemTextJsonForSerialization(enumStorage: ...)"),
            new("Value casting", serializer.ValueCasting.ToString(), "StoreOptions.Serializer(...).ValueCasting"),
            new("Tenant id style", options.TenantIdStyle.ToString(), "StoreOptions.TenantIdStyle"),
            new("Database cardinality", options.Tenancy.Cardinality.ToString(), "StoreOptions.Tenancy.Cardinality"),
            new("Duplicated field enum storage", advanced.DuplicatedFieldEnumStorage.ToString(), "StoreOptions.Advanced.DuplicatedFieldEnumStorage"),
            new("Default tenant usage", YesNo(advanced.DefaultTenantUsageEnabled), "StoreOptions.Advanced.DefaultTenantUsageEnabled"),
            new("N-gram search with unaccent", YesNo(advanced.UseNGramSearchWithUnaccent), "StoreOptions.Advanced.UseNGramSearchWithUnaccent"),
            new("Identifier length limit", Number(options.NameDataLength), "StoreOptions.NameDataLength"),
            new("Update batch size", Number(options.UpdateBatchSize), "StoreOptions.UpdateBatchSize"),
            new("PostgreSQL version", postgresVersion ?? "cannot report", "IDocumentStore.Diagnostics.GetPostgresVersion()"),
        ];

        return new StoreConfiguration(storeKey, displayName, values, databases, databaseNotice);
    }

    /// <summary>
    /// The five safe fields of a <see cref="DatabaseDescriptor" />.
    /// </summary>
    /// <remarks>
    /// Named one at a time on purpose. A descriptor's <c>Properties</c>, <c>Sets</c>, <c>SubjectUri</c>
    /// and <c>DatabaseUri()</c> are all free-form and all places a provider could put a connection
    /// string, so none of them is copied.
    /// </remarks>
    public static ConfiguredDatabase DescribeDatabase(DatabaseDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new ConfiguredDatabase(
            descriptor.Identifier,
            descriptor.Engine,
            descriptor.ServerName,
            descriptor.Port,
            descriptor.DatabaseName,
            descriptor.SchemaOrNamespace,
            [.. descriptor.TenantIds]);
    }

    /// <summary>Describes the event-store card.</summary>
    /// <param name="events">The store's read-only event configuration.</param>
    public static EventStoreConfiguration DescribeEvents(Marten.Events.IReadOnlyEventStoreOptions events)
    {
        ArgumentNullException.ThrowIfNull(events);

        List<ConfigurationValue> values =
        [
            new("Stream identity", events.StreamIdentity.ToString(), "StoreOptions.Events.StreamIdentity"),
            new("Append mode", events.AppendMode.ToString(), "StoreOptions.Events.AppendMode"),
            new("Tenancy", events.TenancyStyle.ToString(), "StoreOptions.Events.TenancyStyle"),
            new("Event schema", events.DatabaseSchemaName, "StoreOptions.Events.DatabaseSchemaName"),
            new("Event naming", events.EventNamingStyle.ToString(), "StoreOptions.Events.EventNamingStyle"),
            new("Mandatory stream type", YesNo(events.UseMandatoryStreamTypeDeclaration), "StoreOptions.Events.UseMandatoryStreamTypeDeclaration"),
            new("Archived stream partitioning", YesNo(events.UseArchivedStreamPartitioning), "StoreOptions.Events.UseArchivedStreamPartitioning"),
            new("Tenant partitioned events", YesNo(events.UseTenantPartitionedEvents), "StoreOptions.Events.UseTenantPartitionedEvents"),
            new("Event skipping", YesNo(events.EnableEventSkippingInProjectionsOrSubscriptions), "StoreOptions.Events.EnableEventSkippingInProjectionsOrSubscriptions"),
            new("Optimized rebuilds", YesNo(events.UseOptimizedProjectionRebuilds), "StoreOptions.Events.UseOptimizedProjectionRebuilds"),
        ];

        Marten.Events.IReadonlyMetadataConfig metadata = events.MetadataConfig;

        // Four flags, and four is all there is: Marten.Events.IReadonlyMetadataConfig (lower-case "o",
        // unlike every neighbouring IReadOnly* type) carries these and nothing else.
        List<ConfigurationValue> metadataValues =
        [
            new("Causation id", YesNo(metadata.CausationIdEnabled), "StoreOptions.Events.MetadataConfig.CausationIdEnabled"),
            new("Correlation id", YesNo(metadata.CorrelationIdEnabled), "StoreOptions.Events.MetadataConfig.CorrelationIdEnabled"),
            new("Headers", YesNo(metadata.HeadersEnabled), "StoreOptions.Events.MetadataConfig.HeadersEnabled"),
            new("User name", YesNo(metadata.UserNameEnabled), "StoreOptions.Events.MetadataConfig.UserNameEnabled"),
        ];

        JasperFx.Events.Daemon.IReadOnlyDaemonSettings daemon = events.Daemon;

        List<ConfigurationValue> daemonValues =
        [
            new("Async mode", daemon.AsyncMode.ToString(), "StoreOptions.Events.Daemon.AsyncMode"),
            new("Slow polling", Duration(daemon.SlowPollingTime), "StoreOptions.Events.Daemon.SlowPollingTime"),
            new("Fast polling", Duration(daemon.FastPollingTime), "StoreOptions.Events.Daemon.FastPollingTime"),
            new("Health check polling", Duration(daemon.HealthCheckPollingTime), "StoreOptions.Events.Daemon.HealthCheckPollingTime"),
            new("Stale sequence threshold", Duration(daemon.StaleSequenceThreshold), "StoreOptions.Events.Daemon.StaleSequenceThreshold"),
            new("High water staleness", Duration(daemon.HighWaterStalenessThreshold), "StoreOptions.Events.Daemon.HighWaterStalenessThreshold"),
            new("Stop and drain timeout", Duration(daemon.StopAndDrainTimeout), "StoreOptions.Events.Daemon.StopAndDrainTimeout"),
        ];

        return new EventStoreConfiguration(
            values,
            metadataValues,
            daemonValues,
            events.AllKnownEventTypes().Count,
            events.Projections().Count);
    }

    /// <summary>Describes every document type the store knows about, alias order.</summary>
    /// <param name="options">The store's read-only options.</param>
    /// <param name="isVisible">
    /// <c>MartenStudioOptions.IsDocumentTypeVisible</c>, or <see langword="null" /> when the host set none.
    /// </param>
    public static IReadOnlyList<DocumentTypeConfiguration> DescribeDocumentTypes(
        IReadOnlyStoreOptions options,
        Func<Type, bool>? isVisible = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<DocumentTypeConfiguration> described = [];

        foreach (IDocumentType documentType in options.AllKnownDocumentTypes())
        {
            if (isVisible is not null && !isVisible(documentType.DocumentType))
            {
                continue;
            }

            described.Add(DescribeDocumentType(documentType));
        }

        described.Sort(static (left, right) => string.CompareOrdinal(left.Alias, right.Alias));
        return described;
    }

    /// <summary>Describes one document type's card.</summary>
    public static DocumentTypeConfiguration DescribeDocumentType(IDocumentType documentType)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        var table = new Table(documentType.TableName);

        List<ConfigurationValue> values =
        [
            new(".NET type", documentType.DocumentType.FullName ?? documentType.DocumentType.Name, "StoreOptions.Schema.For<T>()"),
            new("Table", documentType.TableName.QualifiedName, "StoreOptions.Schema.For<T>().DatabaseSchemaName(...)"),
            new("Id member", documentType.IdMember?.Name ?? "unknown", "StoreOptions.Schema.For<T>().Identity(x => x.Id)"),
            new("Id type", TypeName(documentType.IdType), "StoreOptions.Schema.For<T>().Identity(...)"),
            new("Id strategy", documentType.IdStrategy?.GetType().Name ?? "unknown", "StoreOptions.Schema.For<T>().IdStrategy(...)"),
            new("Optimistic concurrency", YesNo(documentType.UseOptimisticConcurrency), "StoreOptions.Schema.For<T>().UseOptimisticConcurrency(true)"),
            new("Numeric revisions", YesNo(NumericRevisions(documentType)), "StoreOptions.Schema.For<T>().UseNumericRevisions(true)"),
            new("Delete style", DeleteStyleOf(documentType), "StoreOptions.Schema.For<T>().SoftDeleted()"),
            new("Tenancy", documentType.TenancyStyle.ToString(), "StoreOptions.Schema.For<T>().MultiTenanted()"),
            new("Hierarchy", YesNo(documentType.IsHierarchy()), "StoreOptions.Schema.For<T>().AddSubClass<TSub>()"),
            new("Casing", documentType.Casing.ToString(), "StoreOptions.Schema.For<T>().Casing(...)"),
            new("Enum storage", documentType.EnumStorage.ToString(), "StoreOptions.Schema.For<T>().EnumStorage(...)"),
        ];

        List<DuplicatedFieldConfiguration> duplicated = [];
        foreach (DuplicatedField field in documentType.DuplicatedFields)
        {
            duplicated.Add(new DuplicatedFieldConfiguration(field.MemberName, field.ColumnName, field.PgType));
        }

        List<ConfiguredIndex> indexes = [];
        foreach (IndexDefinition index in documentType.Indexes)
        {
            indexes.Add(new ConfiguredIndex(index.Name, Ddl(index, table)));
        }

        List<ConfiguredForeignKey> foreignKeys = [];
        foreach (ForeignKey key in documentType.ForeignKeys)
        {
            foreignKeys.Add(new ConfiguredForeignKey(
                key.Name,
                string.Join(", ", key.ColumnNames ?? []),
                key.LinkedTable?.QualifiedName ?? "unknown",
                string.Join(", ", key.LinkedNames ?? []),
                key.OnDelete.ToString()));
        }

        List<ConfiguredSubClass> subClasses = [];
        foreach (SubClassMapping subClass in documentType.SubClasses)
        {
            subClasses.Add(new ConfiguredSubClass(subClass.Alias, TypeName(subClass.DocumentType)));
        }

        // The metadata column set comes from DocumentTableInfo rather than from Metadata.X.Enabled,
        // because four of the columns are structural: tenant_id exists iff the type is conjoined,
        // mt_doc_type iff it is a hierarchy, and the soft-delete pair iff the delete style is soft -
        // and Metadata.IsSoftDeleted.Enabled reports true even for a type that has no mt_deleted column.
        List<string> metadataColumns = [];
        foreach (DocumentMetadataColumnInfo column in DocumentTableInfo.FromDocumentType(documentType).MetadataColumns)
        {
            metadataColumns.Add(column.ColumnName);
        }

        return new DocumentTypeConfiguration(
            documentType.Alias,
            documentType.DocumentType.Name,
            documentType.DocumentType.FullName ?? documentType.DocumentType.Name,
            documentType.TableName.QualifiedName,
            values,
            duplicated,
            indexes,
            foreignKeys,
            subClasses,
            metadataColumns);
    }

    /// <summary>
    /// The delete style, read from the concrete mapping.
    /// </summary>
    /// <remarks>
    /// <c>IDocumentType</c> does not carry <c>DeleteStyle</c> (Appendix B, U2) and the interface that
    /// does is internal to Marten - but every <c>IDocumentType</c> Marten hands back is a public
    /// <c>Marten.Schema.DocumentMapping</c>, which does. <c>Metadata.IsSoftDeleted.Enabled</c> is not a
    /// substitute: it is true on types that are not soft-deleted at all.
    /// </remarks>
    private static string DeleteStyleOf(IDocumentType documentType) => documentType is DocumentMapping mapping
        ? mapping.DeleteStyle.ToString()
        : "unknown";

    private static bool NumericRevisions(IDocumentType documentType) =>
        documentType is DocumentMapping mapping ? mapping.UseNumericRevisions : documentType.Metadata.Revision.Enabled;

    private static string Ddl(IndexDefinition index, Table table)
    {
        try
        {
            return index.ToDDL(table);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return index.Name;
        }
    }

    private static string TypeName(Type? type) => type?.Name ?? "unknown";

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan value) => value.ToString("g", CultureInfo.InvariantCulture);
}
