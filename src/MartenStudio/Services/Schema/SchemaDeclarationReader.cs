using JasperFx;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;

using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql.Functions;
using Weasel.Postgresql.Tables;

namespace MartenStudio.Services.Schema;

/// <summary>
/// Reads what a store's configuration declares, without asking the database anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the obvious API applies migrations.</b> <c>IMartenDatabase.AllSchemaNames()</c>
/// is <c>AllObjects().Select(x =&gt; x.Identifier.Schema).Distinct()</c>, <c>AllObjects()</c> is
/// <c>BuildFeatureSchemas().SelectMany(x =&gt; x.Objects)</c>, and Marten's
/// <c>BuildFeatureSchemas()</c> is <c>StorageFeatures.AllActiveFeatures(this)</c> — which reaches
/// <c>database.Sequences</c> whenever any document type has a numeric or HiLo id. That property is a
/// <c>Lazy&lt;SequenceFactory&gt;</c> whose factory calls <c>generateOrUpdateFeature(...)</c> and blocks
/// on it, so it runs <c>Migrator.ApplyAllAsync</c> under the database's own <c>AutoCreate</c> — the
/// default being <c>CreateOrUpdate</c>. A single <c>AllSchemaNames()</c> call on an empty schema creates
/// <c>mt_hilo</c> and <c>mt_get_next_hi</c>. Proven live by the P7 review, 2026-09-14, and still so at the
/// floor; from Marten 9.50.3 (#5665) <c>AllActiveFeatures</c> yields <c>database.SequenceFeature</c>, which is
/// described and never applied, and <c>SchemaNoDdlLiveTests</c> pins both ends.
/// </para>
/// <para>
/// So a read path never calls <c>AllSchemaNames()</c>, <c>AllObjects()</c>, <c>ToDatabaseScript()</c> or
/// <c>CreateMigrationAsync()</c>. Everything below comes from <c>IReadOnlyStoreOptions</c>, from
/// <c>IDocumentType</c>, and from the event store's own feature schema — all of which are in-memory
/// object graphs that touch no connection.
/// </para>
/// <para>
/// The one thing that cannot be read from a public API is the set of helper functions Marten installs
/// for document storage: <c>StorageFeatures.SystemFunctions</c> and the <c>SequenceFactory</c> are both
/// internal. Those are listed by name in <see cref="DocumentSchemaFunctions" />, verified against a live
/// Marten 9.35 schema by <c>SchemaLiveTests</c>, which fails if Marten ever installs one this list does
/// not know about.
/// </para>
/// </remarks>
internal static class SchemaDeclarationReader
{
    /// <summary>
    /// The functions Marten installs into the <em>document</em> schema.
    /// </summary>
    /// <remarks>
    /// From <c>Marten.Storage.StorageFeatures.PostProcessConfiguration()</c> (the twenty
    /// <c>SystemFunctions.AddSystemFunction</c> calls) plus <c>mt_get_next_hi</c>, which the HiLo
    /// <c>SequenceFactory</c> declares beside <c>mt_hilo</c>. Both types are internal to Marten, which is
    /// why this is a list rather than a walk. <c>SchemaLiveTests</c> asserts a fresh Marten schema
    /// contains nothing outside it.
    /// </remarks>
    internal static readonly string[] DocumentSchemaFunctions =
    [
        "mt_immutable_timestamp",
        "mt_immutable_timestamptz",
        "mt_immutable_time",
        "mt_immutable_date",
        "mt_grams_vector",
        "mt_grams_query",
        "mt_grams_array",
        "mt_jsonb_append",
        "mt_jsonb_append_key_value",
        "mt_jsonb_copy",
        "mt_jsonb_duplicate",
        "mt_jsonb_fix_null_parent",
        "mt_jsonb_increment",
        "mt_jsonb_insert",
        "mt_jsonb_move",
        "mt_jsonb_path_to_array",
        "mt_jsonb_remove",
        "mt_jsonb_remove_key",
        "mt_jsonb_patch",
        "mt_safe_unaccent",
        "mt_get_next_hi",
    ];

