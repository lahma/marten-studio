using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Relationships;
using MartenStudio.Tests.Database;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// The rules that put tables on the relationships graph, and the per-visitor filter that decides which.
/// </summary>
/// <remarks>
/// <para>
/// Against the database browser's own test stores (<see cref="DatabaseTestStores" />) and a real
/// <see cref="DatabaseGate" />: a visible and a hidden document type in <c>studio_db</c>, an event store,
/// a flat-table projection and an extended table in <c>reporting</c>, and <c>quartz</c>, <c>legacy</c> and
/// <c>secrets</c> beside them. The keys are constructed outright, as <c>pg_constraint</c> would hand them
/// over.
/// </para>
/// <para>
/// The load-bearing assertions are the ones about what a visitor is <em>not</em> told: a table in a schema
/// the gate does not admit is never a node, never an unmatched row's end, and a key reaching one is a
/// number with no name attached; a key into a hidden type is not even that.
/// </para>
/// </remarks>
public class RelationshipTablesBuilderTests
{
    private const string Doc = DatabaseTestStores.DocumentSchema;
    private const string CustomerTable = "mt_doc_dbcustomer";
    private const string SecretTable = "mt_doc_dbsecret";

    private static readonly DateTimeOffset ReadAt = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Tables_come_only_from_schemas_the_visitor_may_see()
    {
        RelationshipGraph graph = Build(
            [
                Quartz("qrtz_triggers", ["sched_name", "job_name", "job_group"], "qrtz_job_details"),
                Key("secrets", "ledger", ["vault_id"], "secrets", "vault"),
            ],
            Open("quartz"),
            relations:
            [
                DatabaseTestStores.Relation("quartz", "qrtz_calendars"),
                DatabaseTestStores.Relation("secrets", "vault"),
            ]);

        graph.Tables.Select(static x => x.QualifiedName).Should().BeEquivalentTo(
            ["quartz.qrtz_calendars", "quartz.qrtz_job_details", "quartz.qrtz_triggers"]);

        graph.Edges.Should().ContainSingle();
        graph.Withheld.Should().BeEmpty(
            "a key between two schemas this visitor is not shown has nothing to do with anything they see");
        graph.VisibleSchemas.Should().Contain("quartz").And.NotContain("secrets");
    }

    [Fact]
    public void A_key_from_a_table_into_a_document_type_is_an_edge_rather_than_an_unmatched_row()
    {
        RelationshipGraph graph = Build(
            [
                Key("legacy", "customer_credit", ["customer_id"], Doc, CustomerTable, onDelete: "Cascade",
                    primaryKey: ["customer_id"], linkedPrimaryKey: ["id"]),
            ],
            Open("legacy"));

        RelationshipEdge edge = graph.Edges.Should().ContainSingle().Subject;

        edge.FromAlias.Should().Be(RelationshipTableNode.KeyFor("legacy", "customer_credit"));
        edge.ToAlias.Should().Be("dbcustomer");
        edge.FromIsTable.Should().BeTrue();
        edge.ToIsTable.Should().BeFalse();
        edge.OnDelete.Should().Be("Cascade");
        edge.State.Should().Be("enforced");
        RelationshipViews.StyleOf(edge).Should().Be("table");

        graph.Unmatched.Should().BeEmpty("a key between a document and a table is drawable now");
    }

    /// <summary>
    /// A key on a document table is Marten's to manage whatever it points at, so it keeps the drift styles:
    /// one nobody declared is "physical only", because an apply would drop it.
    /// </summary>
    [Fact]
    public void A_key_on_a_document_table_into_a_table_keeps_the_declared_and_physical_drift_styles()
    {
        RelationshipGraph graph = Build(
            [Key(Doc, CustomerTable, ["account_code"], "legacy", "accounts", primaryKey: ["id"], linkedPrimaryKey: ["code"])],
            Open("legacy"));

        RelationshipEdge edge = graph.Edges.Should().ContainSingle().Subject;

        edge.FromAlias.Should().Be("dbcustomer");
        edge.ToIsTable.Should().BeTrue();
        edge.OnDocumentTable.Should().BeTrue();
        edge.State.Should().Be("physical only");
        RelationshipViews.StyleOf(edge).Should().Be("physical");
    }

