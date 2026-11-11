using KStudio.Core.Clusters;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Core.Windows;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// Retention before and during a scan: a window that starts before the earliest retained record says so (ENG-A), a principal that cannot
/// read the topic's configuration is told that too, so that "no matches" is never said without what could make it untrue, and records
/// deleted while the scan reads are counted as a gap and the scan goes on from the new log start (ENG-B, decision 6.6).
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

    [Fact]
    public async Task Records_deleted_while_a_scan_reads_are_counted_as_a_gap_and_the_scan_goes_on_from_the_new_log_start()
    {
        // The consumer's own prefetch is bounded by the engine's queue (1 MiB at the least) and fetch size, so a deletion is only seen by a scan
        // that is further from the end of the log than that: 2,000 records of 4 KiB are 8 MiB, and the scan is held after its first record.
        const int total = 2_000;
        const int deleteBefore = 1_800;

        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("deleted");
        await admin.CreateTopicAsync(topic, 1);
        string payload = new('x', 4 * 1024);

        for (int block = 0; block < total; block += 500)
        {
            await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(block, 500).Select(i => LiveRecords.At(i, "k" + i, payload))]);
        }

        HoldingObserver observer = new();
        await using LiveScanEngine engine = LiveScanEngine.Create(
            fixture,
            "reader",
            static o =>
            {
                o.Limits.QueueKilobytes = 1_024;
                o.Limits.FetchBytes = 1024 * 1024;
                o.Limits.MaxMatches = 3_000;
            },
            observer);

        KafkaScanPlan plan = await engine.PlanAsync(LiveScanEngine.Request([topic]), Token);
        await using KafkaScanHandle handle = engine.Scanner.Start(plan, engine.Owner, confirmed: true).Handle!;

        await observer.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60), Token);
        await admin.DeleteRecordsAsync(topic, 0, deleteBefore);
        observer.Proceed.Set();

        KafkaScanResult result = await handle.Result.WaitAsync(TimeSpan.FromMinutes(2), Token);

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        KafkaScanGap gap = result.Gaps.Should().ContainSingle("the deletion fell inside the range the scan had not reached").Subject;
        gap.ToOffset.Should().Be(deleteBefore - 1);
        gap.Lost.Should().Be(deleteBefore - gap.FromOffset);

        IReadOnlyList<long> offsets = [.. handle.Matches.Select(static x => x.Offset)];
        offsets.Should().BeInAscendingOrder();
        offsets.Take(offsets.Count - (total - deleteBefore)).Should().Equal(Enumerable.Range(0, (int) gap.FromOffset).Select(static x => (long) x), "everything before the gap was read");
        offsets.TakeLast(total - deleteBefore).Should().Equal(Enumerable.Range(deleteBefore, total - deleteBefore).Select(static x => (long) x), "and everything after it");
        result.Partitions.Single().State.Should().Be(PartitionEndState.EndOfWindow);
        engine.Logs.Entries.Count(static x => x.EventId.Id == 9669).Should().Be(1);
    }

    private sealed class HoldingObserver : IScanObserver
    {
        private int once;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Proceed { get; } = new(false);

        public void AfterRecord(WirePartition partition, long offset)
        {
            if (Interlocked.Exchange(ref once, 1) == 0)
            {
                Reached.SetResult();
                Proceed.Wait(TimeSpan.FromSeconds(120));
            }
        }
    }
}

/// <summary><see cref="RetentionLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class RetentionLiveTests39(Kafka39Fixture fixture) : RetentionLiveTests(fixture);

/// <summary><see cref="RetentionLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class RetentionLiveTests4(Kafka4Fixture fixture) : RetentionLiveTests(fixture);
