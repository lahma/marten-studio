using Marten;
using Marten.Schema;

using MartenStudio.Internal;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Schema;

using Weasel.Postgresql.Tables;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// MartenStudio's half of the Relationships graph: what <c>StoreOptions</c> says about the store's document types -
/// which are drawn, which are hidden, which members sit behind which columns, and which foreign keys the
/// configuration declares - as the shared builder takes it.
/// </summary>
/// <remarks>
/// <para>
/// Pure: everything here reads <see cref="IReadOnlyStoreOptions" /> and <see cref="IDocumentType" />, which touch no
/// connection, and nothing here builds schema (AGENTS.md hard rule 14). The row estimates are handed in.
/// </para>
/// <para>
/// <b>Hidden types are hidden tables.</b> A mapping the host hid with <c>IsDocumentTypeVisible</c>, and Marten's own
/// infrastructure types, are not nodes; their tables go into <see cref="RelationshipNodeSet.HiddenTables" />, which is
/// how the builder drops every key that touches one rather than listing it as a key nothing could place - naming the
/// hidden collection in that list would be the visibility gate leaking through the very relationship it was set to
/// hide.
/// </para>
/// </remarks>
internal static class MartenRelationshipGraph
{
    /// <summary>The store's nodes, its hidden tables, its own schemas and the members behind its columns.</summary>
    /// <param name="storeOptions">The scoped store's read-only options.</param>
    /// <param name="isVisible"><c>MartenStudioOptions.IsDocumentTypeVisible</c>, or <see langword="null" />.</param>
    /// <param name="estimates">
    /// <c>reltuples</c> per table, keyed as <c>DocumentBrowseQueries.Key</c> spells it. Empty is a valid argument -
    /// the referenced-by read builds the same graph and has no use for the counts.
    /// </param>
    public static RelationshipNodeSet Nodes(
        IReadOnlyStoreOptions storeOptions,
        Func<Type, bool>? isVisible,
        IReadOnlyDictionary<string, long> estimates)
    {
        ArgumentNullException.ThrowIfNull(storeOptions);
        ArgumentNullException.ThrowIfNull(estimates);

        List<RelationshipNode> nodes = [];
        List<(string Schema, string Table)> hidden = [];
        Dictionary<string, IReadOnlyDictionary<string, string>> members = new(StringComparer.OrdinalIgnoreCase);

        foreach (IDocumentType documentType in storeOptions.AllKnownDocumentTypes())
        {
            if (!IsDrawn(documentType, isVisible))
            {
                hidden.Add((documentType.TableName.Schema, documentType.TableName.Name));
                continue;
            }

            nodes.Add(Describe(documentType, estimates));
            members[documentType.Alias] = MembersOf(documentType);
        }

        return new RelationshipNodeSet(nodes, hidden, SchemaDeclarationReader.SchemaNames(storeOptions))
        {
            Members = members,
        };
    }

    /// <summary>
    /// Every foreign key the configuration declares on a drawn document type, with its target resolved to a drawn
    /// document type where the configuration says which.
    /// </summary>
    /// <param name="storeOptions">The scoped store's read-only options.</param>
    /// <param name="isVisible"><c>MartenStudioOptions.IsDocumentTypeVisible</c>, or <see langword="null" />.</param>
    public static IReadOnlyList<RelationshipDeclaredKey> DeclaredKeys(IReadOnlyStoreOptions storeOptions, Func<Type, bool>? isVisible)
    {
        ArgumentNullException.ThrowIfNull(storeOptions);

        List<IDocumentType> visible = [];
        Dictionary<string, IDocumentType> byTable = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<Type, IDocumentType> byClrType = [];

        foreach (IDocumentType documentType in storeOptions.AllKnownDocumentTypes())
        {
            if (!IsDrawn(documentType, isVisible))
            {
                continue;
            }

            visible.Add(documentType);
            byTable[Key(documentType.TableName.Schema, documentType.TableName.Name)] = documentType;
            byClrType[documentType.DocumentType] = documentType;

            // A subclass is not an IDocumentType of its own and has no table of its own: a key declared against one
            // belongs to the root's table, so the root is what the CLR type resolves to.
            foreach (SubClassMapping subclass in documentType.SubClasses)
            {
                byClrType.TryAdd(subclass.DocumentType, documentType);
            }
        }

        List<RelationshipDeclaredKey> declared = [];

        foreach (IDocumentType source in visible)
        {
            foreach (ForeignKey key in source.ForeignKeys)
            {
                string[] columns = key.ColumnNames ?? [];

                if (columns.Length == 0)
                {
                    continue;
                }

                IDocumentType? target = ResolveDeclaredTarget(key, byClrType, byTable);

                declared.Add(new RelationshipDeclaredKey(
                    source.Alias,
                    key.Name,
                    columns,
                    key.LinkedNames ?? [],
                    key.OnDelete.ToString())
                {
                    ToAlias = target?.Alias,
                    LinkedSchema = key.LinkedTable?.Schema,
                    LinkedTable = key.LinkedTable?.Name,
                    LinkedQualifiedName = key.LinkedTable?.QualifiedName,
                });
            }
        }

        return declared;
    }

