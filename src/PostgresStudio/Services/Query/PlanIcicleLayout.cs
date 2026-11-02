using System.Globalization;

namespace PostgresStudio.Services.Query;

/// <summary>One frame of the icicle view: a node, or the nodes too narrow to draw merged into one.</summary>
/// <param name="Id">The node's id, or a negative id for a merged frame.</param>
/// <param name="ParentId">The parent frame's id, or <see langword="null" /> for the root.</param>
/// <param name="Depth">How many levels below the root.</param>
/// <param name="X">Where it starts, as a share of the root's width (0 to 1).</param>
/// <param name="Width">Its width, as a share of the root's (0 to 1): its inclusive value.</param>
/// <param name="Inclusive">Its own value and every value below it.</param>
/// <param name="Own">Its own value: the self time or self cost the speedscope export weighs it by.</param>
/// <param name="Label">What it is called: the node as the tree describes it (its label and target), or "N small nodes".</param>
/// <param name="Merged">How many nodes a merged frame stands for, those below a narrow one included; zero for a node's own frame.</param>
internal sealed record IcicleFrame(int Id, int? ParentId, int Depth, double X, double Width, double Inclusive, double Own, string Label, int Merged);

/// <summary>
/// 1.0-P3B (issue #28): the icicle view's layout - root on top, each node a frame whose width is its inclusive value and whose own
/// part is its self value, from exactly the self time (under <c>ANALYZE</c> with timing) or self cost the tree's bars and the
/// speedscope export use (<see cref="PlanNode.ExclusiveMs" />, <see cref="PlanNode.ExclusiveCost" />). Pure.
/// </summary>
internal static class PlanIcicleLayout
{
    /// <summary>A frame narrower than this share of the root's width is merged with its narrow siblings: at 1,000 px, two pixels.</summary>
    public const double MinWidth = 0.002;

    /// <summary>The frames of <paramref name="plan" />, parents before children, or none when the plan has no time or cost to draw.</summary>
    public static IReadOnlyList<IcicleFrame> Of(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Dictionary<int, double> inclusive = [];
        double total = Inclusive(plan.Root, plan.Timed, inclusive);

        if (total <= 0 || !double.IsFinite(total))
        {
            return [];
        }

        List<IcicleFrame> frames = [];
        int merged = 0;
        Place(plan.Root, null, 0, 0, total, plan.Timed, inclusive, frames, ref merged);
        return frames;
    }

    /// <summary>What the view weighs: time when the plan was timed, cost otherwise.</summary>
    public static string Measure(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Timed ? "time" : "estimated cost";
    }

    /// <summary>A frame's value in words: milliseconds for a timed plan, cost units otherwise.</summary>
    public static string Value(double value, bool timed) =>
        timed ? value.ToString(value >= 100 ? "N0" : "0.###", CultureInfo.InvariantCulture) + " ms" : value.ToString("N2", CultureInfo.InvariantCulture);

    // The one place a node's own value is read: the self time or self cost the tree's bars and the node table show, clamped as the
    // speedscope export clamps it (PlanSpeedscope.Clamp) - never a second computation of either.
    private static double Own(PlanNode node, bool timed) => PlanSpeedscope.Clamp(timed ? node.ExclusiveMs : node.ExclusiveCost);

    private static int Size(PlanNode node)
    {
        int count = 1;

        foreach (PlanNode child in node.Children)
        {
            count += Size(child);
        }

        return count;
    }

    private static double Inclusive(PlanNode node, bool timed, Dictionary<int, double> inclusive)
    {
        double sum = Own(node, timed);

        foreach (PlanNode child in node.Children)
        {
            sum += Inclusive(child, timed, inclusive);
        }

        inclusive[node.Id] = sum;
        return sum;
    }

    private static void Place(
        PlanNode node,
        int? parent,
        int depth,
        double x,
        double total,
        bool timed,
        Dictionary<int, double> inclusive,
        List<IcicleFrame> frames,
        ref int merged)
    {
        double value = inclusive[node.Id];
        frames.Add(new IcicleFrame(node.Id, parent, depth, x, value / total, value, Own(node, timed), node.Description, 0));

        double at = x;
        double smallWidth = 0;
        double smallValue = 0;
        int smallCount = 0;

        foreach (PlanNode child in node.Children)
        {
            double width = inclusive[child.Id] / total;

            if (width < MinWidth)
            {
                smallWidth += width;
                smallValue += inclusive[child.Id];
                smallCount += Size(child);
                continue;
            }

            Place(child, node.Id, depth + 1, at, total, timed, inclusive, frames, ref merged);
            at += width;
        }

        if (smallCount > 0 && smallWidth > 0)
        {
            merged++;
            frames.Add(new IcicleFrame(
                -merged,
                node.Id,
                depth + 1,
                at,
                smallWidth,
                smallValue,
                smallValue,
                smallCount.ToString(CultureInfo.InvariantCulture) + (smallCount == 1 ? " small node" : " small nodes"),
                smallCount));
        }
    }
}
