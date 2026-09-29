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
/// Two things about this service are deliberate and worth not undoing.
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
/// </remarks>
internal sealed class SchemaDataService : ISchemaDataService
{
    /// <summary>What the audit entry is called.</summary>
    private const string ApplyAction = "Apply schema changes";

    /// <summary>How much of the migration script goes into the audit entry and the log message.</summary>
    private const int AuditedSqlLength = 4000;

    /// <summary>The mode both the preview and the apply run under. See the class remarks.</summary>
    private const AutoCreate ApplyMode = AutoCreate.CreateOrUpdate;

    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioScopeResolver resolver;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioAuthorization authorization;
    private readonly StudioActionLog audit;
    private readonly ColumnCatalog columnCatalog;
    private readonly IndexCatalog indexCatalog;
    private readonly DatabaseAccess databaseAccess;
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
        this.logger = logger;
    }

    private int CommandTimeoutSeconds => (int) Math.Ceiling(options.Value.QueryTimeout.TotalSeconds);

    /// <inheritdoc />
    public async Task<SchemaCheck> CheckAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public async Task<MigrationPreview> PreviewAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            return await RenderPreviewAsync(resolved.Database, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Marten Studio could not preview a schema migration for database {DatabaseId}", resolved.Database.Id.Identity);
            return MigrationPreview.None with { Notice = exception.Message };
        }
    }

    /// <inheritdoc />
    public async Task<SchemaApplyResult> ApplyAsync(
        StudioScope scope,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string target = scope.StoreKey + "/" + scope.DatabaseId;

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

        // 2. The visitor, against the write policy, for this store and database.
        ResolvedScope resolved;
        try
        {
            resolved = await resolver
                .ResolveAsync(scope, nameof(StudioCapability.ApplySchemaChanges), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(
                scope,
                authorization.PolicyFor(nameof(StudioCapability.ApplySchemaChanges)) ?? "(none)",
                ApplyAction,
                target);
            throw;
        }

        IMartenDatabase database = resolved.Database;
        string identity = database.Id.Identity;

        // 3. What the visitor typed. The dialog checks this too; this is the check that counts, because a
        //    Blazor circuit is a long-lived object a client can drive.
        if (!string.Equals(confirmation?.Trim(), identity, StringComparison.Ordinal))
        {
            const string message = "The typed confirmation did not match the database identity.";
            audit.Record(ApplyAction, target, succeeded: false, message, StudioCapability.ApplySchemaChanges, scope);
            throw new InvalidOperationException(message);
        }

        // 4. Everything past the confirmation runs on a token of this method's own, never the caller's.
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
        //    The three steps above it - the capability, the write policy and the typed confirmation - do
        //    honour the caller's token: nothing has happened yet, and a refused or abandoned request that
        //    never reached the database costs nothing to drop.
        using var applying = new CancellationTokenSource();

        // 5. The script, rendered before anything is applied, so the audit entry says what was run even
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

        string auditedSql = Clamp(preview.HasSql ? preview.Sql : preview.Notice ?? "(no statements)");
        string user = await authorization.UserNameAsync().ConfigureAwait(false);

        try
        {
            SchemaPatchDifference applied = await database
                .ApplyAllConfiguredChangesToDatabaseAsync(ApplyMode, ct: applying.Token)
                .ConfigureAwait(false);

            string outcome = string.Create(
                CultureInfo.InvariantCulture,
                $"Applied {preview.ObjectCount} object(s) with AutoCreate.{ApplyMode}; Weasel reported {applied}. SQL: {auditedSql}");

            audit.Record(ApplyAction, target, succeeded: true, outcome, StudioCapability.ApplySchemaChanges, scope);
            logger.SchemaChangeApplied(user, scope.StoreKey, identity, outcome);

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
            string cancelled =
                "Cancelled while applying; the migration runs as separate statements with no transaction, " +
                "so part of it may have been applied. SQL: " + auditedSql;

            audit.Record(ApplyAction, target, succeeded: false, cancelled, StudioCapability.ApplySchemaChanges, scope);
            logger.SchemaChangeApplied(user, scope.StoreKey, identity, "CANCELLED: " + cancelled);
            logger.LogWarning(exception, "Marten Studio's schema apply on {DatabaseId} was cancelled", identity);

            throw;
        }
        catch (Exception exception)
        {
            string? sqlState = (exception as PostgresException)?.SqlState;
            string outcome = exception.Message + " SQL: " + auditedSql;

            audit.Record(ApplyAction, target, succeeded: false, outcome, StudioCapability.ApplySchemaChanges, scope);
            logger.SchemaChangeApplied(user, scope.StoreKey, identity, "FAILED: " + outcome);

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
    /// <see cref="DatabaseAccess.ReadDeclarations" /> over every registered store), and failing closed as the
    /// browser does: a configuration that cannot be read is a tab that says so, never a list in which a
    /// hidden type's table or another store's table passes for somebody else's.
    /// </para>
    /// <para>
    /// The partition counts are the one thing on this tab past the store's structure. A per-tenant partition
    /// count is the number of tenants, so it is shown only when the visitor passes the database browser's
    /// gate - <c>Capabilities.BrowseDatabase</c> and the write policy for the database with no tenant - asked
    /// here, in the service, with <see cref="DatabaseAccess.EvaluateAsync" />: the same question the browser's
    /// enforcement asks.
    /// </para>
    /// </remarks>
    public async Task<SchemaTables> TablesAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        string[] schemas = SchemaNames(resolved);

        DatabaseDeclarations declared = databaseAccess.ReadDeclarations(resolved);
        if (!declared.Succeeded)
        {
            return SchemaTables.Unavailable(declared.Failure ?? "The store's configuration could not be read.");
        }

        (bool capabilityEnabled, bool? authorized) = await databaseAccess
            .EvaluateAsync(scope, cancellationToken)
            .ConfigureAwait(false);

        string? withheld = SchemaTableAssembler.PartitionCountsWithheld(
            capabilities.ReadOnly, capabilityEnabled, authorized);

        Dictionary<string, string> typeNames = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, IDocumentType documentType) in DocumentTypesByTable(resolved.Store.Options))
        {
            typeNames[key] = SchemaTypeName.Of(documentType.DocumentType);
        }

        try
        {
            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<TableStatsRow> rows = await SchemaStatsQueries
                .ReadTablesAsync(connection, schemas, CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            long databaseBytes = await SchemaStatsQueries
                .ReadDatabaseSizeAsync(connection, CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<TableStats> tables = SchemaTableAssembler.Assemble(
                rows,
                declared.Classifier,
                resolved.Registration.Key,
                typeNames,
                partitionCountsShown: withheld is null);

            return new SchemaTables(tables, schemas, databaseBytes, null, withheld);
        }
        catch (OperationCanceledException)
        {
            throw;
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
    /// <b>What an apply migrates is wider than <see cref="SchemaDeclarations.ManagedTables" />.</b> Marten's
    /// <c>StorageFeatures.AllActiveFeatures</c> yields <c>StoreOptions.Storage.ExtendedSchemaObjects</c> as a
    /// feature of its own, so an apply migrates those tables - and drops the indexes they do not declare -
    /// exactly as it does a document table. So the managed set is the kind map's (every table it names a
    /// document, event, projection, extended or infrastructure table), the extended tables' declared and
    /// ignored indexes are read from their own Weasel definitions, and a projection's or extended table's
    /// undeclared index is told so in words that fit a table the host built
    /// (<see cref="IndexAdvice.ManagedTableSuggestion" />).
    /// </para>
    /// <para>
    /// A hidden document type's indexes are left out with its table, by the browser's classification over
    /// every registered store - which fails closed here as it does there.
    /// </para>
    /// </remarks>
    public async Task<SchemaIndexes> IndexesAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            // Read before the connection is opened, and deliberately not swallowed: a declaration set
            // that could not be built would make every index on the page look undeclared, which now
            // reads as "the apply will drop this". A reason on screen is the only honest answer.
            SchemaDeclarations declarations = SchemaDeclarationReader.Read(
                resolved.Store.Options,
                options.Value.IsDocumentTypeVisible);

            DatabaseDeclarations declared = databaseAccess.ReadDeclarations(resolved);
            if (!declared.Succeeded)
            {
                return SchemaIndexes.Unavailable(declared.Failure ?? "The store's configuration could not be read.");
            }

            ManagedTableSet managed = ManagedTables(resolved.Store.Options, declarations);

            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<IndexStatsRow> actual = await SchemaStatsQueries
                .ReadIndexesAsync(connection, [.. declarations.Schemas], CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            return IndexAdvice.Join(
                actual,
                managed.Indexes,
                DeclaredCollections(resolved),
                managed.Tables,
                managed.IgnoredIndexes,
                managed.RelationalTables,
                HiddenTables(declared.Classifier));
        }
        catch (OperationCanceledException)
        {
            throw;
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
    /// </remarks>
    public async Task<SchemaFunctions> FunctionsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            SchemaDeclarations declarations = SchemaDeclarationReader.Read(
                resolved.Store.Options,
                options.Value.IsDocumentTypeVisible);

            IReadOnlyList<FunctionStatsRow> rows;

            await using (NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                rows = await SchemaStatsQueries
                    .ReadFunctionsAsync(connection, [.. declarations.Schemas], CommandTimeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);
            }

            (DatabaseGate? gate, string? gateFailure) = await DefinitionGateAsync(scope, resolved, cancellationToken)
                .ConfigureAwait(false);

            List<FunctionInfo> functions = new(rows.Count);
            foreach (FunctionStatsRow row in rows)
            {
                bool declaredHere = declarations.Functions.Contains(SchemaKey.For(row.Schema, row.Name));
                DatabaseObjectKind kind = DatabaseObjectKinds.FromProkind(row.Kind);

                DatabaseObjectOwnership ownership = gate?.Classifier.ClassifyRoutine(row.Schema, row.Name)
                    ?? (declaredHere || DatabaseObjectClassifier.IsMartenName(row.Name)
                        ? new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure)
                        : new DatabaseObjectOwnership(
                            DatabaseObjectOwner.Other,
                            RecognisedAs: DatabaseObjectClassifier.RecognisedAs(row.Schema, row.Name)));

                DatabaseRowAccess access = kind == DatabaseObjectKind.Aggregate
                    ? DatabaseRowAccess.Refused(DatabaseRefusal.NotApplicable, DatabaseObjectService.AggregateHasNoBody)
                    : gate is null
                        ? DatabaseRowAccess.Refused(DatabaseRefusal.Unavailable, gateFailure ?? "The database browser's gate could not be read.")
                        : gate.DefinitionAccess(row.Schema, ownership);

                functions.Add(new FunctionInfo(
                    row.Schema,
                    row.Name,
                    row.IdentityArguments,
                    kind,
                    declaredHere,
                    ownership,
                    access.Allowed,
                    access.Allowed ? null : access.Reason));
            }

            return new SchemaFunctions(functions, null);
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
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            // Synchronous, and it builds every feature schema in the store, so it goes onto the thread
            // pool rather than onto the circuit's renderer. It also walks AllObjects(), which is why the
            // DDL tab is behind a button and says what pressing it may create (see the class remarks).
            IMartenDatabase database = resolved.Database;
            string script = await Task.Run(database.ToDatabaseScript, cancellationToken).ConfigureAwait(false);
            return new DdlScript(script, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Marten Studio could not produce a database script for database {DatabaseId}", resolved.Database.Id.Identity);
            return DdlScript.Unavailable(exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task<string> DatabaseIdentityAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        return resolved.Database.Id.Identity;
    }

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
    /// <para>
    /// The kind map (<see cref="SchemaDeclarations.Objects" />) is the source of which tables: a document,
    /// event, projection, extended or infrastructure table is migrated by an apply. What the reader does not
    /// yet give is the <c>ExtendedSchemaObjects</c> tables' own declared and ignored indexes - their
    /// <c>Table.Indexes</c> and <c>Table.IgnoredIndexes</c> - so they are read here, from the same in-memory
    /// list, touching no connection (hard rule 14).
    /// </para>
    /// <para>
    /// TODO(DB-7, consolidate): this walk belongs in <c>SchemaDeclarationReader.ReadExtendedObjects</c>, which
    /// DB-7 may not edit; the packet report carries the diff. Once it is there, <c>ManagedTables</c>,
    /// <c>Indexes</c> and <c>IgnoredIndexes</c> on the declarations cover it and this becomes the kind map
    /// alone.
    /// </para>
    /// </remarks>
    private static ManagedTableSet ManagedTables(IReadOnlyStoreOptions storeOptions, SchemaDeclarations declarations)
    {
        HashSet<string> tables = new(declarations.ManagedTables, StringComparer.OrdinalIgnoreCase);
        HashSet<string> relational = new(StringComparer.OrdinalIgnoreCase);
        List<DeclaredIndex> indexes = [.. declarations.Indexes];
        HashSet<string> ignored = new(declarations.IgnoredIndexes, StringComparer.OrdinalIgnoreCase);

        // Keyed, so that the day the reader declares the extended tables' indexes itself nothing here is
        // counted twice - a declared index listed twice is a missing index reported twice.
        HashSet<string> indexKeys = new(
            indexes.Select(static x => SchemaKey.For(x.Schema, x.Table, x.Name)),
            StringComparer.OrdinalIgnoreCase);

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

        if (storeOptions is StoreOptions concrete)
        {
            foreach (ISchemaObject schemaObject in concrete.Storage.ExtendedSchemaObjects)
            {
                if (schemaObject is not Table table)
                {
                    continue;
                }

                string schema = string.IsNullOrWhiteSpace(table.Identifier.Schema)
                    ? storeOptions.DatabaseSchemaName
                    : table.Identifier.Schema;
                string name = table.Identifier.Name;

                tables.Add(SchemaKey.For(schema, name));
                relational.Add(SchemaKey.For(schema, name));

                foreach (string ignoredIndex in table.IgnoredIndexes)
                {
                    ignored.Add(SchemaKey.For(schema, name, ignoredIndex));
                }

                foreach (IndexDefinition index in table.Indexes)
                {
                    if (!indexKeys.Add(SchemaKey.For(schema, name, index.Name)))
                    {
                        continue;
                    }

                    indexes.Add(new DeclaredIndex(
                        IndexAdvice.ManagedTableAlias,
                        IndexAdvice.ManagedTableAlias,
                        schema,
                        name,
                        index.Name,
                        IndexDdl(index, table)));
                }
            }
        }

        return new ManagedTableSet(tables, relational, indexes, ignored);
    }

    /// <summary>The statement Weasel would write for <paramref name="index" />, or its name when it will not render.</summary>
    private static string IndexDdl(IndexDefinition index, Table table)
    {
        try
        {
            return index.ToDDL(table);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return index.Name;
        }
    }

    /// <summary>Every hidden document type's table, in every registered store, by qualified name.</summary>
    private static HashSet<string> HiddenTables(DatabaseObjectClassifier classifier)
    {
        HashSet<string> hidden = new(StringComparer.OrdinalIgnoreCase);

        foreach (StoreDeclarations store in classifier.Stores)
        {
            foreach ((string key, MartenDeclaredObject declared) in store.Declarations.Objects)
            {
                if (declared is { Kind: MartenObjectKind.DocumentTable, Visible: false })
                {
                    hidden.Add(key);
                }
            }
        }

        return hidden;
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
}
