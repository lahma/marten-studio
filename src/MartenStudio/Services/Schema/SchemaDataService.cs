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
    private readonly ILogger<SchemaDataService> logger;

    public SchemaDataService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioActionLog audit,
        ILogger<SchemaDataService> logger)
    {
        this.options = options;
        this.resolver = resolver;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.audit = audit;
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

        // 4. The script, rendered before anything is applied, so the audit entry says what was run even
        //    when the run itself fails half way.
        MigrationPreview preview;
        try
        {
            preview = await RenderPreviewAsync(database, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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
                .ApplyAllConfiguredChangesToDatabaseAsync(ApplyMode, ct: cancellationToken)
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
        catch (OperationCanceledException)
        {
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
                byTable.TryGetValue(Key(row.Schema, row.Table), out IDocumentType? documentType);

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
                    documentType?.DocumentType.Name,
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
        string[] schemas = SchemaNames(resolved);

        try
        {
            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<IndexStatsRow> actual = await SchemaStatsQueries
                .ReadIndexesAsync(connection, schemas, CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            return IndexAdvice.Join(actual, DeclaredIndexes(resolved), DeclaredCollections(resolved));
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
        string[] schemas = SchemaNames(resolved);
        HashSet<string> declared = DeclaredFunctionNames(resolved.Database);

        try
        {
            await using NpgsqlConnection connection = resolved.Database.CreateConnection(ConnectionUsage.Read);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<FunctionStatsRow> rows = await SchemaStatsQueries
                .ReadFunctionsAsync(connection, schemas, CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            List<FunctionInfo> functions = new(rows.Count);
            foreach (FunctionStatsRow row in rows)
            {
                functions.Add(new FunctionInfo(
                    row.Schema,
                    row.Name,
                    row.Signature,
                    row.Definition,
                    declared.Contains(Key(row.Schema, row.Name))));
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
            // pool rather than onto the circuit's renderer.
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
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Weasel refuses to write an update script for a migration it judged Invalid, because applying
            // it would mean dropping something. Saying so beats an empty code block.
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

    private static string[] SchemaNames(ResolvedScope resolved)
    {
        try
        {
            return resolved.Database.AllSchemaNames();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [resolved.Store.Options.DatabaseSchemaName];
        }
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

            byTable[Key(documentType.TableName.Schema, documentType.TableName.Name)] = documentType;
        }

        return byTable;
    }

    /// <summary>
    /// Every index the store's configuration asks for, in this database.
    /// </summary>
    /// <remarks>
    /// Read from <c>AllObjects()</c> rather than from <c>IDocumentType.Indexes</c>, because the event
    /// store's tables are configured objects too and have no <c>IDocumentType</c> to hang off. The
    /// primary key is added by name for the same reason: Marten declares it as part of the table rather
    /// than as an entry in the index list, and an index list that called every primary key "undeclared"
    /// would be worse than no list at all.
    /// </remarks>
    private List<DeclaredIndex> DeclaredIndexes(ResolvedScope resolved)
    {
        Dictionary<string, IDocumentType> byTable = DocumentTypesByTable(resolved.Store.Options);
        List<DeclaredIndex> declared = [];

        foreach (ISchemaObject schemaObject in AllObjects(resolved.Database))
        {
            if (schemaObject is not Table table)
            {
                continue;
            }

            string schema = table.Identifier.Schema;
            string name = table.Identifier.Name;
            byTable.TryGetValue(Key(schema, name), out IDocumentType? documentType);

            string alias = documentType?.Alias ?? name;
            string typeName = documentType?.DocumentType.Name ?? name;

            if (!string.IsNullOrWhiteSpace(table.PrimaryKeyName))
            {
                declared.Add(new DeclaredIndex(
                    alias,
                    typeName,
                    schema,
                    name,
                    table.PrimaryKeyName,
                    "primary key (" + string.Join(", ", table.PrimaryKeyColumns) + ")"));
            }

            foreach (IndexDefinition index in table.Indexes)
            {
                declared.Add(new DeclaredIndex(alias, typeName, schema, name, index.Name, Ddl(index, table)));
            }
        }

        return declared;
    }

    private List<DeclaredCollection> DeclaredCollections(ResolvedScope resolved)
    {
        List<DeclaredCollection> collections = [];

        foreach (KeyValuePair<string, IDocumentType> entry in DocumentTypesByTable(resolved.Store.Options))
        {
            IDocumentType documentType = entry.Value;

            collections.Add(new DeclaredCollection(
                documentType.Alias,
                documentType.DocumentType.Name,
                documentType.TableName.Schema,
                documentType.TableName.Name,
                documentType.DuplicatedFields.Count > 0,
                SampleMember(documentType)));
        }

        return collections;
    }

    private static HashSet<string> DeclaredFunctionNames(IMartenDatabase database)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (ISchemaObject schemaObject in AllObjects(database))
        {
            if (schemaObject is Table)
            {
                continue;
            }

            names.Add(Key(schemaObject.Identifier.Schema, schemaObject.Identifier.Name));
        }

        return names;
    }

    /// <summary>
    /// Whatever <c>AllObjects()</c> answers, or nothing.
    /// </summary>
    /// <remarks>
    /// It builds every feature schema in the store, which is where a misconfigured feature surfaces. A
    /// tab that could not list declared objects still shows the ones Postgres has, so the failure is
    /// swallowed here rather than taken up to the page.
    /// </remarks>
    private static IReadOnlyList<ISchemaObject> AllObjects(IMartenDatabase database)
    {
        try
        {
            return [.. database.AllObjects()];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    private static string Ddl(IndexDefinition index, Table table)
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

    private static string Key(string schema, string name) => schema + "." + name;
}
