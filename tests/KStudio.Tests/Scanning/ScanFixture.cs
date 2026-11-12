using System.Text;

using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;
using KStudio.Tests.Support;
using KStudio.Tests.Support.FakeKafka;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The scan engine over the in-memory fake broker, on a clock a test moves by hand. It builds the engine's real container
/// (<c>KStudioEngineRegistration</c>), seeds records with explicit timestamps (every one in the past, as the broker requires) and runs
/// scans to their end, so every test below drives the same code the live suite drives against Kafka.
/// </summary>
internal sealed class ScanFixture : IAsyncDisposable
{
    /// <summary>The start of the seeded time: records are stamped <c>T0 + n</c> seconds.</summary>
    public static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private int topics;

    private ScanFixture(ManualTimeProvider clock, FakeKafkaCluster cluster, FakeKafkaWire wire, ServiceProvider provider, CapturingLoggerProvider logs)
    {
        Clock = clock;
        Cluster = cluster;
        Wire = wire;
        Provider = provider;
        Logs = logs;
    }

    public ManualTimeProvider Clock { get; }

    public FakeKafkaCluster Cluster { get; }

    public FakeKafkaWire Wire { get; }

    public ServiceProvider Provider { get; }

    public CapturingLoggerProvider Logs { get; }

    public KafkaScanOwner Owner { get; } = new("owner-1");

    public IKafkaScanner Scanner => Provider.GetRequiredService<IKafkaScanner>();

    public KafkaEngineOptions Options => Provider.GetRequiredService<KafkaEngineOptions>();

    /// <summary>The fake's readers, in the order they were opened.</summary>
    public IReadOnlyList<FakeScanReader> Readers => Wire.Readers;

