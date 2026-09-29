using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Database;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// DB-7, acceptance 1: the Tables tab classifies with the database browser's rules - over two real Marten
/// configurations, the same ones the classifier's own tests use - rather than with "it lives in the event
/// schema, so it is the event store's".
/// </summary>
public class SchemaTableAssemblerTests
{
    private static readonly IReadOnlyDictionary<string, string> TypeNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SchemaKey.For(DocumentSchema, "mt_doc_dbcustomer")] = nameof(DbCustomer),
        };

    /// <summary>
    /// The mislabel this packet exists for: every unrecognised table in the event schema used to be badged
    /// "event store", because the old rule was the schema. A Quartz.NET job store living there is somebody
    /// else's, and says what it looks like.
    /// </summary>
    [Fact]
    public void A_Quartz_table_in_the_event_schema_is_not_Marten_with_the_Quartz_hint()
    {
        TableStats table = Single(Row(EventSchema, "qrtz_locks"));

        table.Ownership.Owner.Should().Be(DatabaseObjectOwner.Other);
        table.Ownership.IsMarten.Should().BeFalse();
        table.Ownership.RecognisedAs.Should().Be("Quartz.NET");
        table.IsEventTable.Should().BeFalse("living in the event schema does not make a table the event store's");
        table.CollectionAlias.Should().BeNull();
    }

    [Fact]
    public void The_event_stores_own_table_is_the_event_store_and_this_stores()
    {
        TableStats table = Single(Row(EventSchema, "mt_events"));

        table.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenEventStore);
        table.IsEventTable.Should().BeTrue();
        table.OwnedByThisStore.Should().BeTrue();
    }

    [Fact]
    public void Mt_hilo_is_Marten_infrastructure()
    {
        TableStats table = Single(Row(DocumentSchema, "mt_hilo"));

        table.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        table.IsEventTable.Should().BeFalse();
    }

    [Fact]
    public void A_flat_table_projections_table_is_Marten_managed()
    {
        TableStats table = Single(Row(ReportingSchema, "flat_orders"));

        table.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
        table.Ownership.IsMarten.Should().BeTrue();
    }

    [Fact]
    public void An_ExtendedSchemaObjects_table_is_Marten_managed()
    {
        Single(Row(ReportingSchema, "ef_things")).Ownership.Owner
            .Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
    }

    [Fact]
    public void A_visible_document_table_carries_its_alias_and_type_for_the_Documents_link()
    {
        TableStats table = Single(Row(DocumentSchema, "mt_doc_dbcustomer"));

        table.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenDocument);
        table.CollectionAlias.Should().Be("dbcustomer");
        table.DocumentTypeName.Should().Be(nameof(DbCustomer));
        table.OwnedByThisStore.Should().BeTrue();
    }

    /// <summary>
    /// Another registered store's table in a shared schema is Marten's - never "Other" - but it is not this
    /// store's collection, so there is no alias to link this store's Documents to.
    /// </summary>
    [Fact]
    public void Another_stores_document_table_is_labelled_and_never_linked_into_this_stores_Documents()
    {
        TableStats table = Single(Row(OtherSchema, "mt_doc_dbinvoice"));

        table.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenDocument);
        table.OwnedByThisStore.Should().BeFalse();
        table.CollectionAlias.Should().BeNull();
    }

    [Fact]
    public void A_hidden_document_types_table_is_absent_rather_than_Other()
    {
        IReadOnlyList<TableStats> tables = Assemble(
            [Row(DocumentSchema, "mt_doc_dbsecret"), Row(DocumentSchema, "mt_doc_dbcustomer")],
            partitionCountsShown: true);

        tables.Select(x => x.Table).Should().Equal("mt_doc_dbcustomer");
    }

    [Fact]
    public void The_hosts_own_table_is_not_Marten_with_no_hint()
    {
        TableStats table = Single(Row(DocumentSchema, "host_audit_log"));

        table.Ownership.Owner.Should().Be(DatabaseObjectOwner.Other);
        table.Ownership.RecognisedAs.Should().BeNull();
    }

    /// <summary>
    /// F9 from the DB-1 review: a per-tenant partition count is the number of tenants, so the count is
    /// withheld unless the visitor is past the database browser's gate - and "partitioned" stays true.
    /// </summary>
    [Fact]
    public void A_partition_count_is_withheld_when_the_gate_is_shut_and_shown_when_it_is_open()
    {
        TableStatsRow partitioned = Row(DocumentSchema, "host_measurements", kind: "p", partitions: 3);

        TableStats shut = Assemble([partitioned], partitionCountsShown: false).Single();
        shut.IsPartitioned.Should().BeTrue();
        shut.PartitionCount.Should().BeNull("the number of partitions is the number of tenants");

        TableStats open = Assemble([partitioned], partitionCountsShown: true).Single();
        open.IsPartitioned.Should().BeTrue();
        open.PartitionCount.Should().Be(3);
    }

    [Fact]
    public void An_ordinary_table_has_no_partition_count_whatever_the_gate_says()
    {
        TableStats table = Assemble([Row(DocumentSchema, "host_audit_log")], partitionCountsShown: true).Single();

        table.IsPartitioned.Should().BeFalse();
        table.PartitionCount.Should().BeNull();
    }

    [Fact]
    public void The_withheld_sentence_names_the_gate_that_is_shut()
    {
        SchemaTableAssembler.PartitionCountsWithheld(readOnly: false, capabilityEnabled: true, authorized: true)
            .Should().BeNull();

        SchemaTableAssembler.PartitionCountsWithheld(readOnly: false, capabilityEnabled: false, authorized: null)
            .Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase")
            .And.Contain("number of tenants");

        SchemaTableAssembler.PartitionCountsWithheld(readOnly: true, capabilityEnabled: false, authorized: null)
            .Should().Contain("MartenStudioOptions.ReadOnly");

        SchemaTableAssembler.PartitionCountsWithheld(readOnly: false, capabilityEnabled: true, authorized: false)
            .Should().Contain("WriteAuthorizationPolicy");
    }

    [Fact]
    public void The_rows_keep_the_order_the_catalog_read_gave_them()
    {
        IReadOnlyList<TableStats> tables = Assemble(
            [Row(EventSchema, "mt_events"), Row(EventSchema, "qrtz_locks"), Row(DocumentSchema, "mt_hilo")],
            partitionCountsShown: false);

        tables.Select(x => x.Table).Should().Equal("mt_events", "qrtz_locks", "mt_hilo");
    }

    private static TableStats Single(TableStatsRow row) => Assemble([row], partitionCountsShown: false).Single();

    private static IReadOnlyList<TableStats> Assemble(IReadOnlyList<TableStatsRow> rows, bool partitionCountsShown) =>
        SchemaTableAssembler.Assemble(rows, Classifier(), "default", TypeNames, partitionCountsShown);

    private static TableStatsRow Row(string schema, string table, string kind = "r", int partitions = 0) =>
        new(schema, table, 16384, 8192, 8192, 10, 10, 0, 1, 1, null, null, kind, partitions);
}
