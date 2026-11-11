using System.Diagnostics;

using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// The memory a scan holds (AGENTS.md D41, OPEN-6): two concurrent scans over two topics of 128 MiB each, every record kept up to the match cap,
/// with the process's private memory sampled on a timer. The ceiling is $M \approx S \cdot (C + m \cdot c)$ with a per-consumer constant of
/// 64 MiB (SPIKE S4: a consumer over six partitions grew by about 51 MiB private and 59 MiB working set, three times $Q + F$, because
/// librdkafka has overhead per consumer), and the test allows that plus 25 %. It prints the measured peak either way. Kafka 4.x only.
/// </summary>
[Collection(KafkaCollections.V4)]
public sealed class ScanMemoryCeilingLiveTests4(Kafka4Fixture fixture)
{
    private const long MiB = 1024 * 1024;
    private const int Records = 8_192;
    private const int ValueBytes = (16 * 1024) - 2;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_concurrent_scans_stay_inside_the_stated_memory_ceiling()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string[] topics = [admin.NewTopic("memory-a"), admin.NewTopic("memory-b")];
        string payload = new('x', ValueBytes);

        foreach (string topic in topics)
        {
            await admin.CreateTopicAsync(topic, 6);

            for (int partition = 0; partition < 6; partition++)
            {
                for (int block = 0; block < Records / 6; block += 500)
                {
                    int count = Math.Min(500, (Records / 6) - block);
                    await admin.ProduceAsync(topic, partition, [.. Enumerable.Range(block, count).Select(i => LiveRecords.At(i, "k" + (i % 10), payload))]);
                }
            }
        }

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", static o => o.Limits.BytesPerSecond = 64 * MiB);
        KafkaScanOwner first = new("one");
        KafkaScanOwner second = new("two");

        // A tenth of the records match, more than the 500 a scan keeps, and with the payload grant each match keeps its value up to the cap:
        // the queue and the match store are both full for most of the scan.
        KafkaScanPlan planOne = await engine.Scanner.PlanAsync(LiveScanEngine.Request([topics[0]], "key = 'k1'", grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload), first, Token);
        KafkaScanPlan planTwo = await engine.Scanner.PlanAsync(LiveScanEngine.Request([topics[1]], "key = 'k1'", grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload), second, Token);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process current = Process.GetCurrentProcess();
        long baseline = current.PrivateMemorySize64;

        await using KafkaScanHandle one = engine.Scanner.Start(planOne, first, confirmed: true).Handle!;
        await using KafkaScanHandle two = engine.Scanner.Start(planTwo, second, confirmed: true).Handle!;

        // The sampling is the measurement, not a wait standing in for a condition (decision 12.4).
        long peak = baseline;
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));
        Task<KafkaScanResult[]> done = Task.WhenAll(one.Result, two.Result);

        while (!done.IsCompleted && await timer.WaitForNextTickAsync(Token))
        {
            using Process self = Process.GetCurrentProcess();
            peak = Math.Max(peak, self.PrivateMemorySize64);
        }

        KafkaScanResult[] results = await done.WaitAsync(TimeSpan.FromMinutes(3), Token);

        results.Should().OnlyContain(static x => x.StopReason == KafkaScanStopReason.Completed || x.StopReason == KafkaScanStopReason.MatchLimit);
        results.Sum(static x => x.Bytes).Should().BeGreaterThan(8 * MiB, "the scans read enough for the queues to fill");

        long grown = peak - baseline;
        KafkaScanLimits limits = new();
        double perScanMiB = 64 + ((double) limits.MaxMatches * limits.MaxMatchBytes / MiB);
        double allowed = 2 * perScanMiB * 1.25 * MiB;

        // Printed either way, so the number is on record next to the formula.
        TestContext.Current.SendDiagnosticMessage($"KStudio memory ceiling ({fixture.Image}): peak growth {grown / (double) MiB:F1} MiB, allowed {allowed / MiB:F1} MiB (S=2, C=64 MiB, m*c={(double) limits.MaxMatches * limits.MaxMatchBytes / MiB:F1} MiB, +25 %); the formula without C would be 95 MiB.");

        grown.Should().BeLessThan((long) allowed);
    }
}
