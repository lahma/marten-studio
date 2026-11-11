using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Tests.Support;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The Blazor contract of a scan (decision 9.7): batches at most four times a second on the <see cref="TimeProvider" />, a batch only when
/// something changed, a scan thread that never waits for whoever reads the batches, and a last batch that carries the result.
/// </summary>
public class ScanBatchingTests
{
    private static WireReadOutcome.Record Record(string topic, long offset) => new WireReadOutcome.Record(ScanFixture.Rec(topic, 0, offset, (int) offset + 1));

    private static Task<List<KafkaScanBatch>> Collect(KafkaScanHandle handle, List<KafkaScanBatch> into)
    {
        return Task.Run(
            async () =>
            {
                await foreach (KafkaScanBatch batch in handle.ReadBatchesAsync(CancellationToken.None))
                {
                    lock (into)
                    {
                        into.Add(batch);
                    }
                }

                return into;
            },
            CancellationToken.None);
    }

    private static int Count(List<KafkaScanBatch> batches)
    {
        lock (batches)
        {
            return batches.Count;
        }
    }

    // The collector adds under the lock, so whoever enumerates takes a copy under it: enumerating the list itself throws "Collection was modified"
    // whenever a batch lands in the middle (KS-CI-FIX-2).
    private static List<KafkaScanBatch> Snapshot(List<KafkaScanBatch> batches)
    {
        lock (batches)
        {
            return [.. batches];
        }
    }

    [Fact]
    public async Task A_batch_is_handed_over_at_most_once_per_quarter_second_however_often_the_scan_changes()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        await using ScanEngineTests.ScriptedScan guard = scan;
        List<KafkaScanBatch> batches = [];
        _ = Collect(scan.Handle, batches);

        // A record every 100 ms of the manual clock for one second: ten changes, four ticks.
        for (int step = 1; step <= 10; step++)
        {
            scan.Reader.Enqueue(Record(scan.Topic, step - 1));
            int expected = step;
            ScanFixture.Until(() => scan.Handle.Progress.Records == expected, "the scan reads the record");
            scan.Fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        ScanFixture.Until(() => Count(batches) >= 1, "at least one batch");
        List<KafkaScanBatch> seen = Snapshot(batches);
        seen.Should().HaveCountLessThanOrEqualTo(4, "four ticks passed in that second, and a tick hands over at most one batch");
        seen.SelectMany(static x => x.NewMatches).Select(static x => x.Offset).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task The_batch_timer_is_armed_before_the_scan_is_handed_back_so_a_clock_that_moves_at_once_cannot_pass_its_first_tick()
    {
        // KS-CI-FIX-2: the timer used to be created on a thread-pool task started by the constructor, so a clock that moved before the pool got to
        // it was already past the first tick (the timer's first due time is "now + 250 ms" at creation), and on a loaded runner the batch never came.
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("armed");
        fixture.Produce(topic, 0, 0, "k", "v");
        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));
        int before = fixture.Clock.TimersCreatedWithPeriod(KafkaScanHandle.BatchInterval);

        KafkaScanStart started = fixture.Start(plan, confirmed: true);
        await using KafkaScanHandle handle = started.Handle!;

        // Nothing is awaited between Start and this line: the timer is there because the handle's constructor made it, not because a task got to it.
        fixture.Clock.TimersCreatedWithPeriod(KafkaScanHandle.BatchInterval).Should().Be(before + 1);

