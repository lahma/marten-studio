using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Projections.Flattened;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Sql;

using Weasel.Postgresql;
using Weasel.Postgresql.Functions;
using Weasel.Postgresql.Tables;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Real Marten configurations for the database browser's unit tests, built against a host that never
/// resolves - the same trick as <see cref="SqlTestStore" />: <c>DocumentStore.For</c> opens no connection,
/// so the declarations are whatever Marten really declares.
/// </summary>
internal static class DatabaseTestStores
{
    /// <summary>The default store's document schema.</summary>
    public const string DocumentSchema = "studio_db";

    /// <summary>The default store's event schema.</summary>
    public const string EventSchema = "studio_db_events";

    /// <summary>Where the flat-table projection and the extended objects live.</summary>
    public const string ReportingSchema = "reporting";

    /// <summary>The ancillary store's document schema.</summary>
    public const string OtherSchema = "other_db";

    /// <summary>The ancillary store's registration key.</summary>
    public const string OtherStoreKey = "IOtherDbStore";

    /// <summary>
    /// The default store: a visible and a hidden document type, an event store, a flat-table projection
    /// in <see cref="ReportingSchema" />, and a table, a function and a sequence handed to
    /// <c>ExtendedSchemaObjects</c>.
    /// </summary>
    public static IReadOnlyStoreOptions DefaultStore(Action<StoreOptions>? configure = null)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = DocumentSchema;
            options.Events.DatabaseSchemaName = EventSchema;

            options.Schema.For<DbCustomer>();
            options.Schema.For<DbSecret>();
            options.Events.AddEventType<DbOrderPlaced>();
            options.Projections.Add<DbOrderFlatProjection>(ProjectionLifecycle.Inline);

            options.Storage.ExtendedSchemaObjects.Add(new Table(Name(ReportingSchema, "ef_things")));
            options.Storage.ExtendedSchemaObjects.Add(new Function(
                Name(ReportingSchema, "ext_touch", SchemaUtils.IdentifierUsage.Function),
                "create or replace function reporting.ext_touch() returns int language sql as $$ select 1 $$;"));
            options.Storage.ExtendedSchemaObjects.Add(new Sequence(Name(ReportingSchema, "ext_numbers")));

            configure?.Invoke(options);
        });

        return store.Options;
    }

    /// <summary>The other store's Marten-managed relational table, whose rows are that store's data.</summary>
    public const string OtherReportTable = "other_report";

    /// <summary>
    /// A second store, with a document table of its own in its own schema and a table handed to its
    /// <c>ExtendedSchemaObjects</c> - Marten-managed, and relational.
    /// </summary>
    public static IReadOnlyStoreOptions OtherStore()
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = OtherSchema;
            options.Schema.For<DbInvoice>();
            options.Storage.ExtendedSchemaObjects.Add(new Table(Name(OtherSchema, OtherReportTable)));
        });

        return store.Options;
    }

    /// <summary>A Postgres object name, the way Weasel wants one built.</summary>
    public static PostgresqlObjectName Name(
        string schema,
        string name,
        SchemaUtils.IdentifierUsage usage = SchemaUtils.IdentifierUsage.General) =>
        new(schema, name, usage);

    /// <summary>What <c>IsDocumentTypeVisible</c> says in these tests: everything but <see cref="DbSecret" />.</summary>
    public static bool IsVisible(Type type) => type != typeof(DbSecret);

    /// <summary>A classifier over both stores, the default one first, with <see cref="DbSecret" /> hidden.</summary>
    public static DatabaseObjectClassifier Classifier() => Classifier(IsVisible);

    /// <summary>
    /// A classifier over both stores, the default one first, with what <paramref name="isVisible" /> says
    /// hidden - and, when it is <see langword="null" />, nothing hidden and no hiding configured at all.
    /// </summary>
    public static DatabaseObjectClassifier Classifier(Func<Type, bool>? isVisible)
    {
        SchemaDeclarationRead first = SchemaDeclarationReader.ReadForClassification(DefaultStore(), isVisible);
        SchemaDeclarationRead second = SchemaDeclarationReader.ReadForClassification(OtherStore(), isVisible);

        first.Succeeded.Should().BeTrue(first.Failure);
        second.Succeeded.Should().BeTrue(second.Failure);

        return new DatabaseObjectClassifier(
            [
                new StoreDeclarations("default", first.Declarations!),
                new StoreDeclarations(OtherStoreKey, second.Declarations!),
            ],
            hidesDocumentTypes: isVisible is not null);
    }

    /// <summary>The store's own schemas, as the reader declares them.</summary>
    public static IReadOnlyList<string> StoreSchemas() =>
        SchemaDeclarationReader.Read(DefaultStore(), IsVisible).Schemas;

    /// <summary>A live schema list with the store's schemas, two browsable ones and one withheld.</summary>
    public static IReadOnlyList<CatalogSchema> LiveSchemas() =>
    [
        new("information_schema", true, false),
        new("pg_catalog", true, false),
        new(DocumentSchema, true, false),
        new(EventSchema, true, false),
        new(ReportingSchema, true, false),
        new("quartz", true, false),
        new("legacy", true, false),
        new("secrets", true, false),
    ];

    /// <summary>A relation row, with sensible defaults for everything a test is not about.</summary>
    public static CatalogRelation Relation(
        string schema,
        string name,
        string kind = "r",
        bool readable = true,
        string? foreignServer = null,
        int partitions = 0) =>
        new(
            Oid: (uint) Math.Abs(HashCode.Combine(schema, name)),
            Schema: schema,
            Name: name,
            Kind: kind,
            EstimatedRows: 10,
            SizeBytes: 8192,
            RowSecurity: false,
            Populated: true,
            Unlogged: false,
            Readable: readable,
            PartitionCount: partitions,
            ForeignServer: foreignServer,
            Comment: null,
            HasPrimaryKey: true);
}

/// <summary>A visible document type.</summary>
public class DbCustomer
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }

    /// <summary>A name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>The document type the host hides.</summary>
public class DbSecret
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }
}

/// <summary>The ancillary store's document type.</summary>
public class DbInvoice
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }
}

/// <summary>The one event the flat table is written from.</summary>
public class DbOrderPlaced
{
    /// <summary>What it cost.</summary>
    public decimal Amount { get; set; }
}

/// <summary>
/// A flat-table projection into <c>reporting.flat_orders</c>: a plain Weasel table the host shaped, plus
/// an <c>mt_upsert_*</c> function per event.
/// </summary>
public class DbOrderFlatProjection : FlatTableProjection
{
    /// <summary>Declares the table and the one mapped event.</summary>
    public DbOrderFlatProjection()
        : base(DatabaseTestStores.Name(DatabaseTestStores.ReportingSchema, "flat_orders"))
    {
        Table.AddColumn<Guid>("id").AsPrimaryKey();
        Table.AddColumn<decimal>("amount");

        Project<DbOrderPlaced>(map => map.Map(x => x.Amount, "amount"));
    }
}
