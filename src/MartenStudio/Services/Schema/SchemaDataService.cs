using System.Globalization;
using System.Reflection;

using JasperFx;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using Weasel.Core;
using Weasel.Postgresql.Tables;

namespace MartenStudio.Services.Schema;

/// <summary>
/// Everything the Schema screen reads, and the one place a schema change is applied from.
/// </summary>
/// <remarks>
/// <para>
/// Four things about this service are deliberate and worth not undoing.
/// </para>
/// <para>
/// <b>Everything is per database, not per store.</b> <c>IMartenStorage</c> offers
/// <c>CreateMigrationAsync()</c>, <c>ToDatabaseScript()</c> and
/// <c>ApplyAllConfiguredChangesToDatabaseAsync()</c>, and in a <c>DynamicMultiple</c> store the apply
/// reaches <em>every</em> database the store knows about. The same four members exist on
/// <c>IMartenDatabase</c> (through <c>Weasel.Core.Migrations.IDatabase</c>) and answer only for the
/// database the visitor resolved, which is the one they were authorized for. So this service never
/// touches <c>store.Storage</c> for anything that reads or writes schema.
/// </para>
/// <para>
/// <b>Apply uses <c>AutoCreate.CreateOrUpdate</c>, never <c>AutoCreate.All</c>.</b> <c>All</c> drops and
/// recreates every object it manages, which on a populated database means losing the data in it, and it
/// is also the only mode under which Weasel will run a migration it has judged <c>Invalid</c>. The
/// preview is rendered with <c>CreateOrUpdate</c> for the same reason, so what a person reads on screen
/// is exactly what is run. A migration Weasel calls invalid is reported, not quietly forced.
/// </para>
/// <para>
/// <b><c>CreateOrUpdate</c> is not "additive", and nothing here may imply that it is.</b> Weasel's
/// <c>TableDelta.WriteUpdate</c> writes <c>drop index</c> for every physical index the configuration does
/// not declare, <c>drop column</c> for every extra column, <c>alter column … type</c> for a changed one,
/// a <c>drop constraint … CASCADE</c> pair for a changed primary key, and a temp-table copy for a changed
/// partition scheme - all of it reported as <c>Update</c>, and <c>Update</c> is what <c>CreateOrUpdate</c>
/// allows. <see cref="MigrationRisk" /> finds those statements in the preview so the dialog can show them.
/// </para>
/// <para>
/// <b>No read path calls <c>AllSchemaNames()</c>, <c>AllObjects()</c>, <c>ToDatabaseScript()</c> or
/// <c>CreateMigrationAsync()</c>.</b> The first two run Weasel migrations through Marten's lazy
/// <c>Sequences</c> feature - an <c>AllSchemaNames()</c> call on an empty schema creates <c>mt_hilo</c>
/// and <c>mt_get_next_hi</c>, proven live - and the last two are simply slow and connection-hungry.
/// Everything a tab needs on navigation comes from <see cref="SchemaDeclarationReader" />, which reads
/// <c>StoreOptions</c> and nothing else; <see cref="CheckAsync" />, <see cref="PreviewAsync" /> and
/// <see cref="DdlAsync" /> are behind buttons and say what they may create.
/// </para>
/// <para>
/// <b>What Marten would run is shown only to a visitor who may see all of it</b> (<see cref="SchemaScriptGate" />).
/// The check, the preview and the script span every tenant and every document type, so they need the store
/// policy for the database as a whole and - while the store hides a document type - the database browser's
/// gate; so does an apply, whose audit entry carries the script. A host with no policy and no hidden type
/// sees no change.
/// </para>
/// </remarks>
internal sealed class SchemaDataService : ISchemaDataService
{
    /// <summary>What the audit entry is called.</summary>
    private const string ApplyAction = "Apply schema changes";

    /// <summary>What a refused drift check is audited as.</summary>
    private const string CheckAction = "Check schema";

    /// <summary>What a refused migration preview is audited as.</summary>
    private const string PreviewAction = "Preview schema migration";

    /// <summary>What a refused database script is audited as.</summary>
    private const string DdlAction = "Generate schema script";

    /// <summary>How much of the migration script goes into the audit entry and the log message.</summary>
    private const int AuditedSqlLength = 4000;

    /// <summary>The mode both the preview and the apply run under. See the class remarks.</summary>
    private const AutoCreate ApplyMode = AutoCreate.CreateOrUpdate;

    /// <summary><c>lock_not_available</c>: a <c>lock_timeout</c> expired.</summary>
    private const string LockNotAvailable = "55P03";

    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioScopeResolver resolver;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioAuthorization authorization;
    private readonly StudioActionLog audit;
    private readonly ColumnCatalog columnCatalog;
    private readonly IndexCatalog indexCatalog;
    private readonly DatabaseAccess databaseAccess;
    private readonly DatabaseCatalog catalog;
    private readonly MartenStoreRegistry registry;
    private readonly IServiceProvider provider;
    private readonly ILogger<SchemaDataService> logger;

