namespace MartenStudio.Services.Relationships;

/// <summary>Which of the graph's edges the Relationships screen draws.</summary>
internal enum RelationshipViewMode
{
    /// <summary>Everything: document types, tables and every key between them. The default.</summary>
    Both,

    /// <summary>Document types and the keys between them - the screen as it was before tables existed on it.</summary>
    Documents,

    /// <summary>Every key with a table at either end, and the document types those keys reach.</summary>
    Tables,
}

/// <summary>One schema chip: a schema with tables that take part in a key, and how many.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="Tables">How many of its tables take part in a key.</param>
/// <param name="Hue">The schema's colour, the same one its table nodes are drawn in.</param>
internal sealed record RelationshipSchemaChip(string Schema, int Tables, int Hue);

/// <summary>What the screen draws for one view and one schema filter.</summary>
/// <param name="Drawn">
/// The graph cut down to what is drawn: its document nodes, its table nodes and its edges. The
/// accessible table lists exactly <see cref="RelationshipGraph.Edges" /> of this graph.
/// </param>
/// <param name="Isolated">Tables in visible schemas with no key at all, for the Tables view's chips.</param>
/// <param name="DocumentEdges">How many keys the Documents view draws.</param>
/// <param name="TableEdges">How many keys the Tables view draws, under the current schema filter.</param>
/// <param name="Schemas">The schema chips.</param>
/// <param name="OverCap">Whether the picture has too many nodes to draw legibly.</param>
internal sealed record RelationshipProjection(
    RelationshipGraph Drawn,
    IReadOnlyList<RelationshipTableNode> Isolated,
    int DocumentEdges,
    int TableEdges,
    IReadOnlyList<RelationshipSchemaChip> Schemas,
    bool OverCap)
{
    /// <summary>How many keys the Both view draws.</summary>
    public int AllEdges => DocumentEdges + TableEdges;

    /// <summary>How many boxes the picture would have.</summary>
    public int DrawnNodes => Drawn.Nodes.Count + Drawn.Tables.Count;
}

/// <summary>
/// The Relationships screen's views and schema filter, and the object detail page's neighbourhood - all
/// as pure functions of one graph, so what is drawn and what is listed can never disagree.
/// </summary>
internal static class RelationshipViews
{
    /// <summary>
    /// How many boxes a picture with tables in it may have before it is replaced by "pick a schema or a
    /// view". Sixty boxes is already a diagram wider than a desktop screen at a size anybody can read; the
    /// table below it lists every edge either way.
    /// </summary>
    public const int NodeCap = 60;

    /// <summary>The view a <c>?view=</c> value names; anything unrecognised is the default.</summary>
    public static RelationshipViewMode Parse(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "documents" => RelationshipViewMode.Documents,
        "tables" => RelationshipViewMode.Tables,
        _ => RelationshipViewMode.Both,
    };

    /// <summary>What <c>?view=</c> says for a view; <see langword="null" /> for the default, which the URL leaves out.</summary>
    public static string? Token(RelationshipViewMode mode) => mode switch
    {
        RelationshipViewMode.Documents => "documents",
        RelationshipViewMode.Tables => "tables",
        _ => null,
    };

    /// <summary>What one view of <paramref name="graph" /> draws.</summary>
    /// <param name="graph">The whole graph, as the service answered it for this visitor.</param>
    /// <param name="mode">The view.</param>
    /// <param name="schema">
    /// The schema chip, or <see langword="null" /> for every schema. It filters table nodes only: a
    /// document type is in the store's schemas and is drawn in every view that draws documents.
    /// </param>
    public static RelationshipProjection Project(RelationshipGraph graph, RelationshipViewMode mode, string? schema)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (!graph.HasTableEdges)
        {
            // No table takes part in any key: the screen is exactly what it was before tables could be
            // on it - every document type, every key between them, no switch and no cap.
            return new RelationshipProjection(
                graph with { Tables = [] },
                Isolated(graph, schema),
                graph.Edges.Count,
                0,
                [],
                OverCap: false);
        }

        bool Passes(string key) =>
            schema is null
            || graph.FindTable(key) is not { } table
            || string.Equals(table.Schema, schema, StringComparison.Ordinal);

        bool TableEdgePasses(RelationshipEdge edge) =>
            (!edge.FromIsTable || Passes(edge.FromAlias)) && (!edge.ToIsTable || Passes(edge.ToAlias));

        int documentEdges = graph.Edges.Count(static x => !x.IsTableEdge);
        int tableEdges = graph.Edges.Count(x => x.IsTableEdge && TableEdgePasses(x));

