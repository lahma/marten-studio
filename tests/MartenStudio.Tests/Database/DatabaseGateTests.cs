using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Acceptance 5: the gate's levels, applied to catalog rows by the assembler - no database.
/// </summary>
/// <remarks>
/// The snapshot below deliberately carries rows from schemas the gate does not admit, as a cached read
/// made for a wider schema set would. The assembler must drop them itself; the catalog's own filter is
/// the first line, and this is the second.
/// </remarks>
public class DatabaseGateTests
{
    private static readonly CatalogSnapshot Snapshot = new(
        new CatalogList<CatalogRelation>(
        [
            Relation(DocumentSchema, "mt_doc_dbcustomer"),
            Relation(DocumentSchema, "mt_doc_dbsecret"),
            Relation(DocumentSchema, "host_notes"),
            Relation(DocumentSchema, "host_view", kind: "v"),
            Relation(EventSchema, "mt_events"),
            Relation(ReportingSchema, "flat_orders"),
            Relation("quartz", "qrtz_triggers"),
            Relation("quartz", "qrtz_remote", kind: "f", foreignServer: "archive"),
            Relation("quartz", "secret_view", kind: "v"),
            Relation("quartz", "bad\"name"),
            Relation("quartz", "measurements", kind: "p", partitions: 2),
            Relation("legacy", "orders"),
            Relation("secrets", "keys"),
        ],
            false),
        new CatalogList<CatalogRoutine>(
        [
            Routine(DocumentSchema, "mt_jsonb_patch"),
            Routine(DocumentSchema, "host_function"),
            Routine("quartz", "qrtz_cleanup"),
            Routine("quartz", "sum_of_things", kind: "a"),
            Routine("legacy", "recalculate"),
        ],
            false),
        new CatalogList<CatalogTrigger>(
        [
            new(DocumentSchema, "mt_doc_dbsecret", "secret_audit", 17, "O", "public", "audit", false),
            new("quartz", "qrtz_triggers", "qrtz_touch", 23, "D", "quartz", "touch", false),
            new("quartz", "qrtz_triggers", "qrtz_external", 5, "O", "secrets", "leak", false),
        ],
            false),
        new CatalogList<CatalogSequence>(
        [
            new(EventSchema, "mt_events_sequence", "bigint", 1, 1, 1, long.MaxValue, false, true, 42, null, null, null, null),
            new(EventSchema, "mt_events_sequence_acme", "bigint", 1, 1, 1, long.MaxValue, false, true, 7, null, null, null, null),
            new(EventSchema, "mt_events_sequence_globex", "bigint", 1, 1, 1, long.MaxValue, false, true, 9, null, null, null, null),
            new("quartz", "qrtz_seq", "bigint", 1, 1, 1, long.MaxValue, false, true, 5, "legacy", "orders", "id", null),
        ],
            false),
        new CatalogList<CatalogType>(
        [
            new("quartz", "job_state", "e", null, false, null, ["waiting", "running"], [], [], null, 1, null),
            new(DocumentSchema, "host_status", "e", null, false, null, ["on", "off"], [], [], null, 0, null),
        ],
            false),
        new CatalogList<CatalogForeignKey>(
        [
            Key(DocumentSchema, "host_notes", DocumentSchema, "mt_doc_dbsecret"),
            Key(DocumentSchema, "host_notes", DocumentSchema, "mt_doc_dbcustomer"),
            Key("quartz", "qrtz_triggers", "secrets", "keys"),
            Key("legacy", "orders", "quartz", "qrtz_triggers"),
        ],
            false),
        new CatalogList<CatalogViewDependency>(
        [
            new("quartz", "secret_view", DocumentSchema, "mt_doc_dbsecret", "r", 2),
            new("quartz", "secret_view", "quartz", "qrtz_triggers", "r", 1),
        ],
            false));

    // ---------------------------------------------------------------------------------------------
    // No capability
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Without_the_capability_the_store_schemas_structure_is_visible_and_nothing_else()
    {
        DatabaseGate gate = Gate(capability: false, authorized: null, entries: ["*"]);

        gate.IsOpen.Should().BeFalse();
        gate.State.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        gate.State.Denial.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");
        gate.State.Authorized.Should().BeNull("nobody is asked about a capability the process does not have");

        gate.VisibleSchemas.Should().Equal(DocumentSchema, EventSchema, ReportingSchema);

        List<string> tables = Names(gate, DatabaseObjectCategory.Tables);

        tables.Should().Contain(DocumentSchema + ".host_notes", "the store's own schema shows structure without the capability");
        tables.Should().Contain(DocumentSchema + ".mt_doc_dbcustomer");
        tables.Should().NotContain(x => x.StartsWith("quartz.", StringComparison.Ordinal) || x.StartsWith("legacy.", StringComparison.Ordinal),
            "other schemas need the capability");
    }

