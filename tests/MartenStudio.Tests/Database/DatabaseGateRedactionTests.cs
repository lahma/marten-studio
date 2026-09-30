using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The DB-1 review's findings, row by row through the assembler - no database: a withheld schema's name
/// never reaches a page (F1), a view is judged on what it reads whatever it is called (F2) and on what it
/// calls (F3), another store's identity and rows follow that store's policies (F4), and the overview counts
/// are exact (F6).
/// </summary>
public class DatabaseGateRedactionTests
{
    private const string Mask = WithheldNames.Token;

    /// <summary>The live schemas of these tests: the store's, two browsable ones, the other store's, and <c>hr</c>.</summary>
    private static readonly IReadOnlyList<CatalogSchema> Live =
    [
        new("pg_catalog", true, false),
        new(DocumentSchema, true, false),
        new(EventSchema, true, false),
        new(ReportingSchema, true, false),
        new("quartz", true, false),
        new("legacy", true, false),
        new(OtherSchema, true, false),
        new("hr", true, false),
    ];

    // ---------------------------------------------------------------------------------------------
    // F1 - withheld names in definitions and structure
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_withheld_schema_is_masked_in_every_free_text_field_of_a_detail()
    {
        DatabaseGate gate = Gate(entries: ["quartz"]);

        gate.WithheldSchemas.Should().BeEquivalentTo(["legacy", OtherSchema, "hr"]);

        CatalogRelationDetail detail = new(
            Relation("quartz", "refs"),
            [
                new("id", 1, "integer", true, "nextval('hr.refs_seq'::regclass)", string.Empty, string.Empty, true, "copied from hr.salaries"),
                new("grade", 2, "hr.grade", false, null, string.Empty, string.Empty, true, null),
                new("tag", 3, "\"Legal\".tag", false, null, string.Empty, string.Empty, true, null),
            ],
            [new("refs_check", "c", "CHECK (hr.is_ok(id))")],
            [new("refs_idx", "CREATE INDEX refs_idx ON quartz.refs USING btree (hr.norm(id))", false, false, true, false, true, [null])],
            [],
            CatalogList<CatalogForeignKey>.Empty,
            CatalogList<CatalogViewDependency>.Empty);

        DatabaseObjectDetail shown = DatabaseObjectAssembler.Detail(gate, detail);

        shown.Columns[0].Default.Should().Be("nextval('" + Mask + ".refs_seq'::regclass)");
        shown.Columns[0].Comment.Should().Be("copied from " + Mask + ".salaries");
        shown.Columns[1].Type.Should().Be(Mask + ".grade");
        shown.Columns[2].Type.Should().Be("\"Legal\".tag", "a schema that does not exist here is not one anybody withholds");
        shown.Constraints.Single().Definition.Should().Be("CHECK (" + Mask + ".is_ok(id))");
        shown.Indexes.Single().Definition.Should().Be("CREATE INDEX refs_idx ON quartz.refs USING btree (" + Mask + ".norm(id))");

        Serialized(shown).Should().NotContain("hr.", "no field of a detail may name a withheld schema");
    }

    [Fact]
    public void A_routines_signature_result_settings_and_comment_are_masked()
    {
        DatabaseGate gate = Gate(entries: ["quartz"]);

        CatalogSnapshot snapshot = Snapshot(routines:
        [
            new("quartz", "leak", "g hr.grade", "f", "SETOF hr.salaries", "sql", "s", true, ["search_path=hr, quartz"], "see hr.salaries"),
        ]);

        DatabaseRoutineSummary routine = DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Functions)
            .Cast<DatabaseRoutineSummary>().Single();

