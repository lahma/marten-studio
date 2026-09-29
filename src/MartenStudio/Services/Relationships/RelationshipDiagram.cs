using System.Globalization;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// Turns a relationship graph into the neutral diagram's model, and names every edge the same way for the
/// picture and for the table beside it.
/// </summary>
internal static class RelationshipDiagram
{
    /// <summary>The diagram model for <paramref name="graph" />.</summary>
    /// <param name="graph">What to draw: its document nodes, table nodes and edges - all of them.</param>
    /// <param name="collectionHref">The link to a collection's list, scope and all.</param>
    /// <param name="tableHref">The link to a table's object detail, scope and all.</param>
    /// <param name="centre">The node the picture is about, drawn emphasised; <see langword="null" /> for none.</param>
    public static GraphDiagramModel From(
        RelationshipGraph graph,
        Func<string, string> collectionHref,
        Func<string, string, string> tableHref,
        string? centre = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(collectionHref);
        ArgumentNullException.ThrowIfNull(tableHref);

        List<GraphDiagramNode> nodes = [];

        foreach (RelationshipNode node in graph.Nodes)
        {
            nodes.Add(new GraphDiagramNode(
                node.Alias,
                node.Alias,
                node.DisplayName,
                CountText(node.Count, "document", "documents"),
                node.Hue,
                GraphNodeShape.Rounded,
                collectionHref(node.Alias),
                $"{node.Alias} - {node.DisplayName}",
                Emphasis: string.Equals(node.Alias, centre, StringComparison.Ordinal)));
        }

        foreach (RelationshipTableNode table in graph.Tables)
        {
            nodes.Add(new GraphDiagramNode(
                table.Key,
                table.Name,
                table.Schema,
                CountText(table.Count, "row", "rows"),
                table.Hue,
                GraphNodeShape.Square,
                tableHref(table.Schema, table.Name),
                table.OwnerHint is { } hint ? $"{table.QualifiedName} - {hint}" : table.QualifiedName,
                Emphasis: string.Equals(table.Key, centre, StringComparison.Ordinal)));
        }

        List<GraphDiagramEdge> edges = [];

        foreach (RelationshipEdge edge in graph.Edges)
        {
            edges.Add(new GraphDiagramEdge(
                edge.FromAlias,
                edge.ToAlias,
                EdgeClass(edge),
                Describe(graph, edge),
                edge.IsTableEdge ? edge.Column : null,
                EdgeId(graph, edge)));
        }

        return new GraphDiagramModel(nodes, edges, graph.Summary);
    }

    /// <summary>The CSS class of an edge's line style.</summary>
    public static string EdgeClass(RelationshipEdge edge) => "ms-graph-edge-" + RelationshipViews.StyleOf(edge);

    /// <summary>
    /// An edge's identity, the same on the picture and on the table's row (<c>data-edge</c>): its two ends
    /// as a person reads them, and the constraint - or, for a key only the configuration declares, its
    /// columns.
    /// </summary>
    public static string EdgeId(RelationshipGraph graph, RelationshipEdge edge)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(edge);

        return graph.DisplayName(edge.FromAlias) + "→" + graph.DisplayName(edge.ToAlias) + "|" +
               (edge.ConstraintName ?? edge.AllColumns);
    }

    /// <summary>
    /// The tooltip: the key as it has always read for two document types, and with every column on both
    /// sides for one that has a table at either end.
    /// </summary>
    public static string Describe(RelationshipGraph graph, RelationshipEdge edge)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(edge);

        if (!edge.IsTableEdge)
        {
            return $"{edge.FromAlias}.{edge.Column} → {edge.ToAlias} ({edge.State}, on delete {edge.OnDelete})";
        }

        string linked = edge.LinkedColumns.Length > 0 ? " (" + edge.LinkedColumns + ")" : string.Empty;

        return $"{graph.DisplayName(edge.FromAlias)} ({edge.AllColumns}) → {graph.DisplayName(edge.ToAlias)}{linked} " +
               $"({StateSentence(edge)}, on delete {edge.OnDelete})";
    }

    /// <summary>A node's third line: <c>~1,204 rows</c>, and nothing for a count nobody has.</summary>
    /// <remarks>
    /// Prefixed <c>~</c> because it is a <c>reltuples</c> estimate (D8). A table Postgres has never
    /// analysed draws nothing at all rather than <c>~0</c>.
    /// </remarks>
    public static string CountText(DocumentCount count, string singular, string plural)
    {
        if (count.IsUnavailable)
        {
            return string.Empty;
        }

        string rows = count.Value.ToString("N0", CultureInfo.InvariantCulture);

        return (count.IsEstimate ? "~" : string.Empty) + rows + " " + (count.Value == 1 ? singular : plural);
    }

    /// <summary>A key's state, as a sentence for a tooltip.</summary>
    public static string StateSentence(RelationshipEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        if (edge.OnDocumentTable)
        {
            return edge.Validated ? edge.State : edge.State + ", NOT VALID";
        }

        return edge.Validated ? "enforced" : "NOT VALID";
    }
}
