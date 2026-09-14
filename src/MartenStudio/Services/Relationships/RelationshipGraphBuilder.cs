using Marten;
using Marten.Linq.Members;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;

using Weasel.Postgresql.Tables;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// Turns what <c>StoreOptions</c> declares and what <c>pg_constraint</c> holds into one graph.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and deliberately separate from <see cref="RelationshipDataService"/>: everything interesting
/// here is a matching rule — which type a key points at, whether the database agrees, which end is hidden
/// — and every one of those rules is a decision that wants a test with no database in it. The service is
/// then two reads and a call.
/// </para>
/// <para>
/// <b>Hidden types are dropped, unknown ones are listed.</b> A key with an end belonging to a mapping the
/// host hid with <c>IsDocumentTypeVisible</c> — or to Marten's own bookkeeping — disappears completely:
/// not as an edge, not as a node, and not as an entry in <see cref="RelationshipGraph.Unmatched"/>,
/// because naming the hidden collection in a list of "keys that could not be drawn" would be the
/// visibility gate leaking through the very relationship it was set to hide. A key pointing at a table no
/// mapping claims at all is a different thing and is listed.
/// </para>
/// </remarks>
internal static class RelationshipGraphBuilder
{
    /// <summary>Marten's tenant column, which a conjoined-to-conjoined key carries as its second half.</summary>
    private const string TenantColumn = "tenant_id";

    /// <summary>
    /// What separates the three parts of an <see cref="EdgeKey" />.
    /// </summary>
    /// <remarks>
    /// A character no identifier can contain, because <c>SqlIdentifier.Quote</c> refuses a NUL outright —
    /// so two different (table, columns, table) triples can never spell the same key by putting the
    /// separator inside one of their own parts.
    /// </remarks>
    private const char KeySeparator = (char) 0;

    /// <summary>
    /// Builds the graph.
    /// </summary>
    /// <param name="storeOptions">The scoped store's read-only options.</param>
    /// <param name="isVisible">
    /// <c>MartenStudioOptions.IsDocumentTypeVisible</c>, or <see langword="null" /> when the host set none.
    /// </param>
    /// <param name="physicalKeys">Every foreign key Postgres has in the store's schemas.</param>
    /// <param name="estimates">
    /// <c>reltuples</c> per table, keyed as <c>DocumentBrowseQueries.Key</c> spells it. Empty is a valid
    /// argument — the referenced-by read builds the same graph and has no use for the counts.
    /// </param>
    /// <param name="readAt">When the reads happened.</param>
    public static RelationshipGraph Build(
        IReadOnlyStoreOptions storeOptions,
        Func<Type, bool>? isVisible,
        IReadOnlyList<PhysicalForeignKey> physicalKeys,
        IReadOnlyDictionary<string, long> estimates,
        DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(storeOptions);
        ArgumentNullException.ThrowIfNull(physicalKeys);
        ArgumentNullException.ThrowIfNull(estimates);

        List<IDocumentType> visible = [];
        Dictionary<string, IDocumentType> byTable = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<Type, IDocumentType> byClrType = [];
        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

        foreach (IDocumentType documentType in storeOptions.AllKnownDocumentTypes())
        {
            // Every table a mapping claims, hidden ones included. This is what tells "a key into a table
            // the host hid" apart from "a key into a table nothing in this store knows about" - the first
            // is dropped and the second is reported.
            claimed.Add(Key(documentType.TableName.Schema, documentType.TableName.Name));

            if (CollectionAliases.IsMartenInfrastructure(documentType.DocumentType) ||
                (isVisible is not null && !isVisible(documentType.DocumentType)))
            {
                continue;
            }

            visible.Add(documentType);
            byTable[Key(documentType.TableName.Schema, documentType.TableName.Name)] = documentType;
            byClrType[documentType.DocumentType] = documentType;

            // A subclass is not an IDocumentType of its own and has no table of its own: a key declared
            // against one belongs to the root's table, so the root is what the CLR type resolves to.
            foreach (SubClassMapping subclass in documentType.SubClasses)
            {
                byClrType.TryAdd(subclass.DocumentType, documentType);
            }
        }

        List<RelationshipNode> nodes = [];
        foreach (IDocumentType documentType in visible)
        {
            nodes.Add(Describe(documentType, estimates));
        }

        nodes.Sort(static (left, right) => string.CompareOrdinal(left.Alias, right.Alias));

        Dictionary<string, RelationshipEdge> edges = new(StringComparer.Ordinal);
        List<UnmatchedForeignKey> unmatched = [];

        AddDeclared(visible, byTable, byClrType, claimed, edges, unmatched);
        AddPhysical(physicalKeys, byTable, claimed, edges, unmatched);

        List<RelationshipEdge> ordered = [.. edges.Values];
        ordered.Sort(Compare);

        unmatched.Sort(static (left, right) =>
        {
            var byFrom = string.CompareOrdinal(left.From, right.From);
            return byFrom != 0 ? byFrom : string.CompareOrdinal(left.Name, right.Name);
        });

        return new RelationshipGraph(nodes, ordered, unmatched, readAt);
    }