    [Fact]
    public void A_declared_key_into_a_table_is_drawn_and_merges_with_its_constraint()
    {
        IReadOnlyStoreOptions options = DatabaseTestStores.DefaultStore(static o =>
            o.Schema.For<DbCustomer>().ForeignKey(x => x.Name, "legacy", "accounts", "code"));

        RelationshipGraph declaredOnly = Build(
            [],
            Open("legacy"),
            relations: [DatabaseTestStores.Relation("legacy", "accounts")],
            storeOptions: options);

        RelationshipEdge declared = declaredOnly.Edges.Should().ContainSingle().Subject;
        declared.ToAlias.Should().Be(RelationshipTableNode.KeyFor("legacy", "accounts"));
        declared.State.Should().Be("declared only", "the configuration has it and the database does not");
        declared.Member.Should().Be("Name");

        RelationshipGraph both = Build(
            [Key(Doc, CustomerTable, ["name"], "legacy", "accounts", linkedColumns: ["code"], name: CustomerTable + "_name_fkey")],
            Open("legacy"),
            storeOptions: options);

        both.Edges.Should().ContainSingle().Which.State.Should().Be("declared and physical",
            "the declared half and the constraint are one key");

        RelationshipGraph missing = Build([], Open("legacy"), storeOptions: options);

        missing.Edges.Should().BeEmpty();
        missing.Unmatched.Should().ContainSingle().Which.Reason.Should().Contain("does not find in the database");
    }

    [Fact]
    public void Keys_into_a_hidden_type_are_dropped_from_every_list_and_every_count()
    {
        RelationshipGraph graph = Build(
            [
                Key("legacy", "notes", ["secret_id"], Doc, SecretTable),
                Key("secrets", "audit", ["secret_id"], Doc, SecretTable),
                Key(Doc, SecretTable, ["customer_id"], Doc, CustomerTable),
            ],
            Open("legacy"));

        graph.Edges.Should().BeEmpty();
        graph.Unmatched.Should().BeEmpty("naming the hidden table in 'could not be drawn' is the gate leaking");
        graph.Withheld.Should().BeEmpty(
            "'a key from a schema you are not shown points at the hidden type' says the hidden type is there");
        graph.Nodes.Should().NotContain(static x => x.Alias == "dbsecret");
    }

    [Fact]
    public void A_visitor_without_the_capability_is_told_how_many_keys_reach_other_schemas_and_nothing_else()
    {
        RelationshipGraph graph = Build(
            [
                Key("legacy", "customer_credit", ["customer_id"], Doc, CustomerTable, onDelete: "Cascade"),
                Key(Doc, CustomerTable, ["account_code"], "legacy", "accounts"),
                Quartz("qrtz_triggers", ["sched_name", "job_name", "job_group"], "qrtz_job_details"),
            ],
            Closed(),
            relations: [DatabaseTestStores.Relation("legacy", "customer_credit")]);

        graph.Tables.Should().BeEmpty();
        graph.Edges.Should().BeEmpty();
        graph.Unmatched.Should().BeEmpty();

        graph.Withheld.Should().BeEquivalentTo(
        [
            new WithheldForeignKey("dbcustomer", VisibleEndIsTarget: true),
            new WithheldForeignKey("dbcustomer", VisibleEndIsTarget: false),
        ]);

        graph.VisibleSchemas.Should().NotContain(["legacy", "quartz"]);
    }

