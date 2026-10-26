global using MartenTable = PostgresStudio.Services.Sources.StudioTable;

using System.Runtime.CompilerServices;

using JasperFx;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;

using MartenStudio.Services.Schema;
using PostgresStudio.Services.Sources;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// What a Marten table is, for the tables-and-indexes checks: MartenStudio's names for the shared
/// <see cref="StudioTableKind" /> members, so that the Marten-only checks keep reading as they always have.
/// </summary>
/// <remarks>
/// A table the store declares is a <see cref="StudioTable" /> (<c>MartenTable</c> is that type's name in MartenStudio, a
/// global alias), and its kind is the shared enum; these are its constants under the names the Marten checks use.
/// </remarks>
internal static class MartenTableKind
{
    /// <summary>A visible document type's table - the only kind a <c>Schema.For&lt;T&gt;()</c> remedy fits.</summary>
    public const StudioTableKind Document = StudioTableKind.DocumentTable;

    /// <summary>One of the event store's own tables.</summary>
    public const StudioTableKind EventStore = StudioTableKind.EventTable;

    /// <summary>A projection's table, or one of <c>StoreOptions.Storage.ExtendedSchemaObjects</c>.</summary>
    public const StudioTableKind Managed = StudioTableKind.Projection;

    /// <summary>Marten's own bookkeeping (<c>mt_hilo</c>).</summary>
    public const StudioTableKind Infrastructure = StudioTableKind.Bookkeeping;
}