    /// <summary>The key two halves of the same relationship agree on: source table, columns, target table.</summary>
    /// <remarks>
    /// The columns are sorted rather than taken in order, because <c>conkey</c>'s order is the order the
    /// constraint was created in and Marten's declared order is the order <c>DocumentForeignKey</c> writes
    /// them - the same two columns either way round are the same key, and matching them positionally
    /// would report a conjoined key as both "declared only" and "physical only" at once.
    /// </remarks>
    internal static string EdgeKey(
        string fromSchema,
        string fromTable,
        IReadOnlyList<string> columns,
        string toSchema,
        string toTable)
    {
        ArgumentNullException.ThrowIfNull(columns);

        List<string> sorted = [.. columns];
        sorted.Sort(StringComparer.OrdinalIgnoreCase);

        return string.Join(
            KeySeparator,
            Key(fromSchema, fromTable).ToLowerInvariant(),
            string.Join(',', sorted).ToLowerInvariant(),
            Key(toSchema, toTable).ToLowerInvariant());
    }

    private static void AddDeclared(
        List<IDocumentType> visible,
        Dictionary<string, IDocumentType> byTable,
        Dictionary<Type, IDocumentType> byClrType,
        HashSet<string> claimed,
        Dictionary<string, RelationshipEdge> edges,
        List<UnmatchedForeignKey> unmatched)
    {
        foreach (IDocumentType source in visible)
        {
            foreach (ForeignKey key in source.ForeignKeys)
            {
                var columns = key.ColumnNames ?? [];

                if (columns.Length == 0)
                {
                    continue;
                }

                IDocumentType? target = ResolveDeclaredTarget(key, byTable, byClrType);

                if (target is null)
                {
                    // An end that belongs to a mapping the visitor may not see is dropped rather than
                    // listed: an "unmatched" row naming the hidden table would say the thing the gate is
                    // there to stop the studio from saying.
                    if (key.LinkedTable is { } linked && claimed.Contains(Key(linked.Schema, linked.Name)))
                    {
                        continue;
                    }

                    unmatched.Add(new UnmatchedForeignKey(
                        key.Name,
                        source.Alias,
                        string.Join(", ", columns),
                        key.LinkedTable?.QualifiedName ?? "unknown",
                        Declared: true,
                        Physical: false,
                        key.LinkedTable is null
                            ? "The configuration declares this key without naming a table to link to."
                            : "It points at a table no document type of this store maps."));

                    continue;
                }

                var column = PrimaryColumn(columns);

                var edge = new RelationshipEdge(
                    source.Alias,
                    target.Alias,
                    column,
                    MemberFor(source, column),
                    Declared: true,
                    Physical: false,
                    key.OnDelete.ToString(),
                    key.Name);

                edges[EdgeKey(
                    source.TableName.Schema,
                    source.TableName.Name,
                    columns,
                    target.TableName.Schema,
                    target.TableName.Name)] = edge;
            }
        }
    }

    private static void AddPhysical(
        IReadOnlyList<PhysicalForeignKey> physicalKeys,
        Dictionary<string, IDocumentType> byTable,
        HashSet<string> claimed,
        Dictionary<string, RelationshipEdge> edges,
        List<UnmatchedForeignKey> unmatched)
    {
        foreach (PhysicalForeignKey key in physicalKeys)
        {
            // Marten's own bookkeeping, which is not a relationship between this application's document
            // types. mt_events really does carry a foreign key to mt_streams (EventsTable, Marten 9.35),
            // and the event schema is one of the store's own, so without this every store with an event
            // store would open this screen on a list of constraints it did not write and cannot act on.
            if (IsMartenBookkeeping(key.Table) || IsMartenBookkeeping(key.LinkedTable))
            {
                continue;
            }

            var fromKey = Key(key.Schema, key.Table);
            var toKey = Key(key.LinkedSchema, key.LinkedTable);

            var fromHidden = !byTable.ContainsKey(fromKey) && claimed.Contains(fromKey);
            var toHidden = !byTable.ContainsKey(toKey) && claimed.Contains(toKey);

            if (fromHidden || toHidden)
            {
                continue;
            }

            byTable.TryGetValue(fromKey, out IDocumentType? source);
            byTable.TryGetValue(toKey, out IDocumentType? target);

            if (source is null || target is null)
            {
                unmatched.Add(new UnmatchedForeignKey(
                    key.Name,
                    source?.Alias ?? fromKey,
                    string.Join(", ", key.Columns),
                    target?.Alias ?? toKey,
                    Declared: false,
                    Physical: true,
                    source is null
                        ? "It is on a table no document type of this store maps."
                        : "It points at a table no document type of this store maps."));

                continue;
            }

            var edgeKey = EdgeKey(key.Schema, key.Table, key.Columns, key.LinkedSchema, key.LinkedTable);
            var column = PrimaryColumn(key.Columns);

            edges[edgeKey] = edges.TryGetValue(edgeKey, out RelationshipEdge? declared)
                ? declared with { Physical = true }
                : new RelationshipEdge(
                    source.Alias,
                    target.Alias,
                    column,
                    MemberFor(source, column),
                    Declared: false,
                    Physical: true,
                    key.OnDelete,
                    key.Name);
        }
    }

