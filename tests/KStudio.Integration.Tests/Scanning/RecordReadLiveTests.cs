using System.Text.Json;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// The record read on a real broker (D-UI decision 1): a seeded record is found by its offset with its neighbours and without anything
/// unredacted, a gap in the offsets is said to be one, the ends of the log are told apart, and a read creates no topic and commits no offset.
/// </summary>
public abstract class RecordReadLiveTests(KafkaFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<KafkaRecordRead> ReadAsync(
        LiveScanEngine engine, string topic, int partition, long offset, int neighbours = 5, KafkaIsolation isolation = KafkaIsolation.Committed) =>
        engine.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), partition, offset, neighbours, KafkaReadGrant.Envelope, isolation),
            engine.Owner,
            Token);

    [Fact]
    public async Task Reads_a_seeded_record_by_offset()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("record-read");
        await admin.CreateTopicAsync(topic, 2);

        for (int partition = 0; partition < 2; partition++)
        {
            await admin.ProduceAsync(
                topic,
                partition,
                [.. Enumerable.Range(0, 30).Select(i => LiveRecords.At(i, $"p{partition}-k{i}", $"v{i}", ("Authorization", "Bearer s3cr3t"), ("x-tenant", "acme")))]);
        }

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        KafkaRecordRead read = await ReadAsync(engine, topic, 1, 12);

        read.Status.Should().Be(KafkaRecordReadStatus.Found, read.Text);
        read.Record!.Key.Text.Should().Be("p1-k12");
        read.Record.Partition.Should().Be(1);
        read.Before.Select(static x => x.Offset).Should().Equal(7, 8, 9, 10, 11);
        read.After.Select(static x => x.Offset).Should().Equal(13, 14, 15, 16, 17);
        read.Before.Concat(read.After).Append(read.Record).Should().OnlyContain(static x => x.Partition == 1);
        (read.LogStart, read.End).Should().Be((0L, 30L));

        read.Record.Headers.Single(static x => x.Name == "Authorization").Value.IsRedacted.Should().BeTrue();
        read.Record.Headers.Single(static x => x.Name == "x-tenant").Value.Text.Should().Be("acme");
        JsonSerializer.Serialize(read).Should().NotContain("s3cr3t");
    }

    [Fact]
    public async Task The_ends_of_the_log_are_told_apart()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("record-ends");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 20).Select(static i => LiveRecords.At(i, "k" + i, "v"))]);
        await admin.DeleteRecordsAsync(topic, 0, 8);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");

        KafkaRecordRead gone = await ReadAsync(engine, topic, 0, 3);
        gone.Status.Should().Be(KafkaRecordReadStatus.BelowLogStart);
        gone.Text.Should().Be($"Offset 3 is gone: the earliest offset {topic}/0 still holds is 8 (retention or DeleteRecords removed the rest).");

        KafkaRecordRead future = await ReadAsync(engine, topic, 0, 20);
        future.Status.Should().Be(KafkaRecordReadStatus.AboveEnd);
        future.Text.Should().Be($"Offset 20 has not been written yet: {topic}/0 ends at offset 20.");

        KafkaRecordRead first = await ReadAsync(engine, topic, 0, 8);
        first.Status.Should().Be(KafkaRecordReadStatus.Found);
        first.Before.Should().BeEmpty("the log starts here");

        KafkaRecordRead last = await ReadAsync(engine, topic, 0, 19);
        last.Status.Should().Be(KafkaRecordReadStatus.Found);
        last.After.Should().BeEmpty();

        KafkaRecordRead missing = await ReadAsync(engine, topic, 4, 0);
        missing.Status.Should().Be(KafkaRecordReadStatus.PartitionNotFound);
    }

    [Fact]
    public async Task A_gap_in_the_offsets_is_NoRecordAtOffset_with_the_nearest_records()
    {
        // A compacted topic's gap needs the broker's log cleaner, whose timing no test can wait on; an aborted transaction leaves the same
        // thing on the log - offsets that hold no record a committed read can return - and the broker writes it at once.
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("record-gap");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 3).Select(static i => LiveRecords.At(i, "before-" + i, "v"))]);

        await using (IContractTransaction aborted = await admin.BeginTransactionAsync(topic, 0, [.. Enumerable.Range(3, 2).Select(static i => LiveRecords.At(i, "aborted-" + i, "v"))]))
        {
            await aborted.AbortAsync();
        }

        await admin.ProduceAsync(topic, 0, [LiveRecords.At(9, "after", "v")]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        KafkaRecordRead read = await ReadAsync(engine, topic, 0, 3);

        read.Status.Should().Be(KafkaRecordReadStatus.NoRecordAtOffset);
        read.Text.Should().Be("No record at offset 3: the topic is compacted, or the offset holds a transaction marker. The nearest records are shown.");
        read.Before.Select(static x => x.Key.Text).Should().Equal("before-0", "before-1", "before-2");
        read.After.Select(static x => x.Key.Text).Should().Equal("after");

        KafkaRecordRead uncommitted = await ReadAsync(engine, topic, 0, 3, isolation: KafkaIsolation.IncludeUncommitted);
        uncommitted.Status.Should().Be(KafkaRecordReadStatus.Found, "an uncommitted read returns the record of the aborted transaction");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("reader")]
    [InlineData("nogroup")]
    public async Task A_record_read_commits_no_offset_and_creates_no_topic(string user)
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("record-never-writes");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 20).Select(static i => LiveRecords.At(i, "k" + i, "v"))]);

        string[] topicsBefore = TopicNames();

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, user);
        KafkaRecordRead read = await ReadAsync(engine, topic, 0, 10);
        read.Status.Should().Be(KafkaRecordReadStatus.Found, read.Text);

        TopicNames().Should().BeEquivalentTo(topicsBefore, "a record read creates no topic, as a principal that could have created one");

        using IAdminClient client = new AdminClientBuilder(fixture.Client("admin")).Build();
        ListConsumerGroupsResult groups = await client.ListConsumerGroupsAsync();
        (groups.Valid ?? []).Select(static x => x.GroupId).Should().NotContain(
            static x => x.StartsWith("kstudio-", StringComparison.Ordinal) && x.Length == "kstudio-".Length + 32,
            "no group named for a scan survives a record read");

        engine.Provider.GetRequiredService<KafkaScanLimiter>().Running.Should().Be(0, "the read gave its slot back");
    }

    [Fact]
    public async Task Spans_of_several_topics_come_from_two_multi_topic_reads()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string first = admin.NewTopic("span-a");
        string second = admin.NewTopic("span-b");
        await admin.CreateTopicAsync(first, 2);
        await admin.CreateTopicAsync(second, 1);
        await admin.ProduceAsync(first, 0, [.. Enumerable.Range(0, 5).Select(static i => LiveRecords.At(i, "k", "v"))]);
        await admin.ProduceAsync(first, 1, [.. Enumerable.Range(0, 3).Select(static i => LiveRecords.At(i, "k", "v"))]);
        await admin.ProduceAsync(second, 0, [.. Enumerable.Range(0, 7).Select(static i => LiveRecords.At(i, "k", "v"))]);
        await admin.DeleteRecordsAsync(second, 0, 2);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        IKafkaCatalog catalog = engine.Provider.GetRequiredService<IKafkaCatalog>();

        KafkaField<IReadOnlyList<KafkaTopicSpan>> spans = await catalog.ListTopicSpansAsync(
            EngineHarness.ClusterKey, [new KafkaTopicName(second), new KafkaTopicName(first), new KafkaTopicName("ks.not-a-topic")], Token);

        spans.IsAvailable.Should().BeTrue();
        spans.Value.Select(static x => x.Topic).Should().Equal(second, first, "ks.not-a-topic");
        spans.Value[0].OffsetSpan.Value.Should().Be(5, "high 7 minus low 2");
        spans.Value[1].OffsetSpan.Value.Should().Be(8, "5 + 3, summed over the partitions");
        spans.Value[2].OffsetSpan.IsAvailable.Should().BeFalse("a topic the broker does not list is withheld alone");
    }

    private string[] TopicNames()
    {
        using IAdminClient client = new AdminClientBuilder(new AdminClientConfig(fixture.Client("admin")) { AllowAutoCreateTopics = false }).Build();

        // Internal topics are the broker's own (a broker that never hosted a consumer group creates __consumer_offsets when a consumer looks up
        // its group coordinator), so only topics a host could see are compared.
        return [.. client.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(static x => x.Topic)
            .Where(static x => !x.StartsWith("__", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
    }
}

/// <summary><see cref="RecordReadLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class RecordReadLiveTests39(Kafka39Fixture fixture) : RecordReadLiveTests(fixture);

/// <summary><see cref="RecordReadLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class RecordReadLiveTests4(Kafka4Fixture fixture) : RecordReadLiveTests(fixture);
