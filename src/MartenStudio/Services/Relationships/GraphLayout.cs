using System.Globalization;

namespace MartenStudio.Services.Relationships;

/// <summary>One relationship, reduced to the two ends the layout cares about.</summary>
/// <param name="From">The collection that points.</param>
/// <param name="To">The collection pointed at.</param>
internal readonly record struct GraphLink(string From, string To);

/// <summary>One node's box, in SVG user units.</summary>
/// <param name="Alias">The collection this box is.</param>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The box width.</param>
/// <param name="Height">The box height.</param>
/// <param name="Layer">Which column of the diagram it sits in; zero is the leftmost.</param>
/// <param name="Row">Its position within that column, from the top.</param>
internal sealed record GraphNodeBox(string Alias, double X, double Y, double Width, double Height, int Layer, int Row)
{
    /// <summary>The horizontal centre.</summary>
    public double CentreX => X + (Width / 2);

    /// <summary>The vertical centre.</summary>
    public double CentreY => Y + (Height / 2);

    /// <summary>Whether this box and <paramref name="other" /> share any area at all.</summary>
    /// <param name="other">The other box.</param>
    public bool Overlaps(GraphNodeBox other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return X < other.X + other.Width &&
               other.X < X + Width &&
               Y < other.Y + other.Height &&
               other.Y < Y + Height;
    }
}

/// <summary>One edge, as the path an <c>&lt;svg&gt;</c> draws.</summary>
/// <param name="Ordinal">
/// Its position in the input. Blazor's <c>@key</c> uses this rather than the record itself: a record with
/// value equality makes two identical edges the same key, and a duplicate key takes the circuit down.
/// </param>
/// <param name="FromAlias">The collection that points.</param>
/// <param name="ToAlias">The collection pointed at.</param>
/// <param name="Path">The <c>d</c> attribute.</param>
/// <param name="Arrow">
/// The arrowhead, as a closed triangle at the end of <paramref name="Path" /> pointing along it.
/// </param>
/// <param name="LabelX">Where a label for this edge belongs, horizontally.</param>
/// <param name="LabelY">Where a label for this edge belongs, vertically.</param>
/// <param name="IsBackEdge">Whether this edge closes a cycle and is therefore drawn the long way round.</param>
/// <param name="IsSelfLoop">Whether both ends are the same collection.</param>
internal sealed record GraphEdgePath(
    int Ordinal,
    string FromAlias,
    string ToAlias,
    string Path,
    string Arrow,
    double LabelX,
    double LabelY,
    bool IsBackEdge,
    bool IsSelfLoop);

/// <summary>The finished picture: where every box is, every path, and the viewBox around them.</summary>
/// <param name="Nodes">The boxes, in the order they were given.</param>
/// <param name="Edges">The paths, in the order they were given.</param>
/// <param name="Width">The picture's width in user units.</param>
/// <param name="Height">The picture's height in user units.</param>
internal sealed record GraphLayoutResult(
    IReadOnlyList<GraphNodeBox> Nodes,
    IReadOnlyList<GraphEdgePath> Edges,
    double Width,
    double Height)
{
    /// <summary>Nothing to draw.</summary>
    public static GraphLayoutResult Empty { get; } = new([], [], 0, 0);

    /// <summary>The <c>viewBox</c> attribute.</summary>
    public string ViewBox => FormattableString.Invariant($"0 0 {Width} {Height}");
}

/// <summary>
/// Lays a foreign-key graph out on a grid, deterministically and without a library.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layered, not force-directed.</b> A force-directed layout is a physics simulation: it needs
/// JavaScript, it settles somewhere different every time it runs, and two people looking at the same
/// store see two different pictures. A layered layout is a pure function of its input — the same store
/// draws the same diagram in every session, in every process and in a screenshot taken a year ago — and
/// it says something true that a spring layout does not, because the horizontal axis <em>is</em> the
/// direction of reference: a document that points at another sits to the right of it, so the things
/// nothing depends on are down the left and the leaves are down the right.
/// </para>
/// <para>
/// <b>Cycles are broken, not avoided.</b> Document models have them — a pair of types pointing at each
/// other is ordinary — so the layering runs over a DAG obtained by dropping the edges that close a cycle
/// (depth-first, nodes and edges visited in their given order, so which edge gets dropped is
/// deterministic too) and those edges are drawn afterwards as curves that dip below the diagram. Nothing
/// here can fail to terminate: the cycle-breaking pass visits each node once, and the layering that
/// follows it runs on a graph that provably has no cycles left.
/// </para>
/// <para>
/// Within a layer the order is settled by barycentre sweeps — the classic Sugiyama crossing-reduction
/// heuristic — over a fixed number of passes, each a stable sort, so it converges on one answer rather
/// than oscillating. Boxes are a fixed size on a fixed pitch, which is what makes "no two boxes overlap"
/// a property of the constants rather than something to be checked at run time.
/// </para>
/// </remarks>
internal static class GraphLayout
{
    /// <summary>How wide a node's box is.</summary>
    public const double NodeWidth = 176;

