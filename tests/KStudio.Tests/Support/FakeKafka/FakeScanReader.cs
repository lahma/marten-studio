using KStudio.Core.Wire;

namespace KStudio.Tests.Support.FakeKafka;

/// <summary>
/// A scan reader over the fake cluster (decision 3.5): explicit-offset assign, an end at the high watermark or the last stable offset,
/// aborted and control records that occupy offsets and are never delivered under read_committed, a position below the log start reported
/// once and then silence until <see cref="Reposition" />. It records how it was used, so a test can assert that every call came from one
/// thread, every assign carried explicit offsets and <see cref="Close" /> was called once.
/// </summary>
internal sealed class FakeScanReader(FakeKafkaCluster cluster, WireReaderOptions options, string groupId) : IWireScanReader
{
    private readonly Dictionary<WirePartition, long> positions = [];
    private readonly HashSet<WirePartition> paused = [];
    private readonly HashSet<WirePartition> stalled = [];
    private readonly Dictionary<WirePartition, long> endReported = [];
    private readonly HashSet<WirePartition> refused = [];
    private readonly List<int> threads = [];
    private bool groupRefusalSent;
    private int cursor;

    public WireReaderOptions Options { get; } = options;

    public string GroupId { get; } = groupId;

    /// <summary>Every assign this reader received, in order.</summary>
    public List<IReadOnlyList<WirePartitionOffset>> Assigns { get; } = [];

    public int CloseCount { get; private set; }

    public int DisposeCount { get; private set; }

    /// <summary>The managed thread ids of every call, in order.</summary>
    public IReadOnlyList<int> ThreadIds => threads;

    public void Assign(IReadOnlyList<WirePartitionOffset> start)
    {
        Touch();

        foreach (WirePartitionOffset position in start)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(position.Offset, nameof(start));
        }

        Assigns.Add([.. start]);
        positions.Clear();
        stalled.Clear();

        foreach (WirePartitionOffset position in start)
        {
            positions[position.Key] = position.Offset;
        }
    }

    public void Pause(IReadOnlyList<WirePartition> partitions)
    {
        Touch();
        paused.UnionWith(partitions);
    }

    public void Resume(IReadOnlyList<WirePartition> partitions)
    {
        Touch();
        paused.ExceptWith(partitions);
    }

    public void Reposition(WirePartitionOffset position)
    {
        Touch();
        ArgumentOutOfRangeException.ThrowIfNegative(position.Offset, nameof(position));
        positions[position.Key] = position.Offset;
        stalled.Remove(position.Key);
        endReported.Remove(position.Key);
    }

    public WireReadOutcome Read(TimeSpan timeout)
    {
        Touch();

        if (Cluster.RefuseGroupOnFetch && !groupRefusalSent)
        {
            groupRefusalSent = true;
            return new WireReadOutcome.Failure(
                new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Group, GroupId), null);
        }

        List<WirePartition> keys = [.. positions.Keys.OrderBy(static x => x.Topic, StringComparer.Ordinal).ThenBy(static x => x.Partition)];

        for (int attempt = 0; attempt < keys.Count; attempt++)
        {
            WirePartition key = keys[(cursor + attempt) % keys.Count];

            if (paused.Contains(key) || stalled.Contains(key))
            {
                continue;
            }

            WireReadOutcome? outcome = ReadFrom(key);

            if (outcome is not null)
            {
                cursor = (cursor + attempt + 1) % keys.Count;
                return outcome;
            }
        }

        return WireReadOutcome.Idle;
    }

    public void Close()
    {
        Touch();
        CloseCount++;
        Dispose();
    }

    public void Dispose() => DisposeCount++;

    private FakeKafkaCluster Cluster { get; } = cluster;

    private void Touch()
    {
        lock (threads)
        {
            threads.Add(Environment.CurrentManagedThreadId);
        }
    }

    private WireReadOutcome? ReadFrom(WirePartition key)
    {
        FakeTopic? topic = Cluster.Topic(key.Topic);

        // A consumer assigned a topic that does not exist reports nothing at all (SPIKE S1.6).
        if (topic is null || key.Partition >= topic.Partitions.Count)
        {
            return null;
        }

        if (Cluster.IsDenied(KafkaAclOperation.Read, KafkaResourceType.Topic, key.Topic))
        {
            return refused.Add(key)
                ? new WireReadOutcome.Failure(new WireError.NotAuthorized(KafkaAclOperation.Read, KafkaResourceType.Topic, key.Topic), key)
                : null;
        }

        lock (Cluster.Gate)
        {
            FakePartition log = topic.Partitions[key.Partition];
            long position = positions[key];

            if (position < log.LogStart)
            {
                stalled.Add(key);
                return new WireReadOutcome.Failure(new WireError.OffsetOutOfRange(key, position), key);
            }

            long limit = Options.Isolation == WireIsolation.ReadCommitted ? log.LastStableOffset : log.End;

            while (position < limit)
            {
                FakeEntry entry = log.At(position)!;
                position++;
                positions[key] = position;
                endReported.Remove(key);

                bool delivered = entry.Record is not null && (Options.Isolation == WireIsolation.ReadUncommitted || !log.IsAborted(entry));

                if (delivered)
                {
                    return new WireReadOutcome.Record(entry.Record!);
                }
            }

            if (endReported.TryGetValue(key, out long reported) && reported == limit)
            {
                return null;
            }

            endReported[key] = limit;
            return new WireReadOutcome.PartitionEnd(key, limit);
        }
    }
}
