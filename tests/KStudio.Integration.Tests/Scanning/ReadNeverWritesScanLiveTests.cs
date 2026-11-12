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
            LiveGroupOffsets.Committed(fixture, group, [new TopicPartition(topic, 0), new TopicPartition(topic, 1)])
                .Should().BeEmpty($"group {group} committed nothing");
        }
    }

    /// <summary>
    /// The same rule for a live tail (D-M5 decision 3.10), which runs for much longer than a scan and stops and resumes: records are produced during
    /// it, and afterwards no topic was created, neither segment committed an offset and no group named for a scan exists. The principal that cannot
    /// reach a group at all still reads. <c>admin</c> (who could have created a topic or joined a group) tails for a minute; the others for ten seconds.
    /// </summary>
    [Theory]
    [InlineData("admin", 60)]
    [InlineData("reader", 10)]
    [InlineData("nogroup", 10)]
    public async Task A_live_tail_creates_no_topic_commits_no_offset_and_leaves_no_group(string user, int seconds)
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("tail-never-writes");
        await admin.CreateTopicAsync(topic, 2);

        for (int partition = 0; partition < 2; partition++)
        {
            await admin.ProduceAsync(topic, partition, [.. Enumerable.Range(0, 10).Select(static i => LiveRecords.At(i, "old-" + i, "v"))]);
        }

        string[] topicsBefore = TopicNames();

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, user);
        KafkaScanHandle first = await TailLiveTests.StartAsync(engine, [topic]);
        await using KafkaScanHandle firstGuard = first;
        TailLiveTests.UntilCaughtUp(first);

        // Records arrive for the whole of the tail's life, one every second across both partitions.
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        int produced = 0;
        KafkaScanResult? paused = null;
        KafkaScanHandle? second = null;

        while (DateTime.UtcNow < end)
        {
            await admin.ProduceAsync(topic, produced % 2, [LiveRecords.At(100 + produced, "live-" + produced, "v")]);
            produced++;

            // Half-way through, the first segment is paused and a second begins from its cursor: a resume is a new consumer, and commits nothing either.
            if (paused is null && DateTime.UtcNow > end.AddSeconds(-seconds / 2.0))
            {
                first.Stop(KafkaScanStopReason.Paused);
                paused = await first.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);
                second = await TailLiveTests.StartAsync(engine, [topic], resume: paused.Cursor);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Token);
        }

        paused.Should().NotBeNull();
        await using KafkaScanHandle secondGuard = second!;
        SpinWait.SpinUntil(() => first.Matches.Count + second!.Matches.Count == produced, TimeSpan.FromSeconds(30)).Should().BeTrue("every record produced during the tail was read, none twice");

        second!.Stop();
        KafkaScanResult last = await second.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);

        TopicNames().Should().BeEquivalentTo(topicsBefore, "a tail creates no topic, as a principal that could have created one");

        string[] tailGroups = [$"kstudio-{paused!.ScanId:N}", $"kstudio-{last.ScanId:N}"];

        using IAdminClient client = new AdminClientBuilder(fixture.Client("admin")).Build();
        ListConsumerGroupsResult groups = await client.ListConsumerGroupsAsync();
        IReadOnlyList<string> listed = [.. (groups.Valid ?? []).Select(static x => x.GroupId)];
        listed.Should().NotContain(tailGroups, "a tail's group id is never joined: the group does not exist");
        listed.Should().NotContain(static x => x.StartsWith("kstudio-", StringComparison.Ordinal) && x.Length == "kstudio-".Length + 32, "no group named for a scan survives");

        foreach (string group in tailGroups)
        {
            LiveGroupOffsets.Committed(fixture, group, [new TopicPartition(topic, 0), new TopicPartition(topic, 1)])
                .Should().BeEmpty($"group {group} committed nothing");
        }
    }

    private string[] TopicNames()
    {
        using IAdminClient client = new AdminClientBuilder(new AdminClientConfig(fixture.Client("admin")) { AllowAutoCreateTopics = false }).Build();
        // A broker that never hosted a consumer group creates its internal __consumer_offsets when the scan consumer looks up
        // its group coordinator (kstudio.md D37, "The group coordinator on a pristine cluster"); internal topics are the
        // broker's own, so only topics a host could see are compared.
        return [.. client.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(static x => x.Topic)
            .Where(static x => !x.StartsWith("__", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
    }
}

/// <summary><see cref="ReadNeverWritesScanLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class ReadNeverWritesScanLiveTests39(Kafka39Fixture fixture) : ReadNeverWritesScanLiveTests(fixture);

/// <summary><see cref="ReadNeverWritesScanLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class ReadNeverWritesScanLiveTests4(Kafka4Fixture fixture) : ReadNeverWritesScanLiveTests(fixture);
