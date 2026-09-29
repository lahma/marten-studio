using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// DB-7-fix, items 3 and 8: the Schema screen degrades rather than blanks when a registered store cannot be
/// read - what a readable store declares is kept, what the unreadable one could own is withheld - and a hidden
/// document type's per-type routine is absent like its table. Over two real Marten configurations: the
/// default store readable, the other one standing in for a store whose factory throws.
/// </summary>
public class SchemaClassificationTests
{
    private static readonly UnreadableStore Broken = new(OtherStoreKey, "could not be built", "its connection string is wrong");

    [Fact]
    public void A_complete_classification_withholds_nothing_and_says_nothing()
    {
        SchemaClassification complete = SchemaClassification.Complete(Classifier());

        complete.IsDegraded.Should().BeFalse();
        complete.WithholdsRelation("host_audit_log", new DatabaseObjectOwnership(DatabaseObjectOwner.Other)).Should().BeFalse();
        complete.WithholdsRoutine(new DatabaseObjectOwnership(DatabaseObjectOwner.Other)).Should().BeFalse();
        complete.TablesNotice(0).Should().BeNull();
        complete.IndexesNotice(0).Should().BeNull();
        complete.FunctionsNotice(0).Should().BeNull();
    }

    /// <summary>
    /// What the readable store declares keeps its classification; Marten's bookkeeping is Marten's by name
    /// whoever owns it; everything else - the host's table, the unreadable store's own document table and its
    /// extended table - could be that store's, so it is withheld.
    /// </summary>
    [Theory]
    [InlineData(DocumentSchema, "mt_doc_dbcustomer", false)]
    [InlineData(EventSchema, "mt_events", false)]
    [InlineData(ReportingSchema, "flat_orders", false)]
    [InlineData(ReportingSchema, "ef_things", false)]
    [InlineData(DocumentSchema, "mt_hilo", false)]
    [InlineData(DocumentSchema, "mt_tenant_partitions", false)]
    [InlineData(DocumentSchema, "host_audit_log", true)]
    [InlineData(OtherSchema, "mt_doc_dbinvoice", true)]
    [InlineData(OtherSchema, OtherReportTable, true)]
    [InlineData(DocumentSchema, "mt_doc_nobody_declares", true)]
    public void A_degraded_classification_withholds_only_what_the_unreadable_store_could_own(string schema, string table, bool withheld)
    {
        SchemaClassification degraded = Degraded();

        DatabaseObjectOwnership ownership = degraded.Classifier.ClassifyRelation(schema, table)
            ?? throw new InvalidOperationException(schema + "." + table + " is hidden");

        degraded.WithholdsRelation(table, ownership).Should().Be(withheld, schema + "." + table);
    }

    [Fact]
    public void A_readable_stores_hidden_type_stays_absent_rather_than_withheld()
    {
        Degraded().Classifier.ClassifyRelation(DocumentSchema, "mt_doc_dbsecret").Should().BeNull();
    }

    [Fact]
    public void Martens_routines_and_a_readable_stores_declared_function_are_kept_and_the_rest_withheld()
    {
        SchemaClassification degraded = Degraded();

        degraded.WithholdsRoutine(degraded.Classifier.ClassifyRoutine(DocumentSchema, "mt_jsonb_patch")).Should().BeFalse();
        degraded.WithholdsRoutine(degraded.Classifier.ClassifyRoutine(EventSchema, "mt_quick_append_events")).Should().BeFalse();
        degraded.WithholdsRoutine(degraded.Classifier.ClassifyRoutine(ReportingSchema, "ext_touch")).Should().BeFalse(
            "the readable store hands it to ExtendedSchemaObjects");
        degraded.WithholdsRoutine(degraded.Classifier.ClassifyRoutine(DocumentSchema, "host_touch")).Should().BeTrue();
    }

    [Fact]
    public void The_notice_names_the_store_and_why_to_a_visitor_that_stores_policy_passes()
    {
        string notice = Degraded().TablesNotice(2)!;

        notice.Should().StartWith("Marten store '" + OtherStoreKey + "' could not be built (its connection string is wrong)")
            .And.Contain("2 tables here that no readable store declares are withheld")
            .And.Contain("until it can be read");
    }

