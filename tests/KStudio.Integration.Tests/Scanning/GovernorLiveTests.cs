using System.Diagnostics;

using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// The byte rate on a real broker (AGENTS.md D41, decision 9.3): with a 2 MiB/s ceiling, a scan reads 2 MiB/s over 20 seconds, within 10 %.
/// Nothing here asserts a throughput floor - unthrottled, the designed limits ran at 28 to 70 MiB/s against about 200 MiB/s for librdkafka's
/// defaults (SPIKE S4) - only that the ceiling holds. The first second is left out: the bucket starts full, a scan may read one second of
/// rate at once. Kafka 4.x only (OPEN-6).
/// </summary>
[Collection(KafkaCollections.V4)]
public sealed class GovernorLiveTests4(Kafka4Fixture fixture)
{
    private const long MiB = 1024 * 1024;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scan_reads_the_ceiling_within_ten_percent_over_twenty_seconds()
    {
        // 6,500 records of 8 KiB are 52 MiB: more than the 22 s of a 2 MiB/s ceiling can read, so the scan is still running at the end.
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("governor");
        await admin.CreateTopicAsync(topic, 1);
        string payload = new('x', (8 * 1024) - 2);

        for (int block = 0; block < 6_500; block += 500)
        {
            await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(block, 500).Select(i => LiveRecords.At(i, "k" + (i % 10), payload))]);
        }

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", static o => o.Limits.BytesPerSecond = 2 * MiB);
        // Nothing matches, so the scan is stopped by nothing but the ceiling.
        KafkaScanPlan plan = await engine.PlanAsync(LiveScanEngine.Request([topic], "key = 'nothing'"), Token);
        await using KafkaScanHandle handle = engine.Scanner.Start(plan, engine.Owner, confirmed: true).Handle!;

        // The sampling is the measurement, not a wait standing in for a condition (decision 12.4).
        Stopwatch clock = Stopwatch.StartNew();
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(100));
        (double Seconds, long Bytes)? first = null;
        (double Seconds, long Bytes)? last = null;

        while (await timer.WaitForNextTickAsync(Token))
        {
            double seconds = clock.Elapsed.TotalSeconds;
            long bytes = handle.Progress.Bytes;

            if (first is null && seconds >= 1)
            {
                first = (seconds, bytes);
            }

            if (seconds >= 21)
            {
                last = (seconds, bytes);
                break;
            }

            handle.IsRunning.Should().BeTrue("the scan has more to read than the ceiling lets it read in 21 seconds");
        }

        handle.Stop();
        await handle.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);

        double rate = (last!.Value.Bytes - first!.Value.Bytes) / (last.Value.Seconds - first.Value.Seconds);

        rate.Should().BeInRange(2 * MiB * 0.9, 2 * MiB * 1.1, $"the measured rate was {rate / MiB:F2} MiB/s over {last.Value.Seconds - first.Value.Seconds:F1} s");
    }
}
