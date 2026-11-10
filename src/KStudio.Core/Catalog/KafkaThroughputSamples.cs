using System.Collections.Concurrent;

namespace KStudio.Core.Catalog;

/// <summary>
/// A topic's recent growth, from the overview reads that happen anyway (decision 5.2): each read records (time, sum of the high
/// watermarks), and the rate is the change over the newest two samples at least ten seconds apart within ten minutes. No read of its own.
/// </summary>
internal sealed class KafkaThroughputSamples(TimeProvider timeProvider)
{
    private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Memory = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, List<(DateTimeOffset At, long Total)>> samples = new(StringComparer.Ordinal);

    /// <summary>Records one overview read.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="totalHighWatermark">The sum of the topic's high watermarks.</param>
    public void Record(string cluster, string topic, long totalHighWatermark)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<(DateTimeOffset At, long Total)> list = samples.GetOrAdd(cluster + "|" + topic, static _ => []);

        lock (list)
        {
            list.RemoveAll(x => now - x.At > Memory);
            list.Add((now, totalHighWatermark));
        }
    }

    /// <summary>
    /// "≈ N offsets/s": the change in offsets over time between the newest sample and the newest one at least ten seconds before it;
    /// <see langword="null" /> when there is no such pair. It is an estimate of offsets, not records.
    /// </summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="topic">The topic.</param>
    public double? Rate(string cluster, string topic)
    {
        if (!samples.TryGetValue(cluster + "|" + topic, out List<(DateTimeOffset At, long Total)>? list))
        {
            return null;
        }

        lock (list)
        {
            if (list.Count < 2)
            {
                return null;
            }

            (DateTimeOffset At, long Total) newest = list[^1];

            for (int i = list.Count - 2; i >= 0; i--)
            {
                TimeSpan gap = newest.At - list[i].At;

                if (gap >= MinimumGap)
                {
                    return (newest.Total - list[i].Total) / gap.TotalSeconds;
                }
            }

            return null;
        }
    }
}
