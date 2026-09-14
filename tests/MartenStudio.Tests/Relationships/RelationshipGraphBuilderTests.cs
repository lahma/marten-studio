using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Relationships;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// The rules that turn <c>StoreOptions</c> plus <c>pg_constraint</c> into one diagram.
/// </summary>
/// <remarks>
/// <para>
/// Against real Marten mappings and no database: <c>DocumentStore.For</c> never opens a connection, so
/// the declared half of every assertion here is what Marten <em>actually</em> produces - the column name
/// it duplicates a member into, the two-column shape of a conjoined-to-conjoined key, the table a
/// subclass really belongs to - rather than what a hand-written fake would agree with. The physical half
/// is constructed outright, because that is the point: these tests are about what happens when the two
/// disagree.
/// </para>
/// <para>
/// The visibility assertions are the load-bearing ones. A hidden document type must not appear as a node,
/// as either end of an edge, or as a row in the list of keys that could not be drawn - the last of those
/// is the one that is easy to get wrong, because "I could not draw this key into studio_rel.mt_doc_secret"
/// names the very table the host hid.
/// </para>
/// </remarks>
public class RelationshipGraphBuilderTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_declared_key_the_database_does_not_have_is_declared_only()
    {
        RelationshipGraph graph = Build(physical: []);

        RelationshipEdge edge = Edge(graph, "relorder", "relcustomer");

        edge.Declared.Should().BeTrue();
        edge.Physical.Should().BeFalse();
        edge.State.Should().Be("declared only");
        edge.Column.Should().Be("customer_id");
        edge.Member.Should().Be("CustomerId", "the filter a link builds is written with the member, not the column");
        edge.OnDelete.Should().Be("NoAction");
    }

    [Fact]
    public void A_declared_key_the_database_has_is_both()
    {
        RelationshipGraph graph = Build([Physical("relorder", ["customer_id"], "relcustomer")]);

        RelationshipEdge edge = Edge(graph, "relorder", "relcustomer");

        edge.Declared.Should().BeTrue();
        edge.Physical.Should().BeTrue();
        edge.State.Should().Be("declared and physical");
    }

    [Fact]
    public void A_constraint_nobody_declared_is_physical_only()
    {
        RelationshipGraph graph = Build([Physical("relnote", ["order_id"], "relorder", "note_to_order_fkey")]);

        RelationshipEdge edge = Edge(graph, "relnote", "relorder");

        edge.Declared.Should().BeFalse();
        edge.Physical.Should().BeTrue();
        edge.State.Should().Be("physical only");
        edge.ConstraintName.Should().Be("note_to_order_fkey");
    }

    [Fact]
    public void A_constraint_into_a_table_no_document_type_maps_is_listed_rather_than_drawn()
    {
        RelationshipGraph graph = Build([Physical("relorder", ["legacy_id"], "legacy_customers", "legacy_fkey")]);

        graph.Edges.Should().NotContain(x => x.ToAlias.Contains("legacy", StringComparison.Ordinal));

        UnmatchedForeignKey unmatched = graph.Unmatched.Should().ContainSingle().Subject;

        unmatched.Name.Should().Be("legacy_fkey");
        unmatched.From.Should().Be("relorder");
        unmatched.To.Should().Be(Schema + ".legacy_customers");
        unmatched.Physical.Should().BeTrue();
        unmatched.Declared.Should().BeFalse();
        unmatched.Reason.Should().Contain("no document type of this store maps");
    }

    [Fact]
    public void A_constraint_on_a_table_no_document_type_maps_is_listed_too()
    {
        RelationshipGraph graph = Build([Physical("legacy_orders", ["customer_id"], "relcustomer", "legacy_source_fkey")]);

        UnmatchedForeignKey unmatched = graph.Unmatched.Should().ContainSingle().Subject;

        unmatched.From.Should().Be(Schema + ".legacy_orders");
        unmatched.To.Should().Be("relcustomer");
        unmatched.Reason.Should().Contain("It is on a table");
    }

    /// <summary>
    /// Marten's own event tables carry a real foreign key (<c>mt_events.stream_id → mt_streams</c>,
    /// <c>EventsTable</c>, Marten 9.35) and the event schema is one of the store's own, so a screen that
    /// reported every constraint it found would open on a list of Marten's bookkeeping.
    /// </summary>
    [Fact]
    public void Martens_own_event_store_constraints_are_not_reported_at_all()
    {
        RelationshipGraph graph = Build(
            [
                new PhysicalForeignKey(
                    Schema, "mt_events", ["stream_id"], Schema, "mt_streams", ["id"],
                    "fkey_mt_events_stream_id", "Cascade", "NoAction"),
            ]);

        graph.Unmatched.Should().BeEmpty();
        graph.Edges.Should().NotContain(x => x.FromAlias.Contains("mt_", StringComparison.Ordinal));
    }

    [Fact]
    public void A_foreign_key_on_a_discovered_document_table_is_still_reported()
    {
        RelationshipGraph graph = Build(
            [Physical("mt_doc_unmapped", ["customer_id"], "relcustomer", "unmapped_fkey")]);

        graph.Unmatched.Should().ContainSingle(x => x.Name == "unmapped_fkey",
            "a document table no mapping claims is a discovered collection, not Marten's bookkeeping");
    }

    [Fact]
    public void A_hidden_type_is_neither_a_node_nor_an_edge_end_nor_an_unmatched_row()
    {
        RelationshipGraph graph = Build(
            [
                Physical("relsecret", ["customer_id"], "relcustomer", "secret_out_fkey"),
                Physical("relnote", ["secret_id"], "relsecret", "secret_in_fkey"),
            ],
            visible: static type => type != typeof(RelSecret));

        graph.Nodes.Should().NotContain(x => x.Alias == "relsecret");

        graph.Edges.Should().NotContain(x => x.FromAlias == "relsecret" || x.ToAlias == "relsecret");

        graph.Unmatched.Should().NotContain(
            x => x.From.Contains("secret", StringComparison.OrdinalIgnoreCase)
                 || x.To.Contains("secret", StringComparison.OrdinalIgnoreCase),
            "naming the hidden table in the list of keys that could not be drawn is the visibility gate " +
            "leaking through the relationship it was set to hide");
    }

    [Fact]
    public void A_declared_key_pointing_at_a_hidden_type_disappears_entirely()
    {
        RelationshipGraph graph = Build(physical: [], visible: static type => type != typeof(RelSecret));

        // RelNote declares a key to RelSecret; with RelSecret hidden there is nothing to say about it.
        graph.Edges.Should().NotContain(x => x.FromAlias == "relnote");
        graph.Unmatched.Should().BeEmpty();
    }

    [Fact]
    public void Only_hierarchy_roots_are_nodes_and_the_root_carries_its_subclasses()
    {
        RelationshipGraph graph = Build(physical: []);

        graph.Nodes.Should().NotContain(x => x.Alias == "relcar");

        RelationshipNode vehicle = graph.Nodes.Should().ContainSingle(x => x.Alias == "relvehicle").Subject;

        vehicle.IsHierarchyRoot.Should().BeTrue();
        vehicle.DisplayName.Should().Be(nameof(RelVehicle));

        // Marten spells the two aliases differently and this is not a typo: a root's default alias is the
        // lowercased type name (DocumentMapping), while a subclass's is the type name split on camel case
        // and joined with underscores (SubClassMapping.GetTypeMartenAlias, Marten 9.35). The studio shows
        // whichever Marten would answer to, so the test pins Marten's answer rather than a guess at it.
        vehicle.SubclassAliases.Should().Equal(["rel_car"]);
    }

    [Fact]
    public void A_conjoined_key_is_labelled_with_the_column_it_is_about_and_not_with_tenant_id()
    {
        RelationshipGraph graph = Build(physical: []);

        RelationshipEdge edge = Edge(graph, "reltenantedorder", "reltenantedcustomer");

        edge.Column.Should().Be("customer_id",
            "a conjoined-to-conjoined key is (column, tenant_id) and the tenant half is the scope, not " +
            "the relationship");
    }

    /// <summary>
    /// The composite key matches the constraint whatever order Postgres reports its columns in — which is
    /// the order the constraint was created in, and not necessarily Marten's.
    /// </summary>
    [Fact]
    public void A_conjoined_key_matches_its_constraint_with_the_columns_the_other_way_round()
    {
        RelationshipGraph graph = Build(
            [Physical("reltenantedorder", ["tenant_id", "customer_id"], "reltenantedcustomer")]);

        RelationshipEdge edge = Edge(graph, "reltenantedorder", "reltenantedcustomer");

        edge.Declared.Should().BeTrue();
        edge.Physical.Should().BeTrue("the same two columns either way round are the same key");

        graph.Edges.Count(x => x.FromAlias == "reltenantedorder").Should().Be(1);
    }

    [Fact]
    public void A_node_carries_the_reltuples_estimate_and_says_nothing_about_a_table_nobody_analysed()
    {
        RelationshipGraph graph = Build(
            physical: [],
            estimates: new Dictionary<string, long>(StringComparer.Ordinal)
            {
                [Schema + ".mt_doc_relcustomer"] = 4_200,
                [Schema + ".mt_doc_relorder"] = -1,
            });

        RelationshipNode customer = graph.Nodes.Single(x => x.Alias == "relcustomer");
        customer.Count.Value.Should().Be(4_200);
        customer.Count.IsEstimate.Should().BeTrue("the diagram never pays for a count(*) (D8)");

        RelationshipNode order = graph.Nodes.Single(x => x.Alias == "relorder");
        order.Count.IsUnknown.Should().BeTrue("-1 is Postgres for 'never analysed', not for 'empty'");

        RelationshipNode note = graph.Nodes.Single(x => x.Alias == "relnote");
        note.Count.IsUnavailable.Should().BeTrue("the read reported nothing at all for this table");
    }

    [Fact]
    public void Nodes_and_edges_come_back_in_a_stable_order()
    {
        RelationshipGraph first = Build([Physical("relnote", ["order_id"], "relorder")]);
        RelationshipGraph second = Build([Physical("relnote", ["order_id"], "relorder")]);

        first.Nodes.Select(x => x.Alias).Should().BeInAscendingOrder(StringComparer.Ordinal);
        second.Nodes.Select(x => x.Alias).Should().Equal(first.Nodes.Select(x => x.Alias));
        second.Edges.Should().Equal(first.Edges);
    }

    [Fact]
    public void A_graph_with_no_keys_at_all_is_empty_rather_than_failed()
    {
        RelationshipGraph graph = RelationshipGraphBuilder.Build(
            Options(static options => options.Schema.For<RelCustomer>()),
            null,
            [],
            new Dictionary<string, long>(StringComparer.Ordinal),
            ReadAt);

        graph.IsEmpty.Should().BeTrue();
        graph.Error.Should().BeNull();
        graph.Nodes.Should().NotBeEmpty("a store with no foreign keys still has document types");
        graph.Summary.Should().Contain("no foreign keys");
        graph.ReadAt.Should().Be(ReadAt);
    }

    [Fact]
    public void A_failed_graph_says_so_in_its_summary()
    {
        RelationshipGraph graph = RelationshipGraph.Failed("57014: canceling statement", ReadAt);

        graph.Error.Should().Contain("57014");
        graph.Summary.Should().Contain("could not be read");
    }

    // -----------------------------------------------------------------------------------------------

    private const string Schema = "studio_rel";

    private static RelationshipGraph Build(
        IReadOnlyList<PhysicalForeignKey> physical,
        Func<Type, bool>? visible = null,
        IReadOnlyDictionary<string, long>? estimates = null) =>
        RelationshipGraphBuilder.Build(
            Options(),
            visible,
            physical,
            estimates ?? new Dictionary<string, long>(StringComparer.Ordinal),
            ReadAt);

    private static RelationshipEdge Edge(RelationshipGraph graph, string from, string to) =>
        graph.Edges.Should().ContainSingle(x => x.FromAlias == from && x.ToAlias == to).Subject;

    private static PhysicalForeignKey Physical(
        string fromTable,
        string[] columns,
        string toTable,
        string? name = null) =>
        new(
            Schema,
            Table(fromTable),
            columns,
            Schema,
            Table(toTable),
            ["id"],
            name ?? Table(fromTable) + "_" + columns[0] + "_fkey",
            "NoAction",
            "NoAction");

    /// <summary>
    /// A document alias becomes <c>mt_doc_&lt;alias&gt;</c>; anything already looking like a table name
    /// is left alone, so a test can name a table no mapping claims.
    /// </summary>
    private static string Table(string alias) =>
        alias.StartsWith("mt_doc_", StringComparison.Ordinal) || alias.Contains('_', StringComparison.Ordinal)
            ? alias
            : "mt_doc_" + alias;

    private static IReadOnlyStoreOptions Options(Action<StoreOptions>? configure = null)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection("Host=marten-studio-relationship-tests.invalid;Database=none;Username=none;Password=none");
            options.DatabaseSchemaName = Schema;

            if (configure is not null)
            {
                configure(options);
                return;
            }

            options.Schema.For<RelCustomer>();
            options.Schema.For<RelOrder>().ForeignKey<RelCustomer>(x => x.CustomerId);
            options.Schema.For<RelSecret>().ForeignKey<RelCustomer>(x => x.CustomerId);
            options.Schema.For<RelNote>().ForeignKey<RelSecret>(x => x.SecretId);
            options.Schema.For<RelTenantedCustomer>().MultiTenanted();
            options.Schema.For<RelTenantedOrder>()
                .MultiTenanted()
                .ForeignKey<RelTenantedCustomer>(x => x.CustomerId);
            options.Schema.For<RelVehicle>().AddSubClassHierarchy(typeof(RelCar));
        });

        return store.Options;
    }
}

