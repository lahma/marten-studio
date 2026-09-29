using MartenStudio.Services.Database;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Acceptance 4: whose each object is, over two real Marten configurations - one owner kind per test.
/// </summary>
public class DatabaseObjectClassifierTests
{
    private readonly DatabaseObjectClassifier classifier = Classifier();

    [Fact]
    public void A_visible_document_types_table_is_a_Marten_document_with_its_alias_and_its_store()
    {
        DatabaseObjectOwnership? owner = classifier.ClassifyRelation(DocumentSchema, "mt_doc_dbcustomer");

        owner.Should().NotBeNull();
        owner!.Owner.Should().Be(DatabaseObjectOwner.MartenDocument);
        owner.Alias.Should().Be("dbcustomer");
        owner.StoreKey.Should().Be("default");
        owner.IsMarten.Should().BeTrue();
    }

    /// <summary>
    /// A hidden type is not "Other": it is absent, and so is everything only about it - a trigger on its
    /// table, a sequence one of its columns owns.
    /// </summary>
    [Fact]
    public void A_hidden_types_table_and_what_hangs_off_it_are_absent_rather_than_Other()
    {
        classifier.ClassifyRelation(DocumentSchema, "mt_doc_dbsecret").Should().BeNull();
        classifier.IsHiddenTable(DocumentSchema, "mt_doc_dbsecret").Should().BeTrue();

        classifier.ClassifyTrigger(DocumentSchema, "mt_doc_dbsecret", "audit_secret").Should().BeNull();
        classifier.ClassifySequence(DocumentSchema, "mt_doc_dbsecret_id_seq", DocumentSchema, "mt_doc_dbsecret")
            .Should().BeNull();
        classifier.ClassifySequence("public", "host_counter", DocumentSchema, "mt_doc_dbsecret").Should().BeNull();
    }

    [Theory]
    [InlineData("mt_events")]
    [InlineData("mt_streams")]
    [InlineData("mt_event_progression")]
    public void The_event_stores_own_tables_are_the_event_store(string table) =>
        classifier.ClassifyRelation(EventSchema, table)!.Owner.Should().Be(DatabaseObjectOwner.MartenEventStore);

    [Fact]
    public void A_flat_table_projections_table_is_Marten_managed_and_browsable_data()
    {
        classifier.ClassifyRelation(ReportingSchema, "flat_orders")!.Owner
            .Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
    }

    [Fact]
    public void ExtendedSchemaObjects_are_Marten_managed_table_function_and_sequence_alike()
    {
        classifier.ClassifyRelation(ReportingSchema, "ef_things")!.Owner
            .Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
        classifier.ClassifyRoutine(ReportingSchema, "ext_touch")!.Owner
            .Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
        classifier.ClassifySequence(ReportingSchema, "ext_numbers", null, null)!.Owner
            .Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
    }

    /// <summary>
    /// <c>mt_tenant_databases</c> holds tenant connection strings; it must never be "Other", wherever it is.
    /// </summary>
    [Theory]
    [InlineData("tenants", "mt_tenant_databases")]
    [InlineData("public", "mt_tenant_databases")]
    [InlineData(DocumentSchema, "mt_hilo")]
    [InlineData(DocumentSchema, "mt_tenant_partitions")]
    [InlineData("somebody_elses", "mt_doc_ghost")]
    [InlineData("somebody_elses", "MT_ANYTHING")]
    public void Every_mt_table_in_any_schema_is_Marten_infrastructure(string schema, string table) =>
        classifier.ClassifyRelation(schema, table)!.Owner.Should().Be(DatabaseObjectOwner.MartenInfrastructure);

    [Fact]
    public void Marten_functions_and_sequences_are_infrastructure_wherever_they_are()
    {
        classifier.ClassifyRoutine(EventSchema, "mt_quick_append_events")!.Owner
            .Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        classifier.ClassifyRoutine(ReportingSchema, "mt_upsert_flat_orders_dborderplaced")!.Owner
            .Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        classifier.ClassifyRoutine("elsewhere", "mt_jsonb_patch")!.Owner
            .Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        classifier.ClassifySequence(EventSchema, "mt_events_sequence", null, null)!.Owner
            .Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        DatabaseObjectClassifier.ClassifyType("public", "mt_something").Owner
            .Should().Be(DatabaseObjectOwner.MartenInfrastructure);
    }

    /// <summary>The per-tenant event sequences are named after tenants, so they are never listed.</summary>
    [Fact]
    public void A_per_tenant_event_sequence_is_rolled_up_rather_than_listed()
    {
        classifier.ClassifySequence(EventSchema, "mt_events_sequence_acme", null, null).Should().BeNull();
        classifier.ClassifySequence(EventSchema, "mt_events_sequence_8f1d5a6e-1a2b", null, null).Should().BeNull();

        DatabaseObjectClassifier.IsPerTenantEventSequence("mt_events_sequence").Should().BeFalse();
        DatabaseObjectClassifier.IsPerTenantEventSequence("mt_events_sequence_").Should().BeFalse();
        DatabaseObjectClassifier.IsPerTenantEventSequence("mt_events_sequence_t").Should().BeTrue();
    }

    [Fact]
    public void A_table_another_registered_store_declares_is_that_stores_and_never_Other()
    {
        DatabaseObjectOwnership? owner = classifier.ClassifyRelation(OtherSchema, "mt_doc_dbinvoice");

        owner!.Owner.Should().Be(DatabaseObjectOwner.MartenDocument);
        owner.StoreKey.Should().Be(OtherStoreKey);
        owner.Alias.Should().Be("dbinvoice");
    }

