using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Integration.Tests.Sql;

/// <summary>
/// The one test class that asks Marten to build the tables and then asks the studio to describe them.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this suite runs against tables the tests wrote by hand, which means every one of
/// them agrees with the studio's idea of a Marten table by construction. That is exactly the failure
/// mode. <see cref="DocumentTableInfo.FromDocumentType"/> encodes which of Marten's optional columns are
/// <em>structural</em> — <c>tenant_id</c> only when conjoined, <c>mt_doc_type</c> only for a hierarchy,
/// <c>mt_deleted</c> only when the delete style is soft — and that is a claim about Marten's schema
/// generator, not about anything in this repository. A hand-written <c>create table</c> cannot falsify
/// it; only Marten can.
/// </para>
/// <para>
/// So this class configures real stores, calls <c>ApplyAllConfiguredChangesToDatabaseAsync</c>, and then
/// asserts set equality between the columns the studio names and the columns
/// <c>information_schema.columns</c> reports. Both directions matter: a column the studio names and the
/// table lacks is a select list that fails on every page render, and a column the table has and the
/// studio does not name is a feature silently missing from the UI.
/// </para>
/// <para>
/// <c>DisableInformationalFields()</c> is a <em>store</em> policy rather than a per-document one, so it
/// gets a store and a schema of its own; so does each of the two event-store shapes.
/// </para>
/// </remarks>
public class MartenSchemaLiveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private const string LeanSuffix = "_lean";
    private const string RichEventsSuffix = "_events";
    private const string BareEventsSuffix = "_events_bare";

    /// <summary>
    /// The two columns every Marten document table has, which the studio treats as constants rather than
    /// as metadata — so they have to be added back before the sets can be compared.
    /// </summary>
    private static readonly string[] AlwaysPresent = [DocumentTableInfo.IdColumn, DocumentTableInfo.DataColumn];

    private static readonly Dictionary<string, Type> DocumentTypes = new(StringComparer.Ordinal)
    {
        [nameof(SchemaPlainThing)] = typeof(SchemaPlainThing),
        [nameof(SchemaSoftDeletedThing)] = typeof(SchemaSoftDeletedThing),
        [nameof(SchemaTenantedThing)] = typeof(SchemaTenantedThing),
        [nameof(SchemaAnimal)] = typeof(SchemaAnimal),
        [nameof(SchemaRevisionedThing)] = typeof(SchemaRevisionedThing),
    };

    private DocumentStore? store;
    private DocumentStore? leanStore;
    private DocumentStore? richEventStore;
    private DocumentStore? bareEventStore;

    /// <summary>
    /// The five document types and the Postgres type each one's <c>id</c> column really has. The verdict
    /// travels as a name because a <c>[MemberData]</c> row must be public and every type in this area of
    /// the studio is internal (D12).
    /// </summary>
    public static TheoryData<string, string> ExpectedIdColumnTypes => new()
    {
        { nameof(SchemaPlainThing), nameof(DocumentIdColumnType.Uuid) },
        { nameof(SchemaSoftDeletedThing), nameof(DocumentIdColumnType.Uuid) },
        { nameof(SchemaTenantedThing), nameof(DocumentIdColumnType.Int4) },
        { nameof(SchemaAnimal), nameof(DocumentIdColumnType.Uuid) },
        { nameof(SchemaRevisionedThing), nameof(DocumentIdColumnType.Varchar) },
    };

    [PostgresFact]
    public async Task Every_configured_document_type_is_described_exactly_as_Marten_built_it()
    {
        await using var connection = await OpenAsync();

        foreach (var documentType in DocumentTypes.Values)
        {
            var mapping = MappingFor(Store, documentType);
            var physical = await new ColumnCatalog().GetAsync(
                connection, mapping.TableName.Schema, mapping.TableName.Name);

            physical.Exists.Should().BeTrue($"Marten should have created {mapping.TableName.QualifiedName}");

            var described = DocumentTableInfo.FromDocumentType(mapping).WithPhysicalColumns(physical);

            NamedColumns(described).Should().BeEquivalentTo(
                physical.Columns.Select(x => x.Name),
                $"the studio must name every column Marten put on {mapping.TableName.Name}, and no others");
        }
    }

    [PostgresTheory]
    [MemberData(nameof(ExpectedIdColumnTypes))]
    public async Task The_id_column_type_is_the_one_the_table_actually_has(string typeName, string expected)
    {
        await using var connection = await OpenAsync();

        var mapping = MappingFor(Store, DocumentTypes[typeName]);
        var physical = await new ColumnCatalog().GetAsync(
            connection, mapping.TableName.Schema, mapping.TableName.Name);

        var described = DocumentTableInfo.FromDocumentType(mapping).WithPhysicalColumns(physical);

        described.IdColumnType.ToString().Should().Be(expected);
        DocumentIdColumnTypes.FromUdtName(physical.Find(DocumentTableInfo.IdColumn)!.UdtName)
            .Should().Be(described.IdColumnType, "the column's own type is what the id parameter is bound as");
    }

    /// <summary>
    /// The structural columns, one claim at a time, so a failure names which of Marten's rules moved
    /// rather than only that a set did not match.
    /// </summary>
    [PostgresFact]
    public async Task The_structural_columns_appear_exactly_where_Marten_puts_them()
    {
        await using var connection = await OpenAsync();

        var plain = await DescribeAsync(Store, connection, typeof(SchemaPlainThing));
        var soft = await DescribeAsync(Store, connection, typeof(SchemaSoftDeletedThing));
        var tenanted = await DescribeAsync(Store, connection, typeof(SchemaTenantedThing));
        var hierarchy = await DescribeAsync(Store, connection, typeof(SchemaAnimal));
        var revisioned = await DescribeAsync(Store, connection, typeof(SchemaRevisionedThing));

        plain.HasMetadata(DocumentMetadataColumn.IsSoftDeleted).Should().BeFalse();
        plain.HasMetadata(DocumentMetadataColumn.TenantId).Should().BeFalse();
        plain.HasMetadata(DocumentMetadataColumn.DocumentType).Should().BeFalse();
        plain.TenancyStyle.Should().Be(TenancyStyle.Single);
        plain.DuplicatedColumns.Single().ColumnName.Should().Be("name");

        soft.SoftDeleteEnabled.Should().BeTrue();
        soft.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted).Should().Be("mt_deleted");
        soft.MetadataColumnName(DocumentMetadataColumn.SoftDeletedAt).Should().Be("mt_deleted_at");

        tenanted.TenancyStyle.Should().Be(TenancyStyle.Conjoined);
        tenanted.MetadataColumnName(DocumentMetadataColumn.TenantId).Should().Be("tenant_id");

        hierarchy.MetadataColumnName(DocumentMetadataColumn.DocumentType).Should().Be("mt_doc_type");

        // The revision lives in Marten's mt_version column, and its width is read from the table rather
        // than guessed: the default RevisionColumn is bigint, and only an IRevisioned document gets int4.
        var revision = revisioned.MetadataColumns.Single(x => x.Column == DocumentMetadataColumn.Revision);

        revision.ColumnName.Should().Be("mt_version");
        revision.PhysicalDbType.Should().NotBeNull("WithPhysicalColumns read the column's real type");
        revision.DbType.Should().Be(NpgsqlTypes.NpgsqlDbType.Bigint);
    }

    /// <summary>
    /// The other half of the description: the query built from it has to be SQL the database accepts.
    /// A column named that is not there fails here and nowhere earlier.
    /// </summary>
    [PostgresFact]
    public async Task The_list_query_built_from_each_description_runs_against_the_table_Marten_made()
    {
        await using var connection = await OpenAsync();

        List<(IDocumentStore Store, Type Type)> cases =
        [
            .. DocumentTypes.Values.Select(x => (Store, x)),
            (LeanStore, typeof(SchemaLeanThing)),
        ];

        foreach (var (which, documentType) in cases)
        {
            var described = await DescribeAsync(which, connection, documentType);

            using var command = DocumentQueryBuilder.BuildList(described, new DocumentListQuery());

            command.Connection = connection;

            await using var reader = await command.ExecuteReaderAsync();

            reader.HasRows.Should().BeFalse($"{documentType.Name}'s table is empty, but its query is valid SQL");
        }
    }

    /// <summary>
    /// <c>DisableInformationalFields()</c> is a store policy, so it needs a store of its own. The table it
    /// produces is <c>id</c> and <c>data</c> and nothing else, which is the shape a select list written
    /// from Marten's defaults fails against on every render (AGENTS.md hard rule 10).
    /// </summary>
    [PostgresFact]
    public async Task A_store_with_no_informational_fields_has_a_table_of_id_and_data()
    {
        await using var connection = await OpenAsync();

        var mapping = MappingFor(LeanStore, typeof(SchemaLeanThing));
        var physical = await new ColumnCatalog().GetAsync(
            connection, mapping.TableName.Schema, mapping.TableName.Name);

        physical.Columns.Select(x => x.Name).Should().BeEquivalentTo(AlwaysPresent);

        var described = DocumentTableInfo.FromDocumentType(mapping).WithPhysicalColumns(physical);

        described.MetadataColumns.Should().BeEmpty();
        NamedColumns(described).Should().BeEquivalentTo(physical.Columns.Select(x => x.Name));
    }

    /// <summary>
    /// The event tables, in the two shapes U8 is about: every optional metadata column on, and none of
    /// them. <see cref="EventTableInfo"/> is built from <see cref="ColumnCatalog"/>, so this also checks
    /// the catalog's read against a plain <c>information_schema</c> query that goes nowhere near it.
    /// </summary>
    [PostgresFact]
    public async Task The_event_tables_are_described_exactly_as_Marten_built_them()
    {
        await using var connection = await OpenAsync();

        var catalog = new ColumnCatalog();

        var rich = await DescribeEventsAsync(catalog, connection, Schema + RichEventsSuffix);
        var bare = await DescribeEventsAsync(catalog, connection, Schema + BareEventsSuffix);

        foreach (var table in new[] { rich, bare })
        {
            table.EventColumns.Should().NotBeEmpty($"Marten should have created {table.QualifiedEvents}");
            table.EventColumns.Should().BeEquivalentTo(
                await ReadColumnNamesAsync(connection, table.Schema, EventTableInfo.EventsTable));
            table.StreamColumns.Should().BeEquivalentTo(
                await ReadColumnNamesAsync(connection, table.Schema, EventTableInfo.StreamsTable));
        }

        rich.HasCorrelationId.Should().BeTrue();
        rich.HasCausationId.Should().BeTrue();
        rich.HasHeaders.Should().BeTrue();
        rich.HasUserName.Should().BeTrue();

        bare.HasCorrelationId.Should().BeFalse("MetadataConfig leaves them off by default");
        bare.HasCausationId.Should().BeFalse();
        bare.HasHeaders.Should().BeFalse();
        bare.HasUserName.Should().BeFalse();

        // mt_streams."timestamp" is NOT NULL, which is what the streams keyset and the recently-active
        // panel both depend on.
        var streamTimestamp = (await catalog.GetAsync(
            connection, rich.Schema, EventTableInfo.StreamsTable)).Find("timestamp");

        streamTimestamp.Should().NotBeNull();
        streamTimestamp!.IsNullable.Should().BeFalse();

        // And the queries the builder writes from that description are SQL the database accepts, in both
        // shapes - which is the claim a hand-made table cannot make.
        foreach (var table in new[] { rich, bare })
        {
            using var feed = EventQueryBuilder.BuildFeed(table, new EventFeedQuery());
            using var active = EventQueryBuilder.BuildRecentlyActiveStreams(table, 5);

            foreach (var command in new[] { feed, active })
            {
                command.Connection = connection;

                await using var reader = await command.ExecuteReaderAsync();

                reader.HasRows.Should().BeFalse($"{table.Schema} is empty, but the statement is valid");
            }
        }
    }

    /// <summary>Builds the four stores and lets Marten create every table they describe.</summary>
    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        // PostgresTestBase drops and recreates this class's own schema; the three extra ones are this
        // class's to clean, and a reused container can be carrying a previous run's shape of them.
        foreach (var suffix in new[] { LeanSuffix, RichEventsSuffix, BareEventsSuffix })
        {
            await ExecuteAsync(
                connection, $"drop schema if exists {SqlIdentifier.Quote(Schema + suffix)} cascade");
        }

        store = BuildStore();
        leanStore = BuildLeanStore();
        richEventStore = BuildEventStore(RichEventsSuffix, metadata: true);
        bareEventStore = BuildEventStore(BareEventsSuffix, metadata: false);

        foreach (var built in new[] { store, leanStore, richEventStore, bareEventStore })
        {
            await built.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }
    }

    /// <summary>Disposes the stores; the schemas are dropped by the next run that uses these names.</summary>
    public override async ValueTask DisposeAsync()
    {
        foreach (var built in new[] { store, leanStore, richEventStore, bareEventStore })
        {
            if (built is not null)
            {
                await built.DisposeAsync();
            }
        }

        GC.SuppressFinalize(this);

        await base.DisposeAsync();
    }

    private DocumentStore Store => store ?? throw new InvalidOperationException("The store was never built.");

    private DocumentStore LeanStore =>
        leanStore ?? throw new InvalidOperationException("The lean store was never built.");

    /// <summary>
    /// One document type's mapping. Taken through <see cref="IDocumentStore"/> rather than off the
    /// concrete store, because <c>DocumentStore.Options</c> is the mutable <c>StoreOptions</c> and
    /// <c>FindOrResolveDocumentType</c> lives on the read-only interface it implements.
    /// </summary>
    private static IDocumentType MappingFor(IDocumentStore store, Type documentType) =>
        store.Options.FindOrResolveDocumentType(documentType);

    private static IEnumerable<string> NamedColumns(DocumentTableInfo table) =>
        AlwaysPresent
            .Concat(table.MetadataColumns.Select(x => x.ColumnName))
            .Concat(table.DuplicatedColumns.Select(x => x.ColumnName));

    private static async Task<EventTableInfo> DescribeEventsAsync(
        ColumnCatalog catalog,
        NpgsqlConnection connection,
        string schema) =>
        EventTableInfo.FromColumns(
            schema,
            StreamIdentity.AsGuid,
            await catalog.GetAsync(connection, schema, EventTableInfo.EventsTable),
            await catalog.GetAsync(connection, schema, EventTableInfo.StreamsTable));

    private static async Task<List<string>> ReadColumnNamesAsync(
        NpgsqlConnection connection,
        string schema,
        string table)
    {
        // Deliberately not through ColumnCatalog: this is the independent reading the catalog is compared
        // against, so it must not be the catalog's own answer coming back.
        await using var command = new NpgsqlCommand(
            "select column_name from information_schema.columns " +
            "where table_schema = @schema and table_name = @table",
            connection);

        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", table);

        List<string> names = [];

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<DocumentTableInfo> DescribeAsync(
        IDocumentStore store,
        NpgsqlConnection connection,
        Type documentType)
    {
        var mapping = MappingFor(store, documentType);

        var physical = await new ColumnCatalog().GetAsync(
            connection, mapping.TableName.Schema, mapping.TableName.Name);

        return DocumentTableInfo.FromDocumentType(mapping).WithPhysicalColumns(physical);
    }

    private DocumentStore BuildStore() => DocumentStore.For(options =>
    {
        options.Connection(Fixture.ConnectionString);
        options.DatabaseSchemaName = Schema;
        options.AutoCreateSchemaObjects = AutoCreate.All;

        options.Schema.For<SchemaPlainThing>().Duplicate(x => x.Name);
        options.Schema.For<SchemaSoftDeletedThing>().SoftDeleted();
        options.Schema.For<SchemaTenantedThing>().MultiTenanted();
        options.Schema.For<SchemaAnimal>().AddSubClass<SchemaDog>();
        options.Schema.For<SchemaRevisionedThing>().UseNumericRevisions(true);
    });

    private DocumentStore BuildLeanStore() => DocumentStore.For(options =>
    {
        options.Connection(Fixture.ConnectionString);
        options.DatabaseSchemaName = Schema + LeanSuffix;
        options.AutoCreateSchemaObjects = AutoCreate.All;

        // Store-wide, which is why this needs a store of its own rather than a sixth mapping above.
        options.Policies.DisableInformationalFields();

        options.Schema.For<SchemaLeanThing>();
    });

    private DocumentStore BuildEventStore(string schemaSuffix, bool metadata) => DocumentStore.For(options =>
    {
        options.Connection(Fixture.ConnectionString);
        options.DatabaseSchemaName = Schema + schemaSuffix;
        options.Events.DatabaseSchemaName = Schema + schemaSuffix;
        options.AutoCreateSchemaObjects = AutoCreate.All;

        // An event type is what makes the event store part of the schema Marten will build.
        options.Events.AddEventType<SchemaThingHappened>();

        if (metadata)
        {
            // Set one by one rather than through EnableAll(): each flag is a column this test asserts on,
            // and EnableAll() leaves UserNameEnabled alone (verified against Marten 9.35), which is exactly
            // the kind of thing a test that said "enable all" would have hidden.
            options.Events.MetadataConfig.CausationIdEnabled = true;
            options.Events.MetadataConfig.CorrelationIdEnabled = true;
            options.Events.MetadataConfig.HeadersEnabled = true;
            options.Events.MetadataConfig.UserNameEnabled = true;
        }
    });
}

