using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Relationships;

using GraphModel = MartenStudio.Services.Relationships.RelationshipGraph;

namespace MartenStudio.Tests.Support;

/// <summary>
/// What the Relationships screen and the inbound panel are given, said outright by a test.
/// </summary>
/// <remarks>
/// A hand-written fake rather than a mocking framework (AGENTS.md package budget): the seam is one
/// interface with two methods, and a fake that a person can read the whole of is worth more here than a
/// call-matching DSL.
/// </remarks>
internal sealed class FakeRelationshipDataService : IRelationshipDataService
{
    /// <summary>What <see cref="GetGraphAsync" /> answers.</summary>
    public GraphModel Graph { get; set; } = Sample();

    /// <summary>What <see cref="GetReferencedByAsync" /> answers.</summary>
    public ReferencedBy Referenced { get; set; } = ReferencedBy.None;

    /// <summary>What the two methods throw, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many times the page has read, so a refresh can be told from the first load.</summary>
    public int Reads { get; private set; }

    /// <summary>The scope the last read was made with.</summary>
    public StudioScope? LastScope { get; private set; }

    public Task<GraphModel> GetGraphAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Reads++;
        LastScope = scope;

        return Failure is null ? Task.FromResult(Graph) : Task.FromException<GraphModel>(Failure);
    }

    public Task<ReferencedBy> GetReferencedByAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        Reads++;
        LastScope = scope;

        return Failure is null ? Task.FromResult(Referenced) : Task.FromException<ReferencedBy>(Failure);
    }

    /// <summary>
    /// The demo store's own shape plus the two drift cases: an order pointing at a customer that the
    /// database really has, an invoice pointing at the same customer that it does not, and a constraint
    /// on notes that nobody declared.
    /// </summary>
    public static GraphModel Sample() => new(
        [
            Node("customer", "Customer"),
            Node("invoice", "Invoice"),
            Node("note", "Note"),
            Node("order", "Order"),
        ],
        [
            new RelationshipEdge("invoice", "customer", "customer_id", "CustomerId", true, false, "NoAction"),
            new RelationshipEdge("note", "order", "order_id", null, false, true, "Cascade"),
            new RelationshipEdge("order", "customer", "customer_id", "CustomerId", true, true, "NoAction"),
        ],
        [
            new UnmatchedForeignKey(
                "legacy_fkey",
                "order",
                "legacy_id",
                "studio_sample.legacy_customers",
                false,
                true,
                "It points at a table no document type of this store maps."),
        ],
        new DateTimeOffset(2026, 9, 14, 10, 30, 0, TimeSpan.Zero));

    /// <summary>A graph with document types and no keys between them at all.</summary>
    public static GraphModel WithoutKeys() => new(
        [Node("customer", "Customer")],
        [],
        [],
        new DateTimeOffset(2026, 9, 14, 10, 30, 0, TimeSpan.Zero));

    /// <summary>The inbound panel's own sample: one exact count and one that hit the cap.</summary>
    public static ReferencedBy SampleReferencedBy() => new(
        [
            new ReferencedByEntry("invoice", "customer_id", "CustomerId", 100, 1000, true, true, true),
            new ReferencedByEntry("order", "customer_id", "CustomerId", 215, 3, false, true, true),
        ]);

    private static RelationshipNode Node(string alias, string type) =>
        new(alias, type, CollectionColorizer.HueFor(alias), DocumentCount.Estimate(12), false, []);
}
