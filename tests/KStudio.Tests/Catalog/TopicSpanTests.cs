using System.Text;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Tests.Contract;
using KStudio.Tests.Support.FakeKafka;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Catalog;

/// <summary>The home's offset spans (D-UI decision 2): one page of topics costs two cached reads, whatever it holds, and a refusal withholds one topic.</summary>
public class TopicSpanTests
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero));
            Cluster = new FakeKafkaCluster(Clock);
            Engine = EngineHarness.ForWire(new FakeKafkaWire(Cluster, EngineHarness.ClusterKey), Clock, TimeSpan.FromSeconds(5));
        }

        public ManualTimeProvider Clock { get; }

        public FakeKafkaCluster Cluster { get; }

        public EngineHarness Engine { get; }

        public void Topic(string name, int partitions, params int[] recordsPerPartition)
        {
            Cluster.CreateTopic(name, partitions);

            for (int partition = 0; partition < recordsPerPartition.Length; partition++)
            {
                for (int i = 0; i < recordsPerPartition[partition]; i++)
                {
                    Cluster.Produce(name, partition, Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v"), null, T0 + (i * 1000));
                }
            }
        }

        public Task<KafkaField<IReadOnlyList<KafkaTopicSpan>>> Spans(params string[] topics) =>
            Engine.Catalog.ListTopicSpansAsync(EngineHarness.ClusterKey, [.. topics.Select(static x => new KafkaTopicName(x))], Token);

        public int ListOffsetCalls => Cluster.Calls.Count(static x => x.Call == WireCall.ListOffsets);

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    [Fact]
    public async Task Spans_sum_each_topics_partitions()
    {
        await using Rig rig = new();
        rig.Topic("orders", 2, 5, 3);
        rig.Topic("payments", 1, 4);
        rig.Topic("empty", 1, 0);
        rig.Cluster.DeleteRecords("orders", 0, 2);

        KafkaField<IReadOnlyList<KafkaTopicSpan>> spans = await rig.Spans("payments", "orders", "empty");

        spans.IsAvailable.Should().BeTrue();
        spans.Value.Select(static x => x.Topic).Should().Equal("payments", "orders", "empty");

        // (5 - 2) + 3 = 6 for orders: high minus low, summed over its partitions.
        spans.Value.Select(static x => x.OffsetSpan.Value).Should().Equal(4L, 6L, 0L);
    }

    [Fact]
    public async Task A_refused_topic_is_withheld_and_the_others_kept()
    {
        await using Rig rig = new();
        rig.Topic("orders", 1, 5);
        rig.Topic("payments", 1, 4);
        rig.Cluster.OffsetFault = static (partition, _, _) =>
            partition.Topic == "payments" ? new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Topic, "payments") : null;

        KafkaField<IReadOnlyList<KafkaTopicSpan>> spans = await rig.Spans("orders", "payments", "missing");

        spans.Value[0].OffsetSpan.Value.Should().Be(5);
        spans.Value[1].OffsetSpan.IsAvailable.Should().BeFalse();
        spans.Value[1].OffsetSpan.Error.Should().BeOfType<WireError.NotAuthorized>();
        spans.Value[2].OffsetSpan.Error.Should().BeOfType<WireError.NotFound>("a topic metadata does not list is missing, or not describable: the same thing to a visitor");
    }

    [Fact]
    public async Task More_than_100_topics_throw()
    {
        await using Rig rig = new();
        string[] topics = [.. Enumerable.Range(0, 101).Select(static i => "t" + i)];

        Func<Task> ask = () => rig.Spans(topics);

        await ask.Should().ThrowAsync<ArgumentException>();
        (await rig.Spans([.. topics.Take(100)])).IsAvailable.Should().BeTrue("exactly one page is allowed");
    }

    [Fact]
    public async Task Two_callers_within_the_TTL_cost_one_read()
    {
        await using Rig rig = new();
        rig.Topic("orders", 3, 5, 3, 1);
        rig.Topic("payments", 2, 4, 4);

        await rig.Spans("orders", "payments");
        int first = rig.ListOffsetCalls;
        await rig.Spans("orders", "payments");

        first.Should().Be(2, "Earliest and Latest, each for every partition of the page in one call");
        rig.ListOffsetCalls.Should().Be(first, "the second caller within the refresh interval reads nothing");

        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        await rig.Spans("orders", "payments");
        rig.ListOffsetCalls.Should().Be(first + 2, "after the interval the page is read again");
    }

    [Fact]
    public async Task A_page_of_a_hundred_topics_costs_two_reads_and_a_whole_failure_withholds_the_page()
    {
        await using Rig rig = new();

        for (int i = 0; i < 100; i++)
        {
            rig.Topic("t" + i, 2, 1, 2);
        }

        KafkaField<IReadOnlyList<KafkaTopicSpan>> spans = await rig.Spans([.. Enumerable.Range(0, 100).Select(static i => "t" + i)]);

        spans.Value.Should().HaveCount(100).And.OnlyContain(static x => x.OffsetSpan.Value == 3);
        rig.ListOffsetCalls.Should().Be(2);

        rig.Cluster.FailNext(WireCall.Metadata, new WireError.Unavailable("Local_Transport", Retriable: true));
        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        (await rig.Spans("t0")).IsAvailable.Should().BeFalse("when the metadata read fails the whole answer is withheld, with the error");
    }
}
