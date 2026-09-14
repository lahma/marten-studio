using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Live;
using MartenStudio.Services.Schema;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// Reads the foreign-key graph of one store, and the inbound half of one document's relationships.
/// </summary>
/// <remarks>
/// <para>
/// Two catalog reads and a pure function. The schemas come from <c>SchemaDeclarationReader</c>, which
/// reads <c>StoreOptions</c> and opens no connection — never <c>AllSchemaNames()</c>, which is
/// <c>AllObjects()</c> and applies Marten's HiLo migration on the way through (AGENTS.md hard rule 14).
/// Nothing here calls <c>DocumentTables()</c>, <c>Functions()</c> or <c>CreateMigrationAsync()</c>
/// either: opening a tab must not write DDL, and <c>RelationshipsNoDdlLiveTests</c> is what holds this
/// file to it.
/// </para>
/// <para>
/// Failures are values. A page that draws a diagram cannot afford an exception on the circuit for a
/// database that went away mid-read, so every method here catches and returns "cannot report" (plan
/// §4.8) — and each of the inbound counts carries its own error, so one collection whose count timed out
/// does not blank the panel that names the other four.
/// </para>
/// </remarks>
internal sealed class RelationshipDataService : IRelationshipDataService
{
    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioScopeResolver resolver;
    private readonly ColumnCatalog columnCatalog;
    private readonly StudioSnapshotCache cache;
    private readonly ILogger<RelationshipDataService> logger;
    private readonly TimeProvider timeProvider;