        routine.IdentityArguments.Should().Be("g " + Mask + ".grade");
        routine.Ref.Arguments.Should().Be("g " + Mask + ".grade", "the page asks for the definition with what it was shown");
        routine.Result.Should().Be("SETOF " + Mask + ".salaries");
        routine.Config.Should().Equal("search_path=" + Mask + ", quartz");
        routine.SearchPathPinned.Should().BeTrue("decided on the setting itself, before it is masked");
        routine.Comment.Should().Be("see " + Mask + ".salaries");
    }

    [Fact]
    public void A_types_description_is_masked()
    {
        DatabaseGate gate = Gate(entries: ["quartz"]);

        DatabaseTypeSummary type = DatabaseObjectAssembler.Type(
            gate,
            new CatalogType("quartz", "guarded", "d", "hr.base", false, "hr.next()", [], ["CHECK (hr.is_ok(VALUE))"],
                [new CatalogTypeAttribute("a", "hr.grade")], "hr.sub", 0, null));

        type.BaseType.Should().Be(Mask + ".base");
        type.Default.Should().Be(Mask + ".next()");
        type.Checks.Should().Equal("CHECK (" + Mask + ".is_ok(VALUE))");
        type.Attributes.Single().Type.Should().Be(Mask + ".grade");
        type.RangeSubtype.Should().Be(Mask + ".sub");
    }

    /// <summary>A trigger whose function is withheld offers no definition, and its function is not named.</summary>
    [Fact]
    public void A_trigger_whose_function_is_withheld_offers_no_definition()
    {
        DatabaseGate gate = Gate(entries: ["quartz"]);

        CatalogSnapshot snapshot = Snapshot(triggers:
        [
            new("quartz", "qrtz_triggers", "audit_hr", 5, "O", "hr", "audit", false),
            new("quartz", "qrtz_triggers", "audit_catalog", 5, "O", "pg_catalog", "suppress_redundant_updates_trigger", false),
            new("quartz", "qrtz_triggers", "audit_own", 5, "O", "quartz", "audit", false),
        ]);

        var triggers = DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Triggers)
            .Cast<DatabaseTriggerSummary>()
            .ToDictionary(static x => x.Name);

        triggers["audit_hr"].FunctionSchema.Should().BeNull();
        triggers["audit_hr"].DefinitionAvailable.Should().BeFalse("the definition ends EXECUTE FUNCTION hr.audit()");
        triggers["audit_catalog"].DefinitionAvailable.Should().BeTrue("Postgres' own schemas are withheld from nobody");
        triggers["audit_own"].DefinitionAvailable.Should().BeTrue();
    }

    /// <summary>A view reaching into a withheld schema is refused its rows and its query, whatever it reaches for.</summary>
    [Theory]
    [InlineData("relation")]
    [InlineData("function")]
    [InlineData("type")]
    [InlineData("sequence")]
    public void A_view_that_reaches_into_a_withheld_schema_is_refused_its_rows_and_its_query(string what)
    {
        DatabaseGate gate = Gate(entries: ["quartz"]);

        CatalogSnapshot snapshot = Snapshot(
            relations: [Relation("quartz", "hr_view", kind: "v")],
            dependencies: what == "relation" ? [new("quartz", "hr_view", "hr", "salaries", "r", 1)] : [],
            references: what switch
            {
                "function" => [new("quartz", "hr_view", "f", "hr", "pay", false, 1)],
                "type" => [new("quartz", "hr_view", "t", "hr", "grade", false, 1)],
                "sequence" => [new("quartz", "hr_view", "S", "hr", "seq", false, 1)],
                _ => [],
            });

        DatabaseRelationSummary view = (DatabaseRelationSummary) DatabaseObjectAssembler
            .Summaries(gate, snapshot, DatabaseObjectCategory.Views).Single();

        view.Rows.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
        view.Rows.Reason.Should().Be("It reads from a schema you cannot see.");
        view.DefinitionAvailable.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // F2 - an mt_ view over a hidden type's table
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Named mt_, so classified as Marten's infrastructure, and in the store's own schema - which let its
    /// query through with no capability. The verdict comes from what it reads, with or without the capability.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_mt_named_view_over_a_hidden_types_table_is_refused_its_query_with_or_without_the_capability(bool capability)
    {
        DatabaseGate gate = Gate(entries: ["*"], capability: capability);

        CatalogRelationDetail detail = new(
            Relation(DocumentSchema, "mt_peek", kind: "v"),
            [new("id", 1, "uuid", true, null, string.Empty, string.Empty, true, null)],
            [],
            [],
            [],
            CatalogList<CatalogForeignKey>.Empty,
            new CatalogList<CatalogViewDependency>([new(DocumentSchema, "mt_peek", DocumentSchema, "mt_doc_dbsecret", "r", 1)], false));

        gate.Classifier.ClassifyRelation(DocumentSchema, "mt_peek")!.Owner.Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        gate.DefinitionAccess(DocumentSchema, gate.Classifier.ClassifyRelation(DocumentSchema, "mt_peek")!)
            .Allowed.Should().BeTrue("by name it is Marten's own, in the store's own schema");

        DatabaseRowAccess? refused = DatabaseObjectAssembler.DefinitionRefusal(gate, detail);

        refused.Should().NotBeNull();
        refused!.Refusal.Should().Be(DatabaseRefusal.HiddenDependency);

        DatabaseObjectDetail shown = DatabaseObjectAssembler.Detail(gate, detail);
        shown.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency);
        shown.Relation.DefinitionAvailable.Should().BeFalse();
        shown.Dependencies.Should().ContainSingle().Which.Name.Should().BeNull("the hidden table is not named");
    }

    // ---------------------------------------------------------------------------------------------
    // F3 - a view that calls a function
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_view_that_calls_a_function_is_refused_while_any_type_may_be_hidden()
    {
        DatabaseGate gate = Gate(entries: ["*"]);

        CatalogSnapshot snapshot = Snapshot(
            relations: [Relation("quartz", "fn_view", kind: "v"), Relation("quartz", "ext_view", kind: "v")],
            references:
            [
                new("quartz", "fn_view", "f", "quartz", "reads_anything", true, 1),
                new("quartz", "ext_view", "f", "quartz", "an_extensions_function", false, 1),
            ]);

        var views = DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Views)
            .Cast<DatabaseRelationSummary>()
            .ToDictionary(static x => x.Name);

        views["fn_view"].Rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency);
        views["fn_view"].Rows.Reason.Should().Be("It calls a function; the studio cannot tell what that function reads.");
        views["fn_view"].DefinitionAvailable.Should().BeFalse();

        views["ext_view"].Rows.Allowed.Should().BeTrue("an extension's function is not code anybody here wrote");
    }

    [Fact]
    public void A_view_that_calls_a_function_is_allowed_where_nothing_is_hidden()
    {
        DatabaseGate gate = Gate(entries: ["*"], classifier: Classifier(isVisible: null));

        gate.Classifier.MayHideDocumentTypes.Should().BeFalse();

        CatalogSnapshot snapshot = Snapshot(
            relations: [Relation("quartz", "fn_view", kind: "v")],
            references: [new("quartz", "fn_view", "f", "quartz", "reads_anything", true, 1)]);

        DatabaseRelationSummary view = (DatabaseRelationSummary) DatabaseObjectAssembler
            .Summaries(gate, snapshot, DatabaseObjectCategory.Views).Single();

        view.Rows.Allowed.Should().BeTrue(view.Rows.Reason);
        view.DefinitionAvailable.Should().BeTrue();
    }

    /// <summary>
    /// A document table no store declares may be a hidden type Marten has not learned yet: while hiding is
    /// configured, a view over one is refused rather than trusted.
    /// </summary>
    [Fact]
    public void A_view_over_a_document_table_no_store_declares_is_refused_while_hiding_is_configured()
    {
        DatabaseGate hiding = Gate(entries: ["*"]);
        DatabaseGate open = Gate(entries: ["*"], classifier: Classifier(isVisible: null));

        CatalogViewDependency[] reads = [new("quartz", "stale_view", DocumentSchema, "mt_doc_somethingnew", "r", 1)];

        hiding.ViewRefusal(reads, [])!.Refusal.Should().Be(DatabaseRefusal.HiddenDependency);
        open.ViewRefusal(reads, []).Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // F4 - another store's identity and rows
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Another_stores_identity_and_rows_follow_that_stores_policies()
    {
        CatalogSnapshot snapshot = Snapshot(
            relations: [Relation(OtherSchema, "mt_doc_dbinvoice"), Relation(OtherSchema, OtherReportTable)],
            foreignKeys: [new("quartz", "invoice_refs", ["invoice_id"], OtherSchema, "mt_doc_dbinvoice", ["id"], "fk", true, "a", "a")]);

        DatabaseGate refused = Gate(
            entries: ["*"],
            storeKey: "default",
            otherStores: new Dictionary<string, DatabaseStoreAccess>
            {
                [OtherStoreKey] = new(false, DatabaseRowAccess.Refused(DatabaseRefusal.StorePolicy, DatabaseGate.OtherStorePolicyDenial)),
            });

        var tables = DatabaseObjectAssembler.Summaries(refused, snapshot, DatabaseObjectCategory.Tables)
            .Cast<DatabaseRelationSummary>()
            .ToDictionary(static x => x.Name);

        DatabaseObjectOwnership invoice = tables["mt_doc_dbinvoice"].Ownership;
        invoice.Owner.Should().Be(DatabaseObjectOwner.MartenDocument, "still Marten's, so still never read raw");
        invoice.StoreKey.Should().BeNull("a store the policy refuses and a store that does not exist are one answer");
        invoice.Alias.Should().BeNull();
        tables["mt_doc_dbinvoice"].Rows.Refusal.Should().Be(DatabaseRefusal.MartenOwned);

        DatabaseRelationSummary report = tables[OtherReportTable];
        report.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
        report.Ownership.StoreKey.Should().BeNull();
        report.Rows.Allowed.Should().BeFalse("this store's grant says nothing about another store's data");
        report.Rows.Refusal.Should().Be(DatabaseRefusal.StorePolicy);

        DatabaseGate allowed = Gate(
            entries: ["*"],
            storeKey: "default",
            otherStores: new Dictionary<string, DatabaseStoreAccess> { [OtherStoreKey] = DatabaseStoreAccess.Open });

        var shown = DatabaseObjectAssembler.Summaries(allowed, snapshot, DatabaseObjectCategory.Tables)
            .Cast<DatabaseRelationSummary>()
            .ToDictionary(static x => x.Name);

        shown["mt_doc_dbinvoice"].Ownership.StoreKey.Should().Be(OtherStoreKey);
        shown["mt_doc_dbinvoice"].Ownership.Alias.Should().Be("dbinvoice");
        shown[OtherReportTable].Rows.Allowed.Should().BeTrue(shown[OtherReportTable].Rows.Reason);
    }

    [Fact]
    public void Another_stores_rows_are_refused_when_the_write_policy_refuses_that_store()
    {
        DatabaseGate gate = Gate(
            entries: ["*"],
            storeKey: "default",
            otherStores: new Dictionary<string, DatabaseStoreAccess>
            {
                [OtherStoreKey] = new(true, DatabaseRowAccess.Refused(DatabaseRefusal.WritePolicy, DatabaseGate.OtherStoreWritePolicyDenial)),
            });

        DatabaseRowAccess rows = gate.RowsFor(
            Relation(OtherSchema, OtherReportTable),
            gate.Classifier.ClassifyRelation(OtherSchema, OtherReportTable)!,
            viewRefusal: null);

        rows.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        gate.Present(gate.Classifier.ClassifyRelation(OtherSchema, OtherReportTable)!).StoreKey.Should().Be(OtherStoreKey);
    }

    /// <summary>A store nobody asked about is refused, not assumed - fail closed.</summary>
    [Fact]
    public void Another_store_nobody_asked_about_is_refused_its_rows()
    {
        DatabaseGate gate = Gate(entries: ["*"], storeKey: "default", otherStores: new Dictionary<string, DatabaseStoreAccess>());

        gate.RowsFor(
                Relation(OtherSchema, OtherReportTable),
                gate.Classifier.ClassifyRelation(OtherSchema, OtherReportTable)!,
                viewRefusal: null)
            .Refusal.Should().Be(DatabaseRefusal.StorePolicy);

        // This store's own projection table is this store's grant's business.
        gate.RowsFor(Relation(ReportingSchema, "flat_orders"), gate.Classifier.ClassifyRelation(ReportingSchema, "flat_orders")!, viewRefusal: null)
            .Allowed.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // F6 / F10 - exact counts, and the policy the gate names
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_overview_counts_are_the_count_querys_whatever_a_list_holds()
    {
        DatabaseGate gate = Gate(entries: ["quartz"]);

        DatabaseBrowserOverview overview = DatabaseObjectAssembler.Overview(
            gate,
            [
                new CatalogObjectCount("tables", "quartz", 12_345),
                new CatalogObjectCount("sequences", "quartz", 7),
                new CatalogObjectCount("functions", DocumentSchema, 21),
                new CatalogObjectCount("tables", "legacy", 99),
            ]);

        overview.Truncated.Should().BeFalse("a count query is never capped");

        DatabaseSchemaSummary quartz = overview.Schemas.Single(static x => x.Name == "quartz");
        quartz.Tables.Should().Be(12_345, "more than any list's cap");
        quartz.Sequences.Should().Be(7);
        quartz.Views.Should().Be(0);

        overview.Schemas.Single(static x => x.Name == DocumentSchema).Functions.Should().Be(21);
        overview.Schemas.Select(static x => x.Name).Should().NotContain("legacy", "a count for a withheld schema is never shown");
    }

    /// <summary>
    /// F9: a per-tenant table's partition count and the rolled-up per-tenant event sequences are both the
    /// number of tenants. The store's own schemas show structure without the capability; that number is not
    /// structure, so it is withheld while the gate is shut - the kind still says "partitioned".
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tenant_counts_are_shown_only_while_the_gate_is_open(bool open)
    {
        DatabaseGate gate = Gate(entries: ["*"], capability: open);

        CatalogSnapshot snapshot = Snapshot(
            relations: [Relation(EventSchema, "mt_events", kind: "p", partitions: 3)],
            sequences:
            [
                new(EventSchema, "mt_events_sequence", "bigint", 1, 1, 1, long.MaxValue, false, true, null, null, null, null, null),
                new(EventSchema, "mt_events_sequence_acme", "bigint", 1, 1, 1, long.MaxValue, false, true, null, null, null, null, null),
                new(EventSchema, "mt_events_sequence_globex", "bigint", 1, 1, 1, long.MaxValue, false, true, null, null, null, null, null),
            ]);

        DatabaseRelationSummary events = (DatabaseRelationSummary) DatabaseObjectAssembler
            .Summaries(gate, snapshot, DatabaseObjectCategory.Tables).Single();

        events.Kind.Should().Be(DatabaseObjectKind.PartitionedTable, "that it is partitioned is structure");
        events.PartitionCount.Should().Be(open ? 3 : null);

        DatabaseSequenceSummary sequence = DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Sequences)
            .Cast<DatabaseSequenceSummary>()
            .Should().ContainSingle("the per-tenant sequences are never listed").Which;

        sequence.RolledUp.Should().Be(open ? 2 : null);
    }

    [Fact]
    public void A_gate_shut_by_the_store_policy_says_so()
    {
        DatabaseGate gate = new(StoreSchemas(), Live, ["*"], true, false, false, Classifier(), null, DatabaseRefusal.StorePolicy);

        gate.State.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        gate.State.Denial.Should().Be(DatabaseGate.StorePolicyDenial);
        gate.DataAccess("quartz").Refusal.Should().Be(DatabaseRefusal.StorePolicy);
    }

    // ---------------------------------------------------------------------------------------------

    private static DatabaseGate Gate(
        IReadOnlyList<string> entries,
        bool capability = true,
        DatabaseObjectClassifier? classifier = null,
        string? storeKey = null,
        IReadOnlyDictionary<string, DatabaseStoreAccess>? otherStores = null) =>
        new(StoreSchemas(), Live, entries, capability, false, capability ? true : null, classifier ?? Classifier(), null,
            DatabaseRefusal.WritePolicy, storeKey, otherStores);

    private static CatalogSnapshot Snapshot(
        IReadOnlyList<CatalogRelation>? relations = null,
        IReadOnlyList<CatalogRoutine>? routines = null,
        IReadOnlyList<CatalogTrigger>? triggers = null,
        IReadOnlyList<CatalogForeignKey>? foreignKeys = null,
        IReadOnlyList<CatalogViewDependency>? dependencies = null,
        IReadOnlyList<CatalogViewReference>? references = null,
        IReadOnlyList<CatalogSequence>? sequences = null) =>
        new(
            new CatalogList<CatalogRelation>(relations ?? [], false),
            new CatalogList<CatalogRoutine>(routines ?? [], false),
            new CatalogList<CatalogTrigger>(triggers ?? [], false),
            new CatalogList<CatalogSequence>(sequences ?? [], false),
            CatalogList<CatalogType>.Empty,
            new CatalogList<CatalogForeignKey>(foreignKeys ?? [], false),
            new CatalogList<CatalogViewDependency>(dependencies ?? [], false))
        {
            ViewReferences = new CatalogList<CatalogViewReference>(references ?? [], false),
        };

    private static string Serialized(DatabaseObjectDetail detail) =>
        System.Text.Json.JsonSerializer.Serialize(detail);
}
