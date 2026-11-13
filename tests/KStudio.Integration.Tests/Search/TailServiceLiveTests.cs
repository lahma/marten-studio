using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;
using KStudio.Services;

namespace KStudio.Integration.Tests.Search;

/// <summary>
/// The live tail service over a real broker (AGENTS.md D46, D-M5 decision 3.8 and 3.9), composed by the real registrations over the real policy, ring and log: a
/// tail started at the end follows what is produced next, a pause and a resume read every offset once, the deny-listed header stays masked, a tail is one audit
/// entry however many segments it read in, and a <c>Production</c> cluster is never tailed.
/// </summary>
public abstract class TailServiceLiveTests(KafkaFixture fixture)
{
    private const string Sentinel = "S3NT1NEL-tail-live-4d2a";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task Pump(KafkaTailSegment segment) =>
        Task.Run(
            async () =>
            {
                await foreach (KafkaScanBatch batch in segment.ReadBatchesAsync(Token))
                {
                    segment.Session.Apply(batch);

                    if (batch.Final is { } final)
                    {
                        segment.Complete(final);
                    }
                }
            },
            Token);

    private static async Task<(KafkaTailSegment Segment, Task End)> BeginAsync(KafkaTailService tail, KafkaTailInput input, KafkaTailSession? session = null)
    {
        KafkaTailPlanResult plan = await tail.PlanAsync(input, session, Token);
        KafkaTailPlanned planned = plan.Should().BeOfType<KafkaTailPlanResult.Planned>().Subject.Value;
        planned.Plan.IsValid.Should().BeTrue(string.Join("; ", planned.Plan.Errors.Select(static x => x.Message)));

        KafkaTailStartResult start = await tail.StartAsync(session, planned, Token);
        KafkaTailSegment segment = start.Should().BeOfType<KafkaTailStartResult.Running>().Subject.Segment;
        return (segment, Pump(segment));
    }

    private static void Until(Func<bool> condition, string what) =>
        SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(30)).Should().BeTrue("the test waits for: " + what);

    private static void UntilCaughtUp(KafkaTailSession session) =>
        Until(() => session.LiveProgress is { } progress && progress.Partitions.Count > 0 && progress.Partitions.All(static x => x.CaughtUp), "every partition reported its end");

    [KafkaFact]
    public async Task A_tail_follows_what_is_produced_after_its_start_pauses_and_resumes_without_a_gap_and_is_one_audit_entry()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("tail-service");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 5).Select(static i => LiveRecords.At(i, "old-" + i, "v"))]);
        await using LiveSearchHost host = LiveSearchHost.Create(fixture, "reader", options => options.Capabilities.LiveTail = true);
        KafkaTailService tail = host.Tail;

        (KafkaTailSegment first, Task firstEnd) = await BeginAsync(tail, new KafkaTailInput(host.Entry, [topic], null, IncludeUncommitted: false));
        KafkaTailSession session = first.Session;
        UntilCaughtUp(session);
        session.Ring.Should().BeEmpty("a tail starts at the end: the five records already there are not read");

        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(10, 3).Select(static i => LiveRecords.At(i, "new-" + i, "v", ("authorization", "Bearer " + Sentinel)))]);
        Until(() => session.Ring.Count == 3, "three records produced after the start");
        first.Pause();
        await firstEnd.WaitAsync(TimeSpan.FromSeconds(30), Token);
        session.LastStop.Should().Be(KafkaScanStopReason.Paused);

        // Produced while paused: the resume goes on from the exact cursor, so none is missed.
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(20, 2).Select(static i => LiveRecords.At(i, "later-" + i, "v", ("authorization", "Bearer " + Sentinel)))]);
        (KafkaTailSegment second, Task secondEnd) = await BeginAsync(tail, session.Input with { ResumeFrom = session.Cursor }, session);
        Until(() => session.Ring.Count == 5, "the two records produced while paused");
        second.Stop();
        await secondEnd.WaitAsync(TimeSpan.FromSeconds(30), Token);

        session.Ring.Select(static x => x.Offset).Should().Equal([9L, 8L, 7L, 6L, 5L], "newest first, every offset once");
        session.Ring.SelectMany(static x => x.Headers).Where(static x => x.Name == "authorization").Should().OnlyContain(static x => x.Value.IsRedacted);
        host.Ring.Should().BeEmpty("a pause and a resume write nothing");

        session.End(KafkaScanStopReason.Cancelled);
        await session.DisposeAsync();

        host.Ring.Where(static x => x.Action == "LiveTail").ToArray().Should().ContainSingle("one entry per session").Which.Message.Should().Contain("2 segments").And.Contain("5 records").And.NotContain(Sentinel);
        host.Logs.Entries.Where(static x => x.EventId.Id is 9607 or 9608).Select(static x => x.EventId.Id).Should().Equal(9607, 9608);
        host.Logs.Entries.Should().NotContain(static x => x.Message.Contains(Sentinel, StringComparison.Ordinal));
    }

    [KafkaFact]
    public async Task A_production_cluster_is_never_tailed_even_when_the_capability_is_granted()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("tail-production");
        await admin.CreateTopicAsync(topic, 1);
        await using LiveSearchHost host = LiveSearchHost.Create(fixture, "reader", options => options.Capabilities.LiveTail = true, KafkaProfile.Production);

        KafkaTailPlanResult plan = await host.Tail.PlanAsync(new KafkaTailInput(host.Entry, [topic], null, IncludeUncommitted: false), null, Token);

        plan.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Contain("KafkaClusterBuilder.Profile").And.Contain("even when they are granted");
        host.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9609);
        host.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9607);
    }

    [KafkaFact]
    public async Task A_request_naming_a_refused_topic_is_refused_whole()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string allowed = admin.NewTopic("tail-allowed");
        string hidden = admin.NewTopic("tail-hidden");
        await admin.CreateTopicAsync(allowed, 1);
        await admin.CreateTopicAsync(hidden, 1);
        await using LiveSearchHost host = LiveSearchHost.Create(fixture, "reader", options => options.Capabilities.LiveTail = true);
        host.Refuse(hidden);

        KafkaTailPlanResult plan = await host.Tail.PlanAsync(new KafkaTailInput(host.Entry, [allowed, hidden], null, IncludeUncommitted: false), null, Token);

        plan.Should().BeOfType<KafkaTailPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.RequestRefused).And.NotContain(hidden);
        host.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9603);
    }
}

/// <summary>The live tail service on Kafka 3.9.</summary>
/// <param name="fixture">The broker.</param>
[Collection(KafkaCollections.V39)]
public sealed class TailServiceLiveTests39(Kafka39Fixture fixture) : TailServiceLiveTests(fixture);

/// <summary>The live tail service on Kafka 4.x.</summary>
/// <param name="fixture">The broker.</param>
[Collection(KafkaCollections.V4)]
public sealed class TailServiceLiveTests4(Kafka4Fixture fixture) : TailServiceLiveTests(fixture);
