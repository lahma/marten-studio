using Marten;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using MartenStudio.Services.Relationships;
using MartenStudio.Services.Schema;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Relationships;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="IRelationshipNodeSource" />: the store's visible document types as nodes over their
/// tables, the foreign keys <c>StoreOptions</c> declares between them, and "referenced by" - how many documents and
/// rows point at one document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nodes and declared keys are configuration.</b> <see cref="MartenRelationshipGraph" /> reads them from
/// <see cref="IReadOnlyStoreOptions" />, which touches no connection; the one read here is the grouped
/// <c>reltuples</c> estimate for the nodes' counts (D8), through the resolved database's own connection.
/// </para>
/// <para>
/// <b>A non-Marten table's rows are counted only through the row gate.</b> "How many rows of
/// <c>legacy.customer_credit</c> point at this customer" is a read of that table's rows, so it needs
/// <c>BrowseDatabase</c>, the write policy with no tenant, and a schema <c>BrowsableSchemas</c> admits - the page's own
/// gate is asked first, unaudited, so a visitor who may not is never refused (and never audited or logged as refused)
/// for merely opening a document; the enforcement, <see cref="DatabaseAccess.RequireRowAccessAsync" />, then runs in
/// front of the read itself.
/// </para>
/// <para>
/// <b>Opening a document writes nothing to the audit ring or above Debug in the log.</b> The count is a passive read
/// the page makes on every load - twice for a pasted link, because of prerendering - so an entry per referencing table
/// would push the mutations the 500-entry ring exists for out of it while somebody paged through customers. The row
/// gate is asked with <c>record: false</c> for the same reason: a role with no <c>SELECT</c> on the pointing table is a
/// "not counted" on the row, not a refusal of anything the visitor did. The database browser's own row-detail counts
/// are not audited either, so the two agree.
/// </para>
/// <para>
/// Failures are values: each inbound count carries its own error, so one collection whose count timed out does not
/// blank the panel that names the other four. Its log lines are written under <see cref="RelationshipDataService" />'s
/// category, where they have always been.
/// </para>
/// </remarks>
internal sealed class MartenRelationshipNodeSource : IRelationshipNodeSource
{
    private readonly IStudioSettings settings;
    private readonly StudioScopeResolver resolver;
    private readonly ColumnCatalog columnCatalog;
    private readonly RelationshipReads reads;
    private readonly DatabaseAccess access;
    private readonly ILogger<RelationshipDataService> logger;
    private readonly TimeProvider timeProvider;

    public MartenRelationshipNodeSource(
        IStudioSettings settings,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        RelationshipReads reads,
        DatabaseAccess access,
        ILogger<RelationshipDataService> logger)
        : this(settings, resolver, columnCatalog, reads, access, logger, TimeProvider.System)
    {
    }

    internal MartenRelationshipNodeSource(
        IStudioSettings settings,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        RelationshipReads reads,
        DatabaseAccess access,
        ILogger<RelationshipDataService> logger,
        TimeProvider timeProvider)
    {
        this.settings = settings;
        this.resolver = resolver;
        this.columnCatalog = columnCatalog;
        this.reads = reads;
        this.access = access;
        this.logger = logger;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<RelationshipNodeSet> NodesAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (resolved is not ResolvedScope scope)
        {
            return RelationshipNodeSet.None;
        }

        string[] schemas = SchemaNames(scope);
        IReadOnlyDictionary<string, long> estimates;

        await using (NpgsqlConnection connection = scope.Target.CreateConnection(StudioConnectionUsage.Read))
        {
            await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            // One grouped reltuples read for every document node's count (D8); the table nodes' come with the gate's
            // relation list. A diagram of forty document types is not eighty round trips.
            IReadOnlyDictionary<string, DocumentBrowseQueries.TableEstimate> tableEstimates = await DocumentBrowseQueries
                .EstimateAllAsync(connection, schemas, reads.CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            // The diagram draws a row count per node and nothing else; the page count the browser's rail uses to
            // decide whether an exact count is affordable has no meaning here.
            estimates = tableEstimates.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Rows,
                StringComparer.Ordinal);
        }

        return MartenRelationshipGraph.Nodes(scope.Store.Options, settings.IsDocumentTypeVisible, estimates);
    }

