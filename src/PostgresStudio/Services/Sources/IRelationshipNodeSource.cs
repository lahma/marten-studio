using PostgresStudio.Services.Relationships;

namespace PostgresStudio.Services.Sources;

/// <summary>
/// The nodes a source adds to the Relationships graph beyond its physical tables, the keys its configuration declares
/// between them, and what points at one of them.
/// </summary>
/// <remarks>
/// <para>
/// The physical foreign-key graph is a database fact and shared (D27 moved the page to the Database section): the
/// tables, the keys between them, the far ends gated by the database browser's gate. What a source knows beyond the
/// catalog is its own: MartenStudio draws each visible document type as a node over its table, labels a key by the
/// document member it is on, compares the keys its configuration declares with the ones that exist, and counts the
/// documents that point at one document. A plain source registers none of this, and its graph is tables and keys
/// only.
/// </para>
/// <para>
/// <b>What an implementation must not do.</b> Read rows of a table D27 refuses, name a hidden document type or its
/// table anywhere but <see cref="RelationshipNodeSet.HiddenTables" /> (which the shared builder only ever uses to drop
/// what touches it), or run a storage-ensuring call (AGENTS.md hard rule 14): every count is a bounded statement of
/// the studio's own, through the resolved database's connection. <see cref="NodesAsync" /> and
/// <see cref="DeclaredKeys" /> are handed a scope the shared service already resolved, and so authorized, for this
/// visitor, and must not resolve it again or reuse it for another scope; <see cref="GetReferencedByAsync" /> resolves
/// its own, as every data service does (D5).
/// </para>
/// </remarks>
internal interface IRelationshipNodeSource
{
    /// <summary>
    /// The source's own nodes for <paramref name="resolved" />'s database - MartenStudio's visible document types, each
    /// carrying the schema and table it lives in and its row estimate - with the tables it claims but hides, its own
    /// schemas and the members behind its columns. A resolved scope that is not this source's kind answers
    /// <see cref="RelationshipNodeSet.None" />.
    /// </summary>
    /// <param name="resolved">The scope, resolved for this visitor by the shared service.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<RelationshipNodeSet> NodesAsync(ResolvedSource resolved, CancellationToken cancellationToken = default);

    /// <summary>
    /// The foreign keys the source's configuration declares on its visible nodes - MartenStudio's
    /// <c>ForeignKey&lt;T&gt;()</c> - each with its target resolved as far as the configuration can say, in the
    /// configuration's own order. Read from configuration: no IO.
    /// </summary>
    /// <param name="resolved">The scope, resolved for this visitor by the shared service.</param>
    IReadOnlyList<RelationshipDeclaredKey> DeclaredKeys(ResolvedSource resolved);

    /// <summary>
    /// What points at one of the source's nodes: every node or table with a key to it, and how many of its rows point
    /// at this one - MartenStudio's "referenced by" panel. A source with no such node answers
    /// <see cref="ReferencedBy.None" />. Never throws for a database that would not answer: the result carries the
    /// failure.
    /// </summary>
    /// <param name="scope">The scope to read for.</param>
    /// <param name="alias">The node's key - a document collection's alias.</param>
    /// <param name="id">The row's id, as the URL carries it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ReferencedBy> GetReferencedByAsync(StudioScope scope, string alias, string id, CancellationToken cancellationToken = default);
}
