namespace MartenStudio.Services.Relationships;

/// <summary>The outline of a node's box.</summary>
internal enum GraphNodeShape
{
    /// <summary>Rounded corners: a Marten document type.</summary>
    Rounded,

    /// <summary>Square corners: a table the studio does not map as a document type.</summary>
    Square,
}

/// <summary>One box of a <see cref="GraphDiagramModel" />.</summary>
/// <param name="Key">
/// What edges name it by. Compared ordinally, so two quoted names that differ only by case are two nodes.
/// </param>
/// <param name="Title">The first line, cut with an ellipsis when it does not fit.</param>
/// <param name="Subtitle">The second line: the .NET type, or the table's schema.</param>
/// <param name="Detail">The third line: a row count, or nothing.</param>
/// <param name="Hue">The colour of the marker: the collection's, or the schema's.</param>
/// <param name="Shape">Rounded for a document type, square for a table.</param>
/// <param name="Href">Where the node links to - already carrying the scope.</param>
/// <param name="Tooltip">The whole name and anything else the box cut off.</param>
/// <param name="Emphasis">Whether this is the node the diagram is about - the neighbourhood graph's centre.</param>
internal sealed record GraphDiagramNode(
    string Key,
    string Title,
    string? Subtitle,
    string? Detail,
    int Hue,
    GraphNodeShape Shape,
    string Href,
    string Tooltip,
    bool Emphasis = false);

/// <summary>One arrow of a <see cref="GraphDiagramModel" />, from the node that points to the one pointed at.</summary>
/// <param name="From">The pointing node's key.</param>
/// <param name="To">The referenced node's key.</param>
/// <param name="CssClass">The line style: <c>ms-graph-edge-both</c>, <c>-declared</c>, <c>-physical</c>, <c>-table</c>, <c>-notvalid</c>.</param>
/// <param name="Description">The tooltip: every column, the state, the delete action.</param>
/// <param name="Label">What is written on the arrow, or <see langword="null" /> for nothing.</param>
/// <param name="Id">
/// A stable identity the accessible table carries too (<c>data-edge</c>), so a test - or a person with the
/// inspector open - can hold the picture and the table to each other.
/// </param>
internal sealed record GraphDiagramEdge(
    string From,
    string To,
    string CssClass,
    string Description,
    string? Label,
    string Id);

/// <summary>
/// A picture of boxes and arrows, in the diagram component's own terms - nothing about documents, tables
/// or foreign keys in it.
/// </summary>
/// <remarks>
/// The neutral half of the relationships diagram (plan §5): <c>RelationshipGraph.razor</c> turns a
/// relationship graph into one of these, and so does the object detail page's neighbourhood graph, so
/// the two draw with one piece of SVG and one layout.
/// </remarks>
/// <param name="Nodes">The boxes, in the order that breaks layout ties.</param>
/// <param name="Edges">The arrows.</param>
/// <param name="Summary">One sentence, which is the picture's accessible name.</param>
internal sealed record GraphDiagramModel(
    IReadOnlyList<GraphDiagramNode> Nodes,
    IReadOnlyList<GraphDiagramEdge> Edges,
    string Summary)
{
    /// <summary>Nothing to draw.</summary>
    public static GraphDiagramModel Empty { get; } = new([], [], string.Empty);
}