    /// <inheritdoc />
    public IReadOnlyList<RelationshipDeclaredKey> DeclaredKeys(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return resolved is ResolvedScope scope
            ? MartenRelationshipGraph.DeclaredKeys(scope.Store.Options, settings.IsDocumentTypeVisible)
            : [];
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

        DatabaseGate? gate = null;

        try
        {
            // Before the cache and never inside it: resolving is where the store, database and tenant policies are
            // applied, so a cache hit is only ever handed to somebody who has just passed the same check for the same
            // scope (the rule StudioSnapshotCache's own remarks state).
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            (RelationshipDatabaseView? view, _) = await reads.ViewAsync(scope, resolved, withRelations: false, cancellationToken)
                .ConfigureAwait(false);

            gate = view?.Gate;

            IReadOnlyStoreOptions storeOptions = resolved.Store.Options;
            Func<Type, bool>? isVisible = settings.IsDocumentTypeVisible;
            RelationshipNodeSet nodes = MartenRelationshipGraph.Nodes(storeOptions, isVisible, new Dictionary<string, long>(StringComparer.Ordinal));

            PhysicalForeignKeyRead keys = await reads.ForeignKeysAsync(resolved, nodes.OwnSchemas, cancellationToken).ConfigureAwait(false);

            // The same graph the screen draws, filtered for this visitor, so the two can never disagree about what
            // points where - or about what may be named.
            RelationshipGraph graph = RelationshipGraphBuilder.Build(
                nodes,
                MartenRelationshipGraph.DeclaredKeys(storeOptions, isVisible),
                keys.Keys,
                timeProvider.GetUtcNow(),
                view);

            List<RelationshipEdge> inbound = [];
            foreach (RelationshipEdge edge in graph.Edges)
            {
                if (!edge.ToIsTable && string.Equals(edge.ToAlias, alias, StringComparison.OrdinalIgnoreCase))
                {
                    inbound.Add(edge);
                }
            }

            int withheld = graph.Withheld.Count(x =>
                x.VisibleEndIsTarget && string.Equals(x.VisibleEnd, alias, StringComparison.OrdinalIgnoreCase));

            if (inbound.Count == 0 && withheld == 0)
            {
                // Nothing points here, so there is nothing to count and no connection to open. Most document types are
                // in this state, and every one of their detail pages used to pay for a connection and a bounded count
                // loop to find it out. A key read that stopped at its cap still says so: "nothing points here" is then
                // only "nothing that was read".
                return keys.Truncated ? ReferencedBy.None with { Truncated = true } : ReferencedBy.None;
            }

            // Built from the graph's own nodes rather than from AllKnownDocumentTypes(): the builder drops Marten's
            // infrastructure types and everything IsDocumentTypeVisible hid, and a dictionary keyed by hidden aliases
            // is one lookup away from putting a hidden collection on screen. Every edge's ends are visible nodes by
            // construction, so nothing is lost.
            Dictionary<string, IDocumentType> byAlias = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> drawn = new(StringComparer.OrdinalIgnoreCase);

            foreach (RelationshipNode node in graph.Nodes)
            {
                drawn.Add(node.Alias);
            }

            foreach (IDocumentType documentType in storeOptions.AllKnownDocumentTypes())
            {
                if (drawn.Contains(documentType.Alias))
                {
                    byAlias[documentType.Alias] = documentType;
                }
            }

            List<ReferencedByEntry> documents = [];
            List<ReferencedByEntry> tables = [];

            if (inbound.Any(static x => !x.FromIsTable))
            {
                await using NpgsqlConnection connection = resolved.Target.CreateConnection(StudioConnectionUsage.Read);
                await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

                foreach (RelationshipEdge edge in inbound)
                {
                    if (edge.FromIsTable || !byAlias.TryGetValue(edge.FromAlias, out IDocumentType? source))
                    {
                        continue;
                    }

                    documents.Add(await CountAsync(connection, resolved, gate, source, edge, id, cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            foreach (RelationshipEdge edge in inbound)
            {
                if (!edge.FromIsTable
                    || view is null
                    || graph.FindTable(edge.FromAlias) is not { } table
                    || !byAlias.TryGetValue(alias, out IDocumentType? target))
                {
                    continue;
                }

                PhysicalForeignKey? key = keys.Keys.FirstOrDefault(x =>
                    string.Equals(x.Schema, table.Schema, StringComparison.Ordinal)
                    && string.Equals(x.Table, table.Name, StringComparison.Ordinal)
                    && string.Equals(x.Name, edge.ConstraintName, StringComparison.Ordinal));

                if (key is null)
                {
                    continue;
                }

                tables.Add(await CountTableAsync(scope, view.Gate, table, target, edge, key, id, cancellationToken)
                    .ConfigureAwait(false));
            }

            documents.Sort(static (left, right) =>
            {
                var byFrom = string.CompareOrdinal(left.FromAlias, right.FromAlias);
                return byFrom != 0 ? byFrom : string.CompareOrdinal(left.Column, right.Column);
            });

            tables.Sort(static (left, right) =>
            {
                var byFrom = string.CompareOrdinal(left.FromAlias, right.FromAlias);
                return byFrom != 0 ? byFrom : string.CompareOrdinal(left.Column, right.Column);
            });

            return new ReferencedBy([.. documents, .. tables]) { Withheld = withheld, Truncated = keys.Truncated };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Marten Studio could not read what references '{Id}' of '{Alias}'", StudioLogValue.Cap(id), StudioLogValue.Cap(alias));

            return ReferencedBy.Failed(RelationshipReads.Describe(exception, gate));
        }
    }

    /// <summary>The store's own schemas, from its declarations - no connection, no migration (hard rule 14).</summary>
    private static string[] SchemaNames(ResolvedScope scope) =>
        SchemaDeclarationReader.SchemaNames(scope.Store.Options);

    /// <summary>
    /// How many of one collection's documents point at one id, bounded by the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parameter is typed from the <em>physical</em> column rather than from the target's CLR id type: a
    /// strong-typed id guesses <c>Unknown</c> from the CLR type and would be sent as an untyped literal, which cannot
    /// use an index and is one Postgres type-resolution rule away from failing outright - the same trap the
    /// related-documents strip fell into (P2-fix H4).
    /// </para>
    /// <para>
    /// <b>In a read-only transaction, under <c>statement_timeout</c>, as <see cref="CountTableAsync" /> is</b> - as the
    /// store's own role, though, not <c>SqlConsoleRole</c>, because these are Marten's own tables. A count on a large
    /// collection with no index behind the key used to run against the client's <c>CommandTimeout</c> alone: Postgres
    /// was never told to stop, and the timeout arrived as an <see cref="NpgsqlException" /> that no catch here
    /// expected - so it failed the whole panel, with a Warning, on a document open, which is an expected state. A count
    /// out of time is now the row's "not counted", logged at Debug, and the other collections still say theirs.
    /// </para>
    /// </remarks>
    private async Task<ReferencedByEntry> CountAsync(
        NpgsqlConnection connection,
        ResolvedScope resolved,
        DatabaseGate? gate,
        IDocumentType source,
        RelationshipEdge edge,
        string id,
        CancellationToken cancellationToken)
    {
        var hue = CollectionColorizer.HueFor(edge.FromAlias);

        ReferencedByEntry Entry(long count = 0, bool capped = false, string? error = null, string? notCounted = null) =>
            new(edge.FromAlias, edge.Column, edge.Member, hue, count, capped, edge.Declared, edge.Physical, error)
            {
                OnDelete = edge.OnDelete,
                NotCounted = notCounted,
            };

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
                return Entry(error: $"'{source.TableName.QualifiedName}' has no column called '{edge.Column}'.");
            }

            IdParseResult parsed = DocumentQueryBuilder.ParseId(
                DocumentIdColumnTypes.FromUdtName(column.UdtName), id);

            if (!parsed.Success)
            {
                return Entry(error: parsed.Error);
            }

            NpgsqlDbType dbType = PostgresColumn.ToNpgsqlDbType(column.UdtName);

            // The store's own role, as every document read is: SqlConsoleRole narrows what the studio reads beyond
            // Marten's tables, never Marten's own.
            var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { StatementTimeout = settings.QueryTimeout });

            long count = await session
                .InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        await using NpgsqlCommand command = DocumentInboundQueries.BuildInboundCount(
                            table,
                            edge.Column,
                            dbType,
                            parsed.Value!,
                            resolved.TenantId,
                            RelationshipQueries.DefaultInboundCap,
                            reads.SessionCommandTimeoutSeconds);

                        command.Connection = connection;
                        command.Transaction = transaction;

                        object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

                        return result is null or DBNull
                            ? 0L
                            : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var capped = count >= RelationshipQueries.DefaultInboundCap;

            return Entry(capped ? RelationshipQueries.DefaultInboundCap - 1 : count, capped);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && PostgresFailure.IsTimeout(exception))
        {
            // The budget doing its job on a large collection: the row says so, and the panel carries on.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio stopped counting references from '{Alias}' at its timeout", edge.FromAlias);
            }

            return Entry(notCounted: reads.NotCountedInTime());
        }
        catch (PostgresException exception)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio could not count references from '{Alias}'", edge.FromAlias);
            }

