using KStudio.Core.Wire;

namespace KStudio.Tests.Support.FakeKafka;

/// <summary>One call the fake wire received, for the tests that assert what the engine asked.</summary>
/// <param name="Call">Which call.</param>
/// <param name="Partitions">The partitions it carried (list offsets), else empty.</param>
/// <param name="Spec">The spec (list offsets), else <see langword="null" />.</param>
/// <param name="Isolation">The isolation (list offsets), else <see langword="null" />.</param>
/// <param name="Detail">A topic or group name, else empty.</param>
internal sealed record FakeCall(WireCall Call, IReadOnlyList<WirePartition> Partitions, WireOffsetSpec? Spec, WireIsolation? Isolation, string Detail);

/// <summary>An entry of a partition's log: a data record, or a control marker that only occupies an offset.</summary>
internal sealed class FakeEntry
{
    public FakeEntry(long offset, WireRecord? record, long? transaction)
    {
        Offset = offset;
        Record = record;
        Transaction = transaction;
    }

    public long Offset { get; }

    /// <summary>The record; <see langword="null" /> for a control marker.</summary>
    public WireRecord? Record { get; }

    /// <summary>The transaction the record belongs to, when it does.</summary>
    public long? Transaction { get; }

    public bool IsControl => Record is null;
}

/// <summary>One partition: a log with a start, transactions and offsets that survive a delete.</summary>
internal sealed class FakePartition
{
    private readonly List<FakeEntry> entries = [];
    private readonly Dictionary<long, bool?> transactions = [];

    public long LogStart { get; private set; }

    /// <summary>One past the last offset: the high watermark.</summary>
    public long End => LogStart + entries.Count;

    public IReadOnlyList<FakeEntry> Entries => entries;

    /// <summary>The last stable offset: the first offset of the oldest transaction still open, else the end.</summary>
    public long LastStableOffset
    {
        get
        {
            foreach (FakeEntry entry in entries)
            {
                if (entry.Transaction is { } id && transactions.TryGetValue(id, out bool? state) && state is null)
                {
                    return entry.Offset;
                }
            }

            return End;
        }
    }

    public FakeEntry? At(long offset) => offset >= LogStart && offset < End ? entries[(int) (offset - LogStart)] : null;

    public long Append(WireRecord template, long? transaction)
    {
        long offset = End;
        WireRecord stored = new(
            template.Topic, template.Partition, offset, template.Key, template.Value, template.Headers,
            template.TimestampMs, template.TimestampType, template.LeaderEpoch);
        entries.Add(new FakeEntry(offset, stored, transaction));

        if (transaction is { } id && !transactions.ContainsKey(id))
        {
            transactions[id] = null;
        }

        return offset;
    }

    /// <summary>Ends a transaction: its marker occupies an offset, and aborted records stay in the log.</summary>
    public void Finish(long transaction, bool commit)
    {
        transactions[transaction] = commit;
        entries.Add(new FakeEntry(End, null, null));
    }

    /// <summary>Whether a data entry is delivered under read_committed: not aborted and below the last stable offset.</summary>
    public bool IsAborted(FakeEntry entry) =>
        entry.Transaction is { } id && transactions.TryGetValue(id, out bool? state) && state == false;

    public void DeleteBefore(long offset)
    {
        long target = Math.Min(Math.Max(offset, LogStart), End);
        entries.RemoveRange(0, (int) (target - LogStart));
        LogStart = target;
    }
}

