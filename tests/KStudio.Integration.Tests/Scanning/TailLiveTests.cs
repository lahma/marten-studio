using System.Diagnostics;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// The live tail (AGENTS.md D46, D-M5 decision 3) on a real broker: records produced after the start arrive within two seconds, a transaction left
/// open at the start is shown when it commits and never skipped, and a pause plus a resume read every offset exactly once.
/// </summary>
public abstract class TailLiveTests(KafkaFixture fixture)
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(2);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Plans and starts a tail; it must start.</summary>
    internal static async Task<KafkaScanHandle> StartAsync(
        LiveScanEngine engine,
        string[] topics,
        string? predicate = null,
        KafkaIsolation isolation = KafkaIsolation.Committed,
        KafkaScanCursor? resume = null)
    {
        KafkaTailRequest request = new(
            EngineHarness.ClusterKey,
            [.. topics.Select(static x => new KafkaTopicName(x))],
            predicate,
            KafkaReadGrant.Envelope,
            isolation,
            resume);
        KafkaScanPlan plan = await engine.Scanner.PlanTailAsync(request, engine.Owner, Token);
        plan.Errors.Should().BeEmpty();

        KafkaScanStart started = engine.Scanner.StartTail(plan, engine.Owner);
        started.Refusal.Should().BeNull();
        return started.Handle!;
    }

    internal static void UntilCaughtUp(KafkaScanHandle handle) =>
        SpinWait.SpinUntil(() => handle.Progress.Partitions.Count > 0 && handle.Progress.Partitions.All(static x => x.CaughtUp), TimeSpan.FromSeconds(30))
            .Should().BeTrue("every partition of the tail reported its end");

    private static string[] Keys(IReadOnlyList<KafkaMatch> matches) => [.. matches.Select(static x => x.Key.Text ?? string.Empty)];

    [Fact]
    public async Task Records_produced_after_the_start_arrive_within_two_seconds_and_the_ones_already_there_are_not_read()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("tail-arrive");
        await admin.CreateTopicAsync(topic, 2);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 20).Select(static i => LiveRecords.At(i, "old-" + i, "v"))]);
        await admin.ProduceAsync(topic, 1, [.. Enumerable.Range(0, 20).Select(static i => LiveRecords.At(i, "old-" + i, "v"))]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        await using KafkaScanHandle handle = await StartAsync(engine, [topic]);
        UntilCaughtUp(handle);
        handle.Progress.Records.Should().Be(0, "a tail starts at the end of each partition");

        for (int i = 0; i < 3; i++)
        {
            string key = "new-" + i;
            Stopwatch watch = Stopwatch.StartNew();
            await admin.ProduceAsync(topic, i % 2, [LiveRecords.At(100 + i, key, "v")]);

            SpinWait.SpinUntil(() => Keys(handle.Matches).Contains(key), Arrival).Should().BeTrue($"{key} arrived within {Arrival.TotalSeconds} s of being produced (it took {watch.ElapsedMilliseconds} ms)");
        }

        handle.Stop();
        KafkaScanResult result = await handle.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Keys(handle.Matches).Should().Equal("new-0", "new-1", "new-2");
        result.StopReason.Should().Be(KafkaScanStopReason.Cancelled);
        result.Records.Should().Be(3);
        result.OutsideWindow.Should().Be(0);
    }

    [Fact]
    public async Task A_transaction_left_open_at_the_start_is_shown_when_it_commits_under_committed_and_one_that_aborts_is_never_shown()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("tail-transaction");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 5).Select(static i => LiveRecords.At(i, "committed-" + i, "v"))]);

        await using IContractTransaction open = await admin.BeginTransactionAsync(topic, 0, [.. Enumerable.Range(5, 3).Select(static i => LiveRecords.At(i, "open-" + i, "v"))]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        KafkaScanPlan plan = await engine.Scanner.PlanTailAsync(
            new KafkaTailRequest(EngineHarness.ClusterKey, [new KafkaTopicName(topic)], null, KafkaReadGrant.Envelope), engine.Owner, Token);

        PartitionRange range = plan.Ranges.Single();
        range.Start.Should().Be(5, "the last stable offset: the open transaction holds the records above it");
        range.HighWatermark.Should().BeGreaterThan(range.Start);

        await using KafkaScanHandle handle = engine.Scanner.StartTail(plan, engine.Owner).Handle!;
        UntilCaughtUp(handle);
        handle.Matches.Should().BeEmpty("nothing is stable yet");

        await open.CommitAsync();
        SpinWait.SpinUntil(() => handle.Matches.Count == 3, TimeSpan.FromSeconds(30)).Should().BeTrue("the transaction committed, so its records are stable");
        Keys(handle.Matches).Should().Equal("open-5", "open-6", "open-7");

        // An aborted transaction is never shown.
        await using (IContractTransaction aborted = await admin.BeginTransactionAsync(topic, 0, [.. Enumerable.Range(10, 2).Select(static i => LiveRecords.At(i, "aborted-" + i, "v"))]))
        {
            await aborted.AbortAsync();
        }

        await admin.ProduceAsync(topic, 0, [LiveRecords.At(20, "after", "v")]);
        SpinWait.SpinUntil(() => handle.Matches.Count == 4, TimeSpan.FromSeconds(30)).Should().BeTrue();
        handle.Stop();
        await handle.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Keys(handle.Matches).Should().Equal("open-5", "open-6", "open-7", "after");
    }

    [Fact]
    public async Task A_tail_that_includes_uncommitted_records_starts_at_the_high_watermark_and_shows_an_open_transactions_records_at_once()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("tail-uncommitted");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 3).Select(static i => LiveRecords.At(i, "old-" + i, "v"))]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        await using KafkaScanHandle handle = await StartAsync(engine, [topic], isolation: KafkaIsolation.IncludeUncommitted);
        UntilCaughtUp(handle);

        await using IContractTransaction open = await admin.BeginTransactionAsync(topic, 0, [.. Enumerable.Range(3, 2).Select(static i => LiveRecords.At(i, "open-" + i, "v"))]);
        SpinWait.SpinUntil(() => handle.Matches.Count == 2, TimeSpan.FromSeconds(30)).Should().BeTrue("uncommitted records are read as they are appended");
        Keys(handle.Matches).Should().Equal("open-3", "open-4");

        handle.Stop();
        await handle.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);
        await open.AbortAsync();
    }

    [Fact]
    public async Task A_pause_and_a_resume_read_every_offset_exactly_once_though_records_arrived_while_paused()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("tail-resume");
        await admin.CreateTopicAsync(topic, 2);
        await admin.ProduceAsync(topic, 0, [LiveRecords.At(0, "old-0", "v")]);
        await admin.ProduceAsync(topic, 1, [LiveRecords.At(0, "old-1", "v")]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        KafkaScanHandle first = await StartAsync(engine, [topic]);
        await using KafkaScanHandle firstGuard = first;
        UntilCaughtUp(first);

        for (int i = 0; i < 6; i++)
        {
            await admin.ProduceAsync(topic, i % 2, [LiveRecords.At(10 + i, "seg1-" + i, "v")]);
        }

        SpinWait.SpinUntil(() => first.Matches.Count == 6, TimeSpan.FromSeconds(30)).Should().BeTrue();
        first.Stop(KafkaScanStopReason.Paused);
        KafkaScanResult paused = await first.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);
        paused.StopReason.Should().Be(KafkaScanStopReason.Paused);

        // Produced while paused: nothing is skipped by the resume.
        for (int i = 0; i < 6; i++)
        {
            await admin.ProduceAsync(topic, i % 2, [LiveRecords.At(30 + i, "seg2-" + i, "v")]);
        }

        KafkaScanHandle second = await StartAsync(engine, [topic], resume: paused.Cursor);
        await using KafkaScanHandle secondGuard = second;
        SpinWait.SpinUntil(() => second.Matches.Count == 6, TimeSpan.FromSeconds(30)).Should().BeTrue("the six records produced while paused");
        second.Stop();
        await second.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);

        string[] all = [.. Keys(first.Matches), .. Keys(second.Matches)];
        all.Order(StringComparer.Ordinal).Should().Equal(
            [.. Enumerable.Range(0, 6).Select(static i => "seg1-" + i).Concat(Enumerable.Range(0, 6).Select(static i => "seg2-" + i)).Order(StringComparer.Ordinal)],
            "every record of both segments exactly once");
    }
}

/// <summary><see cref="TailLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class TailLiveTests39(Kafka39Fixture fixture) : TailLiveTests(fixture);

/// <summary><see cref="TailLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class TailLiveTests4(Kafka4Fixture fixture) : TailLiveTests(fixture);