        List<RelationshipEdge> drawn = [];
        foreach (RelationshipEdge edge in graph.Edges)
        {
            bool include = mode switch
            {
                RelationshipViewMode.Documents => !edge.IsTableEdge,
                RelationshipViewMode.Tables => edge.IsTableEdge && TableEdgePasses(edge),
                _ => !edge.IsTableEdge || TableEdgePasses(edge),
            };

            if (include)
            {
                drawn.Add(edge);
            }
        }

        HashSet<string> ends = new(StringComparer.Ordinal);
        foreach (RelationshipEdge edge in drawn)
        {
            ends.Add(edge.FromAlias);
            ends.Add(edge.ToAlias);
        }

        // Documents and Both draw every document type, as the screen always has - an unconnected type is
        // still part of the store's shape. Tables draws only the ones its keys reach.
        List<RelationshipNode> documents = mode == RelationshipViewMode.Tables
            ? [.. graph.Nodes.Where(x => ends.Contains(x.Alias))]
            : [.. graph.Nodes];

        List<RelationshipTableNode> tables = mode == RelationshipViewMode.Documents
            ? []
            : [.. graph.Tables.Where(x => ends.Contains(x.Key))];

        RelationshipGraph cut = graph with { Nodes = documents, Edges = drawn, Tables = tables };

        return new RelationshipProjection(
            cut,
            mode == RelationshipViewMode.Documents ? [] : Isolated(graph, schema),
            documentEdges,
            tableEdges,
            Chips(graph),
            OverCap: tables.Count > 0 && documents.Count + tables.Count > NodeCap);
    }

    /// <summary>
    /// The neighbourhood of one object: the object, every node one key away from it, and those keys -
    /// its keys that cannot be drawn, listed - or why there is nothing to show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A key with an end nothing can place is still the object's key.</b> A table whose only key points
    /// at another store's document table, or at an <c>mt_doc_</c> table no store registers, has no edge to
    /// draw - and without its <see cref="RelationshipGraph.Unmatched" /> rows the tab said "no foreign keys"
    /// while the Keys tab beside it listed one. So the object's unmatched rows are kept, whether or not the
    /// object is itself a node (such a table is not, when the key is on it). They name nothing the visitor
    /// may not see: an unmatched row has both ends in visible schemas by construction, and a key with an end
    /// in a withheld one is a <see cref="WithheldForeignKey" />, counted and never named.
    /// </para>
    /// </remarks>
    /// <param name="graph">The whole graph, as the service answered it for this visitor.</param>
    /// <param name="schema">The object's schema, as the catalog has it.</param>
    /// <param name="name">The object's name, as the catalog has it.</param>
    public static RelationshipNeighbourhood Neighbourhood(RelationshipGraph graph, string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(name))
        {
            return RelationshipNeighbourhood.Refused("This link names no object.");
        }

        if (graph.VisibleSchemas is { } visible && !visible.Contains(schema, StringComparer.Ordinal))
        {
            // Said the same way whether or not the schema exists, as the database browser says it.
            return RelationshipNeighbourhood.Refused(
                "'" + schema + "' is not one of the schemas this studio shows you, so its keys are not shown either.");
        }

        string? centre = null;

        foreach (RelationshipNode node in graph.Nodes)
        {
            if (string.Equals(node.Schema, schema, StringComparison.Ordinal)
                && string.Equals(node.Table, name, StringComparison.Ordinal))
            {
                centre = node.Alias;
                break;
            }
        }

        centre ??= graph.FindTable(RelationshipTableNode.KeyFor(schema, name))?.Key;

        int withheld = centre is null ? 0 : graph.Withheld.Count(x => string.Equals(x.VisibleEnd, centre, StringComparison.Ordinal));

        List<UnmatchedForeignKey> unmatched = [.. graph.Unmatched.Where(x => x.Touches(schema, name))];

        List<RelationshipEdge> edges = centre is null
            ? []
            : [.. graph.Edges.Where(x =>
                string.Equals(x.FromAlias, centre, StringComparison.Ordinal)
                || string.Equals(x.ToAlias, centre, StringComparison.Ordinal))];

        if (edges.Count == 0 && unmatched.Count == 0)
        {
            return RelationshipNeighbourhood.Empty(withheld);
        }

        HashSet<string> ends = new(StringComparer.Ordinal);
        foreach (RelationshipEdge edge in edges)
        {
            ends.Add(edge.FromAlias);
            ends.Add(edge.ToAlias);
        }

        RelationshipGraph cut = graph with
        {
            Nodes = [.. graph.Nodes.Where(x => ends.Contains(x.Alias))],
            Tables = [.. graph.Tables.Where(x => ends.Contains(x.Key))],
            Edges = edges,
            Unmatched = unmatched,
            Withheld = centre is null ? [] : [.. graph.Withheld.Where(x => string.Equals(x.VisibleEnd, centre, StringComparison.Ordinal))],
        };

        return new RelationshipNeighbourhood(NeighbourhoodState.Loaded, cut, centre, withheld, null);
    }

    /// <summary>The line styles a set of edges is drawn in, for the legend.</summary>
    public static IReadOnlyList<string> Styles(IEnumerable<RelationshipEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        HashSet<string> styles = new(StringComparer.Ordinal);
        foreach (RelationshipEdge edge in edges)
        {
            styles.Add(StyleOf(edge));
        }

        // A fixed order, so the legend reads the same way whatever order the edges came in.
        return [.. AllStyles.Where(styles.Contains)];
    }

    /// <summary>Every line style, in legend order.</summary>
    public static IReadOnlyList<string> AllStyles { get; } = ["both", "declared", "physical", "table", "notvalid"];

    /// <summary>
    /// One edge's line style: a key on a document table keeps the declared/physical drift styles, because
    /// a Marten migration can change it; a key on somebody else's table is solid, and dashed when Postgres
    /// has not validated it.
    /// </summary>
    public static string StyleOf(RelationshipEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        if (!edge.OnDocumentTable)
        {
            return edge.Validated ? "table" : "notvalid";
        }

        return (edge.Declared, edge.Physical) switch
        {
            (true, false) => "declared",
            (false, true) => "physical",
            _ => "both",
        };
    }

    private static List<RelationshipTableNode> Isolated(RelationshipGraph graph, string? schema)
    {
        HashSet<string> connected = new(StringComparer.Ordinal);
        foreach (RelationshipEdge edge in graph.Edges)
        {
            connected.Add(edge.FromAlias);
            connected.Add(edge.ToAlias);
        }

        return
        [
            .. graph.Tables.Where(x =>
                !connected.Contains(x.Key)
                && (schema is null || string.Equals(x.Schema, schema, StringComparison.Ordinal))),
        ];
    }

    private static List<RelationshipSchemaChip> Chips(RelationshipGraph graph)
    {
        Dictionary<string, HashSet<string>> bySchema = new(StringComparer.Ordinal);

        foreach (RelationshipEdge edge in graph.Edges)
        {
            foreach ((bool isTable, string key) in new[] { (edge.FromIsTable, edge.FromAlias), (edge.ToIsTable, edge.ToAlias) })
            {
                if (isTable && graph.FindTable(key) is { } table)
                {
                    if (!bySchema.TryGetValue(table.Schema, out HashSet<string>? keys))
                    {
                        keys = new HashSet<string>(StringComparer.Ordinal);
                        bySchema[table.Schema] = keys;
                    }

                    keys.Add(key);
                }
            }
        }

        return
        [
            .. bySchema
                .OrderBy(static x => x.Key, StringComparer.Ordinal)
                .Select(static x => new RelationshipSchemaChip(x.Key, x.Value.Count, RelationshipTableNode.SchemaHue(x.Key))),
        ];
    }
}

