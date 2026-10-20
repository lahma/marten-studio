using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// What one visitor may see of the database around the source: the database browser's gate, and the
/// relations in the schemas it admits.
/// </summary>
/// <param name="Gate">
/// The visitor's gate (D27): which schemas' structure they may see, and whose each object is. Built per
/// visitor, never cached across visitors.
/// </param>
/// <param name="Relations">
/// The relations in <see cref="DatabaseGate.VisibleSchemas" />, from the catalog - what gives a table node
/// its row estimate, and a table with no key at all its place in the list of isolated tables. May be
/// empty: a table at either end of a key is a node whether or not it is in here.
/// </param>
/// <param name="Truncated">Whether the catalog's cap stopped the relation list.</param>
internal sealed record RelationshipDatabaseView(
    DatabaseGate Gate,
    IReadOnlyList<CatalogRelation> Relations,
    bool Truncated = false);

/// <summary>
/// Turns what a source declares - its nodes and its configured keys (<see cref="RelationshipNodeSet" />,
/// <see cref="RelationshipDeclaredKey" />) - and what <c>pg_constraint</c> holds into one graph, for one visitor.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and deliberately separate from <see cref="RelationshipDataService"/>: everything interesting
/// here is a matching rule — which type a key points at, whether the database agrees, which end is hidden,
/// which end is in a schema this visitor is not shown — and every one of those rules is a decision that
/// wants a test with no database in it. The service is then two reads and a call.
/// </para>
/// <para>
/// <b>It is also the per-visitor filter.</b> The foreign-key read it is handed is a fact about the
/// database, cached per database and shared by every visitor (plan §5); the
/// <see cref="RelationshipDatabaseView" /> is this visitor's gate. Nothing that one visitor may not see
/// survives this function into the graph handed to them, so the cache in front of it never holds anything
/// visitor-shaped.
/// </para>
/// <para>
/// <b>Hidden types are dropped, unknown ones are listed, withheld ones are counted.</b> A key with an end
/// belonging to a table the source hides (<see cref="RelationshipNodeSet.HiddenTables" />: in MartenStudio a mapping
/// the host hid with <c>IsDocumentTypeVisible</c>) — or to Marten's own bookkeeping, by its <c>mt_</c> prefix, which
/// fails closed in every studio — disappears completely: not as an edge, not as a node, and not as an entry in
/// <see cref="RelationshipGraph.Unmatched"/>, because naming the hidden collection in a list of "keys that
/// could not be drawn" would be the visibility gate leaking through the very relationship it was set to
/// hide. A key with an end in a schema the visitor is not shown is a <see cref="WithheldForeignKey" />:
/// counted beside its visible end, and neither the far table nor its schema named. A key pointing at a
/// table in a visible schema that nothing can place is listed.
/// </para>
/// <para>
/// <b>Tables are drawn only with a gate.</b> Without a <see cref="RelationshipDatabaseView" /> - the gate
/// could not be read, which is the classifier failing closed (D27) - nothing is called somebody else's
/// table, the picture is the source's own nodes alone as it always was, and only the source's own schemas
/// are treated as visible.
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

    /// <summary>The prefix of a Marten document table, which the <c>mt_</c> bookkeeping rule leaves reportable.</summary>
    private const string DocumentTablePrefix = "mt_doc_";

    /// <summary>Builds the graph one visitor sees.</summary>
    /// <param name="nodes">The source's nodes, the tables it hides and its own schemas.</param>
    /// <param name="declared">The keys the source's configuration declares, in its own order.</param>
    /// <param name="physicalKeys">
    /// Every foreign key Postgres has with either end in the schemas any visitor might see - the shared,
    /// cached read. Filtered here.
    /// </param>
    /// <param name="readAt">When the reads happened.</param>
    /// <param name="database">
    /// The visitor's gate and the relations it admits, or <see langword="null" /> for the source's own nodes only.
    /// </param>
    public static RelationshipGraph Build(
        RelationshipNodeSet nodes,
        IReadOnlyList<RelationshipDeclaredKey> declared,
        IReadOnlyList<PhysicalForeignKey> physicalKeys,
        DateTimeOffset readAt,
        RelationshipDatabaseView? database = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(physicalKeys);

        var builder = new Builder(nodes, database);

        builder.RegisterTables(physicalKeys);
        builder.AddDeclared(declared);
        builder.AddPhysical(physicalKeys);

        return builder.Finish(readAt);
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
        !table.StartsWith(DocumentTablePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The column the relationship is really about, for a key between two document types.
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
    /// The label of a key with a table at either end: its columns, less the leading ones both ends'
    /// primary keys share.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Composite keys in a real relational schema usually lead with a partition of the whole database -
    /// Quartz.NET's <c>sched_name</c>, a multi-tenant schema's <c>tenant_id</c> - and every key between two
    /// such tables repeats it. So the two primary keys are compared position by position from the start,
    /// and while both ends of the foreign key sit on that shared prefix at the same position, the column
    /// is left off the label: <c>qrtz_triggers → qrtz_job_details</c> reads <c>job_name, job_group</c>,
    /// which is what tells it from any other key in the schema.
    /// </para>
    /// <para>
    /// <b>A key that is both whole primary keys reads as what it is.</b> A detail table keyed by exactly its
    /// parent's key (<c>qrtz_simple_triggers</c> by <c>qrtz_triggers</c>' three columns) is a one-to-one
    /// extension of it: every one of its columns is in the key, so any label made of them says nothing the
    /// arrow does not, and the three of them were cut to <c>sched_name, trigger_name, trigg…</c> on every
    /// such arrow. It is labelled <see cref="OneToOneLabel" /> - when the pointing columns are the whole of
    /// the pointing table's primary key and the referenced ones the whole of the referenced table's, in any
    /// order. The tooltip and the table always list every column either way.
    /// </para>
    /// <para>
    /// <b>Stripping everything else shows everything.</b> A key that shares the whole prefix without being
    /// both primary keys - the child's key is longer than the key it points with - would strip to nothing, and
    /// an empty label would be worse than a long one.
    /// </para>
    /// </remarks>
    /// <param name="columns">The pointing columns, in constraint order.</param>
    /// <param name="linkedColumns">The referenced columns, in constraint order.</param>
    /// <param name="primaryKey">The pointing table's primary key, or <see langword="null" />.</param>
    /// <param name="linkedPrimaryKey">The referenced table's primary key, or <see langword="null" />.</param>
    internal static string LabelFor(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> linkedColumns,
        IReadOnlyList<string>? primaryKey,
        IReadOnlyList<string>? linkedPrimaryKey)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(linkedColumns);

        if (IsWholeKey(columns, primaryKey) && IsWholeKey(linkedColumns, linkedPrimaryKey))
        {
            return OneToOneLabel;
        }

        int shared = 0;

        if (primaryKey is not null && linkedPrimaryKey is not null)
        {
            while (shared < primaryKey.Count
                   && shared < linkedPrimaryKey.Count
                   && string.Equals(primaryKey[shared], linkedPrimaryKey[shared], StringComparison.Ordinal))
            {
                shared++;
            }
        }

        int strip = 0;

        while (strip < shared
               && strip < columns.Count
               && strip < linkedColumns.Count
               && string.Equals(columns[strip], primaryKey![strip], StringComparison.Ordinal)
               && string.Equals(linkedColumns[strip], linkedPrimaryKey![strip], StringComparison.Ordinal))
        {
            strip++;
        }

        return strip == 0 || strip >= columns.Count
            ? string.Join(", ", columns)
            : string.Join(", ", columns.Skip(strip));
    }

    /// <summary>The label of a key whose columns are both tables' whole primary keys: a one-to-one extension.</summary>
    internal const string OneToOneLabel = "primary key (1:1)";

    /// <summary>
    /// Whether <paramref name="columns" /> are exactly <paramref name="primaryKey" />'s columns - each once,
    /// in any order. Ordinal, like every identifier comparison here: two quoted names that differ by case are
    /// two columns.
    /// </summary>
    private static bool IsWholeKey(IReadOnlyList<string> columns, IReadOnlyList<string>? primaryKey)
    {
        if (primaryKey is not { Count: > 0 } || columns.Count != primaryKey.Count)
        {
            return false;
        }

        HashSet<string> key = new(primaryKey, StringComparer.Ordinal);

        return key.Count == columns.Count && columns.All(key.Contains) && columns.Distinct(StringComparer.Ordinal).Count() == columns.Count;
    }

    private static string Key(string schema, string table) => schema + "." + table;

    /// <summary>What a foreign-key end turned out to be.</summary>
    private enum EndKind
    {
        /// <summary>One of the source's own nodes - in MartenStudio, a visible document type.</summary>
        Document,

        /// <summary>A table the graph draws: somebody else's, or Marten-managed relational data.</summary>
        Table,

        /// <summary>A table the source hides. Everything about the key disappears.</summary>
        Hidden,

        /// <summary>Marten's own bookkeeping. Everything about the key disappears.</summary>
        Bookkeeping,

        /// <summary>A table nothing here can place: an unmapped document table, another store's, or no gate to ask.</summary>
        Unplaced,
    }

    /// <summary>One end of a key, resolved.</summary>
    private readonly record struct End(EndKind Kind, string Schema, string Name, RelationshipNode? Document, RelationshipTableNode? Table)
    {
        /// <summary>
        /// The node key, when the end is a node - never for a table in a schema the visitor is not shown,
        /// which is classified but not registered.
        /// </summary>
        public string? NodeKey => Kind switch
        {
            EndKind.Document => Document?.Alias,
            EndKind.Table => Table?.Key,
            _ => null,
        };

        /// <summary>What an unmatched row calls this end.</summary>
        public string Display => Kind switch
        {
            EndKind.Document => Document!.Alias,
            _ => Key(Schema, Name),
        };
    }

    /// <summary>The state of one build.</summary>
    private sealed class Builder
    {
        private readonly RelationshipNodeSet nodes;
        private readonly RelationshipDatabaseView? database;
        private readonly Func<string, bool> canSee;
        private readonly IReadOnlyList<string> visibleSchemas;

        private readonly Dictionary<string, RelationshipNode> byTable = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RelationshipNode> byAlias = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, RelationshipTableNode> tables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CatalogRelation> relations = new(StringComparer.Ordinal);

        private readonly Dictionary<string, RelationshipEdge> edges = new(StringComparer.Ordinal);
        private readonly List<UnmatchedForeignKey> unmatched = [];
        private readonly List<WithheldForeignKey> withheld = [];

        public Builder(RelationshipNodeSet nodes, RelationshipDatabaseView? database)
        {
            this.nodes = nodes;
            this.database = database;

            if (database is null)
            {
                HashSet<string> set = new(nodes.OwnSchemas, StringComparer.Ordinal);

                canSee = set.Contains;
                visibleSchemas = nodes.OwnSchemas;
            }
            else
            {
                canSee = database.Gate.CanSeeStructure;
                visibleSchemas = database.Gate.VisibleSchemas;
            }

            // Every table the source claims, hidden ones included. This is what tells "a key into a table the host
            // hid" apart from "a key into a table nothing in this source knows about" - the first is dropped and the
            // second is reported.
            foreach ((string schema, string table) in nodes.HiddenTables)
            {
                claimed.Add(Key(schema, table));
            }

            foreach (RelationshipNode node in nodes.Nodes)
            {
                string table = Key(node.Schema ?? string.Empty, node.Table ?? string.Empty);

                claimed.Add(table);
                byTable[table] = node;
                byAlias[node.Alias] = node;
            }
        }

        /// <summary>
        /// Every table the graph may draw, before a single edge is made: the gate's relations, then every
        /// end of every key that turns out to be a table in a visible schema.
        /// </summary>
        public void RegisterTables(IReadOnlyList<PhysicalForeignKey> physicalKeys)
        {
            if (database is null)
            {
                return;
            }

            foreach (CatalogRelation relation in database.Relations)
            {
                if (relation.Kind is not ("r" or "p") || !canSee(relation.Schema))
                {
                    continue;
                }

                relations[RelationshipTableNode.KeyFor(relation.Schema, relation.Name)] = relation;
                Resolve(relation.Schema, relation.Name);
            }

            foreach (PhysicalForeignKey key in physicalKeys)
            {
                if (canSee(key.Schema))
                {
                    Resolve(key.Schema, key.Table);
                }

                if (canSee(key.LinkedSchema))
                {
                    Resolve(key.LinkedSchema, key.LinkedTable);
                }
            }
        }

        public void AddDeclared(IReadOnlyList<RelationshipDeclaredKey> declared)
        {
            foreach (RelationshipDeclaredKey key in declared)
            {
                IReadOnlyList<string> columns = key.Columns;

                if (columns.Count == 0 || !byAlias.TryGetValue(key.FromAlias, out RelationshipNode? source))
                {
                    continue;
                }

                if (key.ToAlias is { } toAlias && byAlias.TryGetValue(toAlias, out RelationshipNode? target))
                {
                    var column = PrimaryColumn(columns);

                    edges[EdgeKey(SchemaOf(source), TableOf(source), columns, SchemaOf(target), TableOf(target))] =
                        new RelationshipEdge(
                            source.Alias,
                            target.Alias,
                            column,
                            nodes.MemberFor(source.Alias, column),
                            Declared: true,
                            Physical: false,
                            key.OnDelete,
                            key.Name)
                        {
                            Columns = string.Join(", ", columns),
                            LinkedColumns = string.Join(", ", key.LinkedColumns),
                        };

                    continue;
                }

                if (key.LinkedSchema is not { } linkedSchema || key.LinkedTable is not { } linkedTable)
                {
                    unmatched.Add(new UnmatchedForeignKey(
                        key.Name,
                        source.Alias,
                        string.Join(", ", columns),
                        "unknown",
                        Declared: true,
                        Physical: false,
                        "The configuration declares this key without naming a table to link to.")
                    {
                        FromSchema = source.Schema,
                        FromTable = source.Table,
                    });

                    continue;
                }

                // An end that belongs to a mapping the visitor may not see is dropped rather than
                // listed: an "unmatched" row naming the hidden table would say the thing the gate is
                // there to stop the studio from saying.
                if (claimed.Contains(Key(linkedSchema, linkedTable)))
                {
                    continue;
                }

                if (!canSee(linkedSchema))
                {
                    withheld.Add(new WithheldForeignKey(source.Alias, VisibleEndIsTarget: false));
                    continue;
                }

                // Classified without being registered: a key only the configuration declares says nothing
                // about whether its table exists, and a node for a table that is not there would be a
                // box on the picture for nothing.
                End end = Resolve(linkedSchema, linkedTable, register: false);

                if (end.Kind is EndKind.Hidden or EndKind.Bookkeeping)
                {
                    continue;
                }

                if (end.Kind == EndKind.Table && FindExisting(linkedSchema, linkedTable) is { } table)
                {
                    edges[EdgeKey(SchemaOf(source), TableOf(source), columns, table.Schema, table.Name)] =
                        new RelationshipEdge(
                            source.Alias,
                            table.Key,
                            LabelFor(columns, key.LinkedColumns, null, null),
                            nodes.MemberFor(source.Alias, PrimaryColumn(columns)),
                            Declared: true,
                            Physical: false,
                            key.OnDelete,
                            key.Name)
                        {
                            Columns = string.Join(", ", columns),
                            LinkedColumns = string.Join(", ", key.LinkedColumns),
                            ToIsTable = true,
                        };

                    continue;
                }

                unmatched.Add(new UnmatchedForeignKey(
                    key.Name,
                    source.Alias,
                    string.Join(", ", columns),
                    key.LinkedQualifiedName ?? Key(linkedSchema, linkedTable),
                    Declared: true,
                    Physical: false,
                    end.Kind == EndKind.Table
                        ? "It points at a table the studio does not find in the database."
                        : "It points at a table no document type of this store maps.")
                {
                    FromSchema = source.Schema,
                    FromTable = source.Table,
                    ToSchema = linkedSchema,
                    ToTable = linkedTable,
                });
            }
        }

        public void AddPhysical(IReadOnlyList<PhysicalForeignKey> physicalKeys)
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

                End from = Resolve(key.Schema, key.Table);
                End to = Resolve(key.LinkedSchema, key.LinkedTable);

                // Hidden is hidden in every schema, the visitor's or not: a count of "keys into schemas you
                // are not shown" that included one into a hidden type would say the hidden type is there.
                if (from.Kind is EndKind.Hidden or EndKind.Bookkeeping || to.Kind is EndKind.Hidden or EndKind.Bookkeeping)
                {
                    continue;
                }

                bool fromSeen = canSee(key.Schema);
                bool toSeen = canSee(key.LinkedSchema);

                if (!fromSeen && !toSeen)
                {
                    // Nothing to do with anything this visitor is shown - the shared read spans every schema
                    // any visitor might see, and this key is between two this one may not.
                    continue;
                }

                if (!fromSeen || !toSeen)
                {
                    withheld.Add(toSeen
                        ? new WithheldForeignKey(to.NodeKey, VisibleEndIsTarget: true)
                        : new WithheldForeignKey(from.NodeKey, VisibleEndIsTarget: false));

                    continue;
                }

                if (from.Kind == EndKind.Unplaced || to.Kind == EndKind.Unplaced)
                {
                    unmatched.Add(new UnmatchedForeignKey(
                        key.Name,
                        from.Display,
                        string.Join(", ", key.Columns),
                        to.Display,
                        Declared: false,
                        Physical: true,
                        from.Kind == EndKind.Unplaced
                            ? "It is on a table no document type of this store maps."
                            : "It points at a table no document type of this store maps.")
                    {
                        FromSchema = key.Schema,
                        FromTable = key.Table,
                        ToSchema = key.LinkedSchema,
                        ToTable = key.LinkedTable,
                    });

                    continue;
                }

                if (from.Kind == EndKind.Document)
                {
                    AddPhysicalOnDocument(key, from, to);
                }
                else
                {
                    AddPhysicalOnTable(key, from, to);
                }
            }
        }

        public RelationshipGraph Finish(DateTimeOffset readAt)
        {
            List<RelationshipNode> drawn = [.. nodes.Nodes];
            drawn.Sort(static (left, right) => string.CompareOrdinal(left.Alias, right.Alias));

            List<RelationshipTableNode> tableNodes = [.. tables.Values];
            tableNodes.Sort(static (left, right) =>
            {
                int bySchema = string.CompareOrdinal(left.Schema, right.Schema);
                return bySchema != 0 ? bySchema : string.CompareOrdinal(left.Name, right.Name);
            });

            List<RelationshipEdge> ordered = [.. edges.Values];
            ordered.Sort(Compare);

            unmatched.Sort(static (left, right) =>
            {
                var byFrom = string.CompareOrdinal(left.From, right.From);
                return byFrom != 0 ? byFrom : string.CompareOrdinal(left.Name, right.Name);
            });

            return new RelationshipGraph(drawn, ordered, unmatched, readAt)
            {
                Tables = tableNodes,
                Withheld = withheld,
                VisibleSchemas = visibleSchemas,
                Truncated = database?.Truncated ?? false,
            };
        }

        private void AddPhysicalOnDocument(PhysicalForeignKey key, End from, End to)
        {
            RelationshipNode source = from.Document!;
            string toKey = to.NodeKey!;
            bool toIsTable = to.Kind == EndKind.Table;

            // The key its declared half was filed under, if the configuration declares one.
            var declaredKey = EdgeKey(key.Schema, key.Table, key.Columns, key.LinkedSchema, key.LinkedTable);
            var edgeKey = declaredKey;

            if (toIsTable)
            {
                // A key into somebody else's table is filed by the constraint itself, ordinally, as a key on
                // one is: EdgeKey folds case, and two tables whose quoted names differ only by case - legacy.Foo
                // and legacy.foo - are two tables, whose keys must not overwrite each other. The declared half
                // it merges with is the one filed under the folded key that resolved to this very node.
                edgeKey = string.Join(KeySeparator, "d", key.Schema, key.Table, key.Name);

                if (edges.TryGetValue(declaredKey, out RelationshipEdge? candidate)
                    && candidate is { Declared: true, Physical: false, ToIsTable: true }
                    && string.Equals(candidate.ToAlias, toKey, StringComparison.Ordinal))
                {
                    edges.Remove(declaredKey);
                    edges[edgeKey] = candidate;
                }
            }

            var primary = PrimaryColumn(key.Columns);
            var label = toIsTable
                ? LabelFor(key.Columns, key.LinkedColumns, key.PrimaryKey, key.LinkedPrimaryKey)
                : primary;

            edges[edgeKey] = edges.TryGetValue(edgeKey, out RelationshipEdge? declared)
                ? declared with
                {
                    Physical = true,
                    Column = toIsTable ? label : declared.Column,
                    Columns = string.Join(", ", key.Columns),
                    LinkedColumns = string.Join(", ", key.LinkedColumns),
                    Validated = key.Validated,
                }
                : new RelationshipEdge(
                    source.Alias,
                    toKey,
                    label,
                    nodes.MemberFor(source.Alias, primary),
                    Declared: false,
                    Physical: true,
                    key.OnDelete,
                    key.Name)
                {
                    Columns = string.Join(", ", key.Columns),
                    LinkedColumns = string.Join(", ", key.LinkedColumns),
                    Validated = key.Validated,
                    ToIsTable = toIsTable,
                };
        }

        private void AddPhysicalOnTable(PhysicalForeignKey key, End from, End to)
        {
            // A key on somebody else's table has no declared half to merge with, so it is keyed by the
            // constraint itself - ordinally, because two tables whose quoted names differ only by case are
            // two tables, and a case-folded key would let one overwrite the other.
            string edgeKey = string.Join(KeySeparator, "t", key.Schema, key.Table, key.Name);

            edges[edgeKey] = new RelationshipEdge(
                from.NodeKey!,
                to.NodeKey!,
                LabelFor(key.Columns, key.LinkedColumns, key.PrimaryKey, key.LinkedPrimaryKey),
                Member: null,
                Declared: false,
                Physical: true,
                key.OnDelete,
                key.Name)
            {
                Columns = string.Join(", ", key.Columns),
                LinkedColumns = string.Join(", ", key.LinkedColumns),
                Validated = key.Validated,
                FromIsTable = true,
                ToIsTable = to.Kind == EndKind.Table,
            };
        }

        /// <summary>What one end of a key is, registering it as a table node when it is one and <paramref name="register" /> says so.</summary>
        private End Resolve(string schema, string name, bool register = true)
        {
            string key = Key(schema, name);

            if (byTable.TryGetValue(key, out RelationshipNode? document))
            {
                return new End(EndKind.Document, schema, name, document, null);
            }

            if (claimed.Contains(key))
            {
                return new End(EndKind.Hidden, schema, name, null, null);
            }

            if (IsMartenBookkeeping(name))
            {
                return new End(EndKind.Bookkeeping, schema, name, null, null);
            }

            if (database is null)
            {
                return new End(EndKind.Unplaced, schema, name, null, null);
            }

            DatabaseObjectClassifier classifier = database.Gate.Classifier;

            if (classifier.IsHiddenTable(schema, name) || classifier.ClassifyRelation(schema, name) is not { } ownership)
            {
                return new End(EndKind.Hidden, schema, name, null, null);
            }

            switch (ownership.Owner)
            {
                case DatabaseObjectOwner.MartenDocument:
                    // Another store's collection in a shared database: a Marten table, so never somebody
                    // else's, and not a node of this store's picture either.
                    return new End(EndKind.Unplaced, schema, name, null, null);

                case DatabaseObjectOwner.MartenEventStore:
                    return new End(EndKind.Bookkeeping, schema, name, null, null);

                case DatabaseObjectOwner.MartenInfrastructure:
                    // An mt_doc_ table nobody registers is a discovered collection, reportable as it always
                    // was; everything else with Marten's prefix is its bookkeeping.
                    return name.StartsWith(DocumentTablePrefix, StringComparison.OrdinalIgnoreCase)
                        ? new End(EndKind.Unplaced, schema, name, null, null)
                        : new End(EndKind.Bookkeeping, schema, name, null, null);
            }

            if (!canSee(schema))
            {
                // Classified, so the caller can tell hidden from withheld, but never a node: a table in a
                // schema this visitor is not shown does not exist as far as the picture is concerned.
                return new End(EndKind.Table, schema, name, null, null);
            }

            string tableKey = RelationshipTableNode.KeyFor(schema, name);

            if (!tables.TryGetValue(tableKey, out RelationshipTableNode? table))
            {
                if (!register)
                {
                    return new End(EndKind.Table, schema, name, null, null);
                }

                table = new RelationshipTableNode(
                    tableKey,
                    schema,
                    name,
                    RelationshipTableNode.SchemaHue(schema),
                    Count(tableKey),
                    ownership.Owner,
                    ownership.RecognisedAs,
                    database.Gate.IsStoreSchema(schema));

                tables[tableKey] = table;
            }

            return new End(EndKind.Table, schema, name, null, table);
        }

        /// <summary>
        /// A registered table by name - exactly, or by the one name that differs only in case, which is how
        /// a declaration Marten folds and a catalog name meet.
        /// </summary>
        private RelationshipTableNode? FindExisting(string schema, string name)
        {
            if (tables.TryGetValue(RelationshipTableNode.KeyFor(schema, name), out RelationshipTableNode? exact))
            {
                return exact;
            }

            RelationshipTableNode? found = null;

            foreach (RelationshipTableNode table in tables.Values)
            {
                if (string.Equals(table.Schema, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (found is not null)
                    {
                        return null;
                    }

                    found = table;
                }
            }

            return found;
        }

        private static string SchemaOf(RelationshipNode node) => node.Schema ?? string.Empty;

        private static string TableOf(RelationshipNode node) => node.Table ?? string.Empty;

        /// <summary>A table node's row count, from the gate's relation list; the catalog already turned "never analysed" into null.</summary>
        private DocumentCount Count(string tableKey) =>
            !relations.TryGetValue(tableKey, out CatalogRelation? relation)
                ? DocumentCount.Unavailable
                : relation.EstimatedRows is { } rows
                    ? DocumentCount.Estimate(rows)
                    : DocumentCount.Unknown;

        /// <summary>Sorted by what a person reads, so the table below the picture is in a stable, readable order.</summary>
        private int Compare(RelationshipEdge left, RelationshipEdge right)
        {
            var byFrom = string.CompareOrdinal(Display(left.FromAlias), Display(right.FromAlias));
            if (byFrom != 0)
            {
                return byFrom;
            }

            var byTo = string.CompareOrdinal(Display(left.ToAlias), Display(right.ToAlias));
            if (byTo != 0)
            {
                return byTo;
            }

            var byColumn = string.CompareOrdinal(left.Column, right.Column);

            return byColumn != 0 ? byColumn : string.CompareOrdinal(left.ConstraintName, right.ConstraintName);
        }

        private string Display(string nodeKey) =>
            tables.TryGetValue(nodeKey, out RelationshipTableNode? table) ? table.QualifiedName : nodeKey;
    }
}
