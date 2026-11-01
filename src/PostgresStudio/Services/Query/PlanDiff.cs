using System.Globalization;

namespace PostgresStudio.Services.Query;

/// <summary>What became of one node when two plans were compared (0.10-B, issue #28).</summary>
internal enum PlanDiffKind
{
    /// <summary>The node is in both plans and says the same: the same kind, target, cost, time and row estimate.</summary>
    Unchanged,

    /// <summary>The node is in both plans and something about it moved.</summary>
    Changed,

    /// <summary>The node is only in the After plan.</summary>
    Added,

    /// <summary>The node is only in the Before plan.</summary>
    Removed,
}

/// <summary>One line of a plan comparison: a node of the After plan, the node of the Before plan it lines up with, or a node on one side only.</summary>
internal sealed record PlanDiffEntry
{
    /// <summary>What became of the node.</summary>
    public required PlanDiffKind Kind { get; init; }

    /// <summary>
    /// Where the node is: the child indexes from the root, <c>0.1.0</c> - in the After plan, or in the Before plan for a
    /// <see cref="PlanDiffKind.Removed" /> node.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>The Before plan's node, or <see langword="null" /> for an added one.</summary>
    public PlanNode? Before { get; init; }

    /// <summary>The After plan's node, or <see langword="null" /> for a removed one.</summary>
    public PlanNode? After { get; init; }

    /// <summary>Whether the node's kind changed: <c>Seq Scan</c> to <c>Index Scan</c>, <c>Hash Join</c> to <c>Merge Join</c>.</summary>
    public bool TypeChanged => Before is not null && After is not null && !string.Equals(Before.Label, After.Label, StringComparison.Ordinal);

    /// <summary>Whether the relation, function or CTE the node reads changed.</summary>
    public bool TargetChanged => Before is not null && After is not null && !string.Equals(PlanDiff.TargetOf(Before), PlanDiff.TargetOf(After), StringComparison.Ordinal);

    /// <summary>The change of the node's total cost, After less Before, when both plans printed one.</summary>
    public double? CostDelta => Before?.TotalCost is { } before && After?.TotalCost is { } after ? after - before : null;

    /// <summary>The change of the node's inclusive time in milliseconds, After less Before, when both plans were analyzed with timing.</summary>
    public double? TimeDelta => Before?.InclusiveMs is { } before && After?.InclusiveMs is { } after ? after - before : null;

    /// <summary>Whether the planner's row estimate moved.</summary>
    public bool RowsChanged => Before?.PlanRows is { } before && After?.PlanRows is { } after && Math.Abs(after - before) >= 0.5;

    /// <summary>The node in words: the After plan's, or the Before plan's for a removed one.</summary>
    public string Description => (After ?? Before)?.Description ?? string.Empty;
}

/// <summary>Two plans compared (0.10-B): every node's line, and the totals.</summary>
internal sealed record PlanComparison
{
    /// <summary>Every node of the After plan in the order it is drawn, each with its Before node or none, then the nodes only the Before plan has.</summary>
    public required IReadOnlyList<PlanDiffEntry> Entries { get; init; }

    /// <summary>The Before plan's estimated total cost.</summary>
    public double? CostBefore { get; init; }

    /// <summary>The After plan's estimated total cost.</summary>
    public double? CostAfter { get; init; }

    /// <summary>The Before plan's execution time in milliseconds, when it was analyzed.</summary>
    public double? ExecutionMsBefore { get; init; }

    /// <summary>The After plan's execution time in milliseconds, when it was analyzed.</summary>
    public double? ExecutionMsAfter { get; init; }

    /// <summary>Whether either plan was read in part, or the comparison itself stopped at <see cref="PlanDiff.MaxNodes" />: it says so.</summary>
    public bool Partial { get; init; }

    /// <summary>The entries of one kind.</summary>
    public int Count(PlanDiffKind kind) => Entries.Count(x => x.Kind == kind);