/// <summary>
/// The tables this store declares in this database, from its configuration alone - and what it declares about their
/// indexes. The declared half of MartenStudio's judged set (<see cref="MartenDatabaseTableSet" />), and an
/// <see cref="IStudioTableSet" /> of its own for a context with no table source.
/// </summary>
/// <param name="Tables">Every declared table but a hidden document type's, in schema and table order.</param>
/// <param name="Declarations">What <see cref="SchemaDeclarationReader" /> read.</param>
/// <param name="StructuralIndexes">The keys (<c>schema.table.index</c>) of the indexes Marten adds by itself.</param>
internal sealed record MartenTableSet(
    IReadOnlyList<StudioTable> Tables,
    SchemaDeclarations Declarations,
    IReadOnlySet<string> StructuralIndexes) : IStudioTableSet
{
    private readonly Dictionary<string, StudioTable> byKey = Tables.ToDictionary(static x => x.Key, StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> declared = new(
        Declarations.Indexes.Select(static x => SchemaKey.For(x.Schema, x.Table, x.Name)),
        StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    /// <remarks>Always <see langword="null" />: the set is every table the store declares, which is never capped.</remarks>
    public string? Coverage => null;

    /// <summary>What the statements bind.</summary>
    public IReadOnlyList<HealthTableTarget> Targets => [.. Tables.Select(static x => x.Target)];

    /// <inheritdoc />
    public StudioTable? Find(string schema, string table) => byKey.GetValueOrDefault(SchemaKey.For(schema, table));

    /// <inheritdoc />
    public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(table);

        if (index.IsPrimaryKey)
        {
            return IndexOrigin.PrimaryKey;
        }

        if (StructuralIndexes.Contains(index.Key))
        {
            return IndexOrigin.Structural;
        }

        if (declared.Contains(index.Key))
        {
            return table.Kind switch
            {
                StudioTableKind.EventTable => IndexOrigin.EventStore,
                StudioTableKind.Projection => IndexOrigin.Managed,
                _ => IndexOrigin.Declared,
            };
        }

        // The event store's ignore list is not per table, so it is keyed by schema and index alone.
        return Declarations.IgnoredIndexes.Contains(index.Key) || Declarations.IgnoredIndexes.Contains(SchemaKey.For(index.Schema, index.Name))
            ? IndexOrigin.Ignored
            : IndexOrigin.Undeclared;
    }
}

/// <summary>
/// Reads the tables this store declares, for the tables-and-indexes checks: the declared half of MartenStudio's judged set
/// (<see cref="MartenHealthTableSource" /> lays it over the catalog), and the Marten-only checks' way to the shared reader
/// (<see cref="HealthTables" />).
/// </summary>
/// <remarks>
/// <para>
/// <b>The declared set comes from the configuration, never from the database</b>: <see cref="SchemaDeclarationReader" />,
/// which reads <c>StoreOptions</c> and <c>IDocumentType</c> and touches no connection - never
/// <c>AllObjects()</c> or <c>AllSchemaNames()</c>, which migrate (AGENTS.md hard rule 14). So a "Marten table"
/// here is one the store declares; a leftover <c>mt_doc_*</c> table no registered type names is not one. The catalog's
/// other tables are <see cref="MartenHealthTableSource" />'s to add, and it leaves every table named like a document table
/// out of its read while a type may be hidden - which is what keeps a hidden type nothing in the process has touched out
/// of every finding.
/// </para>
/// <para>
/// <b>Every table the store declares is the application's own</b> (<see cref="StudioTableOwnership.Owned" />), its event
/// tables and bookkeeping included - kept so on purpose when the set was widened (issue #8, G1), rather than calling
/// Marten's own tables a library's: their indexes are told apart by where they come from (<see cref="IndexOrigin" />),
/// which already keeps Marten's own design out of the index-design findings, and a hand-made index on <c>mt_events</c> is
/// still one the next apply drops, so moving them would change findings for no host's benefit. The library
/// classification of <c>mt_*</c> is for a table whose declarations are not known.
/// </para>
/// <para>
/// <b>A hidden document type is never read about.</b> Its table is left out of the set, so its name is not
/// even a parameter of a statement, and a finding names the document type it is about besides, so the sweep
/// would drop it anyway.
/// </para>
/// </remarks>
internal static class MartenTables
{
    /// <summary>The document type behind each document table this process described, by the table's instance.</summary>
    private static readonly ConditionalWeakTable<StudioTable, IDocumentType> DocumentTypeOf = new();

    /// <summary>The memo key of the declared-table set, as MartenStudio built it.</summary>
    internal const string SetKey = "tables.marten-set";

    /// <summary>One table the store declares, as the checks know it.</summary>
    /// <param name="schema">The schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="kind">What it is.</param>
    /// <param name="outsideStoreSchemas">
    /// Whether <paramref name="schema" /> is not one of the store's own schemas (<c>SchemaDeclarations.Schemas</c>): an
    /// <c>ExtendedSchemaObjects</c> table in a schema of its own. The database browser withholds such a schema from a
    /// visitor short of its gate (D27), so a finding naming the table carries it in <see cref="HealthFinding.GatedSchemas" />.
    /// Required, and computed for every table <see cref="Read" /> lists: a default would be the fail-open answer.
    /// </param>
    /// <param name="documentType">The document type, for a document table; never a hidden one.</param>
    public static StudioTable Table(string schema, string table, StudioTableKind kind, bool outsideStoreSchemas, IDocumentType? documentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        StudioTable described = new(schema, table, kind, StudioTableOwnership.Owned)
        {
            GatedSchemas = outsideStoreSchemas ? [schema] : [],
            TypeName = documentType is null ? null : SchemaTypeName.Of(documentType.DocumentType),
            Alias = documentType?.Alias,
            SoftDeleted = documentType is DocumentMapping { DeleteStyle: DeleteStyle.SoftDelete },
            DocumentTypes = documentType is null ? [] : [documentType.DocumentType],
        };

        if (documentType is not null)
        {
            DocumentTypeOf.AddOrUpdate(described, documentType);
        }

        return described;
    }

    extension(StudioTable table)
    {
        /// <summary>
        /// The document type, for a document table <see cref="MartenTables.Table" /> described; <see langword="null" /> for
        /// any other table, and for one no Marten store of this process described.
        /// </summary>
        public IDocumentType? DocumentType => DocumentTypeOf.TryGetValue(table, out IDocumentType? documentType) ? documentType : null;

        /// <summary>The mapping, when the document type is Marten's own <see cref="DocumentMapping" />.</summary>
        public DocumentMapping? Mapping => table.DocumentType as DocumentMapping;

        /// <summary>
        /// Whether the store's configuration declares the table - one <see cref="MartenTables.Table" /> described - rather
        /// than the catalog listing it beside the store (<see cref="MartenHealthTableSource" />).
        /// </summary>
        /// <remarks>
        /// Told apart by shape, which the two halves of the set keep disjoint: a declared table is always
        /// <see cref="StudioTableOwnership.Owned" /> and never <see cref="StudioTableKind.Relational" />, and an undeclared one
        /// is either the host's relational table (Owned and Relational) or a library's or somebody else's. So a check or a
        /// remedy that only makes sense for this store's own tables - T09's sample, T22's guarded writes, a
        /// <c>StoreOptions</c> line - asks this, and a table that merely sits beside the store is never mistaken for one.
        /// </remarks>
        public bool IsDeclaredByStore => table.Ownership == StudioTableOwnership.Owned && table.Kind != StudioTableKind.Relational;
    }

    /// <summary>The tables the store declares, a hidden type's left out.</summary>
    public static MartenTableSet Read(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyStoreOptions options = context.StoreOptions;
        SchemaDeclarations declarations = SchemaDeclarationReader.Read(options, context.IsDocumentTypeVisible);

        List<StudioTable> tables = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> structural = new(StringComparer.OrdinalIgnoreCase);

        // Ordinal, as the database browser's gate compares schema names: a near miss is gated, never waved through.
        HashSet<string> storeSchemas = new(declarations.Schemas, StringComparer.Ordinal);
        bool OutsideStore(string schema) => !storeSchemas.Contains(schema);

        foreach (IDocumentType documentType in options.AllKnownDocumentTypes())
        {
            string schema = string.IsNullOrWhiteSpace(documentType.TableName.Schema)
                ? documentType.DatabaseSchemaName
                : documentType.TableName.Schema;
            string table = documentType.TableName.Name;

            // Hidden or not, its table is nobody else's: the declarations' own entry for it is skipped below.
            seen.Add(SchemaKey.For(schema, table));

            if (!context.IsDocumentTypeVisible(documentType.DocumentType))
            {
                continue;
            }

            tables.Add(Table(schema, table, StudioTableKind.DocumentTable, OutsideStore(schema), documentType));

            if (documentType is DocumentMapping mapping)
            {
                foreach (string name in StructuralIndexNames(mapping))
                {
                    structural.Add(SchemaKey.For(schema, table, name));
                }
            }
        }

        foreach ((string key, MartenDeclaredObject declared) in declarations.Objects)
        {
            StudioTableKind? kind = declared.Kind switch
            {
                MartenObjectKind.EventTable => StudioTableKind.EventTable,
                MartenObjectKind.ProjectionOrExtendedTable => StudioTableKind.Projection,
                MartenObjectKind.Infrastructure => StudioTableKind.Bookkeeping,
                _ => null,
            };

            // The entry's own schema and name, never the key split back against SchemaDeclarations.Schemas: an
            // ExtendedSchemaObjects table may sit in a schema of its own, which that list deliberately leaves out
            // (D27), and a split that found no schema skipped the table without a word.
            if (kind is null || !seen.Add(key))
            {
                continue;
            }

            tables.Add(Table(declared.Schema, declared.Name, kind.Value, OutsideStore(declared.Schema)));
        }

        tables.Sort(static (a, b) =>
        {
            int bySchema = string.CompareOrdinal(a.Schema, b.Schema);
            return bySchema != 0 ? bySchema : string.CompareOrdinal(a.Table, b.Table);
        });

        return new MartenTableSet(tables, declarations, structural);
    }

    /// <summary>The declared tables, built once per sweep for every check that asks.</summary>
    public static Task<MartenTableSet> SetAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(SetKey, _ => Task.FromResult(Read(context)));
    }

    /// <summary>
    /// The judged tables and the catalog's row for each - the shared read (<see cref="HealthTables" />), read once per sweep
    /// whichever checks ask. A Marten-only check that is about the store's own tables alone keeps the ones
    /// <c>IsDeclaredByStore</c> says are.
    /// </summary>
    /// <remarks>
    /// <b>The sweep's own set, never a second one.</b> The shared read is memoised under one key, and the first set handed
    /// to it is the one read, so a check that asked with the declared set before T01 asked with the whole database's would
    /// have narrowed every table check of that sweep to the declared tables. So where the studio registered its table
    /// source - every sweep - this reads that source's set; only a context built by hand with no source reads the declared
    /// set.
    /// </remarks>
    public static async Task<IReadOnlyList<TableFacts>> TablesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IStudioTableSet set = await JudgedSetAsync(context).ConfigureAwait(false);
        return await HealthTables.TablesAsync(context, set).ConfigureAwait(false);
    }

    /// <summary>The sweep's judged set when the studio registered its table source, and the declared set otherwise.</summary>
    private static async Task<IStudioTableSet> JudgedSetAsync(HealthCheckContext context) =>
        context.Services.GetService(typeof(IHealthTableSource)) is not null
            ? await HealthTables.SetAsync(context).ConfigureAwait(false)
            : await SetAsync(context).ConfigureAwait(false);

    /// <summary>
    /// The indexes <c>Marten.Storage.DocumentTable</c> adds to a document table by itself, named the way it
    /// names them: <c>mt_doc_type</c> for a hierarchy, <c>mt_deleted</c> for soft deletes, and <c>tenant_id</c>
    /// for a conjoined table whose primary key leads with the id (verified at V9.39.0 and V9.50.2).
    /// </summary>
    internal static IEnumerable<string> StructuralIndexNames(DocumentMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (mapping.IsHierarchy())
        {
            yield return new DocumentIndex(mapping, "mt_doc_type").Name;
        }

        if (mapping.DeleteStyle == DeleteStyle.SoftDelete)
        {
            yield return new DocumentIndex(mapping, "mt_deleted").Name;
        }

        if (mapping.TenancyStyle == TenancyStyle.Conjoined
            && mapping.PrimaryKeyTenancyOrdering == PrimaryKeyTenancyOrdering.Id_Then_TenantId)
        {
            yield return new DocumentIndex(mapping, "tenant_id").Name;
        }
    }
}