/// <summary>A document with no configuration but a duplicated field, so the plainest table has one.</summary>
public sealed class SchemaPlainThing
{
    /// <summary>A Guid id, which is the column type Marten defaults to.</summary>
    public Guid Id { get; set; }

    /// <summary>Duplicated into its own column.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>Soft-deleted, which is what puts <c>mt_deleted</c> and <c>mt_deleted_at</c> on the table.</summary>
public sealed class SchemaSoftDeletedThing
{
    /// <summary>A Guid id.</summary>
    public Guid Id { get; set; }

    /// <summary>Only there so the document has a shape.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>Conjoined-tenanted, with an int id so the id-column theory has more than one answer.</summary>
public sealed class SchemaTenantedThing
{
    /// <summary>An int id, which Marten assigns from a HiLo sequence.</summary>
    public int Id { get; set; }

    /// <summary>Only there so the document has a shape.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>The root of a hierarchy, which is what puts <c>mt_doc_type</c> on the table.</summary>
public class SchemaAnimal
{
    /// <summary>A Guid id.</summary>
    public Guid Id { get; set; }

    /// <summary>Only there so the document has a shape.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>A subclass, so <see cref="SchemaAnimal"/> is a hierarchy.</summary>
public sealed class SchemaDog : SchemaAnimal;

/// <summary>Numeric revisions, with a string id.</summary>
public sealed class SchemaRevisionedThing
{
    /// <summary>A string id, which Marten stores in a varchar column.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Only there so the document has a shape.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>The document type the metadata-less store holds: its table is <c>id</c> and <c>data</c>.</summary>
public sealed class SchemaLeanThing
{
    /// <summary>A Guid id.</summary>
    public Guid Id { get; set; }

    /// <summary>Only there so the document has a shape.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>One event type, which is what makes Marten build the event tables at all.</summary>
/// <param name="What">Anything; the payload is never read here.</param>
public sealed record SchemaThingHappened(string What);
