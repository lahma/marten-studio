using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Live;
using MartenStudio.Services.Schema;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// Reads the foreign-key graph around one store - its document types and the tables beside them the
/// visitor may see - and the inbound half of one document's relationships.
/// </summary>
/// <remarks>
/// <para>
/// <b>One shared read, filtered per visitor.</b> The foreign keys come from one <c>pg_constraint</c> read
/// per database, cached through <see cref="StudioSnapshotCache" /> under a key built only from the store,
/// the database, <c>SqlConsoleRole</c> and a schema set that is the same for every visitor: the store's own
/// schemas and whatever <see cref="MartenStudioOptions.BrowsableSchemas" /> admits. Who is asking is never
/// part of it. Each visitor's database-browser gate (<see cref="DatabaseAccess.GateAsync" />, D27) is then
/// applied by <see cref="RelationshipGraphBuilder" /> <em>after</em> the cache, so nothing another visitor may
/// not see is ever handed to them from it.
/// </para>
/// <para>
/// <b>Nothing here migrates.</b> The schemas come from <c>SchemaDeclarationReader</c>, which reads
/// <c>StoreOptions</c> and opens no connection — never <c>AllSchemaNames()</c>, which is
/// <c>AllObjects()</c> and applies Marten's HiLo migration on the way through (AGENTS.md hard rule 14).
/// The key read and the table counts run in the read-only session, as every read of objects Marten does
/// not own does; <c>RelationshipsNoDdlLiveTests</c> is what holds this file to it.
/// </para>
/// <para>
/// <b>A non-Marten table's rows are counted only through the row gate.</b> "How many rows of
/// <c>legacy.customer_credit</c> point at this customer" is a read of that table's rows, so it needs
/// <c>BrowseDatabase</c>, the write policy with no tenant, and a schema <c>BrowsableSchemas</c> admits - the
/// page's own gate is asked first, unaudited, so a visitor who may not is never refused (and never
/// audited or logged as refused) for merely opening a document; the enforcement,
/// <see cref="DatabaseAccess.RequireRowAccessAsync" />, then runs in front of the read itself.
/// </para>
/// <para>
/// <b>Opening a document writes nothing to the audit ring or above Debug in the log.</b> The count is a
/// passive read the page makes on every load - twice for a pasted link, because of prerendering - so an
/// entry per referencing table would push the mutations the 500-entry ring exists for out of it while
/// somebody paged through customers. The row gate is asked with <c>record: false</c> for the same reason: a
/// role with no <c>SELECT</c> on the pointing table is a "not counted" on the row, not a refusal of anything
/// the visitor did. The database browser's own row-detail counts are not audited either, so the two agree.
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
    /// <summary>
    /// What the row gate calls counting the rows of a non-Marten table that reference a document, should it
    /// ever record a refusal of it (only when the capability or a policy changed its answer mid-read).
    /// </summary>
    internal const string ReferencesAction = "Count referencing rows";

    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioScopeResolver resolver;
    private readonly ColumnCatalog columnCatalog;
    private readonly StudioSnapshotCache cache;
    private readonly ILogger<RelationshipDataService> logger;
    private readonly DatabaseAccess access;
    private readonly DatabaseCatalog catalog;
    private readonly StudioCapabilityGuard capabilities;
    private readonly TimeProvider timeProvider;

    public RelationshipDataService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        StudioSnapshotCache cache,
        ILogger<RelationshipDataService> logger,
        DatabaseAccess access,
        DatabaseCatalog catalog,
        StudioCapabilityGuard capabilities)
        : this(options, resolver, columnCatalog, cache, logger, access, catalog, capabilities, TimeProvider.System)
    {
    }

    internal RelationshipDataService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        ColumnCatalog columnCatalog,
        StudioSnapshotCache cache,
        ILogger<RelationshipDataService> logger,
        DatabaseAccess access,
        DatabaseCatalog catalog,
        StudioCapabilityGuard capabilities,
        TimeProvider timeProvider)
    {
        this.options = options;
        this.resolver = resolver;
        this.columnCatalog = columnCatalog;
        this.cache = cache;
        this.logger = logger;
        this.access = access;
        this.catalog = catalog;
        this.capabilities = capabilities;
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
        DatabaseGate? gate = null;

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            string[] schemas = SchemaDeclarationReader.SchemaNames(resolved.Store.Options);

            (RelationshipDatabaseView? view, string? unavailable) = await ViewAsync(scope, resolved, withRelations: true, cancellationToken)
                .ConfigureAwait(false);

            gate = view?.Gate;

            IReadOnlyDictionary<string, long> estimates;

            await using (NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read))
            {
                await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

                // One grouped reltuples read for every document node's count (D8); the table nodes' come
                // with the gate's relation list. A diagram of forty document types is not eighty round trips.
                IReadOnlyDictionary<string, DocumentBrowseQueries.TableEstimate> tableEstimates = await DocumentBrowseQueries
                    .EstimateAllAsync(connection, schemas, CommandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);

                // The diagram draws a row count per node and nothing else; the page count the browser's rail
                // uses to decide whether an exact count is affordable has no meaning here.
                estimates = tableEstimates.ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.Rows,
                    StringComparer.Ordinal);
            }

            PhysicalForeignKeyRead keys = await ForeignKeysAsync(resolved, cancellationToken).ConfigureAwait(false);

            RelationshipGraph graph = RelationshipGraphBuilder.Build(
                resolved.Store.Options,
                options.Value.IsDocumentTypeVisible,
                keys.Keys,
                estimates,
                readAt,
                view);

            return graph with
            {
                TablesUnavailable = unavailable,
                Truncated = graph.Truncated || keys.Truncated,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception, "Marten Studio could not read the relationships of {StoreKey}", scope.StoreKey);

            return RelationshipGraph.Failed(Describe(exception, gate), readAt);
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

        DatabaseGate? gate = null;

        try
        {
            // Before the cache and never inside it: resolving is where the store, database and tenant
            // policies are applied, so a cache hit is only ever handed to somebody who has just passed
            // the same check for the same scope (the rule StudioSnapshotCache's own remarks state).
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            (RelationshipDatabaseView? view, _) = await ViewAsync(scope, resolved, withRelations: false, cancellationToken)
                .ConfigureAwait(false);

            gate = view?.Gate;

            PhysicalForeignKeyRead keys = await ForeignKeysAsync(resolved, cancellationToken).ConfigureAwait(false);

            // The same graph the screen draws, filtered for this visitor, so the two can never disagree
            // about what points where - or about what may be named.
            RelationshipGraph graph = RelationshipGraphBuilder.Build(
                resolved.Store.Options,
                options.Value.IsDocumentTypeVisible,
                keys.Keys,
                new Dictionary<string, long>(StringComparer.Ordinal),
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
                // Nothing points here, so there is nothing to count and no connection to open. Most
                // document types are in this state, and every one of their detail pages used to pay for a
                // connection and a bounded count loop to find it out. A key read that stopped at its cap
                // still says so: "nothing points here" is then only "nothing that was read".
                return keys.Truncated ? ReferencedBy.None with { Truncated = true } : ReferencedBy.None;
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

            List<ReferencedByEntry> documents = [];
            List<ReferencedByEntry> tables = [];

            if (inbound.Any(static x => !x.FromIsTable))
            {
                await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
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
                exception, "Marten Studio could not read what references '{Id}' of '{Alias}'", id, alias);

            return ReferencedBy.Failed(Describe(exception, gate));
        }
    }

    /// <summary>
    /// This visitor's database-browser gate, with the relations it admits when the picture needs them -
    /// or, when the gate cannot be read, nothing and the reason, so the caller falls back to the document
    /// types alone rather than guess whose each table is (D27's classification fails closed).
    /// </summary>
    private async Task<(RelationshipDatabaseView? View, string? Unavailable)> ViewAsync(
        StudioScope scope,
        ResolvedScope resolved,
        bool withRelations,
        CancellationToken cancellationToken)
    {
        DatabaseGate? known = null;

        try
        {
            DatabaseGateRead read = await access.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);

            if (!read.Succeeded)
            {
                // Its sentence names another store only to a visitor that store's policy passes (D27).
                return (null, read.Reason ?? "The database browser's gate could not be read.");
            }

            known = read.Gate;

            if (!withRelations)
            {
                return (new RelationshipDatabaseView(read.Gate, []), null);
            }

            // The same cached, read-only catalog read the database browser lists its tables from, over
            // exactly the schemas this visitor's gate admits.
            CatalogSnapshot snapshot = await catalog
                .SnapshotAsync(resolved.Database, read.Gate.VisibleSchemas, CatalogParts.Relations, null, cancellationToken)
                .ConfigureAwait(false);

            return (new RelationshipDatabaseView(read.Gate, snapshot.Relations.Items, snapshot.Relations.Truncated), null);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            // The screen says so beside a document-only picture; the database's own trouble is what the
            // catalog read reports, and it is not a studio fault worth a Warning of its own.
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio drew the relationships of {StoreKey} without tables", scope.StoreKey);
            }

            return (null, DatabaseAccess.CatalogFailure(exception, known));
        }
    }

    /// <summary>
    /// Every foreign key touching the schemas any visitor might see, from the single-flight cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>pg_constraint</c> read is what every document-detail load and every Relationships load paid
    /// for, and it depends on nothing about the visitor: the schema set is the store's own plus what
    /// <c>BrowsableSchemas</c> admits, which is a fact about the options and the database. So it belongs
    /// behind the cache the live pages already share (D10), keyed per database - and deliberately not per
    /// tenant, because a catalog read has nothing tenant-shaped in it.
    /// </para>
    /// <para>
    /// The scope has already been resolved for this visitor when this runs, and the result is filtered for
    /// them afterwards; the cache key names no visitor because nothing in the value is theirs.
    /// </para>
    /// </remarks>
    private async Task<PhysicalForeignKeyRead> ForeignKeysAsync(
        ResolvedScope resolved,
        CancellationToken cancellationToken)
    {
        string[] schemas = await SchemaSetAsync(resolved, cancellationToken).ConfigureAwait(false);

        return await cache
            .GetAsync(CacheKey(resolved, schemas), token => ReadForeignKeysAsync(resolved.Database, schemas, token), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The schemas the shared read spans: the store's own, and - when <c>BrowseDatabase</c> is on for the
    /// process at all - every schema <c>BrowsableSchemas</c> admits. The same for every visitor.
    /// </summary>
    private async Task<string[]> SchemaSetAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        HashSet<string> set = new(SchemaDeclarationReader.SchemaNames(resolved.Store.Options), StringComparer.Ordinal);

        MartenStudioOptions value = options.Value;

        if (capabilities.IsEnabled(StudioCapability.BrowseDatabase) && value.BrowsableSchemas.Count > 0)
        {
            try
            {
                IReadOnlyList<CatalogSchema> live = await catalog.SchemasAsync(resolved.Database, cancellationToken)
                    .ConfigureAwait(false);

                set.UnionWith(BrowsableSchemaMatcher.Match(value.BrowsableSchemas, live));
            }
            catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
            {
                // The store's own schemas still draw; the gate read beside this one fails the same way and
                // says so on the screen.
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(exception, "Marten Studio read the relationships of the store's own schemas only");
                }
            }
        }

        return [.. set.Order(StringComparer.Ordinal)];
    }

    /// <summary>The key read itself, in the read-only session.</summary>
    private async Task<PhysicalForeignKeyRead> ReadForeignKeysAsync(
        IMartenDatabase database,
        IReadOnlyList<string> schemas,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = database.CreateConnection(ConnectionUsage.Read);
        await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

        return await Session()
            .InTransactionAsync(
                connection,
                (transaction, token) => RelationshipQueries.ReadForeignKeysTouchingAsync(
                    connection,
                    transaction,
                    schemas,
                    RelationshipQueries.DefaultForeignKeyCap,
                    SessionCommandTimeoutSeconds,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One key per store, database and schema set, in the <c>store|database|…</c> shape the other services
    /// use, so a prefix invalidation of that store reaches it.
    /// </summary>
    /// <remarks>
    /// Built from what the resolver settled on - the registration's key and the database's own identity -
    /// never from the scope as the URL spelled it: an empty <c>?db=</c> and the default database named
    /// outright are one database, and keyed on the request they were two cache entries and two key reads.
    /// </remarks>
    private string CacheKey(ResolvedScope resolved, IReadOnlyList<string> schemas) =>
        resolved.Registration.Key + "|" + resolved.Database.Id.Identity + "|relationship-keys|"
        + (options.Value.SqlConsoleRole ?? string.Empty) + "|" + string.Join('\u001e', schemas);

    /// <summary>
    /// How many of one collection's documents point at one id, bounded by the cap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parameter is typed from the <em>physical</em> column rather than from the target's CLR id type:
    /// a strong-typed id guesses <c>Unknown</c> from the CLR type and would be sent as an untyped literal,
    /// which cannot use an index and is one Postgres type-resolution rule away from failing outright -
    /// the same trap the related-documents strip fell into (P2-fix H4).
    /// </para>
    /// <para>
    /// <b>In a read-only transaction, under <c>statement_timeout</c>, as <see cref="CountTableAsync" /> is</b> - as
    /// the store's own role, though, not <c>SqlConsoleRole</c>, because these are Marten's own tables. A
    /// count on a large collection with no index behind the key used to run against the client's
    /// <c>CommandTimeout</c> alone: Postgres was never told to stop, and the timeout arrived as an
    /// <see cref="NpgsqlException" /> that no catch here expected - so it failed the whole panel, with a
    /// Warning, on a document open, which is an expected state. A count out of time is now the row's
    /// "not counted", logged at Debug, and the other collections still say theirs.
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

            // The store's own role, as every document read is: SqlConsoleRole narrows what the studio reads
            // beyond Marten's tables, never Marten's own.
            var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { StatementTimeout = options.Value.QueryTimeout });

            long count = await session
                .InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        await using NpgsqlCommand command = RelationshipQueries.BuildInboundCount(
                            table,
                            edge.Column,
                            dbType,
                            parsed.Value!,
                            resolved.TenantId,
                            RelationshipQueries.DefaultInboundCap,
                            SessionCommandTimeoutSeconds);

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

            return Entry(notCounted: NotCountedInTime());
        }
        catch (PostgresException exception)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio could not count references from '{Alias}'", edge.FromAlias);
            }

            return Entry(error: Describe(exception, gate));
        }
    }

    /// <summary>What a count that ran out of time says on its row.</summary>
    private string NotCountedInTime() =>
        "Counting these took longer than MartenStudioOptions.QueryTimeout (" +
        options.Value.QueryTimeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
        " s), so they were not counted.";

    /// <summary>
    /// How many rows of a table the studio does not map point at one document, bounded by the cap - when
    /// this visitor may read that table's rows at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is the database browser's.</b> The visitor's own gate first, asked without an audit
    /// entry, because a visitor who may not read these rows has merely opened a document and must not be
    /// recorded as refused for it; the row says why it is not counted instead. Only a visitor the gate
    /// admits reaches <see cref="DatabaseAccess.RequireRowAccessAsync" /> - capability, the write policy
    /// with no tenant, the schema, the catalog's own row for the table, whose it is and whether the role
    /// may select from it - asked with <c>record: false</c>, so what it says about the table itself (no
    /// <c>SELECT</c> for <c>SqlConsoleRole</c>, most often) is a "not counted" on the row and a Debug line,
    /// never a refusal in the ring; the capability and the policies it still records, since they can only
    /// refuse there if their answer changed since the gate was read. The count names the catalog's schema
    /// and table, never a string from the page, and goes through the database browser's own inbound count
    /// (<see cref="TableRowQueryBuilder.BuildInboundCount" />), so "referenced by" here and on a row's detail
    /// are one statement.
    /// </para>
    /// <para>
    /// <b>Nothing is audited when it succeeds.</b> It is a read the page makes on every load, not one the
    /// visitor asked for; see the class remarks.
    /// </para>
    /// <para>
    /// <b>Bound from the document, or not counted.</b> A key into a document table references its
    /// <c>id</c> - and, for a conjoined type, perhaps its <c>tenant_id</c>, which is filled from the scope's
    /// tenant or left out to count every tenant's rows, as the document list does with no tenant selected.
    /// A key into any other column of the document table has no value the studio could supply, and says so.
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

        // The row gate refuses a relation whose own name cannot be quoted; a column's name is this read's
        // alone to check, and one that cannot be quoted is a row that says so rather than a panel that fails.
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
            .RequireRowAccessAsync(scope, table.Schema, table.Name, ReferencesAction, record: false, cancellationToken: cancellationToken)
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
            await using NpgsqlConnection connection = grant.Grant.Resolved.Database.CreateConnection(ConnectionUsage.Read);
            await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            long count = await Session()
                .InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        await using NpgsqlCommand command = TableInboundCount(relation.Schema, relation.Name, columns, values)
                            .CreateCommand(connection, transaction, SessionCommandTimeoutSeconds);

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

            // The same columns and values the count used, in the row grammar, so the Rows tab it links to
            // shows exactly the rows counted (DB-5).
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

            return Entry(notCounted: NotCountedInTime());
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

    /// <summary>
    /// The bounded count of a pointing table's rows that carry these values in these columns - the database
    /// browser's own statement (DB-3), capped where every inbound count is.
    /// </summary>
    /// <remarks>
    /// Names from the catalog, quoted by the builder; values bound untyped, so Postgres applies the column's
    /// own input function and a uuid, a bigint or a domain compares correctly with no type name written into
    /// the text; <c>pg_catalog.count</c>, so a <c>count</c> of somebody else's on the path cannot answer.
    /// </remarks>
    internal static TableRowStatement TableInboundCount(
        string schema,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> values) =>
        TableRowQueryBuilder.BuildInboundCount(schema, table, columns, values, RelationshipQueries.DefaultInboundCap);

    /// <summary>
    /// The read-only session every read of an object Marten does not own runs in: read-only transaction,
    /// <c>statement_timeout</c> from <see cref="MartenStudioOptions.QueryTimeout" />, a short
    /// <c>lock_timeout</c>, and <c>SqlConsoleRole</c> when one is set.
    /// </summary>
    private ReadOnlySqlSession Session()
    {
        MartenStudioOptions value = options.Value;

        return new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = value.QueryTimeout,
            Role = value.SqlConsoleRole,
        });
    }

    /// <summary>
    /// A little past the server-side <c>statement_timeout</c>, which is what should fire: it produces 57014
    /// with a message, where a client-side timeout only breaks the connection.
    /// </summary>
    private int SessionCommandTimeoutSeconds => (int) Math.Ceiling(options.Value.QueryTimeout.TotalSeconds) + 5;

    /// <summary>
    /// A failure as the page shows it, Postgres' own words masked through the visitor's gate when it was read
    /// (<see cref="PostgresErrorText" />); before it was, what failed was the scope or the gate itself.
    /// </summary>
    private static string Describe(Exception exception, DatabaseGate? gate)
    {
        string message = exception switch
        {
            PostgresException postgres => $"{postgres.SqlState}: {postgres.MessageText}",
            NpgsqlException => "The database could not be reached: " + exception.Message,
            _ => exception.Message,
        };

        return gate is null ? message : PostgresErrorText.Redact(message, gate.WithheldSchemas) ?? message;
    }
}
