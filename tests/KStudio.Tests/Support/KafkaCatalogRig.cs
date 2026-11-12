using System.Text;

using KStudio.Services;
using KStudio.Tests.Contract;
using KStudio.Tests.Support.FakeKafka;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Support;

/// <summary>
/// The real engine over the fake broker, behind the studio's real circuit services (D-UI decision 9b): the catalog pages are rendered against what a
/// host's container holds, and the fake's call counter says what they asked the broker. One cluster, keyed <see cref="EngineHarness.ClusterKey" />.
/// </summary>
internal sealed class KafkaCatalogRig : IAsyncDisposable
{
    /// <summary>The first record's timestamp, for the topics a test fills.</summary>
    public static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>The cluster entry the studio lists: the engine harness's own cluster, unless a test registers another and names it.</summary>
    public KafkaClusterEntry Entry { get; }

    public KafkaCatalogRig(Action<KafkaEngineOptions>? engine = null, Action<KafkaStudioOptions>? studio = null, string cluster = EngineHarness.ClusterKey)
    {
        Entry = new KafkaClusterEntry(cluster, null, "Test cluster", "production");
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero));
        Cluster = new FakeKafkaCluster(Clock);
        Engine = EngineHarness.ForWire(new FakeKafkaWire(Cluster, EngineHarness.ClusterKey), Clock, TimeSpan.FromSeconds(5), engine);
        Studio = new KafkaStudioHarness(studio, null, [Entry], Engine);
    }

    public ManualTimeProvider Clock { get; }

    public FakeKafkaCluster Cluster { get; }

    public EngineHarness Engine { get; }

    public KafkaStudioHarness Studio { get; }

    /// <summary>How many list-offsets calls reached the fake broker.</summary>
    public int ListOffsetCalls => Cluster.Calls.Count(static x => x.Call == KStudio.Core.Wire.WireCall.ListOffsets);

    /// <summary>How many calls of any kind reached the fake broker.</summary>
    public int BrokerCalls => Cluster.Calls.Count;

    /// <summary>Creates a topic and fills each partition with records whose timestamps start at <see cref="T0" />.</summary>
    public void Topic(string name, int partitions, params int[] recordsPerPartition) =>
        Topic(name, partitions, null, recordsPerPartition);

    /// <summary>The same, with the topic's configuration.</summary>
    public void Topic(string name, int partitions, IReadOnlyDictionary<string, string>? config, params int[] recordsPerPartition)
    {
        Cluster.CreateTopic(name, partitions, config);

        for (int partition = 0; partition < recordsPerPartition.Length; partition++)
        {
            for (int i = 0; i < recordsPerPartition[partition]; i++)
            {
                Cluster.Produce(name, partition, Encoding.UTF8.GetBytes("k" + i), Encoding.UTF8.GetBytes("v" + i), null, T0 + (i * 1000));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Studio.Dispose();
        await Engine.DisposeAsync();
    }
}
