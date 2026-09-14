using System.Globalization;
using System.Reflection;

using JasperFx;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;

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
    private readonly ILogger<SchemaDataService> logger;

    public SchemaDataService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioActionLog audit,
        ColumnCatalog columnCatalog,
        IndexCatalog indexCatalog,
        ILogger<SchemaDataService> logger)
    {
        this.options = options;
        this.resolver = resolver;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.audit = audit;
        this.columnCatalog = columnCatalog;
        this.indexCatalog = indexCatalog;
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
    public async Task<SchemaTables> TablesAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        string[] schemas = SchemaNames(resolved);
        string eventSchema = resolved.Store.Options.Events.DatabaseSchemaName;

        Dictionary<string, IDocumentType> byTable = DocumentTypesByTable(resolved.Store.Options);

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

            List<TableStats> tables = new(rows.Count);
            foreach (TableStatsRow row in rows)
            {
                byTable.TryGetValue(SchemaKey.For(row.Schema, row.Table), out IDocumentType? documentType);

                tables.Add(new TableStats(
                    row.Schema,
                    row.Table,
                    row.TotalBytes,
                    row.HeapBytes,
                    row.IndexBytes,
                    row.EstimatedRows,
                    row.LiveRows,
                    row.DeadRows,
                    row.SequentialScans,
                    row.IndexScans,
                    row.LastVacuum,
                    row.LastAnalyze,
                    documentType?.Alias,
                    documentType is null ? null : SchemaTypeName.Of(documentType.DocumentType),
                    string.Equals(row.Schema, eventSchema, StringComparison.OrdinalIgnoreCase)));
            }

            return new SchemaTables(tables, schemas, databaseBytes, null);
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

            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<IndexStatsRow> actual = await SchemaStatsQueries
                .ReadIndexesAsync(connection, [.. declarations.Schemas], CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            return IndexAdvice.Join(
                actual,
                declarations.Indexes,
                DeclaredCollections(resolved),
                declarations.ManagedTables,
                declarations.IgnoredIndexes);
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
    public async Task<SchemaFunctions> FunctionsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

        try
        {
            SchemaDeclarations declarations = SchemaDeclarationReader.Read(
                resolved.Store.Options,
                options.Value.IsDocumentTypeVisible);

            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<FunctionStatsRow> rows = await SchemaStatsQueries
                .ReadFunctionsAsync(connection, [.. declarations.Schemas], CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            List<FunctionInfo> functions = new(rows.Count);
            foreach (FunctionStatsRow row in rows)
            {
                functions.Add(new FunctionInfo(
                    row.Schema,
                    row.Name,
                    row.Signature,
                    row.Definition,
                    declarations.Functions.Contains(SchemaKey.For(row.Schema, row.Name))));
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
