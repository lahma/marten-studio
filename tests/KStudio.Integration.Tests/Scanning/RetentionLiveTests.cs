using KStudio.Core.Clusters;
using KStudio.Core.Windows;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// Retention before a scan (ENG-A's part; ENG-B adds deletion during the scan through the observer): a window that starts before the earliest
/// retained record says so, and a principal that cannot read the topic's configuration is told that too, so that "no matches" is never said
/// without what could make it untrue.
/// </summary>
public abstract class RetentionLiveTests(KafkaFixture fixture)
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_restricted_reader_is_told_about_the_retention_gap_and_about_the_configs_it_cannot_read()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("retention");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 10).Select(i => new ContractRecord("k" + i, "v" + i, T0 + (i * 10_000L)))]);
        await admin.DeleteRecordsAsync(topic, 0, 5);

        await using EngineHarness reader = EngineHarness.ForClient(fixture.Client("reader"));

        ResolvedWindow resolved = await reader.Resolver.ResolveAsync(
            EngineHarness.ClusterKey,
            [new KafkaTopicName(topic)],
            new KafkaWindow(DateTimeOffset.FromUnixTimeMilliseconds(T0), DateTimeOffset.FromUnixTimeMilliseconds(T0 + 100_000)),
            KafkaIsolation.Committed,
            TimeSpan.Zero,
            Token);

        PartitionRange range = resolved.Ranges.Single();
        (range.Start, range.End, range.LogStart).Should().Be((5L, 10L, 5L));
        range.Notes.OfType<WindowNote.RetentionGap>().Single().Text.Should().Be(
            $"Records before {WindowNote.Time(DateTimeOffset.FromUnixTimeMilliseconds(T0 + 50_000))} are gone (retention): the earliest retained record is offset 5");
        resolved.Topics.Single().Notes.OfType<WindowNote.ConfigNotReadable>().Single().Text.Should().StartWith(
            $"Not available: needs `DESCRIBE_CONFIGS` on topic `{topic}`");
        resolved.Topics.Single().TimestampKind.Should().Be(KafkaTimestampKind.Unknown);
    }
}

/// <summary><see cref="RetentionLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class RetentionLiveTests39(Kafka39Fixture fixture) : RetentionLiveTests(fixture);

/// <summary><see cref="RetentionLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class RetentionLiveTests4(Kafka4Fixture fixture) : RetentionLiveTests(fixture);