/// <summary>The thing everything else points at.</summary>
public class RelCustomer
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }

    /// <summary>A name, so the type is not empty.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>Points at <see cref="RelCustomer" />.</summary>
public class RelOrder
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }

    /// <summary>The customer, duplicated into <c>customer_id</c> by the foreign key.</summary>
    public Guid CustomerId { get; set; }
}

/// <summary>A type the host hides, which points at one visible type and is pointed at by another.</summary>
public class RelSecret
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }

    /// <summary>The customer.</summary>
    public Guid CustomerId { get; set; }
}

/// <summary>Points at the hidden type.</summary>
public class RelNote
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }

    /// <summary>The secret this note is about.</summary>
    public Guid SecretId { get; set; }

    /// <summary>An order, for the physical-only case. Not declared as a foreign key.</summary>
    public Guid OrderId { get; set; }
}

/// <summary>A conjoined type, pointed at by another conjoined one.</summary>
public class RelTenantedCustomer
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A conjoined type pointing at a conjoined one: the two-column key case.</summary>
public class RelTenantedOrder
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }

    /// <summary>The customer.</summary>
    public Guid CustomerId { get; set; }
}

/// <summary>A hierarchy root.</summary>
public class RelVehicle
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A subclass, which has no table of its own.</summary>
public class RelCar : RelVehicle
{
    /// <summary>How many doors.</summary>
    public int Doors { get; set; }
}