    /// <summary>The entries that are not <see cref="PlanDiffKind.Unchanged" />.</summary>
    public IEnumerable<PlanDiffEntry> Differences => Entries.Where(static x => x.Kind != PlanDiffKind.Unchanged);

    /// <summary>The plan's total cost change, After over Before, or <see langword="null" /> without both costs.</summary>
    public double? CostRatio => CostBefore is { } before && CostAfter is { } after && before > 0 ? after / before : null;

    /// <summary>The execution time change, After over Before, or <see langword="null" /> unless both were analyzed.</summary>
    public double? TimeRatio => ExecutionMsBefore is { } before && ExecutionMsAfter is { } after && before > 0 ? after / before : null;
}

/// <summary>
/// 0.10-B (issue #28): two plans lined up node by node - a pure function, which never throws and reads at most
/// <see cref="MaxNodes" /> nodes of either plan, as the reader does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Alignment, in two passes.</b> First by node path: the roots pair, and so do two children at the same child index whenever
/// they are alike - the same node kind, the same relation, function or CTE, or both a join (a <c>Nested Loop</c> that became a
/// <c>Hash Join</c> is one node whose kind changed, not one removed and one added). Then the nodes still without a partner on each
/// side, in the order they are drawn, are lined up by a longest common subsequence over their (node type, target) pairs, so a
/// subtree that moved or was wrapped in a new node still finds its own scans, and what the sequence could not keep in order -
/// siblings that swapped places - is paired with its equal; and what still reads the same relation as a node on the other side is
/// paired with it, its kind changed. What is left on one side is added or removed. The roots pair only when they are alike.
/// </para>
/// <para>
/// <b>What is compared.</b> A pair is <see cref="PlanDiffKind.Changed" /> when its kind or its target differs, its total cost
/// moved by at least a cent, its inclusive time moved by at least a microsecond (when both plans were analyzed with timing), or the
/// planner's row estimate moved by half a row or more. Nothing here reads a database: a pasted plan compares as well as a run one.
/// </para>
/// </remarks>
internal static class PlanDiff
{
    /// <summary>The most nodes read from either plan, the reader's bound (<see cref="QueryPlanReader.MaxNodes" />).</summary>
    public const int MaxNodes = QueryPlanReader.MaxNodes;

    private const double CostEpsilon = 0.005;
    private const double TimeEpsilon = 0.001;