    [Fact]
    public void Unmatched_names_only_what_the_visitor_may_see()
    {
        RelationshipGraph graph = Build(
            [
                Key(Doc, "mt_doc_discovered", ["ref"], "legacy", "accounts"),
                Key(Doc, "mt_doc_discovered", ["customer_id"], Doc, CustomerTable, name: "discovered_customer_fkey"),
            ],
            Closed());

        UnmatchedForeignKey unmatched = graph.Unmatched.Should().ContainSingle().Subject;

        unmatched.Name.Should().Be("discovered_customer_fkey",
            "an unmapped document table in the store's own schema is reportable, as it always was");

        graph.Unmatched.Should().NotContain(
            static x => x.From.Contains("legacy", StringComparison.Ordinal) || x.To.Contains("legacy", StringComparison.Ordinal),
            "a schema this visitor is not shown is never named, not even as the far end of a key nobody can draw");

        graph.Withheld.Should().ContainSingle().Which.VisibleEnd.Should().BeNull(
            "the visible end is not a node, and the far end is not named");
    }

    /// <summary>
    /// The shared read is one list handed to every visitor's build, and each build filters it for that
    /// visitor alone - which is the whole of what makes it safe to cache that list per database.
    /// </summary>
    [Fact]
    public void Two_visitors_with_different_gates_get_different_graphs_from_one_shared_read()
    {
        IReadOnlyList<PhysicalForeignKey> shared =
        [
            Key("legacy", "customer_credit", ["customer_id"], Doc, CustomerTable, onDelete: "Cascade"),
            Key("legacy", "purchase_order_lines", ["order_id"], "legacy", "purchase_orders", validated: false),
        ];

        PhysicalForeignKey[] before = [.. shared];

        RelationshipGraph admitted = Build(shared, Open("legacy"));
        RelationshipGraph refused = Build(shared, Closed());

        admitted.Tables.Select(static x => x.Name).Should().Contain(["customer_credit", "purchase_order_lines", "purchase_orders"]);
        admitted.Edges.Should().HaveCount(2);
        admitted.Withheld.Should().BeEmpty();

        refused.Tables.Should().BeEmpty();
        refused.Edges.Should().BeEmpty();
        refused.Withheld.Should().ContainSingle(
            "one of the two keys touches something the refused visitor sees - the customer - and the other does not");

        shared.Should().Equal(before, "building a visitor's graph must not change the read another visitor is handed");
    }

    [Fact]
    public void A_flat_table_projection_is_a_table_node_marked_as_marten_managed()
    {
        RelationshipGraph graph = Build(
            [Key(DatabaseTestStores.ReportingSchema, "flat_orders", ["customer_id"], Doc, CustomerTable)],
            Closed());

        RelationshipTableNode table = graph.Tables.Should().ContainSingle().Subject;

        table.Name.Should().Be("flat_orders");
        table.Owner.Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
        table.OwnerHint.Should().Be("Marten-managed");
        table.InStoreSchema.Should().BeTrue("the store declares the schema, so its structure needs no capability");
    }

    [Fact]
    public void The_event_store_and_martens_bookkeeping_are_never_nodes_or_counted()
    {
        RelationshipGraph graph = Build(
            [
                Key(DatabaseTestStores.EventSchema, "mt_events", ["stream_id"], DatabaseTestStores.EventSchema, "mt_streams"),
                Key("legacy", "hilo_audit", ["seq"], Doc, "mt_hilo"),
                Key("secrets", "x", ["stream_id"], DatabaseTestStores.EventSchema, "mt_streams"),
            ],
            Open("legacy"));

        graph.Tables.Select(static x => x.Name).Should().NotContain(static x => x.StartsWith("mt_", StringComparison.Ordinal));
        graph.Edges.Should().BeEmpty();
        graph.Unmatched.Should().BeEmpty();
        graph.Withheld.Should().BeEmpty();
    }