    /// <summary>
    /// The graph one visitor sees, from the store's configuration - what the service builds, in one call, for a test
    /// with no database.
    /// </summary>
    /// <param name="storeOptions">The scoped store's read-only options.</param>
    /// <param name="isVisible"><c>MartenStudioOptions.IsDocumentTypeVisible</c>, or <see langword="null" />.</param>
    /// <param name="physicalKeys">The foreign keys Postgres has around the store's schemas.</param>
    /// <param name="estimates"><c>reltuples</c> per document table; empty is valid.</param>
    /// <param name="readAt">When the reads happened.</param>
    /// <param name="database">The visitor's gate and the relations it admits, or <see langword="null" /> for documents only.</param>
    public static RelationshipGraph Build(
        IReadOnlyStoreOptions storeOptions,
        Func<Type, bool>? isVisible,
        IReadOnlyList<PhysicalForeignKey> physicalKeys,
        IReadOnlyDictionary<string, long> estimates,
        DateTimeOffset readAt,
        RelationshipDatabaseView? database = null) =>
        RelationshipGraphBuilder.Build(
            Nodes(storeOptions, isVisible, estimates),
            DeclaredKeys(storeOptions, isVisible),
            physicalKeys,
            readAt,
            database);

    /// <summary>
    /// The .NET member behind each column Marten duplicated one into - the first one per column, ignoring case.
    /// </summary>
    /// <remarks>
    /// This is not decoration: it is what the "referenced by" link filters on. The documents list's search grammar
    /// takes a <em>member path</em> and resolves it to the duplicated column itself, so <c>CustomerId = "…"</c> is an
    /// indexed read and <c>customer_id = "…"</c> would be a JSON-path comparison against a property that does not
    /// exist. Physical fields only: a search-only duplicated field names a metadata column rather than one of its own,
    /// so matching against it would answer "Version" for <c>mt_version</c> and send the referenced-by link filtering on
    /// a member that is not a duplicated column at all. See <see cref="MartenDuplicatedFields" />.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> MembersOf(IDocumentType documentType)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        Dictionary<string, string> members = new(StringComparer.OrdinalIgnoreCase);

        foreach (Marten.Linq.Members.DuplicatedField field in MartenDuplicatedFields.Physical(documentType))
        {
            members.TryAdd(field.ColumnName, field.MemberName);
        }

        return members;
    }

    /// <summary>Whether a document type is drawn: not Marten's own infrastructure, and not hidden by the host.</summary>
    private static bool IsDrawn(IDocumentType documentType, Func<Type, bool>? isVisible) =>
        !CollectionAliases.IsMartenInfrastructure(documentType.DocumentType)
        && (isVisible is null || isVisible(documentType.DocumentType));

    /// <summary>
    /// The document type a declared key points at, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// <c>DocumentForeignKey.ReferenceDocumentType</c> first, because it is the fact Marten actually has:
    /// <c>LinkedTable</c> is nullable on the Weasel base class and a key added through Weasel directly may carry no
    /// table at all. The table is the fallback, which is what resolves a key somebody built without Marten's helper.
    /// </remarks>
    private static IDocumentType? ResolveDeclaredTarget(
        ForeignKey key,
        Dictionary<Type, IDocumentType> byClrType,
        Dictionary<string, IDocumentType> byTable)
    {
        if (key is DocumentForeignKey documentKey &&
            byClrType.TryGetValue(documentKey.ReferenceDocumentType, out IDocumentType? byType))
        {
            return byType;
        }

        return key.LinkedTable is { } linked && byTable.TryGetValue(Key(linked.Schema, linked.Name), out IDocumentType? byName)
            ? byName
            : null;
    }

    private static RelationshipNode Describe(IDocumentType documentType, IReadOnlyDictionary<string, long> estimates)
    {
        List<string> subclasses = [];
        foreach (SubClassMapping subclass in documentType.SubClasses)
        {
            subclasses.Add(subclass.Alias);
        }

        subclasses.Sort(StringComparer.Ordinal);

        return new RelationshipNode(
            documentType.Alias,
            documentType.DocumentType.Name,
            CollectionColorizer.HueFor(documentType.Alias),
            DocumentCountOf(documentType, estimates),
            documentType.IsHierarchy(),
            subclasses)
        {
            Schema = documentType.TableName.Schema,
            Table = documentType.TableName.Name,
        };
    }

    /// <summary>
    /// A document node's row count, from the grouped <c>reltuples</c> read.
    /// </summary>
    /// <remarks>
    /// <c>-1</c> is Postgres for "never analysed" rather than for "empty", so it becomes
    /// <see cref="DocumentCount.Unknown"/> and the node draws no number at all - the alternative is a diagram that says
    /// <c>~0</c> beside a collection that plainly has rows in it.
    /// </remarks>
    private static DocumentCount DocumentCountOf(IDocumentType documentType, IReadOnlyDictionary<string, long> estimates)
    {
        if (!estimates.TryGetValue(
                DocumentBrowseQueries.Key(documentType.TableName.Schema, documentType.TableName.Name),
                out var rows))
        {
            return DocumentCount.Unavailable;
        }

        return rows < 0 ? DocumentCount.Unknown : DocumentCount.Estimate(rows);
    }

    private static string Key(string schema, string table) => schema + "." + table;
}
