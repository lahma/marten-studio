using System.Runtime.InteropServices;

using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// The memory a scan holds (AGENTS.md D41, OPEN-6): two concurrent scans over two topics, every record the heaviest the match cap has to bound
/// (a 96 KiB value and 64 headers of 4 KiB, 352 KiB in all) and every record a match, with the process's private memory sampled on a timer.
/// The ceiling is $M \approx S \cdot (C + 2 m c)$: a per-consumer constant of 64 MiB (SPIKE S4: a consumer over six partitions grew by about
/// 51 MiB private and 59 MiB working set, three times $Q + F$, because librdkafka has overhead per consumer) and, per kept match, at most
/// $c$ bytes of key, headers, attributes and value together, which .NET holds as UTF-16. The test allows that plus 25 %, and prints the
/// measured peak either way. Kafka 4.x only. The peak is the whole process's private memory, so the class is in a collection that disables
/// parallelization: nothing else of the run executes while it measures (on CI the same test, beside the other live classes, measured 465 MB
/// against an allowance of 332 MB).
/// </summary>
/// <remarks>
/// <b>The metric is the private memory the process holds, read per OS</b> (<see cref="ProcessMemory" />): on Windows
/// <c>PrivateMemorySize64</c>, on Linux <c>RssAnon</c> of <c>/proc/self/status</c>. .NET's own <c>PrivateMemorySize64</c> on Linux is the
/// <c>VmData</c> line of that file, the size of the data mappings whether or not a page was ever touched, so it counts what the allocators
/// reserve and the process never holds; CI measured 403 MB and 447 MB of it. Measured with the honest figure (KS-CI-FIX-2): Windows 204 to
/// 239 MiB; Linux (Ubuntu 24.04 container, 4 cores) 239 to 267 MiB, 267 MiB being the run beside all the other live classes. The ceiling
/// (253 MiB) and the allowance (316 MiB) are D41's and did not move.
/// </remarks>
[Collection(KafkaCollections.V4Alone)]
public sealed class ScanMemoryCeilingLiveTests4(Kafka4Fixture fixture)
{
    private const long MiB = 1024 * 1024;
    private const int Partitions = 6;
    private const int PerPartition = 90;
    private const int ValueBytes = 96 * 1024;
    private const int HeaderCount = 64;
    private const int HeaderBytes = 4 * 1024;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_concurrent_scans_stay_inside_the_stated_memory_ceiling()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string[] topics = [admin.NewTopic("memory-a"), admin.NewTopic("memory-b")];
        string payload = new('x', ValueBytes);
        (string Name, string Value)[] headers = [.. Enumerable.Range(0, HeaderCount).Select(static i => ("h" + i, new string('h', HeaderBytes)))];

        foreach (string topic in topics)
        {
            await admin.CreateTopicAsync(topic, Partitions);

            for (int partition = 0; partition < Partitions; partition++)
            {
                for (int block = 0; block < PerPartition; block += 30)
                {
                    int count = Math.Min(30, PerPartition - block);
                    await admin.ProduceAsync(topic, partition, [.. Enumerable.Range(block, count).Select(i => LiveRecords.At(i, "k1", payload, headers))]);
                }
            }
        }

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", static o => o.Limits.BytesPerSecond = 64 * MiB);
        KafkaScanOwner first = new("one");
        KafkaScanOwner second = new("two");

        // Every record matches and each scan keeps 500 of them, so the queue and the match store are both full for most of the scan; with the
        // payload grant each match keeps whatever its budget leaves of its value.
        KafkaScanPlan planOne = await engine.Scanner.PlanAsync(LiveScanEngine.Request([topics[0]], "key = 'k1'", grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload), first, Token);
        KafkaScanPlan planTwo = await engine.Scanner.PlanAsync(LiveScanEngine.Request([topics[1]], "key = 'k1'", grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload), second, Token);

        // The baseline is taken with the broker running, the topics produced and the engine built (the fixture is warm), after a full, compacting
        // collection: what the setup left on the managed heap is not the scans' growth.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        ProcessMemory.Reading start = ProcessMemory.Read();
        long baseline = start.Private;
        ProcessMemory.Reading highest = start;

        await using KafkaScanHandle one = engine.Scanner.Start(planOne, first, confirmed: true).Handle!;
        await using KafkaScanHandle two = engine.Scanner.Start(planTwo, second, confirmed: true).Handle!;

        // The sampling is the measurement, not a wait standing in for a condition (decision 12.4).
        long peak = baseline;
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));
        Task<KafkaScanResult[]> done = Task.WhenAll(one.Result, two.Result);

        while (!done.IsCompleted && await timer.WaitForNextTickAsync(Token))
        {
            ProcessMemory.Reading now = ProcessMemory.Read();
            peak = Math.Max(peak, now.Private);
            highest = highest.Max(now);
        }

        KafkaScanResult[] results = await done.WaitAsync(TimeSpan.FromMinutes(3), Token);

        results.Should().OnlyContain(static x => x.StopReason == KafkaScanStopReason.MatchLimit, "every record matches, so each scan keeps its 500 and stops");
        results.Sum(static x => x.Bytes).Should().BeGreaterThan(64 * MiB, "the scans read enough for the queues to fill");

        // What a kept match holds is bounded by c, whatever the record carried.
        KafkaScanLimits limits = new();

        foreach (KafkaMatch match in one.Matches.Concat(two.Matches))
        {
            long held = (match.Key.Text?.Length ?? 0)
                + match.Headers.Sum(static x => x.Name.Length + (x.Value.Text?.Length ?? 0))
                + match.CloudEventAttributes.Sum(static x => x.Name.Length + (x.Value.Text?.Length ?? 0))
                + (match.ValueText?.Length ?? 0)
                + (match.ValuePreview?.Length ?? 0);
            held.Should().BeLessThanOrEqualTo(limits.MaxMatchBytes);
        }

        long grown = peak - baseline;
        double perScanMiB = 64 + (2.0 * limits.MaxMatches * limits.MaxMatchBytes / MiB);
        double ceiling = 2 * perScanMiB * MiB;
        double allowed = ceiling * 1.25;

        // Printed either way, so the number is on record next to the formula.
        TestContext.Current.SendDiagnosticMessage($"KStudio memory ceiling ({fixture.Image}, {RuntimeInformation.OSDescription}): peak growth {grown / (double) MiB:F1} MiB of {ProcessMemory.PrivateName}, ceiling {ceiling / MiB:F1} MiB, allowed {allowed / MiB:F1} MiB (S=2, C=64 MiB, 2*m*c={2.0 * limits.MaxMatches * limits.MaxMatchBytes / MiB:F1} MiB, +25 %). Highest of each over the scans, baseline to peak: {highest.Describe(start)}.");

        grown.Should().BeLessThan((long) allowed);
    }
}