    /// <summary>The withheld schemas are counted, and never named anywhere in what a page is given.</summary>
    [Fact]
    public void Without_the_capability_the_other_schemas_are_a_count_and_never_a_name()
    {
        DatabaseGate gate = Gate(capability: false, authorized: null, entries: ["*"]);

        DatabaseBrowserOverview overview = DatabaseObjectAssembler.Overview(gate, Snapshot);

        overview.Access.WithheldSchemaCount.Should().Be(3, "quartz, legacy and secrets exist; pg_catalog and information_schema are Postgres'");
        overview.Access.BrowsableSchemas.Should().BeEmpty();
        overview.Schemas.Select(static x => x.Name).Should().Equal(DocumentSchema, EventSchema, ReportingSchema);
        overview.Schemas.Should().OnlyContain(static x => x.IsStoreSchema && !x.RowsBrowsable);
    }

    [Fact]
    public void Without_the_capability_a_non_Marten_definition_in_the_store_schema_is_withheld_and_Martens_own_is_not()
    {
        DatabaseGate gate = Gate(capability: false, authorized: null, entries: ["*"]);

        var routines = DatabaseObjectAssembler.Summaries(gate, Snapshot, DatabaseObjectCategory.Functions)
            .Cast<DatabaseRoutineSummary>()
            .ToDictionary(static x => x.Name);

        routines["mt_jsonb_patch"].DefinitionAvailable.Should().BeTrue("the Schema screen already shows Marten's own");
        routines["host_function"].DefinitionAvailable.Should().BeFalse("the host's code is a BrowseDatabase read");

        var view = (DatabaseRelationSummary) DatabaseObjectAssembler.Summaries(gate, Snapshot, DatabaseObjectCategory.Views).Single();
        view.Name.Should().Be("host_view");
        view.DefinitionAvailable.Should().BeFalse();
        view.Rows.Allowed.Should().BeFalse();
        view.Rows.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);

