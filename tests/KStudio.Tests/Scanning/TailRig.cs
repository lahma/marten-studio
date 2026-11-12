using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;

namespace KStudio.Tests.Scanning;

/// <summary>Plans and starts live tails over the scan fixture's engine (D-M5 decision 3), and waits on what they report.</summary>
internal static class TailRig
{
    public static KafkaTailRequest Request(
        string[] topics,
        string? predicate = null,
        KafkaReadGrant grant = KafkaReadGrant.Envelope,
        KafkaIsolation isolation = KafkaIsolation.Committed,
        KafkaScanCursor? resume = null) =>
        new(EngineHarness.ClusterKey, [.. topics.Select(static x => new KafkaTopicName(x))], predicate, grant, isolation, resume);

    public static Task<KafkaScanPlan> PlanTailAsync(this ScanFixture fixture, KafkaTailRequest request) =>
        fixture.Scanner.PlanTailAsync(request, fixture.Owner, CancellationToken.None);

    public static KafkaScanStart StartTail(this ScanFixture fixture, KafkaScanPlan plan, KafkaScanOwner? owner = null) =>
        fixture.Scanner.StartTail(plan, owner ?? fixture.Owner);

    /// <summary>Plans and starts a tail that must start.</summary>
    public static async Task<KafkaScanHandle> TailAsync(this ScanFixture fixture, KafkaTailRequest request, KafkaScanOwner? owner = null)
    {
        KafkaScanPlan plan = await fixture.PlanTailAsync(request);
        plan.Errors.Should().BeEmpty("the search text of a test run is valid");

        KafkaScanStart started = fixture.StartTail(plan, owner);
        started.Refusal.Should().BeNull();
        return started.Handle!;
    }

    /// <summary>Waits until every partition of the tail has reported its end and no record has arrived since.</summary>
    public static void UntilCaughtUp(this KafkaScanHandle handle) =>
        ScanFixture.Until(() => handle.Progress.Partitions.Count > 0 && handle.Progress.Partitions.All(static x => x.CaughtUp), "every partition of the tail is caught up");

    /// <summary>Waits until the run has read a number of records.</summary>
    public static void UntilRecords(this KafkaScanHandle handle, long records) =>
        ScanFixture.Until(() => handle.Progress.Records >= records, $"the tail has read {records} records");

    /// <summary>The batches of a handle, collected as they arrive.</summary>
    public static List<KafkaScanBatch> Collect(KafkaScanHandle handle)
    {
        List<KafkaScanBatch> into = [];

        _ = Task.Run(
            async () =>
            {
                await foreach (KafkaScanBatch batch in handle.ReadBatchesAsync(CancellationToken.None))
                {
                    lock (into)
                    {
                        into.Add(batch);
                    }
                }
            },
            CancellationToken.None);

        return into;
    }

    public static List<KafkaScanBatch> Snapshot(List<KafkaScanBatch> batches)
    {
        lock (batches)
        {
            return [.. batches];
        }
    }
}