    [Theory]
    [InlineData("quartz", "qrtz_triggers", "Quartz.NET")]
    [InlineData("quartz", "QRTZ_JOB_DETAILS", "Quartz.NET")]
    [InlineData("wolverine", "wolverine_incoming_envelopes", "Wolverine")]
    [InlineData("public", "__EFMigrationsHistory", "EF Core")]
    [InlineData("hangfire", "job", "Hangfire")]
    [InlineData("public", "flyway_schema_history", "Flyway")]
    public void Another_librarys_tables_are_Other_with_a_hint_from_the_name(string schema, string table, string hint)
    {
        DatabaseObjectOwnership owner = classifier.ClassifyRelation(schema, table)!;

        owner.Owner.Should().Be(DatabaseObjectOwner.Other);
        owner.IsMarten.Should().BeFalse();
        owner.RecognisedAs.Should().Be(hint);
    }

    [Fact]
    public void The_hosts_own_objects_are_Other_with_no_hint()
    {
        DatabaseObjectOwnership table = classifier.ClassifyRelation("legacy", "orders")!;
        table.Owner.Should().Be(DatabaseObjectOwner.Other);
        table.RecognisedAs.Should().BeNull();

        classifier.ClassifyRoutine("legacy", "recalculate")!.Owner.Should().Be(DatabaseObjectOwner.Other);
        classifier.ClassifyTrigger("legacy", "orders", "orders_audit")!.Owner.Should().Be(DatabaseObjectOwner.Other);
        DatabaseObjectClassifier.ClassifyType("legacy", "order_status").Owner.Should().Be(DatabaseObjectOwner.Other);
    }

    /// <summary>A sequence belongs with the table whose column owns it.</summary>
    [Fact]
    public void An_owned_sequence_follows_its_tables_owner()
    {
        classifier.ClassifySequence("legacy", "orders_id_seq", "legacy", "orders")!.Owner
            .Should().Be(DatabaseObjectOwner.Other);

        classifier.ClassifySequence(ReportingSchema, "flat_orders_seq", ReportingSchema, "flat_orders")!.Owner
            .Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
    }

    /// <summary>A trigger a host put on a Marten table is the host's: its owner is its own name's.</summary>
    [Fact]
    public void A_trigger_on_a_Marten_table_is_owned_by_its_own_name()
    {
        classifier.ClassifyTrigger(DocumentSchema, "mt_doc_dbcustomer", "customer_audit")!.Owner
            .Should().Be(DatabaseObjectOwner.Other);
    }

    /// <summary>
    /// DB-1-fix re-review, item 1: the per-type functions an older Marten left beside a hidden type's table
    /// name its alias, list its duplicated fields and name its table - so they are as absent as it is.
    /// </summary>
    [Theory]
    [InlineData("mt_upsert_dbsecret")]
    [InlineData("mt_insert_dbsecret")]
    [InlineData("mt_update_dbsecret")]
    [InlineData("mt_overwrite_dbsecret")]
    [InlineData("MT_UPSERT_DBSECRET")]
    public void A_hidden_types_own_functions_are_absent(string name)
    {
        classifier.IsHiddenRoutine(DocumentSchema, name).Should().BeTrue();
        classifier.ClassifyRoutine(DocumentSchema, name).Should().BeNull();
    }

    [Theory]
    [InlineData(DocumentSchema, "mt_upsert_dbcustomer")]
    [InlineData(DocumentSchema, "mt_upsert_")]
    [InlineData(DocumentSchema, "mt_immutable_timestamp")]
    [InlineData("elsewhere", "mt_upsert_dbsecret")]
    [InlineData(DocumentSchema, "mt_delete_dbsecret")]
    public void Every_other_function_is_classified_as_before(string schema, string name)
    {
        classifier.IsHiddenRoutine(schema, name).Should().BeFalse();
        classifier.ClassifyRoutine(schema, name).Should().NotBeNull();
    }

    [Fact]
    public void A_definition_that_names_a_hidden_types_table_is_recognised_however_it_names_it()
    {
        classifier.MentionsHiddenTable("insert into \"studio_db\".\"mt_doc_dbsecret\"(data) values (doc)").Should().BeTrue();
        classifier.MentionsHiddenTable("select count(*) from studio_db.MT_DOC_DBSECRET").Should().BeTrue();
        classifier.MentionsHiddenTable("select 1 from mt_doc_dbsecret -- through its search_path").Should().BeTrue();
        classifier.MentionsHiddenTable("select 1 from studio_db.mt_doc_dbcustomer").Should().BeFalse();
        classifier.MentionsHiddenTable(null).Should().BeFalse();

        Classifier(isVisible: null).MentionsHiddenTable("select 1 from mt_doc_dbsecret").Should().BeFalse("nothing is hidden");
    }

    /// <summary>The overview's count leaves out exactly what the lists leave out: the tables, and their functions.</summary>
    [Fact]
    public void The_count_is_given_every_hidden_table_and_its_functions()
    {
        classifier.HiddenTablesAndRoutines().Should().BeEquivalentTo(
        [
            DocumentSchema + ".mt_doc_dbsecret",
            DocumentSchema + ".mt_upsert_dbsecret",
            DocumentSchema + ".mt_insert_dbsecret",
            DocumentSchema + ".mt_update_dbsecret",
            DocumentSchema + ".mt_overwrite_dbsecret",
        ]);
    }
}
