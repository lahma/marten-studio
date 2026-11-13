using KStudio.Core.Windows;

namespace KStudio.Core.Index;

/// <summary>
/// Turns a host index's hints into the ranges a plan reads (AGENTS.md D48), and trusts none of them: a hint narrows where the scan reads inside the
/// window the plan already has, and every record in the narrowed range is still read and judged. Pure.
/// </summary>
internal static class IndexNarrower
{
    /// <summary>The most hints one plan uses; the rest are left unused and the plan says so.</summary>
    public const int MaxHints = 64;

    /// <summary>
    /// Narrows <paramref name="plan" /> to what <paramref name="answer" /> names. A hint about a topic the search did not name is dropped before
    /// anything counts or shows it; so is one about a partition the plan does not read and one whose range is empty. Per partition the surviving hints
    /// become one range, from the least first offset to the greatest end, intersected with the plan's own range there. A partition no hint reaches is
    /// read as an empty range. When no hint survives, the plan is returned as it was.
    /// </summary>
    /// <param name="plan">The ranges the plan would read: the window's, after pruning.</param>
    /// <param name="answer">What the index said.</param>
    /// <param name="topics">The topics the search names, which are the only ones a hint may be about.</param>
    public static (IReadOnlyList<PartitionRange> Ranges, KafkaIndexNarrowing Narrowing) Narrow(
        IReadOnlyList<PartitionRange> plan,
        KafkaIndexAnswer answer,
        IReadOnlyCollection<string> topics)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(topics);

        long window = plan.Sum(static x => Math.Max(0, x.End - x.Start));
        HashSet<(string Topic, int Partition)> read = [.. plan.Where(static x => !x.IsEmpty).Select(static x => (x.Topic, x.Partition))];
        HashSet<string> named = new(topics, StringComparer.Ordinal);

        List<KafkaIndexHint> valid = [];

        foreach (KafkaIndexHint hint in answer.Ranges)
        {
            if (hint.Topic is null || !named.Contains(hint.Topic) || hint.FromOffset >= hint.ToOffset || !read.Contains((hint.Topic, hint.Partition)))
            {
                continue;
            }

            valid.Add(hint);
        }

        int notUsed = Math.Max(0, valid.Count - MaxHints);
        Dictionary<(string Topic, int Partition), (long From, long To, int Hints)> hulls = [];

        foreach (KafkaIndexHint hint in valid.Take(MaxHints))
        {
            (string, int) key = (hint.Topic, hint.Partition);
            hulls[key] = hulls.TryGetValue(key, out (long From, long To, int Hints) hull)
                ? (Math.Min(hull.From, hint.FromOffset), Math.Max(hull.To, hint.ToOffset), hull.Hints + 1)
                : (hint.FromOffset, hint.ToOffset, 1);
        }

        List<PartitionRange> narrowed = [];
        List<KafkaIndexRange> info = [];
        int used = 0;

        foreach (PartitionRange range in plan)
        {
            if (hulls.TryGetValue((range.Topic, range.Partition), out (long From, long To, int Hints) hull))
            {
                long from = Math.Max(hull.From, range.Start);
                long to = Math.Min(hull.To, range.End);

                if (from < to)
                {
                    narrowed.Add(range with { Start = from, End = to });
                    info.Add(new KafkaIndexRange(range.Topic, range.Partition, from, to, range.Start, range.End));
                    used += hull.Hints;
                    continue;
                }
            }

            // A partition no hint reaches inside the plan is read as nothing.
            narrowed.Add(range with { End = range.Start });
        }

        return info.Count == 0
            ? (plan, KafkaIndexNarrowing.PointedNowhere(window))
            : (narrowed, KafkaIndexNarrowing.Narrowed(info, used, notUsed, window));
    }
}