    public RelationshipDataService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        StudioSnapshotCache cache,
        ILogger<RelationshipDataService> logger)
        : this(options, resolver, columnCatalog, cache, logger, TimeProvider.System)
    {
    }

    internal RelationshipDataService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        StudioSnapshotCache cache,
        ILogger<RelationshipDataService> logger,
        TimeProvider timeProvider)
    {
        this.options = options;
        this.resolver = resolver;
        this.columnCatalog = columnCatalog;
        this.cache = cache;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    private int CommandTimeoutSeconds => Math.Max(1, (int) options.Value.QueryTimeout.TotalSeconds);

    /// <inheritdoc />
    public async Task<RelationshipGraph> GetGraphAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        DateTimeOffset readAt = timeProvider.GetUtcNow();

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            string[] schemas = SchemaDeclarationReader.SchemaNames(resolved.Store.Options);

            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // One grouped reltuples read for every node's count (D8), and one pg_constraint read for the
            // whole diagram. A diagram of forty document types is two round trips, not eighty.
            IReadOnlyDictionary<string, DocumentBrowseQueries.TableEstimate> tableEstimates = await DocumentBrowseQueries
                .EstimateAllAsync(connection, schemas, CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            // The diagram draws a row count per node and nothing else; the page count the browser's rail
            // uses to decide whether an exact count is affordable has no meaning here.
            IReadOnlyDictionary<string, long> estimates = tableEstimates.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Rows,
                StringComparer.Ordinal);

            IReadOnlyList<PhysicalForeignKey> physical = await RelationshipQueries
                .ReadForeignKeysAsync(connection, schemas, CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            return RelationshipGraphBuilder.Build(
                resolved.Store.Options,
                options.Value.IsDocumentTypeVisible,
                physical,
                estimates,
                readAt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Marten Studio could not read the relationships of {StoreKey}", scope.StoreKey);

            return RelationshipGraph.Failed(Describe(exception), readAt);
        }
    }

    /// <inheritdoc />
    public async Task<ReferencedBy> GetReferencedByAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(id))
        {
            return ReferencedBy.None;
        }

        try
        {
            // Before the cache and never inside it: resolving is where the store, database and tenant
            // policies are applied, so a cache hit is only ever handed to somebody who has just passed
            // the same check for the same scope (the rule StudioSnapshotCache's own remarks state).
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            RelationshipGraph graph = await cache
                .GetAsync(CacheKey(scope), token => ReadGraphAsync(resolved, token), cancellationToken)
                .ConfigureAwait(false);

            List<RelationshipEdge> inbound = [];
            foreach (RelationshipEdge edge in graph.Edges)
            {
                if (string.Equals(edge.ToAlias, alias, StringComparison.OrdinalIgnoreCase))
                {
                    inbound.Add(edge);
                }
            }

            if (inbound.Count == 0)
            {
                // Nothing points here, so there is nothing to count and no connection to open. Most
                // document types are in this state, and every one of their detail pages used to pay for a
                // connection and a bounded count loop to find it out.
                return ReferencedBy.None;
            }

            // Built from the graph's own nodes rather than from AllKnownDocumentTypes(): the builder
            // drops Marten's infrastructure types and everything IsDocumentTypeVisible hid, and a
            // dictionary keyed by hidden aliases is one lookup away from putting a hidden collection on
            // screen. Every edge's ends are visible nodes by construction, so nothing is lost.
            Dictionary<string, IDocumentType> byAlias = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> drawn = new(StringComparer.OrdinalIgnoreCase);

            foreach (RelationshipNode node in graph.Nodes)
            {
                drawn.Add(node.Alias);
            }

            foreach (IDocumentType documentType in resolved.Store.Options.AllKnownDocumentTypes())
            {
                if (drawn.Contains(documentType.Alias))
                {
                    byAlias[documentType.Alias] = documentType;
                }
            }

            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            List<ReferencedByEntry> entries = [];

            foreach (RelationshipEdge edge in inbound)
            {
                if (!byAlias.TryGetValue(edge.FromAlias, out IDocumentType? source))
                {
                    continue;
                }

                entries.Add(await CountAsync(connection, resolved, source, edge, id, cancellationToken)
                    .ConfigureAwait(false));
            }

            entries.Sort(static (left, right) =>
            {
                var byFrom = string.CompareOrdinal(left.FromAlias, right.FromAlias);
                return byFrom != 0 ? byFrom : string.CompareOrdinal(left.Column, right.Column);
            });

            return new ReferencedBy(entries);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Marten Studio could not read what references '{Id}' of '{Alias}'", id, alias);

            return ReferencedBy.Failed(Describe(exception));
        }
    }

    /// <summary>
    /// The foreign-key graph alone — no row counts — for one scope, shared between the circuits that ask
    /// for it at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>pg_constraint</c> read and the whole graph build are what every document-detail load paid
    /// for, on every navigation, even for a document type nothing points at: the panel needs the graph to
    /// find that out. Neither half depends on the document, and both are the same for every visitor of
    /// the same scope, so they belong behind the single-flight cache the live pages already share (D10)
    /// rather than behind a short-circuit — the graph is what would have to be read to know whether a
    /// short-circuit applied, since a foreign key somebody added by hand is physical-only and appears in
    /// no <c>StoreOptions</c>.
    /// </para>
    /// <para>
    /// The estimates are deliberately empty. The diagram's node counts are a separate read that changes
    /// every time anything is written, and folding them in here would tie a picture of the schema to a
    /// number that moves - so <see cref="GetGraphAsync"/> keeps its own uncached read and this answers
    /// only the question the detail page asks: what points at what.
    /// </para>
    /// </remarks>
    private async Task<RelationshipGraph> ReadGraphAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        string[] schemas = SchemaDeclarationReader.SchemaNames(resolved.Store.Options);

        await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<PhysicalForeignKey> physical = await RelationshipQueries
            .ReadForeignKeysAsync(connection, schemas, CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        // The same graph the screen draws, so the two can never disagree about what points where.
        return RelationshipGraphBuilder.Build(
            resolved.Store.Options,
            options.Value.IsDocumentTypeVisible,
            physical,
            new Dictionary<string, long>(StringComparer.Ordinal),
            timeProvider.GetUtcNow());
    }

    /// <summary>
    /// One key per scope, in the shape the other services use so that the cache's prefix invalidation
    /// reaches it when anything in that scope is changed.
    /// </summary>
    private static string CacheKey(StudioScope scope) =>
        scope.StoreKey + "|" + scope.DatabaseId + "|" + (scope.TenantId ?? string.Empty) + "|relationship-graph";

    /// <summary>
    /// How many of one collection's documents point at one id, bounded by the cap.
    /// </summary>
    /// <remarks>
    /// The parameter is typed from the <em>physical</em> column rather than from the target's CLR id type:
    /// a strong-typed id guesses <c>Unknown</c> from the CLR type and would be sent as an untyped literal,
    /// which cannot use an index and is one Postgres type-resolution rule away from failing outright -
    /// the same trap the related-documents strip fell into (P2-fix H4).
    /// </remarks>
    private async Task<ReferencedByEntry> CountAsync(
        NpgsqlConnection connection,
        ResolvedScope resolved,
        IDocumentType source,
        RelationshipEdge edge,
        string id,
        CancellationToken cancellationToken)
    {
        var hue = CollectionColorizer.HueFor(edge.FromAlias);

        try
        {
            TableColumns physical = await columnCatalog
                .GetAsync(connection, source.TableName.Schema, source.TableName.Name, cancellationToken)
                .ConfigureAwait(false);

            DocumentTableInfo table = DocumentTableInfo.FromDocumentType(source).WithPhysicalColumns(physical);

            PostgresColumn? column = null;
            foreach (PostgresColumn candidate in physical.Columns)
            {
                if (string.Equals(candidate.Name, edge.Column, StringComparison.OrdinalIgnoreCase))
                {
                    column = candidate;
                    break;
                }
            }

            if (column is null)
            {
                return new ReferencedByEntry(
                    edge.FromAlias, edge.Column, edge.Member, hue, 0, false, edge.Declared, edge.Physical,
                    $"'{source.TableName.QualifiedName}' has no column called '{edge.Column}'.");
            }

            IdParseResult parsed = DocumentQueryBuilder.ParseId(
                DocumentIdColumnTypes.FromUdtName(column.UdtName), id);

            if (!parsed.Success)
            {
                return new ReferencedByEntry(
                    edge.FromAlias, edge.Column, edge.Member, hue, 0, false, edge.Declared, edge.Physical,
                    parsed.Error);
            }

            NpgsqlDbType dbType = PostgresColumn.ToNpgsqlDbType(column.UdtName);

            await using NpgsqlCommand command = RelationshipQueries.BuildInboundCount(
                table,
                edge.Column,
                dbType,
                parsed.Value!,
                resolved.TenantId,
                RelationshipQueries.DefaultInboundCap,
                CommandTimeoutSeconds);

            command.Connection = connection;

            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var count = result is null or DBNull
                ? 0L
                : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);

            var capped = count >= RelationshipQueries.DefaultInboundCap;

            return new ReferencedByEntry(
                edge.FromAlias,
                edge.Column,
                edge.Member,
                hue,
                capped ? RelationshipQueries.DefaultInboundCap - 1 : count,
                capped,
                edge.Declared,
                edge.Physical);
        }
        catch (PostgresException exception)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio could not count references from '{Alias}'", edge.FromAlias);
            }

            return new ReferencedByEntry(
                edge.FromAlias, edge.Column, edge.Member, hue, 0, false, edge.Declared, edge.Physical,
                $"{exception.SqlState}: {exception.MessageText}");
        }
    }

    private static string Describe(Exception exception) => exception switch
    {
        PostgresException postgres => $"{postgres.SqlState}: {postgres.MessageText}",
        NpgsqlException => "The database could not be reached: " + exception.Message,
        _ => exception.Message,
    };
}