    /// <summary>
    /// Reads everything the Schema screen needs from one store's options.
    /// </summary>
    /// <param name="storeOptions">The store's read-only options.</param>
    /// <param name="isDocumentTypeVisible">
    /// <c>MartenStudioOptions.IsDocumentTypeVisible</c>, applied only to the collection names and the
    /// index attribution — never to the schema list, because a hidden type's table still occupies the
    /// schema and a Tables tab that silently omitted a schema would be lying about disk.
    /// </param>
    public static SchemaDeclarations Read(
        IReadOnlyStoreOptions storeOptions,
        Func<Type, bool>? isDocumentTypeVisible = null)
    {
        ArgumentNullException.ThrowIfNull(storeOptions);

        HashSet<string> schemas = new(StringComparer.OrdinalIgnoreCase);
        List<string> orderedSchemas = [];
        HashSet<string> managedTables = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ignoredIndexes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> functions = new(StringComparer.OrdinalIgnoreCase);
        List<DeclaredIndex> indexes = [];
        Dictionary<string, MartenDeclaredObject> objects = new(StringComparer.OrdinalIgnoreCase);

        void AddSchema(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && schemas.Add(name))
            {
                orderedSchemas.Add(name);
            }
        }

        string documentSchema = storeOptions.DatabaseSchemaName;
        string eventSchema = storeOptions.Events.DatabaseSchemaName;

        AddSchema(documentSchema);
        AddSchema(eventSchema);

        foreach (string name in DocumentSchemaFunctions)
        {
            functions.Add(SchemaKey.For(documentSchema, name));
            Declare(objects, documentSchema, name, MartenObjectKind.Function);
        }

        // The HiLo table beside its function. The SequenceFactory that declares it is internal and is only
        // reachable through the migrating feature walk (hard rule 14), so it is named here - an entry for a
        // table that is not there yet classifies nothing and costs nothing.
        Declare(objects, documentSchema, HiloTable, MartenObjectKind.Infrastructure);

        foreach (IDocumentType documentType in storeOptions.AllKnownDocumentTypes())
        {
            string schema = string.IsNullOrWhiteSpace(documentType.TableName.Schema)
                ? documentType.DatabaseSchemaName
                : documentType.TableName.Schema;

            AddSchema(schema);

            string table = documentType.TableName.Name;
            managedTables.Add(SchemaKey.For(schema, table));

            bool visible = isDocumentTypeVisible is null || isDocumentTypeVisible(documentType.DocumentType);

            Declare(objects, schema, table, MartenObjectKind.DocumentTable, visible ? documentType.Alias : null, visible);
            string alias = visible ? documentType.Alias : table;
            string typeName = visible ? SchemaTypeName.Of(documentType.DocumentType) : table;

            // ToDDL only reads the parent's identifier and partitioning, and a document table that is
            // partitioned renders the same CREATE INDEX for the parent - so a bare Table of the right
            // name is enough to render the statement Weasel would write.
            var parent = new Table(documentType.TableName);

            foreach (IndexDefinition index in documentType.Indexes)
            {
                indexes.Add(Describe(index, parent, alias, typeName, schema, table));
            }

            if (documentType is DocumentMapping mapping)
            {
                foreach (string ignored in mapping.IgnoredIndexes)
                {
                    ignoredIndexes.Add(SchemaKey.For(schema, table, ignored));
                }

                // The three indexes Marten adds to the table rather than to the mapping, in
                // Marten.Storage.DocumentTable's constructor. They are structural - they follow from the
                // delete style, the hierarchy and the tenancy ordering, not from an Index() call - and an
                // Indexes tab that called them undeclared would be telling somebody Marten is about to
                // drop its own soft-delete index.
                if (documentType.IsHierarchy())
                {
                    indexes.Add(Describe(new DocumentIndex(mapping, "mt_doc_type"), parent, alias, typeName, schema, table));
                }

                if (mapping.DeleteStyle == DeleteStyle.SoftDelete)
                {
                    indexes.Add(Describe(new DocumentIndex(mapping, "mt_deleted"), parent, alias, typeName, schema, table));
                }

                if (mapping.TenancyStyle == TenancyStyle.Conjoined
                    && mapping.PrimaryKeyTenancyOrdering == PrimaryKeyTenancyOrdering.Id_Then_TenantId)
                {
                    indexes.Add(Describe(new DocumentIndex(mapping, "tenant_id"), parent, alias, typeName, schema, table));
                }
            }
        }