internal sealed class FakeTopic
{
    /// <summary>What a real broker reports for the entries the engine reads when a topic sets nothing: the defaults, never an absent entry.</summary>
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["message.timestamp.type"] = "CreateTime",
        ["cleanup.policy"] = "delete",
        ["retention.ms"] = "604800000",
        ["retention.bytes"] = "-1",
        ["remote.storage.enable"] = "false",
        ["max.message.bytes"] = "1048588",
    };

    public FakeTopic(string name, int partitions, IReadOnlyDictionary<string, string> config)
    {
        Name = name;
        Config = new Dictionary<string, string>(Defaults, StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> pair in config)
        {
            Config[pair.Key] = pair.Value;
        }

        Partitions = [.. Enumerable.Range(0, partitions).Select(static _ => new FakePartition())];
    }

    public string Name { get; }

    public Dictionary<string, string> Config { get; }

    public List<FakePartition> Partitions { get; }

    public bool IsLogAppendTime =>
        Config.TryGetValue("message.timestamp.type", out string? type) && string.Equals(type, "LogAppendTime", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An in-memory Kafka (decision 3.5): topics with partitions as logs, transactions with control markers, configs, groups and an ACL
/// table. It implements the Kafka semantics the contract proves on real brokers, and only those. It has no produce, commit, delete or
/// alter method on the <em>wire</em>; the seeding methods here are the test's.
/// </summary>
internal sealed class FakeKafkaCluster(TimeProvider clock)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, FakeTopic> topics = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Group, string Topic, int Partition), long> committed = [];
    private readonly Dictionary<string, (string State, int Members)> groups = new(StringComparer.Ordinal);
    private readonly HashSet<(KafkaAclOperation, KafkaResourceType, string)> denied = [];
    private readonly Dictionary<WireCall, Queue<WireError>> failures = [];
    private readonly List<FakeCall> calls = [];
    private long nextTransaction = 1;

    public Lock Gate => gate;

    public TimeProvider Clock { get; } = clock;

    /// <summary>When set, a reader's first fetch returns a group refusal and then carries on, as a principal with no group ACL does (SPIKE S1).</summary>
    public bool RefuseGroupOnFetch { get; set; }

    public IReadOnlyList<FakeCall> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    public FakeTopic CreateTopic(string name, int partitions, IReadOnlyDictionary<string, string>? config = null)
    {
        lock (gate)
        {
            FakeTopic topic = new(name, partitions, config ?? new Dictionary<string, string>());
            topics[name] = topic;
            return topic;
        }
    }

    public FakeTopic? Topic(string name)
    {
        lock (gate)
        {
            return topics.GetValueOrDefault(name);
        }
    }

    public IReadOnlyList<FakeTopic> AllTopics()
    {
        lock (gate)
        {
            return [.. topics.Values];
        }
    }

    /// <summary>Appends a record; a <c>LogAppendTime</c> topic stamps it with the cluster clock whatever the caller gave.</summary>
    public long Produce(string topic, int partition, byte[]? key, byte[]? value, WireHeader[]? headers, long timestampMs, long? transaction = null)
    {
        lock (gate)
        {
            FakeTopic t = topics[topic];
            bool append = t.IsLogAppendTime;
            long stamp = append ? Clock.GetUtcNow().ToUnixTimeMilliseconds() : timestampMs;

            return t.Partitions[partition].Append(
                new WireRecord(
                    topic, partition, 0, key, value, headers ?? [], stamp,
                    append ? WireTimestampType.LogAppendTime : WireTimestampType.CreateTime, 0),
                transaction);
        }
    }

    public long BeginTransaction()
    {
        lock (gate)
        {
            return nextTransaction++;
        }
    }

    public void EndTransaction(string topic, int partition, long transaction, bool commit)
    {
        lock (gate)
        {
            topics[topic].Partitions[partition].Finish(transaction, commit);
        }
    }

    public long DeleteRecords(string topic, int partition, long before)
    {
        lock (gate)
        {
            FakePartition p = topics[topic].Partitions[partition];
            p.DeleteBefore(before);
            return p.LogStart;
        }
    }

    public void SetGroup(string group, string state, int members)
    {
        lock (gate)
        {
            groups[group] = (state, members);
        }
    }

    public (string State, int Members)? Group(string group)
    {
        lock (gate)
        {
            return groups.TryGetValue(group, out (string State, int Members) value) ? value : null;
        }
    }

    public void Commit(string group, string topic, int partition, long offset)
    {
        lock (gate)
        {
            committed[(group, topic, partition)] = offset;
        }
    }

    public long? Committed(string group, string topic, int partition)
    {
        lock (gate)
        {
            return committed.TryGetValue((group, topic, partition), out long value) ? value : null;
        }
    }

    /// <summary>Makes the matching calls fail with the mapped refusal.</summary>
    public void Deny(KafkaAclOperation operation, KafkaResourceType resourceType, string resourceName)
    {
        lock (gate)
        {
            denied.Add((operation, resourceType, resourceName));
        }
    }

    public bool IsDenied(KafkaAclOperation operation, KafkaResourceType resourceType, string resourceName)
    {
        lock (gate)
        {
            return denied.Contains((operation, resourceType, resourceName));
        }
    }

    /// <summary>Makes the next call of this kind fail with <paramref name="error" />.</summary>
    public void FailNext(WireCall call, WireError error)
    {
        lock (gate)
        {
            if (!failures.TryGetValue(call, out Queue<WireError>? queue))
            {
                failures[call] = queue = new Queue<WireError>();
            }

            queue.Enqueue(error);
        }
    }

    public WireError? TakeFailure(WireCall call)
    {
        lock (gate)
        {
            return failures.TryGetValue(call, out Queue<WireError>? queue) && queue.Count > 0 ? queue.Dequeue() : null;
        }
    }

    public void Record(FakeCall call)
    {
        lock (gate)
        {
            calls.Add(call);
        }
    }
}