    [Fact]
    public void The_notice_names_neither_the_store_nor_its_error_to_a_visitor_that_stores_policy_refuses()
    {
        SchemaClassification degraded = SchemaClassification.Degraded(
            DefaultOnly(), [new UnreadableStore(null, "could not be built", null)]);

        string notice = degraded.IndexesNotice(1)!;

        notice.Should().StartWith("A registered Marten store you may not see could not be built")
            .And.NotContain(OtherStoreKey)
            .And.Contain("The indexes on 1 table");
    }

    [Fact]
    public void Every_tab_says_so_when_nothing_needed_withholding()
    {
        SchemaClassification degraded = Degraded();

        degraded.TablesNotice(0).Should().Contain("none is withheld");
        degraded.IndexesNotice(0).Should().Contain("none is withheld");
        degraded.FunctionsNotice(0).Should().Contain("none is withheld").And.Contain("Marten's own routines are listed with their bodies");
    }

    [Fact]
    public void Two_unreadable_stores_are_both_named_or_counted()
    {
        SchemaClassification degraded = SchemaClassification.Degraded(
            DefaultOnly(), [Broken, new UnreadableStore(null, "could not be read", null)]);

        degraded.Who().Should().Be(
            "2 registered Marten stores could not be read ('" + OtherStoreKey + "' could not be built: its connection " +
            "string is wrong; one you may not see could not be read)");
    }

    /// <summary>
    /// The re-review of DB-1-fix: a database an earlier Marten wrote keeps <c>mt_upsert_&lt;alias&gt;</c> and
    /// its kin, whose bodies name the hidden type's table.
    /// </summary>
    [Theory]
    [InlineData("mt_upsert_dbsecret", true)]
    [InlineData("mt_insert_dbsecret", true)]
    [InlineData("mt_update_dbsecret", true)]
    [InlineData("MT_OVERWRITE_DBSECRET", true)]
    [InlineData("mt_upsert_dbcustomer", false)]
    [InlineData("mt_jsonb_patch", false)]
    [InlineData("mt_upsert_", false)]
    public void A_hidden_types_per_type_routine_is_hidden_and_a_visible_types_is_not(string routine, bool hidden)
    {
        SchemaClassification.Complete(Classifier()).HidesRoutine(DocumentSchema, routine).Should().Be(hidden, routine);
    }

    [Fact]
    public void A_per_type_routine_whose_table_nobody_declares_is_hidden_while_the_host_hides_anything()
    {
        SchemaClassification.Complete(Classifier()).HidesRoutine(DocumentSchema, "mt_upsert_notlearnedyet")
            .Should().BeTrue("a type Marten has not learned yet may be one the host hides");

        SchemaClassification.Complete(Classifier(isVisible: null)).HidesRoutine(DocumentSchema, "mt_upsert_notlearnedyet")
            .Should().BeFalse("nothing is hidden when the host hides nothing");
    }

    [Fact]
    public void A_per_type_routine_is_matched_to_the_table_in_its_own_schema()
    {
        SchemaClassification.Complete(Classifier()).HidesRoutine(OtherSchema, "mt_upsert_dbsecret")
            .Should().BeTrue("no store declares other_db.mt_doc_dbsecret, and hiding is configured");

        SchemaClassification.Complete(Classifier(isVisible: null)).HidesRoutine(OtherSchema, "mt_upsert_dbsecret")
            .Should().BeFalse();
    }

    private static SchemaClassification Degraded() => SchemaClassification.Degraded(DefaultOnly(), [Broken]);

    /// <summary>The default store alone, as if the other store's factory had thrown.</summary>
    private static DatabaseObjectClassifier DefaultOnly()
    {
        SchemaDeclarationRead read = SchemaDeclarationReader.ReadForClassification(DefaultStore(), IsVisible);
        read.Succeeded.Should().BeTrue(read.Failure);

        return new DatabaseObjectClassifier([new StoreDeclarations("default", read.Declarations!)], hidesDocumentTypes: true);
    }
}