            return Entry(error: RelationshipReads.Describe(exception, gate));
        }
    }

    /// <summary>
    /// How many rows of a table the studio does not map point at one document, bounded by the cap - when this visitor
    /// may read that table's rows at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is the database browser's.</b> The visitor's own gate first, asked without an audit entry, because
    /// a visitor who may not read these rows has merely opened a document and must not be recorded as refused for it;
    /// the row says why it is not counted instead. Only a visitor the gate admits reaches
    /// <see cref="DatabaseAccess.RequireRowAccessAsync" /> - capability, the write policy with no tenant, the schema,
    /// the catalog's own row for the table, whose it is and whether the role may select from it - asked with
    /// <c>record: false</c>, so what it says about the table itself (no <c>SELECT</c> for <c>SqlConsoleRole</c>, most
    /// often) is a "not counted" on the row and a Debug line, never a refusal in the ring; the capability and the
    /// policies it still records, since they can only refuse there if their answer changed since the gate was read.
    /// The count names the catalog's schema and table, never a string from the page, and goes through the database
    /// browser's own inbound count (<see cref="RelationshipDataService.TableInboundCount" />), so "referenced by" here
    /// and on a row's detail are one statement.
    /// </para>
    /// <para>
    /// <b>Nothing is audited when it succeeds.</b> It is a read the page makes on every load, not one the visitor asked
    /// for; see the class remarks.
    /// </para>
    /// <para>
    /// <b>Bound from the document, or not counted.</b> A key into a document table references its <c>id</c> - and, for a
    /// conjoined type, perhaps its <c>tenant_id</c>, which is filled from the scope's tenant or left out to count every
    /// tenant's rows, as the document list does with no tenant selected. A key into any other column of the document
    /// table has no value the studio could supply, and says so.
    /// </para>
    /// </remarks>
    private async Task<ReferencedByEntry> CountTableAsync(
        StudioScope scope,
        DatabaseGate gate,
        RelationshipTableNode table,
        IDocumentType target,
        RelationshipEdge edge,
        PhysicalForeignKey key,
        string id,
        CancellationToken cancellationToken)
    {
        ReferencedByEntry Entry(long count = 0, bool capped = false, string? error = null, string? notCounted = null) =>
            new(table.QualifiedName, edge.Column, null, table.Hue, count, capped, Declared: false, Physical: true, error)
            {
                Schema = table.Schema,
                Table = table.Name,
                OnDelete = edge.OnDelete,
                AllColumns = edge.AllColumns,
                NotCounted = notCounted,
            };

        DocumentTableInfo info = DocumentTableInfo.FromDocumentType(target);
        string? tenantColumn = info.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
            ? info.MetadataColumnName(DocumentMetadataColumn.TenantId)
            : null;

        List<string> columns = [];
        List<string> values = [];

        for (int i = 0; i < key.Columns.Count && i < key.LinkedColumns.Count; i++)
        {
            string linked = key.LinkedColumns[i];

            if (string.Equals(linked, DocumentTableInfo.IdColumn, StringComparison.Ordinal))
            {
                columns.Add(key.Columns[i]);
                values.Add(id);
            }
            else if (tenantColumn is not null && string.Equals(linked, tenantColumn, StringComparison.Ordinal))
            {
                if (scope.TenantId is { } tenant)
                {
                    columns.Add(key.Columns[i]);
                    values.Add(tenant);
                }
            }
            else
            {
                return Entry(notCounted:
                    $"The key references '{linked}' of the {target.Alias} table, which the studio cannot fill in from a document's id.");
            }
        }

        if (columns.Count == 0)
        {
            return Entry(notCounted: "The key does not reference the document's id, so there is nothing to count by.");
        }

        // The row gate refuses a relation whose own name cannot be quoted; a column's name is this read's alone to
        // check, and one that cannot be quoted is a row that says so rather than a panel that fails.
        if (!columns.All(DatabaseCatalogQueries.IsQuotable))
        {
            return Entry(notCounted: "A column of this key has a name the studio cannot put into SQL safely, so its rows are not counted.");
        }

        DatabaseRowAccess rows = gate.DataAccess(table.Schema);

        if (!rows.Allowed)
        {
            return Entry(notCounted: rows.Reason ?? "This studio does not let you read that table's rows.");
        }

        string qualified = DatabaseAccess.Target(table.Schema, table.Name);

        DatabaseRowAccessResult grant = await access
            .RequireRowAccessAsync(
                scope,
                table.Schema,
                table.Name,
                RelationshipDataService.ReferencesAction,
                record: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!grant.Allowed)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Marten Studio did not count the rows of {Table} that reference a {Alias} document: {Reason}",
                    qualified,
                    target.Alias,
                    grant.Reason);
            }

            return Entry(notCounted: grant.Reason ?? "This studio does not let you read that table's rows.");
        }

        CatalogRelation relation = grant.Grant.Relation.Relation;

        try
        {
            await using NpgsqlConnection connection = grant.Grant.Resolved.Target.CreateConnection(StudioConnectionUsage.Read);
            await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            long count = await reads.Session()
                .InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        await using NpgsqlCommand command = RelationshipDataService
                            .TableInboundCount(relation.Schema, relation.Name, columns, values)
                            .CreateCommand(connection, transaction, reads.SessionCommandTimeoutSeconds);

                        object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

                        return result is null or DBNull
                            ? 0L
                            : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            bool capped = count >= RelationshipQueries.DefaultInboundCap;

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Marten Studio counted {Count}{Capped} rows of {Table} that reference a {Alias} document",
                    capped ? RelationshipQueries.DefaultInboundCap - 1 : count,
                    capped ? "+" : string.Empty,
                    qualified,
                    target.Alias);
            }

            // The same columns and values the count used, in the row grammar, so the Rows tab it links to shows exactly
            // the rows counted (DB-5).
            string childFilter = RowFilterGrammar.Format(
                columns.Select((column, index) => (column, RowFilterOperator.Equal, (string?) values[index])));

            return Entry(capped ? RelationshipQueries.DefaultInboundCap - 1 : count, capped) with { ChildFilter = childFilter };
        }
        catch (Exception exception) when (exception is not OperationCanceledException && PostgresFailure.IsTimeout(exception))
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio stopped counting references from '{Table}' at its timeout", qualified);
            }

            return Entry(notCounted: reads.NotCountedInTime());
        }
        catch (PostgresException exception)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio could not count references from '{Table}'", qualified);
            }

            // The values bound are this document's id and tenant: what the visitor already has on screen.
            return Entry(error: exception.SqlState + ": " +
                PostgresErrorText.Redact(exception.MessageText, grant.Grant.Gate.WithheldSchemas, values));
        }
    }
}