        List<KafkaScanBatch> batches = [];
        _ = Collect(handle, batches);
        fixture.Clock.Advance(KafkaScanHandle.BatchInterval);
        ScanFixture.Until(() => Count(batches) >= 1, "the first tick, which the clock reached the moment the scan started, hands over a batch");
    }

    [Fact]
    public async Task Each_tick_hands_over_the_matches_added_since_the_last_batch_and_a_tick_with_no_change_hands_over_nothing()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        await using ScanEngineTests.ScriptedScan guard = scan;
        List<KafkaScanBatch> batches = [];
        _ = Collect(scan.Handle, batches);

        scan.Reader.Enqueue(Record(scan.Topic, 0));
        scan.Reader.Enqueue(Record(scan.Topic, 1));
        ScanFixture.Until(() => scan.Handle.Progress.Records == 2, "two records");
        scan.Fixture.Clock.Advance(TimeSpan.FromMilliseconds(250));
        ScanFixture.Until(() => Count(batches) == 1, "the first batch");

        batches[0].NewMatches.Select(static x => x.Offset).Should().Equal(0L, 1L);
        batches[0].Progress.Records.Should().Be(2);
        batches[0].Final.Should().BeNull();

        scan.Reader.Enqueue(Record(scan.Topic, 2));
        ScanFixture.Until(() => scan.Handle.Progress.Records == 3, "the third record");
        scan.Fixture.Clock.Advance(TimeSpan.FromMilliseconds(250));
        ScanFixture.Until(() => Count(batches) == 2, "the second batch");

        batches[1].NewMatches.Select(static x => x.Offset).Should().Equal(2L);
    }

    [Fact]
    public async Task With_the_batch_reader_blocked_the_scan_thread_still_advances()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        await using ScanEngineTests.ScriptedScan guard = scan;

        // Nobody reads the batches. The scan reads 60 records; the batcher can hold one batch and then waits for a reader that never comes.
        for (int i = 0; i < 30; i++)
        {
            scan.Reader.Enqueue(Record(scan.Topic, i));
        }

        ScanFixture.Until(() => scan.Handle.Progress.Records == 30, "the first thirty records");
        scan.Fixture.Clock.Advance(TimeSpan.FromMilliseconds(250));

        for (int i = 30; i < 60; i++)
        {
            scan.Reader.Enqueue(Record(scan.Topic, i));
        }

        ScanFixture.Until(() => scan.Handle.Progress.Records == 60, "the scan goes on although nobody reads the batches");
        scan.Fixture.Clock.Advance(TimeSpan.FromMilliseconds(250));
        scan.Fixture.Clock.Advance(TimeSpan.FromMilliseconds(250));

        for (int i = 60; i < 90; i++)
        {
            scan.Reader.Enqueue(Record(scan.Topic, i));
        }

        ScanFixture.Until(() => scan.Handle.Progress.Records == 90, "and on again, with the channel full");
        scan.Handle.Matches.Should().HaveCount(90);
    }

    [Fact]
    public async Task The_last_batch_carries_the_result_and_the_channel_then_completes()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        await using ScanEngineTests.ScriptedScan guard = scan;
        List<KafkaScanBatch> batches = [];
        Task<List<KafkaScanBatch>> consumer = Collect(scan.Handle, batches);

        scan.Reader.Enqueue(Record(scan.Topic, 0));
        scan.Reader.Enqueue(new WireReadOutcome.PartitionEnd(new WirePartition(scan.Topic, 0), 1));
        await scan.Handle.Result.Bounded();

        ScanFixture.Until(
            () =>
            {
                scan.Fixture.Clock.Advance(TimeSpan.FromMilliseconds(250));
                return consumer.IsCompleted;
            },
            "the batcher hands over the last batch and completes the channel");

        List<KafkaScanBatch> all = await consumer.Bounded();
        KafkaScanBatch last = all[^1];
        last.Final.Should().NotBeNull();
        last.Final!.StopReason.Should().Be(KafkaScanStopReason.Completed);
        last.Progress.Matches.Should().Be(1);
        all.SelectMany(static x => x.NewMatches).Should().ContainSingle();
        all.Take(all.Count - 1).Should().OnlyContain(static x => x.Final == null);
    }

    [Fact]
    public async Task The_batches_of_a_scan_can_be_read_once()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        await using ScanEngineTests.ScriptedScan guard = scan;

        // The first reader claims the batches when its first MoveNextAsync runs the iterator, which is synchronous up to its first await. It is
        // started here, not on a task: through Collect the claim happened whenever the thread pool got to it, and on a two-core runner the
        // "second" reader below could be the first, wait for a batch the manual clock never ticks, and hang the run (KS-CI-FIX-2).
        using CancellationTokenSource stop = new();
        await using IAsyncEnumerator<KafkaScanBatch> first = scan.Handle.ReadBatchesAsync(stop.Token).GetAsyncEnumerator(stop.Token);
        ValueTask<bool> pending = first.MoveNextAsync();

        Func<Task> second = async () =>
        {
            await foreach (KafkaScanBatch _ in scan.Handle.ReadBatchesAsync(CancellationToken.None))
            {
            }
        };

        await second.Should().ThrowAsync<InvalidOperationException>();

        // Let the first reader go: it ends with the token, not with a batch.
        await stop.CancelAsync();
        Func<Task> ended = async () => await pending;
        await ended.Should().ThrowAsync<OperationCanceledException>();
    }
}
