using MartenStudio.Services.Relationships;

namespace MartenStudio.Services.Sources;

/// <summary>
/// The nodes a source adds to the Relationships graph beyond its physical tables, and what points at one of them.
/// </summary>
/// <remarks>
/// <para>
/// The physical foreign-key graph is a database fact and shared (D27 moved the page to the Database section): the
/// tables, the keys between them, the far ends gated by the database browser's gate. What a source knows beyond the
/// catalog is its own: MartenStudio draws each visible document type as a node over its table, labels a key by the
/// document member it is on, compares the keys its configuration declares with the ones that exist, and counts the
/// documents that point at one document. A plain source contributes nothing, and its graph is tables and keys only.
/// </para>
/// <para>
/// <b>What an implementation must not do.</b> Read rows of a table D27 refuses, name a hidden document type or its
/// table, or run a storage-ensuring call (AGENTS.md hard rule 14): every count is a bounded statement of the studio's
/// own, through the resolved database's connection. The scope is resolved, and so authorized, by the implementation on
/// every call, as every data service does (D5).
/// </para>
/// </remarks>
internal interface IRelationshipNodeSource
{
    /// <summary>
    /// The source's own nodes for <paramref name="scope" />'s database - MartenStudio's document types, each carrying the
    /// schema and table it lives in, so the shared graph can map a key's ends onto them.
    /// </summary>
    /// <param name="scope">The scope to read for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<RelationshipNode>> NodesAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// What points at one of the source's nodes: every collection with a key to it, and how many of its rows point at this
    /// one - MartenStudio's "referenced by" panel. A source with no nodes answers <see cref="ReferencedBy.None" />.
    /// </summary>
    /// <param name="scope">The scope to read for.</param>
    /// <param name="alias">The node's key - a document collection's alias.</param>
    /// <param name="id">The row's id, as the URL carries it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ReferencedBy> GetReferencedByAsync(StudioScope scope, string alias, string id, CancellationToken cancellationToken = default);
}