    /// <summary>Compares <paramref name="before" /> with <paramref name="after" />.</summary>
    /// <param name="before">The plan as it was.</param>
    /// <param name="after">The plan as it is.</param>
    public static PlanComparison Compare(QueryPlan before, QueryPlan after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        List<PlanNode> beforeNodes = [.. before.Nodes.Take(MaxNodes)];
        List<PlanNode> afterNodes = [.. after.Nodes.Take(MaxNodes)];
        bool partial = before.Truncated || after.Truncated || before.NodeCount > MaxNodes || after.NodeCount > MaxNodes;

        Dictionary<PlanNode, string> beforePaths = Paths(before.Root, beforeNodes);
        Dictionary<PlanNode, string> afterPaths = Paths(after.Root, afterNodes);
        HashSet<PlanNode> beforeSet = [.. beforeNodes];
        HashSet<PlanNode> afterSet = [.. afterNodes];

        // Pass one: by path. The roots pair only when they are alike: a Seq Scan at the top of one plan and a Gather at the top of the
        // other are not one node that changed, and the scan finds its partner below the Gather in the passes that follow.
        Dictionary<PlanNode, PlanNode> partner = new(ReferenceEqualityComparer.Instance);
        Dictionary<PlanNode, PlanNode> reverse = new(ReferenceEqualityComparer.Instance);
        Stack<(PlanNode Before, PlanNode After)> pending = new();

        if (Alike(before.Root, after.Root))
        {
            pending.Push((before.Root, after.Root));
        }

        while (pending.Count > 0)
        {
            (PlanNode a, PlanNode b) = pending.Pop();

            if (!beforeSet.Contains(a) || !afterSet.Contains(b))
            {
                continue;
            }

            partner[b] = a;
            reverse[a] = b;

            int common = Math.Min(a.Children.Count, b.Children.Count);

            for (int i = common - 1; i >= 0; i--)
            {
                if (Alike(a.Children[i], b.Children[i]))
                {
                    pending.Push((a.Children[i], b.Children[i]));
                }
            }
        }

        // Pass two: what has no partner, lined up by a longest common subsequence of (type, target).
        List<PlanNode> loneBefore = [.. beforeNodes.Where(x => !reverse.ContainsKey(x))];
        List<PlanNode> loneAfter = [.. afterNodes.Where(x => !partner.ContainsKey(x))];

        // Each lone node's key is made once; passes two, three and four read it back.
        Dictionary<PlanNode, string> keys = new(ReferenceEqualityComparer.Instance);

        foreach (PlanNode node in loneBefore.Concat(loneAfter))
        {
            keys[node] = Key(node);
        }

        foreach ((int i, int j) in LongestCommonSubsequence(loneBefore, loneAfter, keys))
        {
            partner[loneAfter[j]] = loneBefore[i];
            reverse[loneBefore[i]] = loneAfter[j];
        }

        // Pass three: what the sequence could not keep in order - siblings that swapped places - is paired with its equal, in the order
        // met. The After nodes still lone wait in a queue for each key, so a node looks its twin up instead of searching for it.
        PairBy(loneBefore, loneAfter, partner, reverse, x => keys[x]);

        // Pass four: a node that reads the same relation, function or CTE as one still lone on the other side is the same read, planned
        // another way - a Seq Scan on t and an Index Scan on t under a Gather.
        PairBy(loneBefore, loneAfter, partner, reverse, static x => TargetOf(x));

        List<PlanDiffEntry> entries = [];

        foreach (PlanNode node in afterNodes)
        {
            if (partner.TryGetValue(node, out PlanNode? was))
            {
                entries.Add(Pair(was, node, afterPaths[node]));
            }
            else
            {
                entries.Add(new PlanDiffEntry { Kind = PlanDiffKind.Added, Path = afterPaths[node], After = node });
            }
        }

        foreach (PlanNode node in beforeNodes)
        {
            if (!reverse.ContainsKey(node))
            {
                entries.Add(new PlanDiffEntry { Kind = PlanDiffKind.Removed, Path = beforePaths[node], Before = node });
            }
        }

        return new PlanComparison
        {
            Entries = entries,
            CostBefore = before.Root.TotalCost,
            CostAfter = after.Root.TotalCost,
            ExecutionMsBefore = before.ExecutionMs,
            ExecutionMsAfter = after.ExecutionMs,
            Partial = partial,
        };
    }

    /// <summary>What a node reads - its relation, function or CTE - or <see langword="null" />: a join or a sort reads nothing of its own.</summary>
    internal static string? TargetOf(PlanNode node) =>
        node.RelationName is { } relation ? (node.Schema is { } schema ? schema + "." + relation : relation)
        : node.FunctionName ?? node.CteName;

    private static PlanDiffEntry Pair(PlanNode before, PlanNode after, string path)
    {
        PlanDiffEntry entry = new() { Kind = PlanDiffKind.Unchanged, Path = path, Before = before, After = after };

        bool moved = entry.TypeChanged
            || entry.TargetChanged
            || entry.RowsChanged
            || (entry.CostDelta is { } cost && Math.Abs(cost) >= CostEpsilon)
            || (entry.TimeDelta is { } time && Math.Abs(time) >= TimeEpsilon);

        return moved ? entry with { Kind = PlanDiffKind.Changed } : entry;
    }

