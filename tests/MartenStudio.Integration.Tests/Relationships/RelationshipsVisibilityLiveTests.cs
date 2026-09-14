using Marten;

using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services.Relationships;

namespace MartenStudio.Integration.Tests.Relationships;

/// <summary>
/// A document type the host hid never appears on the relationships screen — as a node, as either end of
/// an edge, or as a row of the "could not be drawn" list.
/// </summary>
/// <remarks>
/// <para>
/// The hidden type here is <c>Order</c>, and that is the interesting choice: <c>Order</c> declares a
/// foreign key to <c>Customer</c> <em>and</em> the database really has the constraint. So a
/// visibility gate that only filtered the declared half would still read the constraint out of
/// <c>pg_constraint</c> and draw <c>order → customer</c> as "in the database only" — the hidden
/// collection back on the screen, with a link to it, wearing a different badge.
/// </para>
/// <para>
/// The fixture is separate from <c>RelationshipsLiveTests</c> because <c>IsDocumentTypeVisible</c> is a
/// property of the whole studio container, not of one call.
/// </para>
/// </remarks>
public class RelationshipsVisibilityLiveTests(RelationshipsVisibilityLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<RelationshipsVisibilityLiveTests.Fixture>
{
    /// <summary>The demo store with <c>Order</c> hidden from the studio.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>A customer the seeded orders and invoices point at.</summary>
        public Guid CustomerId { get; private set; }

        /// <inheritdoc />
        protected override void ConfigureStudio(MartenStudioOptions options) =>
            options.IsDocumentTypeVisible = static type => type != typeof(Order);

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            await using IQuerySession query = Marten.Store.QuerySession();

            Order order = await query.Query<Order>().OrderBy(x => x.Reference).FirstAsync();

            CustomerId = order.CustomerId;
        }
    }

    [PostgresFact]
    public async Task A_hidden_type_is_not_a_node()
    {
        RelationshipGraph graph = await ReadAsync();

        graph.Error.Should().BeNull();
        graph.Nodes.Should().NotBeEmpty("the rest of the store is still there");
        graph.Nodes.Should().NotContain(x => x.Alias == "order");
    }

    [PostgresFact]
    public async Task A_hidden_type_is_not_an_edge_end_even_though_the_constraint_is_really_there()
    {
        RelationshipGraph graph = await ReadAsync();

        graph.Edges.Should().NotContain(x => x.FromAlias == "order" || x.ToAlias == "order");

        graph.Edges.Should().Contain(x => x.FromAlias == "invoice" && x.ToAlias == "customer",
            "hiding one type must not hide the others");
    }

    [PostgresFact]
    public async Task A_hidden_types_key_is_not_listed_as_one_that_could_not_be_drawn_either()
    {
        RelationshipGraph graph = await ReadAsync();

        graph.Unmatched.Should().NotContain(
            x => x.From.Contains("order", StringComparison.OrdinalIgnoreCase)
                 || x.To.Contains("order", StringComparison.OrdinalIgnoreCase),
            "naming the hidden table in the list of keys that could not be drawn says exactly the thing " +
            "the gate exists to stop the studio from saying");
    }

    [PostgresFact]
    public async Task A_hidden_type_is_not_a_referenced_by_row()
    {
        using MartenFixture.ScopedService<IRelationshipDataService> service =
            Marten.Resolve<IRelationshipDataService>();

        ReferencedBy referenced = await service.Service.GetReferencedByAsync(
            MartenFixture.ScopeFor(null),
            "customer",
            fixture.CustomerId.ToString(),
            TestContext.Current.CancellationToken);

        referenced.Error.Should().BeNull();
        referenced.Entries.Should().NotContain(x => x.FromAlias == "order",
            "the count would have been a row count of a collection the visitor may not see");
    }

    private async Task<RelationshipGraph> ReadAsync()
    {
        using MartenFixture.ScopedService<IRelationshipDataService> service =
            Marten.Resolve<IRelationshipDataService>();

        return await service.Service.GetGraphAsync(
            MartenFixture.ScopeFor(null), TestContext.Current.CancellationToken);
    }
}
