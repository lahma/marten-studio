using Marten;
using Marten.Linq.SoftDeletes;
using Marten.Schema;

using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services.Relationships;

using Npgsql;

namespace MartenStudio.Integration.Tests.Relationships;

/// <summary>
/// The relationships graph and the inbound counts, against a real schema.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through <c>IRelationshipDataService</c> resolved from the studio's own container, so
/// the scope resolution and the authorization it runs first are part of every call. The raw SQL in this
/// class is only ever used to make the database disagree with the configuration - dropping a constraint
/// Marten declares, adding one it does not - because that disagreement is the whole subject of the
/// screen and there is no other way to create it.
/// </para>
/// <para>
/// Every test that mutates the schema puts it back, in a <c>finally</c>: the fixture is built once for
/// the class and the tests share it.
/// </para>
/// </remarks>
public class RelationshipsLiveTests(RelationshipsLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<RelationshipsLiveTests.Fixture>
{
    /// <summary>How many notes point at the busy customer: one past the cap, so the cap is reached.</summary>
    public const int BusyNoteCount = 1001;

    /// <summary>A note about a customer: a declared foreign key with enough rows to reach the cap.</summary>
    /// <remarks>
    /// The alias is explicit because the type is nested: Marten's default alias for a nested type is
    /// <c>declaringtype_typename</c>, which would put these tables under names no test could guess.
    /// </remarks>
    [DocumentAlias("refnote")]
    public class RefNote
    {
        /// <summary>The id.</summary>
        public Guid Id { get; set; }

        /// <summary>The customer, duplicated into <c>customer_id</c> by the foreign key.</summary>
        public Guid CustomerId { get; set; }

        /// <summary>Something to write down.</summary>
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// A note with a duplicated <c>order_id</c> column and <em>no</em> declared foreign key, so a test can
    /// add the constraint behind Marten's back and see it reported as one the configuration does not have.
    /// </summary>
    [DocumentAlias("loosenote")]
    public class LooseNote
    {
        /// <summary>The id.</summary>
        public Guid Id { get; set; }

        /// <summary>The order, in a real column that nothing constrains.</summary>
        public Guid OrderId { get; set; }
    }

    /// <summary>The demo store plus the two extra document types these tests need.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>The customer <see cref="BusyNoteCount" /> notes point at.</summary>
        public Guid BusyCustomerId { get; private set; }

        /// <summary>A customer with exactly one live order.</summary>
        public Guid OrderedCustomerId { get; private set; }

        /// <summary>A customer whose only order is soft-deleted.</summary>
        public Guid DeletedOrderCustomerId { get; private set; }

        /// <summary>The tenant whose invoices point at <see cref="InvoicedCustomerId" />.</summary>
        public string InvoiceTenantId { get; private set; } = string.Empty;

        /// <summary>A customer one tenant's invoice points at, and the other tenant's does not.</summary>
        public Guid InvoicedCustomerId { get; private set; }

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            options.Schema.For<RefNote>().ForeignKey<Customer>(x => x.CustomerId);
            options.Schema.For<LooseNote>().Duplicate(x => x.OrderId);
        }

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            await using (IQuerySession query = Marten.Store.QuerySession())
            {
                Customer customer = await query.Query<Customer>().OrderBy(x => x.Name).FirstAsync();
                BusyCustomerId = customer.Id;

                Order live = await query.Query<Order>().OrderBy(x => x.Reference).FirstAsync();
                OrderedCustomerId = live.CustomerId;

                // The seeder soft-deletes three of the fifteen orders, and those are exactly the rows the
                // inbound count has to leave out.
                Order deleted = await query.Query<Order>()
                    .Where(x => x.IsDeleted())
                    .OrderBy(x => x.Reference)
                    .FirstAsync();

                DeletedOrderCustomerId = deleted.CustomerId;
            }

            InvoiceTenantId = SampleStore.TenantIds[0];

            await using (IQuerySession tenant = Marten.Store.QuerySession(InvoiceTenantId))
            {
                Invoice invoice = await tenant.Query<Invoice>().OrderBy(x => x.Number).FirstAsync();
                InvoicedCustomerId = invoice.CustomerId;
            }

            List<RefNote> notes = [];
            for (int i = 0; i < BusyNoteCount; i++)
            {
                notes.Add(new RefNote
                {
                    Id = Guid.NewGuid(),
                    CustomerId = BusyCustomerId,
                    Text = "note " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            }

            await Marten.Store.BulkInsertAsync(notes);

            // Two tables no document type maps, with a foreign key between them: the "cannot be drawn"
            // case, which has to be listed rather than turned into anonymous nodes.
            await using NpgsqlConnection connection = await Postgres.OpenAsync();

            await using var command = new NpgsqlCommand(
                $"""
                 create table "{Schema}"."legacy_parent" (id uuid primary key);
                 create table "{Schema}"."legacy_child" (
                     id uuid primary key,
                     parent_id uuid constraint legacy_child_parent_fkey references "{Schema}"."legacy_parent" (id));
                 """,
                connection);

            await command.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task The_demo_stores_order_to_customer_key_is_declared_and_physical()
    {
        RelationshipGraph graph = await ReadAsync();

        graph.Error.Should().BeNull();

        RelationshipEdge edge = Edge(graph, "order", "customer");

        edge.Declared.Should().BeTrue("SampleStore declares ForeignKey<Customer>(x => x.CustomerId)");
        edge.Physical.Should().BeTrue("the migration applied it");
        edge.Column.Should().Be("customer_id");
        edge.Member.Should().Be("CustomerId");
        edge.State.Should().Be("declared and physical");
    }

    [PostgresFact]
    public async Task The_demo_stores_conjoined_invoice_to_customer_key_is_drawn_too()
    {
        RelationshipGraph graph = await ReadAsync();

        RelationshipEdge edge = Edge(graph, "invoice", "customer");

        edge.Declared.Should().BeTrue();
        edge.Physical.Should().BeTrue();
        edge.Column.Should().Be("customer_id",
            "a conjoined source pointing at a single-tenanted target keys on the id column alone");
    }

    [PostgresFact]
    public async Task Every_visible_document_type_is_a_node_and_no_subclass_is()
    {
        RelationshipGraph graph = await ReadAsync();

        graph.Nodes.Select(x => x.Alias).Should()
            .Contain(["customer", "order", "invoice", "vehicle", "product"]);

        graph.Nodes.Should().NotContain(x => x.Alias == "car",
            "a subclass shares the root's table and is listed on the root, not drawn beside it");

        graph.Nodes.Single(x => x.Alias == "vehicle").SubclassAliases.Should().NotBeEmpty();
        graph.Nodes.Single(x => x.Alias == "customer").Count.Should()
            .NotBe(MartenStudio.Internal.Sql.DocumentCount.Unavailable,
                "the diagram reads every node's reltuples in one grouped query - a freshly seeded table " +
                "may well be 'never analysed', but it is never 'could not be read'");
    }

    [PostgresFact]
    public async Task A_dropped_constraint_makes_the_key_declared_only()
    {
        await ExecuteAsync($"alter table \"{Schema}\".\"mt_doc_order\" drop constraint mt_doc_order_customer_id_fkey");

        try
        {
            RelationshipEdge edge = Edge(await ReadAsync(), "order", "customer");

            edge.Declared.Should().BeTrue();
            edge.Physical.Should().BeFalse("nothing in the database is enforcing it any more");
            edge.State.Should().Be("declared only");
        }
        finally
        {
            await ExecuteAsync(
                $"alter table \"{Schema}\".\"mt_doc_order\" add constraint mt_doc_order_customer_id_fkey " +
                $"foreign key (customer_id) references \"{Schema}\".\"mt_doc_customer\" (id)");
        }

        Edge(await ReadAsync(), "order", "customer").Physical.Should().BeTrue("the test put it back");
    }

    [PostgresFact]
    public async Task A_constraint_the_configuration_does_not_declare_is_physical_only()
    {
        await ExecuteAsync(
            $"alter table \"{Schema}\".\"mt_doc_loosenote\" add constraint loose_note_order_fkey " +
            $"foreign key (order_id) references \"{Schema}\".\"mt_doc_order\" (id)");

        try
        {
            RelationshipEdge edge = Edge(await ReadAsync(), "loosenote", "order");

            edge.Declared.Should().BeFalse("StoreOptions declares no foreign key on this type");
            edge.Physical.Should().BeTrue();
            edge.State.Should().Be("physical only");
            edge.ConstraintName.Should().Be("loose_note_order_fkey");
        }
        finally
        {
            await ExecuteAsync(
                $"alter table \"{Schema}\".\"mt_doc_loosenote\" drop constraint loose_note_order_fkey");
        }

        (await ReadAsync()).Edges.Should().NotContain(x => x.FromAlias == "loosenote");
    }

    [PostgresFact]
    public async Task A_key_between_tables_no_document_type_maps_is_listed_rather_than_drawn()
    {
        RelationshipGraph graph = await ReadAsync();

        graph.Nodes.Should().NotContain(x => x.Alias.Contains("legacy", StringComparison.Ordinal));
        graph.Edges.Should().NotContain(x => x.FromAlias.Contains("legacy", StringComparison.Ordinal));

        UnmatchedForeignKey unmatched = graph.Unmatched.Should()
            .ContainSingle(x => x.Name == "legacy_child_parent_fkey").Subject;

        unmatched.Physical.Should().BeTrue();
        unmatched.Declared.Should().BeFalse();
        unmatched.From.Should().Contain("legacy_child");
        unmatched.To.Should().Contain("legacy_parent");
    }

    /// <summary>
    /// The event store's own <c>mt_events → mt_streams</c> key is real, and its schema is one of the
    /// store's own, so a screen that reported every constraint it found would open on a list of Marten's
    /// bookkeeping that nobody can act on.
    /// </summary>
    [PostgresFact]
    public async Task Martens_own_event_store_constraints_are_not_listed()
    {
        RelationshipGraph graph = await ReadAsync();

        graph.Unmatched.Should().OnlyContain(x => x.Name == "legacy_child_parent_fkey");

        graph.Unmatched.Should().NotContain(
            x => x.From.Contains("mt_events", StringComparison.Ordinal)
                 || x.From.Contains("mt_streams", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Referenced_by_counts_the_orders_that_point_at_a_customer()
    {
        ReferencedBy referenced = await ReferencedByAsync(fixture.OrderedCustomerId.ToString());

        referenced.Error.Should().BeNull();

        ReferencedByEntry order = Entry(referenced, "order");

        order.Count.Should().Be(1, "the seeder gives this customer exactly one live order");
        order.IsCapped.Should().BeFalse();
        order.Member.Should().Be("CustomerId");
        order.Declared.Should().BeTrue();
        order.Physical.Should().BeTrue();
    }

    [PostgresFact]
    public async Task Referenced_by_leaves_out_soft_deleted_rows()
    {
        ReferencedBy referenced = await ReferencedByAsync(fixture.DeletedOrderCustomerId.ToString());

        Entry(referenced, "order").Count.Should().Be(0,
            "the only order pointing at this customer is soft-deleted, and the list this row links to " +
            "would not show it either");
    }

    [PostgresFact]
    public async Task Referenced_by_stops_at_the_cap_rather_than_scanning_the_whole_table()
    {
        ReferencedBy referenced = await ReferencedByAsync(fixture.BusyCustomerId.ToString());

        ReferencedByEntry notes = Entry(referenced, "refnote");

        notes.IsCapped.Should().BeTrue();
        notes.Count.Should().Be(referenced.Cap - 1, "the screen renders this as '1,000+'");
    }

    [PostgresFact]
    public async Task Referenced_by_applies_the_pointing_collections_tenant_predicate()
    {
        string id = fixture.InvoicedCustomerId.ToString();
        string otherTenant = SampleStore.TenantIds[1];

        ReferencedByEntry inTenant = Entry(await ReferencedByAsync(id, fixture.InvoiceTenantId), "invoice");
        ReferencedByEntry otherwise = Entry(await ReferencedByAsync(id, otherTenant), "invoice");
        ReferencedByEntry everyone = Entry(await ReferencedByAsync(id), "invoice");

        inTenant.Count.Should().BeGreaterThan(0);
        otherwise.Count.Should().Be(0,
            "the tenant predicate comes from the pointing collection's own tenancy, and the other " +
            "tenant's invoices are not this tenant's to count");
        everyone.Count.Should().Be(inTenant.Count, "a scope with no tenant counts every tenant's rows");
    }

    [PostgresFact]
    public async Task Referenced_by_says_nothing_about_a_document_nothing_points_at()
    {
        ReferencedBy referenced = await ReferencedByAsync(Guid.NewGuid().ToString(), alias: "product");

        referenced.Entries.Should().BeEmpty("no document type has a foreign key to product");
    }

    [PostgresFact]
    public async Task A_malformed_id_is_reported_on_the_row_rather_than_thrown()
    {
        ReferencedBy referenced = await ReferencedByAsync("not-a-guid");

        referenced.Error.Should().BeNull("one row that cannot be counted must not blank the panel");
        referenced.Entries.Should().NotBeEmpty();
        referenced.Entries.Should().OnlyContain(x => x.Error != null);
    }

    // -----------------------------------------------------------------------------------------------

    private async Task<RelationshipGraph> ReadAsync(string? tenantId = null)
    {
        using MartenFixture.ScopedService<IRelationshipDataService> service =
            Marten.Resolve<IRelationshipDataService>();

        return await service.Service.GetGraphAsync(
            MartenFixture.ScopeFor(tenantId), TestContext.Current.CancellationToken);
    }

    private async Task<ReferencedBy> ReferencedByAsync(
        string id,
        string? tenantId = null,
        string alias = "customer")
    {
        using MartenFixture.ScopedService<IRelationshipDataService> service =
            Marten.Resolve<IRelationshipDataService>();

        return await service.Service.GetReferencedByAsync(
            MartenFixture.ScopeFor(tenantId), alias, id, TestContext.Current.CancellationToken);
    }

    private static RelationshipEdge Edge(RelationshipGraph graph, string from, string to) =>
        graph.Edges.Should().ContainSingle(x => x.FromAlias == from && x.ToAlias == to).Subject;

    private static ReferencedByEntry Entry(ReferencedBy referenced, string alias) =>
        referenced.Entries.Should().ContainSingle(x => x.FromAlias == alias).Subject;

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