    public SchemaDataService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioActionLog audit,
        ColumnCatalog columnCatalog,
        IndexCatalog indexCatalog,
        DatabaseAccess databaseAccess,
        DatabaseCatalog catalog,
        MartenStoreRegistry registry,
        IServiceProvider provider,
        ILogger<SchemaDataService> logger)
    {
        this.options = options;
        this.resolver = resolver;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.audit = audit;
        this.columnCatalog = columnCatalog;
        this.indexCatalog = indexCatalog;
        this.databaseAccess = databaseAccess;
        this.catalog = catalog;
        this.registry = registry;
        this.provider = provider;
        this.logger = logger;
    }

    private int CommandTimeoutSeconds => (int) Math.Ceiling(options.Value.QueryTimeout.TotalSeconds);

    /// <inheritdoc />
    public async Task<SchemaCheck> CheckAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ScriptGrant grant = await RequireScriptAsync(scope, SchemaScript.Check, CheckAction, cancellationToken).ConfigureAwait(false);
        if (!grant.Allowed)
        {
            return SchemaCheck.Refused(grant.Withheld);
        }

        SchemaCheck check = await RunCheckAsync(grant.Resolved, cancellationToken).ConfigureAwait(false);

        return await LateRefusalAsync(scope, grant, SchemaScript.Check, CheckAction, cancellationToken).ConfigureAwait(false) is { } late
            ? SchemaCheck.Refused(late)
            : check;
    }

    /// <inheritdoc />
    public async Task<MigrationPreview> PreviewAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ScriptGrant grant = await RequireScriptAsync(scope, SchemaScript.Preview, PreviewAction, cancellationToken).ConfigureAwait(false);
        if (!grant.Allowed)
        {
            return MigrationPreview.Refused(grant.Withheld);
        }

        MigrationPreview preview;
        try
        {
            preview = await RenderPreviewAsync(grant.Resolved.Database, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Marten Studio could not preview a schema migration for database {DatabaseId}", grant.Resolved.Database.Id.Identity);
            preview = MigrationPreview.None with { Notice = exception.Message };
        }

        return await LateRefusalAsync(scope, grant, SchemaScript.Preview, PreviewAction, cancellationToken).ConfigureAwait(false) is { } late
            ? MigrationPreview.Refused(late)
            : preview;
    }

    /// <inheritdoc />
    public async Task<SchemaApplyResult> ApplyAsync(
        StudioScope scope,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string target = Target(scope);

        // 1. The capability, process-wide. Hiding the button is a convenience; this is the refusal.
        try
        {
            capabilities.Require(StudioCapability.ApplySchemaChanges);
        }
        catch (StudioCapabilityDeniedException denied)
        {
            audit.RecordCapabilityDenied(denied, ApplyAction, target);
            throw;
        }

        // 2. The visitor, against the store policy and then the write policy, for this store and database AS A
        //    WHOLE. An apply runs CreateOrUpdate against the database, and that can drop indexes and columns and
        //    rewrite a partitioned table for every tenant in it (AGENTS.md hard rule 14) - so the question is
        //    asked with no tenant, whichever tenant the visitor has selected, and a policy that lets them change
        //    one tenant's data does not let them migrate everybody's. The two policies are asked one at a time
        //    so the refusal names the one that said no, and every refusal is audited against the tenant-less
        //    scope - the one that was refused. Asking the store policy here is also the script's own first
        //    gate: the migration spans every tenant, and a visitor who may not see all of them may not run it.
        StudioScope wholeDatabase = scope with { TenantId = null };

        if (!await authorization.IsAuthorizedAsync(wholeDatabase, capability: null, cancellationToken).ConfigureAwait(false))
        {
            audit.RecordScopeDenied(wholeDatabase, DatabaseAccess.StorePolicyName(options.Value), ApplyAction, target);
            throw new StudioNotAuthorizedException(wholeDatabase);
        }

        ResolvedScope resolved;
        try
        {
            resolved = await resolver
                .ResolveAsync(wholeDatabase, nameof(StudioCapability.ApplySchemaChanges), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(
                wholeDatabase,
                authorization.PolicyFor(nameof(StudioCapability.ApplySchemaChanges)) ?? "(none)",
                ApplyAction,
                target);
            throw;
        }

        // 3. The script it would run: while the store hides a document type, the migration prints that type's
        //    table, so a visitor who may not see it - the database browser's gate - may not run it either: the
        //    audit entry would carry it, and running what you may not read is not a thing to offer. Refused as
        //    a result, audited by the gate itself.
        ScriptGrant script = await RequireHiddenTypesAsync(scope, resolved, SchemaScript.Apply, ApplyAction, cancellationToken)
            .ConfigureAwait(false);

        if (!script.Allowed)
        {
            return SchemaApplyResult.Refused(script.Withheld);
        }

        IMartenDatabase database = resolved.Database;
        string identity = database.Id.Identity;

        // 4. What the visitor typed. The dialog checks this too; this is the check that counts, because a
        //    Blazor circuit is a long-lived object a client can drive.
        if (!string.Equals(confirmation?.Trim(), identity, StringComparison.Ordinal))
        {
            const string message = "The typed confirmation did not match the database identity.";
            audit.Record(ApplyAction, target, succeeded: false, message, StudioCapability.ApplySchemaChanges, wholeDatabase);
            throw new InvalidOperationException(message);
        }

        // 5. Everything past the confirmation runs on a token of this method's own, never the caller's.
        //
        //    Weasel executes a migration as a sequence of commands with no enclosing transaction, so
        //    cancelling half way leaves the schema in neither the old shape nor the new one. The caller's
        //    token is a Blazor circuit's: it is cancelled by a closed tab, a dropped WebSocket or a
        //    navigation, and none of those is a decision to abandon a migration somebody has typed a
        //    database name to start. The rendered script is inside the same boundary because it is the
        //    audit record of what ran - applying a migration and recording no SQL because the tab closed
        //    while the script was being rendered is the same hole in a different place. This is the
        //    reasoning that gives StudioOperationTracker its own CTS for a rebuild.
        //
        //    The steps above it - the capability, the write policy, the script's own gate and the typed
        //    confirmation - do honour the caller's token: nothing has happened yet, and a refused or
        //    abandoned request that never reached the database costs nothing to drop.
        using var applying = new CancellationTokenSource();

        // 6. The script, rendered before anything is applied, so the audit entry says what was run even
        //    when the run itself fails half way.
        MigrationPreview preview;
        try
        {
            preview = await RenderPreviewAsync(database, applying.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            preview = MigrationPreview.None with { Notice = exception.Message };
        }

        // Rendering builds every mapping Marten registered, so a type that became known on the way is asked
        // about now - before anything runs.
        if (await LateRefusalAsync(scope, script, SchemaScript.Apply, ApplyAction, cancellationToken).ConfigureAwait(false) is { } late)
        {
            return SchemaApplyResult.Refused(late);
        }

        string auditedSql = Clamp(preview.HasSql ? preview.Sql : preview.Notice ?? "(no statements)");

        // The Activity ring is read under the store policy, entry by entry: recorded against the database as
        // a whole, the entry is read only by a visitor who may see the whole script, and while the store hides
        // a document type the SQL stays in the application's log (event 9206) rather than the ring.
        string ringSql = HidesDocumentTypes(resolved.Store) ? SchemaScriptGate.RingSqlWithheld : auditedSql;
        string user = await authorization.UserNameAsync().ConfigureAwait(false);

        try
        {
            SchemaPatchDifference applied = await database
                .ApplyAllConfiguredChangesToDatabaseAsync(ApplyMode, ct: applying.Token)
                .ConfigureAwait(false);

            string summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Applied {preview.ObjectCount} object(s) with AutoCreate.{ApplyMode}; Weasel reported {applied}. SQL: ");

            audit.Record(ApplyAction, target, succeeded: true, summary + ringSql, StudioCapability.ApplySchemaChanges, wholeDatabase);
            logger.SchemaChangeApplied(user, scope.StoreKey, identity, summary + auditedSql);

            return new SchemaApplyResult(
                Succeeded: true,
                applied.ToString(),
                preview.ObjectCount,
                $"Applied to {identity}. Weasel reported {applied}.",
                SqlState: null);
        }
        catch (OperationCanceledException exception)
        {
            // Only the process going down can reach this now, and it is precisely the case where the
            // schema may be half migrated. An audit that records nothing because the operation was
            // cancelled cannot answer the question people ask afterwards, which is what ran.
            const string cancelled =
                "Cancelled while applying; the migration runs as separate statements with no transaction, " +
                "so part of it may have been applied. SQL: ";

            audit.Record(ApplyAction, target, succeeded: false, cancelled + ringSql, StudioCapability.ApplySchemaChanges, wholeDatabase);
            logger.SchemaChangeApplied(user, scope.StoreKey, identity, "CANCELLED: " + cancelled + auditedSql);
            logger.LogWarning(exception, "Marten Studio's schema apply on {DatabaseId} was cancelled", identity);

            throw;
        }
        catch (Exception exception)
        {
            string? sqlState = (exception as PostgresException)?.SqlState;

            audit.Record(ApplyAction, target, succeeded: false, exception.Message + " SQL: " + ringSql, StudioCapability.ApplySchemaChanges, wholeDatabase);
            logger.SchemaChangeApplied(user, scope.StoreKey, identity, "FAILED: " + exception.Message + " SQL: " + auditedSql);

            return new SchemaApplyResult(
                Succeeded: false,
                "Invalid",
                preview.ObjectCount,
                exception.Message,
                sqlState);
        }
        finally
        {
            // The studio has just changed the shape of the tables it reads, and its two catalogs are
            // caches with a sixty-second TTL - so without this the documents browser goes on selecting the
            // old column set, and the index verdict goes on recommending the index that was just created,
            // for up to a minute after the apply the same person pressed. The TTL exists for migrations
            // the studio did not make; this is the one it did.
            //
            // In the `finally` rather than only on success, on purpose: a failed apply runs as separate
            // statements with no transaction (see the cancellation path above), so part of it may have
            // landed. A half-applied migration is exactly when a stale catalog is most wrong.
            columnCatalog.Clear();
            indexCatalog.Clear();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Classified by the database browser's own rules (<see cref="DatabaseObjectClassifier" />, read through
    /// <see cref="DatabaseAccess.ReadDeclarations(ResolvedScope)" /> over every registered store). A store that
    /// cannot be read degrades the tab rather than blanking it (<see cref="SchemaClassification" />): what a
    /// readable store declares is listed, and what the unreadable one could own is withheld and counted. Only
    /// a configuration of the scope's own store that cannot be read is a tab that says so and lists nothing.
    /// </para>
    /// <para>
    /// Two things on this tab are counts of tenants: how many partitions a per-tenant partitioned table has,
    /// and how many rows Marten's tenancy tables hold (<see cref="SchemaTableAssembler.TenancyRegistryTables" />).
    /// Both are shown only when the visitor passes the database browser's gate - <c>Capabilities.BrowseDatabase</c>
    /// and, for the database with no tenant, the store policy and the write policy - asked here, in the service,
    /// with <see cref="DatabaseAccess.EvaluatePoliciesAsync" />: the same question the browser's enforcement asks,
    /// whose answer also says which policy refused.
    /// </para>
    /// <para>
    /// The read runs in the read-only session the browser uses, so its size functions give up after three
    /// seconds behind a lock rather than queue for the whole query timeout (<see cref="SchemaTables.Busy" />).
    /// </para>
    /// </remarks>
    public async Task<SchemaTables> TablesAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        string[] schemas = SchemaNames(resolved);

        ClassificationRead classification = await ClassifyAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
        if (!classification.Succeeded)
        {
            return SchemaTables.Unavailable(classification.Failure);
        }

        DatabasePolicyAnswer answer = await databaseAccess
            .EvaluatePoliciesAsync(scope, cancellationToken)
            .ConfigureAwait(false);

        string? withheld = SchemaTableAssembler.PartitionCountsWithheld(
            capabilities.ReadOnly, answer.CapabilityEnabled, answer.Authorized, answer.Refusal);

        Dictionary<string, string> typeNames = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, IDocumentType documentType) in DocumentTypesByTable(resolved.Store.Options))
        {
            typeNames[key] = SchemaTypeName.Of(documentType.DocumentType);
        }

        try
        {
            (IReadOnlyList<TableStatsRow> rows, long databaseBytes) = await InSessionAsync(
                    resolved,
                    async (connection, transaction, timeout, token) =>
                    {
                        IReadOnlyList<TableStatsRow> read = await SchemaStatsQueries
                            .ReadTablesAsync(connection, transaction, schemas, timeout, token)
                            .ConfigureAwait(false);

                        // Last, because a refusal aborts the transaction: under SqlConsoleRole, a role without
                        // CONNECT on the database may not size it.
                        long bytes = await DatabaseSizeAsync(connection, transaction, timeout, token).ConfigureAwait(false);

                        return (read, bytes);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            AssembledTables assembled = SchemaTableAssembler.Assemble(
                rows,
                classification.Value,
                resolved.Registration.Key,
                typeNames,
                partitionCountsShown: withheld is null);

            return new SchemaTables(assembled.Tables, schemas, databaseBytes, null, withheld)
            {
                ClassificationNotice = classification.Value.TablesNotice(assembled.Withheld),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == LockNotAvailable)
        {
            // An expected state - somebody is migrating - that the tab renders, so Debug at most (LogLevelsTests).
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio's table statistics for database {DatabaseId} gave up on a lock", resolved.Database.Id.Identity);
            }

            return SchemaTables.Locked(LockedSentence("A table"));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Marten Studio could not read table statistics for database {DatabaseId}", resolved.Database.Id.Identity);
            return SchemaTables.Unavailable(exception.Message);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>What an apply migrates is wider than the document and event tables.</b> Marten's
    /// <c>StorageFeatures.AllActiveFeatures</c> yields <c>StoreOptions.Storage.ExtendedSchemaObjects</c> as a
    /// feature of its own, so an apply migrates those tables - and drops the indexes they do not declare -
    /// exactly as it does a document table. <see cref="SchemaDeclarationReader" /> declares them as managed
    /// tables with their own declared and ignored indexes, so the managed set is the kind map's (every table it
    /// names a document, event, projection, extended or infrastructure table), and a projection's or extended
    /// table's undeclared index is told so in words that fit a table the host built
    /// (<see cref="IndexAdvice.ManagedTableSuggestion" />).
    /// </para>
    /// <para>
    /// A hidden document type's indexes are left out with its table, by the browser's classification over
    /// every registered store; a store that cannot be read withholds the indexes of every table it could own
    /// (<see cref="SchemaClassification" />). Every definition is read with the <c>search_path</c> pinned to
    /// <c>pg_catalog</c> and masked through the browser's gate, so an expression index that calls a function
    /// in a withheld schema names <c>‹withheld›</c>, as the browser's object detail does.
    /// </para>
    /// </remarks>
    public async Task<SchemaIndexes> IndexesAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            // Read before the connection is opened, and deliberately not forgiving: a declaration set that
            // could not be built would make every index on the page look undeclared, which reads as "the
            // apply will drop this". A reason on screen is the only honest answer.
            SchemaDeclarationRead own = SchemaDeclarationReader.ReadForClassification(
                resolved.Store.Options,
                options.Value.IsDocumentTypeVisible);

            if (!own.Succeeded)
            {
                return SchemaIndexes.Unavailable(own.Failure ?? "The store's configuration could not be read.");
            }

            ClassificationRead classification = await ClassifyAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
            if (!classification.Succeeded)
            {
                return SchemaIndexes.Unavailable(classification.Failure);
            }

            SchemaDeclarations declarations = own.Declarations;
            string[] schemas = [.. declarations.Schemas];

            IReadOnlySet<string> withheldSchemas = await WithheldSchemasAsync(scope, resolved, classification.Value, schemas, cancellationToken)
                .ConfigureAwait(false);

            ManagedTableSet managed = ManagedTables(declarations);

            IReadOnlyList<IndexStatsRow> actual = await InSessionAsync(
                    resolved,
                    (connection, transaction, timeout, token) =>
                        SchemaStatsQueries.ReadIndexesAsync(connection, transaction, schemas, timeout, token),
                    cancellationToken)
                .ConfigureAwait(false);

            DatabaseObjectClassifier classifier = classification.Value.Classifier;
            HashSet<string> excluded = new(classifier.HiddenTables, StringComparer.OrdinalIgnoreCase);
            HashSet<string> withheldTables = new(StringComparer.OrdinalIgnoreCase);
            List<IndexStatsRow> shown = new(actual.Count);

            foreach (IndexStatsRow row in actual)
            {
                string table = SchemaKey.For(row.Schema, row.Table);

                if (excluded.Contains(table) || withheldTables.Contains(table))
                {
                    continue;
                }

                if (classification.Value.IsDegraded)
                {
                    if (classifier.ClassifyRelation(row.Schema, row.Table) is not { } ownership)
                    {
                        excluded.Add(table);
                        continue;
                    }

                    if (classification.Value.WithholdsRelation(row.Table, ownership))
                    {
                        withheldTables.Add(table);
                        continue;
                    }
                }

                shown.Add(row with { Definition = WithheldNames.Redact(row.Definition, withheldSchemas) ?? string.Empty });
            }

            excluded.UnionWith(withheldTables);

            SchemaIndexes joined = IndexAdvice.Join(
                shown,
                managed.Indexes,
                DeclaredCollections(resolved),
                managed.Tables,
                managed.IgnoredIndexes,
                managed.RelationalTables,
                excluded);

            return joined with { ClassificationNotice = classification.Value.IndexesNotice(withheldTables.Count) };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgresException exception) when (exception.SqlState == LockNotAvailable)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "Marten Studio's index statistics for database {DatabaseId} gave up on a lock", resolved.Database.Id.Identity);
            }

            return SchemaIndexes.Locked(LockedSentence("An index"));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Marten Studio could not read index statistics for database {DatabaseId}", resolved.Database.Id.Identity);
            return SchemaIndexes.Unavailable(exception.Message);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Names and kinds only. A body is read one routine at a time, when a person opens its row, through
    /// <see cref="IDatabaseObjectService.GetDefinitionAsync" /> - the database browser's definition read,
    /// which is where the gate is enforced and audited (AGENTS.md hard rule 5). This tab used to read
    /// <c>pg_get_functiondef</c> for every function in the store's schemas on arrival, the host's own in
    /// <c>public</c> included, with no capability at all.
    /// </para>
    /// <para>
    /// <see cref="FunctionInfo.DefinitionAvailable" /> is the same gate's answer asked in advance -
    /// <see cref="DatabaseGate.DefinitionAccess" /> over the browser's classification - so the tab never
    /// offers a body the service would refuse: Marten's own in the store's own schemas to everybody, the
    /// rest only past <c>Capabilities.BrowseDatabase</c>, the write policy and <c>BrowsableSchemas</c>, and
    /// an aggregate's never. A gate that cannot be read withholds every body, with the reason.
    /// </para>
    /// <para>
    /// <b>Masked like the browser's lists.</b> The identity arguments are read with the <c>search_path</c>
    /// pinned to <c>pg_catalog</c> and masked through the gate, so an argument of a type in a withheld schema
    /// reads <c>‹withheld›.grade</c>; the definition read matches a masked signature back to its one overload.
    /// A per-type routine of a hidden document type, which a database an earlier Marten wrote keeps, is absent
    /// like the type's table (<see cref="SchemaClassification.HidesRoutine" />).
    /// </para>
    /// <para>
    /// <b>While a registered store cannot be read</b>, the browser reads nothing at all - it fails closed - so
    /// the tab reads Marten's own bodies in the store's own schemas itself, with the list
    /// (<see cref="SchemaStatsQueries.FunctionBodiesSql" />), and withholds every routine that store could own
    /// (<see cref="SchemaClassification" />). Marten's own routines were always on this tab, and an ancillary
    /// store with a bad connection string in another database is no reason to take them away.
    /// </para>
    /// </remarks>
    public async Task<SchemaFunctions> FunctionsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            SchemaDeclarations declarations = SchemaDeclarationReader.Read(
                resolved.Store.Options,
                options.Value.IsDocumentTypeVisible);

            string[] schemas = [.. declarations.Schemas];
            HashSet<string> storeSchemas = new(schemas, StringComparer.Ordinal);

            ClassificationRead classification = await ClassifyAsync(scope, resolved, cancellationToken).ConfigureAwait(false);

            DatabaseGate? gate = null;
            string? gateFailure = classification.Failure;

            if (classification.Value is { IsDegraded: false })
            {
                (gate, gateFailure) = await DefinitionGateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
            }

            SchemaClassification? degraded = classification.Value is { IsDegraded: true } value ? value : null;
            DatabaseObjectClassifier? classifier = gate?.Classifier ?? classification.Value?.Classifier;

            IReadOnlySet<string> withheldSchemas = gate?.WithheldSchemas
                ?? await EverySchemaButTheStoresAsync(resolved, schemas, cancellationToken).ConfigureAwait(false);

            (IReadOnlyList<FunctionStatsRow> rows, IReadOnlyList<FunctionBodyRow> bodies) = await InSessionAsync(
                    resolved,
                    async (connection, transaction, timeout, token) =>
                    {
                        IReadOnlyList<FunctionStatsRow> read = await SchemaStatsQueries
                            .ReadFunctionsAsync(connection, transaction, schemas, timeout, token)
                            .ConfigureAwait(false);

                        if (degraded is null)
                        {
                            return (read, (IReadOnlyList<FunctionBodyRow>) []);
                        }

                        // Only names already classified as Marten's, in the store's own schemas, ever reach the
                        // body read.
                        string[] martens =
                        [
                            .. read
                                .Where(x => x.Kind != "a"
                                    && !degraded.HidesRoutine(x.Schema, x.Name)
                                    && degraded.Classifier.ClassifyRoutine(x.Schema, x.Name)?.IsMarten == true)
                                .Select(static x => x.Name)
                                .Distinct(StringComparer.Ordinal),
                        ];

                        IReadOnlyList<FunctionBodyRow> texts = await SchemaStatsQueries
                            .ReadFunctionBodiesAsync(connection, transaction, schemas, martens, timeout, token)
                            .ConfigureAwait(false);

                        return (read, texts);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            Dictionary<string, string?> bodyByKey = new(StringComparer.Ordinal);
            foreach (FunctionBodyRow body in bodies)
            {
                bodyByKey[body.Schema + "." + body.Name + "(" + body.IdentityArguments + ")"] = body.Definition;
            }

            List<FunctionInfo> functions = new(rows.Count);
            int withheldRoutines = 0;

            foreach (FunctionStatsRow row in rows)
            {
                if (classifier is not null
                    ? SchemaClassification.IsHiddenTypesRoutine(classifier, row.Schema, row.Name)
                    : IsPerTypeRoutine(row.Name) && options.Value.IsDocumentTypeVisible is not null)
                {
                    // A hidden type's per-type routine: absent, like its table. With no classification at all,
                    // every per-type routine is, while the host hides anything.
                    continue;
                }

                bool declaredHere = declarations.Functions.Contains(SchemaKey.For(row.Schema, row.Name));
                DatabaseObjectKind kind = DatabaseObjectKinds.FromProkind(row.Kind);

                DatabaseObjectOwnership ownership = classifier?.ClassifyRoutine(row.Schema, row.Name)
                    ?? (declaredHere || DatabaseObjectClassifier.IsMartenName(row.Name)
                        ? new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure)
                        : new DatabaseObjectOwnership(
                            DatabaseObjectOwner.Other,
                            RecognisedAs: DatabaseObjectClassifier.RecognisedAs(row.Schema, row.Name)));

                if (degraded is not null && degraded.WithholdsRoutine(ownership))
                {
                    withheldRoutines++;
                    continue;
                }

                string arguments = WithheldNames.Redact(row.IdentityArguments, withheldSchemas) ?? string.Empty;
                string? definition = null;

                DatabaseRowAccess access;
                if (kind == DatabaseObjectKind.Aggregate)
                {
                    access = DatabaseRowAccess.Refused(DatabaseRefusal.NotApplicable, DatabaseObjectService.AggregateHasNoBody);
                }
                else if (gate is not null)
                {
                    access = gate.DefinitionAccess(row.Schema, ownership);
                }
                else if (degraded is not null && ownership.IsMarten && storeSchemas.Contains(row.Schema))
                {
                    definition = bodyByKey.TryGetValue(row.Schema + "." + row.Name + "(" + row.IdentityArguments + ")", out string? text)
                        ? WithheldNames.Redact(text, withheldSchemas)
                        : null;

                    access = definition is null
                        ? DatabaseRowAccess.Refused(DatabaseRefusal.NotFound, "Its body could not be read: it was dropped or replaced while the list was read.")
                        : DatabaseRowAccess.Granted;
                }
                else
                {
                    access = DatabaseRowAccess.Refused(
                        DatabaseRefusal.Unavailable,
                        gateFailure ?? degraded?.Who() ?? "The database browser's gate could not be read.");
                }

                functions.Add(new FunctionInfo(
                    row.Schema,
                    row.Name,
                    arguments,
                    kind,
                    declaredHere,
                    ownership,
                    access.Allowed,
                    access.Allowed ? null : access.Reason,
                    definition));
            }

            return new SchemaFunctions(functions, null)
            {
                ClassificationNotice = degraded?.FunctionsNotice(withheldRoutines),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Marten Studio could not read functions for database {DatabaseId}", resolved.Database.Id.Identity);
            return SchemaFunctions.Unavailable(exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<DdlScript> DdlAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ScriptGrant grant = await RequireScriptAsync(scope, SchemaScript.Ddl, DdlAction, cancellationToken).ConfigureAwait(false);
        if (!grant.Allowed)
        {
            return DdlScript.Refused(grant.Withheld);
        }

        DdlScript script;
        try
        {
            // Synchronous, and it builds every feature schema in the store, so it goes onto the thread
            // pool rather than onto the circuit's renderer. It also walks AllObjects(), which is why the
            // DDL tab is behind a button and says what pressing it may create (see the class remarks).
            IMartenDatabase database = grant.Resolved.Database;
            string text = await Task.Run(database.ToDatabaseScript, cancellationToken).ConfigureAwait(false);
            script = new DdlScript(text, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Marten Studio could not produce a database script for database {DatabaseId}", grant.Resolved.Database.Id.Identity);
            script = DdlScript.Unavailable(exception.Message);
        }

        return await LateRefusalAsync(scope, grant, SchemaScript.Ddl, DdlAction, cancellationToken).ConfigureAwait(false) is { } late
            ? DdlScript.Refused(late)
            : script;
    }

    /// <inheritdoc />
    public async Task<string> DatabaseIdentityAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        return resolved.Database.Id.Identity;
    }

    private async Task<SchemaCheck> RunCheckAsync(ResolvedScope resolved, CancellationToken cancellationToken)
    {
        IMartenDatabase database = resolved.Database;
        string[] schemas = SchemaNames(resolved);

        string? assertion;
        try
        {
            await database.AssertDatabaseMatchesConfigurationAsync(cancellationToken).ConfigureAwait(false);
            assertion = "Marten reports that this database matches its configuration.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Marten's own assertion message names every object it disagrees about and is the single most
            // useful sentence on this tab. It arrives as an exception, and an exception on a tab is a
            // blank tab - so it is rendered as a value (plan section 4.8).
            assertion = exception.Message;
        }

        try
        {
            SchemaMigration migration = await database.CreateMigrationAsync(cancellationToken).ConfigureAwait(false);
            List<SchemaObjectDifference> differences = Describe(migration);

            return differences.Count == 0
                ? SchemaCheck.Matches(schemas, assertion)
                : SchemaCheck.Differences(differences, schemas, assertion);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Marten Studio could not create a schema migration for database {DatabaseId}", database.Id.Identity);
            return SchemaCheck.Unavailable(exception.Message);
        }
    }

    /// <summary>
    /// The gate in front of everything Marten would run against the database: the store policy for the database
    /// as a whole, then - while the store hides a document type - the database browser's gate. Every refusal is
    /// audited under <paramref name="action" />; the grant carries the scope resolved with no tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The store policy is asked with <c>scope with { TenantId = null }</c> before anything is resolved, so a
    /// visitor refused it runs no tenant discovery and touches no database. The scope is then resolved with no
    /// tenant too: the script is the database's, whichever tenant the visitor happens to have selected.
    /// </para>
    /// <para>
    /// "Hides a document type" is asked of what Marten knows: <c>AllKnownDocumentTypes()</c> materialises every
    /// registered type and every projection's published type first - the same set <c>ToDatabaseScript()</c> and
    /// <c>CreateMigrationAsync()</c> walk - so a hidden type the script would print is already known here. The
    /// callers ask once more after rendering (<see cref="LateRefusalAsync" />), for the type a session taught
    /// Marten in between.
    /// </para>
    /// </remarks>
    private async Task<ScriptGrant> RequireScriptAsync(
        StudioScope scope,
        SchemaScript script,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        StudioScope wholeDatabase = scope with { TenantId = null };

        if (!await authorization.IsAuthorizedAsync(wholeDatabase, capability: null, cancellationToken).ConfigureAwait(false))
        {
            audit.RecordScopeDenied(wholeDatabase, DatabaseAccess.StorePolicyName(options.Value), action, Target(scope));
            return ScriptGrant.Refused(new SchemaScriptRefusal(DatabaseRefusal.StorePolicy, SchemaScriptGate.StorePolicyDenial(script)));
        }

        ResolvedScope resolved = await resolver.ResolveAsync(wholeDatabase, null, cancellationToken).ConfigureAwait(false);

        return await RequireHiddenTypesAsync(scope, resolved, script, action, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The script's second gate, for a scope already resolved for the database as a whole: while the store
    /// hides a document type, the database browser's.
    /// </summary>
    private async Task<ScriptGrant> RequireHiddenTypesAsync(
        StudioScope scope,
        ResolvedScope resolved,
        SchemaScript script,
        string action,
        CancellationToken cancellationToken)
    {
        if (!HidesDocumentTypes(resolved.Store))
        {
            return new ScriptGrant(resolved, null, BrowseChecked: false);
        }

        SchemaScriptRefusal? refused = await RequireBrowseAsync(scope, script, action, cancellationToken).ConfigureAwait(false);

        return refused is null ? new ScriptGrant(resolved, null, BrowseChecked: true) : ScriptGrant.Refused(refused);
    }

    /// <summary>
    /// The second half of the script's gate, asked once more after the script was rendered - when a type Marten
    /// learned in between is one the host hides and the gate was not asked the first time.
    /// </summary>
    private async Task<SchemaScriptRefusal?> LateRefusalAsync(
        StudioScope scope,
        ScriptGrant grant,
        SchemaScript script,
        string action,
        CancellationToken cancellationToken)
    {
        if (grant.BrowseChecked || grant.Resolved is null || !HidesDocumentTypes(grant.Resolved.Store))
        {
            return null;
        }

        return await RequireBrowseAsync(scope, script, action, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The database browser's own enforcement - the capability, then both policies for the database with no
    /// tenant, each refusal audited - with its refusal reworded for the script.
    /// </summary>
    private async Task<SchemaScriptRefusal?> RequireBrowseAsync(
        StudioScope scope,
        SchemaScript script,
        string action,
        CancellationToken cancellationToken)
    {
        DatabaseBrowseGrant grant = await databaseAccess
            .RequireBrowseAsync(scope, action, Target(scope), cancellationToken)
            .ConfigureAwait(false);

        return grant.Allowed
            ? null
            : new SchemaScriptRefusal(grant.Refusal, SchemaScriptGate.HiddenTypesDenial(script, grant.Refusal, grant.Reason));
    }

    /// <summary>
    /// Whether <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> hides any document type the store knows.
    /// A store whose types cannot be listed is taken to hide one: the answer decides whether a script that would
    /// print them is shown, and "cannot tell" must not read as "no".
    /// </summary>
    private bool HidesDocumentTypes(IDocumentStore store)
    {
        Func<Type, bool>? visible = options.Value.IsDocumentTypeVisible;
        if (visible is null)
        {
            return false;
        }

        try
        {
            foreach (IDocumentType documentType in store.Options.AllKnownDocumentTypes())
            {
                if (!visible(documentType.DocumentType))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>What an audit entry about this scope's schema names as its target.</summary>
    private static string Target(StudioScope scope) => scope.StoreKey + "/" + scope.DatabaseId;

    /// <summary>
    /// The classification the Tables, Indexes and Functions tabs draw with: the database browser's over every
    /// registered store, or - when one cannot be read - the readable stores' (<see cref="SchemaClassification" />).
    /// A failure only when the scope's own store's configuration cannot be read.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="DatabaseAccess.ReadDeclarations(ResolvedScope)" />: every registration, ancillary
    /// stores included whatever <see cref="MartenStudioOptions.IncludeAncillaryStores" /> says, the resolved
    /// store in its registration's place, options only and no database of any store enumerated. A store that
    /// cannot be read is named only to a visitor that store's store policy passes for the database with no
    /// tenant (AGENTS.md D27), as the browser shows another store's key.
    /// </remarks>
    private async Task<ClassificationRead> ClassifyAsync(
        StudioScope scope,
        ResolvedScope resolved,
        CancellationToken cancellationToken)
    {
        DatabaseDeclarations declared = databaseAccess.ReadDeclarations(resolved);

        if (declared.Succeeded)
        {
            return new ClassificationRead(SchemaClassification.Complete(declared.Classifier), null);
        }

        Func<Type, bool>? isVisible = options.Value.IsDocumentTypeVisible;
        string resolvedKey = resolved.Registration.Key;

        List<StoreDeclarations> readable = [];
        List<(string Key, string Problem, string Detail)> unreadable = [];
        bool sawResolved = false;

        foreach (MartenStoreRegistration registration in registry.Registrations(includeAncillaryStores: true))
        {
            bool isResolved = string.Equals(registration.Key, resolvedKey, StringComparison.OrdinalIgnoreCase);
            IDocumentStore? store;

            if (isResolved)
            {
                store = resolved.Store;
                sawResolved = true;
            }
            else
            {
                StoreAvailability availability = registry.TryResolve(registration, provider, out store);

                if (!availability.IsAvailable || store is null)
                {
                    unreadable.Add((registration.Key, "could not be built", availability.Message ?? "unknown reason"));
                    continue;
                }
            }

            SchemaDeclarationRead read = SchemaDeclarationReader.ReadForClassification(store.Options, isVisible);

            if (!read.Succeeded)
            {
                if (isResolved)
                {
                    return new ClassificationRead(null, OwnConfigurationFailure(resolvedKey, read.Failure));
                }

                unreadable.Add((registration.Key, "could not be read", read.Failure ?? "unknown reason"));
                continue;
            }

            readable.Add(new StoreDeclarations(registration.Key, read.Declarations));
        }

        if (!sawResolved)
        {
            SchemaDeclarationRead read = SchemaDeclarationReader.ReadForClassification(resolved.Store.Options, isVisible);
            if (!read.Succeeded)
            {
                return new ClassificationRead(null, OwnConfigurationFailure(resolvedKey, read.Failure));
            }

            readable.Insert(0, new StoreDeclarations(resolvedKey, read.Declarations));
        }

        var classifier = new DatabaseObjectClassifier(readable, hidesDocumentTypes: isVisible is not null);

        if (unreadable.Count == 0)
        {
            // Whatever failed a moment ago reads now.
            return new ClassificationRead(SchemaClassification.Complete(classifier), null);
        }

        List<UnreadableStore> named = new(unreadable.Count);
        foreach ((string key, string problem, string detail) in unreadable)
        {
            bool mayKnow = await authorization
                .IsAuthorizedAsync(new StudioScope(key, scope.DatabaseId, null), capability: null, cancellationToken)
                .ConfigureAwait(false);

            named.Add(mayKnow ? new UnreadableStore(key, problem, detail) : new UnreadableStore(null, problem, null));
        }

        return new ClassificationRead(SchemaClassification.Degraded(classifier, named), null);

        static string OwnConfigurationFailure(string key, string? failure) =>
            "The configuration of Marten store '" + key + "' could not be read, so the studio cannot tell its " +
            "tables from anybody else's: " + (failure ?? "unknown reason");
    }

    /// <summary>
    /// The schemas whose names this visitor may not read in a deparsed definition: the database browser's gate's
    /// withheld set, or - while the classification is degraded and the gate cannot be built - every schema that
    /// is neither the store's own nor a system schema.
    /// </summary>
    private async Task<IReadOnlySet<string>> WithheldSchemasAsync(
        StudioScope scope,
        ResolvedScope resolved,
        SchemaClassification classification,
        IReadOnlyList<string> storeSchemas,
        CancellationToken cancellationToken)
    {
        if (!classification.IsDegraded)
        {
            DatabaseGateRead read = await databaseAccess.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);

            if (read.Succeeded)
            {
                return read.Gate.WithheldSchemas;
            }
        }

        return await EverySchemaButTheStoresAsync(resolved, storeSchemas, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every live schema that is neither the store's own nor a system schema - the conservative withheld set, for
    /// when the browser's gate cannot say which of them this visitor may see.
    /// </summary>
    private async Task<IReadOnlySet<string>> EverySchemaButTheStoresAsync(
        ResolvedScope resolved,
        IReadOnlyList<string> storeSchemas,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogSchema> live = await catalog.SchemasAsync(resolved.Database, cancellationToken).ConfigureAwait(false);
        HashSet<string> own = new(storeSchemas, StringComparer.Ordinal);

        return live
            .Where(x => !BrowsableSchemaMatcher.IsSystemSchema(x.Name) && !own.Contains(x.Name))
            .Select(static x => x.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Whether a routine is named like one of the per-document-type routines an earlier Marten installed.</summary>
    private static bool IsPerTypeRoutine(string name)
    {
        foreach (string prefix in SchemaClassification.PerTypeRoutinePrefixes)
        {
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs a Schema tab's catalog read the way the database browser runs its own: a read-only transaction,
    /// <c>statement_timeout</c> from <see cref="MartenStudioOptions.QueryTimeout" />, a three-second
    /// <c>lock_timeout</c>, <c>SET LOCAL ROLE</c> to <see cref="MartenStudioOptions.SqlConsoleRole" /> when one is
    /// set, and the <c>search_path</c> pinned to <c>pg_catalog</c> before anything else, so every name Postgres
    /// deparses is printed with its schema and can be masked.
    /// </summary>
    private async Task<T> InSessionAsync<T>(
        ResolvedScope resolved,
        Func<NpgsqlConnection, NpgsqlTransaction, int, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        MartenStudioOptions value = options.Value;

        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = value.QueryTimeout,
            Role = value.SqlConsoleRole,
        });

        // A little past the server-side statement_timeout, which is what should fire: it produces 57014 with a
        // message, where a client-side timeout only breaks the connection.
        int commandTimeout = CommandTimeoutSeconds + 5;

        await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await session
            .InTransactionAsync(
                connection,
                async (transaction, token) =>
                {
                    await DatabaseCatalogQueries.PinSearchPathAsync(connection, transaction, commandTimeout, token)
                        .ConfigureAwait(false);

                    return await read(connection, transaction, commandTimeout, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>pg_database_size</c>, or <c>-1</c> ("n/a" on screen) when the reading role may not size the database -
    /// under <see cref="MartenStudioOptions.SqlConsoleRole" />, a role without <c>CONNECT</c> on it.
    /// </summary>
    private static async Task<long> DatabaseSizeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SchemaStatsQueries
                .ReadDatabaseSizeAsync(connection, transaction, commandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return -1;
        }
    }

    /// <summary>What a Schema tab says when its read gave up behind a lock.</summary>
    /// <param name="what">What was locked, with its article: "A table", "An index".</param>
    private static string LockedSentence(string what) =>
        what + " in this store's schemas is locked right now - somebody is changing it: a migration, or " +
        "Marten adding a tenant's partition - and the read gave up after three seconds rather than queue behind " +
        "the lock and hold up everything queued after it. Try again in a moment.";

    /// <summary>
    /// Renders the migration for one database.
    /// </summary>
    /// <remarks>
    /// This is the whole of U5's answer: <c>SchemaMigration</c> has neither <c>ToSql()</c> nor
    /// <c>UpdateSql()</c> in Weasel 9.32, and <c>WriteAllUpdates</c> into a <see cref="StringWriter" />
    /// with the database's own <c>Migrator</c> is the only way to see the script. It reads the catalog
    /// and writes to a string; nothing is executed.
    /// </remarks>
    private static async Task<MigrationPreview> RenderPreviewAsync(
        IMartenDatabase database,
        CancellationToken cancellationToken)
    {
        SchemaMigration migration = await database.CreateMigrationAsync(cancellationToken).ConfigureAwait(false);
        List<SchemaObjectDifference> deltas = Describe(migration);

        if (migration.Difference == SchemaPatchDifference.None)
        {
            return new MigrationPreview(string.Empty, 0, deltas, migration.Difference.ToString(), null);
        }

        using var writer = new StringWriter();
        try
        {
            migration.WriteAllUpdates(writer, database.Migrator, ApplyMode);
        }
        catch (SchemaMigrationException exception)
        {
            // Weasel refuses to write an update script for a migration it judged Invalid, because applying
            // it would mean dropping something. Saying so beats an empty code block.
            //
            // The type matters: WriteAllUpdates calls AssertPatchingIsValid, which throws
            // Weasel.Core.SchemaMigrationException - a direct subclass of Exception, neither an
            // InvalidOperationException nor a NotSupportedException. Filtering on those two made this
            // notice unreachable and turned an invalid migration into a generic error alert.
            return new MigrationPreview(
                string.Empty,
                deltas.Count,
                deltas,
                migration.Difference.ToString(),
                "This migration cannot be applied as an update. " + exception.Message);
        }

        return new MigrationPreview(
            writer.ToString(),
            deltas.Count,
            deltas,
            migration.Difference.ToString(),
            null);
    }

    private static List<SchemaObjectDifference> Describe(SchemaMigration migration)
    {
        List<SchemaObjectDifference> differences = [];

        foreach (ISchemaObjectDelta delta in migration.Deltas)
        {
            if (delta.Difference == SchemaPatchDifference.None)
            {
                continue;
            }

            differences.Add(new SchemaObjectDifference(
                delta.SchemaObject.Identifier.QualifiedName,
                KindOf(delta.SchemaObject),
                delta.Difference.ToString()));
        }

        return differences;
    }

    /// <summary>
    /// The sort of object a delta is about.
    /// </summary>
    /// <remarks>
    /// Weasel's own type name (<c>Table</c>, <c>Function</c>, <c>SystemFunction</c>, <c>Sequence</c>),
    /// because it is what the reader will see again in Weasel's own messages and in Marten's source.
    /// </remarks>
    private static string KindOf(ISchemaObject schemaObject) =>
        schemaObject is Table ? "Table" : schemaObject.GetType().Name;

    /// <summary>
    /// The schemas this store owns in this database, from its options alone.
    /// </summary>
    /// <remarks>
    /// Never <c>IMartenDatabase.AllSchemaNames()</c>: that is <c>AllObjects()</c>, which is
    /// <c>BuildFeatureSchemas()</c>, which reaches Marten's lazy <c>Sequences</c> feature and applies a
    /// migration on the spot. See <see cref="SchemaDeclarationReader" />.
    /// </remarks>
    private static string[] SchemaNames(ResolvedScope resolved) =>
        SchemaDeclarationReader.SchemaNames(resolved.Store.Options);

    /// <summary>
    /// The database browser's gate for the Functions tab's bodies, or why there is none - asked, never
    /// enforced: enforcement is <see cref="IDatabaseObjectService.GetDefinitionAsync" />'s.
    /// </summary>
    /// <remarks>
    /// A gate that cannot be built - a configuration the classifier cannot read, a catalog read that failed -
    /// withholds every body with its reason rather than failing the list: the names are this tab's own
    /// structure, and only the bodies were ever the gate's.
    /// </remarks>
    private async Task<(DatabaseGate? Gate, string? Failure)> DefinitionGateAsync(
        StudioScope scope,
        ResolvedScope resolved,
        CancellationToken cancellationToken)
    {
        try
        {
            DatabaseGateRead read = await databaseAccess.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);

            return read.Succeeded
                ? (read.Gate, null)
                : (null, read.Reason ?? "The database browser's gate could not be read.");
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return (null, DatabaseAccess.CatalogFailure(exception));
        }
    }

    /// <summary>
    /// The tables an apply from here migrates, the indexes they declare and ignore, and which of them are
    /// relational tables the host shaped.
    /// </summary>
    /// <param name="Tables">Every table an apply migrates.</param>
    /// <param name="RelationalTables">The projections' and <c>ExtendedSchemaObjects</c>' tables among them.</param>
    /// <param name="Indexes">Every declared index, the extended tables' included.</param>
    /// <param name="IgnoredIndexes">Every ignored index, the extended tables' included.</param>
    private sealed record ManagedTableSet(
        IReadOnlySet<string> Tables,
        IReadOnlySet<string> RelationalTables,
        IReadOnlyList<DeclaredIndex> Indexes,
        IReadOnlySet<string> IgnoredIndexes);

    /// <summary>
    /// Everything an apply from this store migrates, from its options alone.
    /// </summary>
    /// <remarks>
    /// The kind map (<see cref="SchemaDeclarations.Objects" />) is the source of which tables: a document,
    /// event, projection, extended or infrastructure table is migrated by an apply. The indexes are the
    /// reader's own, the <c>ExtendedSchemaObjects</c> tables' declared and ignored ones included - it reads
    /// them from their Weasel definitions (<c>SchemaDeclarationReader.ReadExtendedObjects</c>), so this walks
    /// nothing a second time.
    /// </remarks>
    private static ManagedTableSet ManagedTables(SchemaDeclarations declarations)
    {
        HashSet<string> tables = new(declarations.ManagedTables, StringComparer.OrdinalIgnoreCase);
        HashSet<string> relational = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, MartenDeclaredObject declared) in declarations.Objects)
        {
            switch (declared.Kind)
            {
                case MartenObjectKind.DocumentTable:
                case MartenObjectKind.EventTable:
                case MartenObjectKind.Infrastructure:
                    tables.Add(key);
                    break;

                case MartenObjectKind.ProjectionOrExtendedTable:
                    tables.Add(key);
                    relational.Add(key);
                    break;
            }
        }

        return new ManagedTableSet(
            tables,
            relational,
            declarations.Indexes,
            new HashSet<string>(declarations.IgnoredIndexes, StringComparer.OrdinalIgnoreCase));
    }

    private Dictionary<string, IDocumentType> DocumentTypesByTable(IReadOnlyStoreOptions storeOptions)
    {
        Func<Type, bool>? visible = options.Value.IsDocumentTypeVisible;
        Dictionary<string, IDocumentType> byTable = new(StringComparer.OrdinalIgnoreCase);

        foreach (IDocumentType documentType in storeOptions.AllKnownDocumentTypes())
        {
            if (visible is not null && !visible(documentType.DocumentType))
            {
                continue;
            }

            byTable[SchemaKey.For(documentType.TableName.Schema, documentType.TableName.Name)] = documentType;
        }

        return byTable;
    }

    private List<DeclaredCollection> DeclaredCollections(ResolvedScope resolved)
    {
        List<DeclaredCollection> collections = [];

        foreach (KeyValuePair<string, IDocumentType> entry in DocumentTypesByTable(resolved.Store.Options))
        {
            IDocumentType documentType = entry.Value;

            collections.Add(new DeclaredCollection(
                documentType.Alias,
                SchemaTypeName.Of(documentType.DocumentType),
                documentType.TableName.Schema,
                documentType.TableName.Name,
                SampleMember(documentType)));
        }

        return collections;
    }

    /// <summary>
    /// A property of the document type worth naming in a suggested <c>Index(x =&gt; x.Prop)</c> line.
    /// </summary>
    /// <remarks>
    /// Cosmetic, and only ever used inside suggestion text: a suggestion that reads
    /// <c>Index(x =&gt; x.Email)</c> is one a person can paste, and <c>Index(x =&gt; x.Property)</c> is
    /// one they have to translate first.
    /// </remarks>
    private static string? SampleMember(IDocumentType documentType)
    {
        string? idMember = documentType.IdMember?.Name;

        foreach (PropertyInfo property in documentType.DocumentType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (string.Equals(property.Name, idMember, StringComparison.Ordinal))
            {
                continue;
            }

            return property.Name;
        }

        return null;
    }

    private static string Clamp(string value) =>
        value.Length <= AuditedSqlLength ? value : value[..AuditedSqlLength] + " ... (truncated)";

    /// <summary>What the script's gate answers: the scope resolved with no tenant, or the refusal.</summary>
    /// <param name="Resolved">The scope, resolved with no tenant, when allowed.</param>
    /// <param name="Withheld">Why not, when refused.</param>
    /// <param name="BrowseChecked">Whether the database browser's gate was asked (and passed) already.</param>
    private sealed record ScriptGrant(ResolvedScope? Resolved, SchemaScriptRefusal? Withheld, bool BrowseChecked)
    {
        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Resolved))]
        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(false, nameof(Withheld))]
        public bool Allowed
        {
            get
            {
                if (Withheld is not null)
                {
                    return false;
                }

                if (Resolved is null)
                {
                    throw new InvalidOperationException("A script grant carries either a resolved scope or a refusal.");
                }

                return true;
            }
        }

        public static ScriptGrant Refused(SchemaScriptRefusal refusal) => new(null, refusal, BrowseChecked: false);
    }

    /// <summary>A classification, or why the scope's own store's configuration could not be read.</summary>
    /// <param name="Value">The classification.</param>
    /// <param name="Failure">Why there is none.</param>
    private sealed record ClassificationRead(SchemaClassification? Value, string? Failure)
    {
        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Value))]
        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(false, nameof(Failure))]
        public bool Succeeded
        {
            get
            {
                if (Failure is not null)
                {
                    return false;
                }

                if (Value is null)
                {
                    throw new InvalidOperationException("A classification read carries either a classification or a failure.");
                }

                return true;
            }
        }
    }
}
