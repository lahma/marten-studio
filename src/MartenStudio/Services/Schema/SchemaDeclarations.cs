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
/// Everything the Schema screen's read paths need to know about what <c>StoreOptions</c> declares,
/// derived from the options alone.
/// </summary>
/// <param name="Schemas">The schemas this store owns in this database, in a stable order.</param>
/// <param name="Indexes">Every index the configuration asks for, on a table it manages.</param>
/// <param name="ManagedTables">
/// The qualified names of the tables Marten itself configures. An index on any other table in these
/// schemas belongs to the host application and no migration will touch it, which is a materially
/// different fact from "Marten does not declare it".
/// </param>
/// <param name="IgnoredIndexes">
/// The qualified index names the host told Marten's migration detection to leave alone
/// (<c>Schema.For&lt;T&gt;().IgnoreIndex(name)</c> / <c>Events.IgnoreIndex(name)</c>). Weasel removes
/// these from both sides of the delta, so they are neither created nor dropped.
/// </param>
/// <param name="Functions">The qualified names of the functions Marten installs.</param>
internal sealed record SchemaDeclarations(
    IReadOnlyList<string> Schemas,
    IReadOnlyList<DeclaredIndex> Indexes,
    IReadOnlySet<string> ManagedTables,
    IReadOnlySet<string> IgnoredIndexes,
    IReadOnlySet<string> Functions);

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
/// <c>mt_hilo</c> and <c>mt_get_next_hi</c>. Proven live by the P7 review, 2026-09-14.
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
        }

        foreach (IDocumentType documentType in storeOptions.AllKnownDocumentTypes())
        {
            string schema = string.IsNullOrWhiteSpace(documentType.TableName.Schema)
                ? documentType.DatabaseSchemaName
                : documentType.TableName.Schema;

            AddSchema(schema);

            string table = documentType.TableName.Name;
            managedTables.Add(SchemaKey.For(schema, table));

            bool visible = isDocumentTypeVisible is null || isDocumentTypeVisible(documentType.DocumentType);
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

        ReadEventStore(storeOptions, eventSchema, managedTables, ignoredIndexes, functions, indexes, AddSchema);

        return new SchemaDeclarations(orderedSchemas, indexes, managedTables, ignoredIndexes, functions);
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
    /// </remarks>
    private static void ReadEventStore(
        IReadOnlyStoreOptions storeOptions,
        string eventSchema,
        HashSet<string> managedTables,
        HashSet<string> ignoredIndexes,
        HashSet<string> functions,
        List<DeclaredIndex> indexes,
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
                continue;
            }

            if (schemaObject is not Table table)
            {
                continue;
            }

            string schema = table.Identifier.Schema;
            string name = table.Identifier.Name;
            managedTables.Add(SchemaKey.For(schema, name));

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

/// <summary>The qualified-name keys the Schema screen joins on.</summary>
/// <remarks>
/// One place, because a table key and an index key that disagreed about their separator would produce a
/// screen on which nothing is ever declared - which is exactly the failure that has to be loud.
/// </remarks>
internal static class SchemaKey
{
    /// <summary>A qualified table or function name.</summary>
    public static string For(string schema, string name) => schema + "." + name;

    /// <summary>A qualified index name.</summary>
    public static string For(string schema, string table, string name) => schema + "." + table + "." + name;
}

/// <summary>
/// A CLR type's name as a person would write it in C#.
/// </summary>
/// <remarks>
/// <c>Type.Name</c> renders a generic as <c>Envelope`1</c>, and a suggestion that reads
/// <c>opts.Schema.For&lt;Envelope`1&gt;()</c> is one nobody can paste. Nested types keep their outer
/// name, because <c>Outer.Inner</c> is what compiles.
/// </remarks>
internal static class SchemaTypeName
{
    /// <summary>The C#-friendly name of <paramref name="type" />.</summary>
    public static string Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        string name = type.Name;

        int tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        if (type.IsGenericType)
        {
            Type[] arguments = type.GetGenericArguments();
            List<string> rendered = new(arguments.Length);
            foreach (Type argument in arguments)
            {
                rendered.Add(Of(argument));
            }

            name = name + "<" + string.Join(", ", rendered) + ">";
        }

        return type.DeclaringType is { } declaring && !type.IsGenericParameter
            ? Of(declaring) + "." + name
            : name;
    }
}