    /// <summary>
    /// Whether two nodes at the same path are one node that changed. A node that reads something is the same node when it reads the
    /// same relation, function or CTE - however its kind changed, a <c>Seq Scan</c> to an <c>Index Scan</c> - and a different node when
    /// it reads another: two scans that swapped places are found by the second pass, not paired as one that was renamed. A node that
    /// reads nothing (a sort, an aggregate, a join) is the same node when it is the same kind, and any join is a join.
    /// </summary>
    private static bool Alike(PlanNode before, PlanNode after)
    {
        string? target = TargetOf(before);
        string? other = TargetOf(after);

        if (target is not null || other is not null)
        {
            return string.Equals(target, other, StringComparison.Ordinal);
        }

        return string.Equals(before.NodeType, after.NodeType, StringComparison.Ordinal) || (IsJoin(before) && IsJoin(after));
    }

    private static bool IsJoin(PlanNode node) => node.NodeType is "Nested Loop" or "Hash Join" or "Merge Join";

    private static string Key(PlanNode node) => node.NodeType + "\u001f" + TargetOf(node);

    /// <summary>
    /// Pairs each lone Before node with the first lone After node, in the order drawn, that has the same <paramref name="by" /> value -
    /// <see langword="null" /> pairs with nothing - and neither has a partner yet.
    /// </summary>
    private static void PairBy(
        List<PlanNode> loneBefore,
        List<PlanNode> loneAfter,
        Dictionary<PlanNode, PlanNode> partner,
        Dictionary<PlanNode, PlanNode> reverse,
        Func<PlanNode, string?> by)
    {
        Dictionary<string, Queue<PlanNode>> waiting = new(StringComparer.Ordinal);

        foreach (PlanNode node in loneAfter.Where(x => !partner.ContainsKey(x)))
        {
            if (by(node) is { } value)
            {
                if (!waiting.TryGetValue(value, out Queue<PlanNode>? queue))
                {
                    waiting[value] = queue = new Queue<PlanNode>();
                }

                queue.Enqueue(node);
            }
        }

        foreach (PlanNode node in loneBefore.Where(x => !reverse.ContainsKey(x)))
        {
            if (by(node) is { } value && waiting.TryGetValue(value, out Queue<PlanNode>? queue) && queue.TryDequeue(out PlanNode? twin))
            {
                partner[twin] = node;
                reverse[node] = twin;
            }
        }
    }

    /// <summary>The child-index path of every node in <paramref name="nodes" />, as <c>0.1.0</c>.</summary>
    private static Dictionary<PlanNode, string> Paths(PlanNode root, List<PlanNode> nodes)
    {
        Dictionary<PlanNode, string> paths = new(ReferenceEqualityComparer.Instance) { [root] = "0" };
        HashSet<PlanNode> known = [.. nodes];

        foreach (PlanNode node in nodes)
        {
            string path = paths.GetValueOrDefault(node, "0");

            for (int i = 0; i < node.Children.Count; i++)
            {
                if (known.Contains(node.Children[i]))
                {
                    paths[node.Children[i]] = path + "." + i.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        return paths;
    }

    /// <summary>The pairs of indexes (into <paramref name="a" />, into <paramref name="b" />) a longest common subsequence of node keys lines up.</summary>
    private static List<(int A, int B)> LongestCommonSubsequence(List<PlanNode> a, List<PlanNode> b, Dictionary<PlanNode, string> keys)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return [];
        }

        string[] left = [.. a.Select(x => keys[x])];
        string[] right = [.. b.Select(x => keys[x])];
        int n = left.Length;
        int m = right.Length;

        // MaxNodes on a side is 2,000, so the table is at most four million cells of two bytes.
        ushort[,] table = new ushort[n + 1, m + 1];

        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                table[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? (ushort) (table[i + 1, j + 1] + 1)
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        List<(int A, int B)> pairs = [];
        int x = 0;
        int y = 0;

        while (x < n && y < m)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                pairs.Add((x, y));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return pairs;
    }
}
