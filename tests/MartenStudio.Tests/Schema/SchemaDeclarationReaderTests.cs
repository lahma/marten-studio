using Marten;

using MartenStudio.Services.Schema;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// What the Schema screen's navigation paths know, read from <c>StoreOptions</c> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <c>DocumentStore.For</c> never opens a connection, so this runs against Marten's real mappings without
/// a Postgres — which is the point, because the thing being asserted is that none of it needs one.
/// Every method here used to be <c>IMartenDatabase.AllSchemaNames()</c> or <c>AllObjects()</c>, and both
/// of those build Marten's feature schemas, reach the lazy HiLo <c>Sequences</c> feature and apply a
/// migration on the spot (<c>SchemaNoDdlLiveTests</c> holds Marten to that, live).
/// </para>
/// <para>
/// The structural index names are reconstructed with Marten's own <c>DocumentIndex</c>, so they are
/// whatever Marten would name them rather than whatever this code thinks it would; the live suite's
/// Indexes tests are what prove the two agree against a real schema.
/// </para>
/// </remarks>
public class SchemaDeclarationReaderTests
{
    [Fact]
    public void The_schema_list_is_the_document_schema_the_event_schema_and_every_types_own()
    {
        SchemaDeclarations declarations = Read(options =>
        {
            options.Events.DatabaseSchemaName = "studio_events";
            options.Schema.For<SqlTestCustomer>();
            options.Schema.For<SqlTestNote>().DatabaseSchemaName("elsewhere");
        });

        declarations.Schemas.Should().Contain(SqlTestStore.Schema);
        declarations.Schemas.Should().Contain("studio_events", "the event store's tables are in the store's schemas too");
        declarations.Schemas.Should().Contain("elsewhere");
        declarations.Schemas.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// P7-fix follow-up 5: the old fallback returned only <c>DatabaseSchemaName</c>, so a store whose
    /// mappings would not build lost its event schema from every catalog read.
    /// </summary>
    [Fact]
    public void The_schema_names_helper_always_includes_the_event_schema()
    {
        IReadOnlyStoreOptions options = Options(o => o.Events.DatabaseSchemaName = "studio_events");

        SchemaDeclarationReader.SchemaNames(options).Should()
            .Contain(SqlTestStore.Schema).And.Contain("studio_events");
    }

    [Fact]
    public void A_declared_index_is_read_from_the_document_type_rather_than_from_the_database()
    {
        SchemaDeclarations declarations = Read(options =>
            options.Schema.For<SqlTestCustomer>().Index(x => x.Name));

        declarations.Indexes.Should().Contain(x =>
            x.Table == "mt_doc_sqltestcustomer" && x.Name.Contains("name", StringComparison.OrdinalIgnoreCase));

        declarations.Indexes.Should().OnlyContain(x => x.Definition.Length > 0);
        declarations.ManagedTables.Should().Contain(SqlTestStore.Schema + ".mt_doc_sqltestcustomer");
    }

    /// <summary>
    /// The three indexes Marten adds to the <c>DocumentTable</c> rather than to the mapping. They do not
    /// appear in <c>IDocumentType.Indexes</c>, and an Indexes tab that called them undeclared would be
    /// telling somebody Marten is about to drop its own soft-delete index.
    /// </summary>
    [Fact]
    public void The_structural_indexes_a_delete_style_and_a_hierarchy_imply_are_declared_too()
    {
        SchemaDeclarations declarations = Read(options =>
        {
            options.Schema.For<SqlTestNote>().SoftDeleted();
            options.Schema.For<SqlTestCustomer>().AddSubClass<SqlTestVipCustomer>();
        });

        declarations.Indexes.Select(x => x.Name).Should()
            .Contain(x => x.Contains("mt_deleted", StringComparison.Ordinal))
            .And.Contain(x => x.Contains("mt_doc_type", StringComparison.Ordinal));
    }

    [Fact]
    public void An_index_the_host_ignored_is_recorded_as_ignored()
    {
        SchemaDeclarations declarations = Read(options =>
            options.Schema.For<SqlTestCustomer>().IgnoreIndex("hand_rolled_idx"));

        declarations.IgnoredIndexes.Should()
            .Contain(SqlTestStore.Schema + ".mt_doc_sqltestcustomer.hand_rolled_idx");
    }

    [Fact]
    public void The_event_stores_tables_and_functions_are_declared_without_touching_a_database()
    {
        SchemaDeclarations declarations = Read(options =>
        {
            options.Events.DatabaseSchemaName = "studio_events";
            options.Events.AddEventType<SchemaDeclarationEvent>();
        });

        declarations.ManagedTables.Should()
            .Contain("studio_events.mt_events").And.Contain("studio_events.mt_streams");

        declarations.Functions.Should()
            .Contain("studio_events.mt_quick_append_events")
            .And.Contain("studio_events.mt_archive_stream")
            .And.Contain("studio_events.mt_mark_event_progression");

        declarations.Indexes.Should().Contain(x => x.Schema == "studio_events");
    }

    /// <summary>
    /// The one part that cannot be read from a public API: <c>StorageFeatures.SystemFunctions</c> and the
    /// HiLo <c>SequenceFactory</c> are both internal to Marten, so these are a list of names.
    /// <c>SchemaLiveTests</c> is what holds the list to a real schema.
    /// </summary>
    [Fact]
    public void Martens_document_schema_helper_functions_are_declared_by_name()
    {
        SchemaDeclarations declarations = Read();

        foreach (string name in SchemaDeclarationReader.DocumentSchemaFunctions)
        {
            declarations.Functions.Should().Contain(SqlTestStore.Schema + "." + name);
        }

        SchemaDeclarationReader.DocumentSchemaFunctions.Should()
            .Contain("mt_get_next_hi", "the HiLo function is the one a read path used to create")
            .And.Contain("mt_jsonb_patch")
            .And.Contain("mt_safe_unaccent");
    }

    [Fact]
    public void A_hidden_document_type_still_contributes_its_schema()
    {
        SchemaDeclarations declarations = SchemaDeclarationReader.Read(
            Options(options => options.Schema.For<SqlTestNote>().DatabaseSchemaName("elsewhere")),
            static type => type != typeof(SqlTestNote));

        declarations.Schemas.Should().Contain("elsewhere",
            "a hidden type's table still occupies the schema, and a Tables tab that omitted it would be " +
            "lying about disk");
        declarations.ManagedTables.Should().Contain("elsewhere.mt_doc_sqltestnote");
    }

    private static SchemaDeclarations Read(Action<StoreOptions>? configure = null) =>
        SchemaDeclarationReader.Read(Options(configure));

    /// <summary>
    /// The read-only options of a store built against an unreachable host.
    /// </summary>
    /// <remarks>
    /// Reached through <c>IDocumentStore</c> rather than the concrete <c>DocumentStore</c>: the concrete
    /// type's <c>Options</c> is the mutable <c>StoreOptions</c>, which hides the read-only interface the
    /// studio is written against.
    /// </remarks>
    private static IReadOnlyStoreOptions Options(Action<StoreOptions>? configure = null)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            configure?.Invoke(options);
        });

        return store.Options;
    }
}

/// <summary>One event type, so the event store is configured.</summary>
/// <param name="Id">What it happened to.</param>
public record SchemaDeclarationEvent(Guid Id);