    /// <summary>How tall a node's box is. Three lines of text: the alias, the .NET type, the row count.</summary>
    public const double NodeHeight = 64;

    /// <summary>The empty space between one layer's boxes and the next layer's.</summary>
    public const double LayerGap = 104;

    /// <summary>The empty space between two boxes in the same layer.</summary>
    public const double RowGap = 34;

    /// <summary>
    /// The border around the picture. Wider than it looks necessary because a self-reference is drawn as
    /// a loop above its own box, and the topmost row's loop has to stay inside the viewBox.
    /// </summary>
    public const double Margin = 52;

    /// <summary>How far above its box a self-reference loop reaches.</summary>
    public const double SelfLoopHeight = 40;

    /// <summary>How far below the lowest box a cycle-closing edge dips.</summary>
    public const double BackEdgeDrop = 44;

    /// <summary>How long the arrowhead triangle is, along the edge.</summary>
    public const double ArrowLength = 11;

    /// <summary>How wide the arrowhead triangle is, across the edge.</summary>
    public const double ArrowWidth = 9;

    /// <summary>
    /// How many barycentre passes are made. Four is two in each direction, which is where the classic
    /// heuristic stops improving on graphs this size; a fixed number is also what keeps the result a
    /// function of the input rather than of a convergence test.
    /// </summary>
    public const int BarycentreSweeps = 4;

    /// <summary>
    /// Lays out <paramref name="aliases" /> and <paramref name="links" />.
    /// </summary>
    /// <param name="aliases">
    /// The nodes, in the order they should break ties — the caller sorts them, and that order is what
    /// makes the result stable.
    /// </param>
    /// <param name="links">The edges. Links naming an alias that is not a node are ignored.</param>
    public static GraphLayoutResult Compute(IReadOnlyList<string> aliases, IReadOnlyList<GraphLink> links)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(links);

        List<string> nodes = [];
        Dictionary<string, int> index = new(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in aliases)
        {
            if (index.TryAdd(alias, nodes.Count))
            {
                nodes.Add(alias);
            }
        }

        if (nodes.Count == 0)
        {
            return GraphLayoutResult.Empty;
        }

        // Every link that names two known nodes, remembering where it was in the input so the drawn
        // paths come back in the caller's own order.
        List<(int Ordinal, int From, int To)> resolved = [];

        for (var i = 0; i < links.Count; i++)
        {
            if (index.TryGetValue(links[i].From, out var from) && index.TryGetValue(links[i].To, out var to))
            {
                resolved.Add((i, from, to));
            }
        }

        bool[] isBackEdge = BreakCycles(nodes.Count, resolved);
        int[] layers = AssignLayers(nodes.Count, resolved, isBackEdge);
        List<List<int>> rows = OrderWithinLayers(nodes.Count, resolved, isBackEdge, layers);

        GraphNodeBox[] boxes = Place(nodes, layers, rows);

        var width = (boxes.Max(static x => x.X + x.Width)) + Margin;
        var bottom = boxes.Max(static x => x.Y + x.Height);

        List<GraphEdgePath> paths = [];
        var hasBackEdge = false;

        for (var i = 0; i < resolved.Count; i++)
        {
            var (ordinal, from, to) = resolved[i];

            if (from == to)
            {
                paths.Add(SelfLoop(ordinal, boxes[from]));
                continue;
            }

            if (isBackEdge[i] || layers[from] == layers[to])
            {
                hasBackEdge = true;
                paths.Add(BackEdge(ordinal, boxes[from], boxes[to], bottom + BackEdgeDrop));
                continue;
            }

            paths.Add(ForwardEdge(ordinal, boxes[from], boxes[to]));
        }

