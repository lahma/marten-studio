namespace MartenStudio.Services.Relationships;

/// <summary>
/// What the Relationships screen and the detail page's inbound panel read.
/// </summary>
/// <remarks>
/// Scoped, like every other data service, and every method takes a <see cref="StudioScope" /> it resolves
/// before it looks anything up (plan §4.2). Nothing here mutates anything, so nothing here is capability
/// gated: the gate that applies is the per-store authorization policy the scope resolver runs first, on
/// every call.
/// </remarks>
internal interface IRelationshipDataService
{
    /// <summary>
    /// The whole picture: the store's document types, the foreign keys between them, and the keys with an
    /// end outside them.
    /// </summary>
    /// <remarks>
    /// Never throws for a database that would not answer — the graph comes back with
    /// <see cref="RelationshipGraph.Error" /> set, because a screen on a Blazor circuit that throws is a
    /// page that stops responding (plan §4.8).
    /// </remarks>
    /// <param name="scope">The store, database and tenant to read for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<RelationshipGraph> GetGraphAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// What points at one document: every collection with a foreign key to this type, and how many of its
    /// documents point at this particular one.
    /// </summary>
    /// <param name="scope">The store, database and tenant to read for.</param>
    /// <param name="alias">The collection the document is in.</param>
    /// <param name="id">The document's id, as the URL carries it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ReferencedBy> GetReferencedByAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default);
}