    public static ScanFixture Create(
        Action<KafkaEngineOptions>? configure = null,
        IScanObserver? observer = null,
        Func<FakeKafkaWire, IKafkaWire>? wrap = null,
        Action<KafkaClusterBuilder>? cluster = null)
    {
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 8, 18, 0, 0, TimeSpan.Zero));
        FakeKafkaCluster fake = new(clock);
        FakeKafkaWire wire = new(fake, EngineHarness.ClusterKey);
        CapturingLoggerProvider logs = new();

        KafkaEngineOptions options = new();
        options.AddCluster(EngineHarness.ClusterKey, new ClientConfig { BootstrapServers = "fake:9092" }, cluster);
        configure?.Invoke(options);

        ServiceCollection services = new();
        services.AddSingleton(logs.CreateFactory());
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IKafkaWireFactory>(new SingleWireFactory(wrap?.Invoke(wire) ?? wire));

        if (observer is not null)
        {
            services.AddSingleton(observer);
        }

        KStudioEngineRegistration.Add(services, _ => options, _ => new TestEngineSettings(TimeSpan.Zero));
        return new ScanFixture(clock, fake, wire, services.BuildServiceProvider(), logs);
    }

    /// <summary>Creates a topic and returns its name.</summary>
    public string Topic(string label, int partitions = 1, IReadOnlyDictionary<string, string>? config = null)
    {
        string name = $"ks.{label}.{Interlocked.Increment(ref topics)}";
        Cluster.CreateTopic(name, partitions, config);
        return name;
    }

    /// <summary>Appends a record <c>seconds</c> after <see cref="T0" />.</summary>
    public long Produce(
        string topic,
        int partition,
        int seconds,
        string? key,
        string? value,
        params (string Name, string? Value)[] headers) =>
        Cluster.Produce(
            topic,
            partition,
            key is null ? null : Encoding.UTF8.GetBytes(key),
            value is null ? null : Encoding.UTF8.GetBytes(value),
            [.. headers.Select(static x => new WireHeader(x.Name, x.Value is null ? null : Encoding.UTF8.GetBytes(x.Value)))],
            T0.ToUnixTimeMilliseconds() + (seconds * 1000L));

    /// <summary>Appends a record with raw bytes.</summary>
    public long ProduceRaw(string topic, int partition, int seconds, byte[]? key, byte[]? value, params WireHeader[] headers) =>
        Cluster.Produce(topic, partition, key, value, headers, T0.ToUnixTimeMilliseconds() + (seconds * 1000L));

    public static KafkaWindow Window(int fromSeconds, int toSeconds) => new(T0.AddSeconds(fromSeconds), T0.AddSeconds(toSeconds));

    public static KafkaScanRequest Request(
        string[] topics,
        string? predicate = null,
        int fromSeconds = 0,
        int toSeconds = 1_000,
        KafkaReadGrant grant = KafkaReadGrant.Envelope,
        KafkaIsolation isolation = KafkaIsolation.Committed,
        KafkaScanCursor? cursor = null,
        KafkaScanLowering? lower = null,
        TimeSpan? margin = null) =>
        new(
            EngineHarness.ClusterKey,
            [.. topics.Select(static x => new KafkaTopicName(x))],
            Window(fromSeconds, toSeconds),
            predicate,
            grant,
            isolation,
            margin ?? TimeSpan.Zero,
            cursor,
            lower);

    public Task<KafkaScanPlan> PlanAsync(KafkaScanRequest request) => Scanner.PlanAsync(request, Owner, CancellationToken.None);

    public KafkaScanStart Start(KafkaScanPlan plan, bool confirmed = false, KafkaScanOwner? owner = null) => Scanner.Start(plan, owner ?? Owner, confirmed);

    /// <summary>Plans, starts and waits for the result; fails the test if the scan did not start.</summary>
    public async Task<(KafkaScanPlan Plan, KafkaScanResult Result, IReadOnlyList<KafkaMatch> Matches)> RunAsync(KafkaScanRequest request, bool confirmed = true)
    {
        KafkaScanPlan plan = await PlanAsync(request);
        plan.Errors.Should().BeEmpty("the search text of a test run is valid");

        KafkaScanStart started = Start(plan, confirmed);
        started.Refusal.Should().BeNull();

        await using KafkaScanHandle handle = started.Handle!;
        KafkaScanResult result = await handle.Result.Bounded();
        return (plan, result, handle.Matches);
    }

    /// <summary>Declares a topic's builder on the options; only before the first plan, which is when the registry reads them.</summary>
    public void Declare(string topic, Action<KafkaTopicBuilder> configure) => Options.Topic(topic, configure);

    /// <summary>Waits on a condition, for at most the suite's gate bound (a wait on a condition, never a delay).</summary>
    public static void Until(Func<bool> condition, string what)
    {
        SpinWait.SpinUntil(condition, TestBounds.Gate).Should().BeTrue("the test waits for: " + what);
    }

    /// <summary>A wire record for a scripted reader.</summary>
    public static WireRecord Rec(string topic, int partition, long offset, int seconds, string key = "k", string value = "v", WireTimestampType type = WireTimestampType.CreateTime) =>
        new(topic, partition, offset, Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value), [], T0.ToUnixTimeMilliseconds() + (seconds * 1000L), type, 0);

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}

/// <summary>A scan reader that a test controls: what it returns, and when. It wraps nothing; it is the whole reader.</summary>
internal sealed class ScriptedReader : IWireScanReader
{
    private readonly Queue<(WireReadOutcome? Outcome, Action? Action)> script = new();
    private readonly SemaphoreSlim arrived = new(0);
    private readonly ManualResetEventSlim unblock = new(false);
    private readonly Lock gate = new();
    private int reads;

    public List<IReadOnlyList<WirePartitionOffset>> Assigns { get; } = [];

    public List<WirePartition> Paused { get; } = [];

    public List<WirePartitionOffset> Repositions { get; } = [];

    public int Reads => Volatile.Read(ref reads);

    public int CloseCount { get; private set; }

    /// <summary>Whether any <c>Read</c> ran on a thread-pool thread: the scan's thread must be a dedicated one.</summary>
    public bool ReadOnPoolThread { get; private set; }

    public Func<WirePartitionOffset, bool>? RepositionFails { get; set; }

