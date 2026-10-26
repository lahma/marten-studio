using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services.Relationships;

/// <summary>
/// Reads the foreign-key graph around one source - its own nodes and the tables beside them the visitor may see -
/// and hands "what points at this one" to the source that knows its nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The physical graph is a database fact, and shared.</b> The tables, the keys between them and the far ends the
/// database browser's gate withholds are the same in every studio (D27 moved the page to the Database section), and
/// they are read here through <see cref="RelationshipReads" />: the visitor's gate, the relations it admits, and the
/// one cached <c>pg_constraint</c> read per database. What a source knows beyond the catalog - MartenStudio's
/// document types over their tables, the keys its configuration declares, and the members behind its columns - comes
/// from its <see cref="IRelationshipNodeSource" />; a studio with none draws tables and keys only.
/// </para>
/// <para>
/// <b>Resolved once, here, before anything is read.</b> The shared resolver authorizes the scope first (D5), and the
/// node source is handed what it resolved rather than resolving it again.
/// </para>
/// <para>
/// <b>Opening a document writes nothing to the audit ring or above Debug in the log.</b> "Referenced by" is a passive
/// read the page makes on every load, and the node source that answers it says why on each row it did not count
/// rather than recording a refusal of anything the visitor did.
/// </para>
/// <para>
/// Failures are values. A page that draws a diagram cannot afford an exception on the circuit for a database that
/// went away mid-read, so every method here catches and returns "cannot report" (plan §4.8).
/// </para>
/// </remarks>
internal sealed class RelationshipDataService : IRelationshipDataService
{
    /// <summary>
    /// What the row gate calls counting the rows of a non-Marten table that reference a document, should it ever
    /// record a refusal of it (only when the capability or a policy changed its answer mid-read).
    /// </summary>
    internal const string ReferencesAction = "Count referencing rows";

    private readonly StudioSourceResolver resolver;
    private readonly RelationshipReads reads;
    private readonly ILogger<RelationshipDataService> logger;
    private readonly IRelationshipNodeSource? nodes;
    private readonly TimeProvider timeProvider;

    /// <remarks>
    /// <paramref name="nodes" /> is optional: a studio whose sources have no nodes of their own registers none, and
    /// its graph is the catalog's tables and keys.
    /// </remarks>
    public RelationshipDataService(
        StudioSourceResolver resolver,
        RelationshipReads reads,
        ILogger<RelationshipDataService> logger,
        IRelationshipNodeSource? nodes = null)
        : this(resolver, reads, logger, nodes, TimeProvider.System)
    {
    }

    internal RelationshipDataService(
        StudioSourceResolver resolver,
        RelationshipReads reads,
        ILogger<RelationshipDataService> logger,
        IRelationshipNodeSource? nodes,
        TimeProvider timeProvider)
    {
        this.resolver = resolver;
        this.reads = reads;
        this.logger = logger;
        this.nodes = nodes;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<RelationshipGraph> GetGraphAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        DateTimeOffset readAt = timeProvider.GetUtcNow();
        DatabaseGate? gate = null;

        try
        {
            ResolvedSource resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            (RelationshipDatabaseView? view, string? unavailable) = await reads
                .ViewAsync(scope, resolved, withRelations: true, cancellationToken)
                .ConfigureAwait(false);

            gate = view?.Gate;

            // The source's own nodes, with their row estimates (one grouped reltuples read, D8), and the keys its
            // configuration declares. Neither creates or migrates anything (hard rule 14).
            RelationshipNodeSet own = nodes is null
                ? RelationshipNodeSet.None
                : await nodes.NodesAsync(resolved, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<RelationshipDeclaredKey> declared = nodes is null ? [] : nodes.DeclaredKeys(resolved);

            PhysicalForeignKeyRead keys = await reads.ForeignKeysAsync(resolved, own.OwnSchemas, cancellationToken).ConfigureAwait(false);

            RelationshipGraph graph = RelationshipGraphBuilder.Build(own, declared, keys.Keys, readAt, view);

            return graph with
            {
                TablesUnavailable = unavailable,
                Truncated = graph.Truncated || keys.Truncated,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (reads.Product == StudioProduct.PostgresStudio)
            {
                logger.LogWarning(
                    exception, "Postgres Studio could not read the relationships of {StoreKey}", scope.StoreKey);
            }
            else
            {
                logger.LogWarning(
                    exception, "Marten Studio could not read the relationships of {StoreKey}", scope.StoreKey);
            }

            return RelationshipGraph.Failed(RelationshipReads.Describe(exception, gate), readAt);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// "Referenced by" is about one of a source's own nodes - in MartenStudio a document - so it is the node source's
    /// to answer: it resolves the scope, builds the same graph the screen draws from the same shared reads, and counts.
    /// A studio with no node source has nothing that can be referenced and answers <see cref="ReferencedBy.None" />.
    /// </remarks>
    public Task<ReferencedBy> GetReferencedByAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return nodes is null
            ? Task.FromResult(ReferencedBy.None)
            : nodes.GetReferencedByAsync(scope, alias, id, cancellationToken);
    }

    /// <summary>
    /// The bounded count of a pointing table's rows that carry these values in these columns - the database browser's
    /// own statement (DB-3), capped where every inbound count is.
    /// </summary>
    /// <remarks>
    /// Names from the catalog, quoted by the builder; values bound untyped, so Postgres applies the column's own input
    /// function and a uuid, a bigint or a domain compares correctly with no type name written into the text;
    /// <c>pg_catalog.count</c>, so a <c>count</c> of somebody else's on the path cannot answer.
    /// </remarks>
    internal static TableRowStatement TableInboundCount(
        string schema,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> values) =>
        TableRowQueryBuilder.BuildInboundCount(schema, table, columns, values, RelationshipQueries.DefaultInboundCap);
}