        var type = (DatabaseTypeSummary) DatabaseObjectAssembler.Summaries(gate, Snapshot, DatabaseObjectCategory.Types).Single();
        type.DefinitionAvailable.Should().BeFalse();
        type.Labels.Should().BeEmpty("a type's description is a definition, gated like one");
    }

    [Fact]
    public void ReadOnly_and_a_policy_refusal_close_the_gate_and_say_which_setting_did()
    {
        DatabaseGate readOnly = Gate(capability: true, authorized: true, entries: ["*"], readOnly: true);
        readOnly.State.Refusal.Should().Be(DatabaseRefusal.ReadOnly);
        readOnly.State.Denial.Should().Contain("MartenStudioOptions.ReadOnly");
        readOnly.DataAccess("quartz").Refusal.Should().Be(DatabaseRefusal.ReadOnly);

        DatabaseGate refused = Gate(capability: true, authorized: false, entries: ["*"]);
        refused.State.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        refused.State.Denial.Should().Contain("WriteAuthorizationPolicy").And.Contain("no tenant");
        refused.VisibleSchemas.Should().Equal(DocumentSchema, EventSchema, ReportingSchema);
    }

    [Fact]
    public void An_empty_BrowsableSchemas_names_itself()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: []);

        gate.IsOpen.Should().BeTrue("the capability and the policy said yes");
        gate.State.Refusal.Should().Be(DatabaseRefusal.SchemaNotBrowsable);
        gate.State.Denial.Should().Contain("MartenStudioOptions.BrowsableSchemas is empty");
        gate.DataAccess(DocumentSchema).Refusal.Should().Be(DatabaseRefusal.SchemaNotBrowsable,
            "the store's own schemas give structure for free and nothing more");
    }

    // ---------------------------------------------------------------------------------------------
    // Capability on, one schema listed
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void With_the_capability_a_schema_BrowsableSchemas_does_not_admit_is_absent()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["quartz"]);

        gate.VisibleSchemas.Should().Equal(DocumentSchema, EventSchema, ReportingSchema, "quartz");
        gate.State.WithheldSchemaCount.Should().Be(2, "legacy and secrets");

        List<string> tables = Names(gate, DatabaseObjectCategory.Tables);
        tables.Should().Contain("quartz.qrtz_triggers");
        tables.Should().NotContain("legacy.orders").And.NotContain("secrets.keys");

        Names(gate, DatabaseObjectCategory.Functions).Should().NotContain("legacy.recalculate");

        gate.DataAccess("legacy").Refusal.Should().Be(DatabaseRefusal.SchemaNotBrowsable);
        gate.DataAccess("legacy").Reason.Should().Contain("MartenStudioOptions.BrowsableSchemas");
        gate.DataAccess("quartz").Allowed.Should().BeTrue();
    }

    [Fact]
    public void Marten_document_event_and_infrastructure_tables_are_never_row_allowed()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"]);

        var tables = Relations(gate, DatabaseObjectCategory.Tables);

        tables["mt_doc_dbcustomer"].Rows.Refusal.Should().Be(DatabaseRefusal.MartenOwned);
        tables["mt_doc_dbcustomer"].Rows.Reason.Should().Contain("Documents");
        tables["mt_events"].Rows.Refusal.Should().Be(DatabaseRefusal.MartenOwned);

        DatabaseRowAccess infrastructure = gate.RowsFor(
            Relation("tenants", "mt_tenant_databases"),
            gate.Classifier.ClassifyRelation("tenants", "mt_tenant_databases")!,
            dependsOnHidden: false);

        infrastructure.Allowed.Should().BeFalse("mt_tenant_databases holds tenant connection strings");
        infrastructure.Refusal.Should().Be(DatabaseRefusal.MartenOwned);

        tables["flat_orders"].Rows.Allowed.Should().BeTrue("a projection's table is the host's data");
        tables["qrtz_triggers"].Rows.Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_foreign_table_is_listed_with_its_server_and_never_row_allowed()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"]);

        DatabaseRelationSummary remote = Relations(gate, DatabaseObjectCategory.Tables)["qrtz_remote"];

        remote.Kind.Should().Be(DatabaseObjectKind.ForeignTable);
        remote.ForeignServer.Should().Be("archive");
        remote.Rows.Allowed.Should().BeFalse();
        remote.Rows.Refusal.Should().Be(DatabaseRefusal.ForeignTable);
        remote.Rows.Reason.Should().Contain("archive");
    }

    [Fact]
    public void A_view_over_a_hidden_types_table_is_listed_and_refused_its_rows()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"]);

        DatabaseRelationSummary view = Relations(gate, DatabaseObjectCategory.Views)["secret_view"];

        view.Rows.Allowed.Should().BeFalse();
        view.Rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency);
        view.Rows.Reason.Should().Contain("IsDocumentTypeVisible");
    }

    /// <summary>A hidden type's table, its triggers and the keys into it are absent from every list.</summary>
    [Fact]
    public void A_hidden_types_table_its_triggers_and_its_foreign_keys_are_absent()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"]);

        Names(gate, DatabaseObjectCategory.Tables).Should().NotContain(DocumentSchema + ".mt_doc_dbsecret");
        Names(gate, DatabaseObjectCategory.Triggers).Should().NotContain(DocumentSchema + ".secret_audit");

        DatabaseRelationSummary notes = Relations(gate, DatabaseObjectCategory.Tables)["host_notes"];
        notes.ForeignKeysOut.Should().Be(1, "the key into the hidden type's table is dropped, the one to the customer kept");

        CatalogRelationDetail detail = Detail(
            Relation(DocumentSchema, "host_notes"),
            foreignKeys:
            [
                Key(DocumentSchema, "host_notes", DocumentSchema, "mt_doc_dbsecret"),
                Key(DocumentSchema, "host_notes", DocumentSchema, "mt_doc_dbcustomer"),
            ]);

        DatabaseObjectDetail shown = DatabaseObjectAssembler.Detail(gate, detail);
        shown.ForeignKeysOut.Should().ContainSingle().Which.LinkedTable.Should().Be("mt_doc_dbcustomer");

        DatabaseObjectDetail hidden = DatabaseObjectAssembler.Detail(gate, Detail(Relation(DocumentSchema, "mt_doc_dbsecret")));
        hidden.Found.Should().BeFalse("a hidden type's table - and so its indexes - is not there for this visitor");
        hidden.Refusal.Should().Be(DatabaseRefusal.NotFound);
        hidden.Reason.Should().Be(DatabaseObjectAssembler.NotFound(DocumentSchema, "mt_doc_dbsecret"),
            "said exactly as for a relation that does not exist");
    }

    /// <summary>A far end outside the gate is not named: not a key's target, not a trigger's function.</summary>
    [Fact]
    public void Names_in_schemas_the_visitor_may_not_see_are_blanked_rather_than_shown()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["quartz"]);

        CatalogRelationDetail detail = Detail(
            Relation("quartz", "qrtz_triggers"),
            foreignKeys:
            [
                Key("quartz", "qrtz_triggers", "secrets", "keys"),
                Key("legacy", "orders", "quartz", "qrtz_triggers"),
            ]);

        DatabaseObjectDetail shown = DatabaseObjectAssembler.Detail(gate, detail);

        DatabaseForeignKeyInfo outbound = shown.ForeignKeysOut.Single();
        outbound.LinkedVisible.Should().BeFalse();
        outbound.LinkedSchema.Should().BeNull();
        outbound.LinkedTable.Should().BeNull();
        outbound.LinkedColumns.Should().BeEmpty();

        shown.ForeignKeysIn.Should().BeEmpty("a key from a withheld schema is not this visitor's to know about");

        var triggers = DatabaseObjectAssembler.Summaries(gate, Snapshot, DatabaseObjectCategory.Triggers)
            .Cast<DatabaseTriggerSummary>()
            .ToDictionary(static x => x.Name);

        triggers["qrtz_external"].FunctionName.Should().BeNull();
        triggers["qrtz_external"].FunctionSchema.Should().BeNull();
        triggers["qrtz_touch"].FunctionName.Should().Be("touch");

        var sequence = (DatabaseSequenceSummary) DatabaseObjectAssembler
            .Summaries(gate, Snapshot, DatabaseObjectCategory.Sequences)
            .Single(static x => x.Name == "qrtz_seq");

        sequence.OwnerTable.Should().BeNull();
        sequence.OwnedOutsideView.Should().BeTrue();
    }

    [Fact]
    public void A_name_that_cannot_be_quoted_is_listed_and_never_read()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"]);

        DatabaseRelationSummary bad = Relations(gate, DatabaseObjectCategory.Tables)["bad\"name"];

        bad.Quotable.Should().BeFalse();
        bad.Rows.Allowed.Should().BeFalse();
        bad.Rows.Refusal.Should().Be(DatabaseRefusal.Unquotable);
    }

    [Fact]
    public void A_relation_the_role_cannot_select_from_says_which_role()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"], role: "studio_reader");

        DatabaseRowAccess rows = gate.RowsFor(
            Relation("quartz", "locked_down", readable: false),
            gate.Classifier.ClassifyRelation("quartz", "locked_down")!,
            dependsOnHidden: false);

        rows.Refusal.Should().Be(DatabaseRefusal.NoPrivilege);
        rows.Reason.Should().Contain("studio_reader").And.Contain("MartenStudioOptions.SqlConsoleRole");
    }

    [Fact]
    public void A_partitioned_table_is_listed_once_with_its_partitions_counted()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"]);

        DatabaseRelationSummary measurements = Relations(gate, DatabaseObjectCategory.Tables)["measurements"];

        measurements.Kind.Should().Be(DatabaseObjectKind.PartitionedTable);
        measurements.PartitionCount.Should().Be(2);
    }

    [Fact]
    public void The_per_tenant_event_sequences_are_counted_into_the_global_one_and_never_listed()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"]);

        var sequences = DatabaseObjectAssembler.Summaries(gate, Snapshot, DatabaseObjectCategory.Sequences)
            .Cast<DatabaseSequenceSummary>()
            .ToList();

        sequences.Select(static x => x.Name).Should().NotContain(x => x.StartsWith("mt_events_sequence_", StringComparison.Ordinal));
        sequences.Single(static x => x.Name == "mt_events_sequence").RolledUp.Should().Be(2);
    }

    [Fact]
    public void An_aggregate_never_offers_a_definition()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"]);

        DatabaseRoutineSummary aggregate = DatabaseObjectAssembler.Summaries(gate, Snapshot, DatabaseObjectCategory.Functions)
            .Cast<DatabaseRoutineSummary>()
            .Single(static x => x.Name == "sum_of_things");

        aggregate.Kind.Should().Be(DatabaseObjectKind.Aggregate);
        aggregate.DefinitionAvailable.Should().BeFalse("pg_get_functiondef refuses aggregates");
    }

    [Fact]
    public void A_list_is_filtered_by_owner_and_counts_both_owners()
    {
        DatabaseGate gate = Gate(capability: true, authorized: true, entries: ["*"]);

        DatabaseObjectList others = DatabaseObjectAssembler.List(
            gate,
            Snapshot,
            new DatabaseObjectQuery(DatabaseObjectCategory.Tables, Owner: DatabaseOwnerFilter.Other),
            limit: 100);

        others.Items.Should().OnlyContain(static x => !x.Ownership.IsMarten);
        others.Counts.Marten.Should().BeGreaterThan(0);
        others.Counts.All.Should().Be(others.Counts.Marten + others.Counts.Other);
        others.Truncated.Should().BeFalse();

        DatabaseObjectList one = DatabaseObjectAssembler.List(
            gate, Snapshot, new DatabaseObjectQuery(DatabaseObjectCategory.Tables), limit: 1);

        one.Items.Should().ContainSingle();
        one.Truncated.Should().BeTrue("more matched than the limit");
    }

    // ---------------------------------------------------------------------------------------------
    // Row keys
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_row_key_is_the_primary_key_first()
    {
        CatalogRelationDetail detail = Detail(
            Relation("quartz", "qrtz_triggers"),
            columns: [Column("sched_name"), Column("trigger_name"), Column("trigger_group")],
            indexes:
            [
                new("qrtz_triggers_pkey", "…", IsPrimary: true, IsUnique: true, IsValid: true, false, false,
                    ["sched_name", "trigger_name", "trigger_group"]),
            ]);

        DatabaseRowKey key = DatabaseObjectAssembler.RowKey(detail)!;

        key.Source.Should().Be(DatabaseRowKeySource.PrimaryKey);
        key.Columns.Should().Equal("sched_name", "trigger_name", "trigger_group");
    }

    [Fact]
    public void Without_a_primary_key_the_narrowest_usable_unique_index_is_the_key_and_nothing_less_will_do()
    {
        CatalogRelationDetail detail = Detail(
            Relation("legacy", "codes"),
            columns: [Column("a"), Column("b"), Column("maybe", notNull: false)],
            indexes:
            [
                new("wide_unique", "…", false, true, true, false, false, ["a", "b"]),
                new("narrow_unique", "…", false, true, true, false, false, ["a"]),
                new("nullable_unique", "…", false, true, true, false, false, ["maybe"]),
                new("partial_unique", "…", false, true, true, HasPredicate: true, false, ["b"]),
                new("expression_unique", "…", false, true, true, false, HasExpressions: true, [null]),
                new("invalid_unique", "…", false, true, IsValid: false, false, false, ["b"]),
            ]);

        DatabaseRowKey key = DatabaseObjectAssembler.RowKey(detail)!;

        key.Source.Should().Be(DatabaseRowKeySource.UniqueIndex);
        key.IndexName.Should().Be("narrow_unique");
        key.Columns.Should().Equal("a");

        DatabaseObjectAssembler.RowKey(Detail(
                Relation("legacy", "loose"),
                columns: [Column("maybe", notNull: false)],
                indexes: [new("nullable_unique", "…", false, true, true, false, false, ["maybe"])]))
            .Should().BeNull("a unique index over a nullable column does not identify a row");
    }

    // ---------------------------------------------------------------------------------------------

    private static DatabaseGate Gate(
        bool capability,
        bool? authorized,
        IReadOnlyList<string> entries,
        bool readOnly = false,
        string? role = null) =>
        new(StoreSchemas(), LiveSchemas(), entries, capability, readOnly, authorized, Classifier(), role);

    private static List<string> Names(DatabaseGate gate, DatabaseObjectCategory category) =>
        [.. DatabaseObjectAssembler.Summaries(gate, Snapshot, category).Select(static x => x.Schema + "." + x.Name)];

    private static Dictionary<string, DatabaseRelationSummary> Relations(DatabaseGate gate, DatabaseObjectCategory category) =>
        DatabaseObjectAssembler.Summaries(gate, Snapshot, category)
            .Cast<DatabaseRelationSummary>()
            .ToDictionary(static x => x.Name, StringComparer.Ordinal);

    private static CatalogRoutine Routine(string schema, string name, string kind = "f") =>
        new(schema, name, string.Empty, kind, "integer", "sql", "v", false, [], null);

    private static CatalogForeignKey Key(string schema, string table, string linkedSchema, string linkedTable) =>
        new(schema, table, ["ref_id"], linkedSchema, linkedTable, ["id"], table + "_" + linkedTable + "_fkey", true, "a", "a");

    private static CatalogColumn Column(string name, bool notNull = true) =>
        new(name, 1, "text", notNull, null, string.Empty, string.Empty, true, null);

    private static CatalogRelationDetail Detail(
        CatalogRelation relation,
        IReadOnlyList<CatalogColumn>? columns = null,
        IReadOnlyList<CatalogIndex>? indexes = null,
        IReadOnlyList<CatalogForeignKey>? foreignKeys = null) =>
        new(
            relation,
            columns ?? [Column("id")],
            [],
            indexes ?? [],
            [],
            new CatalogList<CatalogForeignKey>(foreignKeys ?? [], false),
            CatalogList<CatalogViewDependency>.Empty);
}
