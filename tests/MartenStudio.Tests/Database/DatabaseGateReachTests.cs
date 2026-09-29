using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Database;

/// <summary>
/// What a relation's rows reach, judged row by row through the gate and the assembler - no database:
/// DB-3-fix F2 (a foreign table's rows are never read through a partition, an inheritance child or a
/// view), and the DB-1-fix re-review's view items - Marten's own and another store's tables behind a view,
/// a schema named as a value, a collation, a text search configuration, code the studio cannot follow, and
/// the schemas extensions own.
/// </summary>
public class DatabaseGateReachTests
{
    /// <summary>
    /// The store's schemas; <c>quartz</c>, <c>legacy</c> and the other store's schema browsable; <c>secrets</c>
    /// withheld and readable by the role; <c>cron</c> an extension's.
    /// </summary>
    private static readonly IReadOnlyList<CatalogSchema> Live =
    [
        new("pg_catalog", true, false),
        new(DocumentSchema, true, false),
        new(EventSchema, true, false),
        new(ReportingSchema, true, false),
        new("quartz", true, false),
        new("legacy", true, false),
        new(OtherSchema, true, false),
        new("secrets", true, false),
        new("cron", true, true),
    ];

    // ---------------------------------------------------------------------------------------------
    // F2: foreign rows are never reached indirectly
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_partitioned_table_with_a_foreign_partition_and_a_table_with_a_foreign_child_are_never_read()
    {
        DatabaseGate gate = Gate();
        DatabaseObjectOwnership other = gate.Classifier.ClassifyRelation("legacy", "mixed_parts")!;

        DatabaseRowAccess partitioned = gate.RowsFor(
            Relation("legacy", "mixed_parts", "p") with { ForeignDescendant = true, DescendantSchemas = ["legacy"] }, other, viewRefusal: null);

        partitioned.Refusal.Should().Be(DatabaseRefusal.ForeignTable);
        partitioned.Reason.Should().Be(DatabaseGate.ForeignDescendantDenial);

        gate.RowsFor(Relation("legacy", "legacy_parent") with { ForeignDescendant = true }, other, viewRefusal: null)
            .Refusal.Should().Be(DatabaseRefusal.ForeignTable, "SELECT from a parent reads its inheritance children");

        gate.RowsFor(Relation("legacy", "local_parts", "p") with { DescendantSchemas = ["legacy"] }, other, viewRefusal: null)
            .Allowed.Should().BeTrue("a partition tree with nothing foreign in it is local rows");

        CatalogSnapshot snapshot = Snapshot(relations: [Relation("legacy", "mixed_parts", "p") with { ForeignDescendant = true }]);

        DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Tables).Cast<DatabaseRelationSummary>().Single()
            .Rows.Refusal.Should().Be(DatabaseRefusal.ForeignTable, "the list says what the row gate will say");
    }

    [Fact]
    public void A_view_that_reads_a_foreign_table_is_refused_its_rows_and_keeps_its_query()
    {
        DatabaseGate gate = Gate();

        foreach (CatalogViewDependency read in new[]
                 {
                     Reads("remote_peek", "legacy", "remote_mirror", "f"),
                     Reads("remote_peek", "legacy", "mixed_parts", "p", foreignDescendant: true),
                     Reads("remote_peek", "legacy", "legacy_parent", "r", foreignDescendant: true, depth: 2),
                 })
        {
            DatabaseObjectDetail detail = DatabaseObjectAssembler.Detail(gate, View("remote_peek", [read]));

            detail.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.ForeignTable, read.Name);
            detail.Relation.Rows.Reason.Should().Be(DatabaseGate.ForeignViewDenial);
            detail.Relation.DefinitionAvailable.Should().BeTrue("the query names what the list shows anyway: " + read.Name);
        }
    }

    [Fact]
    public void A_materialized_view_is_local_rows_and_so_is_a_view_that_reaches_a_foreign_table_only_through_one()
    {
        DatabaseGate gate = Gate();

        DatabaseObjectAssembler.Detail(gate, View("remote_snapshot", [Reads("remote_snapshot", "legacy", "remote_mirror", "f")], kind: "m"))
            .Relation!.Rows.Allowed.Should().BeTrue("a materialized view's rows are stored here");

        DatabaseObjectDetail throughSnapshot = DatabaseObjectAssembler.Detail(
            gate,
            View(
                "snapshot_peek",
                [
                    Reads("snapshot_peek", "legacy", "remote_snapshot", "m"),
                    Reads("snapshot_peek", "legacy", "remote_mirror", "f", depth: 2, throughMaterializedView: true),
                ]));

        throughSnapshot.Relation!.Rows.Allowed.Should().BeTrue(throughSnapshot.Relation.Rows.Reason);
    }

    [Fact]
    public void A_partition_in_a_withheld_schema_withholds_its_parents_rows_but_one_an_extension_owns_does_not()
    {
        DatabaseGate gate = Gate();
        DatabaseObjectOwnership other = gate.Classifier.ClassifyRelation("legacy", "readings")!;

        DatabaseRowAccess withheld = gate.RowsFor(
            Relation("legacy", "readings", "p") with { DescendantSchemas = ["legacy", "secrets"] }, other, viewRefusal: null);

        withheld.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
        withheld.Reason.Should().Be(DatabaseGate.WithheldDescendantDenial);

        gate.RowsFor(Relation("legacy", "hypertable", "r") with { DescendantSchemas = ["cron"] }, other, viewRefusal: null)
            .Allowed.Should().BeTrue("an extension's schema - a hypertable's chunks - is never withheld for this");

        DatabaseObjectDetail view = DatabaseObjectAssembler.Detail(
            gate, View("readings_view", [Reads("readings_view", "legacy", "readings", "p", descendantSchemas: ["legacy", "secrets"])]));

        view.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
        view.Relation.Rows.Reason.Should().Be(DatabaseGate.WithheldDescendantViewDenial);
        view.Relation.DefinitionAvailable.Should().BeTrue("the view's query names the parent, which is visible");
    }

    // ---------------------------------------------------------------------------------------------
    // Re-review 2c: Marten's tables are never read raw, through a view either
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_view_over_a_marten_document_or_event_table_is_refused_its_rows()
    {
        DatabaseGate gate = Gate();

        foreach (CatalogViewDependency read in new[]
                 {
                     Reads("docs", DocumentSchema, "mt_doc_dbcustomer"),
                     Reads("docs", EventSchema, "mt_events"),
                     Reads("docs", DocumentSchema, "mt_hilo", depth: 2),
                 })
        {
            DatabaseObjectDetail detail = DatabaseObjectAssembler.Detail(gate, View("docs", [read]));

            detail.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.MartenOwned, read.Name);
            detail.Relation.Rows.Reason.Should().Be(DatabaseGate.MartenDependencyDenial);
            detail.Relation.DefinitionAvailable.Should().BeTrue(read.Name);
        }

        DatabaseObjectAssembler.Detail(gate, View("flat", [Reads("flat", ReportingSchema, "flat_orders")]))
            .Relation!.Rows.Allowed.Should().BeTrue("a projection's table is Marten-managed relational data, browsable itself");
    }

    [Fact]
    public void A_view_over_another_stores_table_is_refused_what_that_stores_policy_refuses()
    {
        CatalogRelationDetail view = View("their_report", [Reads("their_report", OtherSchema, OtherReportTable)]);

        DatabaseObjectAssembler.Detail(Gate(otherStores: OtherStore(DatabaseStoreAccess.Open)), view)
            .Relation!.Rows.Allowed.Should().BeTrue("that store's policies allow it");

        DatabaseRowAccess refused = DatabaseObjectAssembler.Detail(
                Gate(otherStores: OtherStore(new DatabaseStoreAccess(false, DatabaseRowAccess.Refused(DatabaseRefusal.StorePolicy, DatabaseGate.OtherStorePolicyDenial)))),
                view)
            .Relation!.Rows;

        refused.Refusal.Should().Be(DatabaseRefusal.StorePolicy);

        DatabaseObjectAssembler.Detail(Gate(), view).Relation!.Rows.Refusal
            .Should().Be(DatabaseRefusal.StorePolicy, "a store nobody asked about is refused, not assumed");
    }

    // ---------------------------------------------------------------------------------------------
    // Re-review 2a: a schema named as a value, a collation, a text search configuration or dictionary
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("n", "secrets")]
    [InlineData("C", "c")]
    [InlineData("T", "cfg")]
    [InlineData("D", "dict")]
    public void A_view_that_names_something_in_a_withheld_schema_is_refused_its_rows_and_its_query(string kind, string name)
    {
        DatabaseGate gate = Gate();
        CatalogRelationDetail view = View("names_it", [], [Refers("names_it", kind, "secrets", name)]);

        DatabaseObjectDetail detail = DatabaseObjectAssembler.Detail(gate, view);

        detail.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, kind);
        detail.Relation.DefinitionAvailable.Should().BeFalse(kind);
        DatabaseObjectAssembler.DefinitionRefusal(gate, view)!.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, kind);

        DatabaseObjectAssembler.Detail(gate, View("names_cron", [], [Refers("names_cron", kind, "cron", name)]))
            .Relation!.Rows.Allowed.Should().BeTrue("an extension's schema is not withheld for this: " + kind);
    }

    // ---------------------------------------------------------------------------------------------
    // Re-review 2b: code the studio cannot follow
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_view_calling_code_the_studio_cannot_follow_is_refused_its_rows_while_the_role_can_read_a_withheld_schema()
    {
        DatabaseGate gate = Gate(classifier: Classifier(isVisible: null));
        CatalogRelationDetail view = View("wrapped", [], [Refers("wrapped", "f", "legacy", "wrap", userCode: true)]);

        DatabaseObjectDetail detail = DatabaseObjectAssembler.Detail(gate, view);

        detail.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
        detail.Relation.Rows.Reason.Should().Be(DatabaseGate.OpaqueWithheldDenial);
        detail.Relation.DefinitionAvailable.Should().BeTrue("the query names the function, not what the function reads");

        DatabaseObjectAssembler.Detail(gate, View("atomic", [], [Refers("atomic", "f", "legacy", "atomic", userCode: false)]))
            .Relation!.Rows.Allowed.Should().BeTrue("a SQL-standard body is followed, so its reads are in the dependencies instead");
    }

    [Fact]
    public void Code_the_studio_cannot_follow_is_read_when_nothing_the_role_can_read_is_withheld_unless_it_runs_as_its_owner()
    {
        IReadOnlyList<CatalogSchema> narrowed =
        [
            new(DocumentSchema, true, false),
            new(EventSchema, true, false),
            new(ReportingSchema, true, false),
            new("quartz", true, false),
            new("legacy", true, false),
            new(OtherSchema, true, false),
            new("payroll", false, false),
            new("cron", true, true),
        ];

        DatabaseGate gate = Gate(live: narrowed, classifier: Classifier(isVisible: null));

        DatabaseObjectAssembler.Detail(gate, View("wrapped", [], [Refers("wrapped", "f", "legacy", "wrap", userCode: true)]))
            .Relation!.Rows.Allowed.Should().BeTrue("the function reads as the role, and nothing the role can read is withheld");

        DatabaseObjectAssembler.Detail(gate, View("definer", [], [Refers("definer", "f", "legacy", "wrap", userCode: true, definer: true)]))
            .Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, "SECURITY DEFINER reads as its owner, who may read payroll");

        DatabaseObjectAssembler.Detail(Gate(live: narrowed), View("wrapped", [], [Refers("wrapped", "f", "legacy", "wrap", userCode: true)]))
            .Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency, "while types are hidden, the older and stricter rule still holds");
    }

    // ---------------------------------------------------------------------------------------------
    // Re-review 1: a hidden type's own functions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_hidden_types_own_functions_are_not_listed()
    {
        foreach (bool open in new[] { true, false })
        {
            DatabaseGate gate = open ? Gate() : Gate(capability: false);

            CatalogSnapshot snapshot = Snapshot(routines:
            [
                Routine(DocumentSchema, "mt_upsert_dbsecret"),
                Routine(DocumentSchema, "MT_INSERT_DBSECRET"),
                Routine(DocumentSchema, "mt_update_dbsecret"),
                Routine(DocumentSchema, "mt_overwrite_dbsecret"),
                Routine(DocumentSchema, "mt_upsert_dbcustomer"),
                Routine(DocumentSchema, "mt_immutable_timestamp"),
            ]);

            DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Functions)
                .Select(static x => x.Name)
                .Should().Equal(["mt_upsert_dbcustomer", "mt_immutable_timestamp"], "capability " + (open ? "on" : "off"));
        }
    }

    // ---------------------------------------------------------------------------------------------

    private static DatabaseGate Gate(
        IReadOnlyList<CatalogSchema>? live = null,
        DatabaseObjectClassifier? classifier = null,
        IReadOnlyDictionary<string, DatabaseStoreAccess>? otherStores = null,
        bool capability = true) =>
        new(StoreSchemas(), live ?? Live, ["quartz", "legacy", OtherSchema], capability, false, capability ? true : null,
            classifier ?? Classifier(), null, DatabaseRefusal.WritePolicy, "default", otherStores);

    private static Dictionary<string, DatabaseStoreAccess> OtherStore(DatabaseStoreAccess access) =>
        new(StringComparer.OrdinalIgnoreCase) { [OtherStoreKey] = access };

    private static CatalogViewDependency Reads(
        string view,
        string schema,
        string name,
        string kind = "r",
        int depth = 1,
        bool foreignDescendant = false,
        IReadOnlyList<string>? descendantSchemas = null,
        bool throughMaterializedView = false) =>
        new("legacy", view, schema, name, kind, depth, foreignDescendant, descendantSchemas, throughMaterializedView);

    private static CatalogViewReference Refers(string view, string kind, string schema, string name, bool userCode = false, bool definer = false) =>
        new("legacy", view, kind, schema, name, userCode, 1, definer);

    private static CatalogRelationDetail View(
        string name,
        IReadOnlyList<CatalogViewDependency> reads,
        IReadOnlyList<CatalogViewReference>? references = null,
        string kind = "v") =>
        new(
            Relation("legacy", name, kind),
            [new CatalogColumn("id", 1, "integer", false, null, string.Empty, string.Empty, true, null)],
            [],
            [],
            [],
            CatalogList<CatalogForeignKey>.Empty,
            new CatalogList<CatalogViewDependency>(reads, false))
        {
            References = new CatalogList<CatalogViewReference>(references ?? [], false),
        };

    private static CatalogRoutine Routine(string schema, string name) =>
        new(schema, name, string.Empty, "f", "void", "plpgsql", "v", false, [], null);

    private static CatalogSnapshot Snapshot(
        IReadOnlyList<CatalogRelation>? relations = null,
        IReadOnlyList<CatalogRoutine>? routines = null) =>
        new(
            new CatalogList<CatalogRelation>(relations ?? [], false),
            new CatalogList<CatalogRoutine>(routines ?? [], false),
            CatalogList<CatalogTrigger>.Empty,
            CatalogList<CatalogSequence>.Empty,
            CatalogList<CatalogType>.Empty,
            CatalogList<CatalogForeignKey>.Empty,
            CatalogList<CatalogViewDependency>.Empty);
}