        var height = bottom + (hasBackEdge ? BackEdgeDrop + Margin : Margin);

        return new GraphLayoutResult(boxes, paths, Round(width), Round(height));
    }

    /// <summary>
    /// Marks the links that close a cycle, by depth-first search in node and link order.
    /// </summary>
    /// <remarks>
    /// An explicit stack rather than recursion: the node count is the store's document-type count, which
    /// nothing bounds, and a store shaped as one long chain would otherwise put that whole chain on the
    /// call stack.
    /// </remarks>
    private static bool[] BreakCycles(int nodeCount, List<(int Ordinal, int From, int To)> links)
    {
        List<int>[] outgoing = Adjacency(nodeCount, links);

        var back = new bool[links.Count];

        // 0 unvisited, 1 on the current path, 2 finished. A link into a node that is on the current path
        // is exactly a link that closes a cycle.
        var state = new byte[nodeCount];
        var cursor = new int[nodeCount];

        Stack<int> path = new();

        for (var start = 0; start < nodeCount; start++)
        {
            if (state[start] != 0)
            {
                continue;
            }

            state[start] = 1;
            cursor[start] = 0;
            path.Push(start);

            while (path.Count > 0)
            {
                var node = path.Peek();

                if (cursor[node] >= outgoing[node].Count)
                {
                    state[node] = 2;
                    path.Pop();
                    continue;
                }

                var linkIndex = outgoing[node][cursor[node]++];
                var next = links[linkIndex].To;

                if (next == node)
                {
                    // A self-reference is not a cycle to break; it is drawn as a loop.
                    continue;
                }

                switch (state[next])
                {
                    case 0:
                        state[next] = 1;
                        cursor[next] = 0;
                        path.Push(next);
                        break;

                    case 1:
                        back[linkIndex] = true;
                        break;

                    default:
                        break;
                }
            }
        }

        return back;
    }

    /// <summary>
    /// Longest-path layering along the direction of reference: a node sits one layer to the right of
    /// everything it points at.
    /// </summary>
    private static int[] AssignLayers(
        int nodeCount,
        List<(int Ordinal, int From, int To)> links,
        bool[] isBackEdge)
    {
        List<int>[] outgoing = Adjacency(nodeCount, links, isBackEdge);

        var layers = new int[nodeCount];
        var settled = new bool[nodeCount];
        var cursor = new int[nodeCount];

        Stack<int> pending = new();

        for (var start = 0; start < nodeCount; start++)
        {
            if (settled[start])
            {
                continue;
            }

            pending.Push(start);
            cursor[start] = 0;

            while (pending.Count > 0)
            {
                var node = pending.Peek();

                if (cursor[node] < outgoing[node].Count)
                {
                    var next = links[outgoing[node][cursor[node]++]].To;

                    if (!settled[next])
                    {
                        // The graph has no cycles left, so pushing an unsettled target can never come
                        // back round to this node.
                        cursor[next] = 0;
                        pending.Push(next);
                    }

                    continue;
                }

                var layer = 0;
                foreach (var linkIndex in outgoing[node])
                {
                    layer = Math.Max(layer, layers[links[linkIndex].To] + 1);
                }

                layers[node] = layer;
                settled[node] = true;
                pending.Pop();
            }
        }

        return layers;
    }

    /// <summary>
    /// Orders the nodes within each layer by repeated barycentre sorting, so that edges cross as little
    /// as this kind of heuristic manages.
    /// </summary>
    private static List<List<int>> OrderWithinLayers(
        int nodeCount,
        List<(int Ordinal, int From, int To)> links,
        bool[] isBackEdge,
        int[] layers)
    {
        var layerCount = 0;
        foreach (var layer in layers)
        {
            layerCount = Math.Max(layerCount, layer + 1);
        }

        List<List<int>> rows = [];
        for (var i = 0; i < layerCount; i++)
        {
            rows.Add([]);
        }

        for (var node = 0; node < nodeCount; node++)
        {
            rows[layers[node]].Add(node);
        }

        List<int>[] outgoing = Adjacency(nodeCount, links, isBackEdge);
        List<int>[] incoming = ReverseAdjacency(nodeCount, links, isBackEdge);

        var position = new int[nodeCount];
        Reposition(rows, position);

        for (var sweep = 0; sweep < BarycentreSweeps; sweep++)
        {
            var upwards = sweep % 2 == 0;

            for (var i = 0; i < layerCount; i++)
            {
                var layer = upwards ? i : layerCount - 1 - i;

                // Going up the layers a node is ordered by what it points at (everything to its left);
                // coming back down, by what points at it.
                List<int>[] neighbours = upwards ? outgoing : incoming;

                List<int> row = rows[layer];
                var barycentres = new double[row.Count];

                for (var r = 0; r < row.Count; r++)
                {
                    barycentres[r] = Barycentre(row[r], neighbours, links, upwards, position);
                }

                List<int> ordered = [.. row];

                // A stable sort, so a node with no neighbours at all keeps the place the alias order gave
                // it rather than moving about between sweeps.
                var indices = Enumerable.Range(0, ordered.Count).ToArray();
                Array.Sort(indices, (left, right) =>
                {
                    var byKey = barycentres[left].CompareTo(barycentres[right]);
                    return byKey != 0 ? byKey : left.CompareTo(right);
                });

                rows[layer] = [.. indices.Select(x => ordered[x])];
                Reposition(rows, position);
            }
        }

        return rows;
    }

    private static double Barycentre(
        int node,
        List<int>[] neighbours,
        List<(int Ordinal, int From, int To)> links,
        bool useTargets,
        int[] position)
    {
        double total = 0;
        var count = 0;

        foreach (var linkIndex in neighbours[node])
        {
            var other = useTargets ? links[linkIndex].To : links[linkIndex].From;

            if (other == node)
            {
                continue;
            }

            total += position[other];
            count++;
        }

        return count == 0 ? position[node] : total / count;
    }

    private static void Reposition(List<List<int>> rows, int[] position)
    {
        foreach (List<int> row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                position[row[i]] = i;
            }
        }
    }

    private static GraphNodeBox[] Place(List<string> nodes, int[] layers, List<List<int>> rows)
    {
        var tallest = 0;
        foreach (List<int> row in rows)
        {
            tallest = Math.Max(tallest, row.Count);
        }

        var boxes = new GraphNodeBox[nodes.Count];

        for (var layer = 0; layer < rows.Count; layer++)
        {
            List<int> row = rows[layer];

            // Each layer is centred against the tallest one, which keeps the picture balanced. The pitch
            // is constant, so two boxes in one layer are always a whole RowGap apart whatever the offset.
            var offset = (tallest - row.Count) * (NodeHeight + RowGap) / 2;

            for (var r = 0; r < row.Count; r++)
            {
                var node = row[r];

                boxes[node] = new GraphNodeBox(
                    nodes[node],
                    Round(Margin + (layer * (NodeWidth + LayerGap))),
                    Round(Margin + offset + (r * (NodeHeight + RowGap))),
                    NodeWidth,
                    NodeHeight,
                    layers[node],
                    r);
            }
        }

        return boxes;
    }

    /// <summary>
    /// A forward edge: out of the pointing box's left side, into the right side of what it points at.
    /// </summary>
    private static GraphEdgePath ForwardEdge(int ordinal, GraphNodeBox from, GraphNodeBox to)
    {
        var x0 = from.X;
        var y0 = from.CentreY;
        var x1 = to.X + to.Width;
        var y1 = to.CentreY;

        var pull = Math.Max(28, (x0 - x1) / 2);

        return new GraphEdgePath(
            ordinal,
            from.Alias,
            to.Alias,
            Cubic(x0, y0, x0 - pull, y0, x1 + pull, y1, x1, y1),
            Arrow(x1 + pull, y1, x1, y1),
            Round(Midpoint(x0, x0 - pull, x1 + pull, x1)),
            Round(Midpoint(y0, y0, y1, y1)),
            IsBackEdge: false,
            IsSelfLoop: false);
    }

    /// <summary>
    /// A cycle-closing edge, drawn the long way round underneath the diagram so that it cannot be
    /// mistaken for one of the edges the layering respects.
    /// </summary>
    private static GraphEdgePath BackEdge(int ordinal, GraphNodeBox from, GraphNodeBox to, double dropY)
    {
        var x0 = from.CentreX;
        var y0 = from.Y + from.Height;
        var x1 = to.CentreX;
        var y1 = to.Y + to.Height;

        return new GraphEdgePath(
            ordinal,
            from.Alias,
            to.Alias,
            Cubic(x0, y0, x0, dropY, x1, dropY, x1, y1),
            Arrow(x1, dropY, x1, y1),
            Round(Midpoint(x0, x0, x1, x1)),
            Round(Midpoint(y0, dropY, dropY, y1)),
            IsBackEdge: true,
            IsSelfLoop: false);
    }

    /// <summary>A self-reference, drawn as a loop over the top of its own box.</summary>
    private static GraphEdgePath SelfLoop(int ordinal, GraphNodeBox box)
    {
        var x0 = box.X + (box.Width * 0.32);
        var x1 = box.X + (box.Width * 0.68);
        var y = box.Y;
        var top = box.Y - SelfLoopHeight;

        return new GraphEdgePath(
            ordinal,
            box.Alias,
            box.Alias,
            Cubic(x0, y, x0 - 18, top, x1 + 18, top, x1, y),
            Arrow(x1 + 18, top, x1, y),
            Round(box.CentreX),
            Round(Midpoint(y, top, top, y)),
            IsBackEdge: false,
            IsSelfLoop: true);
    }

    /// <summary>
    /// The arrowhead at the end of an edge, as a closed triangle pointing along the curve's final
    /// tangent.
    /// </summary>
    /// <remarks>
    /// <b>Drawn rather than referenced through an SVG <c>&lt;marker&gt;</c>, and that is deliberate.</b>
    /// A marker is reached with <c>marker-end="url(#id)"</c>, and a fragment-only <c>url()</c> is resolved
    /// against the document's <em>base</em> URL — which in this application is never the current URL,
    /// because the studio renders a studio-rooted <c>&lt;base href&gt;</c> for every mount so that its
    /// relative links survive a sub-path mounting (D11). Under a <c>&lt;base&gt;</c> that differs from the
    /// page's own address, browsers have a long history of resolving such references to a document that
    /// does not contain the marker and silently drawing no arrowheads at all. Fifteen lines of geometry
    /// have no such failure mode, and they are testable.
    /// </remarks>
    private static string Arrow(double fromX, double fromY, double tipX, double tipY)
    {
        var dx = tipX - fromX;
        var dy = tipY - fromY;
        var length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length < 0.001)
        {
            return string.Empty;
        }

        dx /= length;
        dy /= length;

        var baseX = tipX - (dx * ArrowLength);
        var baseY = tipY - (dy * ArrowLength);
        var halfX = -dy * ArrowWidth / 2;
        var halfY = dx * ArrowWidth / 2;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"M {Round(tipX)} {Round(tipY)} L {Round(baseX + halfX)} {Round(baseY + halfY)} L {Round(baseX - halfX)} {Round(baseY - halfY)} Z");
    }

    private static string Cubic(
        double x0, double y0,
        double cx0, double cy0,
        double cx1, double cy1,
        double x1, double y1) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"M {Round(x0)} {Round(y0)} C {Round(cx0)} {Round(cy0)}, {Round(cx1)} {Round(cy1)}, {Round(x1)} {Round(y1)}");

    /// <summary>The point at <c>t = 0.5</c> on a cubic Bézier, which is where a label belongs.</summary>
    private static double Midpoint(double p0, double p1, double p2, double p3) =>
        (p0 + (3 * p1) + (3 * p2) + p3) / 8;

    /// <summary>
    /// Two decimal places, which is finer than a pixel at any zoom this diagram is read at and is what
    /// makes the output byte-identical between two runs rather than merely equal to within a rounding
    /// error.
    /// </summary>
    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static List<int>[] Adjacency(
        int nodeCount,
        List<(int Ordinal, int From, int To)> links,
        bool[]? skip = null)
    {
        var adjacency = new List<int>[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            adjacency[i] = [];
        }

        for (var i = 0; i < links.Count; i++)
        {
            if (skip is not null && (skip[i] || links[i].From == links[i].To))
            {
                continue;
            }

            adjacency[links[i].From].Add(i);
        }

        return adjacency;
    }

    private static List<int>[] ReverseAdjacency(
        int nodeCount,
        List<(int Ordinal, int From, int To)> links,
        bool[] skip)
    {
        var adjacency = new List<int>[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            adjacency[i] = [];
        }

        for (var i = 0; i < links.Count; i++)
        {
            if (skip[i] || links[i].From == links[i].To)
            {
                continue;
            }

            adjacency[links[i].To].Add(i);
        }

        return adjacency;
    }
}
