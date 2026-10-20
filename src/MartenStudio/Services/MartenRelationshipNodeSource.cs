using MartenStudio.Services.Relationships;
using MartenStudio.Services.Sources;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="IRelationshipNodeSource" />: the document nodes and the inbound counts
/// <see cref="IRelationshipDataService" /> reads today. PS-1a's pass-through; the relationships packet (PS-1d) splits
/// the document half out of <c>RelationshipDataService</c> into this source and has the shared graph ask it.
/// </summary>
internal sealed class MartenRelationshipNodeSource : IRelationshipNodeSource
{
    private readonly IRelationshipDataService relationships;

    public MartenRelationshipNodeSource(IRelationshipDataService relationships)
    {
        this.relationships = relationships;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationshipNode>> NodesAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        RelationshipGraph graph = await relationships.GetGraphAsync(scope, cancellationToken).ConfigureAwait(false);
        return graph.Nodes;
    }

    /// <inheritdoc />
    public Task<ReferencedBy> GetReferencedByAsync(StudioScope scope, string alias, string id, CancellationToken cancellationToken = default) =>
        relationships.GetReferencedByAsync(scope, alias, id, cancellationToken);
}
