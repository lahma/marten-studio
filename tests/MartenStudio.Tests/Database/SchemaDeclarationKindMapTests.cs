using Marten;

using MartenStudio.Services.Schema;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The per-object kind map <c>SchemaDeclarationReader</c> grew for the database browser, still from
/// <c>StoreOptions</c> alone - and the fail-closed entry point beside the forgiving one.
/// </summary>
public class SchemaDeclarationKindMapTests
{
    [Fact]
    public void Every_kind_is_mapped_from_the_options_alone()
    {
        SchemaDeclarations declarations = SchemaDeclarationReader.Read(DefaultStore(), IsVisible);

        declarations.ObjectsFailure.Should().BeNull();

        Kind(declarations, DocumentSchema, "mt_doc_dbcustomer").Should().Be(new MartenDeclaredObject(
            MartenObjectKind.DocumentTable, "dbcustomer", Visible: true));

        Kind(declarations, DocumentSchema, "mt_doc_dbsecret").Should().Be(new MartenDeclaredObject(
            MartenObjectKind.DocumentTable, Alias: null, Visible: false),
            "a hidden type's alias is exactly what it must not show");

        Kind(declarations, EventSchema, "mt_events")!.Kind.Should().Be(MartenObjectKind.EventTable);
        Kind(declarations, EventSchema, "mt_streams")!.Kind.Should().Be(MartenObjectKind.EventTable);
        Kind(declarations, EventSchema, "mt_event_progression")!.Kind.Should().Be(MartenObjectKind.EventTable);
        Kind(declarations, EventSchema, "mt_quick_append_events")!.Kind.Should().Be(MartenObjectKind.Function);

        Kind(declarations, ReportingSchema, "flat_orders")!.Kind.Should().Be(MartenObjectKind.ProjectionOrExtendedTable);
        Kind(declarations, ReportingSchema, "ef_things")!.Kind.Should().Be(MartenObjectKind.ProjectionOrExtendedTable);
        Kind(declarations, ReportingSchema, "ext_touch")!.Kind.Should().Be(MartenObjectKind.Function);
        Kind(declarations, ReportingSchema, "ext_numbers")!.Kind.Should().Be(MartenObjectKind.Sequence);

        Kind(declarations, DocumentSchema, "mt_jsonb_patch")!.Kind.Should().Be(MartenObjectKind.Function);
        Kind(declarations, DocumentSchema, "mt_hilo")!.Kind.Should().Be(MartenObjectKind.Infrastructure);
    }

    /// <summary>
    /// The feature schemas' <c>Sequence</c> objects, which the Schema screen has never listed, are in the
    /// map now: <c>mt_events_sequence</c> is Marten's.
    /// </summary>
    [Fact]
    public void The_event_stores_sequence_is_in_the_map()
    {
        SchemaDeclarations declarations = SchemaDeclarationReader.Read(DefaultStore(), IsVisible);

        Kind(declarations, EventSchema, "mt_events_sequence")!.Kind.Should().Be(MartenObjectKind.Sequence);
    }

    [Fact]
    public void The_flat_tables_upsert_function_is_declared_too()
    {
        SchemaDeclarations declarations = SchemaDeclarationReader.Read(DefaultStore(), IsVisible);

        declarations.Objects.Keys.Should().Contain(
            x => x.StartsWith(ReportingSchema + ".mt_upsert_flat_orders", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An <c>ExtendedSchemaObjects</c> table is a managed table - Marten yields the list as a feature of its
    /// own, so an apply migrates it, drop index included (DB-7) - and a sequence is still not a function.
    /// </summary>
    [Fact]
    public void An_extended_table_is_managed_and_a_sequence_is_not_a_function()
    {
        SchemaDeclarations declarations = SchemaDeclarationReader.Read(DefaultStore(), IsVisible);

        declarations.ManagedTables.Should().Contain(ReportingSchema + ".ef_things",
            "an apply migrates ExtendedSchemaObjects like Marten's own tables");
        declarations.Functions.Should().NotContain(ReportingSchema + ".ext_touch");
        declarations.Functions.Should().NotContain(EventSchema + ".mt_events_sequence");
        declarations.ManagedTables.Should().Contain(EventSchema + ".mt_events");
    }

    [Fact]
    public void The_classification_read_succeeds_with_the_same_map()
    {
        SchemaDeclarationRead read = SchemaDeclarationReader.ReadForClassification(DefaultStore(), IsVisible);

        read.Succeeded.Should().BeTrue(read.Failure);
        read.Failure.Should().BeNull();
        read.Declarations!.Objects.Should().ContainKey(DocumentSchema + ".mt_doc_dbcustomer");
    }

    /// <summary>
    /// Acceptance 4, fail closed: a configuration that throws while it is being read is a failure, never
    /// an empty map - an empty map would call every Marten table "Other".
    /// </summary>
    [Fact]
    public void A_declaration_read_that_throws_fails_closed_rather_than_answering_an_empty_map()
    {
        IReadOnlyStoreOptions options = DefaultStore();

        SchemaDeclarationRead read = SchemaDeclarationReader.ReadForClassification(
            options,
            static _ => throw new InvalidOperationException("the visibility delegate blew up"));

        read.Succeeded.Should().BeFalse();
        read.Declarations.Should().BeNull();
        read.Failure.Should().Contain("the visibility delegate blew up");

        // The forgiving helper the Schema screen uses keeps its own fallback, unchanged.
        SchemaDeclarationReader.SchemaNames(options).Should().Contain(DocumentSchema);
    }

    private static MartenDeclaredObject? Kind(SchemaDeclarations declarations, string schema, string name) =>
        declarations.Objects.GetValueOrDefault(SchemaKey.For(schema, name));
}