    /// <summary>When set, <c>Read</c> ignores its timeout and blocks until <see cref="Unblock" />: a reader that never returns to its thread.</summary>
    public bool BlockForever { get; set; }

    public void Unblock() => unblock.Set();

    /// <summary>Queues an outcome for a <c>Read</c>.</summary>
    public void Enqueue(WireReadOutcome outcome)
    {
        lock (gate)
        {
            script.Enqueue((outcome, null));
        }

        arrived.Release();
    }

    /// <summary>Queues something to do on the scan's thread, between two outcomes (for example moving the clock exactly there).</summary>
    public void EnqueueAction(Action action)
    {
        lock (gate)
        {
            script.Enqueue((null, action));
        }

        arrived.Release();
    }

    public void Assign(IReadOnlyList<WirePartitionOffset> start) => Assigns.Add([.. start]);

    public void Pause(IReadOnlyList<WirePartition> partitions) => Paused.AddRange(partitions);

    public void Resume(IReadOnlyList<WirePartition> partitions)
    {
    }

    public void Reposition(WirePartitionOffset position)
    {
        if (RepositionFails?.Invoke(position) == true)
        {
            throw new WireException(new WireError.Failed("Local_State", IsFatal: false));
        }

        Repositions.Add(position);
    }

    public WireReadOutcome Read(TimeSpan timeout)
    {
        Interlocked.Increment(ref reads);
        ReadOnPoolThread |= Thread.CurrentThread.IsThreadPoolThread;

        if (BlockForever)
        {
            unblock.Wait();
            return WireReadOutcome.Idle;
        }

        while (true)
        {
            // A real reader blocks until something arrives or the timeout ends.
            if (!arrived.Wait(timeout))
            {
                return WireReadOutcome.Idle;
            }

            (WireReadOutcome? outcome, Action? action) item;

            lock (gate)
            {
                item = script.Dequeue();
            }

            if (item.action is not null)
            {
                item.action();
                continue;
            }

            return item.outcome!;
        }
    }

    public void Close() => CloseCount++;

    public void Dispose()
    {
    }
}

/// <summary>A wire that opens the readers a test hands it and otherwise answers as the fake does.</summary>
internal sealed class ScriptedWire(FakeKafkaWire inner, Func<WireReaderOptions, IWireScanReader> open) : IKafkaWire
{
    public string ClusterKey => inner.ClusterKey;

    public Task<WireResult<WireMetadata>> GetMetadataAsync(CancellationToken cancellationToken) => inner.GetMetadataAsync(cancellationToken);

    public Task<WireResult<IReadOnlyList<WireTopicDescription>>> DescribeTopicsAsync(IReadOnlyList<KafkaTopicName> topics, CancellationToken cancellationToken) =>
        inner.DescribeTopicsAsync(topics, cancellationToken);

    public Task<WireResult<WireClusterDescription>> DescribeClusterAsync(CancellationToken cancellationToken) => inner.DescribeClusterAsync(cancellationToken);

    public Task<WireResult<IReadOnlyList<WireOffset>>> ListOffsetsAsync(
        IReadOnlyList<WirePartition> partitions, WireOffsetSpec spec, WireIsolation isolation, CancellationToken cancellationToken) =>
        inner.ListOffsetsAsync(partitions, spec, isolation, cancellationToken);

    public Task<WireResult<WireTopicConfig>> DescribeTopicConfigAsync(KafkaTopicName topic, CancellationToken cancellationToken) =>
        inner.DescribeTopicConfigAsync(topic, cancellationToken);

    public Task<WireResult<IReadOnlyList<WireGroupOffset>>> ListGroupOffsetsAsync(string groupId, IReadOnlyList<WirePartition> partitions, CancellationToken cancellationToken) =>
        inner.ListGroupOffsetsAsync(groupId, partitions, cancellationToken);

    public Task<WireResult<WireGroupDescription>> DescribeGroupAsync(string groupId, CancellationToken cancellationToken) =>
        inner.DescribeGroupAsync(groupId, cancellationToken);

    public IWireScanReader OpenReader(WireReaderOptions options) => open(options);
}