/// <summary>What the neighbourhood graph can be.</summary>
internal enum NeighbourhoodState
{
    /// <summary>The object and its neighbours.</summary>
    Loaded,

    /// <summary>The object has no key this visitor may see.</summary>
    Empty,

    /// <summary>The object is not one this visitor may see the structure of.</summary>
    Refused,
}

/// <summary>One object's direct neighbours, or why there are none to draw.</summary>
/// <param name="State">What it is.</param>
/// <param name="Graph">The object, its neighbours and the keys between them; empty unless loaded.</param>
/// <param name="Centre">The object's node key, when it is a node.</param>
/// <param name="Withheld">How many of its keys reach schemas the visitor is not shown. A count, never names.</param>
/// <param name="Reason">Why it was refused.</param>
internal sealed record RelationshipNeighbourhood(
    NeighbourhoodState State,
    RelationshipGraph Graph,
    string? Centre,
    int Withheld,
    string? Reason)
{
    /// <summary>Refused, and why.</summary>
    public static RelationshipNeighbourhood Refused(string reason) =>
        new(NeighbourhoodState.Refused, RelationshipGraph.None, null, 0, reason);

    /// <summary>Nothing to draw, though <paramref name="withheld" /> keys may reach schemas the visitor is not shown.</summary>
    public static RelationshipNeighbourhood Empty(int withheld) =>
        new(NeighbourhoodState.Empty, RelationshipGraph.None, null, withheld, null);
}
