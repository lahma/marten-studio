using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// AGENTS.md hard rule 17 on the broker, for scans: after scans as <c>admin</c> (who may create topics, and whose broker auto-creates them) and
/// as <c>reader</c>, no topic was created, the scan's group has no committed offset, and no group named for a scan exists on the cluster.
/// </summary>
public abstract class ReadNeverWritesScanLiveTests(KafkaFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("admin")]
    [InlineData("reader")]
    [InlineData("nogroup")]
    public async Task A_scan_creates_no_topic_commits_no_offset_and_leaves_no_group(string user)
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("never-writes");
        await admin.CreateTopicAsync(topic, 2);

        for (int partition = 0; partition < 2; partition++)
        {
            await admin.ProduceAsync(topic, partition, [.. Enumerable.Range(0, 50).Select(static i => LiveRecords.At(i, "k" + i, "v" + i))]);
        }

        string[] topicsBefore = TopicNames();

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, user);
        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(LiveScanEngine.Request([topic], "key ^= 'k'"), Token);
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        matches.Should().HaveCount(100);

        // Also a scan that is stopped part-way: a consumer that is closed while it reads must not commit either.
        KafkaScanPlan plan = await engine.PlanAsync(LiveScanEngine.Request([topic]), Token);
        await using KafkaScanHandle stopped = engine.Scanner.Start(plan, engine.Owner, confirmed: true).Handle!;
        stopped.Stop();
        KafkaScanResult partial = await stopped.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);

        TopicNames().Should().BeEquivalentTo(topicsBefore, "a scan creates no topic, as a principal that could have created one");

        string[] scanGroups = [$"kstudio-{result.ScanId:N}", $"kstudio-{partial.ScanId:N}"];

        using IAdminClient client = new AdminClientBuilder(fixture.Client("admin")).Build();
        ListConsumerGroupsResult groups = await client.ListConsumerGroupsAsync();
        IReadOnlyList<string> listed = [.. (groups.Valid ?? []).Select(static x => x.GroupId)];
        listed.Should().NotContain(scanGroups, "the scan's group id is never joined: the group does not exist");
        listed.Should().NotContain(static x => x.StartsWith("kstudio-", StringComparison.Ordinal) && x.Length == "kstudio-".Length + 32, "no group named for a scan survives");

        foreach (string group in scanGroups)
        {
            List<ListConsumerGroupOffsetsResult> committed = await client.ListConsumerGroupOffsetsAsync(
                [new ConsumerGroupTopicPartitions(group, [new TopicPartition(topic, 0), new TopicPartition(topic, 1)])]);

            committed.SelectMany(static x => x.Partitions).Where(static x => x.Offset.Value >= 0 && !x.Error.IsError).Should().BeEmpty($"group {group} committed nothing");
        }
    }

    private string[] TopicNames()
    {
        using IAdminClient client = new AdminClientBuilder(new AdminClientConfig(fixture.Client("admin")) { AllowAutoCreateTopics = false }).Build();
        return [.. client.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(static x => x.Topic).Order(StringComparer.Ordinal)];
    }
}

/// <summary><see cref="ReadNeverWritesScanLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class ReadNeverWritesScanLiveTests39(Kafka39Fixture fixture) : ReadNeverWritesScanLiveTests(fixture);

/// <summary><see cref="ReadNeverWritesScanLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class ReadNeverWritesScanLiveTests4(Kafka4Fixture fixture) : ReadNeverWritesScanLiveTests(fixture);
