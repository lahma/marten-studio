using Marten;
using Marten.Linq.SoftDeletes;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services;
using MartenStudio.Services.Live;
using MartenStudio.Services.Relationships;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

            // The partitioned pair is the shape Marten's tenant partitioning produces
            // (MartenManagedTenantListPartitions): Postgres copies the key declared on the parent onto
            // every partition, and onto the parent once per referenced partition, so a catalog read that
            // did not ask for conparentid = 0 would report this one key as four - three of them naming
            // tables (…_acme, …_globex) no mapping knows and no visibility filter can recognise.
            await using var command = new NpgsqlCommand(
                $"""
                 create table "{Schema}"."legacy_parent" (id uuid primary key);
                 create table "{Schema}"."legacy_child" (
                     id uuid primary key,
                     parent_id uuid constraint legacy_child_parent_fkey references "{Schema}"."legacy_parent" (id));

                 create table "{Schema}"."legacy_parted" (
                     id uuid,
                     tenant_id varchar not null,
                     parent_id uuid constraint legacy_parted_parent_fkey references "{Schema}"."legacy_parent" (id),
                     primary key (tenant_id, id)) partition by list (tenant_id);
                 create table "{Schema}"."legacy_parted_acme"
                     partition of "{Schema}"."legacy_parted" for values in ('acme');
                 create table "{Schema}"."legacy_parted_globex"
                     partition of "{Schema}"."legacy_parted" for values in ('globex');
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

    /// <summary>
    /// Two plain tables in the store's own schema, with a key between them, are drawn as two table nodes
    /// and an edge - their structure is visible without <c>BrowseDatabase</c>, as the Schema screen has
    /// always shown it (D27) - rather than listed as a key that could not be drawn.
    /// </summary>
    [PostgresFact]
    public async Task A_key_between_two_plain_tables_in_the_store_schema_is_drawn_between_table_nodes()
    {
        RelationshipGraph graph = await ReadAsync();

        graph.Nodes.Should().NotContain(x => x.Alias.Contains("legacy", StringComparison.Ordinal),
            "a plain table is a table node, never a document node");

        graph.Tables.Select(x => x.Name).Should().Contain(["legacy_parent", "legacy_child"]);

        RelationshipEdge edge = graph.Edges.Should().ContainSingle(x => x.ConstraintName == "legacy_child_parent_fkey").Subject;

        edge.FromIsTable.Should().BeTrue();
        edge.ToIsTable.Should().BeTrue();
        graph.DisplayName(edge.FromAlias).Should().Be(Schema + ".legacy_child");
        graph.DisplayName(edge.ToAlias).Should().Be(Schema + ".legacy_parent");
        edge.Column.Should().Be("parent_id");
        edge.Validated.Should().BeTrue();

        graph.Unmatched.Should().NotContain(x => x.Name == "legacy_child_parent_fkey");
    }

    /// <summary>
    /// A partitioned table's key is drawn once — the one somebody declared — and never once per
    /// partition, and no partition is ever a node.
    /// </summary>
    /// <remarks>
    /// Postgres clones a foreign key declared on a partitioned table onto every partition, and onto the
    /// parent once per referenced partition. On a store using Marten's tenant partitioning that is a row
    /// per tenant per key, each naming <c>mt_doc_&lt;alias&gt;_&lt;tenant&gt;</c> — a table the mappings do not know,
    /// so every one would have been reported: the tenant list printed on a page the visitor may be scoped
    /// to one tenant of, and past <c>IsDocumentTypeVisible</c>, which can only recognise the parent. Found
    /// by the adversarial review of P10, measured on a live catalog.
    /// </remarks>
    [PostgresFact]
    public async Task A_partitioned_tables_key_is_reported_once_and_never_once_per_partition()
    {
        RelationshipGraph graph = await ReadAsync();

        graph.Edges.Should().ContainSingle(x => x.ConstraintName == "legacy_parted_parent_fkey",
            "the parent's declared key is the only one of the four rows Postgres holds that anybody wrote");

        graph.Tables.Should().Contain(x => x.Name == "legacy_parted");

        graph.Tables.Should().NotContain(
            x => x.Name.Contains("legacy_parted_acme", StringComparison.Ordinal)
                 || x.Name.Contains("legacy_parted_globex", StringComparison.Ordinal),
            "a partition name is a tenant id on a store that partitions by tenant");

        graph.Unmatched.Should().NotContain(
            x => x.From.Contains("legacy_parted", StringComparison.Ordinal)
                 || x.To.Contains("legacy_parted", StringComparison.Ordinal));
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

        graph.Unmatched.Should().BeEmpty(
            "the fixture's own plain tables are drawn now, and nothing else here is worth reporting");

        graph.Tables.Should().NotContain(
            x => x.Name.StartsWith("mt_", StringComparison.Ordinal),
            "Marten's bookkeeping is never a table node");

        graph.Edges.Should().NotContain(
            x => x.ConstraintName != null && x.ConstraintName.Contains("mt_events", StringComparison.Ordinal));
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

        // The identity is the assertion: ReferencedBy.None is returned by the branch that answers before
        // a connection is opened. Every other path builds a `new ReferencedBy(entries)`, so this is what
        // says the detail page of a type nothing points at costs a graph lookup and nothing else.
        referenced.Should().BeSameAs(ReferencedBy.None);
    }

    /// <summary>
    /// The foreign-key graph behind the inbound panel is read once per scope, not once per document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every document-detail load used to pay for a full <c>pg_constraint</c> read and a whole graph
    /// build before it could discover that nothing points at the type on screen. Neither half depends on
    /// the document, so both go through the single-flight snapshot cache the live pages already share
    /// (D10) — after the scope has been resolved, never before, because resolving is where the store
    /// policy is applied.
    /// </para>
    /// <para>
    /// <b>Proved by making the database disagree with the cache.</b> The constraint is dropped between
    /// two reads through the same service: a cached graph still reports it, and a service with a cache of
    /// its own — the anti-vacuity half — reports the drop that really happened. The cache here is built
    /// with a five-minute life rather than the container's one second, so the test is about the sharing
    /// and not about the clock.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task The_inbound_panel_reads_the_foreign_key_graph_once_per_scope()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string id = fixture.OrderedCustomerId.ToString();

        using IServiceScope scope = Services.CreateScope();

        RelationshipDataService shared = Build(scope, TimeSpan.FromMinutes(5));

        Entry(await shared.GetReferencedByAsync(Scope, "customer", id, token), "order").Physical.Should()
            .BeTrue("the migration applied the key this test is about");

        await ExecuteAsync($"alter table \"{Schema}\".\"mt_doc_order\" drop constraint mt_doc_order_customer_id_fkey");

        try
        {
            Entry(await shared.GetReferencedByAsync(Scope, "customer", id, token), "order").Physical.Should()
                .BeTrue("the graph was read once and the second call was served from the cache");

            // The anti-vacuity half: a service whose cache is empty reads pg_constraint and sees the drop,
            // so the assertion above is about the cache rather than about a read that never notices.
            RelationshipDataService fresh = Build(scope, TimeSpan.FromMinutes(5));

            Entry(await fresh.GetReferencedByAsync(Scope, "customer", id, token), "order").Physical.Should()
                .BeFalse("nothing in the database is enforcing it any more");
        }
        finally
        {
            await ExecuteAsync(
                $"alter table \"{Schema}\".\"mt_doc_order\" add constraint mt_doc_order_customer_id_fkey " +
                $"foreign key (customer_id) references \"{Schema}\".\"mt_doc_customer\" (id)");
        }

        Entry(await Build(scope, TimeSpan.FromMinutes(5)).GetReferencedByAsync(Scope, "customer", id, token), "order")
            .Physical.Should().BeTrue("the test put it back");
    }

    /// <summary>
    /// The real service, with a cache of this test's own rather than the container's one-second one.
    /// </summary>
    /// <param name="scope">A DI scope to take the resolver, catalog and options from.</param>
    /// <param name="timeToLive">How long this instance's cached graph is good for.</param>
    private static RelationshipDataService Build(IServiceScope scope, TimeSpan timeToLive) =>
        new RelationshipDataService(
            scope.ServiceProvider.GetRequiredService<IOptions<MartenStudioOptions>>(),
            scope.ServiceProvider.GetRequiredService<StudioScopeResolver>(),
            scope.ServiceProvider.GetRequiredService<ColumnCatalog>(),
            new StudioSnapshotCache(TimeProvider.System, timeToLive),
            scope.ServiceProvider.GetRequiredService<ILogger<RelationshipDataService>>(),
            scope.ServiceProvider.GetRequiredService<MartenStudio.Services.Database.DatabaseAccess>(),
            scope.ServiceProvider.GetRequiredService<MartenStudio.Services.Database.DatabaseCatalog>(),
            scope.ServiceProvider.GetRequiredService<StudioCapabilityGuard>(),
            TimeProvider.System);

    /// <summary>
    /// SEC-fix F6: a document collection's count that runs out of time is that row's "not counted", logged at
    /// Debug - never a failed panel and a Warning on a document open. It used to run against the client's
    /// <c>CommandTimeout</c> alone, so Postgres was never told to stop and the timeout came back as an
    /// <c>NpgsqlException</c> nothing expected. Somebody holding the pointing table is the slow count, made
    /// deterministic: the count waits behind the lock until its <c>statement_timeout</c> ends it.
    /// </summary>
    [PostgresFact]
    public async Task A_count_that_runs_out_of_time_is_not_counted_on_its_row_and_warns_nobody()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using IServiceScope scope = Services.CreateScope();

        var capture = new MartenStudio.Integration.Tests.Logging.LogCapture();
        using ILoggerFactory loggers = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(capture);
        });

        var service = new RelationshipDataService(
            Microsoft.Extensions.Options.Options.Create(new MartenStudioOptions { QueryTimeout = TimeSpan.FromSeconds(1) }),
            scope.ServiceProvider.GetRequiredService<StudioScopeResolver>(),
            scope.ServiceProvider.GetRequiredService<ColumnCatalog>(),
            new StudioSnapshotCache(TimeProvider.System, TimeSpan.FromMinutes(5)),
            loggers.CreateLogger<RelationshipDataService>(),
            scope.ServiceProvider.GetRequiredService<MartenStudio.Services.Database.DatabaseAccess>(),
            scope.ServiceProvider.GetRequiredService<MartenStudio.Services.Database.DatabaseCatalog>(),
            scope.ServiceProvider.GetRequiredService<StudioCapabilityGuard>(),
            TimeProvider.System);

        await using NpgsqlConnection holder = await Postgres.OpenAsync(token);
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(token);

        await using (var hold = new NpgsqlCommand($"lock table \"{Schema}\".\"mt_doc_order\" in access exclusive mode", holder, held))
        {
            await hold.ExecuteNonQueryAsync(token);
        }

        ReferencedBy referenced;

        try
        {
            referenced = await service.GetReferencedByAsync(Scope, "customer", fixture.OrderedCustomerId.ToString(), token);
        }
        finally
        {
            await held.RollbackAsync(token);
        }

        referenced.Error.Should().BeNull("one collection whose count ran out of time must not blank the panel");

        ReferencedByEntry order = Entry(referenced, "order");
        order.IsCounted.Should().BeFalse();
        order.Error.Should().BeNull("running out of time is not a fault");
        order.NotCounted.Should().Contain("MartenStudioOptions.QueryTimeout (1 s)");

        Entry(referenced, "refnote").IsCounted.Should().BeTrue("the other collections still say theirs");

        capture.WarningsOrWorse.Should().BeEmpty();
        capture.Lines.Should().Contain(static x => x.Level == LogLevel.Debug && x.Message.Contains("at its timeout", StringComparison.Ordinal));
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

    /// <summary>
    /// Reads the graph through the container's own service - after dropping whatever the container's
    /// single-flight cache holds, because several tests here change a constraint and read straight after,
    /// and a key read shared for a second with the test before would be a test of the cache rather than of
    /// the drift. <see cref="The_inbound_panel_reads_the_foreign_key_graph_once_per_scope" /> is the one
    /// about the cache, and it builds a cache of its own.
    /// </summary>
    private async Task<RelationshipGraph> ReadAsync(string? tenantId = null)
    {
        Marten.Services.GetRequiredService<StudioSnapshotCache>().InvalidatePrefix("default|");

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
        Marten.Services.GetRequiredService<StudioSnapshotCache>().InvalidatePrefix("default|");

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