        ReadEventStore(storeOptions, eventSchema, managedTables, ignoredIndexes, functions, indexes, objects, AddSchema);

        string? objectsFailure = ReadExtendedObjects(storeOptions, objects, managedTables, indexes, ignoredIndexes);

        return new SchemaDeclarations(orderedSchemas, indexes, managedTables, ignoredIndexes, functions)
        {
            Objects = objects,
            ObjectsFailure = objectsFailure,
        };
    }

    /// <summary>
    /// Reads one store's declarations for <em>classification</em>, failing closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The database browser asks this whose each table is, and a table nobody claims is shown as the
    /// host's own - with its rows readable once the host has turned the browser on. So a read that throws
    /// cannot become an empty map here the way <see cref="SchemaNames" /> becomes two schema names: an
    /// empty map would label every Marten table "Other" and hand its rows to a raw reader that knows
    /// nothing of tenancy or soft delete. The caller gets the failure, with the configuration's own
    /// message, and shows it instead of a classification.
    /// </para>
    /// <para>
    /// Touches no connection, like <see cref="Read" />: <c>IReadOnlyStoreOptions</c>, <c>IDocumentType</c>,
    /// the event store's feature objects and <c>StoreOptions.Storage.ExtendedSchemaObjects</c> are all
    /// in-memory object graphs.
    /// </para>
    /// </remarks>
    /// <param name="storeOptions">The store's read-only options.</param>
    /// <param name="isDocumentTypeVisible"><c>MartenStudioOptions.IsDocumentTypeVisible</c>.</param>
    public static SchemaDeclarationRead ReadForClassification(
        IReadOnlyStoreOptions storeOptions,
        Func<Type, bool>? isDocumentTypeVisible = null)
    {
        ArgumentNullException.ThrowIfNull(storeOptions);

        try
        {
            SchemaDeclarations declarations = Read(storeOptions, isDocumentTypeVisible);

            return declarations.ObjectsFailure is { } failure
                ? SchemaDeclarationRead.Failed(failure)
                : SchemaDeclarationRead.Success(declarations);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SchemaDeclarationRead.Failed(exception.Message);
        }
    }

    /// <summary>The HiLo bookkeeping table Marten creates in the document schema.</summary>
    internal const string HiloTable = "mt_hilo";

    /// <summary>
    /// Adds <c>StoreOptions.Storage.ExtendedSchemaObjects</c> to the map, or says why it could not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IReadOnlyStoreOptions</c> has no <c>Storage</c>; the list lives on the concrete
    /// <c>StoreOptions</c>, which is the only implementation Marten has. A different implementation is
    /// therefore a map that cannot be completed, and it is reported as one rather than treated as "no
    /// extended objects".
    /// </para>
    /// <para>
    /// Caught and recorded here, not thrown, so that the Schema screen's callers of <see cref="Read" /> -
    /// which never needed this - are not newly exposed to it.
    /// </para>
    /// </remarks>
    private static string? ReadExtendedObjects(
        IReadOnlyStoreOptions storeOptions,
        Dictionary<string, MartenDeclaredObject> objects,
        HashSet<string> managedTables,
        List<DeclaredIndex> indexes,
        HashSet<string> ignoredIndexes)
    {
        if (storeOptions is not StoreOptions concrete)
        {
            return "The store's options are not a Marten StoreOptions, so its ExtendedSchemaObjects cannot be read.";
        }

        try
        {
            foreach (ISchemaObject schemaObject in concrete.Storage.ExtendedSchemaObjects)
            {
                if (schemaObject is Weasel.Postgresql.Extension)
                {
                    // An extension's own objects are excluded from the browser wholesale (pg_depend
                    // deptype 'e'), so there is nothing to classify.
                    continue;
                }

                MartenObjectKind kind = schemaObject switch
                {
                    Function => MartenObjectKind.Function,
                    Weasel.Postgresql.Sequence => MartenObjectKind.Sequence,

                    // A table, a view, or an ISchemaObject of the host's own: relational, Marten-managed,
                    // and the host's data - the same standing as a flat-table projection's table.
                    _ => MartenObjectKind.ProjectionOrExtendedTable,
                };

                DbObjectName identifier = schemaObject.Identifier;
                string schema = string.IsNullOrWhiteSpace(identifier.Schema)
                    ? storeOptions.DatabaseSchemaName
                    : identifier.Schema;

                // Its schema is deliberately not added to SchemaDeclarations.Schemas: that list is D27's "store's
                // own schemas", which the database browser shows the structure of without BrowseDatabase and the
                // Schema screen lists, and an extended object in a schema of its own must not widen either. The
                // pair travels on the entry instead, for the health checks that read the table (MartenTables).
                Declare(objects, schema, identifier.Name, kind);

                // An apply migrates these like Marten's own tables - StorageFeatures yields itself as a
                // feature whenever the list is not empty - so a table here is a managed table, its indexes
                // are declared, and its ignored indexes are ignored. Without this the Indexes tab calls its
                // declared indexes undeclared, and its hand-made ones safe (DB-7).
                if (schemaObject is Table table)
                {
                    managedTables.Add(SchemaKey.For(schema, identifier.Name));

                    foreach (string ignored in table.IgnoredIndexes)
                    {
                        ignoredIndexes.Add(SchemaKey.For(schema, identifier.Name, ignored));
                    }

                    foreach (IndexDefinition index in table.Indexes)
                    {
                        indexes.Add(Describe(index, table, ExtendedAlias, ExtendedAlias, schema, identifier.Name));
                    }
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "StoreOptions.Storage.ExtendedSchemaObjects could not be read: " + exception.Message;
        }
    }

    /// <summary>
    /// The schemas a store owns, when that is all the caller needs.
    /// </summary>
    /// <remarks>
    /// Falls back to the two schema names on the options when the mappings will not build, because a
    /// misconfigured document type must not take the whole screen down - the Drift tab is where that
    /// belongs, and it will say so in Marten's own words.
    /// </remarks>
    public static string[] SchemaNames(IReadOnlyStoreOptions storeOptions)
    {
        ArgumentNullException.ThrowIfNull(storeOptions);

        try
        {
            return [.. Read(storeOptions).Schemas];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase)
            {
                storeOptions.DatabaseSchemaName,
                storeOptions.Events.DatabaseSchemaName,
            };

            return [.. names];
        }
    }

    /// <summary>
    /// Adds the event store's own tables, indexes and functions.
    /// </summary>
    /// <remarks>
    /// <c>EventGraph</c> implements <c>IFeatureSchema</c> explicitly, and its <c>Objects</c> are built by
    /// constructors alone - <c>StreamsTable</c>, <c>EventsTable</c>, the progression tables, the
    /// <c>mt_archive_stream</c> and <c>mt_quick_append_events</c> functions. Nothing there opens a
    /// connection, which is the whole reason this reaches the event store this way instead of through
    /// <c>AllObjects()</c>: the sequence feature is what ran DDL, and it is not in here.
    /// <para>
    /// For the kind map, a table is the event store's own when its type is one of Marten's
    /// (<c>StreamsTable</c>, <c>EventsTable</c>, <c>EventProgressionTable</c>, <c>NaturalKeyTable</c>,
    /// <c>EventTagTable</c>, <c>DcbTagVersionTable</c> - all internal to the Marten assembly), and a
    /// projection's when it is anything else: a flat-table projection's <c>Table</c> and an
    /// <c>EventProjection.SchemaObjects</c> table are plain Weasel tables the host shaped, holding the host's
    /// own data. The assembly is the test rather than a list of names so that a table Marten adds in a
    /// later version is classified as Marten's without anyone updating a list.
    /// </para>
    /// </remarks>
    private static void ReadEventStore(
        IReadOnlyStoreOptions storeOptions,
        string eventSchema,
        HashSet<string> managedTables,
        HashSet<string> ignoredIndexes,
        HashSet<string> functions,
        List<DeclaredIndex> indexes,
        Dictionary<string, MartenDeclaredObject> objects,
        Action<string?> addSchema)
    {
        if (storeOptions.Events is not IFeatureSchema feature)
        {
            return;
        }

        foreach (string ignored in storeOptions.Events.IgnoredIndexes)
        {
            // The event store's ignore list is not per table, so it is recorded against every event
            // table. That matches what Marten does with it.
            ignoredIndexes.Add(SchemaKey.For(eventSchema, ignored));
        }

        // Deliberately not wrapped in a catch. A store whose event configuration will not build has to
        // say so on screen: swallowing it here would leave every event index looking like something
        // Marten does not manage, which now reads as a verdict about what an apply would do to it. The
        // callers turn this into SchemaIndexes/SchemaFunctions.Unavailable with the message on it, and
        // SchemaNames keeps its own fallback so the Tables tab still works (P7-fix follow-up 4).
        foreach (ISchemaObject schemaObject in feature.Objects)
        {
            addSchema(schemaObject.Identifier.Schema);

            if (schemaObject is Function)
            {
                functions.Add(SchemaKey.For(schemaObject.Identifier.Schema, schemaObject.Identifier.Name));
                Declare(objects, schemaObject.Identifier.Schema, schemaObject.Identifier.Name, MartenObjectKind.Function);
                continue;
            }

            if (schemaObject is Weasel.Postgresql.Sequence)
            {
                // mt_events_sequence. Never on the Schema screen's lists, which is why nothing but the kind
                // map records it. The per-tenant sequences (PerTenantEventSequences) are a wrapper with a
                // placeholder identifier and are skipped: their real names carry tenant ids.
                Declare(objects, schemaObject.Identifier.Schema, schemaObject.Identifier.Name, MartenObjectKind.Sequence);
                continue;
            }

            if (schemaObject is not Table table)
            {
                if (schemaObject is Weasel.Postgresql.Views.View)
                {
                    // A projection that declares a view: relational, the host's data, Marten-managed.
                    Declare(objects, schemaObject.Identifier.Schema, schemaObject.Identifier.Name, MartenObjectKind.ProjectionOrExtendedTable);
                }

                continue;
            }

            string schema = table.Identifier.Schema;
            string name = table.Identifier.Name;
            managedTables.Add(SchemaKey.For(schema, name));

            Declare(
                objects,
                schema,
                name,
                table.GetType().Assembly == typeof(StoreOptions).Assembly
                    ? MartenObjectKind.EventTable
                    : MartenObjectKind.ProjectionOrExtendedTable);

            foreach (string ignored in table.IgnoredIndexes)
            {
                ignoredIndexes.Add(SchemaKey.For(schema, name, ignored));
            }

            foreach (IndexDefinition index in table.Indexes)
            {
                indexes.Add(Describe(index, table, EventStoreAlias, EventStoreAlias, schema, name));
            }
        }
    }

    /// <summary>What the event store's tables are attributed to, where a document type would be named.</summary>
    internal const string EventStoreAlias = "event store";

    /// <summary>What an <c>ExtendedSchemaObjects</c> table's indexes are attributed to (= <c>IndexAdvice.ManagedTableAlias</c>).</summary>
    internal const string ExtendedAlias = "Marten-managed";

    /// <summary>
    /// Adds one object to the kind map under its key, carrying the schema and name the key was built from - the
    /// one place an entry is made, so that the two can never disagree. The first declaration of a name wins.
    /// </summary>
    private static void Declare(
        Dictionary<string, MartenDeclaredObject> objects,
        string schema,
        string name,
        MartenObjectKind kind,
        string? alias = null,
        bool visible = true) =>
        objects.TryAdd(SchemaKey.For(schema, name), new MartenDeclaredObject(schema, name, kind, alias, visible));

    private static DeclaredIndex Describe(
        IndexDefinition index,
        Table parent,
        string alias,
        string typeName,
        string schema,
        string table) =>
        new(alias, typeName, schema, table, index.Name, Ddl(index, parent));

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
}