    [Fact]
    public void A_not_valid_key_is_carried_on_its_edge_and_drawn_dashed()
    {
        RelationshipGraph graph = Build(
            [Key("legacy", "purchase_order_lines", ["order_id"], "legacy", "purchase_orders", validated: false)],
            Open("legacy"));

        RelationshipEdge edge = graph.Edges.Should().ContainSingle().Subject;

        edge.Validated.Should().BeFalse();
        edge.State.Should().Be("not valid");
        RelationshipViews.StyleOf(edge).Should().Be("notvalid");
        RelationshipDiagram.EdgeClass(edge).Should().Be("ms-graph-edge-notvalid");
    }

    [Fact]
    public void Two_tables_whose_names_differ_only_by_case_are_two_nodes_and_two_edges()
    {
        RelationshipGraph graph = Build(
            [
                Key("legacy", "Orders", ["customer_id"], "legacy", "customers", name: "orders_upper_fkey"),
                Key("legacy", "orders", ["customer_id"], "legacy", "customers", name: "orders_lower_fkey"),
            ],
            Open("legacy"));

        graph.Tables.Select(static x => x.Name).Should().BeEquivalentTo(["Orders", "customers", "orders"]);
        graph.Edges.Should().HaveCount(2);
        graph.Edges.Select(static x => x.FromAlias).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// DB-6 review F6: a document's keys into <c>legacy.Accounts</c> and <c>legacy.accounts</c> were filed under
    /// one case-folded key, so one overwrote the other. A key into a table is filed by its constraint,
    /// ordinally, as a key on one always was.
    /// </summary>
    [Fact]
    public void A_documents_keys_into_two_tables_whose_names_differ_only_by_case_are_two_edges()
    {
        RelationshipGraph graph = Build(
            [
                Key(Doc, CustomerTable, ["account_code"], "legacy", "Accounts", linkedColumns: ["code"], name: "customer_upper_fkey"),
                Key(Doc, CustomerTable, ["account_code"], "legacy", "accounts", linkedColumns: ["code"], name: "customer_lower_fkey"),
            ],
            Open("legacy"));

        graph.Edges.Should().HaveCount(2, "legacy.Accounts and legacy.accounts are two tables");
        graph.Edges.Select(static x => x.ToAlias).Should().BeEquivalentTo(
            [RelationshipTableNode.KeyFor("legacy", "Accounts"), RelationshipTableNode.KeyFor("legacy", "accounts")]);
        graph.Edges.Select(static x => x.ConstraintName).Should().BeEquivalentTo(["customer_upper_fkey", "customer_lower_fkey"]);
        graph.Edges.Should().OnlyContain(static x => x.FromAlias == "dbcustomer" && x.ToIsTable);
    }

    /// <summary>
    /// The ordinal filing must not cost the declared half its merge: the declared key joins the constraint
    /// into the very table it resolved to, and only that one - in either order the constraints arrive.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_declared_key_merges_with_the_constraint_into_its_own_table_and_not_the_one_differing_by_case(bool upperFirst)
    {
        IReadOnlyStoreOptions options = DatabaseTestStores.DefaultStore(static o =>
            o.Schema.For<DbCustomer>().ForeignKey(x => x.Name, "legacy", "accounts", "code"));

        PhysicalForeignKey upper = Key(Doc, CustomerTable, ["name"], "legacy", "Accounts", linkedColumns: ["code"], name: "customer_upper_fkey");
        PhysicalForeignKey lower = Key(Doc, CustomerTable, ["name"], "legacy", "accounts", linkedColumns: ["code"], name: CustomerTable + "_name_fkey");

        RelationshipGraph graph = Build(upperFirst ? [upper, lower] : [lower, upper], Open("legacy"), storeOptions: options);

        graph.Edges.Should().HaveCount(2);
        graph.Edges.Single(static x => x.ToAlias == RelationshipTableNode.KeyFor("legacy", "accounts"))
            .State.Should().Be("declared and physical");
        graph.Edges.Single(static x => x.ToAlias == RelationshipTableNode.KeyFor("legacy", "Accounts"))
            .State.Should().Be("physical only", "nothing declares a key into the upper-case table");
    }

    /// <summary>
    /// An unmatched row carries both of its ends as the catalog or the configuration spells them, so the object
    /// page's neighbourhood can keep the rows of the table it is about - on either end, compared ordinally.
    /// </summary>
    [Fact]
    public void An_unmatched_row_carries_both_its_ends()
    {
        RelationshipGraph physical = Build(
            [Key(Doc, "mt_doc_discovered", ["customer_id"], Doc, CustomerTable, name: "discovered_customer_fkey")],
            Closed());

        UnmatchedForeignKey key = physical.Unmatched.Should().ContainSingle().Subject;

        (key.FromSchema, key.FromTable, key.ToSchema, key.ToTable).Should().Be((Doc, "mt_doc_discovered", Doc, CustomerTable));
        key.Touches(Doc, "mt_doc_discovered").Should().BeTrue();
        key.Touches(Doc, CustomerTable).Should().BeTrue();
        key.Touches(Doc, "MT_DOC_DISCOVERED").Should().BeFalse("two quoted names that differ by case are two tables");
        key.Touches("legacy", "mt_doc_discovered").Should().BeFalse();

        IReadOnlyStoreOptions options = DatabaseTestStores.DefaultStore(static o =>
            o.Schema.For<DbCustomer>().ForeignKey(x => x.Name, "legacy", "accounts", "code"));

        UnmatchedForeignKey declared = Build([], Open("legacy"), storeOptions: options).Unmatched.Should().ContainSingle().Subject;

        (declared.FromSchema, declared.FromTable, declared.ToSchema, declared.ToTable)
            .Should().Be((Doc, CustomerTable, "legacy", "accounts"), "a declared key's ends are the configuration's");
    }

    [Fact]
    public void A_table_node_carries_its_estimate_and_its_schemas_colour()
    {
        RelationshipGraph graph = Build(
            [Key("legacy", "customer_credit", ["customer_id"], Doc, CustomerTable)],
            Open("legacy"),
            relations: [DatabaseTestStores.Relation("legacy", "customer_credit")]);

        RelationshipTableNode table = graph.Tables.Should().ContainSingle().Subject;

        table.Count.Value.Should().Be(10);
        table.Count.IsEstimate.Should().BeTrue("the diagram never pays for a count(*) (D8)");
        table.Hue.Should().Be(RelationshipTableNode.SchemaHue("legacy"), "a table is coloured by its schema");
    }

    /// <summary>
    /// The sample's two browsable schemas share a slot of the ring as bare names, which drew every Quartz
    /// and every legacy box in one colour; hashed as schemas they do not.
    /// </summary>
    [Fact]
    public void The_samples_schemas_are_told_apart_by_colour()
    {
        CollectionColorizer.HueFor("quartz").Should().Be(CollectionColorizer.HueFor("legacy"),
            "the premise: as bare names the two collide");

        RelationshipTableNode.SchemaHue("quartz").Should().NotBe(RelationshipTableNode.SchemaHue("legacy"));
        RelationshipTableNode.SchemaHue("quartz").Should().NotBe(RelationshipTableNode.SchemaHue("studio_sample"));
        RelationshipTableNode.SchemaHue("legacy").Should().NotBe(RelationshipTableNode.SchemaHue("studio_sample"));
    }

    // ----------------------------------------------------------------------------------------------
    // The composite label
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_composite_key_is_labelled_without_the_leading_columns_both_primary_keys_share()
    {
        RelationshipGraphBuilder.LabelFor(
                ["sched_name", "job_name", "job_group"],
                ["sched_name", "job_name", "job_group"],
                ["sched_name", "trigger_name", "trigger_group"],
                ["sched_name", "job_name", "job_group"])
            .Should().Be("job_name, job_group");

        RelationshipGraphBuilder.LabelFor(
                ["tenant_id", "customer_id"],
                ["tenant_id", "id"],
                ["tenant_id", "invoice_id"],
                ["tenant_id", "id"])
            .Should().Be("customer_id", "a tenant column leading both keys is the scope, not the relationship");
    }

    /// <summary>
    /// UX-6: a detail table keyed by exactly its parent's key - Quartz.NET's four trigger-detail tables into
    /// <c>qrtz_triggers</c> - is a one-to-one extension of it. Its three columns said nothing the arrow did
    /// not, and were cut to <c>sched_name, trigger_name, trigg…</c> on every one of the four arrows.
    /// </summary>
    [Fact]
    public void A_key_that_is_both_whole_primary_keys_is_labelled_one_to_one()
    {
        RelationshipGraphBuilder.LabelFor(
                ["sched_name", "trigger_name", "trigger_group"],
                ["sched_name", "trigger_name", "trigger_group"],
                ["sched_name", "trigger_name", "trigger_group"],
                ["sched_name", "trigger_name", "trigger_group"])
            .Should().Be("primary key (1:1)");

        RelationshipGraphBuilder.LabelFor(["customer_id"], ["id"], ["customer_id"], ["id"])
            .Should().Be(RelationshipGraphBuilder.OneToOneLabel, "one column is still the whole of both keys");

        RelationshipGraphBuilder.LabelFor(["b", "a"], ["y", "x"], ["a", "b"], ["x", "y"])
            .Should().Be(RelationshipGraphBuilder.OneToOneLabel, "a key is a set of columns: the order they are declared in does not make it less whole");
    }

    [Fact]
    public void A_key_that_is_only_part_of_a_primary_key_is_not_one_to_one()
    {
        RelationshipGraphBuilder.LabelFor(
                ["sched_name", "job_name", "job_group"],
                ["sched_name", "job_name", "job_group"],
                ["sched_name", "trigger_name", "trigger_group"],
                ["sched_name", "job_name", "job_group"])
            .Should().Be("job_name, job_group", "the pointing side is not its table's key: many triggers share one job");

        RelationshipGraphBuilder.LabelFor(["customer_id"], ["id"], ["customer_id"], ["tenant_id", "id"])
            .Should().Be("customer_id", "the referenced side is not the whole of its table's key");

        RelationshipGraphBuilder.LabelFor(["customer_id"], ["id"], null, ["id"])
            .Should().Be("customer_id", "without the pointing table's key there is nothing to call one-to-one");
    }

    [Fact]
    public void A_label_that_would_strip_every_column_shows_them_all()
    {
        RelationshipGraphBuilder.LabelFor(
                ["sched_name", "trigger_name", "trigger_group"],
                ["sched_name", "trigger_name", "trigger_group"],
                ["sched_name", "trigger_name", "trigger_group", "fired_at"],
                ["sched_name", "trigger_name", "trigger_group"])
            .Should().Be("sched_name, trigger_name, trigger_group",
                "a key that shares the whole prefix without being both whole keys strips to nothing, and an empty label says nothing");
    }

    [Fact]
    public void A_label_strips_nothing_without_both_primary_keys_or_off_the_shared_prefix()
    {
        RelationshipGraphBuilder.LabelFor(["a", "b"], ["a", "b"], null, ["a", "b"]).Should().Be("a, b");
        RelationshipGraphBuilder.LabelFor(["a", "b"], ["a", "b"], ["a", "x"], null).Should().Be("a, b");
        RelationshipGraphBuilder.LabelFor(["b", "a"], ["b", "a"], ["a", "x"], ["a", "y"]).Should().Be("b, a",
            "the shared column is not where the key starts");
    }

    [Fact]
    public void The_builder_labels_a_table_edge_with_the_rule_and_keeps_every_column_beside_it()
    {
        RelationshipGraph graph = Build(
            [Quartz("qrtz_triggers", ["sched_name", "job_name", "job_group"], "qrtz_job_details")],
            Open("quartz"));

        RelationshipEdge edge = graph.Edges.Should().ContainSingle().Subject;

        edge.Column.Should().Be("job_name, job_group");
        edge.Columns.Should().Be("sched_name, job_name, job_group");
        edge.LinkedColumns.Should().Be("sched_name, job_name, job_group");
        RelationshipDiagram.Describe(graph, edge).Should().Contain("sched_name, job_name, job_group",
            "the tooltip always lists every column");
    }

    [Fact]
    public void A_detail_table_s_key_into_its_parent_is_drawn_one_to_one_and_the_tooltip_still_lists_every_column()
    {
        string[] triggerKey = ["sched_name", "trigger_name", "trigger_group"];

        RelationshipGraph graph = Build(
            [Key("quartz", "qrtz_simple_triggers", triggerKey, "quartz", "qrtz_triggers", primaryKey: triggerKey, linkedPrimaryKey: triggerKey)],
            Open("quartz"));

        RelationshipEdge edge = graph.Edges.Should().ContainSingle().Subject;

        edge.Column.Should().Be("primary key (1:1)");
        edge.AllColumns.Should().Be("sched_name, trigger_name, trigger_group", "the table lists every column beneath the label");
        RelationshipDiagram.Describe(graph, edge).Should().Contain("(sched_name, trigger_name, trigger_group) → quartz.qrtz_triggers (sched_name, trigger_name, trigger_group)",
            "the tooltip always lists every column on both sides");
    }

    // ----------------------------------------------------------------------------------------------

    private static RelationshipGraph Build(
        IReadOnlyList<PhysicalForeignKey> keys,
        DatabaseGate gate,
        IReadOnlyList<CatalogRelation>? relations = null,
        IReadOnlyStoreOptions? storeOptions = null) =>
        RelationshipGraphBuilder.Build(
            storeOptions ?? DatabaseTestStores.DefaultStore(),
            DatabaseTestStores.IsVisible,
            keys,
            new Dictionary<string, long>(StringComparer.Ordinal),
            ReadAt,
            new RelationshipDatabaseView(gate, relations ?? []));

    /// <summary>A visitor with the capability, the policy's yes, and these schemas browsable.</summary>
    private static DatabaseGate Open(params string[] browsable) =>
        new(
            DatabaseTestStores.StoreSchemas(),
            DatabaseTestStores.LiveSchemas(),
            browsable,
            capabilityEnabled: true,
            readOnly: false,
            authorized: true,
            DatabaseTestStores.Classifier());

    /// <summary>A visitor without the capability: the store's own schemas, and nothing else.</summary>
    private static DatabaseGate Closed() =>
        new(
            DatabaseTestStores.StoreSchemas(),
            DatabaseTestStores.LiveSchemas(),
            ["quartz", "legacy"],
            capabilityEnabled: false,
            readOnly: false,
            authorized: null,
            DatabaseTestStores.Classifier());

    private static PhysicalForeignKey Quartz(string table, string[] columns, string linkedTable) =>
        Key(
            "quartz",
            table,
            columns,
            "quartz",
            linkedTable,
            primaryKey: ["sched_name", "trigger_name", "trigger_group"],
            linkedPrimaryKey: ["sched_name", "job_name", "job_group"]);

    private static PhysicalForeignKey Key(
        string schema,
        string table,
        string[] columns,
        string linkedSchema,
        string linkedTable,
        string[]? linkedColumns = null,
        string? name = null,
        string onDelete = "NoAction",
        bool validated = true,
        string[]? primaryKey = null,
        string[]? linkedPrimaryKey = null) =>
        new(
            schema,
            table,
            columns,
            linkedSchema,
            linkedTable,
            linkedColumns ?? columns.Select(static x => x == "customer_id" || x == "secret_id" ? "id" : x).ToArray(),
            name ?? table + "_" + columns[0] + "_fkey",
            onDelete,
            "NoAction",
            validated,
            primaryKey,
            linkedPrimaryKey);
}
