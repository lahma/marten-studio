using System.Text;

using KStudio.Core.Wire;
using KStudio.Tests.Support.FakeKafka;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Contract;

/// <summary>The contract cluster over the in-memory fake: seeding writes to the fake, and a <c>LogAppendTime</c> stamp is the manual clock.</summary>
internal sealed class FakeContractCluster : IContractCluster
{
    private static int topics;

    private readonly ContractPrincipal? principal;

    public FakeContractCluster(ContractPrincipal? principal = null)
    {
        this.principal = principal;
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero));
        Cluster = new FakeKafkaCluster(Clock) { RefuseGroupOnFetch = principal == ContractPrincipal.NoGroup };
        FakeWire = new FakeKafkaWire(Cluster, EngineHarness.ClusterKey);
        Engine = EngineHarness.ForWire(FakeWire, Clock);
    }

    public ManualTimeProvider Clock { get; }

    public FakeKafkaCluster Cluster { get; }

    public FakeKafkaWire FakeWire { get; }

    public EngineHarness Engine { get; }

    public IKafkaWire Wire => FakeWire;

    public string NewTopic(string label) => "ks." + label + "." + Interlocked.Increment(ref topics);

    public Task CreateTopicAsync(string topic, int partitions, IReadOnlyDictionary<string, string>? config = null)
    {
        Cluster.CreateTopic(topic, partitions, config);

        // The broker's describeonly principal has DESCRIBE on the topics and no READ.
        if (principal == ContractPrincipal.DescribeOnly)
        {
            Cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, topic);
        }

        return Task.CompletedTask;
    }

    public Task<bool> TopicExistsAsync(string topic) => Task.FromResult(Cluster.Topic(topic) is not null);

    public Task ProduceAsync(string topic, int partition, IReadOnlyList<ContractRecord> records)
    {
        foreach (ContractRecord record in records)
        {
            Append(topic, partition, record, null);
        }

        return Task.CompletedTask;
    }

    public Task<IContractTransaction> BeginTransactionAsync(string topic, int partition, IReadOnlyList<ContractRecord> records)
    {
        long id = Cluster.BeginTransaction();

        foreach (ContractRecord record in records)
        {
            Append(topic, partition, record, id);
        }

        return Task.FromResult<IContractTransaction>(new FakeTransaction(Cluster, topic, partition, id));
    }

    public Task DeleteRecordsAsync(string topic, int partition, long before)
    {
        Cluster.DeleteRecords(topic, partition, before);
        return Task.CompletedTask;
    }

    public Task<AppendBatch> ProduceAppendTimeAsync(string topic, int partition, int count, long afterMs)
    {
        long now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
        Clock.Advance(TimeSpan.FromMilliseconds(Math.Max(1_000, afterMs - now + 1_000)));

        long first = Cluster.Topic(topic)!.Partitions[partition].End;
        long max = 0;

        for (int i = 0; i < count; i++)
        {
            max = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            Cluster.Produce(topic, partition, Encoding.UTF8.GetBytes("k" + i), Encoding.UTF8.GetBytes("v" + i), null, 0);
            Clock.Advance(TimeSpan.FromSeconds(1));
        }

        return Task.FromResult(new AppendBatch(first, Cluster.Topic(topic)!.Partitions[partition].End, max));
    }

    public async ValueTask DisposeAsync() => await Engine.DisposeAsync();

    private void Append(string topic, int partition, ContractRecord record, long? transaction) =>
        Cluster.Produce(
            topic,
            partition,
            record.Key is null ? null : Encoding.UTF8.GetBytes(record.Key),
            record.Value is null ? null : Encoding.UTF8.GetBytes(record.Value),
            record.Headers is null ? null : [.. record.Headers.Select(static x => new WireHeader(x.Key, Encoding.UTF8.GetBytes(x.Value)))],
            record.TimestampMs,
            transaction);

    private sealed class FakeTransaction(FakeKafkaCluster cluster, string topic, int partition, long id) : IContractTransaction
    {
        private bool ended;

        public Task CommitAsync() => End(commit: true);

        public Task AbortAsync() => End(commit: false);

        public ValueTask DisposeAsync()
        {
            if (!ended)
            {
                End(commit: false);
            }

            return ValueTask.CompletedTask;
        }

        private Task End(bool commit)
        {
            if (!ended)
            {
                ended = true;
                cluster.EndTransaction(topic, partition, id, commit);
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary><see cref="WireContract" /> on the fake.</summary>
public sealed class FakeWireContractTests : WireContract
{
    internal override Task<IContractCluster> OpenAsync() => Task.FromResult<IContractCluster>(new FakeContractCluster());
}

/// <summary><see cref="WindowContract" /> on the fake.</summary>
public sealed class FakeWindowContractTests : WindowContract
{
    internal override Task<IContractCluster> OpenAsync() => Task.FromResult<IContractCluster>(new FakeContractCluster());
}

/// <summary><see cref="ReaderAclContract" /> on the fake.</summary>
public sealed class FakeReaderAclContractTests : ReaderAclContract
{
    internal override Task<IContractCluster> OpenAsync(ContractPrincipal principal) =>
        Task.FromResult<IContractCluster>(new FakeContractCluster(principal));
}
