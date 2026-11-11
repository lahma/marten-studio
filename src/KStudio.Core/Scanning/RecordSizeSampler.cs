using System.Collections.Concurrent;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Wire;
using KStudio.Core.Windows;

namespace KStudio.Core.Scanning;

/// <summary>
/// Samples the mean size of a topic's records for the estimate before a scan (decision 9.10): a short consumer reads at most 50 records and
/// 1 MiB from the starts of at most three of the largest ranges, within 3 seconds. The answer is cached per cluster and topic for ten
/// minutes, one sampling runs per cluster at a time, and the bytes it reads count against the owner's budget. A refused or empty sample is
/// <see langword="null" />: the estimate then says "size unknown", and never invents one.
/// </summary>
internal sealed class RecordSizeSampler(
    KafkaWires wires,
    KafkaEngineSnapshot settings,
    OwnerByteBudget budget,
    KStudioLoggers loggers,
    TimeProvider time)
{
    /// <summary>The most records read.</summary>
    public const int MaxRecords = 50;

    /// <summary>The most bytes read.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The longest a sampling takes.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(3);

    /// <summary>How long an answer is reused.</summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    private const int MaxIdleReads = 30;
    private const int MaxRanges = 3;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, long? Mean)> cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> perCluster = new(StringComparer.Ordinal);

    /// <summary>The mean size of a topic's records in its ranges, or <see langword="null" /> when it cannot be sampled.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="topic">The topic.</param>
    /// <param name="ranges">The topic's ranges of this scan.</param>
    /// <param name="isolation">The isolation the scan reads under.</param>
    /// <param name="owner">The owner the bytes are counted against.</param>
    /// <param name="cancellationToken">Abandons the sampling.</param>
    public async Task<long?> MeanRecordBytesAsync(
        string cluster,
        string topic,
        IReadOnlyList<PartitionRange> ranges,
        WireIsolation isolation,
        KafkaScanOwner owner,
        CancellationToken cancellationToken)
    {
        string key = cluster + "|" + topic;

        if (cache.TryGetValue(key, out (DateTimeOffset At, long? Mean) known) && time.GetUtcNow() - known.At < TimeToLive)
        {
            return known.Mean;
        }

        List<PartitionRange> largest = [.. ranges.Where(x => x.Topic == topic && !x.IsEmpty).OrderByDescending(static x => x.End - x.Start).Take(MaxRanges)];

        if (largest.Count == 0)
        {
            return null;
        }

        SemaphoreSlim gate = perCluster.GetOrAdd(cluster, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Another caller may have sampled while this one waited.
            if (cache.TryGetValue(key, out known) && time.GetUtcNow() - known.At < TimeToLive)
            {
                return known.Mean;
            }

            long? mean = await Task.Factory
                .StartNew(() => Sample(cluster, topic, largest, isolation, owner), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .ConfigureAwait(false);

            cache[key] = (time.GetUtcNow(), mean);
            return mean;
        }
        finally
        {
            gate.Release();
        }
    }

    private long? Sample(string cluster, string topic, List<PartitionRange> ranges, WireIsolation isolation, KafkaScanOwner owner)
    {
        long bytes = 0;
        int records = 0;
        IWireScanReader? reader = null;

        try
        {
            reader = wires.For(cluster).OpenReader(new WireReaderOptions(Guid.NewGuid(), isolation, settings.Limits));
            reader.Assign([.. ranges.Select(static x => new WirePartitionOffset(x.Topic, x.Partition, x.Start))]);

            Dictionary<WirePartition, long> ends = ranges.ToDictionary(static x => x.Key, static x => x.End);
            HashSet<WirePartition> finished = [];
            DateTimeOffset deadline = time.GetUtcNow() + MaxDuration;
            int idle = 0;

            while (records < MaxRecords && bytes < MaxBytes && finished.Count < ranges.Count && idle < MaxIdleReads && time.GetUtcNow() < deadline)
            {
                switch (reader.Read(TimeSpan.FromMilliseconds(100)))
                {
                    case WireReadOutcome.Record { Value: { } record }:
                        idle = 0;

                        if (ends.TryGetValue(new WirePartition(record.Topic, record.Partition), out long end) && record.Offset < end)
                        {
                            records++;
                            bytes += record.Size;
                        }

                        break;

                    case WireReadOutcome.PartitionEnd reached:
                        finished.Add(reached.Partition);
                        break;

                    case WireReadOutcome.Failure { Error: WireError.NotAuthorized { ResourceType: KafkaResourceType.Topic } }:
                        // Refused to read: nothing can be sampled, and saying so is the estimate's job.
                        return null;

                    case WireReadOutcome.Failure:
                        idle++;
                        break;

                    default:
                        idle++;
                        break;
                }
            }

            return records == 0 ? null : (long) Math.Ceiling(bytes / (double) records);
        }
        catch (Exception exception) when (exception is WireException or InvalidOperationException or ObjectDisposedException)
        {
            string topicText = KafkaLogValue.Cap(topic)!;
            string clusterText = KafkaLogValue.Cap(cluster)!;
            string kind = exception.GetType().Name;
            KStudioLog.SampleFailed(loggers.Scans, topicText, clusterText, kind);
            return null;
        }
        finally
        {
            budget.Add(owner.Key, bytes);

            try
            {
                reader?.Close();
            }
            catch (WireException)
            {
                // The reader is gone either way.
            }
        }
    }
}
