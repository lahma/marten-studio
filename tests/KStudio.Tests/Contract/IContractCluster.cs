using KStudio.Core.Wire;

namespace KStudio.Tests.Contract;

/// <summary>A record a contract test seeds. A <c>null</c> value is a tombstone and a <c>null</c> key a null key.</summary>
/// <param name="Key">The key.</param>
/// <param name="Value">The value.</param>
/// <param name="TimestampMs">The producer's timestamp in Unix milliseconds (ignored by a <c>LogAppendTime</c> topic). Always in the past (F7).</param>
/// <param name="Headers">The headers.</param>
internal sealed record ContractRecord(string? Key, string? Value, long TimestampMs, IReadOnlyList<KeyValuePair<string, string>>? Headers = null);

/// <summary>A transaction a test left open; dispose aborts it if the test did not end it.</summary>
internal interface IContractTransaction : IAsyncDisposable
{
    Task CommitAsync();

    Task AbortAsync();
}

/// <summary>What <see cref="IContractCluster.ProduceAppendTimeAsync" /> produced.</summary>
/// <param name="FirstOffset">The offset of the first record whose broker timestamp is strictly after the asked-for time.</param>
/// <param name="EndOffset">One past the last record produced.</param>
/// <param name="MaxTimestampMs">The highest broker timestamp of the batch.</param>
internal sealed record AppendBatch(long FirstOffset, long EndOffset, long MaxTimestampMs);

/// <summary>
/// A cluster the contract suites run on: the fake in the fast suite, a real broker in the live suite. It seeds, and exposes the engine
/// over the cluster; the contract proves the fake behaves as Kafka does, so a fast test may rely on a behaviour only if the contract
/// proves it on both images.
/// </summary>
internal interface IContractCluster : IAsyncDisposable
{
    /// <summary>The engine over this cluster.</summary>
    EngineHarness Engine { get; }

    /// <summary>The wire of the cluster.</summary>
    IKafkaWire Wire { get; }

    /// <summary>A topic name no other test uses.</summary>
    string NewTopic(string label);

    Task CreateTopicAsync(string topic, int partitions, IReadOnlyDictionary<string, string>? config = null);

    /// <summary>Appends records to one partition, in order.</summary>
    Task ProduceAsync(string topic, int partition, IReadOnlyList<ContractRecord> records);

    /// <summary>Appends records inside a transaction and leaves it open.</summary>
    Task<IContractTransaction> BeginTransactionAsync(string topic, int partition, IReadOnlyList<ContractRecord> records);

    /// <summary>Deletes every record before <paramref name="before" />.</summary>
    Task DeleteRecordsAsync(string topic, int partition, long before);

    /// <summary>
    /// On a <c>LogAppendTime</c> topic: produces <paramref name="count" /> records whose broker timestamps are all strictly after
    /// <paramref name="afterMs" />, waiting on a condition (the broker's clock reading later) rather than assuming the test's clock
    /// matches the broker's. Records produced while waiting precede <see cref="AppendBatch.FirstOffset" />.
    /// </summary>
    Task<AppendBatch> ProduceAppendTimeAsync(string topic, int partition, int count, long afterMs);
}