    /// <summary>
    /// The document type a declared key points at, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// <c>DocumentForeignKey.ReferenceDocumentType</c> first, because it is the fact Marten actually has:
    /// <c>LinkedTable</c> is nullable on the Weasel base class and a key added through Weasel directly may
    /// carry no table at all. The table is the fallback, which is what resolves a key somebody built
    /// without Marten's helper.
    /// </remarks>
    private static IDocumentType? ResolveDeclaredTarget(
        ForeignKey key,
        Dictionary<string, IDocumentType> byTable,
        Dictionary<Type, IDocumentType> byClrType)
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

    /// <summary>
    /// Whether a table is Marten's own bookkeeping rather than one of this application's collections.
    /// </summary>
    /// <remarks>
    /// The <c>mt_</c> prefix minus the <c>mt_doc_</c> one: <c>mt_events</c>, <c>mt_streams</c>,
    /// <c>mt_event_progression</c>, <c>mt_hilo</c> and the rest. A document table that no mapping claims
    /// is a different thing entirely and stays reportable, because a discovered collection with a foreign
    /// key on it is exactly the kind of thing somebody wants to be told about.
    /// </remarks>
    /// <param name="table">The unqualified table name.</param>
    internal static bool IsMartenBookkeeping(string table) =>
        table.StartsWith("mt_", StringComparison.OrdinalIgnoreCase) &&
        !table.StartsWith(DocumentBrowseQueries.TablePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The column the relationship is really about.
    /// </summary>
    /// <remarks>
    /// A conjoined-to-conjoined key is <c>(column, tenant_id)</c>: the tenant half is the scope, not the
    /// relationship, and drawing an edge labelled <c>tenant_id</c> would describe every such key
    /// identically.
    /// </remarks>
    internal static string PrimaryColumn(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        foreach (var column in columns)
        {
            if (!string.Equals(column, TenantColumn, StringComparison.OrdinalIgnoreCase))
            {
                return column;
            }
        }

        return columns.Count > 0 ? columns[0] : string.Empty;
    }

    /// <summary>
    /// The .NET member behind a column, when Marten duplicated one into it.
    /// </summary>
    /// <remarks>
    /// This is not decoration: it is what the "referenced by" link filters on. The documents list's search
    /// grammar takes a <em>member path</em> and resolves it to the duplicated column itself, so
    /// <c>CustomerId = "…"</c> is an indexed read and <c>customer_id = "…"</c> would be a JSON-path
    /// comparison against a property that does not exist.
    /// </remarks>
    internal static string? MemberFor(IDocumentType documentType, string column)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        foreach (DuplicatedField field in documentType.DuplicatedFields)
        {
            if (string.Equals(field.ColumnName, column, StringComparison.OrdinalIgnoreCase))
            {
                return field.MemberName;
            }
        }

        return null;
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
            Count(documentType, estimates),
            documentType.IsHierarchy(),
            subclasses);
    }

    /// <summary>
    /// The node's row count, from the grouped <c>reltuples</c> read.
    /// </summary>
    /// <remarks>
    /// <c>-1</c> is Postgres for "never analysed" rather than for "empty", so it becomes
    /// <see cref="DocumentCount.Unknown"/> and the node draws no number at all - the alternative is a
    /// diagram that says <c>~0</c> beside a collection that plainly has rows in it.
    /// </remarks>
    private static DocumentCount Count(IDocumentType documentType, IReadOnlyDictionary<string, long> estimates)
    {
        if (!estimates.TryGetValue(
                DocumentBrowseQueries.Key(documentType.TableName.Schema, documentType.TableName.Name),
                out var rows))
        {
            return DocumentCount.Unavailable;
        }

        return rows < 0 ? DocumentCount.Unknown : DocumentCount.Estimate(rows);
    }

    private static int Compare(RelationshipEdge left, RelationshipEdge right)
    {
        var byFrom = string.CompareOrdinal(left.FromAlias, right.FromAlias);
        if (byFrom != 0)
        {
            return byFrom;
        }

        var byTo = string.CompareOrdinal(left.ToAlias, right.ToAlias);

        return byTo != 0 ? byTo : string.CompareOrdinal(left.Column, right.Column);
    }

    private static string Key(string schema, string table) => schema + "." + table;
}
